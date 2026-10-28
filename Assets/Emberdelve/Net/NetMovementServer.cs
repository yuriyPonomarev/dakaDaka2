using System;
using System.Collections.Generic;
using System.Globalization;
using Emberdelve.Movement;
using Emberdelve.Net.Rules;
using Emberdelve.Player;
using Unity.Netcode;
using UnityEngine;

namespace Emberdelve.Net
{
    /// <summary>
    /// U3A4a, server only (owned by NetServer): the authoritative movement simulation.
    ///
    ///  - Receive: the entry point of both movement RPCs. The sender is resolved ONLY from the connection that carried the message
    ///    (RpcParams.Receive.SenderClientId, passed in by the RPC) through the NetIdentityTable; a frame for an object that
    ///    connection does not own is a forged-owner movement attempt: rejected and counted, nothing else happens. Frames from an
    ///    unknown connection (before the spawn finished, after the despawn) are ignored and counted globally.
    ///  - Every frame goes through the player's InputSanitizer and then into the player's ServerMoveInputBuffer.
    ///  - On every NGO network tick (NetworkManager.NetworkTickSystem.Tick, not Update) each registered player gets exactly ONE
    ///    step: one frame from the buffer (or the repeat / neutral rule), the aim pose applied (yaw to the body, pitch to the look
    ///    pivot, clamped), PlayerMovement.Step with dt = 1 / TickRate. Position, collision and speed are therefore the server's.
    ///
    /// Logging (all lines start with the server tag from NetBootstrap):
    ///   move id=N pos=(x,y,z) acc=.. rep=.. flood=.. inv=.. tick=.. yaw=.. clamp=.. qdrop=.. forge=.. vmax=.. ymax=.. air=.. depth=..
    ///        written when the player moved or a counter changed (at most every 15 ticks) and at least every 120 ticks; a last line
    ///        with final=1 plus the per-reason counters is written when the player is removed;
    ///   REJECT kind count=n id=N     (kinds: replay flood nan big-move seq-jump seq-zero flags bad-batch forge-move; rate limited:
    ///        counts 1..3, powers of two, every 100th);
    ///   seg id=N ... one line per finished stretch of horizontal movement (distance, mean / peak speed, from / to);
    ///   air id=N ... one line per jump / fall of at least 4 airborne ticks.
    /// </summary>
    internal sealed class NetMovementServer
    {
        private const float SegStartSpeed = 0.3f;
        private const float SegStopSpeed = 0.1f;
        private const int SegStopTicks = 6;
        private const int AirMinTicks = 4;
        private const int LogEveryTicks = 15;
        private const int LogIdleTicks = 120;
        private const float LogMoveEpsilon = 0.002f;

        private sealed class MoveState
        {
            public int PlayerId;
            public ulong ClientId;
            public string Slot;
            public PlayerRig Rig;
            public NetworkObject Object;
            public readonly InputSanitizer Sanitizer = new InputSanitizer();
            public readonly ServerMoveInputBuffer Buffer = new ServerMoveInputBuffer();

            public int Forge;
            public int BadBatch;

            public Vector3 LastPos;
            public float VMax;
            public float VMaxSinceLog;
            public float YMaxSinceLog = float.NegativeInfinity;
            public float RestY;
            public int AirTicksTotal;
            public int AirTicks;
            public float AirYMax;
            public float AirRest;

            public bool Moving;
            public long SegStartTick;
            public Vector3 SegFrom;
            public Vector3 SegTo;
            public float SegDist;
            public float SegPeak;
            public int SegTicks;
            public int StillTicks;

            public long NextLogTick;
            public long LastLogTick = -1;
            public Vector3 LoggedPos;
            public long LoggedSignature = -1;
        }

        public static NetMovementServer Current { get; private set; }

        private readonly NetworkManager _nm;
        private readonly NetIdentityTable _table;
        private readonly Action<string> _log;
        private readonly Action<string> _logError;
        private readonly Dictionary<int, MoveState> _states = new Dictionary<int, MoveState>();
        private readonly List<MoveState> _order = new List<MoveState>();

        private bool _subscribed;
        private long _tick;
        private int _noPlayer;

        public NetMovementServer(NetworkManager nm, NetIdentityTable table, Action<string> log, Action<string> logError)
        {
            _nm = nm;
            _table = table;
            _log = log;
            _logError = logError;
            Current = this;
        }

        public int Registered => _states.Count;
        public long TickCount => _tick;

