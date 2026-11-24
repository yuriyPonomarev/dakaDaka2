using System;
using System.Collections.Generic;
using System.Globalization;
using Emberdelve.BladeDefense;
using Emberdelve.AI;
using Emberdelve.Combat;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Level;
using Emberdelve.Net.Rules;
using Emberdelve.Player;
using Unity.Netcode;
using UnityEngine;

namespace Emberdelve.Net
{
    /// <summary>
    /// U3B3, server only (owned by NetServer): weapon combat for networked players. The owner's NetCombatSender sends INTENTS
    /// (<see cref="CombatIntentKind"/>: attack press, guard down / up, draw start / release, weapon-set swap) through
    /// NetPlayerRig.CombatIntentRpc (reliable, ordered); everything else is decided here by the code the offline game already runs.
    ///
    /// Receive order (same as U3B2's skill channel):
    ///   1. IDENTITY FIRST: the connection that carried the message must be the owner of the object the call was made on and that object
    ///      must be its admitted player. Nothing of the object's state is read when this fails; the SENDER is charged.
    ///   2. The connection's own CombatIntentGate (kind, sequence, rate) on the server's monotonic clock.
    ///   3. EXECUTION on the sender's server rig (row.Rig): MeleeCombat.TryBeginSwing / RangedCombat.ServerBeginDraw / ServerReleaseDraw /
    ///      PlayerRig guard / ClassLoadout.RequestWeaponSetSwap, all of which end in LocalCombatAuthority (swept hit detection, guard,
    ///      hit location, damage, armor, falloff, teams and friendly fire). Both players and enemies are targets exactly as offline.
    ///      A draw's CHARGE is measured by the server's clock (ServerDrawTimer), never sent.
    ///   4. FEEDBACK: the owner gets one CombatResultRpc per request that passed the gate; observers get swing / shot / hit events
    ///      (presentation only); health and death are NetworkVariables; enemies are published as snapshots.
    ///
    /// SHARED ABUSE ACCOUNTING. Every reject is charged to the connection's SkillRejectLedger owned by NetSkillServer (one budget per
    /// connection, skill and combat traffic add up; log tag "combat"), so a flood on either channel ends in the same disconnect.
    ///
    /// AIM AND LATENCY. The swing / shot direction is the rig's current pose, i.e. the last SANITIZED movement frame NetMovementServer
    /// stepped; the intent carries no aim. Movement is unreliable and intents are reliable, so an action is resolved with the aim of the
    /// newest input frame the server had when the intent was dequeued, which can be older than the client's key press by the input's
    /// network delay (log field inputAgeTicks). A client cannot be favoured by sending a different aim: it has none to send.
    ///
    /// Server log lines (server tag from NetBootstrap):
    ///   combat-server ready ...
    ///   combat-spawn client=.. id=.. party=.. x=.. y=.. z=.. yaw=..
    ///   combat-intent client=.. id=.. kind=.. seq=.. outcome=.. reason=.. charge=.. aimYaw=.. aimPitch=.. inputAgeTicks=..
    ///   combat reject=KIND client=..  (via the shared ledger)
    ///   combat-swing id=.. weapon=.. pattern=.. step=.. windup=.. active=.. recovery=..
    ///   combat-shot id=.. serial=.. weapon=.. ox oy oz dx dy dz speed gravity maxTime range charge ..
    ///   combat-hit src=.. attacker=.. target=.. targetKind=.. weapon=.. amount=.. loc=.. locMult=.. blocked=.. blockReduction=.. ... dist=..
    ///   combat-flight-end serial=.. target=.. air=.. x y z
    ///   combat-vitals id=.. health=.. max=.. dead=..
    ///   combat-conn final client=.. accepted=.. refused=.. gate counters ...
    /// </summary>
    internal sealed class NetCombatServer
    {
        private const float PublishSeconds = 0.05f;
        private const float EnemyPoseSeconds = 0.2f;

        private sealed class Conn
        {
            public ulong ClientId;
            public int PlayerId;
            public CombatIntentGate Gate;
            public NetPlayerRig NetRig;
            public PlayerRig Rig;
            public MeleeCombat Melee;
            public RangedCombat Ranged;
            public Action<MeleeSwingIntent> SwingHandler;
            public int Accepted;
            public int Refused;
            public bool Published;
            public bool EnemiesSynced;
            public bool Bot;
        }

        public static NetCombatServer Current { get; private set; }

        private readonly NetworkManager _nm;
        private readonly NetIdentityTable _table;
        private readonly NetSkillServer _skills;
        private readonly Action<string> _log;
        private readonly Action<string> _logError;
        private readonly Dictionary<ulong, Conn> _conns = new Dictionary<ulong, Conn>();
        private readonly Dictionary<int, Conn> _byCombatId = new Dictionary<int, Conn>();
        private readonly Dictionary<int, CombatVitalsData> _enemyVitals = new Dictionary<int, CombatVitalsData>();
        private readonly Dictionary<int, EnemyPresentationWire> _enemyPresentation = new Dictionary<int, EnemyPresentationWire>();
        private readonly HashSet<int> _enemyPresentationChanges = new HashSet<int>();
        private readonly List<int> _scratch = new List<int>();
        private readonly List<int> _scratchIds = new List<int>();
        private readonly List<Rect> _retiredEnemyFloors = new List<Rect>();
        private LocalCombatAuthority _subscribed;
        private float _nextPublish;
        private float _nextEnemyPose;
        private ulong _nextHitEvent;

