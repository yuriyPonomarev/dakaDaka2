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
    /// R1c3 gate: movement abilities are validated by the authority, casts live on the authority's clock, and the sender-less
    /// path is closed for player-owned ids. Real Level01 run, P1 warrior and P2 archer.
    /// Proves: an honest dash (the real PlayerMovement dash, and reported ones) is untouched; a forged dash result (too far,
    /// through a wall, too early, not true at all) is rolled back to the furthest honest point and recorded; a replayed or
    /// other-player report changes nothing; a dash nobody reports is checked by the authority itself; a cast nobody completes
    /// is completed once by the authority (self buff applied once) and a prompt completion is not doubled; sender-less
    /// commands for player-owned ids are refused with state unchanged while enemies (unowned) keep working.
    /// Entry: <c>Emberdelve.EditorTools.EmberdelveMovementLifecycleGate.Run</c>.
    /// </summary>
    [InitializeOnLoad]
    public static class EmberdelveMovementLifecycleGate
    {
        private const string Pending = "Emberdelve.MovementLifecycle.Pending";

        static EmberdelveMovementLifecycleGate()
        {
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.EnteredPlayMode && SessionState.GetBool(Pending, false))
                {
                    SessionState.SetBool(Pending, false);
                    new GameObject("MovementLifecycle_Gate").AddComponent<MovementLifecycleHarness>();
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

    public sealed class MovementLifecycleHarness : MonoBehaviour
    {
        private static readonly Vector3 Spot = new Vector3(3000f, 0f, 3800f);

        private int _n, _fail;
        private float _clock;

        private RunDirector _director;
        private LocalCombatAuthority _authority;
        private PlayerRig _p1, _p2;
        private EnemyAgent _enemy;
        private CombatSenderId _s1, _s2;
        private SkillData _dash, _cry;
        private GameObject _wall;

        private void Check(bool ok, string text)
        {
            _n++;
            if (!ok)
            {
                _fail++;
                Debug.LogError("[MovementLifecycle] mismatch " + text);
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

            if (_wall != null) Destroy(_wall);
            if (_authority != null) _authority.SetClock(null);
            Debug.Log("[Emberdelve][MovementLifecycle] " + (_fail == 0 ? "PASS" : "FAIL") + " checks=" + _n + " failures=" + _fail);
            EditorApplication.Exit(_fail == 0 ? 0 : 1);
        }

        // ---- helpers -----------------------------------------------------------------------------------------

        private void Advance(float seconds)
        {
            _clock += seconds;
            _authority.AdvanceKitsTo(_clock);
        }

        private static void Teleport(PlayerRig rig, Vector3 position)
        {
            CharacterController controller = rig.GetComponent<CharacterController>();
            if (controller != null) controller.enabled = false;
            rig.transform.SetPositionAndRotation(position, Quaternion.identity);
            if (controller != null) controller.enabled = true;
            Physics.SyncTransforms();
        }

        /// <summary>The dash length the rig's own effect scale gives, with PlayerMovement's clamp.</summary>
        private float Meters() => Mathf.Clamp(_dash.dashDistance * DamageResolver.ClampEffectScale(_p1.AbilityEffectScale), 0.5f, 20f);

        private bool OpenDash(out int activation, out Vector3 start)
        {
            Advance(_dash.EffectiveCooldown + 1f);
            EmberdelveKitFixtures.RefillMana(_p1);
            Teleport(_p1, Spot + new Vector3(0f, 0.15f, 0f));
            start = _p1.transform.position;
            bool ok = _authority.TryBeginSkillCast(_s1, _p1.CombatId, _dash.skillId, out activation, out string reason);
            Check(ok && activation > 0, "a dash cast opens (" + reason + ")");
            return ok;
        }

        private float FlatFrom(Vector3 start, Vector3 p) => new Vector3(p.x - start.x, 0f, p.z - start.z).magnitude;

        private bool Near(Vector3 a, Vector3 b, float eps = 0.06f) => (a - b).magnitude <= eps;

        private DashRollback LastRollback()
        {
            var list = _authority.DashRollbacks;
            return list.Count > 0 ? list[list.Count - 1] : default;
        }

        private void AddWall(Vector3 start)
        {
            _wall = new GameObject("MovementLifecycle_Wall");
            _wall.transform.position = start + new Vector3(0f, 1f, 2.5f);
            BoxCollider box = _wall.AddComponent<BoxCollider>();
            box.size = new Vector3(8f, 4f, 0.4f);
            int level = LayerMask.NameToLayer(GameConfig.LevelLayerName);
            Check(level >= 0, "the Emv_Level layer exists for the wall");
            if (level >= 0) _wall.layer = level;
            Physics.SyncTransforms();
        }

        private void RemoveWall()
        {
            if (_wall != null) Destroy(_wall);
            _wall = null;
            Physics.SyncTransforms();
        }

        // ---- the gate ----------------------------------------------------------------------------------------

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

            CharacterClassData archerClass = AssetDatabase.LoadAssetAtPath<CharacterClassData>(EmberdelvePaths.Data + "/Class_Archer.asset");
            _dash = AssetDatabase.LoadAssetAtPath<SkillData>(EmberdelvePaths.Data + "/Skill_WarriorDash.asset");
            _cry = AssetDatabase.LoadAssetAtPath<SkillData>(EmberdelvePaths.Data + "/Skill_WarriorBattleCry.asset");
            Check(archerClass != null && _dash != null && _cry != null, "class and skill assets available");
            if (archerClass == null || _dash == null || _cry == null) yield break;

            _p2 = _director.SpawnPlayer(Spot + new Vector3(0f, 0f, 30f), Quaternion.identity, 1, archerClass, out string spawnReason);
            Check(_p2 != null, "a rival archer joins the run (" + spawnReason + ")");
            if (_p2 == null) yield break;
            _p2.Movement.enabled = false;
            yield return null;

            _s1 = _p1.CombatSender;
            _s2 = _p2.CombatSender;
            Check(_s1.IsPlayer && _s2.IsPlayer && _s1 != _s2, "both players are owned by distinct session senders");

            yield return RealDash();

            _clock = Time.time;
            _authority.SetClock(() => _clock);
            _authority.AdvanceKitsTo(_clock);

            yield return ForgedDashes();
            yield return HonestReports();
            yield return ReplayAndForeignReport();
            yield return UnreportedDash();
            yield return CastLifetime();
            yield return SenderlessPath();
        }

        // ---- 1. the real dash through the real movement component ---------------------------------------------

        private IEnumerator RealDash()
        {
            _authority.SetClock(null);
            EmberdelveKitFixtures.RefillMana(_p1);
            Teleport(_p1, Spot + new Vector3(0f, 0.15f, 0f));
            _p1.Movement.enabled = true;
            yield return null;

            int accepted = _authority.DashAcceptedCount;
            int rollbacks = _authority.DashRollbackCount;
            Vector3 start = _p1.transform.position;
            bool cast = _p1.Skills.TryCast(_dash.skillId);
            Check(cast, "the real SkillCaster casts Dash");
            if (cast)
            {
                float guard = 0f;
                while ((_p1.Movement.IsDashing || _authority.DashAcceptedCount == accepted) && guard < 2f && _authority.DashRollbackCount == rollbacks)
                {
                    guard += Time.deltaTime;
                    yield return null;
                }

                Check(_authority.DashAcceptedCount == accepted + 1 && _authority.DashRollbackCount == rollbacks,
                    "the honest dash end was reported and accepted by the authority, no rollback (" + _authority.LastDashRollbackReason + ")");
                Check(FlatFrom(start, _p1.transform.position) > 0.5f, "and the character kept the ground it covered (" + FlatFrom(start, _p1.transform.position).ToString("F2") + " m)");
            }

            _p1.Movement.CancelDash();
            _p1.Movement.enabled = false;
            yield return null;
        }

        // ---- 2. dishonest results ------------------------------------------------------------------------------

        private IEnumerator ForgedDashes()
        {
            Vector3 fwd = Vector3.forward;
            float meters = Meters();

            // too far: the client claims 15 m
            Check(OpenDash(out int act, out Vector3 start), "dash 1 opens");
            Advance(0.3f);
            int rollbacks = _authority.DashRollbackCount;
            Vector3 claimed = start + fwd * 15f;
            Teleport(_p1, claimed);
            bool ok = _authority.ReportDashResult(_s1, _p1.CombatId, act, claimed, out string reason);
            DashRollback rb = LastRollback();
            Check(!ok && _authority.DashRollbackCount == rollbacks + 1 && Has(reason, "exceeds"), "a 15 m dash is refused and counted (" + reason + ")");
            Check(rb.CasterId == _p1.CombatId && rb.ActivationId == act && Near(rb.Claimed, claimed) && !rb.FromExpiry, "the event records caster, activation and the claim");
            Check(FlatFrom(start, _p1.transform.position) <= meters + 0.01f && Near(_p1.transform.position, rb.Accepted),
                "the rig was put back at the furthest honest point (" + FlatFrom(start, _p1.transform.position).ToString("F2") + " m of " + meters.ToString("F2") + ")");
            Check(Has(_authority.LastDashRollbackReason, "exceeds"), "LastDashRollbackReason is set");

            // through a wall: 4 m claimed (inside the distance budget), a wall 2.3 m away
            Check(OpenDash(out act, out start), "dash 2 opens");
            Advance(0.3f);
            AddWall(start);
            rollbacks = _authority.DashRollbackCount;
            claimed = start + fwd * Mathf.Min(4f, meters);
            Teleport(_p1, claimed);
            ok = _authority.ReportDashResult(_s1, _p1.CombatId, act, claimed, out reason);
            rb = LastRollback();
            Check(!ok && _authority.DashRollbackCount == rollbacks + 1 && Has(reason, "geometry"), "a dash that passes through level geometry is refused (" + reason + ")");
            Check(FlatFrom(start, _p1.transform.position) < 2.4f && Near(_p1.transform.position, rb.Accepted), "and the rig is back in front of the wall (" + FlatFrom(start, _p1.transform.position).ToString("F2") + " m)");

            // an honest dash stopped by that wall is untouched
            Check(OpenDash(out act, out start), "dash 3 opens");
            Advance(0.3f);
            rollbacks = _authority.DashRollbackCount;
            int accepted = _authority.DashAcceptedCount;
            claimed = start + fwd * 1.9f;
            Teleport(_p1, claimed);
            ok = _authority.ReportDashResult(_s1, _p1.CombatId, act, claimed, out reason);
            Check(ok && _authority.DashRollbackCount == rollbacks && _authority.DashAcceptedCount == accepted + 1 && Near(_p1.transform.position, claimed, 0.01f),
                "a dash that stopped at the wall is accepted and the rig stays where it is (" + reason + ")");
            RemoveWall();

            // too early: the full distance in zero time
            Check(OpenDash(out act, out start), "dash 4 opens");
            rollbacks = _authority.DashRollbackCount;
            claimed = start + fwd * Mathf.Min(4f, meters);
            Teleport(_p1, claimed);
            ok = _authority.ReportDashResult(_s1, _p1.CombatId, act, claimed, out reason);
            Check(!ok && _authority.DashRollbackCount == rollbacks + 1 && Has(reason, "too early"), "a dash reported the instant it was cast is refused (" + reason + ")");
            Check(FlatFrom(start, _p1.transform.position) < 0.05f, "and the rig is back where it started");

            // not a number / a climb
            Check(OpenDash(out act, out start), "dash 5 opens");
            Advance(0.3f);
            rollbacks = _authority.DashRollbackCount;
            claimed = start + fwd * 1f + Vector3.up * 40f;
            Teleport(_p1, claimed);
            ok = _authority.ReportDashResult(_s1, _p1.CombatId, act, claimed, out reason);
            Check(!ok && _authority.DashRollbackCount == rollbacks + 1 && Has(reason, "vertical") && Mathf.Abs(_p1.transform.position.y - start.y) < 0.05f,
                "a dash that climbs 40 m is refused and the height is restored (" + reason + ")");
            yield return null;
        }

        // ---- 3. honest reports -----------------------------------------------------------------------------------

        private IEnumerator HonestReports()
        {
            Check(OpenDash(out int act, out Vector3 start), "honest dash opens");
            Advance(0.3f);
            int rollbacks = _authority.DashRollbackCount;
            int accepted = _authority.DashAcceptedCount;
            Vector3 end = start + Vector3.forward * (Meters() * 0.95f);
            Teleport(_p1, end);
            bool ok = _authority.ReportDashResult(_s1, _p1.CombatId, act, end, out string reason);
            Check(ok && _authority.DashRollbackCount == rollbacks && _authority.DashAcceptedCount == accepted + 1 && Near(_p1.transform.position, end, 0.01f),
                "a dash of 95% of its length reported after its duration is accepted untouched (" + reason + ")");

            // a dash that barely moved (blocked at once) is honest too
            Check(OpenDash(out act, out start), "second honest dash opens");
            Advance(0.3f);
            ok = _authority.ReportDashResult(_s1, _p1.CombatId, act, start, out reason);
            Check(ok && _authority.DashRollbackCount == rollbacks, "a dash that did not move is accepted (" + reason + ")");
            yield return null;
        }

        // ---- 4. replay and other players ------------------------------------------------------------------------

        private IEnumerator ReplayAndForeignReport()
        {
            Check(OpenDash(out int act, out Vector3 start), "dash opens for the replay test");
            Advance(0.3f);

            int refusals = _authority.SenderRefusalCount;
            int rollbacks = _authority.DashRollbackCount;
            int accepted = _authority.DashAcceptedCount;
            Vector3 claimed = start + Vector3.forward * 12f;
            Teleport(_p1, claimed);
            bool ok = _authority.ReportDashResult(_s2, _p1.CombatId, act, claimed, out string reason);
            Check(!ok && Has(reason, "does not own") && _authority.SenderRefusalCount == refusals + 1 && _authority.DashRollbackCount == rollbacks
                  && Near(_p1.transform.position, claimed, 0.01f), "P2 cannot report (or roll back) P1's dash (" + reason + ")");
            ok = _authority.ReportDashResult(CombatSenderId.TrustedHost, _p1.CombatId, act, claimed, out reason);
            Check(!ok && _authority.SenderRefusalCount == refusals + 2 && _authority.DashRollbackCount == rollbacks, "neither can the trusted host or a missing sender (" + reason + ")");

            ok = _authority.ReportDashResult(_s1, _p1.CombatId, act, start + Vector3.forward * 0.5f, out reason);
            Check(ok && _authority.DashAcceptedCount == accepted + 1, "the owner's own (honest) report is then accepted");
            Teleport(_p1, claimed);
            ok = _authority.ReportDashResult(_s1, _p1.CombatId, act, claimed, out reason);
            Check(!ok && Has(reason, "already") && _authority.DashRollbackCount == rollbacks && _authority.DashAcceptedCount == accepted + 1,
                "a second report for the same dash is refused, not judged (" + reason + ")");

            ok = _authority.ReportDashResult(_s1, _p1.CombatId, 999999, claimed, out reason);
            Check(!ok && Has(reason, "no such"), "an invented activation id is refused (" + reason + ")");
            ok = _authority.ReportDashResult(_s2, _p2.CombatId, act, claimed, out reason);
            Check(!ok && Has(reason, "no such"), "and so is P1's activation id sent as P2's own dash (" + reason + ")");

            // a non-dash activation cannot be used to move
            Advance(_cry.EffectiveCooldown + 1f);
            EmberdelveKitFixtures.RefillMana(_p1);
            Check(_authority.TryBeginSkillCast(_s1, _p1.CombatId, _cry.skillId, out int cryAct, out reason), "Battle Cry opens");
            ok = _authority.ReportDashResult(_s1, _p1.CombatId, cryAct, claimed, out reason);
            Check(!ok && Has(reason, "not a tracked dash"), "a cast that is not a dash cannot report a move (" + reason + ")");
            _authority.CompleteSkillCast(_s1, _p1.CombatId, cryAct);
            yield return null;
        }

        // ---- 5. a dash nobody reports --------------------------------------------------------------------------

        private IEnumerator UnreportedDash()
        {
            Check(OpenDash(out int act, out Vector3 start), "dash opens (client never reports)");
            int rollbacks = _authority.DashRollbackCount;
            Vector3 claimed = start + Vector3.forward * 25f;
            Teleport(_p1, claimed);
            Advance(0.3f);
            Check(_authority.DashRollbackCount == rollbacks, "inside the grace the authority waits for the report");
            Advance(LocalCombatAuthority.DashReportGrace + 0.2f);
            DashRollback rb = LastRollback();
            Check(_authority.DashRollbackCount == rollbacks + 1 && rb.FromExpiry && rb.ActivationId == act && FlatFrom(start, _p1.transform.position) <= Meters() + 0.01f,
                "the authority checked the unreported dash itself and rolled the position back (" + FlatFrom(start, _p1.transform.position).ToString("F2") + " m)");
            bool ok = _authority.ReportDashResult(_s1, _p1.CombatId, act, claimed, out string reason);
            Check(!ok && Has(reason, "already"), "a late report after the authority's own check is ignored (" + reason + ")");

            Check(OpenDash(out act, out start), "second dash opens (client never reports, honest)");
            rollbacks = _authority.DashRollbackCount;
            Vector3 end = start + Vector3.forward * (Meters() * 0.9f);
            Teleport(_p1, end);
            Advance(0.3f + LocalCombatAuthority.DashReportGrace + 0.2f);
            Check(_authority.DashRollbackCount == rollbacks && Near(_p1.transform.position, end, 0.01f), "an honest unreported dash is left alone");
            yield return null;
        }

        // ---- 6. cast lifetime ---------------------------------------------------------------------------------------

        private IEnumerator CastLifetime()
        {
            StatusKind buff = _cry.SelfStatusSpec.Kind;
            Check(_cry.affectsSelf && buff != StatusKind.None, "Battle Cry has a self buff");

            Advance(_cry.EffectiveCooldown + 1f);
            EmberdelveKitFixtures.RefillMana(_p1);
            _p1.Status.ClearStatuses();
            int auto = _authority.ActivationsAutoCompleted;
            bool ok = _authority.TryBeginSkillCast(_s1, _p1.CombatId, _cry.skillId, out int act, out string reason);
            Check(ok && !_p1.Status.HasStatus(buff), "Battle Cry opens and its buff has not landed yet (" + reason + ")");

            Advance(1.0f);
            Check(_authority.ActivationsAutoCompleted == auto && !_p1.Status.HasStatus(buff), "inside the window the authority leaves the cast open");
            Advance(0.8f);
            Check(_authority.ActivationsAutoCompleted == auto + 1, "a cast the caller never completed is completed by the authority after its window");
            Check(_p1.Status.HasStatus(buff), "and the skill's own self buff was applied by it");
            Check(!_authority.CompleteSkillCast(_s1, _p1.CombatId, act), "the caller's late CompleteSkillCast finds it already closed");
            Advance(1.0f);
            Check(_authority.ActivationsAutoCompleted == auto + 1, "and it is not completed twice");

            // prompt completion keeps working and is not doubled by the expiry
            Advance(_cry.EffectiveCooldown + 1f);
            EmberdelveKitFixtures.RefillMana(_p1);
            _p1.Status.ClearStatuses();
            auto = _authority.ActivationsAutoCompleted;
            ok = _authority.TryBeginSkillCast(_s1, _p1.CombatId, _cry.skillId, out act, out reason);
            Check(ok && _authority.CompleteSkillCast(_s1, _p1.CombatId, act) && _p1.Status.HasStatus(buff), "a prompt completion still applies the buff (" + reason + ")");
            Advance(3f);
            Check(_authority.ActivationsAutoCompleted == auto, "and the expiry does not touch it afterwards");

            yield return null;
        }

        // ---- 7. the sender-less path ---------------------------------------------------------------------------

        private IEnumerator SenderlessPath()
        {
            Teleport(_p1, Spot + new Vector3(0f, 0.15f, 0f));
            Advance(_cry.EffectiveCooldown + _dash.EffectiveCooldown + 2f);
            EmberdelveKitFixtures.RefillMana(_p1);
            yield return null;

            Check(EmberdelveKitFixtures.SenderFor(_p1) == _s1 && EmberdelveKitFixtures.SenderFor(_p2.CombatId) == _s2
                  && EmberdelveKitFixtures.SenderFor(_enemy.CombatId) == CombatSenderId.TrustedHost,
                "the fixture helper resolves the owning sender for players and the trusted host for an enemy");
            Check(!CombatSenders.TryGetOwner(_enemy.CombatId, out _) && CombatSenders.TryGetOwner(_p1.CombatId, out _), "the enemy is unowned and the player is owned");

            // skill cast
            int refusals = _authority.SenderRefusalCount;
            int open = _authority.OpenActivationCount;
            float mana = _authority.TryGetKit(_p1.CombatId, out KitView kit) ? kit.Mana : -1f;
            bool ok = _authority.TryBeginSkillCast(_p1.CombatId, _cry.skillId, out int act, out string reason);
            _authority.TryGetKit(_p1.CombatId, out KitView kitAfter);
            Check(!ok && act == 0 && Has(reason, "no sender") && _authority.SenderRefusalCount == refusals + 1 && _authority.OpenActivationCount == open
                  && Mathf.Approximately(kitAfter.Mana, mana), "a sender-less cast for a player is refused and spends nothing (" + reason + ")");
            ok = _authority.TryBeginSkillCast(_s1, _p1.CombatId, _cry.skillId, out act, out reason);
            Check(ok && act > 0, "the same cast from its owner then works: the refusal had stamped no cooldown (" + reason + ")");

            // completion of a real open activation
            refusals = _authority.SenderRefusalCount;
            Check(!_authority.CompleteSkillCast(_p1.CombatId, act) && _authority.SenderRefusalCount == refusals + 1, "a sender-less completion is refused");
            Check(_authority.CompleteSkillCast(_s1, _p1.CombatId, act), "the owner completes it");

            // swing
            WeaponData sword = _p1.ActiveWeapon;
            Check(sword != null && sword.UsesSweptMelee, "P1 holds a swept melee weapon");
            if (sword != null)
            {
                _p1.TryGetAimPose(out _, out float yaw, out float pitch);
                MeleeSwingIntent swing = new MeleeSwingIntent(_p1.CombatId, sword.weaponId, 0, 0, yaw, pitch, CombatRegistry.NextSequence(_p1.CombatId));
                int started = _authority.SwingsStarted;
                refusals = _authority.SenderRefusalCount;
                ok = _authority.TryBeginMeleeSwing(swing, out int swingId, out reason);
                Check(!ok && swingId == 0 && Has(reason, "no sender") && _authority.SwingsStarted == started && _authority.SenderRefusalCount == refusals + 1,
                    "a sender-less swing for a player is refused (" + reason + ")");
                ok = _authority.TryBeginMeleeSwingAsOwner(swing, out swingId, out reason);
                Check(ok && _authority.SwingsStarted == started + 1, "the owner-sent swing with the same intent begins (the refusal burned no sequence): " + reason);
                float guard = 0f;
                while (_authority.ActiveSwingCount > 0 && guard < 4f) { guard += Time.deltaTime; yield return null; }
            }

            // attack and projectile
            WeaponData bow = _p2.ActiveWeapon;
            if (bow != null && bow.IsRanged)
            {
                AttackIntent shot = new AttackIntent(_p2.CombatId, _p2.Kind, bow, 1f, _p2.GetMuzzlePosition(), _p2.AimDirection, bow.rangedRange,
                    CombatRegistry.NextSequence(_p2.CombatId), Time.time);
                refusals = _authority.SenderRefusalCount;
                Check(!_authority.LaunchProjectile(shot) && _authority.SenderRefusalCount == refusals + 1 && Has(_authority.LastAttackRejectReason, "no sender"),
                    "a sender-less arrow for a player is refused");
                HitCandidate hit = new HitCandidate(_p1.CombatId, _p1.CombatCenter, false);
                float hp = _p1.CurrentHealth;
                Check(!_authority.SubmitAttack(shot, hit, out reason) && Has(reason, "no sender") && Mathf.Approximately(hp, _p1.CurrentHealth),
                    "a sender-less attack for a player is refused and does no damage (" + reason + ")");
            }

            // the enemy is unowned: its own sender-less path keeps working
            int swingsBefore = _authority.SwingsStarted;
            refusals = _authority.SenderRefusalCount;
            bool begun = _enemy.TryBeginAttack(Vector3.forward, out reason);
            Check(begun && _authority.SwingsStarted == swingsBefore + 1 && _authority.SenderRefusalCount == refusals, "an enemy's sender-less attack still begins a swing: " + reason);

            // the escape hatch exists only as an internal seam
            Check(!_authority.AllowSenderlessPlayerCommands, "sender-less player commands are off by default");
            yield return null;
        }
    }
}