        /// <summary>Called every frame by NetServer: attaches to the network tick once the transport is listening.</summary>
        public void EnsureSubscribed()
        {
            if (_subscribed || _nm == null || !_nm.IsListening) return;
            NetworkTickSystem ticks = _nm.NetworkTickSystem;
            if (ticks == null) return;

            ticks.Tick += OnNetworkTick;
            _subscribed = true;
            _log("movement simulation attached tickRate=" + NetTuning.TickRate + " dt=" + NetTuning.TickSeconds.ToString("F4", CultureInfo.InvariantCulture));
        }

        public void Dispose()
        {
            if (_subscribed && _nm != null)
            {
                NetworkTickSystem ticks = _nm.NetworkTickSystem;
                if (ticks != null) ticks.Tick -= OnNetworkTick;
            }

            _subscribed = false;
            _states.Clear();
            _order.Clear();
            if (Current == this) Current = null;
        }

        // ---- registration ----------------------------------------------------------------------

        /// <summary>A player was admitted and spawned. The host's own presenting player is not simulated here (it moves with its own Update).</summary>
        public void Register(NetIdentity row)
        {
            if (row == null || row.Rig == null || row.Object == null) return;
            if (row.ClientId == NetworkManager.ServerClientId) return;
            PlayerMovement movement = row.Rig.Movement;
            if (movement == null) { _logError("movement: player " + row.PlayerId + " has no PlayerMovement"); return; }
            if (_states.ContainsKey(row.PlayerId)) return;

            MoveState state = new MoveState
            {
                PlayerId = row.PlayerId,
                ClientId = row.ClientId,
                Slot = row.Slot,
                Rig = row.Rig,
                Object = row.Object,
                LastPos = row.Rig.transform.position,
                RestY = row.Rig.transform.position.y
            };
            state.LoggedPos = state.LastPos;
            state.NextLogTick = _tick;
            _states[row.PlayerId] = state;
            _order.Add(state);

            _log("movement registered id=" + row.PlayerId + " client=" + row.ClientId +
                 " walk=" + F(movement.ResolveGroundSpeed(false, false)) +
                 " sprint=" + F(movement.ResolveGroundSpeed(true, false)) +
                 " crouch=" + F(movement.ResolveGroundSpeed(false, true)) +
                 " jump=" + F(row.Rig.Class != null ? row.Rig.Class.jumpHeight : 0f) +
                 " pos=" + P(state.LastPos) + " tick=" + _tick);
        }

        public void Unregister(int playerId, string reason)
        {
            if (!_states.TryGetValue(playerId, out MoveState state)) return;

            _states.Remove(playerId);
            _order.Remove(state);
            if (state.Moving) EndSegment(state, true);
            WriteMoveLine(state, true, reason);
        }

        // ---- receive ---------------------------------------------------------------------------

        /// <summary>
        /// A movement batch arrived. <paramref name="target"/> is the player object the RPC was invoked on; <paramref name="senderClientId"/>
        /// is RpcParams.Receive.SenderClientId, the connection that actually delivered it.
        /// </summary>
        public void Receive(NetPlayerRig target, ulong senderClientId, in MoveBatchWire batch)
        {
            if (!_table.TryGetByClient(senderClientId, out NetIdentity sender) || !_states.TryGetValue(sender.PlayerId, out MoveState state))
            {
                _noPlayer++;
                if (IsLoggable(_noPlayer)) _log("REJECT no-player count=" + _noPlayer + " sender=" + senderClientId);
                return;
            }

            NetworkObject targetObject = target != null ? target.NetworkObject : null;
            if (targetObject == null || !ReferenceEquals(targetObject, state.Object))
            {
                state.Forge++;
                if (IsLoggable(state.Forge))
                {
                    int targetId = target != null ? target.ServerPlayerId.Value : 0;
                    _log("REJECT forge-move count=" + state.Forge + " id=" + state.PlayerId + " target=" + targetId +
                         " targetOwner=" + (targetObject != null ? targetObject.OwnerClientId : 0UL) + " sender=" + senderClientId);
                }

                return;
            }

            int count = batch.Count;
            if (count < 1 || count > MoveBatchWire.MaxFrames)
            {
                state.BadBatch++;
                if (IsLoggable(state.BadBatch)) _log("REJECT bad-batch count=" + state.BadBatch + " id=" + state.PlayerId);
                return;
            }

            double now = _tick / (double)NetTuning.TickRate;
            for (int i = 0; i < count; i++)
            {
                MoveInputFrame frame = batch.Get(i).ToFrame();
                bool primary = i == count - 1;
                SanitizeVerdict verdict = state.Sanitizer.Submit(in frame, primary, now, out MoveInputFrame clean);
                switch (verdict)
                {
                    case SanitizeVerdict.Accepted:
                        state.Buffer.Push(in clean);
                        break;
                    case SanitizeVerdict.DroppedReplay:
                        LogReject("replay", state.Sanitizer.DroppedReplay, state);
                        break;
                    case SanitizeVerdict.DroppedFlood:
                        LogReject("flood", state.Sanitizer.DroppedFlood, state);
                        break;
                    case SanitizeVerdict.DroppedInvalid:
                        LogInvalid(state);
                        break;
                }
            }
        }