        public NetCombatServer(NetworkManager nm, NetIdentityTable table, NetSkillServer skills, Action<string> log, Action<string> logError)
        {
            _nm = nm;
            _table = table;
            _skills = skills;
            _log = log;
            _logError = logError;
            Current = this;
            _log("combat-server ready kinds=" + CombatNetLimits.KindMax +
                 " gateBurst=" + F(CombatIntentGate.Burst) + " gateRate=" + F(CombatIntentGate.RequestsPerSecond) +
                 " maxOverhold=" + F(CombatNetLimits.MaxDrawOverholdSeconds) + " sharedLedger=1");
        }

        private static double Now => Time.realtimeSinceStartupAsDouble;

        public int ConnectionCount => _conns.Count;
        public int ActorCount => _byCombatId.Count;

        public void Dispose()
        {
            Unsubscribe();
            foreach (var actor in _byCombatId.Values) DetachActor(actor);
            _conns.Clear();
            _byCombatId.Clear();
            _enemyVitals.Clear();
            _enemyPresentation.Clear();
            _enemyPresentationChanges.Clear();
            if (Current == this) Current = null;
        }

        // ---- lifecycle -------------------------------------------------------------------------

        /// <summary>A player was admitted and spawned: its rig becomes remote driven (a server rig has no input device) and gets a gate.</summary>
        public void OnAdmitted(NetIdentity row)
        {
            if (row == null || row.Rig == null) return;

            Conn conn = new Conn
            {
                ClientId = row.ClientId,
                PlayerId = row.PlayerId,
                Rig = row.Rig,
                NetRig = row.Object != null ? row.Object.GetComponent<NetPlayerRig>() : null,
                Melee = row.Rig.GetComponent<MeleeCombat>(),
                Ranged = row.Rig.GetComponent<RangedCombat>()
            };

            int strikes = _skills != null ? _skills.StrikesOf(row.ClientId) : 0;
            conn.Gate = strikes > 0 ? new CombatIntentGate(0.0, Now) : new CombatIntentGate();

            bool remote = row.ClientId != NetworkManager.ServerClientId;
            if (remote)
            {
                if (conn.Melee != null) conn.Melee.RemoteDriven = true;
                if (conn.Ranged != null) conn.Ranged.RemoteDriven = true;
            }

            if (conn.Melee != null)
            {
                Conn captured = conn;
                conn.SwingHandler = intent => OnSwingStarted(captured, intent);
                conn.Melee.AttackStarted += conn.SwingHandler;
            }

            _conns[row.ClientId] = conn;
            _byCombatId[row.Rig.CombatId] = conn;

            Vector3 p = row.Rig.transform.position;
            _log("combat-spawn client=" + row.ClientId + " id=" + row.PlayerId + " party=" + row.PartyId +
                 " x=" + F(p.x) + " y=" + F(p.y) + " z=" + F(p.z) + " yaw=" + F(row.Rig.transform.eulerAngles.y) +
                 " remote=" + (remote ? 1 : 0) + " strikes=" + strikes);
        }

        public void OnClosed(ulong clientId)
        {
            if (!_conns.TryGetValue(clientId, out Conn conn)) return;
            _conns.Remove(clientId);
            if (conn.Rig != null) _byCombatId.Remove(conn.Rig.CombatId);
            DetachActor(conn);

            CombatIntentGate g = conn.Gate;
            _log("combat-conn final client=" + clientId + " id=" + conn.PlayerId + " accepted=" + conn.Accepted + " refused=" + conn.Refused +
                 " gateAccepted=" + g.Accepted + " invalid=" + g.Invalid + " replay=" + g.Replay + " flood=" + g.Flood);
        }

        // ---- receive ---------------------------------------------------------------------------
        /// <summary>Registers a server-controlled actor without inventing a client connection or identity row.</summary>
        public bool OnBotAdmitted(PlayerRig rig, NetPlayerRig netRig)
        {
            if (NetRole.IsClientOnly || rig == null || netRig == null || netRig.Rig != rig || rig.IsLocallyControlled
                || !CombatSenders.Owns(CombatSenderId.ForPlayer(rig.PlayerId), rig.CombatId)
                || _byCombatId.ContainsKey(rig.CombatId)) return false;
            var actor = new Conn { Bot = true, PlayerId = rig.PlayerId, Rig = rig, NetRig = netRig,
                Melee = rig.GetComponent<MeleeCombat>(), Ranged = rig.GetComponent<RangedCombat>() };
            if (actor.Melee != null)
            {
                actor.Melee.RemoteDriven = true;
                actor.SwingHandler = intent => OnSwingStarted(actor, intent);
                actor.Melee.AttackStarted += actor.SwingHandler;
            }
            if (actor.Ranged != null) actor.Ranged.RemoteDriven = true;
            if (rig.Movement != null) rig.Movement.ExternalDriven = true;
            rig.SetInputEnabled(false);
            _byCombatId.Add(rig.CombatId, actor);
            return true;
        }

        public bool OnBotRemoved(int combatId)
        {
            if (!_byCombatId.TryGetValue(combatId, out var actor) || !actor.Bot) return false;
            DetachActor(actor);
            _byCombatId.Remove(combatId);
            return true;
        }

        private static void DetachActor(Conn actor)
        {
            if (actor.Melee != null)
            {
                actor.Melee.SetRemoteAttack(false);
                if (actor.SwingHandler != null) actor.Melee.AttackStarted -= actor.SwingHandler;
            }
            if (actor.Ranged != null) actor.Ranged.CancelDraw();
        }

