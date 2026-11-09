using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Emberdelve.Ballistics;
using Emberdelve.Combat;
using Emberdelve.Core;
using Emberdelve.Data;

namespace Emberdelve.EditorTools
{
    /// <summary>
    /// D2 gate: authority-simulated projectiles. Fixed 1/60 s substeps with authored gravity, a wall (and the ground)
    /// stops an arrow, damage falls off with AIR TIME from the weapon's table, and none of it depends on the frame rate.
    ///
    /// Every expected number is computed independently of the authority: the drop and flight time from the closed-form
    /// parabola (<see cref="ProjectileBallistics"/> is the pure rule, the authority only steps it), the falloff from the
    /// literal table values written in this file, and the contact time of a target from a bisection on the closed-form path.
    /// Fixtures stand at (3000, 0, 3000), far outside the dungeon. Entities are analytic hit volumes (no colliders);
    /// only the wall and ground boxes of the wall section are physics colliders, and they live on the level layer.
    ///
    /// Run: Unity -batchmode -nographics -projectPath . -logFile Logs/d2_projectilesim.log
    ///      -executeMethod Emberdelve.EditorTools.EmberdelveProjectileSimGate.Run
    /// Final line: [Emberdelve][ProjectileSim] PASS|FAIL checks=N failures=M
    /// The pure math (integrator, table, aim solver) also has NUnit tests in Tests/EditMode/ProjectileBallisticsTests.cs.
    /// </summary>
    [InitializeOnLoad]
    public static class EmberdelveProjectileSimGate
    {
        private const string Pending = "Emberdelve.ProjectileSimGate.Pending";

