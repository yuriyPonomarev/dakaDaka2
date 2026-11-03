using System;
using System.Collections.Generic;
using System.Globalization;
using Emberdelve.Combat;
using Emberdelve.Data;
using Emberdelve.Net.Rules;
using Emberdelve.Player;
using Emberdelve.Skills;
using Unity.Netcode;
using UnityEngine;

namespace Emberdelve.Net
{
    /// <summary>
    /// U3B2, server only (owned by NetServer): the first production server-owned combat channel, active skills (slots 0 and 1).
    ///
    /// Receive is the entry point of NetPlayerRig.SkillIntentRpc. Everything about a request is decided here, in this order:
    ///   1. IDENTITY FIRST. The sender is the connection that carried the message (RpcParams.Receive.SenderClientId, never a
    ///      payload field). The call must be made on that connection's own admitted player object. Nothing about the object the
    ///      call was MADE on is read when this fails (no mana, health, id or kit: only its NetworkObject identity is compared), and
    ///      the request is charged to the SENDER's reject ledger, never to the victim.
    ///   2. THE CONNECTION'S GATE (SkillIntentGate: sequence, slot, rate) on the server's monotonic clock. Its verdicts are rejects.
    ///   3. THE SERVER'S OWN SkillCaster.TryCastSlot on the sender's server rig: cooldown, mana, weapon and authority rules decide
    ///      the outcome and spend the resources. The aim a cast resolves with is the rig's current server pose, i.e. the last
    ///      SANITIZED movement frame NetMovementServer stepped (yaw to the body, pitch to the look pivot). Movement (unreliable) and
    ///      skills (reliable) are different channels, so a cast uses the aim of the newest input frame the server had processed when
    ///      the request was dequeued, which can be older than the client's key press by up to the input's network delay (log field
    ///      inputAgeTicks says how old). Client aim is never sent with a skill request.
    ///   4. FEEDBACK. The owner gets one reliable SkillResultRpc per request that passed the gate (cast / refused + reason code and
    ///      the server's mana and cooldowns); a cast also sends the other clients a SkillVisualRpc (skill id; the caster is the
    ///      object). Gate rejects get no answer (replying to a flood would amplify it). The owner's mana and cooldowns are also
    ///      published as an owner-read NetworkVariable a few times a second (regeneration and cooldown expiry).
    ///
    /// ABUSE CONTROL. EVERY reject (identity, invalid, replay, flood) is charged to the sending connection's SkillRejectLedger, a
    /// cheap token bucket consulted before anything else happens (see SkillAbusePolicy): rejects inside the allowance are only
    /// counted, rejects beyond it are "excess", and the excess reaching the threshold disconnects the client ("skill abuse"). Further
    /// traffic of a connection marked for disconnect is dropped unseen. Reject logs are rate limited per kind (NetLogThrottle).
    /// A slot that was disconnected is remembered (SkillAbuseMemory, keyed by the dev slot, survives the connection AND the actor)
    /// for the policy's memory window; when it returns inside that window the new connection is on PROBATION: its reject allowance
    /// starts EMPTY, its skill gate starts with no tokens (they refill from admission) and its disconnect threshold is halved once per
    /// remembered strike. The slot is a dev identity from the connect payload, not authentication: changing it dodges the memory
    /// (accounts, a later milestone, replace it). The host's own connection is never disconnected.
    ///
    /// Lines (server tag from NetBootstrap):
    ///   skill-server ready ...                 policy at startup
    ///   skill-abuse probation client=.. ...    a remembered slot was admitted
    ///   skill reject=KIND client=.. ...        rate-limited
    ///   skill-abuse disconnect client=.. ...   final counters of the connection and the strike now on record
    ///   skill-cast client=.. id=.. slot=.. seq=.. cast=.. reason=.. manaBefore=.. manaAfter=.. maxMana=.. cd0=.. cd1=.. ...
    ///   skill-conn final client=.. ...         when a connection ends
    /// </summary>
    internal sealed class NetSkillServer
    {
        private const float PublishSeconds = 0.2f;

        private sealed class Conn
        {
            public ulong ClientId;
            public string Slot = "";
            public SkillIntentGate Gate;
            public SkillRejectLedger Ledger;
            public bool Disconnecting;
            public int Cast;
            public int Refused;
            public int IgnoredAfterDisconnect;
            public NetPlayerRig NetRig;
            public bool Published;
            public SkillStateData LastPublished;
        }

        public static NetSkillServer Current { get; private set; }

