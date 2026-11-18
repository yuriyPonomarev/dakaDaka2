using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Emberdelve.AI;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Player;
using Emberdelve.Combat;

namespace Emberdelve.EditorTools
{
    /// <summary>
    /// D1a gate: the swept-weapon melee. The authority owns the swing, so every case here talks to a real
    /// LocalCombatAuthority on an injected clock (stepped by hand, no wall-clock sleeps) with plain fake
    /// entities - an attacker that only has a pose, targets that only have hit shapes - and a few real level
    /// colliders for the wall cases. The last section drives the real PlayerRig / MeleeCombat / WeaponVisualRig
    /// in real time to prove the thin client and the visual follow the same data.
    ///
    /// Run: Unity -batchmode -nographics -projectPath . -logFile Logs/d1a_sweptmelee.log
    ///      -executeMethod Emberdelve.EditorTools.EmberdelveSweptMeleeGate.Run
    /// </summary>
    [InitializeOnLoad]
    public static class EmberdelveSweptMeleeGate
    {
        private const string Pending = "Emberdelve.SweptMeleeGate.Pending";

        static EmberdelveSweptMeleeGate()
        {
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.EnteredPlayMode && SessionState.GetBool(Pending, false))
                {
                    SessionState.SetBool(Pending, false);
                    new GameObject("SweptMelee_Gate").AddComponent<SweptMeleeHarness>();
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

    public sealed class SweptMeleeHarness : MonoBehaviour
    {
        private const string Tag = "[SweptMeleeGate]";

        private int _checks;
        private int _failures;
        private readonly List<UnityEngine.Object> _cleanup = new List<UnityEngine.Object>();
        private readonly List<IDisposable> _scenarioCleanup = new List<IDisposable>();

        private LocalCombatAuthority _authority;
        private GameConfig _config;
        private float _clock = 1000f;

        /// <summary>
        /// Every scenario is built around this point, far outside the dungeon, so the real level geometry (the
        /// authority's wall mask is the real Emv_Level layer) can never touch a swing the gate did not set up.
        /// Test coordinates are local: x right, y up, z forward from this origin.
        /// </summary>
        private static readonly Vector3 Origin = new Vector3(3000f, 0f, 3000f);

        private WeaponData _sword;
        private WeaponData _rapier;
        private WeaponData _crossbow;
        private WeaponData _claw;
        private HitLocationTable _table;

        private const int Slash = 1;       // sword patterns (Dark and Darker order): 0 = right to left, 1 = left to right, 2 = overhead
        private const int SlashBack = 0;
        private const int Overhead = 2;

        // ------------------------------------------------------------------------------------------------
        // Fakes
        // ------------------------------------------------------------------------------------------------

        /// <summary>An attacker that only has a pose and a weapon, like an entity the authority knows.</summary>
        private sealed class Swinger : ICombatEntity, IActiveWeaponProvider, IAimPoseProvider
        {
            public int CombatId { get; set; }
            public EntityKind Kind { get; set; } = EntityKind.LocalPlayer;
            public int TeamId { get; set; } = CombatTeams.PlayerTeam;
            public Vector3 Eye = new Vector3(0f, 1.8f, 0f);
            public float Yaw;
            public float Pitch;
            public WeaponData Weapon;
            public bool Dead;
            public float Taken;
            public bool HasPose = true;

            public Vector3 CombatCenter => Eye + Vector3.down * 0.7f;
            public Collider HeadCollider => null;
            public WeaponData ActiveWeapon => Weapon;
            public bool IsDead => Dead;
            public float CurrentHealth => 1000f - Taken;
            public float MaxHealth => 1000f;
            public void ApplyDamage(DamageInfo damage) { Taken += damage.Amount; }

            public bool TryGetAimPose(out Vector3 eye, out float yawDegrees, out float pitchDegrees)
            {
                eye = Eye;
                yawDegrees = Yaw;
                pitchDegrees = Pitch;
                return HasPose;
            }
        }

        /// <summary>An attacker without a swing pose (an enemy-like entity): it can never begin a swept swing.</summary>
        private sealed class PoselessAttacker : ICombatEntity, IActiveWeaponProvider
        {
            public int CombatId { get; set; }
            public EntityKind Kind => EntityKind.Enemy;
            public int TeamId => CombatTeams.MonsterTeam;
            public Vector3 Point = new Vector3(0f, 1.15f, 6f);
            public WeaponData Weapon;
            public Vector3 CombatCenter => Point;
            public Collider HeadCollider => null;
            public WeaponData ActiveWeapon => Weapon;
            public bool IsDead => false;
            public float CurrentHealth => 100f;
            public float MaxHealth => 100f;
            public void ApplyDamage(DamageInfo damage) { }
        }

        /// <summary>A hostile target that only has hit shapes, optional armour and an optional directional guard.</summary>
        private sealed class Dummy : ICombatEntity, IHitShapeProvider, IArmorRatingProvider, IDefenseProvider
        {
            public int CombatId { get; set; }
            public EntityKind Kind => EntityKind.Enemy;
            public int TeamId { get; set; } = CombatTeams.MonsterTeam;
            public readonly List<HitShape> Shapes = new List<HitShape>();
            public HitVolume[] Volumes;
            public Vector3 Center = new Vector3(0f, 1.5f, 2f);
            public bool Dead;
            public float Armor;
            public float GuardReduction;
            public float GuardArc;
            public Vector3 GuardForward = new Vector3(0f, 0f, -1f);
            public readonly List<DamageInfo> Received = new List<DamageInfo>();

            public Vector3 CombatCenter => Center;
            public Collider HeadCollider => null;
            public bool IsDead => Dead;
            public float CurrentHealth => 100000f;
            public float MaxHealth => 100000f;
            public float Taken { get; private set; }

            public void ApplyDamage(DamageInfo damage)
            {
                Received.Add(damage);
                Taken += damage.Amount;
            }

            public void CollectHitShapes(List<HitShape> shapes)
            {
                shapes.AddRange(Shapes);
                if (Volumes != null) HitVolumeLayout.Collect(Volumes, shapes);
            }

            public float GetArmorRating() => Armor;

            public bool TryGetGuardReduction(in DamageInfo incoming, out float reduction)
            {
                reduction = 0f;
                if (GuardArc <= 0f || GuardReduction <= 0f) return false;
                Vector3 toSource = incoming.SourcePosition - Center;
                toSource.y = 0f;
                if (toSource.sqrMagnitude < 1e-4f) return false;
                if (Vector3.Angle(GuardForward, toSource) > GuardArc * 0.5f) return false;
                reduction = GuardReduction;
                return true;
            }
        }

        private sealed class Scenario : IDisposable
        {
            public Swinger Who;
            public readonly List<Dummy> Targets = new List<Dummy>();
            public readonly List<GameObject> Props = new List<GameObject>();
            public readonly List<ICombatEntity> Registered = new List<ICombatEntity>();

            public void Dispose()
            {
                foreach (ICombatEntity e in Registered) CombatRegistry.Unregister(e.CombatId, e);
                foreach (GameObject go in Props) if (go != null) UnityEngine.Object.DestroyImmediate(go);
                Registered.Clear();
                Props.Clear();
            }
        }

        // ------------------------------------------------------------------------------------------------
        // Plumbing
        // ------------------------------------------------------------------------------------------------

        private void Check(bool ok, string label)
        {
            _checks++;
            if (!ok)
            {
                _failures++;
                Debug.LogError($"{Tag} FAIL {label}");
            }
        }

        private void Near(float expected, float actual, float tolerance, string label)
        {
            Check(Mathf.Abs(expected - actual) <= tolerance, $"{label} (expected {expected:F4}, got {actual:F4})");
        }

        private Scenario NewScenario(WeaponData weapon, Vector3? eye = null, float yaw = 0f, float pitch = 0f)
        {
            Scenario s = new Scenario();
            s.Who = new Swinger { CombatId = CombatRegistry.AllocateId(), Weapon = weapon, Yaw = yaw, Pitch = pitch };
            s.Who.Eye = Origin + (eye ?? new Vector3(0f, 1.8f, 0f));
            CombatRegistry.Register(s.Who.CombatId, s.Who);
            s.Registered.Add(s.Who);
            _scenarioCleanup.Add(s);
            return s;
        }

        private Dummy AddBody(Scenario s, Vector3 center, float radius = 0.3f, HitLocation location = HitLocation.Body)
        {
            center += Origin;
            Dummy d = new Dummy { CombatId = CombatRegistry.AllocateId(), Center = center };
            d.Shapes.Add(new HitShape(location, center, center, radius));
            CombatRegistry.Register(d.CombatId, d);
            s.Targets.Add(d);
            s.Registered.Add(d);
            return d;
        }

        private Dummy AddHumanoid(Scenario s, Vector3 feet, bool withArms = true)
        {
            feet += Origin;
            GameObject root = new GameObject("SweptGate_Humanoid");
            root.transform.position = feet;
            s.Props.Add(root);
            Dummy d = new Dummy { CombatId = CombatRegistry.AllocateId(), Center = feet + Vector3.up * 1.15f };
            d.Volumes = HitVolumeLayout.EnsureHumanoid(root, 1f);
            if (!withArms) foreach (HitVolume v in d.Volumes) if (v != null && v.location == HitLocation.Arms) v.enabled = false;
            CombatRegistry.Register(d.CombatId, d);
            s.Targets.Add(d);
            s.Registered.Add(d);
            return d;
        }

        private GameObject AddWall(Scenario s, Vector3 center, Vector3 size)
        {
            GameObject wall = new GameObject("SweptGate_Wall");
            wall.transform.position = center + Origin;
            BoxCollider box = wall.AddComponent<BoxCollider>();
            box.size = size;
            int level = LayerMask.NameToLayer(GameConfig.LevelLayerName);
            if (level >= 0) wall.layer = level;
            s.Props.Add(wall);
            Physics.SyncTransforms();
            return wall;
        }

        private MeleeSwingIntent Intent(Swinger who, int pattern, int? sequence = null, string weaponId = null, int combo = 0,
            float? yaw = null, float? pitch = null, int? instigator = null)
        {
            int id = instigator ?? who.CombatId;
            return new MeleeSwingIntent(id, weaponId ?? (who.Weapon != null ? who.Weapon.weaponId : string.Empty), pattern, combo,
                yaw ?? who.Yaw, pitch ?? who.Pitch, sequence ?? CombatRegistry.NextSequence(id));
        }

        private bool Begin(Scenario s, int pattern, out int swingId, out string reason)
        {
            MeleeSwingIntent intent = Intent(s.Who, pattern);
            return _authority.TryBeginMeleeSwingAsOwner(intent, out swingId, out reason);
        }

        /// <summary>Begins a swing and steps the authority clock through it. Returns the final status.</summary>
        private MeleeSwingStatus RunSwing(Scenario s, int pattern, params float[] dts)
        {
            Check(Begin(s, pattern, out int id, out string reason), "swing accepted (" + reason + ")");
            if (dts == null || dts.Length == 0) dts = new[] { 0.005f };

            float simulated = 0f;
            int step = 0;
            MeleeSwingStatus status = default;
            while (simulated < 4f)
            {
                float dt = dts[step++ % dts.Length];
                _clock += dt;
                simulated += dt;
                _authority.StepSwings();
                if (_authority.TryGetMeleeSwingStatus(id, out status) && status.Finished) break;
            }

            _authority.TryGetMeleeSwingStatus(id, out status);
            return status;
        }

        private static int CountHitsOn(MeleeSwingStatus status, Dummy d)
        {
            int n = 0;
            for (int i = 0; i < status.Hits.Count; i++) if (status.Hits[i].TargetCombatId == d.CombatId) n++;
            return n;
        }

        private static bool WasHit(MeleeSwingStatus status, Dummy d) => CountHitsOn(status, d) > 0;

        private static MeleeSwingHit HitOn(MeleeSwingStatus status, Dummy d)
        {
            for (int i = 0; i < status.Hits.Count; i++) if (status.Hits[i].TargetCombatId == d.CombatId) return status.Hits[i];
            return default;
        }

        private string Summarize(MeleeSwingStatus status, Scenario s)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append($"phase={status.Phase} end={status.EndReason} wall={status.WallContactSample} hits={status.Hits.Count};");
            for (int i = 0; i < status.Hits.Count; i++)
            {
                MeleeSwingHit h = status.Hits[i];
                int index = s.Targets.FindIndex(t => t.CombatId == h.TargetCombatId);
                sb.Append($" [{index}:{h.Location}:{h.Amount:F3}:{h.FirstSample}:{h.Sample}]");
            }
            return sb.ToString();
        }

        private void Cooldown(float seconds) => _clock += seconds;

        // ------------------------------------------------------------------------------------------------
        // Run
        // ------------------------------------------------------------------------------------------------

        private IEnumerator Start()
        {
            yield return null;
            yield return null;

            Section("setup and data", SetupAndData);
            Section("reach, arcs and the thrust line", ReachAndArcs);
            Section("hit locations and armour", LocationsAndArmour);
            Section("once per target, cleave, target limit", OncePerTargetAndCleave);
            Section("wall stops", WallStops);
            Section("tick independence and thin targets", TickIndependence);
            Section("path matches the pattern data", PathMatchesPattern);
            Section("overhead versus horizontal", OverheadVersusHorizontal);
            Section("forged intents", ForgedIntents);
            Section("guard, enemy claw, legacy path", GuardAndLegacy);
            Section("weapon swap and cooldown floor", WeaponSwapAndCooldown);
            Section("D1c5: production configuration (zones on, real chain selection)", ProductionConfiguration);

            IEnumerator real = RealRigSection();
            while (true)
            {
                bool more = false;
                try { more = real.MoveNext(); }
                catch (Exception e) { _failures++; Debug.LogException(e); }
                if (!more) break;
                yield return real.Current;
            }

            foreach (IDisposable d in _scenarioCleanup) d.Dispose();
            foreach (UnityEngine.Object o in _cleanup) if (o != null) Destroy(o);

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
            Debug.Log($"{Tag} section '{name}': {_checks - checksBefore} checks, {_failures - failuresBefore} failures");
        }

        // ------------------------------------------------------------------------------------------------
        // 0/1. setup and the data itself
        // ------------------------------------------------------------------------------------------------

        private void SetupAndData()
        {
            _sword = AssetDatabase.LoadAssetAtPath<WeaponData>(EmberdelvePaths.Data + "/Weapon_AshenShortSword.asset");
            _rapier = AssetDatabase.LoadAssetAtPath<WeaponData>(EmberdelvePaths.Data + "/Weapon_EmberRapier.asset");
            _crossbow = AssetDatabase.LoadAssetAtPath<WeaponData>(EmberdelvePaths.Data + "/Weapon_EmberCrossbow.asset");
            _claw = AssetDatabase.LoadAssetAtPath<WeaponData>(EmberdelvePaths.Data + "/Weapon_HollowClaw.asset");
            Check(_sword != null && _rapier != null && _crossbow != null && _claw != null, "weapon assets are available");
            if (_sword == null || _rapier == null || _crossbow == null || _claw == null) throw new InvalidOperationException("weapon assets missing");

            GameConfig sceneConfig = AssetDatabase.LoadAssetAtPath<GameConfig>(EmberdelvePaths.Data + "/EmberdelveGameConfig.asset");
            Check(sceneConfig != null && sceneConfig.hitLocations != null, "the game config references a hit-location table asset");
            _table = sceneConfig != null ? sceneConfig.hitLocations : HitLocationTable.Default;

            _config = ScriptableObject.CreateInstance<GameConfig>();
            _cleanup.Add(_config);
            _config.levelMask = LayerMask.GetMask(GameConfig.LevelLayerName);
            _config.hitLocations = null;   // code defaults first; a custom asset is tested later

            GameObject go = new GameObject("SweptMeleeGate_Authority");
            _cleanup.Add(go);
            _authority = go.AddComponent<LocalCombatAuthority>();
            typeof(LocalCombatAuthority).GetField("config", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(_authority, _config);
            _authority.SetClock(() => _clock);
            _authority.AutoStepSwings = false;   // the gate steps by hand: no wall clock anywhere in this gate

            // D1b fixture opt-ins (explained in Docs/Tasks/D1b.md, "Rewritten fixtures"): this gate pins the SWEEP
            // (reach, locations, cleave, walls, tick independence) with a specific pattern per case, including the
            // overhead and the back slash that the sword's combo chain now only reaches as steps 2 and 1, and with
            // exact damage numbers that must not depend on where along the blade the target happens to sit. So it
            // accepts those patterns at step 0 (AllowLegacyPatternSelection) and leaves the impact-zone bands off
            // (ImpactZonesEnabled = false). The chain itself and the zones are pinned by EmberdelveCombatFlowGate.
            _authority.AllowLegacyPatternSelection = true;
            _authority.ImpactZonesEnabled = false;

            // ---- pattern data
            Check(_sword.UsesSweptMelee && _sword.swingPatterns.Length == 4, "the sword has four swing patterns (three chain patterns and the riposte cut)");
            Check(_rapier.UsesSweptMelee && _rapier.swingPatterns.Length == 1, "the rapier has one swing pattern");
            Check(_claw.UsesSweptMelee && _claw.swingPatterns.Length == 1 && !_crossbow.UsesSweptMelee, "D1c4: the enemy claw has one swing pattern (the telegraph wedge swept) and the crossbow has none");
            Check(_sword.swingPatterns[0].kind == SwingKind.HorizontalRightToLeft && _sword.swingPatterns[1].kind == SwingKind.HorizontalLeftToRight
                  && _sword.swingPatterns[2].kind == SwingKind.Overhead && _rapier.swingPatterns[0].kind == SwingKind.Thrust,
                "sword = slash R->L, slash L->R, overhead (Dark and Darker order); rapier = thrust");

            foreach (WeaponData w in new[] { _sword, _rapier })
            {
                foreach (SwingPatternData p in w.swingPatterns)
                {
                    Check(p.Validate(out string problem), $"{p.patternId}: pattern data validates ({problem})");
                    // D1a left a move-speed hook at 1; D1b consumes it as three phase multipliers (windup / hit / recovery).
                    Check(Mathf.Approximately(p.windupMoveMultiplier, 0.85f) && Mathf.Approximately(p.activeMoveMultiplier, 0.70f) && Mathf.Approximately(p.recoveryMoveMultiplier, 0.85f),
                        $"{p.patternId}: the D1b phase move multipliers are authored (0.85 / 0.70 / 0.85)");
                    Check(Mathf.Approximately(p.damageMultiplier, 1f), $"{p.patternId}: the pattern shape is damage x1 (the per-step multiplier lives in the weapon chain)");
                    Check(p.windupTime > 0f && p.activeTime > 0f && p.recoveryTime > 0f, $"{p.patternId}: windup, active and recovery durations are authored");
                    Check(SwingGeometry.SampleCount(p, w) == SwingGeometry.SampleCount(p, w) && SwingGeometry.SampleCount(p, w) >= SwingGeometry.MinSamples,
                        $"{p.patternId}: the sample count is a stable function of the data ({SwingGeometry.SampleCount(p, w)})");
                    // D1c2: the grid includes the rotation allowance for the 720 deg/s ceiling and must not hit the upper clamp, or its spacing guarantee is void
                    Check(SwingGeometry.SampleCount(p, w) < SwingGeometry.MaxSamples, $"{p.patternId}: the D1c2 sample count stays below the {SwingGeometry.MaxSamples} clamp ({SwingGeometry.SampleCount(p, w)})");
                }
            }

            Near(_sword.windupTime, _sword.swingPatterns[0].windupTime, 1e-5f, "slash windup mirrors the weapon timing");
            Near(_sword.activeWindow, _sword.swingPatterns[0].activeTime, 1e-5f, "slash active mirrors the weapon timing");
            Near(_sword.recoveryTime, _sword.swingPatterns[0].recoveryTime, 1e-5f, "slash recovery mirrors the weapon timing");
            Near(_rapier.windupTime, _rapier.swingPatterns[0].windupTime, 1e-5f, "thrust windup mirrors the weapon timing");

            // ---- reach numbers (effective reach from the eye)
            Near(2.291f, _sword.swingPatterns[0].EffectiveReach(_sword), 1e-3f, "sword effective reach is about 2.3 m at the eye (H1 retune)");
            Near(1.2f, _sword.swingPatterns[0].BladeLength(_sword), 0.01f, "sword damage segment is about 1.2 m");
            Near(0.30f, _sword.swingPatterns[0].activeTime, 1e-4f, "sword slash active phase is 0.30 s");
            Near(2.331f, _rapier.swingPatterns[0].EffectiveReach(_rapier), 1e-3f, "rapier thrust reach is about 2.3 m");
            Near(_sword.swingPatterns[0].EffectiveReach(_sword), _sword.swingPatterns[0].BladeLength(_sword) + _sword.swingPatterns[0].sweepRadius + _sword.swingPatterns[0].reachInset, 1e-4f,
                "sword blade length + sweep radius + inset = effective reach");

            // ---- the location table is data, our own tunables
            Near(1.5f, _table.Multiplier(HitLocation.Head), 1e-6f, "table: head 1.5");
            Near(1.0f, _table.Multiplier(HitLocation.Body), 1e-6f, "table: body 1.0");
            Near(0.8f, _table.Multiplier(HitLocation.Arms), 1e-6f, "table: arms 0.8");
            Near(0.7f, _table.Multiplier(HitLocation.Hands), 1e-6f, "table: hands 0.7");
            Near(0.6f, _table.Multiplier(HitLocation.Legs), 1e-6f, "table: legs 0.6");
            Near(0.5f, _table.Multiplier(HitLocation.Feet), 1e-6f, "table: feet 0.5");
            Near(1.0f, _table.Multiplier(HitLocation.Unspecified), 1e-6f, "table: an unspecified location is 1.0");
            Near(1.6f, _table.Multiplier(HitLocation.Head, 0.1f), 1e-6f, "head multiplier is additive-capable (1.5 + 0.1)");
            Near(1.8f, _table.Multiplier(HitLocation.Head, 0.5f), 1e-6f, "head multiplier is capped at the existing 1.8 ceiling");
            Near(1.8f, DamageResolver.HeadshotMultiplier, 1e-6f, "the legacy 1.8 headshot ceiling is unchanged");
            Near(1.5f, HitLocationTable.Default.Multiplier(HitLocation.Head), 1e-6f, "the code-default table agrees with the asset");

            // ---- the intent is plain data and carries no outcome fields
            string[] fields = Array.ConvertAll(typeof(MeleeSwingIntent).GetFields(BindingFlags.Instance | BindingFlags.Public), f => f.Name);
            Array.Sort(fields);
            Check(string.Join(",", fields) == "AimPitch,AimYaw,ComboStep,InstigatorCombatId,PatternIndex,Sequence,WeaponId",
                "MeleeSwingIntent carries only instigator, weapon id, pattern, combo step, aim snapshot and sequence (" + string.Join(",", fields) + ")");
        }

        // ------------------------------------------------------------------------------------------------
        // 2. reach, arc, thrust line
        // ------------------------------------------------------------------------------------------------

        private bool Hits(WeaponData weapon, int pattern, Vector3 center, float radius = 0.33f)
        {
            Scenario s = NewScenario(weapon);
            Dummy d = AddBody(s, center, radius);
            MeleeSwingStatus st = RunSwing(s, pattern);
            Cooldown(2f);
            bool hit = WasHit(st, d);
            s.Dispose();
            return hit;
        }

        private void ReachAndArcs()
        {
            // sword slash: targets in front at ordinary distances are hit
            foreach (float d in new[] { 0.9f, 1.4f, 1.8f, 2.2f })
            {
                Check(Hits(_sword, Slash, new Vector3(0f, 1.5f, d)), $"sword slash L->R hits a target {d:F1} m in front");
                Check(Hits(_sword, SlashBack, new Vector3(0f, 1.5f, d)), $"sword slash R->L hits a target {d:F1} m in front");
            }

            // off to the sides inside the swept sector
            Check(Hits(_sword, Slash, new Vector3(1.56f, 1.5f, 0.9f)), "sword slash hits a target 60 degrees to the right at 1.8 m");
            Check(Hits(_sword, Slash, new Vector3(-1.56f, 1.5f, 0.9f)), "sword slash hits a target 60 degrees to the left at 1.8 m");

            // beyond reach and behind
            Check(!Hits(_sword, Slash, new Vector3(0f, 1.5f, 3.6f)), "sword misses a target whose surface is beyond the 2.9 m reach");
            Check(!Hits(_sword, Slash, new Vector3(0f, 1.5f, 4.5f)), "sword misses a target at 4.5 m");
            Check(!Hits(_sword, Slash, new Vector3(0f, 1.5f, -1.5f)), "sword misses a target directly behind the attacker");
            Check(!Hits(_sword, Slash, new Vector3(0f, 1.5f, -0.5f)), "sword misses a target just behind the attacker");
            Check(!Hits(_sword, Overhead, new Vector3(0f, 1.5f, -1.5f)), "the overhead misses a target behind the attacker");
            Check(!Hits(_sword, Slash, new Vector3(0f, 1.5f, 1.8f) + Vector3.up * 3f), "sword misses a target 3 m above the line of fire");

            // yaw of the attacker rotates the whole sweep (the sweep is in the aim frame)
            {
                Scenario s = NewScenario(_sword, yaw: 90f);
                Dummy right = AddBody(s, new Vector3(1.8f, 1.5f, 0f));
                Dummy behindNow = AddBody(s, new Vector3(0f, 1.5f, 1.8f));
                MeleeSwingStatus st = RunSwing(s, Slash);
                Check(WasHit(st, right), "after turning 90 degrees right the target that was on the right is in front and is hit");
                Check(!WasHit(st, behindNow), "after turning 90 degrees right the old forward target is on the left, outside the swept sector at that range");
                Cooldown(2f);
                s.Dispose();
            }

            // the rapier thrust is a narrow line, not an arc
            foreach (float d in new[] { 1.0f, 1.8f, 2.4f })
            {
                Check(Hits(_rapier, 0, new Vector3(0f, 1.6f, d)), $"rapier thrust hits a target on the line at {d:F1} m");
            }

            Check(Hits(_rapier, 0, new Vector3(0.20f, 1.6f, 2.4f)), "rapier hits a target 0.2 m off the line at 2.4 m");
            Check(!Hits(_rapier, 0, new Vector3(2.4f * Mathf.Sin(20f * Mathf.Deg2Rad), 1.6f, 2.4f * Mathf.Cos(20f * Mathf.Deg2Rad))),
                "rapier misses a target 20 degrees off the line at range (a thrust is not an arc)");
            Check(!Hits(_rapier, 0, new Vector3(-2.4f * Mathf.Sin(20f * Mathf.Deg2Rad), 1.6f, 2.4f * Mathf.Cos(20f * Mathf.Deg2Rad))),
                "rapier misses a target 20 degrees off the line on the other side");
            Check(!Hits(_rapier, 0, new Vector3(0f, 1.6f, 3.6f)), "rapier misses a target beyond the 2.3 m thrust reach");
            Check(!Hits(_rapier, 0, new Vector3(0f, 1.6f, -1.2f)), "rapier misses a target behind");
            Check(!Hits(_rapier, 0, new Vector3(2.0f * Mathf.Sin(25f * Mathf.Deg2Rad), 1.6f, 2.0f * Mathf.Cos(25f * Mathf.Deg2Rad))),
                "rapier misses a target 25 degrees off the line at 2.0 m (0.85 m to the side)");
            Check(Hits(_sword, Slash, new Vector3(2.0f * Mathf.Sin(25f * Mathf.Deg2Rad), 1.5f, 2.0f * Mathf.Cos(25f * Mathf.Deg2Rad))),
                "control: the sword slash does hit that same 25-degree-off target (an arc)");
        }

        // ------------------------------------------------------------------------------------------------
        // 3. hit locations through the real damage path
        // ------------------------------------------------------------------------------------------------

        private void LocationsAndArmour()
        {
            float baseDamage = _sword.baseDamage;
            HitLocation[] all = { HitLocation.Head, HitLocation.Body, HitLocation.Arms, HitLocation.Hands, HitLocation.Legs, HitLocation.Feet };
            float[] mult = { 1.5f, 1.0f, 0.8f, 0.7f, 0.6f, 0.5f };

            for (int i = 0; i < all.Length; i++)
            {
                // armour 0
                Scenario s = NewScenario(_sword);
                Dummy d = AddBody(s, new Vector3(0f, 1.5f, 1.8f), 0.3f, all[i]);
                MeleeSwingStatus st = RunSwing(s, Slash);
                bool hit = d.Received.Count == 1;
                Check(hit, $"{all[i]} volume is struck once");
                if (hit)
                {
                    DamageInfo info = d.Received[0];
                    Near(baseDamage * mult[i], info.Amount, 1e-3f, $"{all[i]}: damage = {baseDamage} x {mult[i]} through the real path, armour 0");
                    Check(info.Location == all[i], $"{all[i]}: DamageInfo carries the location the authority decided ({info.Location})");
                    Check(info.Headshot == (all[i] == HitLocation.Head), $"{all[i]}: the headshot flag is true only for the head");
                    Check(!info.Blocked, $"{all[i]}: not blocked");
                    Check(st.Hits.Count == 1 && st.Hits[0].Location == all[i], $"{all[i]}: the swing report names the location");
                }
                Cooldown(2f);
                s.Dispose();

                // armour 100 (final = amount * 100 / (100 + rating)), applied after the location multiplier
                Scenario sa = NewScenario(_sword);
                Dummy da = AddBody(sa, new Vector3(0f, 1.5f, 1.8f), 0.3f, all[i]);
                da.Armor = 100f;
                RunSwing(sa, Slash);
                if (da.Received.Count == 1)
                {
                    Near(baseDamage * mult[i] * 100f / 200f, da.Received[0].Amount, 1e-3f, $"{all[i]}: armour 100 halves the located damage");
                }
                else Check(false, $"{all[i]}: armoured target struck once");
                Cooldown(2f);
                sa.Dispose();
            }

            // a different rating, still after the multiplier
            {
                Scenario s = NewScenario(_sword);
                Dummy d = AddBody(s, new Vector3(0f, 1.5f, 1.8f), 0.3f, HitLocation.Head);
                d.Armor = 50f;
                RunSwing(s, Slash);
                Near(baseDamage * 1.5f * 100f / 150f, d.Received.Count == 1 ? d.Received[0].Amount : -1f, 1e-3f, "head with armour 50: 26 x 1.5 x 100/150");
                Cooldown(2f);
                s.Dispose();
            }

            // the table is data: a custom asset changes the numbers, the head still obeys the 1.8 ceiling
            {
                HitLocationTable custom = ScriptableObject.CreateInstance<HitLocationTable>();
                _cleanup.Add(custom);
                custom.head = 2.0f;
                custom.feet = 0.25f;
                _config.hitLocations = custom;

                Scenario s = NewScenario(_sword);
                Dummy head = AddBody(s, new Vector3(0f, 1.5f, 1.8f), 0.3f, HitLocation.Head);
                RunSwing(s, Slash);
                Near(baseDamage * 1.8f, head.Received.Count == 1 ? head.Received[0].Amount : -1f, 1e-3f, "a custom head value of 2.0 is capped at 1.8");
                Cooldown(2f);
                s.Dispose();

                Scenario sf = NewScenario(_sword);
                Dummy foot = AddBody(sf, new Vector3(0f, 1.5f, 1.8f), 0.3f, HitLocation.Feet);
                RunSwing(sf, Slash);
                Near(baseDamage * 0.25f, foot.Received.Count == 1 ? foot.Received[0].Amount : -1f, 1e-3f, "a custom feet value of 0.25 is read from the table asset");
                Cooldown(2f);
                sf.Dispose();

                _config.hitLocations = _table;
                Scenario st = NewScenario(_sword);
                Dummy again = AddBody(st, new Vector3(0f, 1.5f, 1.8f), 0.3f, HitLocation.Feet);
                RunSwing(st, Slash);
                Near(baseDamage * 0.5f, again.Received.Count == 1 ? again.Received[0].Amount : -1f, 1e-3f, "the shipped table asset gives feet 0.5");
                Cooldown(2f);
                st.Dispose();
                _config.hitLocations = null;
            }

            // pattern damage multiplier and the rapier base
            {
                Scenario s = NewScenario(_sword);
                Dummy d = AddBody(s, new Vector3(0f, 1.5f, 1.8f), 0.3f, HitLocation.Body);
                RunSwing(s, Overhead);
                Near(baseDamage * _sword.swingPatterns[2].damageMultiplier, d.Received.Count == 1 ? d.Received[0].Amount : -1f, 1e-3f,
                    "the overhead's own damage multiplier (data) applies");
                Cooldown(2f);
                s.Dispose();

                Scenario sr = NewScenario(_rapier);
                Dummy dr = AddBody(sr, new Vector3(0f, 1.6f, 1.8f), 0.3f, HitLocation.Body);
                RunSwing(sr, 0);
                Near(_rapier.baseDamage, dr.Received.Count == 1 ? dr.Received[0].Amount : -1f, 1e-3f, "rapier thrust on a body volume deals the rapier base damage");
                Check(dr.Received.Count == 1 && dr.Received[0].Type == _rapier.damageType, "the damage type comes from the rapier asset");
                Cooldown(2f);
                sr.Dispose();
            }
        }

        // ------------------------------------------------------------------------------------------------
        // 4. once per target, cleave, limits
        // ------------------------------------------------------------------------------------------------

        private void OncePerTargetAndCleave()
        {
            // one target, several overlapping volumes touched over many samples -> exactly one hit
            {
                Scenario s = NewScenario(_sword);
                Dummy d = AddBody(s, new Vector3(0f, 1.5f, 1.8f), 0.4f, HitLocation.Body);
                d.Shapes.Add(new HitShape(HitLocation.Arms, new Vector3(-0.3f, 1.5f, 1.8f), new Vector3(-0.3f, 1.9f, 1.8f), 0.3f));
                d.Shapes.Add(new HitShape(HitLocation.Legs, new Vector3(0.3f, 1.2f, 1.8f), new Vector3(0.3f, 1.6f, 1.8f), 0.3f));
                MeleeSwingStatus st = RunSwing(s, Slash);
                Check(d.Received.Count == 1 && st.Hits.Count == 1, $"a target with three overlapping volumes is hit exactly once per swing ({d.Received.Count})");
                Cooldown(2f);

                MeleeSwingStatus second = RunSwing(s, SlashBack);
                Check(d.Received.Count == 2 && second.Hits.Count == 1, "the next swing can hit the same target again (once per swing, not once per life)");
                Cooldown(2f);
                s.Dispose();
            }

            // cleave: five targets across the arc, the slash can hit at most maxTargets of them, in sweep order
            {
                Scenario s = NewScenario(_sword);
                float[] xs = { -1.2f, -0.6f, 0f, 0.6f, 1.2f };
                foreach (float x in xs) AddBody(s, new Vector3(x, 1.5f, 1.7f), 0.3f);
                int limit = _sword.swingPatterns[0].maxTargets;
                MeleeSwingStatus st = RunSwing(s, Slash);
                Check(st.Hits.Count == limit, $"a slash cleaves exactly maxTargets={limit} of five targets ({st.Hits.Count})");
                bool ordered = st.Hits.Count == limit;
                for (int i = 0; ordered && i < st.Hits.Count; i++) ordered = st.Hits[i].TargetCombatId == s.Targets[i].CombatId;
                Check(ordered, "L->R: the four hits are the four leftmost targets, in sweep order");
                Check(!WasHit(st, s.Targets[4]), "the rightmost target is beyond the target limit");
                for (int i = 1; i < st.Hits.Count; i++) Check(st.Hits[i].FirstSample >= st.Hits[i - 1].FirstSample, $"hit {i} was first touched no earlier than hit {i - 1}");
                foreach (Dummy t in s.Targets) Check(t.Received.Count <= 1, "no target took more than one hit from the cleave");
                Cooldown(2f);

                // R->L reverses the sweep order
                foreach (Dummy t in s.Targets) t.Received.Clear();
                MeleeSwingStatus back = RunSwing(s, SlashBack);
                bool reversed = back.Hits.Count == limit;
                for (int i = 0; reversed && i < back.Hits.Count; i++) reversed = back.Hits[i].TargetCombatId == s.Targets[s.Targets.Count - 1 - i].CombatId;
                Check(reversed, "R->L: the four hits are the four rightmost targets, right to left");
                Check(!WasHit(back, s.Targets[0]), "the leftmost target is beyond the limit for the reverse slash");
                Cooldown(2f);
                s.Dispose();
            }

            // three targets, all inside the sweep: three are hit by a slash with room for four
            {
                Scenario s = NewScenario(_sword);
                AddBody(s, new Vector3(-0.8f, 1.5f, 1.6f), 0.3f);
                AddBody(s, new Vector3(0f, 1.5f, 1.6f), 0.3f);
                AddBody(s, new Vector3(0.8f, 1.5f, 1.6f), 0.3f);
                MeleeSwingStatus st = RunSwing(s, Slash);
                Check(st.Hits.Count == 3 && s.Targets.TrueForAll(t => t.Received.Count == 1), "a horizontal slash may cleave three targets once each");
                Cooldown(2f);
                s.Dispose();
            }

            // the overhead has a smaller limit
            {
                Scenario s = NewScenario(_sword);
                AddBody(s, new Vector3(0f, 1.5f, 1.0f), 0.3f);
                AddBody(s, new Vector3(0f, 1.5f, 1.8f), 0.3f);
                AddBody(s, new Vector3(0f, 1.5f, 2.4f), 0.3f);
                int limit = _sword.swingPatterns[2].maxTargets;
                MeleeSwingStatus st = RunSwing(s, Overhead);
                Check(limit == 2 && st.Hits.Count == 2, $"the overhead stops at its own maxTargets={limit} ({st.Hits.Count} hits on three targets in line)");
                Cooldown(2f);
                s.Dispose();
            }

            // friendly and dead entities are never hit, the attacker never hits itself
            {
                Scenario s = NewScenario(_sword);
                Dummy friend = AddBody(s, new Vector3(0f, 1.5f, 1.6f), 0.3f);
                friend.TeamId = CombatTeams.PlayerTeam;
                Dummy corpse = AddBody(s, new Vector3(-0.6f, 1.5f, 1.6f), 0.3f);
                corpse.Dead = true;
                Dummy foe = AddBody(s, new Vector3(0.6f, 1.5f, 1.6f), 0.3f);
                MeleeSwingStatus st = RunSwing(s, Slash);
                Check(friend.Received.Count == 0, "a same-team entity is not struck");
                Check(corpse.Received.Count == 0, "a dead entity is not struck");
                Check(foe.Received.Count == 1 && st.Hits.Count == 1, "the hostile live entity is struck");
                Check(s.Who.Taken == 0f, "the attacker does not strike itself");
                Cooldown(2f);
                s.Dispose();
            }

            // an entity that exposes no hit volumes cannot be struck by the sweep
            {
                Scenario s = NewScenario(_sword);
                PoselessAttacker blind = new PoselessAttacker { CombatId = CombatRegistry.AllocateId(), Point = Origin + new Vector3(0f, 1.5f, 1.8f), Weapon = _claw };
                CombatRegistry.Register(blind.CombatId, blind);
                s.Registered.Add(blind);
                MeleeSwingStatus st = RunSwing(s, Slash);
                Check(st.Hits.Count == 0, "an entity with no hit volumes is not part of the sweep");
                Cooldown(2f);
                s.Dispose();
            }
        }

        // ------------------------------------------------------------------------------------------------
        // 5. wall stops
        // ------------------------------------------------------------------------------------------------

        private Scenario WallScenario(bool withWall, out Dummy left, out Dummy right, out Dummy behind, out Dummy behind2)
        {
            Scenario s = NewScenario(_sword);
            left = AddBody(s, new Vector3(-0.8f, 1.5f, 1.0f), 0.3f);
            right = AddBody(s, new Vector3(0.8f, 1.5f, 1.0f), 0.3f);
            behind = AddBody(s, new Vector3(0f, 1.5f, 2.0f), 0.3f);
            behind2 = AddBody(s, new Vector3(-0.5f, 1.5f, 2.0f), 0.3f);
            if (withWall) AddWall(s, new Vector3(0f, 1.8f, 1.65f), new Vector3(4f, 4f, 0.3f));
            return s;
        }

        private void WallStops()
        {
            // control: with no wall everything in reach is hit
            {
                Scenario s = WallScenario(false, out Dummy left, out Dummy right, out Dummy behind, out Dummy behind2);
                MeleeSwingStatus st = RunSwing(s, Slash);
                Check(WasHit(st, left) && WasHit(st, right) && WasHit(st, behind) && WasHit(st, behind2),
                    "control (no wall): the near targets and both targets beyond the wall's position are all hit");
                Check(st.EndReason == SwingEndReason.Completed && st.WallContactSample < 0, "control: the swing completes with no wall contact");
                Cooldown(2f);
                s.Dispose();
            }

            // wall at z 1.5..1.8: the slash meets it early in the sweep
            {
                Scenario s = WallScenario(true, out Dummy left, out Dummy right, out Dummy behind, out Dummy behind2);
                MeleeSwingStatus st = RunSwing(s, Slash);
                Check(st.WallStopped && st.Finished, $"the swing is stopped by the wall and finishes (end {st.EndReason})");
                Check(st.WallContactSample >= 0 && st.WallContactSample < st.SampleCount / 3, $"the wall contact happens early in the sweep (sample {st.WallContactSample}/{st.SampleCount})");
                Check(st.SamplesProcessed <= st.WallContactSample + 1, "no sample after the wall contact was processed");
                Check(WasHit(st, left) && left.Received.Count == 1, "a target hit BEFORE the wall contact keeps its damage");
                Check(HitOn(st, left).FirstSample < st.WallContactSample, "that target was touched on an earlier sample than the wall");
                Check(behind.Received.Count == 0 && behind2.Received.Count == 0, "targets behind the wall take no damage");
                Check(right.Received.Count == 0, "a target in front of the wall but later in the sweep takes no damage either: the swing has ended");

                Check(_authority.ActiveSwingCount == 0, "the stopped swing is no longer running");

                // the stop cost the swinger the rest of the sweep, and the cooldown still applies
                Check(!Begin(s, SlashBack, out _, out string reason) && reason.Contains("cooldown"), "after a wall stop the weapon cooldown still applies (" + reason + ")");
                Cooldown(2f);
                s.Dispose();
            }

            // the wall hits exactly what stands in front of it: the nearer target is damaged, its twin behind is not
            {
                Scenario s = NewScenario(_sword);
                Dummy front = AddBody(s, new Vector3(-0.8f, 1.5f, 1.0f), 0.3f);
                Dummy back = AddBody(s, new Vector3(-0.8f, 1.5f, 2.0f), 0.3f);
                AddWall(s, new Vector3(0f, 1.8f, 1.65f), new Vector3(4f, 4f, 0.3f));
                MeleeSwingStatus st = RunSwing(s, Slash);
                Check(WasHit(st, front) && !WasHit(st, back), "target in front of a wall is struck, the one behind it is not");
                Cooldown(2f);
                s.Dispose();
            }

            // standing against a wall: the base is inside the wall, the swing ends on its first sample
            {
                Scenario s = NewScenario(_sword);
                Dummy beyond = AddBody(s, new Vector3(0f, 1.5f, 1.5f), 0.3f);
                AddWall(s, new Vector3(0f, 1.8f, 0.75f), new Vector3(4f, 4f, 0.3f));     // the hand starts inside it (z 0.6..0.9)
                MeleeSwingStatus st = RunSwing(s, Slash);
                Check(st.WallStopped && st.WallContactSample == 0, $"a swing begun with the weapon inside a wall stops on sample 0 ({st.WallContactSample})");
                Check(beyond.Received.Count == 0, "nothing beyond a wall the weapon starts in is hit");
                Cooldown(2f);
                s.Dispose();
            }

            // a wall that is not between the weapon and the target changes nothing
            {
                Scenario s = NewScenario(_sword);
                Dummy t = AddBody(s, new Vector3(0f, 1.5f, 1.6f), 0.3f);
                AddWall(s, new Vector3(0f, 1.8f, -3f), new Vector3(8f, 4f, 0.3f));
                MeleeSwingStatus st = RunSwing(s, Slash);
                Check(WasHit(st, t) && st.EndReason == SwingEndReason.Completed, "a wall behind the attacker does not stop the swing");
                Cooldown(2f);
                s.Dispose();
            }

            // a side wall the blade moves AWAY from clips the segment but does not end the swing; swinging INTO it does
            {
                Scenario s = NewScenario(_sword);
                Dummy mid = AddBody(s, new Vector3(0f, 1.5f, 1.8f), 0.3f);
                AddWall(s, new Vector3(-1.2f, 1.8f, 1.5f), new Vector3(0.3f, 4f, 6f));     // left wall, face at x = -1.05
                MeleeSwingStatus lr = RunSwing(s, Slash);
                Check(!lr.WallStopped && lr.EndReason == SwingEndReason.Completed && lr.WallContactSample < 0,
                    $"a left-to-right slash that STARTS pointing at the left wall is not stopped by it: the blade moves away from it ({lr.EndReason})");
                Check(WasHit(lr, mid), "and it still finishes the sweep and hits the target in front");
                Cooldown(2f);

                mid.Received.Clear();
                MeleeSwingStatus rl = RunSwing(s, SlashBack);
                Check(rl.WallStopped && rl.WallContactSample > rl.SampleCount / 2 && rl.WallContactSample < rl.SampleCount * 0.95f,
                    $"the right-to-left slash swings INTO the same wall late in the sweep and is stopped (sample {rl.WallContactSample}/{rl.SampleCount})");
                Check(mid.Received.Count == 1 && HitOn(rl, mid).FirstSample < rl.WallContactSample, "the target in front, touched before the wall contact, keeps its damage");
                Cooldown(2f);
                s.Dispose();
            }

            // M3a: the blade travels ALONG/away from a side wall with a target behind that wall: clipping must keep it unhurt
            {
                Scenario s = NewScenario(_sword);
                Dummy mid = AddBody(s, new Vector3(0f, 1.5f, 1.8f), 0.3f);
                Dummy behindWall = AddBody(s, new Vector3(-1.6f, 1.5f, 1.2f), 0.3f);       // beyond the left wall, where the blade points at the start
                AddWall(s, new Vector3(-1.2f, 1.8f, 1.5f), new Vector3(0.3f, 4f, 6f));
                MeleeSwingStatus lr = RunSwing(s, Slash);
                Check(lr.EndReason == SwingEndReason.Completed && WasHit(lr, mid), "side wall: the slash completes and hits the front target");
                Check(behindWall.Received.Count == 0, "side wall: a target behind the wall the blade starts against (and moves away from) takes no damage");
                Cooldown(2f);
                s.Dispose();
            }

            // M3a (mirror): the right-to-left slash starts against a RIGHT wall and moves away from it
            {
                Scenario s = NewScenario(_sword);
                Dummy mid = AddBody(s, new Vector3(0f, 1.5f, 1.8f), 0.3f);
                Dummy behindWall = AddBody(s, new Vector3(1.6f, 1.5f, 1.2f), 0.3f);
                AddWall(s, new Vector3(1.2f, 1.8f, 1.5f), new Vector3(0.3f, 4f, 6f));
                MeleeSwingStatus rl = RunSwing(s, SlashBack);
                Check(rl.EndReason == SwingEndReason.Completed && WasHit(rl, mid), "mirror side wall: the slash completes and hits the front target");
                Check(behindWall.Received.Count == 0, "mirror side wall: the target behind the right wall takes no damage");
                Cooldown(2f);
                s.Dispose();
            }

            // M3a (ceiling): the overhead starts pointing up into a ceiling and moves away from it; a target above is not hit
            {
                Scenario s = NewScenario(_sword);
                Dummy front = AddBody(s, new Vector3(0f, 1.5f, 1.4f), 0.3f);
                Dummy above = AddBody(s, new Vector3(0.15f, 3.9f, 0.6f), 0.3f);
                AddWall(s, new Vector3(0f, 3.55f, 1f), new Vector3(6f, 0.5f, 6f));      // slab y 3.3..3.8, underside above the raised blade
                MeleeSwingStatus st = RunSwing(s, Overhead);
                Check(st.EndReason == SwingEndReason.Completed, $"ceiling: the overhead is not stopped by a ceiling it moves away from ({st.EndReason})");
                Check(WasHit(st, front), "ceiling: the overhead still hits the target in front");
                Check(above.Received.Count == 0, "ceiling: a target above the ceiling takes no damage");
                Cooldown(2f);
                s.Dispose();
            }

            // M-a: a swing cannot start around an outside corner (the line from the eye to the hand is walled)
            {
                Scenario control = NewScenario(_sword);
                Dummy seen = AddBody(control, new Vector3(0.6f, 1.5f, 1.6f), 0.3f);
                MeleeSwingStatus open = RunSwing(control, Slash);
                Check(WasHit(open, seen), "corner control: without the wall the target at the right is hit");
                Cooldown(2f);
                control.Dispose();

                Scenario s = NewScenario(_sword);
                Dummy hidden = AddBody(s, new Vector3(0.6f, 1.5f, 1.6f), 0.3f);
                AddWall(s, new Vector3(-1.425f, 1.8f, 0.375f), new Vector3(3.15f, 4f, 0.15f));   // spans x -3..0.15, z 0.30..0.45: ends just right of the eye, clear of the hand (z 0.59)
                MeleeSwingStatus st = RunSwing(s, Slash);
                Check(st.WallStopped && st.WallContactSample == 0, $"corner: the hand starts on the far side of a wall edge the eye cannot see past, the swing stops on sample 0 ({st.WallContactSample})");
                Check(hidden.Received.Count == 0, "corner: nothing is struck around the corner");
                Cooldown(2f);
                s.Dispose();
            }

            // the rapier thrust is stopped by a wall in front of the target as well
            {
                Scenario s = NewScenario(_rapier);
                Dummy t = AddBody(s, new Vector3(0f, 1.6f, 2.3f), 0.3f);
                AddWall(s, new Vector3(0f, 1.8f, 1.5f), new Vector3(4f, 4f, 0.3f));
                MeleeSwingStatus st = RunSwing(s, 0);
                Check(st.WallStopped && t.Received.Count == 0, "a thrust into a wall is stopped and does not reach the target behind it");
                Cooldown(2f);
                s.Dispose();
            }
        }

        // ------------------------------------------------------------------------------------------------
        // 6. tick independence and thin targets
        // ------------------------------------------------------------------------------------------------

        private static readonly float[][] TickPlans =
        {
            new[] { 0.001f },
            new[] { 0.0137f },
            new[] { 0.02f },
            new[] { 0.05f, 0.011f, 0.2f },
            new[] { 0.5f },
            new[] { 2.5f },
        };

        private void TickIndependence()
        {
            // 1. the five-target cleave
            string reference = null;
            for (int p = 0; p < TickPlans.Length; p++)
            {
                Scenario s = NewScenario(_sword);
                foreach (float x in new[] { -1.2f, -0.6f, 0f, 0.6f, 1.2f }) AddBody(s, new Vector3(x, 1.5f, 1.7f), 0.3f);
                string summary = Summarize(RunSwing(s, Slash, TickPlans[p]), s);
                if (reference == null) reference = summary;
                Check(summary == reference, $"cleave: tick plan {p} gives the same hits as the 1 ms plan\n  ref {reference}\n  got {summary}");
                Cooldown(2f);
                s.Dispose();
            }

            // 2. the wall scenario (stop sample, hits kept, hits denied)
            reference = null;
            for (int p = 0; p < TickPlans.Length; p++)
            {
                Scenario s = WallScenario(true, out _, out _, out _, out _);
                string summary = Summarize(RunSwing(s, Slash, TickPlans[p]), s);
                if (reference == null) reference = summary;
                Check(summary == reference, $"wall: tick plan {p} stops at the same sample with the same hits\n  ref {reference}\n  got {summary}");
                Cooldown(2f);
                s.Dispose();
            }

            // 3. the humanoid dummy with all its volumes, overhead (location decided by the contact window)
            reference = null;
            for (int p = 0; p < TickPlans.Length; p++)
            {
                Scenario s = NewScenario(_sword, pitch: 23f);
                AddHumanoid(s, new Vector3(0f, 0f, 1.5f));
                string summary = Summarize(RunSwing(s, Overhead, TickPlans[p]), s);
                if (reference == null) reference = summary;
                Check(summary == reference, $"humanoid overhead: tick plan {p} gives the same location and damage\n  ref {reference}\n  got {summary}");
                Cooldown(2f);
                s.Dispose();
            }

            // 4. a thin target (radius 0.01) against a pattern with a thin sweep (0.02): no tunnelling at any tick
            WeaponData thinWeapon = Instantiate(_sword);
            thinWeapon.weaponId = "thin_test_sword";
            SwingPatternData thinPattern = ScriptableObject.CreateInstance<SwingPatternData>();
            EditorJsonUtility.FromJsonOverwrite(EditorJsonUtility.ToJson(_sword.swingPatterns[0]), thinPattern);
            thinPattern.patternId = "thin_test";
            thinPattern.sweepRadius = 0.02f;
            thinWeapon.swingPatterns = new[] { thinPattern };
            _cleanup.Add(thinWeapon);
            _cleanup.Add(thinPattern);

            float bladeLength = thinPattern.BladeLength(thinWeapon);
            SwingFrame frame = new SwingFrame(new Vector3(0f, 1.8f, 0f), 0f, 0f);
            Check(SwingGeometry.SampleCount(thinPattern, thinWeapon) > SwingGeometry.SampleCount(_sword.swingPatterns[0], _sword),
                $"a thinner sweep needs a denser sample grid ({SwingGeometry.SampleCount(thinPattern, thinWeapon)} vs {SwingGeometry.SampleCount(_sword.swingPatterns[0], _sword)})");

            foreach (float u in new[] { 0.15f, 0.5f, 0.85f })
            {
                SwingGeometry.BladeSegment(frame, thinPattern.ActivePose(u), bladeLength, out Vector3 b, out Vector3 t);
                Vector3 mid = Vector3.Lerp(b, t, 0.6f);
                reference = null;
                for (int p = 0; p < TickPlans.Length; p++)
                {
                    Scenario s = NewScenario(thinWeapon);
                    Dummy thin = AddBody(s, mid, 0.01f);
                    MeleeSwingStatus st = RunSwing(s, 0, TickPlans[p]);
                    string summary = Summarize(st, s);
                    if (reference == null) reference = summary;
                    Check(WasHit(st, thin), $"thin target on the path at u={u:F2}: hit with tick plan {p}");
                    Check(summary == reference, $"thin target u={u:F2}: tick plan {p} reports the same hit as the 1 ms plan");
                    Cooldown(2f);
                    s.Dispose();
                }

                // negative control: the same thin target moved off the path by more than radius + sweep is missed at every tick
                Scenario sm = NewScenario(thinWeapon);
                Dummy off = AddBody(sm, mid + Vector3.up * 0.25f, 0.01f);
                MeleeSwingStatus miss = RunSwing(sm, 0, TickPlans[0]);
                Check(!WasHit(miss, off), $"control: a thin target 0.25 m off the path at u={u:F2} is missed");
                Cooldown(2f);
                sm.Dispose();
            }
        }

        // ------------------------------------------------------------------------------------------------
        // 7. the swept segment follows the pattern data
        // ------------------------------------------------------------------------------------------------

        /// <summary>Independent re-derivation of the active pose of a two-key pattern (the shipped patterns have two keys).</summary>
        private static void ExpectedSegment(SwingPatternData p, float bladeLength, Vector3 eye, float yaw, float pitch, float u, out Vector3 b, out Vector3 t)
        {
            // piecewise linear between the keys, derived here without calling the pattern's own interpolation
            int seg = 0;
            while (seg < p.keys.Length - 2 && u > p.keys[seg + 1].t) seg++;
            SwingKey k0 = p.keys[seg];
            SwingKey k1 = p.keys[seg + 1];
            float f = (u - k0.t) / (k1.t - k0.t);
            Vector3 basePos = Vector3.Lerp(k0.basePos, k1.basePos, f);
            float by = Mathf.Lerp(k0.bladeYaw, k1.bladeYaw, f);
            float bp = Mathf.Lerp(k0.bladePitch, k1.bladePitch, f);
            Quaternion aim = Quaternion.Euler(pitch, yaw, 0f);
            b = eye + aim * basePos;
            t = b + aim * (Quaternion.Euler(bp, by, 0f) * Vector3.forward) * bladeLength;
        }

        private void PathMatchesPattern()
        {
            Vector3 eye = new Vector3(0.4f, 1.7f, -0.3f);
            foreach (WeaponData w in new[] { _sword, _rapier })
            {
                for (int pi = 0; pi < w.swingPatterns.Length; pi++)
                {
                    SwingPatternData p = w.swingPatterns[pi];
                    float length = p.BladeLength(w);
                    int samples = SwingGeometry.SampleCount(p, w);

                    // the data function against an independent derivation, at several times, in a tilted frame
                    foreach (float u in new[] { 0f, 0.1f, 0.25f, 0.5f, 0.75f, 0.9f, 1f })
                    {
                        float time = p.windupTime + p.activeTime * u;
                        SwingPose pose = p.Evaluate(time);
                        SwingGeometry.BladeSegment(new SwingFrame(eye, 35f, 12f), pose, length, out Vector3 b, out Vector3 t);
                        ExpectedSegment(p, length, eye, 35f, 12f, u, out Vector3 eb, out Vector3 et);
                        Check((b - eb).magnitude < 1e-3f && (t - et).magnitude < 1e-3f, $"{p.patternId}: segment at u={u:F2} matches the pattern keys (base err {(b - eb).magnitude:F5}, tip err {(t - et).magnitude:F5})");
                        Near(length, (t - b).magnitude, 1e-3f, $"{p.patternId}: the segment keeps its length at u={u:F2}");
                    }

                    // windup starts at rest, recovery ends at rest, the active phase starts/ends on the first/last key
                    SwingPose rest = p.RestPose;
                    SwingPose w0 = p.Evaluate(0f);
                    SwingPose end = p.Evaluate(p.TotalTime + 0.5f);
                    Check((w0.Base - rest.Base).magnitude < 1e-4f && Mathf.Abs(w0.Yaw - rest.Yaw) < 1e-3f, $"{p.patternId}: the swing starts at the rest pose");
                    Check((end.Base - rest.Base).magnitude < 1e-4f && Mathf.Abs(end.Yaw - rest.Yaw) < 1e-3f, $"{p.patternId}: the swing ends at the rest pose");
                    Check((p.Evaluate(p.windupTime).Base - p.keys[0].basePos).magnitude < 1e-4f, $"{p.patternId}: the active phase begins on the first key");
                    Check((p.Evaluate(p.windupTime + p.activeTime).Base - p.keys[p.keys.Length - 1].basePos).magnitude < 1e-4f, $"{p.patternId}: the active phase ends on the last key");

                    // the AUTHORITY sweeps that same segment: a thin probe on the blade at several u values is hit at about that sample
                    float previousFirst = -1f;
                    foreach (float u in new[] { 0.1f, 0.3f, 0.5f, 0.7f, 0.9f })
                    {
                        ExpectedSegment(p, length, new Vector3(0f, 1.8f, 0f), 0f, 0f, u, out Vector3 b, out Vector3 t);
                        Vector3 probe = Vector3.Lerp(b, t, 0.6f);

                        Scenario s = NewScenario(w);
                        Dummy d = AddBody(s, probe, 0.03f);
                        MeleeSwingStatus st = RunSwing(s, pi);
                        bool hit = WasHit(st, d);
                        Check(hit, $"{p.patternId}: a probe on the blade at u={u:F2} is struck");
                        if (hit)
                        {
                            float first = HitOn(st, d).FirstSample / (float)samples;
                            bool consistent = p.kind == SwingKind.Thrust
                                ? first >= 0f && first <= 1f
                                : first >= u - 0.13f && first <= u + 0.01f;
                            Check(consistent, $"{p.patternId}: the probe at u={u:F2} was first touched at {first:F3} of the sweep");
                            if (p.kind != SwingKind.Thrust) Check(first > previousFirst, $"{p.patternId}: probes later on the path are touched later ({first:F3} > {previousFirst:F3})");
                            previousFirst = first;
                        }
                        Cooldown(2f);
                        s.Dispose();
                    }
                }
            }

            // the first-person visual of the real rig uses the same function in the same frame
            PlayerRig rig = FindFirstObjectByType<PlayerRig>();
            Check(rig != null && rig.Loadout != null && rig.Loadout.Visual != null && !rig.Loadout.Visual.IsRanged, "the real rig shows a melee weapon visual");
            if (rig == null || rig.Loadout == null || rig.Loadout.Visual == null) return;

            WeaponVisualRig visual = rig.Loadout.Visual;
            WeaponData held = rig.ActiveWeapon;
            Check(held == _sword, "the warrior rig holds the sword");
            for (int pi = 0; pi < held.swingPatterns.Length; pi++)
            {
                SwingPatternData p = held.swingPatterns[pi];
                Vector3 transitionBase = rig.Look.AimTransform.InverseTransformPoint(visual.transform.position);
                Quaternion transitionRotation = Quaternion.Inverse(rig.Look.AimTransform.rotation) * visual.transform.rotation;
                visual.PlaySwing(held, p);
                Check(visual.IsSwinging && visual.Pattern == p, $"{p.patternId}: the visual rig plays this pattern");
                Check(rig.TryGetAimPose(out Vector3 eyeNow, out float yawNow, out float pitchNow), "the rig reports an aim pose");
                SwingFrame rigFrame = new SwingFrame(eyeNow, yawNow, pitchNow);
                Transform aim = rig.Look.AimTransform;
                Check(Quaternion.Angle(aim.rotation, rigFrame.Rotation) < 0.05f, "the camera has no roll: the authority frame (yaw, pitch) is the camera rotation");

                foreach (float time in new[] { 0f, p.windupTime * 0.5f, p.windupTime, p.windupTime + p.activeTime * 0.25f, p.windupTime + p.activeTime * 0.5f,
                             p.windupTime + p.activeTime * 0.75f, p.windupTime + p.activeTime, p.windupTime + p.activeTime + p.recoveryTime * 0.5f })
                {
                    visual.ApplySwingTime(time);
                    SwingGeometry.BladeSegment(rigFrame, p.Evaluate(time), p.BladeLength(held), out Vector3 eb, out Vector3 et);
                    if (time < p.Timing.Windup || time > p.Timing.ActiveEnd)
                    {
                        // Non-damaging phases blend from the captured ready/previous pose; active geometry above stays exact.
                        WeaponVisualRig.SamplePresentation(p, p.Timing, time, transitionBase, transitionRotation, false,
                            out Vector3 position, out Quaternion rotation);
                        eb = rigFrame.Eye + rigFrame.Rotation * position;
                        et = eb + rigFrame.Rotation * rotation * Vector3.forward * p.BladeLength(held);
                    }
                    float baseErr = (visual.BladeBase - eb).magnitude;
                    float tipErr = (visual.BladeTip - et).magnitude;
                    Check(baseErr < 2e-3f && tipErr < 2e-3f, $"{p.patternId}: visual segment at t={time:F3} matches phase presentation (exact authority samples while active; base {baseErr:F5}, tip {tipErr:F5})");
                }
            }
        }

        // ------------------------------------------------------------------------------------------------
        // 8. overhead versus horizontal
        // ------------------------------------------------------------------------------------------------

        private void OverheadVersusHorizontal()
        {
            float pitchToChest = Mathf.Atan2(1.8f - 1.15f, 1.5f) * Mathf.Rad2Deg;

            // humanoid at 1.5 m, aim at the chest: the slash lands on the body, the overhead comes down on the head
            {
                Scenario s = NewScenario(_sword, pitch: pitchToChest);
                // D1c2: the arm volumes are switched off here. The left-to-right slash arrives from the left and its first touch is
                // the left arm (x -0.43, r 0.10): the 210-sample grid finds that touch at sample 51 and the 20 ms window closes while
                // the arm is still the deepest volume, whereas the old 66-sample grid rounded the window up to 22.7 ms and let the
                // torso overtake by 2 mm. Arms are geometrically correct for the slash; this section pins body-vs-head multipliers,
                // so the target has no arms (arm contacts are pinned in the locations section).
                Dummy h = AddHumanoid(s, new Vector3(0f, 0f, 1.5f), withArms: false);
                MeleeSwingStatus slash = RunSwing(s, Slash);
                Check(slash.Hits.Count == 1 && slash.Hits[0].Location == HitLocation.Body, $"aimed at the chest, the horizontal slash lands on the body ({(slash.Hits.Count > 0 ? slash.Hits[0].Location.ToString() : "none")})");
                Cooldown(2f);
                MeleeSwingStatus over = RunSwing(s, Overhead);
                Check(over.Hits.Count == 1 && over.Hits[0].Location == HitLocation.Head, $"aimed at the same chest, the overhead lands on the head ({(over.Hits.Count > 0 ? over.Hits[0].Location.ToString() : "none")})");
                if (slash.Hits.Count == 1 && over.Hits.Count == 1)
                {
                    float ratio = over.Hits[0].Amount / slash.Hits[0].Amount;
                    Near(1.5f * _sword.swingPatterns[2].damageMultiplier, ratio, 1e-3f, "overhead head hit / slash body hit = head 1.5 x the overhead pattern's own multiplier (data)");
                }
                Cooldown(2f);
                s.Dispose();

                // the thrust of the rapier aimed at the chest lands on the body
                Scenario sr = NewScenario(_rapier, pitch: pitchToChest);
                AddHumanoid(sr, new Vector3(0f, 0f, 1.5f));
                MeleeSwingStatus thrust = RunSwing(sr, 0);
                Check(thrust.Hits.Count == 1 && thrust.Hits[0].Location == HitLocation.Body, $"aimed at the chest, the rapier thrust lands on the body ({(thrust.Hits.Count > 0 ? thrust.Hits[0].Location.ToString() : "none")})");
                Cooldown(2f);
                sr.Dispose();
            }

            // a high, thin target above the line of fire: only the overhead reaches it
            {
                Vector3 high = new Vector3(0f, 1.8f + 0.35f, 1.7f);
                Scenario s = NewScenario(_sword);
                Dummy d = AddBody(s, high, 0.12f);
                MeleeSwingStatus slash = RunSwing(s, Slash);
                Check(!WasHit(slash, d), "a small target 0.35 m above the eye line is missed by the horizontal slash");
                Cooldown(2f);
                MeleeSwingStatus back = RunSwing(s, SlashBack);
                Check(!WasHit(back, d), "and by the reverse slash");
                Cooldown(2f);
                MeleeSwingStatus over = RunSwing(s, Overhead);
                Check(WasHit(over, d), "but the overhead, which comes down through that space, hits it");
                Cooldown(2f);
                s.Dispose();
            }

            // a low aim puts the slash on the legs or feet (multiplier at most 0.6)
            {
                float pitchToFeet = Mathf.Atan2(1.8f - 0.3f, 1.5f) * Mathf.Rad2Deg;
                Scenario s = NewScenario(_sword, pitch: pitchToFeet);
                AddHumanoid(s, new Vector3(0f, 0f, 1.5f));
                MeleeSwingStatus low = RunSwing(s, Slash);
                bool lowOk = low.Hits.Count == 1 && (low.Hits[0].Location == HitLocation.Legs || low.Hits[0].Location == HitLocation.Feet);
                Check(lowOk, $"aimed at the feet, the slash lands on the legs or feet ({(low.Hits.Count > 0 ? low.Hits[0].Location.ToString() : "none")})");
                if (low.Hits.Count == 1) Check(low.Hits[0].Amount <= _sword.baseDamage * 0.6f + 1e-3f, $"and deals at most 0.6 of base damage ({low.Hits[0].Amount:F2})");
                Cooldown(2f);
                s.Dispose();
            }
        }

        // ------------------------------------------------------------------------------------------------
        // 9. forged intents (R1a rules, carried to the swing request)
        // ------------------------------------------------------------------------------------------------

        private void ForgedIntents()
        {
            Scenario s = NewScenario(_sword);
            Dummy t = AddBody(s, new Vector3(0f, 1.5f, 1.8f), 0.3f);
            Swinger who = s.Who;

            bool Try(MeleeSwingIntent intent, out string reason) => _authority.TryBeginMeleeSwingAsOwner(intent, out _, out reason);
            int rejectedBefore = _authority.RejectedCount;
            int startedAtStart = _authority.SwingsStarted;

            Check(!Try(Intent(who, 0, instigator: 999999), out string r), "unknown instigator id is rejected (" + r + ")");
            Check(r.Contains("unknown instigator"), "  ... for the identity rule");
            Check(!Try(Intent(who, 0, weaponId: "ember_rapier"), out r) && r.Contains("not the equipped weapon"), "a weapon the instigator does not hold is rejected (" + r + ")");
            Check(!Try(Intent(who, 0, weaponId: ""), out r) && r.Contains("not the equipped weapon"), "an empty weapon id is rejected");
            Check(!Try(Intent(who, 0, weaponId: "hollow_claw"), out r) && r.Contains("not the equipped weapon"), "an enemy weapon id is rejected");
            Check(!Try(Intent(who, 7), out r) && r.Contains("not allowed"), "a pattern index the weapon does not have is rejected (" + r + ")");
            Check(!Try(Intent(who, -1), out r) && r.Contains("not allowed"), "a negative pattern index is rejected");
            // D1b: combo steps exist now. A step 1 request without a running chain is refused by the authority's own chain state
            // (the old "does not exist yet" wording no longer applies); the full forged-step matrix is in EmberdelveCombatFlowGate.
            Check(!Try(Intent(who, 1, combo: 1), out r) && r.Contains("no running combo chain"), "combo step 1 with no running chain is rejected (" + r + ")");
            Check(!Try(Intent(who, 0, combo: -3), out r) && r.Contains("does not exist"), "a negative combo step is rejected");
            Check(!Try(Intent(who, 0, yaw: 140f), out r) && r.Contains("aim snapshot"), "an aim snapshot 140 degrees from the authoritative pose is rejected (" + r + ")");
            Check(!Try(Intent(who, 0, pitch: 80f), out r) && r.Contains("aim snapshot"), "an aim snapshot 80 degrees off in pitch is rejected");
            Check(!Try(Intent(who, 0, yaw: float.NaN), out r) && r.Contains("aim snapshot"), "a NaN aim snapshot is rejected");
            Check(t.Received.Count == 0, "none of the forged requests did any damage");
            Check(_authority.ActiveSwingCount == 0 && _authority.SwingsStarted == startedAtStart, "none of the forged requests started a swing");
            Check(_authority.RejectedCount - rejectedBefore == 11, $"every forged request was counted as rejected ({_authority.RejectedCount - rejectedBefore} of 11)");

            // an aim within tolerance is fine (the snapshot is compared, not used)
            int startedBefore = _authority.SwingsStarted;
            who.Yaw = 4f;
            Check(Try(Intent(who, 0, yaw: 0f), out r), "an aim snapshot 4 degrees off the authoritative pose is within tolerance (" + r + ")");
            Check(_authority.SwingsStarted == startedBefore + 1, "that request started one swing");
            who.Yaw = 0f;
            // the swing it started sweeps the AUTHORITY's pose (yaw 4), not the client's claim (yaw 0)
            _clock += 0.05f; _authority.StepSwings();
            Check(!Try(Intent(who, 0), out r) && r.Contains("swing is already in progress"), "a second swing cannot start while one is running (" + r + ")");
            for (int i = 0; i < 400; i++) { _clock += 0.01f; _authority.StepSwings(); }
            Cooldown(2f);

            // sequence replay and ordering
            int seq = CombatRegistry.NextSequence(who.CombatId);
            Check(Try(Intent(who, 0, sequence: seq), out r), "a fresh sequence is accepted");
            for (int i = 0; i < 400; i++) { _clock += 0.01f; _authority.StepSwings(); }
            Cooldown(2f);
            Check(!Try(Intent(who, 0, sequence: seq), out r) && r.Contains("sequence"), "the same sequence replayed is rejected (" + r + ")");
            Check(!Try(Intent(who, 0, sequence: seq - 1), out r) && r.Contains("sequence"), "an older sequence is rejected");
            Check(!Try(Intent(who, 0, sequence: 0), out r) && r.Contains("sequence"), "sequence 0 is rejected");
            Check(Try(Intent(who, 0, sequence: seq + 100), out r), "a larger sequence is accepted (gaps are fine)");
            while (CombatRegistry.NextSequence(who.CombatId) < seq + 100) { }
            for (int i = 0; i < 400; i++) { _clock += 0.01f; _authority.StepSwings(); }

            // the swing in progress and the sequence are judged on the authority clock only (the intent has no timestamp field
            // at all). A full sword slash is 0.84 s; the cooldown rule itself is exercised in the cooldown section below.
            Cooldown(2f);
            Check(Try(Intent(who, 0), out r), "after a full cooldown a swing is accepted");
            _clock += 0.50f; _authority.StepSwings();
            Check(!Try(Intent(who, 0), out r) && r.Contains("in progress"), "begin while the previous swing still runs is rejected (" + r + ")");
            _clock += 0.36f; _authority.StepSwings();      // 0.86 s: the swing is over and the interval floor (0.84 s) has passed
            Check(_authority.ActiveSwingCount == 0, "the swing is over after its 0.84 s");
            Check(Try(Intent(who, 0), out r), "0.86 s after the begin a new swing is accepted (" + r + ")");
            for (int i = 0; i < 400; i++) { _clock += 0.01f; _authority.StepSwings(); }
            Cooldown(2f);

            // dead instigator
            who.Dead = true;
            Check(!Try(Intent(who, 0), out r) && r.Contains("dead"), "a dead instigator cannot begin a swing (" + r + ")");
            who.Dead = false;

            // death mid-swing: no damage after the instigator dies
            {
                Cooldown(2f);
                Check(Begin(s, Slash, out int id, out r), "begin a swing that will be cut short");
                _clock += 0.25f; _authority.StepSwings();
                who.Dead = true;
                for (int i = 0; i < 200; i++) { _clock += 0.005f; _authority.StepSwings(); }
                _authority.TryGetMeleeSwingStatus(id, out MeleeSwingStatus st);
                Check(st.Finished && st.EndReason == SwingEndReason.InstigatorDied, $"the swing ends when its instigator dies ({st.EndReason})");
                Check(st.Hits.Count == 0, "and no hit lands after death");
                who.Dead = false;
                Cooldown(2f);
            }

            // an unregistered (gone) instigator
            {
                Scenario gone = NewScenario(_sword);
                AddBody(gone, new Vector3(0f, 1.5f, 1.8f), 0.3f);
                Check(Begin(gone, Slash, out int id, out r), "begin a swing for an entity that will vanish");
                CombatRegistry.Unregister(gone.Who.CombatId, gone.Who);
                _clock += 0.5f; _authority.StepSwings();
                _authority.TryGetMeleeSwingStatus(id, out MeleeSwingStatus st);
                Check(st.Finished && st.EndReason == SwingEndReason.InstigatorGone && st.Hits.Count == 0, "a swing whose instigator left the registry ends and deals nothing");
                Cooldown(2f);
                gone.Dispose();
            }

            // weapons that cannot swing at all
            {
                Scenario crossbow = NewScenario(_crossbow);
                Check(!_authority.TryBeginMeleeSwingAsOwner(Intent(crossbow.Who, 0), out _, out r) && r.Contains("no swing patterns"), "a ranged weapon cannot begin a melee swing (" + r + ")");
                // far from every other fixture of this section, so the empty-air claw swing below can only hit what it is given (nothing)
                Scenario claw = NewScenario(_claw, new Vector3(500f, 1.8f, 500f));
                Check(_authority.TryBeginMeleeSwingAsOwner(Intent(claw.Who, 0), out int clawSwing, out r), "D1c4: the enemy claw now has a swept swing (" + r + ")");
                _clock += 1.0f; _authority.StepSwings();
                Check(_authority.TryGetMeleeSwingStatus(clawSwing, out MeleeSwingStatus clawStatus) && clawStatus.Finished && clawStatus.Hits.Count == 0, "and a claw swing at nothing runs to its end without a hit");
                Cooldown(2f);
                Scenario rapier = NewScenario(_rapier);
                Check(!_authority.TryBeginMeleeSwingAsOwner(Intent(rapier.Who, 1), out _, out r) && r.Contains("not allowed"), "the rapier has no pattern 1 (a sword pattern index does not carry over) (" + r + ")");
                Check(!_authority.TryBeginMeleeSwingAsOwner(Intent(rapier.Who, 0, weaponId: "ashen_short_sword"), out _, out r) && r.Contains("not the equipped weapon"), "claiming the sword while holding the rapier is rejected");
                PoselessAttacker poseless = new PoselessAttacker { CombatId = CombatRegistry.AllocateId(), Weapon = _sword };
                CombatRegistry.Register(poseless.CombatId, poseless);
                s.Registered.Add(poseless);
                MeleeSwingIntent pi = new MeleeSwingIntent(poseless.CombatId, _sword.weaponId, 0, 0, 0f, 0f, CombatRegistry.NextSequence(poseless.CombatId));
                Check(!_authority.TryBeginMeleeSwingAsOwner(pi, out _, out r) && r.Contains("aim pose"), "an entity with no authoritative aim pose cannot swing (" + r + ")");
                who.HasPose = false;
                Check(!Try(Intent(who, 0), out r) && r.Contains("aim pose"), "a pose that cannot be read is rejected (" + r + ")");
                who.HasPose = true;
            }

            // the swing a client starts is placed from the authority's pose, never from the client's aim claim
            {
                Cooldown(2f);
                Scenario p = NewScenario(_sword, yaw: 0f);
                Dummy straight = AddBody(p, new Vector3(0f, 1.5f, 1.8f), 0.3f);
                Dummy rightSide = AddBody(p, new Vector3(1.8f, 1.5f, 0f), 0.3f);
                // the client claims it looks 8 degrees right (within tolerance), the authority holds yaw 0
                MeleeSwingIntent claim = Intent(p.Who, 0, yaw: 8f);
                Check(_authority.TryBeginMeleeSwingAsOwner(claim, out int id, out r), "a slightly different claim is accepted");
                for (int i = 0; i < 300; i++) { _clock += 0.005f; _authority.StepSwings(); }
                _authority.TryGetMeleeSwingStatus(id, out MeleeSwingStatus st);
                Check(WasHit(st, straight) && !WasHit(st, rightSide), "the sweep used the authoritative pose (yaw 0): the forward target is hit and the one 90 degrees right is not");
                Cooldown(2f);
                p.Dispose();
            }

            Cooldown(2f);
            s.Dispose();
        }

        // ------------------------------------------------------------------------------------------------
        // 10. guard / enemy claw / legacy melee
        // ------------------------------------------------------------------------------------------------

        private void GuardAndLegacy()
        {
            // guard from the front, through the real DamageResolver path
            {
                Scenario s = NewScenario(_sword);
                Dummy g = AddBody(s, new Vector3(0f, 1.5f, 1.8f), 0.3f);
                g.GuardArc = 120f;
                g.GuardReduction = 0.5f;
                g.GuardForward = new Vector3(0f, 0f, -1f);   // faces the attacker at the origin
                RunSwing(s, Slash);
                Check(g.Received.Count == 1 && g.Received[0].Blocked, "a swing from the guarded front is blocked");
                if (g.Received.Count == 1) Near(_sword.baseDamage * 0.5f, g.Received[0].Amount, 1e-3f, "the block halves the damage (existing guard rule)");
                Cooldown(2f);
                s.Dispose();

                // the same guard, attacker behind the defender
                Scenario behind = NewScenario(_sword, eye: new Vector3(0f, 1.8f, 3.6f), yaw: 180f);
                Dummy g2 = AddBody(behind, new Vector3(0f, 1.5f, 1.8f), 0.3f);
                g2.GuardArc = 120f;
                g2.GuardReduction = 0.5f;
                g2.GuardForward = new Vector3(0f, 0f, -1f);   // still faces -Z; the attacker is at +Z, behind it
                RunSwing(behind, Slash);
                Check(g2.Received.Count == 1 && !g2.Received[0].Blocked, "a swing from behind the guard is not blocked");
                if (g2.Received.Count == 1) Near(_sword.baseDamage, g2.Received[0].Amount, 1e-3f, "and deals full damage");
                Cooldown(2f);
                behind.Dispose();
            }

            // the legacy arc-less melee path: refused for an entity with a swing pose, unchanged for an enemy
            {
                Scenario s = NewScenario(_sword);
                Dummy t = AddBody(s, new Vector3(0f, 1.5f, 1.5f), 0.3f);
                AttackIntent forged = new AttackIntent(s.Who.CombatId, EntityKind.LocalPlayer, _sword, 1f, s.Who.CombatCenter,
                    Vector3.forward, 2.9f, CombatRegistry.NextSequence(s.Who.CombatId), 0f);
                Check(!_authority.SubmitAttackAsOwner(forged, new HitCandidate(t.CombatId, t.Center, true), out string reason) && reason.Contains("swept swing"),
                    "a melee SubmitAttack from an entity with a swing pose is refused (" + reason + ")");
                Check(t.Received.Count == 0, "and deals nothing (a forged 4 m reach cannot bypass the sweep)");

                PoselessAttacker claw = new PoselessAttacker { CombatId = CombatRegistry.AllocateId(), Weapon = _claw, Point = Origin + new Vector3(0f, 1.15f, 2.0f) };
                CombatRegistry.Register(claw.CombatId, claw);
                s.Registered.Add(claw);
                Vector3 victimCenter = s.Who.CombatCenter;
                AttackIntent clawIntent = new AttackIntent(claw.CombatId, EntityKind.Enemy, _claw, 1f, claw.Point,
                    victimCenter - claw.Point, _claw.reach + 0.4f, CombatRegistry.NextSequence(claw.CombatId), 0f);
                float takenBefore = s.Who.Taken;
                bool ok = _authority.SubmitAttackAsOwner(clawIntent, new HitCandidate(s.Who.CombatId, victimCenter, false), out reason);
                Check(ok && Mathf.Abs((s.Who.Taken - takenBefore) - _claw.baseDamage) < 1e-3f, $"the legacy path for a pose-less claw holder (a fake with no swing pose) is unchanged: 15 damage ({s.Who.Taken - takenBefore:F2}; {reason})");
                Check(s.Who.Taken > 0f, "the claw hit reached the player-side entity");

                // D1c4: a claw holder WITH a swing pose (a real EnemyAgent) is refused on the legacy path like any melee weapon.
                Scenario poseClaw = NewScenario(_claw);
                poseClaw.Who.Kind = EntityKind.Enemy; poseClaw.Who.TeamId = CombatTeams.MonsterTeam;
                float victimBefore = s.Who.Taken;
                AttackIntent poseIntent = new AttackIntent(poseClaw.Who.CombatId, EntityKind.Enemy, _claw, 1f, poseClaw.Who.CombatCenter,
                    victimCenter - poseClaw.Who.CombatCenter, _claw.reach + 0.4f, CombatRegistry.NextSequence(poseClaw.Who.CombatId), 0f);
                Check(!_authority.SubmitAttackAsOwner(poseIntent, new HitCandidate(s.Who.CombatId, victimCenter, false), out reason) && reason.Contains("swept swing"),
                    "D1c4: a claw SubmitAttack from an entity with a swing pose is refused (" + reason + ")");
                Check(Mathf.Abs(s.Who.Taken - victimBefore) < 1e-4f, "and deals nothing");
                Cooldown(2f);
                s.Dispose();
            }
        }

        // ------------------------------------------------------------------------------------------------
        // 10b. weapon swap mid-swing (M1) and the cooldown floor (M-d)
        // ------------------------------------------------------------------------------------------------

        /// <summary>D1c5: the sword's impact zones as literals (0.7 from the hilt, 0.9 from t 0.35, 1.0 from t 0.70), independent of the data.</summary>
        private static float LiteralSwordZone(float t) => t >= 0.70f ? 1.0f : t >= 0.35f ? 0.9f : 0.7f;

        /// <summary>
        /// D1c5 (D1b review 3b): everything above runs with the two internal test seams (legacy pattern selection on, impact zones
        /// off). This section switches both to their PRODUCTION values: the chain's own patterns are the only step-0 patterns,
        /// the chain multipliers apply, and the impact zone is part of the damage. Expectations are literals (26 base, chain
        /// 1.00 / 1.05 / 1.10, body x1.0, the sword's bands).
        /// </summary>
        private void ProductionConfiguration()
        {
            bool legacy = _authority.AllowLegacyPatternSelection;
            bool zones = _authority.ImpactZonesEnabled;
            _authority.AllowLegacyPatternSelection = false;
            _authority.ImpactZonesEnabled = true;
            try
            {
                Near(26f, _sword.baseDamage, 1e-4f, "production: the sword's base damage is the literal 26");

                // the overhead cannot be picked at step 0 any more
                {
                    Scenario s = NewScenario(_sword);
                    AddBody(s, new Vector3(0f, 1.5f, 1.8f), 0.33f);
                    bool ok = _authority.TryBeginMeleeSwingAsOwner(Intent(s.Who, Overhead), out int id, out string why);
                    Check(!ok && id == 0 && why.Contains("chain"), $"production: the overhead is refused at combo step 0 ({why})");
                    ok = _authority.TryBeginMeleeSwingAsOwner(Intent(s.Who, Slash), out id, out why);
                    Check(!ok && why.Contains("chain"), $"production: the left-to-right back slash is refused at combo step 0 ({why})");
                    Cooldown(3f);
                    s.Dispose();
                }

                // the three chain steps, real selection, zones on: damage = 26 x step x body 1.0 x literal zone of the measured contact t
                {
                    Scenario s = NewScenario(_sword);
                    Dummy d = AddBody(s, new Vector3(0f, 1.5f, 1.8f), 0.33f);
                    float[] step = { 1.00f, 1.05f, 1.10f };
                    int[] pattern = { SlashBack, Slash, Overhead };   // the chain opens right-to-left (Dark and Darker order)
                    for (int i = 0; i < 3; i++)
                    {
                        d.Received.Clear();
                        bool ok = _authority.TryBeginMeleeSwingAsOwner(Intent(s.Who, pattern[i], null, null, i), out int id, out string why);
                        Check(ok, $"production: chain step {i + 1} is accepted with its own pattern ({why})");
                        if (!ok) break;
                        float simulated = 0f;
                        MeleeSwingStatus st = default;
                        while (simulated < 4f)
                        {
                            _clock += 0.005f;
                            simulated += 0.005f;
                            _authority.StepSwings();
                            if (_authority.TryGetMeleeSwingStatus(id, out st) && st.Finished) break;
                        }

                        Check(st.ComboStep == i && Mathf.Abs(st.StepMultiplier - step[i]) < 1e-5f, $"production: step {i + 1} carries the chain multiplier x{step[i]:F2} ({st.StepMultiplier:F2})");
                        bool hit = d.Received.Count == 1 && st.Hits.Count == 1;
                        Check(hit, $"production: step {i + 1} hits the body once");
                        if (hit)
                        {
                            MeleeSwingHit h = st.Hits[0];
                            float zone = LiteralSwordZone(h.ContactT);
                            bool onEdge = Mathf.Abs(h.ContactT - 0.35f) < 0.005f || Mathf.Abs(h.ContactT - 0.70f) < 0.005f;
                            Check(onEdge || Mathf.Abs(h.ZoneMultiplier - zone) < 1e-5f, $"production: step {i + 1} zone x{h.ZoneMultiplier:F2} is the literal band for t={h.ContactT:F3} (x{zone:F2})");
                            Near(26f * step[i] * 1.0f * h.ZoneMultiplier, d.Received[0].Amount, 1e-3f,
                                $"production: step {i + 1} damage = 26 x {step[i]:F2} x body 1.0 x zone {h.ZoneMultiplier:F2}");
                        }
                    }

                    Check(!_authority.TryGetExpectedComboStep(s.Who.CombatId, out int next) || next == 0, "production: the finisher reset the chain");
                    Cooldown(3f);
                    s.Dispose();
                }

                // near vs far along the blade: the zone band follows where on the blade the contact is (hilt end weaker than tip)
                {
                    float nearZone = -1f, farZone = -1f;
                    foreach ((float z, bool isNear) c in new[] { (1.0f, true), (2.0f, false) })
                    {
                        Scenario s = NewScenario(_sword);
                        Dummy d = AddBody(s, new Vector3(0f, 1.5f, c.z), 0.3f);
                        MeleeSwingStatus st = RunSwing(s, SlashBack);   // the chain's opening slash (right-to-left)
                        bool hit = d.Received.Count == 1 && st.Hits.Count == 1;
                        Check(hit, $"production: the slash hits a body {c.z:F1} m ahead");
                        if (hit)
                        {
                            float zone = st.Hits[0].ZoneMultiplier;
                            if (c.isNear) nearZone = zone; else farZone = zone;
                            Near(26f * zone, d.Received[0].Amount, 1e-3f, $"production: body at {c.z:F1} m: damage = 26 x zone {zone:F2}");
                        }
                        Cooldown(3f);
                        s.Dispose();
                    }

                    Check(nearZone > 0f && farZone > 0f && nearZone < farZone, $"production: a near contact (zone x{nearZone:F2}) deals less than a far one (x{farZone:F2})");
                }
            }
            finally
            {
                _authority.AllowLegacyPatternSelection = legacy;
                _authority.ImpactZonesEnabled = zones;
            }
        }

        private void WeaponSwapAndCooldown()
        {
            // swap during the windup (0.24 s): the swing ends, nothing is ever hit, even when the sword comes back
            {
                Scenario s = NewScenario(_sword);
                Dummy left = AddBody(s, new Vector3(-0.8f, 1.5f, 1.0f), 0.3f);
                Check(Begin(s, Slash, out int id, out string reason), "swap test: begin (" + reason + ")");
                _clock += 0.10f; _authority.StepSwings();
                s.Who.Weapon = _crossbow;
                _clock += 0.02f; _authority.StepSwings();
                _authority.TryGetMeleeSwingStatus(id, out MeleeSwingStatus st);
                Check(st.Finished && st.EndReason == SwingEndReason.WeaponChanged, $"a weapon swap during the windup ends the swing ({st.EndReason})");
                s.Who.Weapon = _sword;
                for (int i = 0; i < 200; i++) { _clock += 0.01f; _authority.StepSwings(); }
                Check(left.Received.Count == 0 && st.Hits.Count == 0, "no hit lands after the swap, not even when the sword is back in hand");
                Check(_authority.ActiveSwingCount == 0, "the swapped swing is gone");
                Cooldown(2f);
                s.Dispose();
            }

            // swap during the active phase: a hit made before the swap is kept, nothing after it
            {
                Scenario s = NewScenario(_sword);
                Dummy left = AddBody(s, new Vector3(-0.8f, 1.5f, 1.0f), 0.3f);      // touched at the very start of the sweep
                Dummy right = AddBody(s, new Vector3(0.8f, 1.5f, 1.0f), 0.3f);      // touched late in the sweep
                Check(Begin(s, Slash, out int id, out string reason), "swap test 2: begin (" + reason + ")");
                _clock += 0.35f; _authority.StepSwings();                          // 0.11 s into the 0.30 s active phase
                Check(left.Received.Count == 1, "the target touched before the swap has been hit");
                s.Who.Weapon = _crossbow;
                for (int i = 0; i < 200; i++) { _clock += 0.01f; _authority.StepSwings(); }
                _authority.TryGetMeleeSwingStatus(id, out MeleeSwingStatus st);
                Check(st.Finished && st.EndReason == SwingEndReason.WeaponChanged, $"a weapon swap during the active phase ends the swing ({st.EndReason})");
                Check(left.Received.Count == 1 && right.Received.Count == 0, "the earlier hit is kept and the later target is never hit");
                s.Who.Weapon = _sword;
                Cooldown(2f);
                s.Dispose();
            }

            // the crossbow cannot begin a melee swing and the swapped-away swing cannot be resumed by a later begin id
            {
                Scenario s = NewScenario(_crossbow);
                Check(!_authority.TryBeginMeleeSwingAsOwner(Intent(s.Who, 0), out _, out string reason) && reason.Contains("no swing patterns"), "the crossbow in hand cannot begin a melee swing");
                s.Dispose();
            }

            // M-d: the interval between two swings is the weapon cooldown, floored at the full length of the last swing
            {
                Scenario s = NewScenario(_rapier);
                Check(Begin(s, 0, out int id, out string reason), "rapier cooldown test: begin (" + reason + ")");
                _clock += 0.52f; _authority.StepSwings();     // the 0.50 s thrust is over; 0.8 x 0.68 s = 0.544 s has not passed
                Check(_authority.ActiveSwingCount == 0, "the thrust is over");
                Check(!Begin(s, 0, out _, out reason) && reason.Contains("cooldown"), "0.52 s after the begin the weapon cooldown still holds (" + reason + ")");
                _clock += 0.04f; _authority.StepSwings();     // 0.56 s
                Check(Begin(s, 0, out _, out reason), "0.56 s after the begin the cooldown has expired (" + reason + ")");
                _clock += 2f; _authority.StepSwings();
                s.Dispose();

                // a wall stop ends the swing early (about 0.61 s) but the next swing still waits the full 0.84 s of the pattern
                Scenario w = NewScenario(_sword);
                AddBody(w, new Vector3(0f, 1.5f, 2.0f), 0.3f);
                AddWall(w, new Vector3(0f, 1.8f, 1.65f), new Vector3(4f, 4f, 0.3f));
                Check(Begin(w, Slash, out id, out reason), "floor test: begin (" + reason + ")");
                _clock += 0.70f; _authority.StepSwings();
                _authority.TryGetMeleeSwingStatus(id, out MeleeSwingStatus st);
                Check(st.WallStopped && st.Finished, $"the swing was stopped by the wall and is over at 0.70 s ({st.EndReason})");
                Check(!Begin(w, SlashBack, out _, out reason) && reason.Contains("cooldown"), "0.70 s: refused (cooldown, " + reason + ")");
                _clock += 0.08f; _authority.StepSwings();     // 0.78 s: past 0.8 x 0.92 = 0.736 s but before the 0.84 s floor
                Check(!Begin(w, SlashBack, out _, out reason) && reason.Contains("cooldown"), "0.78 s: still refused, the pattern's 0.84 s is the floor after a wall stop (" + reason + ")");
                _clock += 0.08f; _authority.StepSwings();     // 0.86 s
                Check(Begin(w, SlashBack, out _, out reason), "0.86 s: accepted (" + reason + ")");
                _clock += 2f; _authority.StepSwings();
                w.Dispose();
            }
        }

        // ------------------------------------------------------------------------------------------------
        // 11. the real rig: thin client, real time
        // ------------------------------------------------------------------------------------------------

        private IEnumerator RealRigSection()
        {
            PlayerRig rig = FindFirstObjectByType<PlayerRig>();
            Check(rig != null && !rig.IsDead, "a living player rig is available");
            if (rig == null) yield break;

            MeleeCombat melee = rig.GetComponent<MeleeCombat>();
            Check(melee != null && melee.enabled && melee.Weapon == _sword, "the rig's MeleeCombat is live with the sword");
            if (melee == null) yield break;

            // park every enemy and take the rig to an empty spot so the only things in range are this gate's props
            foreach (EnemyAgent a in FindObjectsByType<EnemyAgent>(FindObjectsSortMode.None)) a.enabled = false;
            rig.Movement.enabled = false;
            CharacterController controller = rig.GetComponent<CharacterController>();
            if (controller != null) controller.enabled = false;
            rig.transform.SetPositionAndRotation(Origin + new Vector3(0f, 0.15f, 0f), Quaternion.identity);
            if (controller != null) controller.enabled = true;
            Physics.SyncTransforms();
            yield return null;

            // real time from here on: the authority runs on Time.time and steps itself every FixedUpdate
            _authority.SetClock(null);
            _authority.AutoStepSwings = true;

            Scenario s = new Scenario();
            _scenarioCleanup.Add(s);
            s.Who = null;

            Vector3 eye = rig.AimOrigin;
            Vector3 forward = rig.AimDirection;
            Check(Mathf.Abs(forward.y) < 0.01f, "the rig looks level at the test spot");

            Dummy target = new Dummy { CombatId = CombatRegistry.AllocateId(), Center = eye + forward * 1.8f + Vector3.down * 0.3f };
            target.Shapes.Add(new HitShape(HitLocation.Body, target.Center, target.Center, 0.33f));
            CombatRegistry.Register(target.CombatId, target);
            s.Targets.Add(target);
            s.Registered.Add(target);

            WeaponVisualRig visual = rig.Loadout.Visual;
            float cooldownReadoutBefore = melee.CooldownRemaining;

            int acceptedBefore = _authority.AcceptedCount;
            int startedBefore = _authority.SwingsStarted;
            int sequenceBefore = _authority.LastSequenceFor(rig.CombatId);

            // the thin client: one call, no hit data
            Check(melee.TryBeginSwing(0), "MeleeCombat asks the authority to begin the slash and is accepted");
            Check(_authority.SwingsStarted == startedBefore + 1, "the authority started exactly one swing");
            Check(_authority.LastSequenceFor(rig.CombatId) > sequenceBefore, "the request consumed a fresh command sequence");
            Check(melee.IsSwinging && melee.ActivePattern == _sword.swingPatterns[0], "MeleeCombat plays the slash pattern");
            Check(visual.IsSwinging && visual.Pattern == _sword.swingPatterns[0], "the weapon visual plays the same pattern");
            Check(_authority.TryGetMeleeSwingStatus(melee.LastSwingId, out MeleeSwingStatus acceptedHud), "the HUD reads an accepted swing interval");
            Check(melee.CooldownRemaining > 0f && melee.CooldownRemaining <= acceptedHud.RepeatInterval + 1e-3f && cooldownReadoutBefore <= 0f, $"the HUD cooldown readout runs within its accepted interval ({melee.CooldownRemaining:F2} s)");
            Check(!melee.TryBeginSwing(0), "a second begin is refused while the swing runs");

            float guard = 0f;
            while (melee.IsSwinging && guard < 3f) { guard += Time.deltaTime; yield return null; }
            Check(!melee.IsSwinging, "the swing ends (real time)");
            yield return null;

            Check(target.Received.Count == 1, $"the target in front took exactly one hit from the real rig ({target.Received.Count})");
            if (target.Received.Count == 1)
            {
                DamageInfo info = target.Received[0];
                float expected = _sword.baseDamage * rig.GetOutgoingDamageMultiplier(_sword) * _table.Multiplier(info.Location);
                Near(expected, info.Amount, 0.01f, $"the damage is base x the rig's own multiplier x the {info.Location} location ({info.Amount:F2})");
                Check(info.SourceCombatId == rig.CombatId && info.SourceKind == EntityKind.LocalPlayer, "attributed to the rig");
            }

            yield return new WaitForSeconds(0.15f);
            Check(_authority.AcceptedCount == acceptedBefore + 1, "the authority counted one accepted hit");
            Check(_authority.TryGetMeleeSwingStatus(melee.LastSwingId, out MeleeSwingStatus done) && done.Finished && done.EndReason == SwingEndReason.Completed,
                "the finished swing is reported as completed");
            Check(done.TurnClampedDegrees == 0f, $"D1c2: real input never reaches the turn ceiling (clamped {done.TurnClampedDegrees:F3} degrees)");
            Check(!visual.IsSwinging, "the visual returned to rest");
            // D1b: this used to wait 0.6 s and expect the alternating slash from ChoosePatternIndex (and the overhead for a
            // sprinting press). The pattern order is now the weapon's combo chain, owned by the authority: a second press
            // INSIDE the combo window continues it at step 1 (the left-to-right back slash; the chain opens right-to-left). The sprint-overhead placeholder is gone.
            yield return new WaitForSeconds(0.1f);
            Check(_authority.TryGetExpectedComboStep(rig.CombatId, out int expectedStep) && expectedStep == 1, "the authority expects combo step 1 next");
            Check(melee.TryBeginSwing() && melee.ComboStep == 1 && melee.ActivePattern.kind == SwingKind.HorizontalLeftToRight, "the next press is combo step 1, the left-to-right back slash");
            guard = 0f;
            while (melee.IsSwinging && guard < 3f) { guard += Time.deltaTime; yield return null; }
            Check(target.Received.Count == 2, "the reverse slash hits the same target again (a new swing)");
            Check(_authority.TryGetMeleeSwingStatus(melee.LastSwingId, out MeleeSwingStatus reverseStatus) && reverseStatus.TurnClampedDegrees == 0f,
                "D1c2: the second real swing is not clamped either");
            yield return new WaitForSeconds(0.6f);

            // M1: a weapon swap in the middle of a swing ends it everywhere: no hit lands, the visual is rebuilt, the client idles
            {
                int hitsBeforeSwap = target.Received.Count;
                WeaponVisualRig oldVisual = rig.Loadout.Visual;
                Check(melee.TryBeginSwing(0), "swap test: a swing begins");
                int swapSwing = melee.LastSwingId;
                yield return null;
                // D1b: a PLAYER swap during a swing is now refused (EmberdelveCombatFlowGate pins that). This case keeps the
                // authority's defence in depth covered with a FORCED change (class apply / respawn path), which is not a player swap.
                Check(rig.Loadout.ForceSwitchToSlot(1) && rig.ActiveWeapon == _crossbow, "the loadout is forced to the crossbow during the windup");
                yield return new WaitForSeconds(1.0f);
                Check(target.Received.Count == hitsBeforeSwap, "no invisible swing lands after the swap");
                Check(_authority.TryGetMeleeSwingStatus(swapSwing, out MeleeSwingStatus swapStatus) && swapStatus.Finished && swapStatus.EndReason == SwingEndReason.WeaponChanged,
                    $"the authority ended the swing as WeaponChanged ({swapStatus.EndReason})");
                Check(!melee.enabled && !melee.IsSwinging, "the melee client is off and not swinging");
                Check(oldVisual == null && rig.Loadout.Visual != null && !rig.Loadout.Visual.IsSwinging, "the old weapon visual (and its trail) was destroyed with the swap");
                Check(rig.Loadout.SwitchToSlot(0) && melee.enabled && !melee.IsSwinging && rig.Loadout.Visual != null && !rig.Loadout.Visual.IsSwinging, "swapping back leaves a clean sword visual");
                visual = rig.Loadout.Visual;      // the old rig was destroyed by the swap
                yield return new WaitForSeconds(1.2f);
            }

            // a wall in front of the rig: the swing stops, the client recoils, nothing behind is hit
            GameObject wall = new GameObject("SweptGate_RigWall");
            s.Props.Add(wall);
            wall.transform.position = eye + forward * 1.65f;
            BoxCollider box = wall.AddComponent<BoxCollider>();
            box.size = new Vector3(4f, 4f, 0.3f);
            wall.layer = LayerMask.NameToLayer(GameConfig.LevelLayerName);
            Physics.SyncTransforms();
            yield return null;

            int hitsBeforeWall = target.Received.Count;
            Check(melee.TryBeginSwing(0), "a swing towards the wall is accepted (the wall is found by the sweep, not by the client)");
            int wallSwing = melee.LastSwingId;
            guard = 0f;
            bool sawRecoil = false;
            while (melee.IsSwinging && guard < 3f)
            {
                if (melee.RecoilStarted) sawRecoil = true;
                guard += Time.deltaTime;
                yield return null;
            }

            yield return new WaitForSeconds(0.2f);   // the authority finishes the recoil on its own fixed step
            Check(_authority.TryGetMeleeSwingStatus(wallSwing, out MeleeSwingStatus wallStatus) && wallStatus.WallStopped && wallStatus.Finished,
                $"the authority stopped the swing at the wall ({wallStatus.EndReason}, phase {wallStatus.Phase})");
            Check(sawRecoil, "MeleeCombat entered its recoil when the authority reported the wall stop");
            Check(target.Received.Count == hitsBeforeWall, "the target behind the wall took no damage");
            yield return new WaitForSeconds(0.6f);
            Check(!visual.IsSwinging, "the visual finished its recoil and is back at rest");

            // leave the scene as found
            foreach (EnemyAgent a in FindObjectsByType<EnemyAgent>(FindObjectsSortMode.None)) a.enabled = true;
            yield return null;
        }
    }
}