        public void Receive(NetPlayerRig target, ulong sender, int kind, uint sequence)
        {
            if (_skills != null && _skills.DropIfDisconnecting(sender)) return;
            double now = Now;

            // 1. identity first.
            string fault = IdentityFault(target, sender, out NetIdentity row);
            if (fault != null)
            {
                string detail = " fault=" + fault + " targetNet=" + (target != null && target.NetworkObject != null ? target.NetworkObject.NetworkObjectId : 0UL);
                Charge(sender, SkillRejectKind.Identity, detail, false);
                return;
            }

            if (!_conns.TryGetValue(sender, out Conn conn))
            {
                Charge(sender, SkillRejectKind.Identity, " fault=no-combat-state", false);
                return;
            }

            // 2. the connection's own gate.
            SkillIntentVerdict verdict = conn.Gate.Submit(kind, sequence, now);
            if (verdict != SkillIntentVerdict.Accepted)
            {
                SkillRejectKind rejectKind = verdict == SkillIntentVerdict.Invalid ? SkillRejectKind.Invalid
                    : verdict == SkillIntentVerdict.Replay ? SkillRejectKind.Replay : SkillRejectKind.Flood;
                Charge(sender, rejectKind, " seq=" + sequence + " kind=" + kind, conn.Gate.Exhausted);
                return;
            }

            // 3. execution on the server's rig.
            CombatIntentKind intent = (CombatIntentKind)kind;
            PlayerRig rig = conn.Rig;
            float charge = 0f;
            string authorityReason = "";
            CombatRefusalCode refusal = sender == NetworkManager.ServerClientId
                ? CombatRefusalCode.ClientRole
                : Execute(conn, rig, intent, out charge, out authorityReason);
            bool accepted = refusal == CombatRefusalCode.None;
            if (accepted) conn.Accepted++; else conn.Refused++;

            long age = -1;
            NetMovementServer movement = NetMovementServer.Current;
            if (movement != null) movement.TryGetInputAge(rig.PlayerId, out age);
            float pitch = rig.Look != null ? rig.Look.Pitch : 0f;

            _log("combat-intent client=" + sender + " id=" + rig.PlayerId + " kind=" + intent + " seq=" + sequence +
                 " outcome=" + (accepted ? CombatOutcome.Accepted : CombatOutcome.Refused) + " reason=" + refusal +
                 " charge=" + F(charge) + " aimYaw=" + F(rig.transform.eulerAngles.y) + " aimPitch=" + F(pitch) +
                 " inputAgeTicks=" + age + (string.IsNullOrEmpty(authorityReason) ? "" : " authority='" + Safe(authorityReason) + "'"));

            // 4. feedback to the owner (the host's own player never uses this channel).
            if (sender == NetworkManager.ServerClientId) return;
            try
            {
                target.CombatResultRpc(new CombatResultWire
                {
                    Sequence = sequence,
                    Kind = (byte)kind,
                    Outcome = (byte)(accepted ? CombatOutcome.Accepted : CombatOutcome.Refused),
                    Reason = (byte)refusal,
                    Charge = charge
                });
            }
            catch (Exception e)
            {
                _logError("combat feedback threw for client " + sender + ": " + e.Message);
            }
        }

        private CombatRefusalCode Execute(Conn conn, PlayerRig rig, CombatIntentKind intent, out float charge, out string authorityReason)
        {
            charge = 0f;
            authorityReason = "";
            if (rig == null) return CombatRefusalCode.NoKit;
            if (rig.IsDead) return CombatRefusalCode.Dead;
            if (rig.IsInFloorTransit) { authorityReason = "floor transfer pending"; return CombatRefusalCode.Authority; }

            MeleeCombat melee = conn.Melee;
            RangedCombat ranged = conn.Ranged;
            rig.Loadout?.RefreshHeldCombat();
            bool heldMeleeAttack = rig.HeldMeleeWeapon != null &&
                (intent == CombatIntentKind.AttackPress || intent == CombatIntentKind.AttackHold);
            if (rig.IsHoldingUtility && !heldMeleeAttack && intent != CombatIntentKind.GuardUp && intent != CombatIntentKind.AttackRelease && intent != CombatIntentKind.DrawCancel && intent != CombatIntentKind.SwapWeapon)
                return CombatRefusalCode.WrongWeapon;
            bool meleeActive = melee != null && melee.isActiveAndEnabled && melee.Weapon != null && !melee.Weapon.IsRanged && melee.Weapon == rig.ActiveWeapon;
            bool rangedActive = ranged != null && ranged.isActiveAndEnabled && ranged.Weapon != null && ranged.Weapon.IsRanged;

            switch (intent)
            {
                case CombatIntentKind.AttackHold:
                    if (!meleeActive) return rangedActive ? CombatRefusalCode.WrongWeapon : CombatRefusalCode.NoKit;
                    melee.SetRemoteAttack(true);
                    return CombatRefusalCode.None;

                case CombatIntentKind.AttackRelease:
                    if (melee != null) melee.SetRemoteAttack(false);
                    return CombatRefusalCode.None;

                case CombatIntentKind.AttackPress:
                    if (!meleeActive) return rangedActive ? CombatRefusalCode.WrongWeapon : CombatRefusalCode.NoKit;
                    if (melee.RemoteGuardWanted || rig.IsGuarding) return CombatRefusalCode.Guarding;
                    if (melee.IsSwinging || melee.CooldownRemaining > 0f) return CombatRefusalCode.TooEarly;
                    return melee.TryBeginSwing() ? CombatRefusalCode.None : CombatRefusalCode.Authority;

                case CombatIntentKind.GuardDown:
                    if (rangedActive) return ranged.SetRemoteGuard(true);
                    if (!meleeActive) return CombatRefusalCode.WrongWeapon;
                    if (!rig.CanGuard) return CombatRefusalCode.CannotGuard;
                    melee.SetRemoteGuard(true);
                    return CombatRefusalCode.None;

                case CombatIntentKind.GuardUp:
                    if (ranged != null) ranged.SetRemoteGuard(false);
                    if (melee != null) melee.SetRemoteGuard(false);
                    return CombatRefusalCode.None;

                case CombatIntentKind.DrawCancel:
                    if (ranged != null) ranged.CancelDraw();
                    return CombatRefusalCode.None;

                case CombatIntentKind.Reload:
                    return rangedActive ? ranged.ServerRequestReload() : CombatRefusalCode.WrongWeapon;

                case CombatIntentKind.DrawStart:
                    if (!rangedActive) return meleeActive ? CombatRefusalCode.WrongWeapon : CombatRefusalCode.NoKit;
                    {
                        ranged.ServerBeginDraw(out CombatRefusalCode begin);
                        return begin;
                    }

                case CombatIntentKind.DrawRelease:
                    if (ranged == null) return CombatRefusalCode.NoKit;
                    {
                        ranged.ServerReleaseDraw(out CombatRefusalCode release, out charge, out authorityReason);
                        return release;
                    }

                case CombatIntentKind.SwapWeapon:
                    if (ranged != null) ranged.SetRemoteGuard(false);
                    if (rig.Loadout == null) return CombatRefusalCode.NoKit;
                    if (melee != null) melee.SetRemoteAttack(false);
                    if (melee != null) melee.SetRemoteGuard(false);
                    return rig.Loadout.RequestWeaponSetSwap() ? CombatRefusalCode.None : CombatRefusalCode.SwapRefused;

                default:
                    return CombatRefusalCode.Unsupported;
            }
        }

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
            return null;
        }