        private readonly NetworkManager _nm;
        private readonly NetIdentityTable _table;
        private readonly SkillAbusePolicy _policy;
        private readonly SkillAbuseMemory _memory;
        private readonly Func<ulong, string> _resolveSlot;
        private readonly Action<string> _log;
        private readonly Action<string> _logError;
        private readonly Dictionary<ulong, Conn> _conns = new Dictionary<ulong, Conn>();
        private readonly List<ulong> _scratch = new List<ulong>();
        private float _nextPublish;

        public NetSkillServer(NetworkManager nm, NetIdentityTable table, SkillAbusePolicy policy, Func<ulong, string> resolveSlot,
            Action<string> log, Action<string> logError)
        {
            _nm = nm;
            _table = table;
            _policy = policy ?? SkillAbusePolicy.Default;
            _memory = new SkillAbuseMemory(_policy);
            _resolveSlot = resolveSlot;
            _log = log;
            _logError = logError;
            Current = this;
            _log("skill-server ready slots=" + SkillNetLimits.ActiveSlots +
                 " gateBurst=" + F(SkillIntentGate.Burst) + " gateRate=" + F(SkillIntentGate.RequestsPerSecond) +
                 " rejectBurst=" + F(_policy.RejectBurst) + " rejectRefill=" + F(_policy.RejectRefillPerSecond) +
                 " disconnectAfter=" + _policy.DisconnectExcess + " memorySeconds=" + F(_policy.MemorySeconds));
        }

        /// <summary>The server's monotonic clock: unscaled seconds since this process started. Not Time.time (scene loads reset it) and never request data.</summary>
        private static double Now => Time.realtimeSinceStartupAsDouble;

        public int ConnectionCount => _conns.Count;
        public int RememberedSlots => _memory.Count;

        public void Dispose()
        {
            _conns.Clear();
            if (Current == this) Current = null;
        }

        // ---- lifecycle -------------------------------------------------------------------------

        /// <summary>A player was admitted and spawned: creates the connection's gate and ledger (probation when its slot is remembered).</summary>
        public void OnAdmitted(NetIdentity row)
        {
            if (row == null) return;
            Conn conn = GetConn(row.ClientId, Now, row.Slot);
            conn.NetRig = row.Object != null ? row.Object.GetComponent<NetPlayerRig>() : null;
        }

        /// <summary>The connection ended (any reason): its gate and ledger are discarded; the abuse memory of its slot is not.</summary>
        public void OnClosed(ulong clientId)
        {
            if (!_conns.TryGetValue(clientId, out Conn conn)) return;
            _conns.Remove(clientId);
            SkillIntentGate g = conn.Gate;
            SkillRejectLedger l = conn.Ledger;
            _log("skill-conn final client=" + clientId + " slot=" + Dash(conn.Slot) +
                 " accepted=" + g.Accepted + " invalid=" + g.Invalid + " replay=" + g.Replay + " flood=" + g.Flood +
                 " cast=" + conn.Cast + " refused=" + conn.Refused +
                 " rejects=" + l.Total + " identity=" + l.Count(SkillRejectKind.Identity) + " excess=" + l.Excess +
                 " ignored=" + conn.IgnoredAfterDisconnect + " strikes=" + l.Strikes);
        }

        private Conn GetConn(ulong clientId, double now, string slotHint)
        {
            if (_conns.TryGetValue(clientId, out Conn existing))
            {
                if (string.IsNullOrEmpty(existing.Slot) && !string.IsNullOrEmpty(slotHint)) existing.Slot = slotHint;
                return existing;
            }

            string slot = !string.IsNullOrEmpty(slotHint) ? slotHint : (_resolveSlot != null ? _resolveSlot(clientId) : "");
            int strikes = _memory.StrikesFor(slot, now);
            Conn conn = new Conn { ClientId = clientId, Slot = slot ?? "" };
            conn.Ledger = new SkillRejectLedger(_policy, strikes, now);
            conn.Gate = strikes > 0 ? new SkillIntentGate(0.0, now) : new SkillIntentGate();
            _conns[clientId] = conn;

            if (strikes > 0)
            {
                _log("skill-abuse probation client=" + clientId + " slot=" + Dash(conn.Slot) + " strikes=" + strikes +
                     " disconnectAfter=" + conn.Ledger.Threshold + " rejectTokens=" + F(conn.Ledger.Tokens) +
                     " gateTokens=" + F(conn.Gate.Tokens) + " memoryLeft=" + F(_memory.SecondsLeft(slot, now)));
            }

            return conn;
        }

