using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Emberdelve.Combat;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Player;

namespace Emberdelve.EditorTools
{
    /// <summary>
    /// D1c3 gate: ranged projectiles take their damage multiplier from the SAME hit-location table melee uses
    /// (head 1.5, body 1.0, arms 0.8, hands 0.7, legs 0.6, feet 0.5), decided by the authority from the first hit volume
    /// the arrow touches along its step segment, never from a client claim or a head collider. Every expected factor
    /// below is a literal written in this file, not read from the table in code. Fixtures stand far outside the dungeon
    /// and the authority runs with empty physics masks, so only the analytic hit volumes are in play.
    ///
    /// Run: Unity -batchmode -nographics -projectPath . -logFile Logs/d1c3_rangedlocation.log
    ///      -executeMethod Emberdelve.EditorTools.EmberdelveRangedLocationGate.Run
    /// </summary>
    [InitializeOnLoad]
    public static class EmberdelveRangedLocationGate
    {
        private const string Pending = "Emberdelve.RangedLocationGate.Pending";

        static EmberdelveRangedLocationGate()
        {
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.EnteredPlayMode && SessionState.GetBool(Pending, false))
                {
                    SessionState.SetBool(Pending, false);
                    new GameObject("RangedLocation_Gate").AddComponent<RangedLocationHarness>();
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

    public sealed class RangedLocationHarness : MonoBehaviour
    {
        private const string Tag = "[RangedLocationGate]";
        private static readonly Vector3 Origin = new Vector3(3000f, 0f, 3000f);

        private int _checks;
        private int _failures;
        private readonly List<UnityEngine.Object> _cleanup = new List<UnityEngine.Object>();
        private readonly List<ICombatEntity> _registered = new List<ICombatEntity>();

        private LocalCombatAuthority _authority;
        private GameConfig _config;
        private float _clock = 100f;
        private WeaponData _bow;
        private WeaponData _sword;

        private sealed class Shooter : ICombatEntity, IActiveWeaponProvider, IAimPoseProvider
        {
            public int CombatId { get; set; }
            public EntityKind Kind => EntityKind.Enemy;
            public int TeamId => CombatTeams.MonsterTeam;
            public WeaponData ActiveWeapon { get; set; }
            public Vector3 AimOrigin;
            public Vector3 AimDirection;
            public Vector3 CombatCenter => AimOrigin;
            public Collider HeadCollider => null;
            public bool IsDead => false;
            public float CurrentHealth => 100f;
            public float MaxHealth => 100f;
            public bool TryGetAimPose(out Vector3 eye, out float yawDegrees, out float pitchDegrees)
            {
                eye = AimOrigin;
                return SwingGeometry.TryAimAngles(AimDirection, out yawDegrees, out pitchDegrees);
            }
            public void ApplyDamage(DamageInfo damage) { }
        }

        /// <summary>A hostile target that exposes explicit hit volumes (no colliders, no head collider).</summary>
        private sealed class Volumes : ICombatEntity, IHitShapeProvider
        {
            public int CombatId { get; set; }
            public EntityKind Kind => EntityKind.Enemy;
            public int TeamId => CombatTeams.PlayerTeam;   // hostile to the monster-team shooter
            public readonly List<HitShape> Shapes = new List<HitShape>();
            public Vector3 Center = Origin + new Vector3(0f, 1.1f, 0f);
            public float Taken;
            public DamageInfo Last = DamageInfo.Invalid;
            public Vector3 CombatCenter => Center;
            public Collider HeadCollider => null;
            public bool IsDead => false;
            public float CurrentHealth => 100000f - Taken;
            public float MaxHealth => 100000f;
            public void ApplyDamage(DamageInfo damage) { Last = damage; Taken += damage.Amount; }
            public void CollectHitShapes(List<HitShape> shapes) { shapes.AddRange(Shapes); }
        }

        /// <summary>A hostile target with a body sphere and an ENABLED head collider but no hit volumes at all.</summary>
        private sealed class Bare : ICombatEntity
        {
            public int CombatId { get; set; }
            public EntityKind Kind => EntityKind.Enemy;
            public int TeamId => CombatTeams.PlayerTeam;   // hostile to the monster-team shooter
            public Collider Head;
            public float Taken;
            public DamageInfo Last = DamageInfo.Invalid;
            public Vector3 CombatCenter => Origin + new Vector3(0f, 1.1f, 0f);
            public Collider HeadCollider => Head;
            public bool IsDead => false;
            public float CurrentHealth => 100000f - Taken;
            public float MaxHealth => 100000f;
            public void ApplyDamage(DamageInfo damage) { Last = damage; Taken += damage.Amount; }
        }

        private void Check(bool ok, string label)
        {
            _checks++;
            if (ok) return;
            _failures++;
            Debug.LogError($"{Tag} FAIL {label}");
        }

        private void Near(float expected, float actual, float tolerance, string label)
        {
            Check(Mathf.Abs(expected - actual) <= tolerance, $"{label} (expected {expected:F4}, got {actual:F4})");
        }

        private T Register<T>(T entity, int id) where T : class, ICombatEntity
        {
            CombatRegistry.Register(id, entity);
            _registered.Add(entity);
            return entity;
        }

        private Volumes NewVolumes(IEnumerable<HitShape> shapes)
        {
            Volumes v = new Volumes { CombatId = CombatRegistry.AllocateId() };
            v.Shapes.AddRange(shapes);
            return Register(v, v.CombatId);
        }

        /// <summary>
        /// A standing humanoid in the layout of the game's hit volumes (head 1.68, torso 0.92-1.334, arms at +-0.396,
        /// legs at +-0.147, feet below them) plus a hand sphere beside each hip so Hands can be pinned. World offsets are
        /// relative to <paramref name="root"/>.
        /// </summary>
        private static List<HitShape> Human(Vector3 root)
        {
            List<HitShape> s = new List<HitShape>();
            s.Add(new HitShape(HitLocation.Head, root + new Vector3(0f, 1.68f, 0f), root + new Vector3(0f, 1.68f, 0f), 0.22f));
            s.Add(new HitShape(HitLocation.Body, root + new Vector3(0f, 0.92f, 0f), root + new Vector3(0f, 1.334f, 0f), 0.30f));
            for (int side = -1; side <= 1; side += 2)
            {
                s.Add(new HitShape(HitLocation.Arms, root + new Vector3(side * 0.396f, 0.966f, 0f), root + new Vector3(side * 0.396f, 1.334f, 0f), 0.092f));
                s.Add(new HitShape(HitLocation.Hands, root + new Vector3(side * 0.50f, 0.70f, 0f), root + new Vector3(side * 0.50f, 0.70f, 0f), 0.06f));
                s.Add(new HitShape(HitLocation.Legs, root + new Vector3(side * 0.147f, 0.46f, 0f), root + new Vector3(side * 0.147f, 0.736f, 0f), 0.12f));
                s.Add(new HitShape(HitLocation.Feet, root + new Vector3(side * 0.147f, 0.11f, 0f), root + new Vector3(side * 0.147f, 0.23f, 0f), 0.11f));
            }
            return s;
        }

        /// <summary>Launches one bow arrow from <paramref name="from"/> along <paramref name="dir"/> and resolves the flight.</summary>
        private bool Shoot(Vector3 from, Vector3 dir, out string reason)
        {
            _clock += 10f;
            Shooter shooter = new Shooter { CombatId = CombatRegistry.AllocateId(), ActiveWeapon = _bow, AimOrigin = from, AimDirection = dir.normalized };
            CombatRegistry.Register(shooter.CombatId, shooter);
            try
            {
                AttackIntent intent = new AttackIntent(shooter.CombatId, shooter.Kind, _bow, 1f, from, shooter.AimDirection,
                    _bow.rangedRange, CombatRegistry.NextSequence(shooter.CombatId), _clock);
                bool launched = _authority.LaunchProjectile(intent);
                reason = _authority.LastAttackRejectReason;
                if (launched) _authority.AdvanceProjectiles(2f);
                return launched;
            }
            finally { CombatRegistry.Unregister(shooter.CombatId, shooter); }
        }

        /// <summary>One shot at a fixture; checks the location, the headshot flag and base x LITERAL factor.</summary>
        private void ExpectShot(Volumes target, string label, Vector3 fromLocal, Vector3 dir, HitLocation location, float literalFactor)
        {
            target.Last = DamageInfo.Invalid;
            float before = target.Taken;
            bool launched = Shoot(Origin + fromLocal, dir, out string reason);
            Check(launched, $"{label}: the launch is accepted ({reason})");
            DamageInfo info = target.Last;
            Check(info.IsValid && target.Taken > before, $"{label}: the arrow hit the target");
            if (!info.IsValid) return;
            Check(info.Location == location, $"{label}: location is {location} (got {info.Location})");
            Check(info.Headshot == (location == HitLocation.Head), $"{label}: Headshot is true exactly for Head (got {info.Headshot})");
            float distance = Vector3.Distance(info.SourcePosition, info.Point);
            Near(_bow.DamageAtRange(distance) * literalFactor, info.Amount, 0.02f, $"{label}: damage is bow base at {distance:F2} m x {literalFactor:F1}");
        }

        private IEnumerator Start()
        {
            yield return null;
            yield return null;
            try { Execute(); }
            catch (Exception e)
            {
                _failures++;
                Debug.LogError($"{Tag} FAIL gate threw");
                Debug.LogException(e);
            }
            finally
            {
                foreach (ICombatEntity e in _registered) CombatRegistry.Unregister(e.CombatId, e);
                foreach (UnityEngine.Object o in _cleanup) if (o != null) Destroy(o);
            }
            Debug.Log($"{Tag} {(_failures == 0 ? "PASS" : "FAIL")} checks={_checks} failures={_failures}");
            EditorApplication.Exit(_failures == 0 ? 0 : 1);
        }

        private void Section(string name, Action body)
        {
            int checksBefore = _checks;
            int failuresBefore = _failures;
            try { body(); }
            catch (Exception e)
            {
                _failures++;
                Debug.LogError($"{Tag} FAIL section '{name}' threw");
                Debug.LogException(e);
            }
            // Fixtures of one section stand at the same spot as the next section's: unregister them so an earlier
            // target cannot catch the arrow meant for a later one.
            foreach (ICombatEntity e in _registered) CombatRegistry.Unregister(e.CombatId, e);
            _registered.Clear();
            Debug.Log($"{Tag} section '{name}': {_checks - checksBefore} checks, {_failures - failuresBefore} failures");
        }

        private void Execute()
        {
            _bow = AssetDatabase.LoadAssetAtPath<WeaponData>(EmberdelvePaths.Data + "/Weapon_HollowBow.asset");
            _sword = AssetDatabase.LoadAssetAtPath<WeaponData>(EmberdelvePaths.Data + "/Weapon_AshenShortSword.asset");
            Check(_bow != null && _bow.IsRanged && _sword != null, "bow and sword assets are available");
            if (_bow == null || _sword == null) return;

            _config = ScriptableObject.CreateInstance<GameConfig>();
            _cleanup.Add(_config);
            _config.hitMask = 0;       // no physics query can find anything: the analytic hit volumes alone decide
            _config.levelMask = 0;
            _config.hitLocations = null;   // the code-default table; the expectations below are literals
            GameObject go = new GameObject("RangedLocationGate_Authority");
            _cleanup.Add(go);
            _authority = go.AddComponent<LocalCombatAuthority>();
            typeof(LocalCombatAuthority).GetField("config", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(_authority, _config);
            _authority.SetClock(() => _clock);

            Section("table multipliers per location", TablePerLocation);
            Section("first touched volume wins", FirstTouch);
            Section("no volumes: Unspecified x1", NoVolumes);
            Section("client cannot claim a location", NoClientClaim);
            Section("resolver rules (cap, flat skills, legacy)", ResolverRules);
            Section("crouch lowers the player's volumes", Crouch);
        }

        // ------------------------------------------------------------------------------------------------

        private void TablePerLocation()
        {
            Volumes human = NewVolumes(Human(Origin));
            Vector3 fwd = Vector3.forward;
            // The arrow flies horizontally along +z from 8 m in front, at the height/side of each volume.
            ExpectShot(human, "arrow to the head", new Vector3(0f, 1.68f, -8f), fwd, HitLocation.Head, 1.5f);
            ExpectShot(human, "arrow to the body", new Vector3(0f, 1.13f, -8f), fwd, HitLocation.Body, 1.0f);
            ExpectShot(human, "arrow to the arm", new Vector3(0.396f, 1.15f, -8f), fwd, HitLocation.Arms, 0.8f);
            ExpectShot(human, "arrow to the hand", new Vector3(0.50f, 0.70f, -8f), fwd, HitLocation.Hands, 0.7f);
            ExpectShot(human, "arrow to the leg", new Vector3(0.147f, 0.50f, -8f), fwd, HitLocation.Legs, 0.6f);
            ExpectShot(human, "arrow to the foot", new Vector3(0.147f, 0.17f, -8f), fwd, HitLocation.Feet, 0.5f);

            // Same table for a crossbow bolt: no head collider rule anywhere on the path.
            WeaponData crossbow = AssetDatabase.LoadAssetAtPath<WeaponData>(EmberdelvePaths.Data + "/Weapon_EmberCrossbow.asset");
            Check(crossbow != null && crossbow.IsRanged, "the crossbow asset is available");
            if (crossbow != null)
            {
                WeaponData saved = _bow;
                _bow = crossbow;
                try { ExpectShot(human, "crossbow bolt to the head", new Vector3(0f, 1.68f, -8f), fwd, HitLocation.Head, 1.5f); }
                finally { _bow = saved; }
            }
        }

        private void FirstTouch()
        {
            Volumes human = NewVolumes(Human(Origin));

            // From the side at chest height the arrow meets the near arm BEFORE it reaches the torso it is aimed at.
            ExpectShot(human, "side shot at chest height touches the near arm first", new Vector3(-3f, 1.15f, 0f), Vector3.right, HitLocation.Arms, 0.8f);

            // Aimed at the upper chest from above and in front, the descending arrow clips the head sphere first.
            Vector3 from = new Vector3(0f, 3.2f, -1.2f);
            Vector3 aim = new Vector3(0f, 1.45f, 0f);
            ExpectShot(human, "descending arrow aimed at the chest touches the head first", from, aim - from, HitLocation.Head, 1.5f);

            // The humanoid would catch the tie shots (same spot): take it out of the world, and let each tie fixture
            // leave before the next one stands up.
            CombatRegistry.Unregister(human.CombatId, human);
            _registered.Remove(human);

            // Equal entry distance: the lower HitLocation value wins whatever the order of the volumes.
            Vector3 c = Origin + new Vector3(0f, 1.1f, 0f);
            HitShape legs = new HitShape(HitLocation.Legs, c, c, 0.3f);
            HitShape body = new HitShape(HitLocation.Body, c, c, 0.3f);
            HitShape head = new HitShape(HitLocation.Head, c, c, 0.3f);
            Vector3 level = new Vector3(0f, 1.1f, -8f);
            Volumes tieA = NewVolumes(new[] { legs, body });
            ExpectShot(tieA, "tie Legs/Body (Legs listed first)", level, Vector3.forward, HitLocation.Body, 1.0f);
            CombatRegistry.Unregister(tieA.CombatId, tieA);
            Volumes tieB = NewVolumes(new[] { body, legs });
            ExpectShot(tieB, "tie Body/Legs (Body listed first)", level, Vector3.forward, HitLocation.Body, 1.0f);
            CombatRegistry.Unregister(tieB.CombatId, tieB);
            ExpectShot(NewVolumes(new[] { body, head }), "tie Body/Head resolves to Head", level, Vector3.forward, HitLocation.Head, 1.5f);
        }

        private void NoVolumes()
        {
            GameObject headGo = new GameObject("RangedLocationGate_BareHead");
            _cleanup.Add(headGo);
            SphereCollider headCollider = headGo.AddComponent<SphereCollider>();
            headCollider.radius = 0.15f;
            headGo.transform.position = Origin + new Vector3(0f, 1.7f, 0f);
            Bare bare = new Bare { CombatId = CombatRegistry.AllocateId(), Head = headCollider };
            Register(bare, bare.CombatId);
            Check(bare.HeadCollider != null && bare.HeadCollider.enabled && !(((ICombatEntity)bare) is IHitShapeProvider),
                "the fixture has an enabled head collider and no hit volumes");

            bool launched = Shoot(Origin + new Vector3(0f, 1.1f, -8f), Vector3.forward, out string reason);
            Check(launched, $"no volumes: the launch is accepted ({reason})");
            DamageInfo info = bare.Last;
            Check(info.IsValid, "no volumes: the body sphere still takes the arrow");
            if (!info.IsValid) return;
            float distance = Vector3.Distance(info.SourcePosition, info.Point);
            Check(info.Location == HitLocation.Unspecified && !info.Headshot, $"no volumes: Unspecified and no headshot (got {info.Location}, {info.Headshot})");
            Near(_bow.DamageAtRange(distance) * 1.0f, info.Amount, 0.02f, "no volumes: the damage is the plain bow base, x1.0");
        }

        private void NoClientClaim()
        {
            Volumes human = NewVolumes(Human(Origin));

            // A hit-only ranged SubmitAttack (the only place a client could send a head flag) is refused outright.
            _clock += 10f;
            Shooter claimant = new Shooter { CombatId = CombatRegistry.AllocateId(), ActiveWeapon = _bow, AimOrigin = Origin + new Vector3(0f, 1.68f, -8f), AimDirection = Vector3.forward };
            CombatRegistry.Register(claimant.CombatId, claimant);
            try
            {
                AttackIntent intent = new AttackIntent(claimant.CombatId, claimant.Kind, _bow, 1f, claimant.AimOrigin, Vector3.forward,
                    _bow.rangedRange, CombatRegistry.NextSequence(claimant.CombatId), _clock);
                float before = human.Taken;
                bool accepted = _authority.SubmitAttack(intent, new HitCandidate(human.CombatId, Origin + new Vector3(0f, 1.68f, 0f), true), out string reason);
                Check(!accepted && human.Taken == before && reason != null && reason.Contains("must launch"),
                    "a hit-only ranged claim with the head flag set applies nothing (" + reason + ")");
            }
            finally { CombatRegistry.Unregister(claimant.CombatId, claimant); }

            // A launch has no location field: aimed at the body it lands on the body, whatever else the shooter says.
            ExpectShot(human, "launch aimed at the body lands on the body", new Vector3(0f, 1.13f, -8f), Vector3.forward, HitLocation.Body, 1.0f);

            // A forged candidate flag handed straight to the resolver on the projectile path is ignored: Headshot comes
            // from the authority's location, and so does the factor.
            AttackIntent resolved = new AttackIntent(1, EntityKind.Enemy, _bow, 1f, Vector3.zero, Vector3.forward, _bow.rangedRange, 1, 0f);
            HitCandidate forged = new HitCandidate(human.CombatId, new Vector3(0f, 0f, 8f), true);
            DamageInfo asBody = DamageResolver.Resolve(_bow, resolved, forged, human, 1f, 0f, null, true, false, HitLocation.Body, 1.0f);
            Check(!asBody.Headshot && asBody.Location == HitLocation.Body, "a forged candidate Headshot flag is ignored when the resolved location is Body");
            Near(_bow.DamageAtRange(8f) * 1.0f, asBody.Amount, 0.001f, "...and no head factor is applied to it");
        }

        private void ResolverRules()
        {
            // Head additive bonus keeps its documented cap of 1.8 on top of the new 1.5 base.
            HitLocationTable table = HitLocationTable.Default;
            Near(1.5f, table.Multiplier(HitLocation.Head), 1e-5f, "head table value is 1.5 with no bonus");
            Near(1.6f, table.Multiplier(HitLocation.Head, 0.1f), 1e-5f, "a +0.1 head bonus adds to the base (1.6)");
            Near(1.8f, table.Multiplier(HitLocation.Head, 0.5f), 1e-5f, "a +0.5 head bonus is capped at 1.8");
            Near(1.8f, table.Multiplier(HitLocation.Head, 100f), 1e-5f, "a huge head bonus is still capped at 1.8");
            Near(0.8f, table.Multiplier(HitLocation.Arms, 0.5f), 1e-5f, "the head bonus never applies to other locations");

            Volumes human = NewVolumes(Human(Origin));
            Vector3 hitPoint = new Vector3(0f, 0f, 8f);
            AttackIntent attack = new AttackIntent(1, EntityKind.Enemy, _bow, 1f, Vector3.zero, Vector3.forward, _bow.rangedRange, 1, 0f);
            HitCandidate hit = new HitCandidate(human.CombatId, hitPoint, false);

            DamageInfo head = DamageResolver.Resolve(_bow, attack, hit, human, 1f, 0f, null, true, false, HitLocation.Head, 1.5f);
            Check(head.Headshot && head.Location == HitLocation.Head, "projectile Head: Headshot true, Location Head");
            Near(_bow.DamageAtRange(8f) * 1.5f, head.Amount, 0.001f, "projectile Head: base x 1.5");

            // Legacy (non projectile) hit-only resolution keeps the old binary rule: only ranged projectiles moved.
            HitCandidate legacyHead = new HitCandidate(human.CombatId, hitPoint, true);
            DamageInfo legacy = DamageResolver.Resolve(_bow, attack, legacyHead, human);
            Near(_bow.DamageAtRange(8f) * 1.8f, legacy.Amount, 0.001f, "a non-projectile resolution with a head flag still carries the legacy 1.8");

            // A flat-damage skill ignores location on the projectile path.
            SkillData flat = ScriptableObject.CreateInstance<SkillData>();
            _cleanup.Add(flat);
            flat.skillId = "gate_ranged_flat";
            flat.flatDamage = 14f;
            SkillIntent flatIntent = new SkillIntent(1, EntityKind.Enemy, flat, _bow, Vector3.zero, Vector3.forward, 8f, 1, 0f, default, 1, 1f);
            DamageInfo flatHead = DamageResolver.ResolveSkill(flatIntent, hit, human, 1f, 1f, 0f, true, false, HitLocation.Head, 1.5f);
            Near(14f, flatHead.Amount, 0.001f, "a flat-damage projectile skill takes no location factor");
            Check(!flatHead.Headshot && flatHead.Location == HitLocation.Unspecified, "a flat-damage projectile skill reports no location");

            // A weapon-based skill (MultiShot / PiercingShot shape) uses the table once.
            SkillData weaponSkill = ScriptableObject.CreateInstance<SkillData>();
            _cleanup.Add(weaponSkill);
            weaponSkill.skillId = "gate_ranged_weapon";
            weaponSkill.flatDamage = -1f;
            weaponSkill.damageMultiplier = 1.35f;
            SkillIntent weaponIntent = new SkillIntent(1, EntityKind.Enemy, weaponSkill, _bow, Vector3.zero, Vector3.forward, 8f, 1, 0f, default, 1, 1f);
            DamageInfo skillLegs = DamageResolver.ResolveSkill(weaponIntent, hit, human, 1f, 1f, 0f, true, false, HitLocation.Legs, 0.6f);
            Near(_bow.DamageAtRange(8f) * 1.35f * 0.6f, skillLegs.Amount, 0.001f, "a weapon-based projectile skill hit on the legs is base x skill x 0.6");
            Check(!skillLegs.Headshot && skillLegs.Location == HitLocation.Legs, "...with Location Legs and no headshot");
            DamageInfo skillHead = DamageResolver.ResolveSkill(weaponIntent, hit, human, 1f, 1f, 0f, true, false, HitLocation.Head, 1.5f);
            Near(_bow.DamageAtRange(8f) * 1.35f * 1.5f, skillHead.Amount, 0.001f, "a weapon-based projectile skill hit on the head is base x skill x 1.5");
            Check(skillHead.Headshot && skillHead.Location == HitLocation.Head, "...with Headshot true");
        }

        private void Crouch()
        {
            PlayerRig rig = FindFirstObjectByType<PlayerRig>();
            Check(rig != null && rig.Look != null && rig.Movement != null && rig.Class != null, "a player rig with look, movement and class is available");
            if (rig == null || rig.Look == null || rig.Movement == null || rig.Class == null) return;

            Vector3 savedPosition = rig.transform.position;
            bool savedCrouch = rig.Movement.IsCrouched;
            try
            {
                Vector3 root = Origin + new Vector3(30f, 0f, 0f);
                rig.transform.position = root;
                Physics.SyncTransforms();
                rig.Movement.SetCrouched(false);
                rig.SetGuardHeld(false);
                rig.Health.Configure(100000, 0f, 0f);

                float standHead = HeadCentreY(rig);
                Near(root.y + 1.68f, standHead, 0.001f, "standing: the head volume centre is at 1.68 m");
                Near(1f, rig.Look.HitVolumeHeightRatio, 1e-6f, "standing: the hit volume height ratio is exactly 1");

                DamageInfo info = ShootRig(rig, root, 1.68f, out bool standingHit);
                Check(standingHit && info.Location == HitLocation.Head && info.Headshot, "standing: an arrow at 1.68 m hits the Head location");

                rig.Movement.SetCrouched(true);
                float expectedRatio = rig.Class.crouchEyeHeight / rig.Class.standEyeHeight;   // class data, not code
                Check(expectedRatio < 0.95f, $"the class has a real crouch (eye {rig.Class.crouchEyeHeight:F2} / {rig.Class.standEyeHeight:F2})");
                float crouchHead = HeadCentreY(rig);
                Near(root.y + 1.68f * expectedRatio, crouchHead, 0.002f, "crouched: the head volume follows the lowered eye");
                Check(crouchHead < standHead - 0.2f, "crouched: the head volume is clearly lower than standing");

                DamageInfo over = ShootRig(rig, root, 1.68f, out bool overHit);
                Check(!overHit, "crouched: a standing-height arrow passes over the player");
                DamageInfo low = ShootRig(rig, root, 1.68f * expectedRatio, out bool lowHit);
                Check(lowHit && low.Location == HitLocation.Head, "crouched: an arrow at the lowered head height hits the Head location");

                rig.Movement.SetCrouched(false);
                Near(standHead, HeadCentreY(rig), 0.001f, "standing again: the head volume is back at 1.68 m");
            }
            finally
            {
                rig.Movement.SetCrouched(savedCrouch);
                rig.transform.position = savedPosition;
                Physics.SyncTransforms();
            }
        }

        private float HeadCentreY(PlayerRig rig)
        {
            List<HitShape> shapes = new List<HitShape>(16);
            rig.CollectHitShapes(shapes);
            for (int i = 0; i < shapes.Count; i++)
            {
                if (shapes[i].Location == HitLocation.Head) return shapes[i].A.y;
            }
            return float.NaN;
        }

        private DamageInfo ShootRig(PlayerRig rig, Vector3 root, float heightAboveRoot, out bool hit)
        {
            DamageInfo captured = DamageInfo.Invalid;
            Action<DamageInfo> listener = damage => captured = damage;
            rig.Health.Configure(100000, 0f, 0f);
            rig.Health.Damaged += listener;
            try { Shoot(root + new Vector3(0f, heightAboveRoot, -8f), Vector3.forward, out _); }
            finally { rig.Health.Damaged -= listener; }
            hit = captured.IsValid;
            return captured;
        }
    }
}