        private void Charge(ulong sender, SkillRejectKind kind, string detail, bool exhausted)
        {
            if (_skills != null) _skills.ChargeReject(sender, kind, "combat", detail, exhausted);
        }

        // ---- authority events ------------------------------------------------------------------

        private void EnsureSubscribed()
        {
            if (_subscribed != null) return;
            if (!GameServices.TryGet<ICombatAuthority>(out ICombatAuthority authority)) return;
            LocalCombatAuthority local = authority as LocalCombatAuthority;
            if (local == null) return;
            _subscribed = local;
            local.HitResolved += OnHit;
            local.ProjectileLaunched += OnLaunch;
            local.FlightEnded += OnFlightEnd;
        }

        private void Unsubscribe()
        {
            if (_subscribed == null) return;
            _subscribed.HitResolved -= OnHit;
            _subscribed.ProjectileLaunched -= OnLaunch;
            _subscribed.FlightEnded -= OnFlightEnd;
            _subscribed = null;
        }

        private void OnSwingStarted(Conn conn, MeleeSwingIntent intent)
        {
            MeleeCombat melee = conn.Melee;
            SwingPatternData pattern = melee != null ? melee.ActivePattern : null;
            SwingTiming timing = melee != null ? melee.ActiveTiming : default(SwingTiming);
            string patternId = pattern != null && pattern.patternId != null ? pattern.patternId : "";

            // U3C: the committed trajectory is the authority's own record of this swing (the same data its sweep uses), never the intent's.
            BladeWarning warning = default(BladeWarning);
            bool aimCommitted = false;
            int riposteStrike = 0;
            if (melee != null && GameServices.TryGet<ICombatAuthority>(out ICombatAuthority swingAuthority)
                && swingAuthority.TryGetMeleeSwingStatus(melee.LastSwingId, out MeleeSwingStatus swingStatus))
            {
                warning = swingStatus.Warning;
                aimCommitted = swingStatus.AimCommitted;
                riposteStrike = swingStatus.RiposteStrike;
            }

            _log("combat-swing id=" + conn.PlayerId + " weapon=" + Dash(intent.WeaponId) + " pattern=" + Dash(patternId) + " step=" + intent.ComboStep +
                 " windup=" + F(timing.Windup) + " active=" + F(timing.Active) + " recovery=" + F(timing.Recovery) +
                 " trajectory=" + warning.Trajectory + " height=" + warning.Height + " yaw=" + F(warning.AimYaw) + " pitch=" + F(warning.AimPitch) +
                 " minyaw=" + F(warning.MinBladeYaw) + " maxyaw=" + F(warning.MaxBladeYaw) + " reach=" + F(warning.Reach) +
                 " committed=" + (aimCommitted ? 1 : 0) + " riposte=" + riposteStrike);

            if (conn.NetRig == null || !conn.NetRig.IsSpawned) return;
            PublishAvatar(conn);
            try
            {
                CombatSwingWire wire = new CombatSwingWire
                {
                    WeaponId = intent.WeaponId,
                    PatternId = patternId,
                    ComboStep = (byte)Mathf.Clamp(intent.ComboStep, 0, 255),
                    Windup = timing.Windup,
                    Active = timing.Active,
                    Recovery = timing.Recovery
                };
                wire.SetWarning(in warning, aimCommitted);
                wire.Riposte = (byte)Mathf.Clamp(riposteStrike, 0, 255);
                conn.NetRig.CombatSwingRpc(wire);
            }
            catch (Exception e) { _logError("combat swing event threw: " + e.Message); }
        }

