using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Emberdelve.Core;
using Emberdelve.AI;
using Emberdelve.Data;
using Emberdelve.Player;
using Emberdelve.Combat;

namespace Emberdelve.EditorTools
{
    /// <summary>
    /// R1a gate: weapon attacks submitted to LocalCombatAuthority are requests, never outcomes.
    /// Every forged field a client controls (weapon, range, hit point, headshot flag, timestamp, sequence)
    /// is submitted and the authority must reject it or ignore it, and the legitimate attack beside it must
    /// still land with the authority's own number.
    ///
    /// Run: Unity -batchmode -nographics -projectPath . -logFile Logs/r1a_gate.log
    ///      -executeMethod Emberdelve.EditorTools.EmberdelveCombatAuthorityGate.Run
    /// The authority clock is injected, so cooldowns are advanced deterministically and a timestamp inside
    /// the intent is provably irrelevant.
    /// </summary>
    [InitializeOnLoad]
    public static class EmberdelveCombatAuthorityGate
    {
        private const string Pending = "Emberdelve.CombatAuthorityGate.Pending";

        static EmberdelveCombatAuthorityGate()
        {
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.EnteredPlayMode && SessionState.GetBool(Pending, false))
                {
                    SessionState.SetBool(Pending, false);
                    new GameObject("CombatAuthority_Gate").AddComponent<CombatAuthorityHarness>();
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

    public sealed class CombatAuthorityHarness : MonoBehaviour
    {
        private const string Tag = "[CombatAuthorityGate]";

        private int _checks;
        private int _failures;
        private readonly List<UnityEngine.Object> _cleanup = new List<UnityEngine.Object>();
        private readonly List<Fake> _fakes = new List<Fake>();

        private LocalCombatAuthority _authority;
        private float _clock = 100f;
        private PlayerRig _rig;
        private Fake _target;
        private SphereCollider _head;

        /// <summary>A hostile (or friendly) combat entity with a body, optional head collider and a weapon.</summary>
        private sealed class Fake : ICombatEntity, IActiveWeaponProvider
        {
            public int CombatId { get; set; }
            public EntityKind Kind { get; set; } = EntityKind.Enemy;
            public int TeamId { get; set; } = CombatTeams.MonsterTeam;
            public Vector3 Point;
            public Collider Head;
            public bool Dead;
            public float Taken;
            public DamageInfo Last = DamageInfo.Invalid;
            public WeaponData Weapon;

            public Vector3 CombatCenter => Point;
            public Collider HeadCollider => Head;
            public WeaponData ActiveWeapon => Weapon;
            public bool IsDead => Dead;
            public float CurrentHealth => 100000f - Taken;
            public float MaxHealth => 100000f;

            public void ApplyDamage(DamageInfo damage)
            {
                Last = damage;
                Taken += damage.Amount;
            }
        }

        /// <summary>An entity that cannot name a weapon at all: it must not be able to attack.</summary>
        private sealed class WeaponlessFake : ICombatEntity
        {
            public int CombatId { get; set; }
            public EntityKind Kind => EntityKind.Enemy;
            public int TeamId => CombatTeams.MonsterTeam;
            public Vector3 CombatCenter => Vector3.zero;
            public Collider HeadCollider => null;
            public bool IsDead => false;
            public float CurrentHealth => 1f;
            public float MaxHealth => 1f;
            public void ApplyDamage(DamageInfo damage) { }
        }

        private void Check(bool ok, string label)
        {
            _checks++;
            if (!ok)
            {
                _failures++;
                Debug.LogError($"{Tag} FAIL {label}");
            }
        }

        private IEnumerator Start()
        {
            yield return null;
            yield return null;

            try { Execute(); }
            catch (Exception e)
            {
                _failures++;
                Debug.LogException(e);
            }
            finally
            {
                foreach (Fake f in _fakes) CombatRegistry.Unregister(f.CombatId, f);
                foreach (UnityEngine.Object o in _cleanup) if (o != null) Destroy(o);
            }

            Debug.Log($"{Tag} {(_failures == 0 ? "PASS" : "FAIL")} checks={_checks} failures={_failures}");
            EditorApplication.Exit(_failures == 0 ? 0 : 1);
        }

        private Fake MakeFake(string name, int team, EntityKind kind, WeaponData weapon)
        {
            Fake f = new Fake { CombatId = CombatRegistry.AllocateId(), TeamId = team, Kind = kind, Weapon = weapon };
            CombatRegistry.Register(f.CombatId, f);
            _fakes.Add(f);
            return f;
        }

        // Outcome of one submission.
        private struct Outcome
        {
            public bool Accepted;
            public string Reason;
            public float Damage;
            public DamageInfo Info;
        }

        private Outcome Submit(Fake target, WeaponData claimedWeapon, float maxRange, Vector3 hitPoint, bool headFlag,
            float timestamp, int? sequence = null, int? instigatorId = null, EntityKind kind = EntityKind.LocalPlayer)
        {
            int id = instigatorId ?? _rig.CombatId;
            int seq = sequence ?? CombatRegistry.NextSequence(id);
            AttackIntent intent = new AttackIntent(id, kind, claimedWeapon, 1f, _rig.CombatCenter,
                (hitPoint - _rig.CombatCenter), maxRange, seq, timestamp);

            float before = target.Taken;
            target.Last = DamageInfo.Invalid;
            bool ok = _authority.SubmitAttackAsOwner(intent, new HitCandidate(target.CombatId, hitPoint, headFlag), out string reason);
            return new Outcome { Accepted = ok, Reason = reason ?? string.Empty, Damage = target.Taken - before, Info = target.Last };
        }

        private Outcome LaunchPlayerProjectile(Fake target, WeaponData claimedWeapon, float maxRange, Vector3 aimPoint)
        {
            Quaternion originalAim = _rig.Look.AimTransform.rotation;
            Vector3 muzzle = _rig.GetMuzzlePosition();
            Vector3 direction = aimPoint - muzzle;
            if (direction.sqrMagnitude > 0.0001f) _rig.Look.AimTransform.rotation = Quaternion.LookRotation(direction.normalized, Vector3.up);
            AttackIntent intent = new AttackIntent(_rig.CombatId, _rig.Kind, claimedWeapon, 1f,
                muzzle, direction, maxRange, CombatRegistry.NextSequence(_rig.CombatId), _clock);
            float before = target.Taken;
            target.Last = DamageInfo.Invalid;
            // The magazine is the authority's record now (the old rig-side reload no longer refills it behind this gate's back):
            // keep a round available through the host seam so every probe tests its own rule, not the crossbow's 4-round magazine.
            RangedCombat probeRanged = _rig.GetComponent<RangedCombat>();
            if (probeRanged != null && probeRanged.Magazine < 1) EmberdelveKitFixtures.RefundAmmo(probeRanged, 1);
            bool accepted = _authority.LaunchProjectileAsOwner(intent);
            string reason = _authority.LastAttackRejectReason;
            if (accepted)
            {
                float travelTime = claimedWeapon != null
                    ? claimedWeapon.rangedRange / Mathf.Max(2f, claimedWeapon.projectileSpeed) + 0.02f
                    : 0.1f;
                _authority.AdvanceProjectiles(travelTime);
            }
            _rig.Look.AimTransform.rotation = originalAim;
            return new Outcome { Accepted = accepted, Reason = reason, Damage = target.Taken - before, Info = target.Last };
        }

        private void PlaceTarget(float distance)
        {
            _target.Point = _rig.CombatCenter + Vector3.forward * distance;
            _head.transform.position = _target.Point + Vector3.up * 0.6f;
            Physics.SyncTransforms();
        }

        /// <summary>Advances the AUTHORITY clock past any weapon cooldown. The only legal way to wait.</summary>
        private void AdvanceClock(float seconds) => _clock += seconds;

        private void Execute()
        {
            _rig = FindFirstObjectByType<PlayerRig>();
            Check(_rig != null && !_rig.IsDead, "a living player rig is available");
            if (_rig == null) return;

            // Keep projectile probes away from scene enemies and arena colliders so this gate measures
            // its own analytic target; wall ordering and physical scene contacts live in ProjectileBlockGate.
            foreach (EnemyAgent enemy in FindObjectsByType<EnemyAgent>(FindObjectsSortMode.None)) enemy.gameObject.SetActive(false);
            _rig.transform.position = new Vector3(3000f, 0f, 3000f);
            Physics.SyncTransforms();

            WeaponData sword = _rig.MainHandWeapon;
            WeaponData crossbow = AssetDatabase.LoadAssetAtPath<WeaponData>(EmberdelvePaths.Data + "/Weapon_EmberCrossbow.asset");
            WeaponData rapier = AssetDatabase.LoadAssetAtPath<WeaponData>(EmberdelvePaths.Data + "/Weapon_EmberRapier.asset");
            WeaponData claw = AssetDatabase.LoadAssetAtPath<WeaponData>(EmberdelvePaths.Data + "/Weapon_HollowClaw.asset");
            Check(sword != null && crossbow != null && rapier != null && claw != null, "weapon assets are available");
            if (sword == null || crossbow == null || rapier == null || claw == null) return;
            Check(_rig.Loadout != null && _rig.Loadout.ActiveWeapon == sword, "the rig holds the sword in its active hand");
            Check(_rig.Loadout.Secondary == crossbow, "the warrior's authored secondary is the crossbow");

            GameConfig cfg = ScriptableObject.CreateInstance<GameConfig>();
            _cleanup.Add(cfg);
            cfg.levelMask = 0;
            GameObject go = new GameObject("CombatAuthorityGate_Authority");
            _cleanup.Add(go);
            _authority = go.AddComponent<LocalCombatAuthority>();
            typeof(LocalCombatAuthority).GetField("config", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(_authority, cfg);
            _authority.SetClock(() => _clock);

            GameObject headGo = new GameObject("CombatAuthorityGate_Head");
            _cleanup.Add(headGo);
            _head = headGo.AddComponent<SphereCollider>();
            _head.radius = 0.15f;

            _target = MakeFake("target", CombatTeams.MonsterTeam, EntityKind.Enemy, null);
            _target.Head = _head;
            _head.enabled = false;

            // ---- D1a: a melee weapon attack from the player rig (an entity with a swing pose) no longer
            // goes through this arc-less path at all. Default authority: rejected, nothing applied, no
            // sequence spent. The tests below are about the claim rules of SubmitAttack itself (weapon, range,
            // hit point, headshot, clock, sequence, hostility), which the sword probes exactly as before, so
            // they opt in to the legacy path explicitly (internal seam, Editor assembly only).
            PlaceTarget(1.5f);
            Vector3 body = _target.Point;
            Outcome swingOnly = Submit(_target, sword, sword.reach + sword.hitSphereRadius, body, false, _clock);
            Check(!swingOnly.Accepted && swingOnly.Damage == 0f && swingOnly.Reason.Contains("swept swing"),
                "D1a: a melee SubmitAttack from the player rig is refused by default (" + swingOnly.Reason + ")");
            _authority.AllowLegacyMeleeAttacks = true;

            // ---- baseline: the legitimate attack, and the number the authority produces for it
            Outcome baseline = Submit(_target, sword, sword.reach + sword.hitSphereRadius, body, false, _clock);
            Check(baseline.Accepted, "a legitimate sword attack is accepted (" + baseline.Reason + ")");
            Check(baseline.Damage > 0f && baseline.Info.IsValid, $"the legitimate attack deals authority damage ({baseline.Damage:F2})");
            float baselineDamage = baseline.Damage;
            Check(!baseline.Info.Headshot, "a body hit is not a headshot");

            // ---- 1. forged weapon: not the equipped weapon
            AdvanceClock(10f);
            PlaceTarget(1.5f);
            Outcome forgedWeapon = Submit(_target, rapier, rapier.reach + rapier.hitSphereRadius, body, false, _clock);
            Check(!forgedWeapon.Accepted && forgedWeapon.Damage == 0f, "a weapon the rig does not hold is rejected");
            Check(forgedWeapon.Reason.Contains("not the equipped weapon"), "forged weapon rejected for the weapon rule (" + forgedWeapon.Reason + ")");

            Outcome strongerClaw = Submit(_target, claw, claw.reach + claw.hitSphereRadius, body, false, _clock);
            Check(!strongerClaw.Accepted && strongerClaw.Reason.Contains("not the equipped weapon"), "an enemy weapon claimed by the player is rejected");

            Outcome nullWeapon = Submit(_target, null, 3f, body, false, _clock);
            Check(!nullWeapon.Accepted && nullWeapon.Reason.Contains("not the equipped weapon"), "an intent with no weapon is rejected");

            // After a real loadout swap the crossbow is the equipped weapon: it passes, the sword fails.
            Check(_rig.Loadout.SwitchToSlot(1) && _rig.MainHandWeapon == crossbow, "loadout swapped to the crossbow");
            AdvanceClock(10f);
            Outcome swordWhileCrossbow = Submit(_target, sword, sword.reach + sword.hitSphereRadius, body, false, _clock);
            Check(!swordWhileCrossbow.Accepted && swordWhileCrossbow.Reason.Contains("not the equipped weapon"),
                "the sword is rejected while the crossbow is the equipped weapon");
            Outcome crossbowOk = Submit(_target, crossbow, crossbow.rangedRange, body, false, _clock);
            Check(!crossbowOk.Accepted && crossbowOk.Reason.Contains("must launch") && crossbowOk.Damage == 0f,
                "a ranged hit-only SubmitAttack cannot bypass projectile flight (" + crossbowOk.Reason + ")");
            Outcome crossbowFlight = LaunchPlayerProjectile(_target, crossbow, crossbow.rangedRange, _target.Point);
            Check(crossbowFlight.Accepted && crossbowFlight.Damage > 0f && crossbowFlight.Info.IsValid,
                "the equipped crossbow damages the actual target only after its authority-owned flight (" + crossbowFlight.Reason + ")");

            // ---- 2. flight stops at the authoritative weapon range; a forged launch range is refused.
            AdvanceClock(10f);
            PlaceTarget(60f);
            float farBefore = _target.Taken;
            Outcome farFlight = LaunchPlayerProjectile(_target, crossbow, crossbow.rangedRange, _target.Point);
            Check(farFlight.Accepted && Mathf.Abs(_target.Taken - farBefore) < 0.001f,
                "an arrow may travel to weapon range but cannot hit a target beyond it (" + farFlight.Reason + ")");
            AdvanceClock(10f);
            Outcome farForgedRange = LaunchPlayerProjectile(_target, crossbow, 1000f, _target.Point);
            Check(!farForgedRange.Accepted && farForgedRange.Damage == 0f && farForgedRange.Reason.Contains("claimed range"),
                "a claimed MaxRange of 1000 does not widen an accepted projectile flight (" + farForgedRange.Reason + ")");
            PlaceTarget(20f);
            AdvanceClock(10f);
            Outcome inReachForgedRange = LaunchPlayerProjectile(_target, crossbow, 1000f, _target.Point);
            Check(!inReachForgedRange.Accepted && inReachForgedRange.Reason.Contains("claimed range"),
                "even an in-reach target is refused when the range claim is forged (" + inReachForgedRange.Reason + ")");

            // ---- 3. client hit-point claims cannot bypass projectile launch and collision.
            PlaceTarget(20f);
            Outcome atMuzzle = Submit(_target, crossbow, crossbow.rangedRange, _rig.CombatCenter, false, _clock);
            Check(!atMuzzle.Accepted && atMuzzle.Damage == 0f && atMuzzle.Reason.Contains("must launch"),
                "a claimed muzzle hit cannot produce ranged damage (" + atMuzzle.Reason + ")");
            Outcome nan = Submit(_target, crossbow, crossbow.rangedRange, new Vector3(float.NaN, 0f, 0f), false, _clock);
            Check(!nan.Accepted && nan.Damage == 0f && nan.Reason.Contains("must launch"),
                "a NaN hit-point claim cannot produce ranged damage (" + nan.Reason + ")");

            // Falloff now measures the actual flight impact, not any client-supplied hit point.
            AdvanceClock(10f);
            PlaceTarget(30f);
            Outcome honest30 = LaunchPlayerProjectile(_target, crossbow, crossbow.rangedRange, _target.Point);
            Check(honest30.Accepted && honest30.Damage > 0f, "an honest 30m crossbow flight reaches and damages the target (" + honest30.Reason + ")");
            float mult = _rig.GetOutgoingDamageMultiplier(crossbow);
            float flat = _rig.GetFlatWeaponDamageBonus(crossbow);
            float actualImpactRange = Vector3.Distance(honest30.Info.SourcePosition, honest30.Info.Point);
            float expected30 = crossbow.DamageAtRange(actualImpactRange, flat) * mult;
            Check(Mathf.Abs(honest30.Damage - expected30) < 0.02f,
                $"the projectile falloff uses its actual impact distance ({honest30.Damage:F3} vs {expected30:F3})");
            AdvanceClock(10f);
            Vector3 shortened = _target.Point + _rig.transform.right * 2f;
            float beforeMissAim = _target.Taken;
            Outcome shortClaim = LaunchPlayerProjectile(_target, crossbow, crossbow.rangedRange, shortened);
            Check(shortClaim.Accepted && Mathf.Abs(_target.Taken - beforeMissAim) < 0.001f,
                "an off-axis launch misses the body even when the client claims its center");

            // ---- 3b. D1c3: a ranged shot at a target WITHOUT hit volumes earns no location at all, whatever head collider
            // it carries. The old rule (head collider contact = headshot x1.8) is gone for projectiles; this fake has an
            // enabled head collider and no IHitShapeProvider, so the flight resolves Unspecified, x1.0, Headshot false.
            AdvanceClock(10f);
            PlaceTarget(15f);
            _head.enabled = true;
            Outcome noVolumes = LaunchPlayerProjectile(_target, crossbow, crossbow.rangedRange, _target.Point);
            _head.enabled = false;
            float noVolumesRange = Vector3.Distance(noVolumes.Info.SourcePosition, noVolumes.Info.Point);
            float expectedNoVolumes = crossbow.DamageAtRange(noVolumesRange, flat) * mult;   // x1.0: no location factor at all
            Check(noVolumes.Accepted && noVolumes.Damage > 0f && !noVolumes.Info.Headshot && noVolumes.Info.Location == HitLocation.Unspecified,
                "D1c3: a volume-less target with an enabled head collider takes an Unspecified, non-headshot arrow (" + noVolumes.Reason + ")");
            Check(Mathf.Abs(noVolumes.Damage - expectedNoVolumes) < 0.02f,
                $"D1c3: that arrow carries no location factor, the old x1.8 is gone ({noVolumes.Damage:F3} vs {expectedNoVolumes:F3})");

            // Back to the sword for the rest.
            Check(_rig.Loadout.SwitchToSlot(0) && _rig.MainHandWeapon == sword, "loadout back on the sword");

            // ---- L1: a melee target genuinely beyond the sword's reach (honest range claim, honest point on the target)
            AdvanceClock(10f);
            PlaceTarget(8f);
            Outcome meleeFar = Submit(_target, sword, sword.reach + sword.hitSphereRadius, _target.Point, false, _clock);
            Check(!meleeFar.Accepted && meleeFar.Damage == 0f && meleeFar.Reason.Contains("weapon reaches"),
                "a melee target 8 m away is rejected on the authority's own distance (" + meleeFar.Reason + ")");
            // ---- 4. headshot: the geometry decides, never the flag
            AdvanceClock(10f);
            PlaceTarget(1.5f);
            _head.enabled = true;
            Vector3 headPoint = _head.transform.position;
            Outcome forgedHead = Submit(_target, sword, sword.reach + sword.hitSphereRadius, body, true, _clock);
            Check(forgedHead.Accepted, "a body hit that claims a headshot is still accepted as a body hit (" + forgedHead.Reason + ")");
            Check(!forgedHead.Info.Headshot, "the forged headshot flag does not mark the damage as a headshot");
            Check(Mathf.Abs(forgedHead.Damage - baselineDamage) < 0.01f,
                $"the forged headshot deals body damage ({forgedHead.Damage:F2} vs {baselineDamage:F2})");

            AdvanceClock(10f);
            Outcome realHead = Submit(_target, sword, sword.reach + sword.hitSphereRadius, headPoint, false, _clock);
            Check(realHead.Accepted && realHead.Info.Headshot, "a hit on the head collider is a headshot even when the claim says no");
            Check(Mathf.Abs(realHead.Damage - baselineDamage * DamageResolver.HeadshotMultiplier) < 0.05f,
                $"the real headshot carries the headshot multiplier ({realHead.Damage:F2} vs {baselineDamage * DamageResolver.HeadshotMultiplier:F2})");
            _head.enabled = false;

            // ---- 5. clock: no timestamp in the intent can move the authority's cooldown
            AdvanceClock(10f);
            PlaceTarget(1.5f);
            Outcome stamped = Submit(_target, sword, sword.reach + sword.hitSphereRadius, body, false, _clock + 1000f);
            Check(stamped.Accepted, "an attack stamped far in the future is accepted on authority time (" + stamped.Reason + ")");
            Outcome future = Submit(_target, sword, sword.reach + sword.hitSphereRadius, body, false, _clock + 5000f);
            Check(!future.Accepted && future.Reason.Contains("cooldown"), "an immediate repeat is a cooldown rejection whatever its timestamp says (" + future.Reason + ")");
            Outcome past = Submit(_target, sword, sword.reach + sword.hitSphereRadius, body, false, _clock - 5000f);
            Check(!past.Accepted && past.Reason.Contains("cooldown"), "a timestamp in the far past does not dodge the cooldown (" + past.Reason + ")");
            AdvanceClock(sword.cooldown * 0.5f);
            Outcome half = Submit(_target, sword, sword.reach + sword.hitSphereRadius, body, false, _clock + 5000f);
            Check(!half.Accepted && half.Reason.Contains("cooldown"), "half a cooldown on the authority clock is still too soon");
            AdvanceClock(sword.cooldown * 2f);
            Outcome afterWait = Submit(_target, sword, sword.reach + sword.hitSphereRadius, body, false, _clock - 5000f);
            Check(afterWait.Accepted, "a legitimate attack after the real cooldown is accepted although it claims a stale timestamp (" + afterWait.Reason + ")");

            // ---- 6. sequence replay
            AdvanceClock(10f);
            int seq = CombatRegistry.NextSequence(_rig.CombatId);
            Outcome first = Submit(_target, sword, sword.reach + sword.hitSphereRadius, body, false, _clock, seq);
            Check(first.Accepted, "the first use of a sequence is accepted (" + first.Reason + ")");
            AdvanceClock(10f);
            Outcome replay = Submit(_target, sword, sword.reach + sword.hitSphereRadius, body, false, _clock, seq);
            Check(!replay.Accepted && replay.Damage == 0f && replay.Reason.Contains("sequence"), "a replayed sequence is rejected (" + replay.Reason + ")");
            Outcome older = Submit(_target, sword, sword.reach + sword.hitSphereRadius, body, false, _clock, seq - 1);
            Check(!older.Accepted && older.Reason.Contains("sequence"), "an older sequence is rejected (" + older.Reason + ")");

            // ---- 7. identity rules still hold
            AdvanceClock(10f);
            Outcome unknownInstigator = Submit(_target, sword, 3f, body, false, _clock, null, 987654321);
            Check(!unknownInstigator.Accepted && unknownInstigator.Reason.Contains("unknown instigator"), "an unknown instigator id is rejected (" + unknownInstigator.Reason + ")");

            Fake ghostTarget = new Fake { CombatId = 987654322 };
            bool unknownTarget = _authority.SubmitAttackAsOwner(
                new AttackIntent(_rig.CombatId, EntityKind.LocalPlayer, sword, 1f, _rig.CombatCenter, Vector3.forward, 3f, CombatRegistry.NextSequence(_rig.CombatId), _clock),
                new HitCandidate(ghostTarget.CombatId, body, false), out string unknownTargetReason);
            Check(!unknownTarget && unknownTargetReason.Contains("unknown target"), "an unknown target id is rejected (" + unknownTargetReason + ")");

            Fake friend = MakeFake("friend", CombatTeams.PlayerTeam, EntityKind.LocalPlayer, null);
            friend.Point = body;
            float friendBefore = friend.Taken;
            bool friendly = _authority.SubmitAttackAsOwner(
                new AttackIntent(_rig.CombatId, EntityKind.LocalPlayer, sword, 1f, _rig.CombatCenter, Vector3.forward, 3f, CombatRegistry.NextSequence(_rig.CombatId), _clock),
                new HitCandidate(friend.CombatId, body, false), out string friendlyReason);
            Check(!friendly && friend.Taken == friendBefore && friendlyReason.Contains("not hostile"), "friendly fire is rejected (" + friendlyReason + ")");

            bool selfHit = _authority.SubmitAttackAsOwner(
                new AttackIntent(_rig.CombatId, EntityKind.LocalPlayer, sword, 1f, _rig.CombatCenter, Vector3.forward, 3f, CombatRegistry.NextSequence(_rig.CombatId), _clock),
                new HitCandidate(_rig.CombatId, _rig.CombatCenter, false), out string selfReason);
            Check(!selfHit && selfReason.Contains("self"), "a self hit is rejected (" + selfReason + ")");

            // A dead instigator (a second, player-team entity that holds a weapon) cannot attack.
            Fake deadAttacker = MakeFake("dead attacker", CombatTeams.PlayerTeam, EntityKind.LocalPlayer, sword);
            deadAttacker.Point = _rig.CombatCenter;
            deadAttacker.Dead = true;
            Outcome dead = Submit(_target, sword, 3f, body, false, _clock, null, deadAttacker.CombatId);
            Check(!dead.Accepted && dead.Damage == 0f && dead.Reason.Contains("dead"), "a dead instigator is rejected (" + dead.Reason + ")");

            // A living entity that holds the sword attacks like anyone else; one with no weapon provider cannot.
            Fake liveAttacker = MakeFake("live attacker", CombatTeams.PlayerTeam, EntityKind.LocalPlayer, sword);
            liveAttacker.Point = _rig.CombatCenter;
            Outcome live = Submit(_target, sword, sword.reach + sword.hitSphereRadius, body, false, _clock, null, liveAttacker.CombatId);
            Check(live.Accepted, "a living second entity holding the sword can attack (" + live.Reason + ")");

            WeaponlessFake weaponless = new WeaponlessFake { CombatId = CombatRegistry.AllocateId() };
            CombatRegistry.Register(weaponless.CombatId, weaponless);
            Outcome noWeapon = Submit(_target, sword, 3f, body, false, _clock, null, weaponless.CombatId);
            Check(!noWeapon.Accepted && noWeapon.Reason.Contains("no authoritative weapon"), "an entity that cannot name a weapon cannot attack (" + noWeapon.Reason + ")");
            CombatRegistry.Unregister(weaponless.CombatId, weaponless);

            // A dead target is not an error and not a hit.
            _target.Dead = true;
            float deadTargetBefore = _target.Taken;
            bool deadTarget = _authority.SubmitAttackAsOwner(
                new AttackIntent(_rig.CombatId, EntityKind.LocalPlayer, sword, 1f, _rig.CombatCenter, Vector3.forward, 3f, CombatRegistry.NextSequence(_rig.CombatId), _clock),
                new HitCandidate(_target.CombatId, body, false), out string deadTargetReason);
            Check(!deadTarget && _target.Taken == deadTargetBefore && deadTargetReason.Contains("target is dead"), "a dead target takes no damage (" + deadTargetReason + ")");
            _target.Dead = false;

            // The authority did not need the intent clock: the injected clock is what it reads.
            Check(Mathf.Abs(_authority.Now - _clock) < 0.0001f, "the authority reports its injected clock");
            _authority.SetClock(null);
            Check(Mathf.Abs(_authority.Now - Time.time) < 0.5f, "clearing the injected clock returns the authority to Time.time");

            Debug.Log($"{Tag} accepted={_authority.AcceptedCount} rejected={_authority.RejectedCount}");
        }
    }
}