        private void LogInvalid(MoveState state)
        {
            InputSanitizer s = state.Sanitizer;
            switch (s.LastInvalidReason)
            {
                case InvalidReason.NonFinite: LogReject("nan", s.InvalidNonFinite, state); break;
                case InvalidReason.MoveMagnitude: LogReject("big-move", s.InvalidMagnitude, state); break;
                case InvalidReason.SeqWindow: LogReject("seq-jump", s.InvalidSeqWindow, state); break;
                case InvalidReason.SeqZero: LogReject("seq-zero", s.InvalidSeqZero, state); break;
                case InvalidReason.Flags: LogReject("flags", s.InvalidFlags, state); break;
            }
        }

        private void LogReject(string kind, int count, MoveState state)
        {
            if (IsLoggable(count)) _log("REJECT " + kind + " count=" + count + " id=" + state.PlayerId);
        }

        private static bool IsLoggable(int count)
        {
            return count <= 3 || (count & (count - 1)) == 0 || count % 100 == 0;
        }

        // ---- the tick --------------------------------------------------------------------------

        private void OnNetworkTick()
        {
            _tick++;
            const float dt = NetTuning.TickSeconds;

            for (int i = _order.Count - 1; i >= 0; i--)
            {
                MoveState state = _order[i];
                PlayerRig rig = state.Rig;
                if (rig == null || state.Object == null || !state.Object.IsSpawned)
                {
                    // Despawned or destroyed under us: stop simulating it (NetServer.RemoveActor normally got there first).
                    _order.RemoveAt(i);
                    _states.Remove(state.PlayerId);
                    continue;
                }

                StepPlayer(state, rig, dt);
            }

            // Oldest registration first for the log lines (the loop above runs backwards so removal is safe).
            for (int i = 0; i < _order.Count; i++) MaybeLog(_order[i]);
        }

        private void StepPlayer(MoveState state, PlayerRig rig, float dt)
        {
            PlayerMovement movement = rig.Movement;
            PlayerLook look = rig.Look;
            Transform body = rig.transform;
            if (movement == null) return;

            float fallbackYaw = MoveInputFrame.WrapDegrees(body.eulerAngles.y);
            float fallbackPitch = look != null ? look.Pitch : 0f;
            state.Buffer.Next(fallbackYaw, fallbackPitch, out MoveInputFrame frame);

            if (look != null) look.ApplyAimPose(frame.Yaw, frame.Pitch);
            movement.Step(in frame, dt);

            Vector3 pos = body.position;
            float dx = pos.x - state.LastPos.x;
            float dz = pos.z - state.LastPos.z;
            float distance = Mathf.Sqrt(dx * dx + dz * dz);
            float speed = distance / dt;
            float dy = pos.y - state.LastPos.y;

            if (speed > state.VMax) state.VMax = speed;
            if (speed > state.VMaxSinceLog) state.VMaxSinceLog = speed;
            if (pos.y > state.YMaxSinceLog) state.YMaxSinceLog = pos.y;

            TrackSegment(state, pos, distance, speed);
            TrackAir(state, movement, pos, dy);

            state.LastPos = pos;
        }

        private void TrackSegment(MoveState state, Vector3 pos, float distance, float speed)
        {
            if (!state.Moving)
            {
                if (speed <= SegStartSpeed) return;

                state.Moving = true;
                state.SegStartTick = _tick;
                state.SegFrom = state.LastPos;
                state.SegTo = pos;
                state.SegDist = distance;
                state.SegPeak = speed;
                state.SegTicks = 1;
                state.StillTicks = 0;
                return;
            }

            state.SegDist += distance;
            state.SegTicks++;
            if (speed > state.SegPeak) state.SegPeak = speed;

            if (speed < SegStopSpeed)
            {
                state.StillTicks++;
                if (state.StillTicks >= SegStopTicks) EndSegment(state, false);
            }
            else
            {
                state.StillTicks = 0;
                state.SegTo = pos;
            }
        }