        private void OnLaunch(CombatLaunchReport r)
        {
            _byCombatId.TryGetValue(r.InstigatorId, out Conn conn);
            int id = conn != null ? conn.PlayerId : 0;
            _log("combat-shot id=" + id + " combat=" + r.InstigatorId + " serial=" + r.Serial + " weapon=" + Dash(r.WeaponId) +
                 " ox=" + F(r.Origin.x) + " oy=" + F(r.Origin.y) + " oz=" + F(r.Origin.z) +
                 " dx=" + F(r.Direction.x) + " dy=" + F(r.Direction.y) + " dz=" + F(r.Direction.z) +
                 " speed=" + F(r.Speed) + " gravity=" + F(r.Gravity) + " maxTime=" + F(r.MaxFlightTime) + " range=" + F(r.Range) +
                 " charge=" + F(r.Charge) + " radius=" + F(r.Radius) + " length=" + F(r.Length));

            NetPlayerRig carrier = conn != null ? conn.NetRig : null;
            // An enemy's arrow (the skeleton archer) goes out on any spawned player object: the RPC reaches every client.
            if ((carrier == null || !carrier.IsSpawned) && IsEnemyInstigator(r.InstigatorId)) carrier = BroadcastCarrier();
            if (carrier == null || !carrier.IsSpawned) return;
            try
            {
                carrier.CombatShotRpc(new CombatShotWire
                {
                    Serial = (uint)Mathf.Max(0, r.Serial),
                    WeaponId = r.WeaponId,
                    OriginX = r.Origin.x, OriginY = r.Origin.y, OriginZ = r.Origin.z,
                    DirX = r.Direction.x, DirY = r.Direction.y, DirZ = r.Direction.z,
                    Speed = r.Speed,
                    Gravity = r.Gravity,
                    MaxFlightTime = r.MaxFlightTime,
                    Range = r.Range,
                    Charge = r.Charge,
                    Radius = r.Radius,
                    Length = r.Length
                });
            }
            catch (Exception e) { _logError("combat shot event threw: " + e.Message); }
        }

        private void OnFlightEnd(CombatFlightEnd end)
        {
            _log("combat-flight-end serial=" + end.Serial + " instigator=" + end.InstigatorId + " target=" + end.TargetId + " air=" + F(end.AirTime) +
                 " x=" + F(end.Position.x) + " y=" + F(end.Position.y) + " z=" + F(end.Position.z));

            _byCombatId.TryGetValue(end.InstigatorId, out Conn conn);
            NetPlayerRig carrier = conn != null ? conn.NetRig : null;
            if ((carrier == null || !carrier.IsSpawned) && IsEnemyInstigator(end.InstigatorId)) carrier = BroadcastCarrier();
            if (carrier == null || !carrier.IsSpawned) return;
            try
            {
                carrier.CombatFlightEndRpc(new CombatFlightEndWire
                {
                    Serial = (uint)Mathf.Max(0, end.Serial),
                    X = end.Position.x, Y = end.Position.y, Z = end.Position.z,
                    AirTime = end.AirTime
                });
            }
            catch (Exception e) { _logError("combat flight event threw: " + e.Message); }
        }

        private static bool IsEnemyInstigator(int combatId) =>
            CombatRegistry.TryGet(combatId, out ICombatEntity entity) && entity != null && entity.Kind == EntityKind.Enemy;

        /// <summary>Any spawned player object; its ClientsAndHost RPCs carry world events that belong to no player (an enemy's arrow).</summary>
        private NetPlayerRig BroadcastCarrier()
        {
            foreach (Conn c in _conns.Values)
                if (c != null && c.NetRig != null && c.NetRig.IsSpawned) return c.NetRig;
            return null;
        }

        private void OnHit(CombatHitReport r)
        {
            CombatRegistry.TryGet(r.AttackerId, out ICombatEntity attacker);
            CombatRegistry.TryGet(r.TargetId, out ICombatEntity victim);
            bool targetIsEnemy = victim != null && victim.Kind == EntityKind.Enemy;
            float dist = attacker != null && victim != null ? Vector3.Distance(attacker.CombatCenter, victim.CombatCenter) : -1f;

            _byCombatId.TryGetValue(r.AttackerId, out Conn attackerConn);
            _byCombatId.TryGetValue(r.TargetId, out Conn targetConn);
            int attackerId = attackerConn != null ? attackerConn.PlayerId : 0;
            int targetId = targetConn != null ? targetConn.PlayerId : 0;

            _log("combat-hit src=" + r.Source + " attacker=" + attackerId + " attackerCombat=" + r.AttackerId +
                 " target=" + targetId + " targetCombat=" + r.TargetId + " targetKind=" + (targetIsEnemy ? "Enemy" : "Player") +
                 " weapon=" + Dash(r.WeaponId) + " amount=" + F(r.Amount) + " loc=" + r.Location + " locMult=" + F(r.LocationMultiplier) +
                 " blocked=" + r.Blocked + " blockReduction=" + F(r.BlockReduction) + " guardBroken=" + r.GuardBroken + " killed=" + r.Killed +
                 " outMult=" + F(r.OutgoingMultiplier) + " flat=" + F(r.FlatBonus) + " armor=" + F(r.ArmorRating) +
                 " step=" + F(r.StepMultiplier) + " zone=" + F(r.ZoneMultiplier) + " contactT=" + F(r.ContactT) +
                 " charge=" + F(r.Charge) + " air=" + F(r.AirTime) + " airScale=" + F(r.AirTimeScale) + " dist=" + F(dist) +
                 " px=" + F(r.Point.x) + " py=" + F(r.Point.y) + " pz=" + F(r.Point.z) + " parried=" + r.Parried);

            NetPlayerRig carrier = attackerConn != null ? attackerConn.NetRig : (targetConn != null ? targetConn.NetRig : null);
            if (carrier == null || !carrier.IsSpawned) return;
            try
            {
                carrier.CombatHitRpc(new CombatHitWire
                {
                    EventId = ++_nextHitEvent,
                    PointX = r.Point.x, PointY = r.Point.y, PointZ = r.Point.z,
                    Source = (byte)r.Source,
                    Attacker = attackerId,
                    Target = targetIsEnemy ? r.TargetId : targetId,
                    TargetIsEnemy = targetIsEnemy,
                    Amount = r.Amount,
                    Location = (byte)r.Location,
                    Blocked = r.Blocked,
                    Parried = r.Parried,
                    GuardBroken = r.GuardBroken,
                    Killed = r.Killed
                });
            }
            catch (Exception e) { _logError("combat hit event threw: " + e.Message); }
        }