        // ---- receive ---------------------------------------------------------------------------

        /// <summary>
        /// A skill request arrived. <paramref name="target"/> is the player object the RPC was invoked on; <paramref name="sender"/>
        /// is RpcParams.Receive.SenderClientId.
        /// </summary>
        public void Receive(NetPlayerRig target, ulong sender, int slot, uint sequence)
        {
            double now = Now;
            Conn conn = GetConn(sender, now, null);
            if (conn.Disconnecting)
            {
                conn.IgnoredAfterDisconnect++;
                return;
            }

            // 1. identity first: nothing of the target's state is read, the sender pays.
            string fault = IdentityFault(target, sender, out NetIdentity row);
            if (fault != null)
            {
                string detail = " fault=" + fault + " targetNet=" + (target != null && target.NetworkObject != null ? target.NetworkObject.NetworkObjectId : 0UL);
                Reject(conn, SkillRejectKind.Identity, now, detail);
                return;
            }

            // 2. the connection's own gate, on the server's clock.
            SkillIntentVerdict verdict = conn.Gate.Submit(slot, sequence, now);
            if (verdict != SkillIntentVerdict.Accepted)
            {
                SkillRejectKind kind = verdict == SkillIntentVerdict.Invalid ? SkillRejectKind.Invalid
                    : verdict == SkillIntentVerdict.Replay ? SkillRejectKind.Replay : SkillRejectKind.Flood;
                Reject(conn, kind, now, " seq=" + sequence + " slot=" + slot);
                if (conn.Gate.Exhausted && !conn.Disconnecting) EndConnection(conn, "skill sequence exhausted", false, now);
                return;
            }

            // 3. the server's own kit decides.
            PlayerRig rig = row.Rig;
            SkillCaster skills = rig.Skills;
            ManaComponent mana = rig.Mana;
            SkillData skill = skills.ActiveSkill(slot);
            string skillId = skill != null && skill.skillId != null ? Cut(skill.skillId) : "";

            float manaBefore = mana != null ? mana.CurrentMana : -1f;
            bool cast = skills.TryCastSlot(slot, out SkillRefusalCode code);
            SkillStateData state = Capture(rig);
            if (cast) conn.Cast++; else conn.Refused++;

            long age = -1;
            NetMovementServer movement = NetMovementServer.Current;
            if (movement != null) movement.TryGetInputAge(rig.PlayerId, out age);
            float pitch = rig.Look != null ? rig.Look.Pitch : 0f;

            _log("skill-cast client=" + sender + " id=" + rig.PlayerId + " slot=" + slot + " seq=" + sequence + " skill=" + Dash(skillId) +
                 " cast=" + cast + " reason=" + code +
                 " manaBefore=" + F(manaBefore) + " manaAfter=" + F(state.Mana) + " maxMana=" + F(state.MaxMana) +
                 " cd0=" + F(state.Cooldown0) + " cd1=" + F(state.Cooldown1) +
                 " aimYaw=" + F(rig.transform.eulerAngles.y) + " aimPitch=" + F(pitch) + " inputAgeTicks=" + age);

            // 4. feedback. The host's own player never goes through this channel (it casts locally).
            if (sender == NetworkManager.ServerClientId) return;
            try
            {
                target.SkillResultRpc(new SkillResultWire
                {
                    Sequence = sequence,
                    Slot = (byte)slot,
                    Outcome = (byte)(cast ? SkillCastOutcome.Cast : SkillCastOutcome.Refused),
                    Reason = (byte)code,
                    State = SkillStateWire.From(in state),
                    SkillId = skillId
                });
                if (cast) target.SkillVisualRpc(new SkillVisualWire { Slot = (byte)slot, SkillId = skillId });
            }
            catch (Exception e)
            {
                _logError("skill feedback threw for client " + sender + ": " + e.Message);
            }
        }

        /// <summary>
        /// Null when the sender may use this object; else a short tag. Reads only identities (NetworkObject, ownership, row, rig
        /// reference, id equality): no health, mana, kit or id VALUE of the object the call was made on is touched.
        /// </summary>
        private string IdentityFault(NetPlayerRig target, ulong sender, out NetIdentity row)
        {
            row = null;
            if (target == null) return "no-target";
            if (_table == null || !_table.TryGetByClient(sender, out row) || row == null) return "no-player";

            NetworkObject obj = target.NetworkObject;
            if (obj == null || !obj.IsSpawned) return "not-spawned";
            if (!ReferenceEquals(obj, row.Object)) return "not-your-object";
            if (row.ClientId != sender || obj.OwnerClientId != sender) return "not-owner";

            PlayerRig rig = row.Rig;
            if (rig == null || !ReferenceEquals(target.Rig, rig) || row.PlayerId != rig.PlayerId) return "rig-mismatch";
            if (rig.Skills == null) return "no-kit";
            return null;
        }

