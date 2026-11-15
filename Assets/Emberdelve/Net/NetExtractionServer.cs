using System;
using System.Collections.Generic;
using Emberdelve.Combat;
using Emberdelve.Core;
using Emberdelve.Flow;
using Emberdelve.Interaction;
using Emberdelve.Net.Rules;
using Emberdelve.Player;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace Emberdelve.Net
{
    /// <summary>Server-owned extraction leases. Client timing is only a request; this class decides every commit.</summary>
    internal sealed class NetExtractionServer
    {
        private sealed class PendingHold
        {
            public ExtractionPoint Point;
            public ExtractionHoldWindow Window;
            public readonly ExtractionHoldCommitLatch CommitLatch = new ExtractionHoldCommitLatch();
            public bool CompletionRequested;
            public double LastHeartbeatAt;
            public HealthComponent Health;
            public Action<DamageInfo> DamageHandler;
        }

        internal static NetExtractionServer Current { get; private set; }
        private const double HeartbeatSilenceSeconds = 1.25;
        private const double HoldStartIntervalSeconds = 0.15;
        private readonly NetworkManager _network;
        private readonly NetIdentityTable _identities;
        private readonly NetSkillServer _skills;
        private readonly Action<string> _log;
        private readonly Dictionary<ulong, uint> _lastSequence = new Dictionary<ulong, uint>();
        private readonly Dictionary<ulong, double> _lastStartAt = new Dictionary<ulong, double>();
        private readonly Dictionary<ulong, PendingHold> _holds = new Dictionary<ulong, PendingHold>();
        private readonly List<ulong> _scratch = new List<ulong>();

        public NetExtractionServer(NetworkManager network, NetIdentityTable identities, NetSkillServer skills, Action<string> log)
        {
            _network = network;
            _identities = identities;
            _skills = skills;
            _log = log;
            Current = this;
        }

        public void OnClosed(ulong clientId)
        {
            RemoveHold(clientId);
            _lastSequence.Remove(clientId);
            _lastStartAt.Remove(clientId);
        }

        public void Dispose()
        {
            foreach (ulong sender in new List<ulong>(_holds.Keys)) RemoveHold(sender);
            _lastSequence.Clear();
            _lastStartAt.Clear();
            _scratch.Clear();
            if (ReferenceEquals(Current, this)) Current = null;
        }

        public bool BeginHold(ulong sender, NetworkObject target, uint sequence)
        {
            if (_skills != null && _skills.DropIfDisconnecting(sender)) return false;
            if (!TryResolve(sender, target, out NetIdentity row))
            {
                _log?.Invoke("extract-hold reject=Identity sender=" + sender + " op=begin seq=" + sequence);
                RejectKnownOwner(sender, sequence, "Extraction request is not yours");
                return false;
            }
            if (!TryConsumeSequence(sender, sequence))
            {
                Charge(sender, SkillRejectKind.Replay, "sequence=" + sequence);
                Reject(row, sequence, "Stale extraction request");
                return false;
            }
            if (_holds.ContainsKey(sender))
            {
                Reject(row, sequence, "Another extraction hold is already active");
                return false;
            }
            if (!TryGetPoint(row.Rig, out ExtractionPoint point))
            {
                Reject(row, sequence, "Extraction point is unavailable");
                return false;
            }
            double now = Time.realtimeSinceStartupAsDouble;
            if (_lastStartAt.TryGetValue(sender, out double lastStart) && now - lastStart < HoldStartIntervalSeconds)
            {
                Charge(sender, SkillRejectKind.Flood, "hold start rate");
                Reject(row, sequence, "Please wait before trying extraction again");
                return false;
            }
            _lastStartAt[sender] = now;
            if (!point.CanInteract(row.Rig, out string reason))
            {
                Reject(row, sequence, reason);
                return false;
            }

            Vector3 position = row.Rig.transform.position;
            if (!ExtractionHoldWindow.TryStart(sequence, now, point.HoldDuration, position.x, position.y, position.z,
                    out ExtractionHoldWindow window))
            {
                Reject(row, sequence, "Extraction hold duration is invalid");
                return false;
            }

            var pending = new PendingHold
            {
                Point = point,
                Window = window,
                LastHeartbeatAt = now,
                Health = row.Rig.Health
            };
            if (pending.Health != null)
            {
                pending.DamageHandler = _ => Interrupt(sender, sequence, "Damage interrupted extraction");
                pending.Health.Damaged += pending.DamageHandler;
            }
            _holds.Add(sender, pending);
            _log?.Invoke("extract-hold begin sender=" + sender + " player=" + row.PlayerId +
                         " seq=" + sequence + " duration=" + window.Duration);
            return true;
        }

        public void Heartbeat(ulong sender, NetworkObject target, uint sequence)
        {
            if (!TryResolve(sender, target, out _))
            {
                _log?.Invoke("extract-hold reject=Identity sender=" + sender + " op=heartbeat seq=" + sequence);
                return;
            }
            if (!_holds.TryGetValue(sender, out PendingHold pending) || pending.Window.Sequence != sequence) return;
            pending.LastHeartbeatAt = Time.realtimeSinceStartupAsDouble;
        }

        public void CancelHold(ulong sender, NetworkObject target, uint sequence)
        {
            if (!TryResolve(sender, target, out _))
            {
                _log?.Invoke("extract-hold reject=Identity sender=" + sender + " op=cancel seq=" + sequence);
                return;
            }
            if (!_holds.TryGetValue(sender, out PendingHold pending) || pending.Window.Sequence != sequence) return;
            RemoveHold(sender);
            _log?.Invoke("extract-hold cancel sender=" + sender + " seq=" + sequence);
        }

        public void CompleteHold(ulong sender, NetworkObject target, uint sequence)
        {
            if (_skills != null && _skills.DropIfDisconnecting(sender)) return;
            if (!TryResolve(sender, target, out NetIdentity row))
            {
                _log?.Invoke("extract-hold reject=Identity sender=" + sender + " op=complete seq=" + sequence);
                return;
            }
            if (!_holds.TryGetValue(sender, out PendingHold pending) || pending.Window.Sequence != sequence) return;

            ExtractionHoldVerdict verdict = Evaluate(pending, sequence, row.Rig, Time.realtimeSinceStartupAsDouble);
            if (verdict == ExtractionHoldVerdict.TooEarly)
            {
                pending.CompletionRequested = true;
                return;
            }
            if (verdict != ExtractionHoldVerdict.Valid)
            {
                Interrupt(sender, sequence, ReasonFor(verdict));
                return;
            }
            if (!pending.Point.CanInteract(row.Rig, out string reason))
            {
                Interrupt(sender, sequence, reason);
                return;
            }
            Commit(sender, row, pending);
        }

        public void Tick(float ignoredNow)
        {
            if (_holds.Count == 0) return;
            double now = Time.realtimeSinceStartupAsDouble;
            _scratch.Clear();
            foreach (ulong sender in _holds.Keys) _scratch.Add(sender);
            for (int i = 0; i < _scratch.Count; i++)
            {
                ulong sender = _scratch[i];
                if (!_holds.TryGetValue(sender, out PendingHold pending)) continue;
                if (pending == null || pending.Point == null || _identities == null ||
                    !_identities.TryGetByClient(sender, out NetIdentity row) || row == null || row.Rig == null)
                {
                    RemoveHold(sender);
                    continue;
                }

                ExtractionHoldVerdict verdict = Evaluate(pending, pending.Window.Sequence, row.Rig, now);
                string reason = verdict == ExtractionHoldVerdict.TooEarly || verdict == ExtractionHoldVerdict.Valid
                    ? null : ReasonFor(verdict);
                if (reason == null && !pending.Point.CanInteract(row.Rig, out reason)) { }
                if (reason == null && !ExtractionHoldWindow.IsHeartbeatFresh(pending.LastHeartbeatAt, now, HeartbeatSilenceSeconds))
                    reason = "Extraction hold heartbeat expired";

                if (reason == null && pending.CompletionRequested && verdict == ExtractionHoldVerdict.Valid)
                {
                    Commit(sender, row, pending);
                    continue;
                }
                if (reason != null) Interrupt(sender, pending.Window.Sequence, reason);
            }
            _scratch.Clear();
        }

        private void Commit(ulong sender, NetIdentity row, PendingHold pending)
        {
            // Remove the lease before invoking gameplay code: duplicate complete RPCs become inert.
            if (!pending.CommitLatch.TrySeal()) return;
            RemoveHold(sender);
            string reason = null;
            bool accepted = GameServices.TryGet(out RunDirector director) && director != null &&
                director.BeginExtraction(row.Rig, out reason);
            if (!accepted && string.IsNullOrEmpty(reason)) reason = "Extraction could not be completed";
            if (accepted) NetMovementServer.Current?.Unregister(row.PlayerId, "extracted");
            SendResult(row, pending.Window.Sequence, accepted, accepted ? "" : reason);
            _log?.Invoke("extract-hold complete sender=" + sender + " player=" + row.PlayerId +
                         " seq=" + pending.Window.Sequence + " accepted=" + accepted +
                         (accepted ? "" : " reason=" + reason));
        }

        private ExtractionHoldVerdict Evaluate(PendingHold pending, uint sequence, PlayerRig rig, double now)
        {
            if (rig == null) return ExtractionHoldVerdict.Expired;
            Vector3 position = rig.transform.position;
            return pending.Window.Evaluate(sequence, now, position.x, position.y, position.z);
        }

        private void Interrupt(ulong sender, uint sequence, string reason)
        {
            if (!_holds.TryGetValue(sender, out PendingHold pending) || pending.Window.Sequence != sequence) return;
            RemoveHold(sender);
            int playerId = 0;
            if (_identities != null && _identities.TryGetByClient(sender, out NetIdentity row))
            {
                playerId = row.PlayerId;
                SendResult(row, sequence, false, reason);
            }
            _log?.Invoke("extract-hold interrupt sender=" + sender + " player=" + playerId + " seq=" + sequence + " reason=" + reason);
        }

        private void RemoveHold(ulong sender)
        {
            if (!_holds.TryGetValue(sender, out PendingHold pending)) return;
            if (pending?.Health != null && pending.DamageHandler != null) pending.Health.Damaged -= pending.DamageHandler;
            _holds.Remove(sender);
        }

        private bool TryResolve(ulong sender, NetworkObject target, out NetIdentity row)
        {
            row = null;
            return _network != null && _network.IsServer && _identities != null &&
                _identities.TryGetByClient(sender, out row) && row != null && row.ClientId == sender &&
                target != null && target.IsSpawned && target.OwnerClientId == sender && ReferenceEquals(target, row.Object) &&
                row.Rig != null && row.PlayerId == row.Rig.PlayerId &&
                ReferenceEquals(target.GetComponent<PlayerRig>(), row.Rig);
        }

        private bool TryConsumeSequence(ulong sender, uint sequence)
        {
            if (sequence == 0 || sequence == uint.MaxValue) return false;
            uint expected = _lastSequence.TryGetValue(sender, out uint previous) ? previous + 1 : 1;
            if (sequence != expected) return false;
            _lastSequence[sender] = sequence;
            return true;
        }

        private static bool TryGetPoint(PlayerRig actor, out ExtractionPoint point)
        {
            point = GameServices.TryGet(out RunDirector director) && director != null
                ? ExtractionPoint.FindForFloor(director.ExtractionFloorFor(actor)) : null;
            return point != null;
        }

        private void RejectKnownOwner(ulong sender, uint sequence, string reason)
        {
            if (_identities != null && _identities.TryGetByClient(sender, out NetIdentity row)) Reject(row, sequence, reason);
        }

        private static void Reject(NetIdentity row, uint sequence, string reason) => SendResult(row, sequence, false, reason);

        private void Charge(ulong sender, SkillRejectKind kind, string detail) =>
            _skills?.ChargeReject(sender, kind, "extraction", detail, false);

        private static void SendResult(NetIdentity row, uint sequence, bool accepted, string reason)
        {
            if (row?.Object == null || !row.Object.IsSpawned) return;
            NetPlayerRig view = row.Object.GetComponent<NetPlayerRig>();
            if (view == null) return;
            string boundedReason = reason ?? "";
            if (boundedReason.Length > 100) boundedReason = boundedReason.Substring(0, 100);
            if (row.ClientId == NetworkManager.ServerClientId)
                view.GetComponent<NetExtractionClient>()?.OnResult(sequence, accepted, boundedReason);
            else
                view.ExtractionHoldResultRpc(sequence, accepted, new FixedString128Bytes(boundedReason));
        }

        private static string ReasonFor(ExtractionHoldVerdict verdict) => verdict switch
        {
            ExtractionHoldVerdict.TooEarly => "Extraction hold is not complete",
            ExtractionHoldVerdict.Expired => "Extraction hold expired",
            ExtractionHoldVerdict.WrongSequence => "Stale extraction hold",
            ExtractionHoldVerdict.Moved => "Movement interrupted extraction",
            _ => "Extraction hold was interrupted"
        };
    }
}
