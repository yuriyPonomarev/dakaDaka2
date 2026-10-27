using System;
using System.Collections;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Emberdelve.AI;
using Emberdelve.Combat;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Flow;
using Emberdelve.Player;

namespace Emberdelve.EditorTools
{
    /// <summary>
    /// R1c1 gate: combat commands carry a sender, and the authority refuses a command whose instigator/caster combat id
    /// the sender does not own. Real Level01 run with two admitted players (P1 warrior, P2 archer in a rival party):
    /// for every command type (attack, swing, projectile, skill cast, skill complete, skill hit, skill projectile) the
    /// OTHER player, the trusted host and a sender-less command are refused with the authority state unchanged (no
    /// damage, mana, ammo, cooldown stamp, sequence, swing or activation), the identical command from its owner is then
    /// accepted (so the forged attempts consumed nothing), a replay is refused by both rules, enemies keep working
    /// through the trusted host, and the real components (MeleeCombat, RangedCombat, SkillCaster) send as their rig.
    /// Entry: <c>Emberdelve.EditorTools.EmberdelveSenderOwnershipGate.Run</c>.
    /// </summary>
    [InitializeOnLoad]
    public static class EmberdelveSenderOwnershipGate
    {
        private const string Pending = "Emberdelve.SenderOwnership.Pending";