        static EmberdelveProjectileSimGate()
        {
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.EnteredPlayMode && SessionState.GetBool(Pending, false))
                {
                    SessionState.SetBool(Pending, false);
                    new GameObject("ProjectileSim_Gate").AddComponent<ProjectileSimHarness>();
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

    public sealed class ProjectileSimHarness : MonoBehaviour
    {
        private const string Tag = "[Emberdelve][ProjectileSim]";
        private const float Dt = ProjectileBallistics.StepSeconds;
        private static readonly Vector3 Origin = new Vector3(3000f, 0f, 3000f);

        private int _checks;
        private int _failures;
        private readonly List<UnityEngine.Object> _cleanup = new List<UnityEngine.Object>();
        private readonly List<ICombatEntity> _registered = new List<ICombatEntity>();

        private LocalCombatAuthority _authority;
        private GameConfig _config;
        private float _clock = 100f;
        private WeaponData _bow;
        private WeaponData _crossbow;
        private SkillData _multi;
        private SkillData _pierce;
        private Trace _current;

        // ---- fixtures -------------------------------------------------------------------------------------

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

        /// <summary>A hostile target made only of analytic hit volumes (no colliders), optionally wearing armor.</summary>
        private sealed class Volumes : ICombatEntity, IHitShapeProvider, IArmorRatingProvider
        {
            public int CombatId { get; set; }
            public EntityKind Kind => EntityKind.Enemy;
            public int TeamId => CombatTeams.PlayerTeam;   // hostile to the monster-team shooter
            public readonly List<HitShape> Shapes = new List<HitShape>();
            public Vector3 Center;
            public float Armor;
            public float Taken;
            public DamageInfo Last = DamageInfo.Invalid;
            public Vector3 CombatCenter => Center;
            public Collider HeadCollider => null;
            public bool IsDead => false;
            public float CurrentHealth => 100000f - Taken;
            public float MaxHealth => 100000f;
            public void ApplyDamage(DamageInfo damage) { Last = damage; Taken += damage.Amount; }
            public void CollectHitShapes(List<HitShape> shapes) { shapes.AddRange(Shapes); }
            public float GetArmorRating() => Armor;
        }

        private sealed class Trace
        {
            public readonly List<Vector3> Positions = new List<Vector3>();
            public readonly List<Vector3> Velocities = new List<Vector3>();
            public ProjectileEndReport End;
            public bool Ended;
            public bool Launched;
        }

        private enum Pump { OneBigDelta, SixtyHertz, Irregular }

        private static readonly float[] IrregularDeltas = { 0.0031f, 0.021f, 0.0167f, 0.0504f, 0.0009f, 0.0333f, 0.0122f };

        private void Check(bool ok, string label)
        {
            _checks++;
            if (ok) return;
            _failures++;
            Debug.LogError($"{Tag} FAIL {label}");
        }

        private void Near(float expected, float actual, float tolerance, string label)
        {
            Check(Mathf.Abs(expected - actual) <= tolerance, $"{label} (expected {expected:F4}, got {actual:F4}, tolerance {tolerance:F4})");
        }

        private T Register<T>(T entity, int id) where T : class, ICombatEntity
        {
            CombatRegistry.Register(id, entity);
            _registered.Add(entity);
            return entity;
        }

        private Volumes Body(Vector3 center, float radius = 0.3f, float armor = 0f)
        {
            Volumes v = new Volumes { CombatId = CombatRegistry.AllocateId(), Center = center, Armor = armor };
            v.Shapes.Add(new HitShape(HitLocation.Body, center, center, radius));
            return Register(v, v.CombatId);
        }

        /// <summary>The standing humanoid of the location gate (head 1.68, torso 0.92-1.334, arms, legs, feet) rooted at <paramref name="root"/>.</summary>
        private Volumes Human(Vector3 root)
        {
            Volumes v = new Volumes { CombatId = CombatRegistry.AllocateId(), Center = root + new Vector3(0f, 1.1f, 0f) };
            v.Shapes.Add(new HitShape(HitLocation.Head, root + new Vector3(0f, 1.68f, 0f), root + new Vector3(0f, 1.68f, 0f), 0.22f));
            v.Shapes.Add(new HitShape(HitLocation.Body, root + new Vector3(0f, 0.92f, 0f), root + new Vector3(0f, 1.334f, 0f), 0.30f));
            for (int side = -1; side <= 1; side += 2)
            {
                v.Shapes.Add(new HitShape(HitLocation.Arms, root + new Vector3(side * 0.396f, 0.966f, 0f), root + new Vector3(side * 0.396f, 1.334f, 0f), 0.092f));
                v.Shapes.Add(new HitShape(HitLocation.Hands, root + new Vector3(side * 0.50f, 0.70f, 0f), root + new Vector3(side * 0.50f, 0.70f, 0f), 0.06f));
                v.Shapes.Add(new HitShape(HitLocation.Legs, root + new Vector3(side * 0.147f, 0.46f, 0f), root + new Vector3(side * 0.147f, 0.736f, 0f), 0.12f));
                v.Shapes.Add(new HitShape(HitLocation.Feet, root + new Vector3(side * 0.147f, 0.11f, 0f), root + new Vector3(side * 0.147f, 0.23f, 0f), 0.11f));
            }
            return Register(v, v.CombatId);
        }

        private void Forget(Volumes v)
        {
            CombatRegistry.Unregister(v.CombatId, v);
            _registered.Remove(v);
        }

        // ---- flying ---------------------------------------------------------------------------------------

        private void OnStep(int serial, int instigator, int step, Vector3 position, Vector3 velocity)
        {
            if (_current == null) return;
            _current.Positions.Add(position);
            _current.Velocities.Add(velocity);
        }

        private void OnEnd(ProjectileEndReport report)
        {
            if (_current == null) return;
            _current.End = report;
            _current.Ended = true;
        }

        private static float MuzzleSpeed(WeaponData weapon) => Mathf.Max(2f, weapon.projectileSpeed);

        /// <summary>Launches one arrow of <paramref name="weapon"/> from <paramref name="from"/> along <paramref name="dir"/> and runs the flight to its end.</summary>
        private Trace Fly(WeaponData weapon, Vector3 from, Vector3 dir, Pump pump = Pump.OneBigDelta)
        {
            _clock += 10f;
            Shooter shooter = new Shooter { CombatId = CombatRegistry.AllocateId(), ActiveWeapon = weapon, AimOrigin = from, AimDirection = dir.normalized };
            CombatRegistry.Register(shooter.CombatId, shooter);
            Trace trace = new Trace();
            _current = trace;
            try
            {
                AttackIntent intent = new AttackIntent(shooter.CombatId, shooter.Kind, weapon, 1f, from, shooter.AimDirection,
                    weapon.rangedRange, CombatRegistry.NextSequence(shooter.CombatId), _clock);
                trace.Launched = _authority.LaunchProjectileAsOwner(intent);
                if (!trace.Launched) return trace;

                switch (pump)
                {
                    case Pump.OneBigDelta:
                        _authority.AdvanceProjectiles(4f);
                        break;
                    case Pump.SixtyHertz:
                        for (int i = 0; i < 600 && _authority.ActiveProjectileCount > 0; i++) _authority.AdvanceProjectiles(1f / 60f);
                        break;
                    default:
                        for (int i = 0; i < 4000 && _authority.ActiveProjectileCount > 0; i++)
                            _authority.AdvanceProjectiles(IrregularDeltas[i % IrregularDeltas.Length]);
                        break;
                }
                return trace;
            }
            finally
            {
                CombatRegistry.Unregister(shooter.CombatId, shooter);
                _current = null;
            }
        }

        private static Vector3 Level => Vector3.forward;

        private static Vector3 Elevated(float pitchRadians) =>
            new Vector3(0f, Mathf.Sin(pitchRadians), Mathf.Cos(pitchRadians));

        /// <summary>Drop-compensated direction to a point <paramref name="horizontal"/> m ahead (+z) and <paramref name="heightDelta"/> m above the muzzle.</summary>
        private Vector3 Compensated(WeaponData weapon, float horizontal, float heightDelta, string label)
        {
            bool solved = ProjectileBallistics.SolveLowPitch(MuzzleSpeed(weapon), weapon.projectileGravity, horizontal, heightDelta, out float pitch);
            Check(solved, $"{label}: a low-arc aim solution exists at {horizontal:F0} m");
            return Elevated(solved ? pitch : 0f);
        }

        /// <summary>Earliest time (s) at which the closed-form path comes within <paramref name="reach"/> of <paramref name="center"/>; -1 if never within 3 s.</summary>
        private static float ContactTime(Vector3 start, Vector3 velocity, float gravity, Vector3 center, float reach)
        {
            BallisticState s0 = new BallisticState(start.x, start.y, start.z, velocity.x, velocity.y, velocity.z);
            Func<float, float> gap = t =>
            {
                BallisticState p = ProjectileBallistics.PositionAt(s0, gravity, t);
                return Vector3.Distance(new Vector3(p.PX, p.PY, p.PZ), center) - reach;
            };
            for (float t = 0f; t < 3f; t += 0.001f)
            {
                if (gap(t) > 0f) continue;
                float lo = Mathf.Max(0f, t - 0.001f);
                float hi = t;
                for (int i = 0; i < 40; i++)
                {
                    float mid = (lo + hi) * 0.5f;
                    if (gap(mid) <= 0f) hi = mid; else lo = mid;
                }
                return hi;
            }
            return -1f;
        }

        // ---- the gate -------------------------------------------------------------------------------------

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
                if (_authority != null)
                {
                    _authority.ProjectileStepped -= OnStep;
                    _authority.ProjectileEnded -= OnEnd;
                }
                foreach (ICombatEntity e in _registered) CombatRegistry.Unregister(e.CombatId, e);
                foreach (UnityEngine.Object o in _cleanup) if (o != null) Destroy(o);
            }
            Debug.Log($"{Tag} {(_failures == 0 ? "PASS" : "FAIL")} checks={_checks} failures={_failures}");
            EmberdelveGateExit.Exit(_failures == 0 ? 0 : 1);
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
            // Fixtures of one section stand where the next section's do: take them out of the registry.
            foreach (ICombatEntity e in new List<ICombatEntity>(_registered)) CombatRegistry.Unregister(e.CombatId, e);
            _registered.Clear();
            Debug.Log($"{Tag} section '{name}': {_checks - checksBefore} checks, {_failures - failuresBefore} failures");
        }