        // ---- abuse -----------------------------------------------------------------------------

        private void Reject(Conn conn, SkillRejectKind kind, double now, string detail)
        {
            SkillRejectAction action = conn.Ledger.Record(kind, now);
            if (conn.Ledger.ShouldLog(kind))
            {
                _log("skill reject=" + kind + " client=" + conn.ClientId + " slot=" + Dash(conn.Slot) + detail +
                     " count=" + conn.Ledger.Count(kind) + " total=" + conn.Ledger.Total + " excess=" + conn.Ledger.Excess);
            }

            if (action == SkillRejectAction.Disconnect) EndConnection(conn, "skill abuse", true, now);
        }

        private void EndConnection(Conn conn, string reason, bool abuse, double now)
        {
            if (conn.Disconnecting) return;
            conn.Disconnecting = true;

            bool host = conn.ClientId == NetworkManager.ServerClientId;
            int strikes = abuse && !host ? _memory.RecordDisconnect(conn.Slot, now) : 0;
            SkillRejectLedger l = conn.Ledger;
            _log((abuse ? "skill-abuse disconnect" : "skill-conn end") + " client=" + conn.ClientId + " slot=" + Dash(conn.Slot) +
                 " reason='" + reason + "' strikes=" + strikes + " priorStrikes=" + l.Strikes +
                 " identity=" + l.Count(SkillRejectKind.Identity) + " invalid=" + l.Count(SkillRejectKind.Invalid) +
                 " replay=" + l.Count(SkillRejectKind.Replay) + " flood=" + l.Count(SkillRejectKind.Flood) +
                 " total=" + l.Total + " excess=" + l.Excess + " threshold=" + l.Threshold +
                 " memorySeconds=" + F(_policy.MemorySeconds) + (host ? " host=1 (not disconnected)" : ""));

            if (host || _nm == null) return;
            try { _nm.DisconnectClient(conn.ClientId, reason); }
            catch (Exception e) { _logError("skill disconnect threw for client " + conn.ClientId + ": " + e.Message); }
        }

        // ---- replicated state --------------------------------------------------------------------

        /// <summary>Called every frame by NetServer: publishes each owner's mana / cooldown picture a few times a second when it changed.</summary>
        public void Tick(float now)
        {
            if (now < _nextPublish) return;
            _nextPublish = now + PublishSeconds;

            _scratch.Clear();
            foreach (KeyValuePair<ulong, Conn> pair in _conns) _scratch.Add(pair.Key);
            for (int i = 0; i < _scratch.Count; i++)
            {
                if (!_conns.TryGetValue(_scratch[i], out Conn conn)) continue;
                if (conn.ClientId == NetworkManager.ServerClientId) continue;
                NetPlayerRig netRig = conn.NetRig;
                if (netRig == null || !netRig.IsSpawned || netRig.Rig == null || netRig.Rig.Skills == null) continue;

                SkillStateData state = Capture(netRig.Rig).Quantized();
                if (conn.Published && state.Equals(conn.LastPublished)) continue;
                conn.Published = true;
                conn.LastPublished = state;
                netRig.SkillState.Value = SkillStateWire.From(in state);
            }
        }

        /// <summary>The server's mana and per-slot cooldowns of one rig (the numbers every reply and the published variable carry).</summary>
        private static SkillStateData Capture(PlayerRig rig)
        {
            ManaComponent mana = rig.Mana;
            SkillCaster skills = rig.Skills;
            return new SkillStateData(
                mana != null ? mana.CurrentMana : 0f,
                mana != null ? mana.MaxMana : 0f,
                skills.CooldownRemaining(0), skills.CooldownRemaining(1),
                skills.Snapshot(0).CooldownSeconds, skills.Snapshot(1).CooldownSeconds);
        }

        // ---- helpers ---------------------------------------------------------------------------

        private static string Cut(string text)
        {
            return text.Length > SkillNetLimits.MaxSkillIdChars ? text.Substring(0, SkillNetLimits.MaxSkillIdChars) : text;
        }

        private static string Dash(string text) { return string.IsNullOrEmpty(text) ? "-" : text; }

        internal static string F(double value) { return value.ToString("F3", CultureInfo.InvariantCulture); }
    }
}