        static EmberdelveSenderOwnershipGate()
        {
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.EnteredPlayMode && SessionState.GetBool(Pending, false))
                {
                    SessionState.SetBool(Pending, false);
                    new GameObject("SenderOwnership_Gate").AddComponent<SenderOwnershipHarness>();
                }
            };
        }

        public static void Run()
        {
            SessionState.SetBool(Pending, true);
            EditorSceneManager.OpenScene("Assets/Emberdelve/Scenes/Emberdelve_Level01.unity");
            EditorApplication.EnterPlaymode();
        }
    }

    public sealed class SenderOwnershipHarness : MonoBehaviour
    {
        private static readonly Vector3 Spot = new Vector3(3000f, 0f, 3400f);

        private int _n, _fail;

        private RunDirector _director;
        private LocalCombatAuthority _authority;
        private PlayerRig _p1, _p2;
        private EnemyAgent _enemy;
        private CombatSenderId _s1, _s2;
        private SkillData _cry, _multi, _pierce;

        private delegate bool Command(out string reason);

        private struct State
        {
            public int Accepted, Rejected, Swings, Active, Open, Sequence, Ammo;
            public float LastAttack, Mana;
        }

        private void Check(bool ok, string text)
        {
            _n++;
            if (!ok)
            {
                _fail++;
                Debug.LogError("[SenderOwnership] mismatch " + text);
            }
        }

        private static bool Has(string text, string part) => text != null && text.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;

        private IEnumerator Start()
        {
            yield return null;
            yield return null;

            IEnumerator run = Execute();
            while (true)
            {
                bool more;
                try { more = run.MoveNext(); }
                catch (Exception e) { _fail++; Debug.LogException(e); break; }
                if (!more) break;
                yield return run.Current;
            }

            if (_authority != null) _authority.AllowLegacyMeleeAttacks = false;
            CombatTeams.FriendlyFire = false;
            Debug.Log("[Emberdelve][SenderOwnership] " + (_fail == 0 ? "PASS" : "FAIL") + " checks=" + _n + " failures=" + _fail);
            EditorApplication.Exit(_fail == 0 ? 0 : 1);
        }

        // -------------------------------------------------------------------------------------- helpers

        private void Place(PlayerRig rig, Vector3 feet)
        {
            CharacterController controller = rig.GetComponent<CharacterController>();
            if (controller != null) controller.enabled = false;
            rig.transform.SetPositionAndRotation(new Vector3(feet.x, feet.y + 0.15f, feet.z), Quaternion.identity);
            if (controller != null) controller.enabled = true;
            Physics.SyncTransforms();
        }

        private static Vector3 Flat(Vector3 v)
        {
            v.y = 0f;
            return v.sqrMagnitude < 0.0001f ? Vector3.forward : v.normalized;
        }

        private static RangedCombat RangedOf(PlayerRig rig) => rig != null ? rig.GetComponent<RangedCombat>() : null;

        private State Snap(int combatId)
        {
            State s = new State
            {
                Accepted = _authority.AcceptedCount,
                Rejected = _authority.RejectedCount,
                Swings = _authority.SwingsStarted,
                Active = _authority.ActiveSwingCount,
                Open = _authority.OpenActivationCount,
                Sequence = _authority.LastSequenceFor(combatId),
                LastAttack = _authority.LastAttackTimeFor(combatId),
            };
            if (CombatRegistry.TryGet(combatId, out ICombatEntity entity) && entity is PlayerRig rig)
            {
                s.Mana = rig.Mana != null ? rig.Mana.CurrentMana : 0f;
                RangedCombat ranged = RangedOf(rig);
                s.Ammo = ranged != null ? ranged.Magazine : -1;
            }
            return s;
        }

        private static bool Unchanged(State a, State b)
        {
            return a.Accepted == b.Accepted && a.Swings == b.Swings && a.Active == b.Active && a.Open == b.Open
                   && a.Sequence == b.Sequence && a.Ammo == b.Ammo
                   && a.LastAttack.Equals(b.LastAttack) && Mathf.Abs(a.Mana - b.Mana) < 0.001f;
        }

        /// <summary>Runs a command that must be refused by the sender rule and leave every piece of authority state alone.</summary>
        private void Forged(string label, int entityId, Command command)
        {
            State before = Snap(entityId);
            bool ok = command(out string reason);
            State after = Snap(entityId);
            Check(!ok, label + " is refused");
            Check(Has(reason, "sender"), label + " names the sender rule (" + reason + ")");
            Check(Unchanged(before, after) && after.Rejected == before.Rejected + 1,
                label + " changed nothing: damage/mana/ammo/stamp/sequence/swing/activation all as before");
        }

        private void Refresh()
        {
            if (_p1.Health != null) _p1.Health.Heal(100000f);
            if (_p2.Health != null) _p2.Health.Heal(100000f);
            EmberdelveKitFixtures.RefillMana(_p1);
            EmberdelveKitFixtures.RefillMana(_p2);
            RangedCombat r2 = RangedOf(_p2);
            if (r2 != null) EmberdelveKitFixtures.RefundAmmo(r2, 20);
        }

        private T Load<T>(string file) where T : UnityEngine.Object
        {
            T asset = AssetDatabase.LoadAssetAtPath<T>(EmberdelvePaths.Data + "/" + file);
            Check(asset != null, "asset available: " + file);
            return asset;
        }

        // -------------------------------------------------------------------------------------- the gate

        private IEnumerator Execute()
        {
            _p1 = FindFirstObjectByType<PlayerRig>();
            _director = FindFirstObjectByType<RunDirector>();
            _authority = FindFirstObjectByType<LocalCombatAuthority>();
            _enemy = FindFirstObjectByType<EnemyAgent>();
            Check(_p1 != null && _director != null && _authority != null && _enemy != null, "scene has a rig, a run director, the combat authority and an enemy");
            if (_p1 == null || _director == null || _authority == null || _enemy == null) yield break;

            foreach (EnemyAgent a in FindObjectsByType<EnemyAgent>(FindObjectsSortMode.None)) a.enabled = false;
            _p1.Movement.enabled = false;

            CharacterClassData archerClass = Load<CharacterClassData>("Class_Archer.asset");
            _cry = Load<SkillData>("Skill_WarriorBattleCry.asset");
            _multi = Load<SkillData>("Skill_ArcherMultiShot.asset");
            _pierce = Load<SkillData>("Skill_ArcherPiercingShot.asset");
            if (archerClass == null || _cry == null || _multi == null || _pierce == null) yield break;

            _p2 = _director.SpawnPlayer(Spot + new Vector3(0f, 0f, 30f), Quaternion.identity, 1, archerClass, out string spawnReason);
            Check(_p2 != null, "a rival archer joins the run (" + spawnReason + ")");
            if (_p2 == null) yield break;
            _p2.Movement.enabled = false;
            yield return null;

            _authority.SetClock(null);
            _authority.AutoStepSwings = true;
            _s1 = CombatSenderId.ForPlayer(_p1.PlayerId);
            _s2 = CombatSenderId.ForPlayer(_p2.PlayerId);

            yield return Identity();
            yield return AttackCommands();
            yield return SwingCommands();
            yield return ProjectileCommands();
            yield return SkillCommands();
            yield return SkillProjectileCommands();
            yield return EnemyCommands();
        }

        // ---- 1. who owns what -----------------------------------------------------------------------------

        private IEnumerator Identity()
        {
            Check(_director.TryGetSession(_p1.PlayerId, out PlayerSession ses1) && ses1.Rig == _p1 && _s1.IsPlayer && _s1.Value == ses1.PlayerId,
                "P1's sender id is the player id its session is keyed by");
            Check(_director.TryGetSession(_p2.PlayerId, out PlayerSession ses2) && ses2.Rig == _p2 && _s2.IsPlayer && _s2.Value == ses2.PlayerId && _s1 != _s2,
                "P2 has its own sender id");
            Check(CombatSenders.TryGetOwner(_p1.CombatId, out CombatSenderId o1) && o1 == _s1, "the run director bound P1's combat id to P1's sender");
            Check(CombatSenders.TryGetOwner(_p2.CombatId, out CombatSenderId o2) && o2 == _s2, "and P2's combat id to P2's sender");
            Check(_p1.CombatSender == _s1 && _p2.CombatSender == _s2, "each rig sends as its own bound sender");
            Check(!CombatSenders.TryGetOwner(_enemy.CombatId, out _), "an enemy has no owning sender");
            Check(CombatSenders.Owns(_s1, _p1.CombatId) && !CombatSenders.Owns(_s2, _p1.CombatId) && !CombatSenders.Owns(CombatSenderId.TrustedHost, _p1.CombatId),
                "only P1's sender owns P1");

            string r;
            Check(CombatSenders.Authorize(_s1, _p1.CombatId, out r) && CombatSenders.Authorize(_s2, _p2.CombatId, out r), "a player sender is authorized for its own entity");
            Check(!CombatSenders.Authorize(_s2, _p1.CombatId, out r) && Has(r, "does not own") && !CombatSenders.Authorize(_s1, _p2.CombatId, out r),
                "and for nobody else's player entity");
            Check(!CombatSenders.Authorize(CombatSenderId.TrustedHost, _p1.CombatId, out r) && !CombatSenders.Authorize(CombatSenderId.None, _p1.CombatId, out r) && Has(r, "no sender"),
                "the trusted host cannot impersonate a player and a sender-less command has no authority");
            Check(CombatSenders.Authorize(CombatSenderId.TrustedHost, _enemy.CombatId, out r), "the trusted host acts for an unowned enemy");
            Check(!CombatSenders.Authorize(_s1, _enemy.CombatId, out r) && Has(r, "does not own"), "a player cannot act for an enemy");
            Check(!CombatSenders.Authorize(_s1, 987654, out r), "nor for an unknown entity");

            int bound = CombatSenders.BoundCount;
            Check(!CombatSenders.Bind(_s2, _p1.CombatId, out r) && CombatSenders.TryGetOwner(_p1.CombatId, out CombatSenderId still) && still == _s1,
                "a second sender cannot take over P1's entity");
            Check(!CombatSenders.Bind(CombatSenderId.TrustedHost, _p1.CombatId, out r) && !CombatSenders.Bind(CombatSenderId.None, _p1.CombatId, out r),
                "neither can the host nor an empty sender");
            Check(!CombatSenders.Bind(_s1, _enemy.CombatId, out r) && !CombatSenders.Bind(_s1, 987654, out r), "a player sender cannot be bound to an enemy or an unknown id");
            Check(CombatSenders.Bind(_s1, _p1.CombatId, out r) && CombatSenders.BoundCount == bound, "rebinding the same pair is a no-op");
            Check(!_director.TryJoin(_p2, 1, false, out string joinReason) && CombatSenders.TryGetOwner(_p2.CombatId, out CombatSenderId again) && again == _s2,
                "a second join of an admitted rig is refused and the binding is untouched (" + joinReason + ")");
            yield return null;
        }

        // ---- 2. SubmitAttack ------------------------------------------------------------------------------

        private IEnumerator AttackCommands()
        {
            Refresh();
            _authority.AllowLegacyMeleeAttacks = true;
            Place(_p1, Spot);
            Vector3 fwd = Flat(_p1.AimDirection);
            Place(_p2, Spot + fwd * 1.4f);
            yield return null;

            WeaponData sword = _p1.ActiveWeapon;
            Check(sword != null && !sword.IsRanged, "P1 holds a melee weapon");
            if (sword == null) yield break;

            int seq = CombatRegistry.NextSequence(_p1.CombatId);
            AttackIntent attack = new AttackIntent(_p1.CombatId, _p1.Kind, sword, 1f, _p1.CombatCenter, fwd,
                sword.reach + sword.hitSphereRadius, seq, Time.time);
            HitCandidate hit = new HitCandidate(_p2.CombatId, _p2.CombatCenter, false);
            float hp = _p2.CurrentHealth;

            Forged("attack for P1 sent by P2", _p1.CombatId, (out string r) => _authority.SubmitAttack(_s2, attack, hit, out r));
            Forged("attack for P1 sent by the host", _p1.CombatId, (out string r) => _authority.SubmitAttack(CombatSenderId.TrustedHost, attack, hit, out r));
            Forged("attack for P1 with no sender", _p1.CombatId, (out string r) => _authority.SubmitAttack(CombatSenderId.None, attack, hit, out r));
            Check(Mathf.Approximately(_p2.CurrentHealth, hp), "no forged attack damaged the target");

            bool ok = _authority.SubmitAttack(_s1, attack, hit, out string reason);
            Check(ok, "the identical attack from its owner is accepted (nothing was burned by the forgeries): " + reason);
            Check(_p2.CurrentHealth < hp, "the owner's attack damaged the target (" + hp + " -> " + _p2.CurrentHealth + ")");
            Check(_authority.LastSequenceFor(_p1.CombatId) == seq && _authority.LastCommandSender == _s1, "the authority consumed exactly that sequence for P1");

            float hpAfter = _p2.CurrentHealth;
            State before = Snap(_p1.CombatId);
            ok = _authority.SubmitAttack(_s1, attack, hit, out reason);
            Check(!ok && Has(reason, "sequence") && Unchanged(before, Snap(_p1.CombatId)) && Mathf.Approximately(_p2.CurrentHealth, hpAfter),
                "a replay by the owner is refused as a stale sequence and changes nothing (" + reason + ")");
            Forged("replayed attack sent by P2", _p1.CombatId, (out string r) => _authority.SubmitAttack(_s2, attack, hit, out r));
            Check(Mathf.Approximately(_p2.CurrentHealth, hpAfter), "the forged replay did no damage either");

            // the other direction: P1 cannot act for P2
            WeaponData bow = _p2.ActiveWeapon;
            Check(bow != null && bow.IsRanged, "P2 holds a ranged weapon");
            AttackIntent attack2 = new AttackIntent(_p2.CombatId, _p2.Kind, bow, 1f, _p2.CombatCenter, -fwd, bow.rangedRange,
                CombatRegistry.NextSequence(_p2.CombatId), Time.time);
            HitCandidate hit2 = new HitCandidate(_p1.CombatId, _p1.CombatCenter, false);
            float p1Hp = _p1.CurrentHealth;
            Forged("attack for P2 sent by P1", _p2.CombatId, (out string r) => _authority.SubmitAttack(_s1, attack2, hit2, out r));
            Check(Mathf.Approximately(_p1.CurrentHealth, p1Hp), "P1 was not damaged by the forged P2 attack");

            _authority.AllowLegacyMeleeAttacks = false;
            yield return new WaitForSeconds(1.6f);
        }

        // ---- 3. TryBeginMeleeSwing ------------------------------------------------------------------------

        private IEnumerator SwingCommands()
        {
            Refresh();
            Vector3 fwd = Flat(_p1.AimDirection);
            Place(_p1, Spot);
            Place(_p2, Spot + fwd * 1.4f);
            yield return null;

            WeaponData sword = _p1.ActiveWeapon;
            int pattern = sword.HasComboChain ? sword.comboSteps[0].patternIndex : 0;
            Check(_p1.TryGetAimPose(out _, out float yaw, out float pitch), "P1 has an aim pose");
            MeleeSwingIntent swing = new MeleeSwingIntent(_p1.CombatId, sword.weaponId, pattern, 0, yaw, pitch, CombatRegistry.NextSequence(_p1.CombatId));
            float hp = _p2.CurrentHealth;
            int forgedId = -1;

            Forged("swing for P1 sent by P2", _p1.CombatId, (out string r) => { bool b = _authority.TryBeginMeleeSwing(_s2, swing, out forgedId, out r); return b; });
            Check(forgedId == 0, "a forged swing gets no swing id");
            Forged("swing for P1 sent by the host", _p1.CombatId, (out string r) => _authority.TryBeginMeleeSwing(CombatSenderId.TrustedHost, swing, out forgedId, out r));
            Forged("swing for P1 with no sender", _p1.CombatId, (out string r) => _authority.TryBeginMeleeSwing(CombatSenderId.None, swing, out forgedId, out r));

            // the other direction (the bow has no swing; the sender rule must stop it before any weapon rule does)
            WeaponData bow = _p2.ActiveWeapon;
            MeleeSwingIntent swing2 = new MeleeSwingIntent(_p2.CombatId, bow.weaponId, 0, 0, 0f, 0f, CombatRegistry.NextSequence(_p2.CombatId));
            Forged("swing for P2 sent by P1", _p2.CombatId, (out string r) => _authority.TryBeginMeleeSwing(_s1, swing2, out forgedId, out r));

            yield return null;
            Check(Mathf.Approximately(_p2.CurrentHealth, hp) && _authority.ActiveSwingCount == 0, "no forged swing exists or hurt anyone");

            int swingsBefore = _authority.SwingsStarted;
            bool ok = _authority.TryBeginMeleeSwing(_s1, swing, out int swingId, out string reason);
            Check(ok && swingId > 0 && _authority.SwingsStarted == swingsBefore + 1,
                "the identical swing from its owner begins (the forgeries burned no sequence, stamp or swing slot): " + reason);

            State before = Snap(_p1.CombatId);
            ok = _authority.TryBeginMeleeSwing(_s1, swing, out int replayId, out reason);
            Check(!ok && replayId == 0 && Has(reason, "sequence") && Unchanged(before, Snap(_p1.CombatId)), "a replay by the owner is refused as a stale sequence (" + reason + ")");
            Forged("replayed swing sent by P2", _p1.CombatId, (out string r) => _authority.TryBeginMeleeSwing(_s2, swing, out forgedId, out r));

            float guard = 0f;
            while (_authority.ActiveSwingCount > 0 && guard < 4f) { guard += Time.deltaTime; yield return null; }
            yield return new WaitForSeconds(0.2f);
            Check(_p2.CurrentHealth < hp, "only the owner's swing damaged the target (" + hp + " -> " + _p2.CurrentHealth + ")");

            // the real component sends as its rig
            yield return new WaitForSeconds(1.6f);
            Refresh();
            MeleeCombat melee = _p1.GetComponent<MeleeCombat>();
            Check(melee != null && melee.enabled, "P1 has the real melee component");
            _authority.ClearLastCommandSender();
            int started = _authority.SwingsStarted;
            Check(melee.TryBeginSwing(0) && _authority.SwingsStarted == started + 1, "MeleeCombat still begins a real swing");
            Check(_authority.LastCommandSender == _s1, "and it was sent through P1's sender (" + _authority.LastCommandSender + ")");
            guard = 0f;
            while (melee.IsSwinging && guard < 3f) { guard += Time.deltaTime; yield return null; }
            yield return new WaitForSeconds(1.6f);
        }

        // ---- 4. LaunchProjectile --------------------------------------------------------------------------

        private IEnumerator ProjectileCommands()
        {
            Refresh();
            Vector3 fwd = Flat(_p1.AimDirection);
            Place(_p1, Spot);
            Place(_p2, Spot + fwd * 10f);
            yield return null;

            WeaponData bow = _p2.ActiveWeapon;
            RangedCombat bowCombat = RangedOf(_p2);
            Check(bow != null && bow.IsRanged && bowCombat != null && bowCombat.HasAmmo(3), "P2 holds a loaded bow");
            if (bow == null || bowCombat == null) yield break;

            AttackIntent shot = new AttackIntent(_p2.CombatId, _p2.Kind, bow, 1f, _p2.GetMuzzlePosition(), _p2.AimDirection, bow.rangedRange,
                CombatRegistry.NextSequence(_p2.CombatId), Time.time);

            Forged("arrow for P2 sent by P1", _p2.CombatId, (out string r) => _authority.LaunchProjectile(_s1, shot, out r));
            Forged("arrow for P2 sent by the host", _p2.CombatId, (out string r) => _authority.LaunchProjectile(CombatSenderId.TrustedHost, shot, out r));
            Forged("arrow for P2 with no sender", _p2.CombatId, (out string r) => _authority.LaunchProjectile(CombatSenderId.None, shot, out r));

            int magazine = bowCombat.Magazine;
            int accepted = _authority.AcceptedCount;
            bool ok = _authority.LaunchProjectile(_s2, shot, out string reason);
            Check(ok && bowCombat.Magazine == magazine - 1 && _authority.AcceptedCount == accepted + 1,
                "the identical arrow from its owner flies and spends one round (" + reason + ")");

            State before = Snap(_p2.CombatId);
            ok = _authority.LaunchProjectile(_s2, shot, out reason);
            Check(!ok && Unchanged(before, Snap(_p2.CombatId)), "a replay by the owner launches nothing and spends nothing (" + reason + ")");
            Forged("replayed arrow sent by P1", _p2.CombatId, (out string r) => _authority.LaunchProjectile(_s1, shot, out r));

            // the other direction, with P1's crossbow, then the real component
            yield return new WaitForSeconds(1.6f);
            Refresh();
            Check(_p1.Loadout.SwitchToSlot(1) && _p1.ActiveWeapon != null && _p1.ActiveWeapon.IsRanged, "P1 takes the crossbow");
            yield return null;
            WeaponData crossbow = _p1.ActiveWeapon;
            RangedCombat crossbowCombat = RangedOf(_p1);
            Check(crossbowCombat != null && crossbowCombat.HasAmmo(1), "P1's crossbow is loaded");
            if (crossbow != null && crossbowCombat != null)
            {
                AttackIntent bolt = new AttackIntent(_p1.CombatId, _p1.Kind, crossbow, 1f, _p1.GetMuzzlePosition(), _p1.AimDirection, crossbow.rangedRange,
                    CombatRegistry.NextSequence(_p1.CombatId), Time.time);
                Forged("bolt for P1 sent by P2", _p1.CombatId, (out string r) => _authority.LaunchProjectile(_s2, bolt, out r));

                _authority.ClearLastCommandSender();
                accepted = _authority.AcceptedCount;
                crossbowCombat.Fire(1f);
                Check(_authority.AcceptedCount == accepted + 1 && _authority.LastCommandSender == _s1, "RangedCombat.Fire launches through P1's sender (" + _authority.LastCommandSender + ")");
            }
            _p1.Loadout.SwitchToSlot(0);
            yield return new WaitForSeconds(1.6f);
        }

        // ---- 5. skill cast, complete, hit -----------------------------------------------------------------

        private IEnumerator SkillCommands()
        {
            Refresh();
            Vector3 fwd = Flat(_p1.AimDirection);
            Place(_p1, Spot);
            Place(_p2, Spot + fwd * 1.5f);
            yield return null;

            Check(_p1.ActiveWeapon != null && !_p1.ActiveWeapon.IsRanged, "P1 holds the sword for Battle Cry");
            int cost = _cry.EffectiveManaCost;
            float mana0 = _p1.Mana.CurrentMana;
            Check(mana0 >= cost, "P1 can pay for Battle Cry");

            int forgedAct = -1;
            Forged("cast for P1 sent by P2", _p1.CombatId, (out string r) => { bool b = _authority.TryBeginSkillCast(_s2, _p1.CombatId, _cry.skillId, out forgedAct, out r); return b; });
            Check(forgedAct == 0 && Mathf.Abs(_p1.Mana.CurrentMana - mana0) < 0.01f, "the forged cast opened no activation and spent no mana");
            Forged("cast for P1 sent by the host", _p1.CombatId, (out string r) => _authority.TryBeginSkillCast(CombatSenderId.TrustedHost, _p1.CombatId, _cry.skillId, out forgedAct, out r));
            Forged("cast for P1 with no sender", _p1.CombatId, (out string r) => _authority.TryBeginSkillCast(CombatSenderId.None, _p1.CombatId, _cry.skillId, out forgedAct, out r));

            int open = _authority.OpenActivationCount;
            bool ok = _authority.TryBeginSkillCast(_s1, _p1.CombatId, _cry.skillId, out int act, out string reason);
            Check(ok && act > 0 && _authority.OpenActivationCount == open + 1,
                "the same cast from its owner opens (the forgeries stamped no cooldown and spent nothing): " + reason);
            Check(Mathf.Abs(_p1.Mana.CurrentMana - (mana0 - cost)) < 0.5f, "and spends the mana exactly once (" + mana0 + " -> " + _p1.Mana.CurrentMana + ")");

            // another player cannot finish, hit with, or borrow P1's activation
            Forged("complete of P1's cast sent by P2", _p1.CombatId, (out string r) => { bool b = _authority.CompleteSkillCast(_s2, _p1.CombatId, act); r = _authority.LastSkillRejectReason; return b; });
            Forged("complete of P1's cast sent by the host", _p1.CombatId, (out string r) => { bool b = _authority.CompleteSkillCast(CombatSenderId.TrustedHost, _p1.CombatId, act); r = _authority.LastSkillRejectReason; return b; });

            SkillIntent stolen = new SkillIntent(_p2.CombatId, _p2.Kind, _cry, _p2.ActiveWeapon, _p2.CombatCenter, -fwd, _cry.radius,
                CombatRegistry.NextSequence(_p2.CombatId), Time.time, default, act);
            float p1Hp = _p1.CurrentHealth;
            ok = _authority.SubmitSkill(_s2, stolen, new HitCandidate(_p1.CombatId, _p1.CombatCenter, false), out reason);
            Check(!ok && Has(reason, "activation") && Mathf.Approximately(_p1.CurrentHealth, p1Hp), "P2, as itself, cannot ride on P1's activation id (" + reason + ")");
            State openState = Snap(_p2.CombatId);
            ok = _authority.CompleteSkillCast(_s2, _p2.CombatId, act);
            Check(!ok && Snap(_p2.CombatId).Open == openState.Open, "nor close it under its own id");

            Vector3 outward = Flat(_p2.CombatCenter - _p1.CombatCenter);
            SkillIntent cry = new SkillIntent(_p1.CombatId, _p1.Kind, _cry, _p1.ActiveWeapon, _p1.CombatCenter, outward, _cry.radius,
                CombatRegistry.NextSequence(_p1.CombatId), Time.time, default, act);
            HitCandidate cryHit = new HitCandidate(_p2.CombatId, _p2.CombatCenter, false);
            float hp = _p2.CurrentHealth;

            Forged("skill hit for P1 sent by P2", _p1.CombatId, (out string r) => _authority.SubmitSkill(_s2, cry, cryHit, out r));
            Forged("skill hit for P1 sent by the host", _p1.CombatId, (out string r) => _authority.SubmitSkill(CombatSenderId.TrustedHost, cry, cryHit, out r));
            Forged("skill hit for P1 with no sender", _p1.CombatId, (out string r) => _authority.SubmitSkill(CombatSenderId.None, cry, cryHit, out r));
            Check(Mathf.Approximately(_p2.CurrentHealth, hp), "no forged skill hit damaged the target");

            ok = _authority.SubmitSkill(_s1, cry, cryHit, out reason);
            Check(ok && _p2.CurrentHealth < hp, "the identical skill hit from its owner lands (" + hp + " -> " + _p2.CurrentHealth + "): " + reason);

            hp = _p2.CurrentHealth;
            State before = Snap(_p1.CombatId);
            ok = _authority.SubmitSkill(_s1, cry, cryHit, out reason);
            Check(!ok && Unchanged(before, Snap(_p1.CombatId)) && Mathf.Approximately(_p2.CurrentHealth, hp), "a replayed hit by the owner is refused (" + reason + ")");
            Forged("replayed skill hit sent by P2", _p1.CombatId, (out string r) => _authority.SubmitSkill(_s2, cry, cryHit, out r));

            ok = _authority.CompleteSkillCast(_s1, _p1.CombatId, act);
            Check(ok, "the owner completes its own cast");
            Check(!_authority.CompleteSkillCast(_s1, _p1.CombatId, act), "a second completion is refused");
            Forged("repeat complete sent by P2", _p1.CombatId, (out string r) => { bool b = _authority.CompleteSkillCast(_s2, _p1.CombatId, act); r = _authority.LastSkillRejectReason; return b; });
            yield return null;
        }

        // ---- 6. projectile skills and the real SkillCaster -------------------------------------------------

        private IEnumerator SkillProjectileCommands()
        {
            Refresh();
            Vector3 fwd = Flat(_p1.AimDirection);
            Place(_p1, Spot);
            Place(_p2, Spot + fwd * 12f);
            yield return null;

            WeaponData bow = _p2.ActiveWeapon;
            RangedCombat bowCombat = RangedOf(_p2);
            Check(bow != null && bow.IsRanged && bowCombat != null && bowCombat.HasAmmo(_pierce.AmmoPerCast), "P2 can pay for Piercing Shot");
            if (bow == null || bowCombat == null) yield break;

            int magazine = bowCombat.Magazine;
            float mana = _p2.Mana.CurrentMana;
            int forgedAct = -1;
            Forged("Piercing Shot cast for P2 sent by P1", _p2.CombatId, (out string r) => { bool b = _authority.TryBeginSkillCast(_s1, _p2.CombatId, _pierce.skillId, out forgedAct, out r); return b; });
            Check(forgedAct == 0 && bowCombat.Magazine == magazine && Mathf.Abs(_p2.Mana.CurrentMana - mana) < 0.01f, "the forged cast spent no ammo and no mana");

            bool ok = _authority.TryBeginSkillCast(_s2, _p2.CombatId, _pierce.skillId, out int act, out string reason);
            Check(ok && act > 0 && bowCombat.Magazine == magazine - _pierce.AmmoPerCast, "the owner opens the same cast and pays the round once: " + reason);

            SkillIntent launch = new SkillIntent(_p2.CombatId, _p2.Kind, _pierce, bow, _p2.GetMuzzlePosition(), _p2.AimDirection, bow.rangedRange,
                CombatRegistry.NextSequence(_p2.CombatId), Time.time, default, act);
            Forged("skill arrow for P2 sent by P1", _p2.CombatId, (out string r) => _authority.LaunchSkillProjectile(_s1, launch, 0, out r));
            Forged("skill arrow for P2 sent by the host", _p2.CombatId, (out string r) => _authority.LaunchSkillProjectile(CombatSenderId.TrustedHost, launch, 0, out r));
            Forged("complete of P2's cast sent by P1", _p2.CombatId, (out string r) => { bool b = _authority.CompleteSkillCast(_s1, _p2.CombatId, act); r = _authority.LastSkillRejectReason; return b; });

            ok = _authority.LaunchSkillProjectile(_s2, launch, 0, out reason);
            Check(ok, "the identical skill arrow from its owner flies: " + reason);
            State before = Snap(_p2.CombatId);
            ok = _authority.LaunchSkillProjectile(_s2, launch, 0, out reason);
            Check(!ok && Snap(_p2.CombatId).Accepted == before.Accepted, "a replayed skill arrow is refused (" + reason + ")");
            Forged("replayed skill arrow sent by P1", _p2.CombatId, (out string r) => _authority.LaunchSkillProjectile(_s1, launch, 0, out r));
            Check(_authority.CompleteSkillCast(_s2, _p2.CombatId, act), "the owner completes the cast");

            // R1c3: the sender-less overload no longer serves a player-owned id (it stays the trusted path for unowned ids only)
            yield return new WaitForSeconds(1.0f);
            Refresh();
            int senderlessRefusals = _authority.SenderRefusalCount;
            bool legacy = _authority.TryBeginSkillCast(_p2.CombatId, _multi.skillId, out int legacyAct, out reason);
            Check(!legacy && legacyAct == 0 && reason.Contains("no sender") && _authority.SenderRefusalCount == senderlessRefusals + 1,
                "the sender-less overload is refused for the rig's own id (R1c3; was: kept working): " + reason);

            // the real SkillCaster sends as its rig (Piercing Shot's stamp is spent, Multi-shot's local cooldown is separate)
            yield return new WaitForSeconds(Mathf.Max(_multi.EffectiveCooldown, _pierce.EffectiveCooldown) + 0.5f);
            Refresh();
            _authority.ClearLastCommandSender();
            int accepted = _authority.AcceptedCount;
            Check(_p2.Skills.TryCast(_multi.skillId), "the real SkillCaster casts Multi-shot for P2");
            Check(_authority.LastCommandSender == _s2 && _authority.AcceptedCount > accepted, "and every command of that cast was sent as P2 (" + _authority.LastCommandSender + ")");
            yield return new WaitForSeconds(1.5f);
        }

        // ---- 7. enemies keep working -----------------------------------------------------------------------

        private IEnumerator EnemyCommands()
        {
            Refresh();
            WeaponData claw = _enemy.ActiveWeapon;
            Check(claw != null && claw.UsesSweptMelee, "the enemy holds a swept melee weapon");
            if (claw == null) yield break;

            _enemy.TryGetAimPose(out _, out float yaw, out float pitch);
            MeleeSwingIntent forged = new MeleeSwingIntent(_enemy.CombatId, claw.weaponId, 0, 0, yaw, pitch, CombatRegistry.NextSequence(_enemy.CombatId));
            int forgedId = -1;
            Forged("swing for an enemy sent by P1", _enemy.CombatId, (out string r) => _authority.TryBeginMeleeSwing(_s1, forged, out forgedId, out r));
            Forged("swing for an enemy sent by P2", _enemy.CombatId, (out string r) => _authority.TryBeginMeleeSwing(_s2, forged, out forgedId, out r));

            int started = _authority.SwingsStarted;
            bool begun = _enemy.TryBeginAttack(Vector3.forward, out string reason);
            Check(begun && _authority.SwingsStarted == started + 1, "the enemy's own attack path (sender-less, unowned entity) still begins a swing: " + reason);

            float guard = 0f;
            while (_authority.ActiveSwingCount > 0 && guard < 5f) { guard += Time.deltaTime; yield return null; }
            guard = 0f;
            while (_authority.WeaponCooldownRemaining(_enemy.CombatId, claw) > 0f && guard < 6f) { guard += Time.deltaTime; yield return null; }

            _enemy.TryGetAimPose(out _, out yaw, out pitch);
            MeleeSwingIntent hostSwing = new MeleeSwingIntent(_enemy.CombatId, claw.weaponId, 0, 0, yaw, pitch, CombatRegistry.NextSequence(_enemy.CombatId));
            started = _authority.SwingsStarted;
            bool ok = _authority.TryBeginMeleeSwing(CombatSenderId.TrustedHost, hostSwing, out int swingId, out reason);
            Check(ok && swingId > 0 && _authority.SwingsStarted == started + 1, "the trusted host can command the unowned enemy explicitly: " + reason);

            guard = 0f;
            while (_authority.ActiveSwingCount > 0 && guard < 5f) { guard += Time.deltaTime; yield return null; }
            Check(_p1.CurrentHealth > 0f && _p2.CurrentHealth > 0f && _director.State == RunState.Running, "both players and the run are intact at the end");
        }
    }
}