        // ---- replicated state --------------------------------------------------------------------

        /// <summary>Called every frame by NetServer: subscribes to the authority once it exists, publishes vitals, enemy snapshots, stale draws.</summary>
        public void Tick(float now)
        {
            EnsureSubscribed();
            if (now < _nextPublish) return;
            _nextPublish = now + PublishSeconds;

            _scratch.Clear();
            foreach (KeyValuePair<int, Conn> pair in _byCombatId) _scratch.Add(pair.Key);
            for (int i = 0; i < _scratch.Count; i++)
            {
                if (!_byCombatId.TryGetValue(_scratch[i], out Conn conn)) continue;
                if (conn.Rig == null) continue;

                RangedCombat ranged = conn.Ranged;
                if (ranged != null && ranged.IsDrawing && ranged.ServerDraw.IsStale(Time.time)) ranged.CancelDraw();

                PublishVitals(conn);
            }

            if (now >= _nextEnemyPose)
            {
                _nextEnemyPose = now + EnemyPoseSeconds;
                PublishEnemies();
            }
        }

        private void PublishAvatar(Conn conn)
        {
            if (conn.NetRig == null || !conn.NetRig.IsSpawned) return;
            var rig=conn.Rig;
            var pose=new AvatarCombatWire {
                Bot=conn.Bot,
                Utility=rig.Inventory != null && rig.Inventory.HeldItem != null && rig.HeldMeleeWeapon == null ? rig.Inventory.HeldItem.itemId : "",
                Main=rig.ActiveWeapon!=null ? rig.ActiveWeapon.weaponId : "",
                Off=!rig.IsHoldingUtility && rig.OffhandWeapon!=null && (rig.ActiveWeapon==null || !rig.ActiveWeapon.twoHanded) ? rig.OffhandWeapon.weaponId : "",
                Guard=rig.IsGuarding,
                Crouched=rig.Movement != null && rig.Movement.IsCrouched,
                InFloorTransit=rig.IsInFloorTransit,
                EyeOffset=rig.AimOrigin-rig.transform.position,
                Aim=Quaternion.LookRotation(rig.AimDirection,Vector3.up)
            };
            var channel = rig.GetComponent<Emberdelve.Items.ConsumableChannel>();
            if (!rig.IsDead && !rig.IsExtracted && rig.IsHoldingUtility && channel != null && channel.IsChanneling
                && rig.Inventory.HeldStackId == channel.ChannelStackId)
            {
                pose.Drinking = true;
                pose.DrinkProgress = channel.ChannelProgress;
                pose.DrinkDuration = channel.ChannelDuration;
            }
            var melee=conn.Melee;
            var ranged = conn.Ranged;
            pose.CrossbowLoaded = !rig.IsDead && !rig.IsExtracted && !rig.IsHoldingUtility
                && rig.ActiveWeapon != null && rig.ActiveWeapon.CanCrossbowGuard
                && ranged != null && ranged.HasAuthoritativeAmmo && ranged.Magazine > 0 && !ranged.IsReloading;
            if (ranged != null && ranged.isActiveAndEnabled && ranged.IsReloading
                && !rig.IsDead && !rig.IsExtracted && !rig.IsHoldingUtility)
            {
                pose.Reloading = true;
                pose.ReloadProgress = ranged.ReloadProgress;
                pose.ReloadDuration = ranged.ReloadDuration;
            }
            if (melee != null && melee.TryGetSwingPresentation(out var status))
            {
                pose.Swing=!status.Recoiled; pose.Recoil=status.Recoiled;
                pose.StopFraction=Mathf.Clamp01((float)status.WallContactSample/Mathf.Max(1,status.SampleCount));
                pose.RecoilDuration=status.Timing.WallRecoil;
                pose.SwingId=status.SwingId; pose.Pattern=melee.ActivePattern.patternId;
                pose.Elapsed=status.Elapsed; pose.Windup=status.Timing.Windup; pose.Active=status.Timing.Active; pose.Recovery=status.Timing.Recovery;
                pose.EyeOffset=status.PresentationFrame.Eye-rig.transform.position; pose.Aim=status.PresentationFrame.Rotation;
                pose.SampleTime=conn.NetRig.NetworkManager.ServerTime.Time;
            }
            if (ranged != null && ranged.isActiveAndEnabled && ranged.IsDrawing
                && !rig.IsDead && !rig.IsExtracted && !rig.IsHoldingUtility
                && !pose.Guard && !pose.Reloading && !pose.Drinking && !pose.Swing && !pose.Recoil)
            {
                pose.Drawing = true;
                pose.DrawProgress = ranged.Charge;
            }
            if (!pose.Equals(conn.NetRig.AvatarCombat.Value)) conn.NetRig.AvatarCombat.Value=pose;
        }