        private void EndSegment(MoveState state, bool open)
        {
            int movingTicks = Mathf.Max(1, state.SegTicks - state.StillTicks);
            float dx = state.SegTo.x - state.SegFrom.x;
            float dz = state.SegTo.z - state.SegFrom.z;
            float displacement = Mathf.Sqrt(dx * dx + dz * dz);
            float mean = state.SegDist / (movingTicks * NetTuning.TickSeconds);

            _log("seg id=" + state.PlayerId + " kind=move startTick=" + state.SegStartTick + " endTick=" + _tick +
                 " ticks=" + movingTicks + " secs=" + F(movingTicks * NetTuning.TickSeconds) +
                 " dist=" + F(state.SegDist) + " disp=" + F(displacement) + " mean=" + F(mean) + " peak=" + F(state.SegPeak) +
                 " from=" + P(state.SegFrom) + " to=" + P(state.SegTo) + (open ? " open=1" : ""));
            state.Moving = false;
            state.StillTicks = 0;
        }

        private void TrackAir(MoveState state, PlayerMovement movement, Vector3 pos, float dy)
        {
            if (!movement.IsGrounded)
            {
                if (state.AirTicks == 0)
                {
                    state.AirYMax = pos.y;
                    state.AirRest = state.RestY;
                }
                else if (pos.y > state.AirYMax)
                {
                    state.AirYMax = pos.y;
                }

                state.AirTicks++;
                state.AirTicksTotal++;
                return;
            }

            if (state.AirTicks >= AirMinTicks)
            {
                _log("air id=" + state.PlayerId + " ticks=" + state.AirTicks + " rest=" + F(state.AirRest) + " ymax=" + F(state.AirYMax) +
                     " rise=" + F(state.AirYMax - state.AirRest) + " tick=" + _tick);
            }

            state.AirTicks = 0;
            if (Mathf.Abs(dy) < 0.005f) state.RestY = pos.y;
        }

        // ---- logging ---------------------------------------------------------------------------

        private static long Signature(MoveState s)
        {
            InputSanitizer z = s.Sanitizer;
            return z.Accepted + 1000L * (z.DroppedReplay + 1000L * (z.DroppedFlood + 1000L * (z.DroppedInvalid + 1000L * (s.Forge + 1000L * s.Buffer.Overflowed))));
        }

        private void MaybeLog(MoveState s)
        {
            if (_tick < s.NextLogTick) return;
            s.NextLogTick = _tick + LogEveryTicks;

            Vector3 delta = s.LastPos - s.LoggedPos;
            bool moved = delta.sqrMagnitude > LogMoveEpsilon * LogMoveEpsilon;
            bool counters = Signature(s) != s.LoggedSignature;
            bool first = s.LastLogTick < 0;
            bool idle = _tick - s.LastLogTick >= LogIdleTicks;
            if (!(moved || counters || first || idle)) return;

            WriteMoveLine(s, false, null);
        }

        private void WriteMoveLine(MoveState s, bool final, string reason)
        {
            InputSanitizer z = s.Sanitizer;
            string line = "move id=" + s.PlayerId + " pos=" + P(s.LastPos) +
                          " acc=" + z.Accepted + " rep=" + z.DroppedReplay + " flood=" + z.DroppedFlood + " inv=" + z.DroppedInvalid +
                          " tick=" + _tick + " yaw=" + F(s.Rig != null ? MoveInputFrame.WrapDegrees(s.Rig.transform.eulerAngles.y) : 0f) +
                          " clamp=" + z.Clamped + " qdrop=" + s.Buffer.Overflowed + " forge=" + s.Forge +
                          " vmax=" + F(s.VMaxSinceLog) + " ymax=" + F(float.IsNegativeInfinity(s.YMaxSinceLog) ? s.LastPos.y : s.YMaxSinceLog) +
                          " air=" + s.AirTicksTotal + " depth=" + s.Buffer.Depth;
            if (final)
            {
                line += " final=1 reason=" + (reason ?? "-") + " vmaxAll=" + F(s.VMax) +
                        " nan=" + z.InvalidNonFinite + " big=" + z.InvalidMagnitude + " seqj=" + z.InvalidSeqWindow +
                        " seq0=" + z.InvalidSeqZero + " flags=" + z.InvalidFlags + " badbatch=" + s.BadBatch +
                        " redundant=" + z.Redundant + " repeated=" + s.Buffer.RepeatedTicks + " neutral=" + s.Buffer.NeutralTicks +
                        " noplayer=" + _noPlayer;
            }

            _log(line);

            s.LastLogTick = _tick;
            s.LoggedPos = s.LastPos;
            s.LoggedSignature = Signature(s);
            s.VMaxSinceLog = 0f;
            s.YMaxSinceLog = float.NegativeInfinity;
        }

        private static string F(float value) { return value.ToString("F3", CultureInfo.InvariantCulture); }

        private static string P(Vector3 v) { return "(" + F(v.x) + "," + F(v.y) + "," + F(v.z) + ")"; }
    }
}