        private void Execute()
        {
            _bow = AssetDatabase.LoadAssetAtPath<WeaponData>(EmberdelvePaths.Data + "/Weapon_HollowBow.asset");
            _crossbow = AssetDatabase.LoadAssetAtPath<WeaponData>(EmberdelvePaths.Data + "/Weapon_EmberCrossbow.asset");
            _multi = AssetDatabase.LoadAssetAtPath<SkillData>(EmberdelvePaths.Data + "/Skill_ArcherMultiShot.asset");
            _pierce = AssetDatabase.LoadAssetAtPath<SkillData>(EmberdelvePaths.Data + "/Skill_ArcherPiercingShot.asset");
            Check(_bow != null && _bow.IsRanged && _crossbow != null && _crossbow.IsRanged && _multi != null && _pierce != null,
                "bow, crossbow, Multi-shot and Piercing Shot assets are available");
            if (_bow == null || _crossbow == null || _multi == null || _pierce == null) return;

            int levelLayer = LayerMask.NameToLayer(GameConfig.LevelLayerName);
            if (levelLayer < 0) levelLayer = 0;
            _config = ScriptableObject.CreateInstance<GameConfig>();
            _cleanup.Add(_config);
            _config.hitMask = 0;                   // entities are analytic volumes: no physics query finds a body
            _config.levelMask = 1 << levelLayer;   // only the wall and ground boxes of the wall section are level geometry
            _config.hitLocations = null;           // the code-default table; expectations below are literals
            GameObject go = new GameObject("ProjectileSimGate_Authority");
            _cleanup.Add(go);
            _authority = go.AddComponent<LocalCombatAuthority>();
            typeof(LocalCombatAuthority).GetField("config", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(_authority, _config);
            _authority.SetClock(() => _clock);
            _authority.ProjectileStepped += OnStep;
            _authority.ProjectileEnded += OnEnd;
            Check(_authority.SimulateBallistics, "the authority simulates ballistics by default");

            Section("authored data", AuthoredData);
            Section("drop below the aim line", Drop);
            Section("time of flight", TimeOfFlight);
            Section("range and flight-time caps", Caps);
            Section("determinism and frame-rate independence", Determinism);
            Section("falloff by air time", FalloffByAirTime);
            Section("crossbow flatter than bow", CrossbowVsBow);
            Section("damage formula position", FormulaPosition);
            Section("first touch location with drop compensated aim", LocationAtRange);
            Section("walls and ground", Walls);
            Section("ballistics off is the legacy flight", LegacyMode);
        }

        // ------------------------------------------------------------------------------------------------

        private void AuthoredData()
        {
            Near(42f, _bow.projectileSpeed, 1e-4f, "bow muzzle speed 42 m/s (unchanged)");
            Near(9.81f, _bow.projectileGravity, 1e-4f, "bow gravity 9.81 m/s^2");
            Near(1.6f, _bow.projectileMaxFlightTime, 1e-4f, "bow maximum flight time 1.6 s");
            Near(54f, _crossbow.projectileSpeed, 1e-4f, "crossbow muzzle speed 54 m/s (unchanged)");
            Near(9.81f, _crossbow.projectileGravity, 1e-4f, "crossbow gravity 9.81 m/s^2");
            Near(1.2f, _crossbow.projectileMaxFlightTime, 1e-4f, "crossbow maximum flight time 1.2 s");
            Check(_crossbow.projectileSpeed > _bow.projectileSpeed && Mathf.Abs(_crossbow.projectileGravity - _bow.projectileGravity) < 1e-4f,
                "the crossbow is faster under the same gravity as the bow");
            Check(_bow.HasAirTimeFalloff && _crossbow.HasAirTimeFalloff, "bow and crossbow carry an air-time falloff table");
            Check(ProjectileBallistics.IsValidTable(_bow.projectileFalloff) && ProjectileBallistics.IsValidTable(_crossbow.projectileFalloff),
                "both tables ascend by time with non-negative scales");
            // literal table values (not read back from the asset's own evaluation of itself)
            Near(1.00f, _bow.AirTimeFalloffScale(0f), 1e-5f, "bow table at 0 s is 1.00");
            Near(1.00f, _bow.AirTimeFalloffScale(0.3f), 1e-5f, "bow table at 0.3 s is 1.00");
            Near(0.70f, _bow.AirTimeFalloffScale(0.5f), 1e-5f, "bow table at 0.5 s is 0.70 (DnD recurve reference)");
            Near(0.45f, _bow.AirTimeFalloffScale(1.1f), 1e-5f, "bow table at 1.1 s is 0.45");
            Near(0.45f, _bow.AirTimeFalloffScale(5f), 1e-5f, "bow table stays at 0.45 after its last key");
            Near(0.90f, _crossbow.AirTimeFalloffScale(0.5f), 1e-5f, "crossbow table at 0.5 s is 0.90 (less falloff than the bow)");
            Near(0.75f, _crossbow.AirTimeFalloffScale(0.9f), 1e-5f, "crossbow table at 0.9 s is 0.75");
            Check(_crossbow.AirTimeFalloffScale(0.5f) > _bow.AirTimeFalloffScale(0.5f), "the crossbow loses less at 0.5 s");
            Near(1f, _multi.projectileGravityMultiplier, 1e-5f, "Multi-shot flies with the weapon's gravity (x1)");
            Near(1f, _multi.projectileSpeedMultiplier, 1e-5f, "Multi-shot flies at the weapon's speed (x1)");
            Near(0.75f, _pierce.projectileGravityMultiplier, 1e-5f, "Piercing Shot is a heavier, flatter arrow (gravity x0.75)");
            Near(1f, _pierce.projectileSpeedMultiplier, 1e-5f, "Piercing Shot flies at the weapon's speed (x1)");
        }

        private void Drop()
        {
            Vector3 from = Origin + new Vector3(0f, 1.5f, 0f);
            Trace t = Fly(_bow, from, Level);
            Check(t.Launched && t.Ended && t.Positions.Count > 50, $"the bow flight launches, steps and ends ({t.Positions.Count} steps)");
            if (!t.Launched || t.Positions.Count < 50) return;

            float speed = MuzzleSpeed(_bow);
            float g = _bow.projectileGravity;
            foreach (float aim in new[] { 20f, 40f })
            {
                int i = FirstStepAtDistance(t, from, aim);
                Check(i >= 0, $"the arrow reaches {aim:F0} m");
                if (i < 0) continue;
                float z = t.Positions[i].z - from.z;
                float time = (i + 1) * Dt;
                float expected = ProjectileBallistics.DropBelowAimLine(speed, g, z);
                float drop = from.y - t.Positions[i].y;
                // Tolerance from the step size: even a semi-implicit Euler step would be off by only g*dt*t/2; this integrator is exact.
                float tolerance = 0.5f * g * Dt * time + 0.005f;
                Near(expected, drop, tolerance, $"drop below the aim line at {z:F1} m of aim line ({time:F3} s)");
                Check(drop > 0.5f, $"the {aim:F0} m shot is clearly below the aim line ({drop:F2} m), not hitscan");
            }
            Near(2.7211f, ProjectileBallistics.DropBelowAimLine(42f, 6f, 40f), 1e-3f, "closed form: a 40 m aim line at 42 m/s and 6 m/s^2 drops 2.72 m");

            // the whole trajectory equals the closed-form parabola
            BallisticState start = new BallisticState(from.x, from.y, from.z, 0f, 0f, speed);
            float worst = 0f;
            for (int k = 0; k < t.Positions.Count; k++)
            {
                BallisticState p = ProjectileBallistics.PositionAt(start, g, (k + 1) * Dt);
                worst = Mathf.Max(worst, Vector3.Distance(t.Positions[k], new Vector3(p.PX, p.PY, p.PZ)));
            }
            Check(worst < 0.01f, $"every simulated step lies on the closed-form parabola (worst deviation {worst * 1000f:F2} mm)");
        }

        private static int FirstStepAtDistance(Trace t, Vector3 from, float aimDistance)
        {
            for (int i = 0; i < t.Positions.Count; i++)
                if (t.Positions[i].z - from.z >= aimDistance) return i;
            return -1;
        }

        private void TimeOfFlight()
        {
            Vector3 from = Origin + new Vector3(0f, 1.5f, 0f);
            Trace bow = Fly(_bow, from, Level);
            Trace crossbow = Fly(_crossbow, from, Level);
            Check(bow.Launched && crossbow.Launched, "bow and crossbow flights launch");
            foreach (float d in new[] { 20f, 40f })
            {
                int i = FirstStepAtDistance(bow, from, d);
                Check(i >= 0, $"bow reaches {d:F0} m");
                if (i >= 0)
                    Near(ProjectileBallistics.TimeAlongAimLine(42f, d), (i + 1) * Dt, Dt + 1e-4f, $"bow time of flight to {d:F0} m is {d}/42 s, to within one step");
            }
            foreach (float d in new[] { 20f, 30f })
            {
                int i = FirstStepAtDistance(crossbow, from, d);
                Check(i >= 0, $"crossbow reaches {d:F0} m");
                if (i >= 0)
                    Near(ProjectileBallistics.TimeAlongAimLine(54f, d), (i + 1) * Dt, Dt + 1e-4f, $"crossbow time of flight to {d:F0} m is {d}/54 s, to within one step");
            }
            Near(20f / 42f, ProjectileBallistics.TimeAlongAimLine(42f, 20f), 1e-5f, "closed form: 20 m at 42 m/s is 0.476 s");
            Near(40f / 42f, ProjectileBallistics.TimeAlongAimLine(42f, 40f), 1e-5f, "closed form: 40 m at 42 m/s is 0.952 s");
        }

        private void Caps()
        {
            Vector3 from = Origin + new Vector3(0f, 1.5f, 0f);
            Trace full = Fly(_bow, from, Level);
            Check(full.Ended && full.End.Reason == ProjectileEndReason.RangeExhausted, "an unobstructed bow arrow ends by exhausting its path-length range");
            Near(_bow.rangedRange, full.End.Traveled, 0.02f, "...after exactly Ranged Range metres of path");
            Check(full.End.AirTime <= _bow.projectileMaxFlightTime, "...inside the maximum flight time");

            WeaponData slow = Instantiate(_bow);
            _cleanup.Add(slow);
            slow.projectileMaxFlightTime = 0.5f;
            Trace timed = Fly(slow, from, Level);
            Check(timed.Ended && timed.End.Reason == ProjectileEndReason.TimeExpired, "a weapon with a 0.5 s flight-time cap ends by TimeExpired");
            Check(timed.End.Steps == 30, $"...after exactly 30 steps = 0.5 s ({timed.End.Steps})");

            WeaponData noGravity = Instantiate(_bow);
            _cleanup.Add(noGravity);
            noGravity.projectileGravity = 0f;
            Trace flat = Fly(noGravity, from, Level);
            Check(flat.Ended && flat.Positions.Count > 10, "a weapon with no gravity still flies");
            float flatDrop = 0f;
            for (int i = 0; i < flat.Positions.Count; i++) flatDrop = Mathf.Max(flatDrop, Mathf.Abs(flat.Positions[i].y - from.y));
            Check(flatDrop < 1e-3f, $"...and stays on the aim line (max deviation {flatDrop * 1000f:F2} mm)");
        }

        private void Determinism()
        {
            Vector3 from = Origin + new Vector3(0.3f, 1.4f, 0f);
            Vector3 dir = Elevated(0.04f);
            Trace a = Fly(_bow, from, dir, Pump.OneBigDelta);
            Trace b = Fly(_bow, from, dir, Pump.OneBigDelta);
            Trace c = Fly(_bow, from, dir, Pump.SixtyHertz);
            Trace d = Fly(_bow, from, dir, Pump.Irregular);
            Check(a.Ended && b.Ended && c.Ended && d.Ended && a.Positions.Count > 50, $"four runs of the same shot all end ({a.Positions.Count} steps)");
            Check(Identical(a, b), "two runs with the same inputs are bit-identical (every step position and velocity, and the end)");
            Check(Identical(a, c), "a run advanced one 1/60 s frame at a time is bit-identical to one advanced in a single 4 s delta");
            Check(Identical(a, d), "a run advanced in irregular frame times (3 ms .. 50 ms) is bit-identical too: the trajectory does not depend on the frame rate");
        }

        private static bool Identical(Trace x, Trace y)
        {
            if (x.Positions.Count != y.Positions.Count || x.Ended != y.Ended) return false;
            for (int i = 0; i < x.Positions.Count; i++)
                if (x.Positions[i] != y.Positions[i] || x.Velocities[i] != y.Velocities[i]) return false;
            return x.End.Steps == y.End.Steps && x.End.Reason == y.End.Reason && x.End.Position == y.End.Position
                && x.End.Traveled == y.End.Traveled;
        }

        /// <summary>
        /// A body sphere placed so a level shot touches it at an air time of about <paramref name="airTime"/>; returns the
        /// damage the target took, the contact time computed independently on the closed-form path, and the shot.
        /// </summary>
        private void ShotAtAirTime(WeaponData weapon, float airTime, out float contact, out float taken, out Trace trace)
        {
            Vector3 from = Origin + new Vector3(0f, 1.5f, 0f);
            float speed = MuzzleSpeed(weapon);
            BallisticState start = new BallisticState(from.x, from.y, from.z, 0f, 0f, speed);
            float reach = 0.3f + weapon.projectileRadius * 0.5f;
            BallisticState at = ProjectileBallistics.PositionAt(start, weapon.projectileGravity, airTime + reach / speed);
            Vector3 center = new Vector3(at.PX, at.PY, at.PZ);
            Volumes target = Body(center);
            contact = ContactTime(from, new Vector3(0f, 0f, speed), weapon.projectileGravity, center, reach);
            trace = Fly(weapon, from, Level);
            taken = target.Taken;
            Forget(target);
        }

        private void FalloffByAirTime()
        {
            ShotAtAirTime(_bow, 0.5f, out float contact, out float taken, out Trace trace);
            Check(trace.Ended && trace.End.Reason == ProjectileEndReason.Body, "the bow arrow stops in the body");
            Near(0.5f, contact, 0.003f, "fixture: the bow arrow touches the body at an air time of 0.5 s");
            Near(34f * 0.7f, taken, 0.15f, "bow damage at 0.5 s of air time is base 34 x 0.70 = 23.8 (literal table value)");
            Near(34f * _bow.AirTimeFalloffScale(contact), taken, 0.06f, "...and equals base x the table at the independently computed contact time");
            float legacy = _bow.DamageAtRange(Vector3.Distance(trace.End.Origin, trace.End.ImpactPoint));
            Check(Mathf.Abs(legacy - taken) > 2f, $"...and is NOT the legacy range falloff ({legacy:F1}): falloff follows air time, not distance");

            ShotAtAirTime(_crossbow, 0.5f, out contact, out taken, out trace);
            Near(0.5f, contact, 0.003f, "fixture: the crossbow bolt touches the body at an air time of 0.5 s");
            Near(24f * 0.9f, taken, 0.1f, "crossbow damage at 0.5 s is base 24 x 0.90 = 21.6");

            ShotAtAirTime(_bow, 0.2f, out contact, out taken, out trace);
            Near(34f, taken, 0.06f, "bow damage at 0.2 s is the full base 34 (inside the plateau)");

            ShotAtAirTime(_bow, 0.9f, out contact, out taken, out trace);
            Near(34f * (0.7f + (0.45f - 0.7f) * ((contact - 0.5f) / 0.6f)), taken, 0.1f, "bow damage at 0.9 s is on the 0.5 -> 1.1 s segment of the table");

            // The same air time at a different distance gives the same factor: a faster bolt flown over a longer distance in 0.5 s
            // takes the same scale, a slow one over a shorter distance too (falloff is by time).
            WeaponData slowBow = Instantiate(_bow);
            _cleanup.Add(slowBow);
            slowBow.projectileSpeed = 21f;
            slowBow.rangedRange = 30f;
            ShotAtAirTime(slowBow, 0.5f, out contact, out taken, out trace);
            Near(0.5f, contact, 0.004f, "fixture: a half-speed bow arrow touches the body at 0.5 s (about 10 m away)");
            Near(34f * 0.7f, taken, 0.2f, "...and takes the same x0.70 as the full-speed arrow that flew twice the distance");
        }

        private void CrossbowVsBow()
        {
            Vector3 from = Origin + new Vector3(0f, 1.5f, 0f);
            Trace bow = Fly(_bow, from, Level);
            Trace crossbow = Fly(_crossbow, from, Level);
            int ib = FirstStepAtDistance(bow, from, 30f);
            int ic = FirstStepAtDistance(crossbow, from, 30f);
            Check(ib >= 0 && ic >= 0, "both reach 30 m");
            if (ib < 0 || ic < 0) return;
            float bowDrop = from.y - bow.Positions[ib].y;
            float crossbowDrop = from.y - crossbow.Positions[ic].y;
            Check(crossbowDrop < bowDrop, $"the crossbow is flatter: {crossbowDrop:F2} m of drop at 30 m against the bow's {bowDrop:F2} m");
            Near((42f / 54f) * (42f / 54f), crossbowDrop / bowDrop, .03f, "drop ratio follows squared muzzle-speed ratio under shared gravity");
            Check((ic + 1) * Dt < (ib + 1) * Dt, $"...and gets there sooner ({(ic + 1) * Dt:F3} s against {(ib + 1) * Dt:F3} s)");
            Near(ProjectileBallistics.DropBelowAimLine(54f, 9.81f, crossbow.Positions[ic].z - from.z), crossbowDrop, 0.5f * 9.81f * Dt * (ic + 1) * Dt + 0.005f,
                "the crossbow's drop equals the closed form for 54 m/s and 9.81 m/s^2");
        }

        private void FormulaPosition()
        {
            // Direct resolver checks of where the falloff enters: after location and the defender's armor, as the last factor.
            Volumes plain = Body(Origin);
            Volumes armored = Body(Origin, 0.3f, 100f);   // rating 100 -> x100/(100+100) = 0.5
            AttackIntent attack = new AttackIntent(1, EntityKind.Enemy, _bow, 1f, Vector3.zero, Vector3.forward, _bow.rangedRange, 1, 0f);
            HitCandidate hitPlain = new HitCandidate(plain.CombatId, new Vector3(0f, 0f, 8f), false);
            HitCandidate hitArmored = new HitCandidate(armored.CombatId, new Vector3(0f, 0f, 8f), false);

            DamageInfo none = DamageResolver.Resolve(_bow, attack, hitPlain, plain, 1f, 0f, null, true, false, HitLocation.Body, 1f);
            Near(34f, none.Amount, 0.001f, "no air-time scale: the legacy range rule applies (8 m is inside the plateau): 34");
            DamageInfo scaled = DamageResolver.Resolve(_bow, attack, hitPlain, plain, 1f, 0f, null, true, false, HitLocation.Body, 1f, 0.7f);
            Near(34f * 0.7f, scaled.Amount, 0.001f, "air-time scale 0.7: 34 x 0.7 = 23.8");
            DamageInfo head = DamageResolver.Resolve(_bow, attack, hitPlain, plain, 1f, 0f, null, true, false, HitLocation.Head, 1.5f, 0.7f);
            Near(34f * 1.5f * 0.7f, head.Amount, 0.001f, "location Head x1.5 and falloff 0.7: 35.7");
            Check(head.Headshot && head.Location == HitLocation.Head && head.RangedProjectile, "...the hit keeps its Head location, headshot and projectile flags");
            // armor mitigates Physical damage only (ARMOR_RULES.md) and the bow is Pierce, so use a Physical copy for the armor case
            WeaponData physicalBow = Instantiate(_bow);
            _cleanup.Add(physicalBow);
            physicalBow.damageType = DamageType.Physical;
            AttackIntent physicalAttack = new AttackIntent(1, EntityKind.Enemy, physicalBow, 1f, Vector3.zero, Vector3.forward, physicalBow.rangedRange, 1, 0f);
            DamageInfo armorOnly = DamageResolver.Resolve(physicalBow, physicalAttack, hitArmored, armored, 1f, 0f, null, true, false, HitLocation.Body, 1f);
            Near(34f * 0.5f, armorOnly.Amount, 0.001f, "armor 100 halves a Physical hit: 17");
            DamageInfo armor = DamageResolver.Resolve(physicalBow, physicalAttack, hitArmored, armored, 1f, 0f, null, true, false, HitLocation.Body, 1f, 0.7f);
            Near(34f * 0.5f * 0.7f, armor.Amount, 0.001f, "armor 100 (x0.5) and falloff 0.7 multiply: 11.9");
            DamageInfo gone = DamageResolver.Resolve(_bow, attack, hitPlain, plain, 1f, 0f, null, true, false, HitLocation.Body, 1f, 0f);
            Near(1f, gone.Amount, 0.001f, "a falloff of 0 still leaves the 1.0 damage floor");
            DamageInfo notProjectile = DamageResolver.Resolve(_bow, attack, hitPlain, plain, 1f, 0f, null, false, false, HitLocation.Unspecified, 1f, 0.7f);
            Near(34f, notProjectile.Amount, 0.001f, "an air-time scale is ignored for a non-projectile resolution");

            SkillData weaponSkill = Instantiate(_pierce);
            _cleanup.Add(weaponSkill);
            weaponSkill.flatDamage = -1f;
            weaponSkill.damageMultiplier = 1.35f;
            SkillIntent skillIntent = new SkillIntent(1, EntityKind.Enemy, weaponSkill, _bow, Vector3.zero, Vector3.forward, 8f, 1, 0f, default, 1, 1f);
            DamageInfo skill = DamageResolver.ResolveSkill(skillIntent, hitPlain, plain, 1f, 1f, 0f, true, false, HitLocation.Body, 1f, 0.7f);
            Near(34f * 1.35f * 0.7f, skill.Amount, 0.001f, "a weapon-based projectile skill takes the weapon's air-time falloff: 34 x 1.35 x 0.7 = 32.13");
            SkillData flat = Instantiate(_pierce);
            _cleanup.Add(flat);
            flat.flatDamage = 14f;
            SkillIntent flatIntent = new SkillIntent(1, EntityKind.Enemy, flat, _bow, Vector3.zero, Vector3.forward, 8f, 1, 0f, default, 1, 1f);
            DamageInfo flatHit = DamageResolver.ResolveSkill(flatIntent, hitPlain, plain, 1f, 1f, 0f, true, false, HitLocation.Body, 1f, 0.7f);
            Near(14f, flatHit.Amount, 0.001f, "a flat-damage projectile skill has no falloff at all");
        }

        private void LocationAtRange()
        {
            Vector3 muzzle = Origin + new Vector3(0f, 1.5f, 0f);
            Vector3 root = Origin + new Vector3(0f, 0f, 30f);
            Volumes human = Human(root);
            float speed = MuzzleSpeed(_bow);

            // Head: aim compensated for drop at the head's centre (1.68 m, i.e. 0.18 m above the muzzle).
            Vector3 headAim = Compensated(_bow, 30f, 0.18f, "head shot");
            human.Last = DamageInfo.Invalid;
            Trace t = Fly(_bow, muzzle, headAim);
            Check(human.Last.IsValid && human.Taken > 0f, "the drop-compensated arrow hits the humanoid at 30 m");
            Check(human.Last.Location == HitLocation.Head && human.Last.Headshot, $"...on the HEAD: first touch decides, as in D1c3 ({human.Last.Location})");
            float contact = ContactTime(muzzle, headAim * speed, _bow.projectileGravity, root + new Vector3(0f, 1.68f, 0f), 0.22f + _bow.projectileRadius * 0.5f);
            Near(34f * 1.5f * _bow.AirTimeFalloffScale(contact), human.Last.Amount, 0.15f,
                $"...for base x 1.5 x the air-time falloff at the contact time {contact:F3} s");
            Check(t.Ended && t.End.Reason == ProjectileEndReason.Body, "...and the arrow ended in the body");

            // Body: same range, aim compensated for the chest.
            Vector3 bodyAim = Compensated(_bow, 30f, -0.37f, "body shot");
            human.Last = DamageInfo.Invalid;
            Fly(_bow, muzzle, bodyAim);
            Check(human.Last.IsValid && human.Last.Location == HitLocation.Body && !human.Last.Headshot,
                $"aimed at the chest with compensation the arrow takes the BODY location ({human.Last.Location})");

            // Legs: a compensated shot from beside the centre line at leg height meets the leg capsule.
            Vector3 legMuzzle = muzzle + new Vector3(0.147f, 0f, 0f);
            Vector3 legAim = Compensated(_bow, 30f, 0.5f - 1.5f, "leg shot");
            human.Last = DamageInfo.Invalid;
            Fly(_bow, legMuzzle, legAim);
            Check(human.Last.IsValid && human.Last.Location == HitLocation.Legs,
                $"aimed at the leg with compensation the arrow takes the LEGS location ({human.Last.Location})");

            // Without compensation the arrow falls ~1.5 m under the aim point: it is not a headshot.
            human.Last = DamageInfo.Invalid;
            Trace straight = Fly(_bow, muzzle, new Vector3(0f, 0.18f / 30f, 1f));
            Check(!human.Last.IsValid || human.Last.Location != HitLocation.Head, "the same shot with no drop compensation does not take the head");
            int iz = FirstStepAtDistance(straight, muzzle, 29.5f);
            Check(iz >= 0 && straight.Positions[iz].y < 1.68f - 1f, "...it is more than a metre below the head when it reaches the humanoid's distance");
        }

        private void Walls()
        {
            Vector3 muzzle = Origin + new Vector3(0f, 1.5f, 0f);
            int levelLayer = Mathf.Max(0, LayerMask.NameToLayer(GameConfig.LevelLayerName));

            // control: a compensated arrow at a body 25 m away hits it when nothing is in the way
            Volumes target = Body(Origin + new Vector3(0f, 1.5f, 25f));
            Vector3 aim = Compensated(_bow, 25f, 0f, "wall scenario");
            Trace control = Fly(_bow, muzzle, aim);
            Check(control.Ended && control.End.Reason == ProjectileEndReason.Body && target.Taken > 0f, "control: with a clear lane the compensated arrow hits the body at 25 m");

            // a 0.5 m thick wall 15 m ahead
            GameObject wall = new GameObject("ProjectileSim_Wall");
            _cleanup.Add(wall);
            wall.layer = levelLayer;
            BoxCollider box = wall.AddComponent<BoxCollider>();
            box.size = new Vector3(12f, 14f, 0.5f);
            wall.transform.position = Origin + new Vector3(0f, 3f, 15f);
            Physics.SyncTransforms();
            float frontFace = Origin.z + 15f - 0.25f;
            target.Taken = 0f;
            target.Last = DamageInfo.Invalid;
            Trace blocked = Fly(_bow, muzzle, aim);
            Check(blocked.Ended && blocked.End.Reason == ProjectileEndReason.Wall, $"a wall between shooter and target stops the arrow ({blocked.End.Reason})");
            Check(target.Taken == 0f && !target.Last.IsValid, "NO hit through the wall: the target behind it took nothing");
            Near(frontFace, blocked.End.ImpactPoint.z, 0.01f, "the impact point is on the wall's front face");
            Near(frontFace - _bow.projectileRadius * 0.5f, blocked.End.Position.z, 0.02f, "the arrow's last position is one shaft radius in front of it");
            Check(blocked.End.TargetId < 0, "the flight records no target");

            // a 5 cm wall is not tunnelled by the fast crossbow bolt (about 0.9 m per step)
            box.size = new Vector3(12f, 14f, 0.05f);
            Physics.SyncTransforms();
            float thinFront = Origin.z + 15f - 0.025f;
            Vector3 boltAim = Compensated(_crossbow, 25f, 0f, "thin wall");
            Trace thin = Fly(_crossbow, muzzle, boltAim);
            Check(thin.Ended && thin.End.Reason == ProjectileEndReason.Wall && target.Taken == 0f,
                $"a 5 cm wall stops a crossbow bolt that moves 0.9 m per step: no tunnelling ({thin.End.Reason})");
            Near(thinFront, thin.End.ImpactPoint.z, 0.01f, "...at the thin wall's face");

            // remove the wall: the arrow flies on to the target again (the wall really was the only difference)
            wall.SetActive(false);
            Physics.SyncTransforms();
            Fly(_bow, muzzle, aim);
            Check(target.Taken > 0f, "with the wall gone the same shot hits the target");

            // the ground: a level shot from 1.5 m falls onto it where the parabola says
            GameObject ground = new GameObject("ProjectileSim_Ground");
            _cleanup.Add(ground);
            ground.layer = levelLayer;
            BoxCollider floor = ground.AddComponent<BoxCollider>();
            floor.size = new Vector3(400f, 1f, 400f);
            ground.transform.position = Origin + new Vector3(0f, -0.5f, 100f);
            Physics.SyncTransforms();
            Forget(target);
            Trace fall = Fly(_bow, muzzle, Level);
            float r = _bow.projectileRadius * 0.5f;
            float tImpact = Mathf.Sqrt(2f * (1.5f - r) / _bow.projectileGravity);
            Check(fall.Ended && fall.End.Reason == ProjectileEndReason.Wall, $"a level arrow drops onto the ground ({fall.End.Reason})");
            Near(0f, fall.End.ImpactPoint.y, 0.02f, "...and lands on the ground plane");
            Near(MuzzleSpeed(_bow) * tImpact, fall.End.ImpactPoint.z - muzzle.z, 0.5f, $"...{MuzzleSpeed(_bow) * tImpact:F1} m downrange, where the parabola reaches the floor");
        }

        private void LegacyMode()
        {
            Vector3 from = Origin + new Vector3(0f, 1.5f, 0f);
            Volumes target = Body(Origin + new Vector3(0f, 1.5f, 25f));
            _authority.SimulateBallistics = false;
            try
            {
                Trace t = Fly(_bow, from, Level);
                Check(t.Launched && t.Ended && t.End.Reason == ProjectileEndReason.Body, "with ballistics off a level arrow still flies straight into a body 25 m away");
                float worst = 0f;
                for (int i = 0; i < t.Positions.Count; i++) worst = Mathf.Max(worst, Mathf.Abs(t.Positions[i].y - from.y));
                Check(worst < 1e-3f, $"...with no drop at all (max deviation {worst * 1000f:F2} mm)");
                float distance = Vector3.Distance(target.Last.SourcePosition, target.Last.Point);
                Near(_bow.DamageAtRange(distance), target.Last.Amount, 0.02f, "...and the legacy RANGE falloff applies");
            }
            finally { _authority.SimulateBallistics = true; }
            Check(_authority.SimulateBallistics, "ballistics are switched back on");
        }
    }
}