        private void PublishVitals(Conn conn)
        {
            NetPlayerRig netRig = conn.NetRig;
            if (netRig == null || !netRig.IsSpawned) return;

            PublishAvatar(conn);
            // Guard can change without any health change (release, stagger recovery, equipment swap).
            netRig.GuardRaised.Value = conn.Rig.IsGuarding;
            netRig.ImpactGuardRemaining.Value = Mathf.Round(conn.Rig.ImpactGuardRemaining * 100f) / 100f;
            var ranged = conn.Ranged;
            RangedAmmoWire ammo = default;
            if (ranged != null && ranged.isActiveAndEnabled && ranged.Weapon != null && ranged.Weapon.IsRanged
                && !conn.Rig.IsDead && !conn.Rig.IsExtracted && !conn.Rig.IsHoldingUtility)
                ammo = new RangedAmmoWire { Weapon = ranged.Weapon.weaponId, Magazine = ranged.Magazine, Reserve = ranged.Reserve };
            if (!ammo.Equals(netRig.RangedAmmo.Value))
            {
                netRig.RangedAmmo.Value = ammo;
                _log("combat-ammo id=" + conn.PlayerId + " weapon=" + ammo.Weapon
                    + " magazine=" + ammo.Magazine + " reserve=" + ammo.Reserve);
            }
            CombatVitalsData vitals = new CombatVitalsData(conn.Rig.CurrentHealth, conn.Rig.MaxHealth, conn.Rig.IsDead).Quantized();
            var wire = CombatVitalsWire.From(in vitals, conn.Rig.IsExtracted);
            if (conn.Published && wire.Equals(netRig.Vitals.Value)) return;
            conn.Published = true;
            netRig.Vitals.Value = wire;
            _log("combat-vitals id=" + conn.PlayerId + " health=" + F(vitals.Health) + " max=" + F(vitals.MaxHealth) + " dead=" + vitals.Dead);
        }

        /// <summary>
        /// Enemies are not NetworkObjects (the dungeon's enemy spawner is scene-authored and client processes have none); the server sends each
        /// client a snapshot through the client's own player object: poses a few times a second (unreliable) and health / death on change
        /// (reliable). A client that just joined gets every enemy's vitals once. Presentation: proxy capsules (NetEnemyViews).
        /// </summary>
        private void PublishEnemies()
        {
            if (NetRole.EnemiesDisabled) return;
            _retiredEnemyFloors.Clear();
            foreach (var map in UnityEngine.Object.FindObjectsByType<DungeonModuleSceneAssembler>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (!map.RuntimeContentRetired || map.RuntimeLayout == null) continue;
                var layout = map.RuntimeLayout;
                _retiredEnemyFloors.Add(new Rect(layout.origin.x, layout.origin.z,
                    layout.gridWidth * layout.CellSize, layout.gridHeight * layout.CellSize));
            }
            IReadOnlyList<ICombatEntity> all = CombatRegistry.All;

            _scratchIds.Clear();
            bool anyChange = false;
            for (int i = 0; i < all.Count; i++)
            {
                ICombatEntity entity = all[i];
                if (entity == null || entity.Kind != EntityKind.Enemy) continue;
                // Retirement is permanent and explicit. Ordinary inactive/dormant enemies keep their
                // existing publication behavior; no fake death or damage is applied to either kind.
                Vector3 position = entity is Component component ? component.transform.position : entity.CombatCenter;
                if (_retiredEnemyFloors.Exists(bounds => bounds.Contains(new Vector2(position.x, position.z))))
                {
                    _enemyVitals.Remove(entity.CombatId); _enemyPresentation.Remove(entity.CombatId);
                    continue;
                }
                CombatVitalsData current = new CombatVitalsData(entity.CurrentHealth, entity.MaxHealth, entity.IsDead).Quantized();
                int id = entity.CombatId;
                _scratchIds.Add(id);
                bool known = _enemyVitals.TryGetValue(id, out CombatVitalsData prior);
                if (!known || !prior.Equals(current))
                {
                    _enemyVitals[id] = current;
                    anyChange = true;
                    if (!known || prior.Dead != current.Dead || current.Health < prior.Health)
                        _log("combat-enemy id=" + id + " health=" + F(current.Health) + " max=" + F(current.MaxHealth) + " dead=" + current.Dead);
                }
            }

            _enemyPresentationChanges.Clear();
            for (int i = 0; i < _scratchIds.Count; i++)
            {
                int id = _scratchIds[i];
                if (!CombatRegistry.TryGet(id, out ICombatEntity entity)) continue;
                EnemyPresentationWire current = BuildEnemyPresentation(entity);
                bool known = _enemyPresentation.TryGetValue(id, out EnemyPresentationWire previous);
                bool changed = !known || previous.VisualKind != current.VisualKind || previous.Flags != current.Flags ||
                    previous.SwingsBegun != current.SwingsBegun || previous.HitSequence != current.HitSequence ||
                    previous.WindupSeconds != current.WindupSeconds || previous.SwingSeconds != current.SwingSeconds ||
                    previous.AttackPhase != current.AttackPhase || previous.PhaseDuration != current.PhaseDuration ||
                    previous.RecoilStart != current.RecoilStart || previous.AttackVariant != current.AttackVariant;
                _enemyPresentation[id] = current;
                if (changed)
                {
                    _enemyPresentationChanges.Add(id);
                    _log("combat-enemy-presentation id=" + id + " kind=" + current.VisualKindName + " windingUp=" + current.IsWindingUp +
                         " swings=" + current.SwingsBegun + " hits=" + current.HitSequence + " dead=" + current.IsDead +
                         " phase=" + current.AttackPhase + " duration=" + current.PhaseDuration.ToString("F3", CultureInfo.InvariantCulture));
                }
            }

            foreach (KeyValuePair<ulong, Conn> pair in _conns)
            {
                Conn conn = pair.Value;
                if (conn.ClientId == NetworkManager.ServerClientId) continue;
                NetPlayerRig carrier = conn.NetRig;
                if (carrier == null || !carrier.IsSpawned) continue;

                try
                {
                    bool full = !conn.EnemiesSynced;
                    for (int i = 0; i < _scratchIds.Count; i++)
                    {
                        int id = _scratchIds[i];
                        if (!CombatRegistry.TryGet(id, out ICombatEntity entity)) continue;
                        CombatVitalsData v = _enemyVitals[id];
                        EnemyPresentationWire presentation = _enemyPresentation[id];
                        if (full || anyChange)
                        {
                            // vitals are small and rare; resending all on any change keeps the client trivially consistent
                            carrier.EnemyVitalsRpc(new EnemyVitalsWire { Enemy = id, Health = v.Health, MaxHealth = v.MaxHealth, Dead = v.Dead });
                        }

                        if (full || _enemyPresentationChanges.Contains(id)) carrier.EnemyPresentationRpc(presentation);

                        if (v.Dead) continue;
                        Component comp = entity as Component;
                        // Proxy positions use the stable root frame, not the lowered resting combat centre.
                        Vector3 pos = comp != null ? comp.transform.position + Vector3.up * 1.15f : entity.CombatCenter;
                        float yaw = comp != null ? comp.transform.eulerAngles.y : 0f;
                        carrier.EnemyPoseRpc(new EnemyPoseWire { Enemy = id, X = pos.x, Y = pos.y, Z = pos.z, Yaw = yaw });
                    }

                    conn.EnemiesSynced = true;
                }
                catch (Exception e) { _logError("enemy snapshot threw: " + e.Message); }
            }
        }

        private static EnemyPresentationWire BuildEnemyPresentation(ICombatEntity entity)
        {
            EnemyAgent agent = entity as EnemyAgent;
            byte kind = ResolveVisualKind(agent);
            EnemyAttackPhase phase = agent != null ? agent.AttackPresentation : default;
            byte flags = 0;
            if (agent != null && agent.IsWindingUp) flags |= EnemyPresentationWire.WindingUpFlag;
            if (entity.IsDead) flags |= EnemyPresentationWire.DeadFlag;
            else if (agent != null && agent.IsDormant) flags |= EnemyPresentationWire.DormantFlag;
            else if (agent != null && agent.IsWaking) flags |= EnemyPresentationWire.WakingFlag;
            return new EnemyPresentationWire
            {
                Version = EnemyPresentationWire.CurrentVersion,
                Enemy = entity.CombatId,
                VisualKind = kind,
                Flags = flags,
                SwingsBegun = agent != null ? (uint)Math.Max(0, agent.SwingsBegun) : 0u,
                HitSequence = agent != null ? agent.DamageSequence : 0u,
                WindupSeconds = agent != null ? agent.WindupSeconds : 0f,
                SwingSeconds = agent != null ? agent.AttackSwingSeconds : 0f,
                AttackPhase = phase.Phase,
                PhaseElapsed = phase.Elapsed,
                PhaseDuration = phase.Duration,
                RecoilStart = phase.RecoilStart,
                WakeElapsed = agent != null && agent.Brain != null ? agent.Brain.WakeElapsed : 0f,
                AttackVariant = agent != null ? WireAttackVariant(agent) : (byte)0,
                SampleTime = NetworkManager.Singleton != null ? NetworkManager.Singleton.ServerTime.Time : 0d
            };
        }

        private static bool _loggedUnknownVariant;

        // Variants the clients can animate go out as they are; an unknown index is sent as the original attack and reported once (never silently
        // shown as the overhead).
        private static byte WireAttackVariant(EnemyAgent agent)
        {
            int variant = agent.AttackVariantIndex;
            if (variant >= 0 && variant <= EnemyPresentationWire.MaxAttackVariant) return (byte)variant;
            if (!_loggedUnknownVariant)
            {
                _loggedUnknownVariant = true;
                UnityEngine.Debug.LogError(NetTuning.LogPrefix + "enemy " + agent.CombatId + " chose attack variant " + variant +
                    " which no client animates (max " + EnemyPresentationWire.MaxAttackVariant + "); sending the original attack.");
            }
            return 0;
        }

        // The server reads the actual authored visual binding. Data.elite is only a fail-safe when no built visual/controller is present.
        private static byte ResolveVisualKind(EnemyAgent agent)
        {
            if (agent == null) return 0;
            if (agent.UsesCinderClub) return EnemyPresentationWire.CinderWardenKind;
            EnemyVisualDriver driver = agent.GetComponentInChildren<EnemyVisualDriver>(true);
            RuntimeAnimatorController controller = driver != null && driver.Animator != null ? driver.Animator.runtimeAnimatorController : null;
            string name = controller != null ? controller.name : string.Empty;
            if (name.EndsWith("_Archer", StringComparison.Ordinal) || agent.IsArcher) return EnemyPresentationWire.ArcherKind;
            if (name.EndsWith("_Rogue", StringComparison.Ordinal)) return 1;
            if (name.EndsWith("_Warrior", StringComparison.Ordinal)) return 2;
            if (name.EndsWith("_Mage", StringComparison.Ordinal)) return 3;
            if (name.EndsWith("_Minion", StringComparison.Ordinal)) return 0;
            return agent.Data != null && agent.Data.isElite ? (byte)2 : (byte)0;
        }

        // ---- helpers ---------------------------------------------------------------------------

        private static string Dash(string text) { return string.IsNullOrEmpty(text) ? "-" : text; }

        private static string Safe(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string t = text.Length > 80 ? text.Substring(0, 80) : text;
            return t.Replace('\n', ' ').Replace('\r', ' ').Replace('\'', '`');
        }

        internal static string F(double value) { return value.ToString("F3", CultureInfo.InvariantCulture); }
    }
}
