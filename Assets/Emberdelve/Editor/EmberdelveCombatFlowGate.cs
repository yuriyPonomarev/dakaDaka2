using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using Emberdelve.InputLayer;
using Emberdelve.AI;
using Emberdelve.BladeDefense;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Items;
using Emberdelve.Player;
using Emberdelve.Combat;
using Emberdelve.UI;

namespace Emberdelve.EditorTools
{
    /// <summary>
    /// D1b gate: the combat FLOW on top of the D1a swept melee. Everything authority-side runs on a real
    /// LocalCombatAuthority with an INJECTED clock stepped by hand (no wall-clock sleeps), against plain fake entities
    /// and, where the real thing matters (guard, movement speed, HUD), the real PlayerRig in the Level01 scene.
    /// Only the hit-slow walking distance and the sprint/guard measurement run in real time, because they measure real
    /// frames of the real PlayerMovement / StatusComponent.
    ///
    /// Run: Unity -batchmode -nographics -projectPath . -logFile Logs/d1b_combatflow.log
    ///      -executeMethod Emberdelve.EditorTools.EmberdelveCombatFlowGate.Run
    /// </summary>
    [InitializeOnLoad]
    public static class EmberdelveCombatFlowGate
    {
        private const string Pending = "Emberdelve.CombatFlowGate.Pending";

        static EmberdelveCombatFlowGate()
        {
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.EnteredPlayMode && SessionState.GetBool(Pending, false))
                {
                    SessionState.SetBool(Pending, false);
                    new GameObject("CombatFlow_Gate").AddComponent<CombatFlowHarness>();
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

    public sealed class CombatFlowHarness : MonoBehaviour
    {
        private const string Tag = "[CombatFlowGate]";

        private int _checks;
        private int _failures;
        private readonly List<UnityEngine.Object> _cleanup = new List<UnityEngine.Object>();
        private readonly List<IDisposable> _scenarioCleanup = new List<IDisposable>();

        private LocalCombatAuthority _authority;
        private GameConfig _config;
        private float _clock = 5000f;

        private static readonly Vector3 Origin = new Vector3(3000f, 0f, 3000f);

        // Walking is measured here, near the world origin: at 3000 m a float position cannot resolve the first
        // millimetres of a 1000 fps acceleration ramp (the step is below the float spacing), so the rig sticks.
        private static readonly Vector3 WalkOrigin = new Vector3(-400f, 0f, -400f);

        private WeaponData _sword;
        private WeaponData _rapier;
        private WeaponData _shield;
        private WeaponData _crossbow;
        private WeaponData _bow;
        private WeaponData _claw;
        private WeaponData _maul;
        private HitLocationTable _table;

        // ------------------------------------------------------------------------------------------------
        // Fakes
        // ------------------------------------------------------------------------------------------------

        private sealed class Swinger : ICombatEntity, IActiveWeaponProvider, IWeaponChangeSource, IAimPoseProvider, IGuardStateSource
        {
            /// <summary>D1c5: the guard flag a fixture raises (the authority refuses a swing begin while it is up).</summary>
            public bool Guarding;
            public bool IsGuarding => Guarding;
            public int CombatId { get; set; }
            /// <summary>Bumped by a fixture to model every weapon change, also an A-B-A swap that leaves Weapon unchanged.</summary>
            public int WeaponChangeCount { get; set; }
            public EntityKind Kind { get; set; } = EntityKind.LocalPlayer;
            public int TeamId { get; set; } = CombatTeams.PlayerTeam;
            public Vector3 Eye = new Vector3(0f, 1.8f, 0f);
            public float Yaw;
            public float Pitch;
            public WeaponData Weapon;
            public bool Dead;
            public float Taken;

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
                return true;
            }
        }

        private sealed class PoselessAttacker : ICombatEntity, IActiveWeaponProvider
        {
            public int CombatId { get; set; }
            public EntityKind Kind => EntityKind.Enemy;
            public int TeamId => CombatTeams.MonsterTeam;
            public Vector3 Point;
            public WeaponData Weapon;
            public Vector3 CombatCenter => Point;
            public Collider HeadCollider => null;
            public WeaponData ActiveWeapon => Weapon;
            public bool IsDead => false;
            public float CurrentHealth => 100f;
            public float MaxHealth => 100f;
            public void ApplyDamage(DamageInfo damage) { }
        }

        private sealed class ProjectileAttacker : ICombatEntity, IActiveWeaponProvider, IAimPoseProvider
        {
            public int CombatId { get; set; }
            public Vector3 Point;
            public Vector3 AimDirection;
            public WeaponData Weapon;
            public EntityKind Kind => EntityKind.Enemy;
            public int TeamId => CombatTeams.MonsterTeam;
            public Vector3 CombatCenter => Point;
            public Collider HeadCollider => null;
            public WeaponData ActiveWeapon => Weapon;
            public bool IsDead => false;
            public float CurrentHealth => 100f;
            public float MaxHealth => 100f;
            public void ApplyDamage(DamageInfo damage) { }
            public bool TryGetAimPose(out Vector3 eye, out float yawDegrees, out float pitchDegrees)
            {
                eye = Point;
                return SwingGeometry.TryAimAngles(AimDirection, out yawDegrees, out pitchDegrees);
            }
        }

        /// <summary>A hostile target with analytic hit shapes, optional armour, optional plain guard and an optional status host.</summary>
        private sealed class Dummy : ICombatEntity, IHitShapeProvider, IArmorRatingProvider, IDefenseProvider, IStatusTarget
        {
            public int CombatId { get; set; }
            public EntityKind Kind => EntityKind.Enemy;
            public int TeamId { get; set; } = CombatTeams.MonsterTeam;
            public readonly List<HitShape> Shapes = new List<HitShape>();
            public Vector3 Center = new Vector3(0f, 1.5f, 2f);
            public bool Dead;
            public float Armor;
            public float GuardReduction;
            public StatusComponent Status;
            public readonly List<DamageInfo> Received = new List<DamageInfo>();

            public Vector3 CombatCenter => Center;
            public Collider HeadCollider => null;
            public bool IsDead => Dead;
            public float CurrentHealth => 100000f;
            public float MaxHealth => 100000f;

            public void ApplyDamage(DamageInfo damage) { Received.Add(damage); }
            public void CollectHitShapes(List<HitShape> shapes) { shapes.AddRange(Shapes); }
            public float GetArmorRating() => Armor;

            public bool TryGetGuardReduction(in DamageInfo incoming, out float reduction)
            {
                reduction = GuardReduction;
                return GuardReduction > 0f;
            }

            public void ApplyStatus(StatusSpec spec, int sourceCombatId) { if (Status != null) Status.ApplyStatus(spec, sourceCombatId); }
            public void ClearStatuses() { if (Status != null) Status.ClearStatuses(); }
            public bool HasStatus(StatusKind kind) => Status != null && Status.HasStatus(kind);
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

        private WeaponData _arcSword;

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

        private GameObject AddWall(Scenario s, Vector3 center, Vector3 size)
        {
            GameObject wall = new GameObject("FlowGate_Wall");
            wall.transform.position = center + Origin;
            BoxCollider box = wall.AddComponent<BoxCollider>();
            box.size = size;
            int level = LayerMask.NameToLayer(GameConfig.LevelLayerName);
            if (level >= 0) wall.layer = level;
            s.Props.Add(wall);
            Physics.SyncTransforms();
            return wall;
        }

        /// <summary>A box collider on a named layer (or the level layer as a trigger) standing in for a chest, a gate pillar or a pickup.</summary>
        private GameObject AddProp(Scenario s, Vector3 center, Vector3 size, string layerName, bool trigger)
        {
            GameObject prop = new GameObject("FlowGate_Prop_" + layerName + (trigger ? "_trigger" : string.Empty));
            prop.transform.position = center + Origin;
            BoxCollider box = prop.AddComponent<BoxCollider>();
            box.size = size;
            box.isTrigger = trigger;
            int layer = LayerMask.NameToLayer(layerName);
            Check(layer >= 0, $"the layer '{layerName}' exists in the project");
            if (layer >= 0) prop.layer = layer;
            s.Props.Add(prop);
            Physics.SyncTransforms();
            return prop;
        }

        private MeleeSwingIntent Intent(Swinger who, int step, int pattern, string weaponId = null, int? instigator = null)
        {
            int id = instigator ?? who.CombatId;
            return new MeleeSwingIntent(id, weaponId ?? (who.Weapon != null ? who.Weapon.weaponId : string.Empty), pattern, step,
                who.Yaw, who.Pitch, CombatRegistry.NextSequence(id));
        }

        /// <summary>The chain's own pattern for a step of the weapon the swinger holds.</summary>
        private static int ChainPattern(WeaponData weapon, int step) =>
            weapon.HasComboChain && step >= 0 && step < weapon.comboSteps.Length ? weapon.comboSteps[step].patternIndex : 0;

        // Real-time marks of the sword's first chain step, derived from its data: a press just inside the recovery, and a moment
        // shortly after the whole swing (well inside the 0.5 s press buffer).
        private SwingTiming FirstStepTiming => _arcSword.ComboTiming(0, _arcSword.swingPatterns[_arcSword.comboSteps[0].patternIndex]);
        private float RecoveryPressAt => FirstStepTiming.ActiveEnd + 0.25f * FirstStepTiming.Recovery;
        private float AfterFirstSwing => FirstStepTiming.Total + 0.22f;

        private bool Begin(Scenario s, int step, out int id, out string reason) =>
            _authority.TryBeginMeleeSwingAsOwner(Intent(s.Who, step, ChainPattern(s.Who.Weapon, step)), out id, out reason);

        private bool BeginForged(Swinger who, int step, int pattern, out string reason, string weaponId = null) =>
            _authority.TryBeginMeleeSwingAsOwner(Intent(who, step, pattern, weaponId), out _, out reason);

        /// <summary>Steps the authority clock until the swing is finished (or 4 s). Returns the final status.</summary>
        private MeleeSwingStatus Finish(int id, float dt = 0.005f)
        {
            float simulated = 0f;
            MeleeSwingStatus status = default;
            while (simulated < 4f)
            {
                _clock += dt;
                simulated += dt;
                _authority.StepSwings();
                if (_authority.TryGetMeleeSwingStatus(id, out status) && status.Finished) break;
            }

            _authority.TryGetMeleeSwingStatus(id, out status);
            return status;
        }

        /// <summary>Begin + run to the end of one chain step. Returns the status (default when refused).</summary>
        private MeleeSwingStatus SwingStep(Scenario s, int step, out bool accepted, out string reason)
        {
            float begun = _clock;
            accepted = Begin(s, step, out int id, out reason);
            if (!accepted) return default;
            MeleeSwingStatus status = Finish(id);

            // A quick chain step (short recovery scale) can end before the authority's repeat interval of that swing: the weapon cooldown x the
            // authority grace (0.8), or the step's own length when longer. A follow-up before it is refused ("attack faster than weapon
            // cooldown"), so a caller that chains steps back to back waits for it. Derived from the loaded weapon and pattern.
            WeaponData weapon = s.Who.Weapon;
            if (weapon != null)
            {
                int patternIndex = ChainPattern(weapon, step);
                SwingPatternData pattern = weapon.swingPatterns != null && patternIndex >= 0 && patternIndex < weapon.swingPatterns.Length ? weapon.swingPatterns[patternIndex] : null;
                float repeatFloor = Mathf.Max(0.8f * weapon.cooldown, weapon.ComboTiming(step, pattern).Total);
                if (_clock - begun < repeatFloor) Advance(begun + repeatFloor + 0.005f - _clock);
            }

            return status;
        }

        private void Advance(float seconds)
        {
            _clock += seconds;
            _authority.StepSwings();
        }

        private static MeleeSwingHit HitOn(MeleeSwingStatus status, Dummy d)
        {
            for (int i = 0; i < status.Hits.Count; i++) if (status.Hits[i].TargetCombatId == d.CombatId) return status.Hits[i];
            return default;
        }

        private static bool WasHit(MeleeSwingStatus status, Dummy d)
        {
            for (int i = 0; i < status.Hits.Count; i++) if (status.Hits[i].TargetCombatId == d.CombatId) return true;
            return false;
        }

        private void ZonesOff() { _authority.ImpactZonesEnabled = false; }
        private void ZonesOn() { _authority.ImpactZonesEnabled = true; }

        // ------------------------------------------------------------------------------------------------
        // Run
        // ------------------------------------------------------------------------------------------------

        private IEnumerator Start()
        {
            yield return null;
            yield return null;

            Section("setup and data", SetupAndData);
            Section("combo chain: multipliers, window, finisher", ComboChain);
            Section("swing end without a tick", NoTickNeeded);
            Section("combo chain: forged steps", ForgedSteps);
            Section("combo chain: resets", ChainResets);
            Section("rapier thrust chain", RapierChain);
            Section("impact zones", ImpactZones);
            Section("hit slow (authority rules)", HitSlowRules);
            Section("D1c1: review follow-ups (authority)", D1c1Authority);
            Section("D1c5: props do not stop swings", PropsDoNotBlock);
            Section("D1c5: display timing derives from the pattern", DisplayTimingFromPattern);
            Section("B1: slow merge keeps each slow's own magnitude and duration", SlowMerge);

            foreach (IEnumerator part in new[] { RigSections() })
            {
                IEnumerator e = part;
                while (true)
                {
                    bool more = false;
                    try { more = e.MoveNext(); }
                    catch (Exception ex) { _failures++; Debug.LogException(ex); }
                    if (!more) break;
                    yield return e.Current;
                }
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
        // 1. setup and data
        // ------------------------------------------------------------------------------------------------

        private void SetupAndData()
        {
            _sword = AssetDatabase.LoadAssetAtPath<WeaponData>(EmberdelvePaths.Data + "/Weapon_AshenShortSword.asset");
            _arcSword = EmberdelveDarkerCombat.FlatArcFixture(_sword);   // mechanics scenarios swing flat arcs with the sword's own timing
            _rapier = AssetDatabase.LoadAssetAtPath<WeaponData>(EmberdelvePaths.Data + "/Weapon_EmberRapier.asset");
            _shield = AssetDatabase.LoadAssetAtPath<WeaponData>(EmberdelvePaths.Data + "/Weapon_EmberbulwarkShield.asset");
            _crossbow = AssetDatabase.LoadAssetAtPath<WeaponData>(EmberdelvePaths.Data + "/Weapon_EmberCrossbow.asset");
            _bow = AssetDatabase.LoadAssetAtPath<WeaponData>(EmberdelvePaths.Data + "/Weapon_HollowBow.asset");
            _claw = AssetDatabase.LoadAssetAtPath<WeaponData>(EmberdelvePaths.Data + "/Weapon_HollowClaw.asset");
            _maul = AssetDatabase.LoadAssetAtPath<WeaponData>(EmberdelvePaths.Data + "/Weapon_WardenMaul.asset");
            Check(_arcSword != null && _rapier != null && _shield != null && _crossbow != null && _bow != null && _claw != null && _maul != null, "weapon assets are available");
            if (_arcSword == null || _rapier == null || _shield == null || _crossbow == null || _bow == null || _claw == null || _maul == null) throw new InvalidOperationException("weapon assets missing");

            GameConfig sceneConfig = AssetDatabase.LoadAssetAtPath<GameConfig>(EmberdelvePaths.Data + "/EmberdelveGameConfig.asset");
            _table = sceneConfig != null && sceneConfig.hitLocations != null ? sceneConfig.hitLocations : HitLocationTable.Default;
            Check(sceneConfig != null && sceneConfig.flow != null, "the game config carries the D1b flow tunables");
            if (sceneConfig != null && sceneConfig.flow != null)
            {
                Near(0.30f, sceneConfig.flow.guardBreakReductionScale, 1e-6f, "tunable: a broken guard keeps 30 percent of its mitigation");
                Near(0.80f, sceneConfig.flow.guardStaggerSeconds, 1e-6f, "tunable: guard stagger 0.8 s");
                Near(0.60f, sceneConfig.flow.guardStaggerMoveMultiplier, 1e-6f, "tunable: guard stagger move x0.6");
            }

            _config = ScriptableObject.CreateInstance<GameConfig>();
            _cleanup.Add(_config);
            _config.levelMask = LayerMask.GetMask(GameConfig.LevelLayerName);
            _config.hitLocations = null;

            GameObject go = new GameObject("CombatFlowGate_Authority");
            _cleanup.Add(go);
            _authority = go.AddComponent<LocalCombatAuthority>();
            typeof(LocalCombatAuthority).GetField("config", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(_authority, _config);
            _authority.SetClock(() => _clock);
            _authority.AutoStepSwings = false;
            _authority.ImpactZonesEnabled = false;      // each section says when it turns the zones on
            _authority.GuardReadyDelayEnabled = false;  // sections raise a guard and strike in the same instant; BladeDefenseOnRig proves the delay

            // ---- sword chain data: slash, slash, overhead at 1.00 / 1.05 / 1.10
            Check(_arcSword.HasComboChain && _arcSword.comboSteps.Length == 3, "the sword has a three-step combo chain");
            Check(_arcSword.comboSteps[0].patternIndex == 0 && _arcSword.comboSteps[1].patternIndex == 1 && _arcSword.comboSteps[2].patternIndex == 2,
                "sword chain patterns: slash L->R, slash R->L, overhead");
            Near(1.00f, _arcSword.comboSteps[0].damageMultiplier, 1e-6f, "sword step 1 multiplier 1.00");
            Near(1.05f, _arcSword.comboSteps[1].damageMultiplier, 1e-6f, "sword step 2 multiplier 1.05");
            Near(1.10f, _arcSword.comboSteps[2].damageMultiplier, 1e-6f, "sword step 3 multiplier 1.10");
            // Reference short sword (Docs/Tasks/DARKER_COMBAT_PORT_20261124.md): the window is ComboWindow.Close - Collision.Off of step 1;
            // the last step's pattern already holds its full recovery, so there is no extra finisher recovery.
            Near(0.36f, _arcSword.comboWindow, 1e-6f, "sword combo window 0.36 s after the recovery begins");
            Near(0f, _arcSword.comboFinisherRecovery, 1e-6f, "sword finisher recovery is part of the last pattern");
            // Reference short sword directions for the first two: -122 (down to the right), 375 (to the left, rising). The third
            // attack is a thrust by the user's decision of 25 Nov 2026 (the reference's third cut, 340, is kept outside the chain).
            Check(_sword.swingPatterns[0].kind == SwingKind.HorizontalLeftToRight && _sword.swingPatterns[1].kind == SwingKind.HorizontalRightToLeft
                  && _sword.swingPatterns[2].kind == SwingKind.Thrust && _sword.comboSteps[2].patternIndex == 2,
                "the chain's patterns are a cut down to the right (left-to-right by its measured path), a cut to the left, then a thrust");
            foreach (SwingPatternData p in _arcSword.swingPatterns)
            {
                Near(1f, p.damageMultiplier, 1e-6f, p.patternId + ": the pattern shape is x1 (the multiplier is the chain's)");
                // The reference slows an attacker to one "prepare" factor around the hit window and a lower (or equal) one inside it.
                Near(p.windupMoveMultiplier, p.recoveryMoveMultiplier, 1e-6f, p.patternId + ": windup and recovery share the prepare factor");
                Check(p.activeMoveMultiplier <= p.windupMoveMultiplier + 1e-6f, p.patternId + ": the hit phase is never faster than the prepare phase");
                Check(p.activeMoveMultiplier >= 0.5f && p.windupMoveMultiplier <= 1f, p.patternId + ": move factors stay inside 0.5..1");
            }

            Check(_rapier.HasComboChain && _rapier.comboSteps.Length == 4 && _rapier.comboSteps[0].patternIndex == 0 && _rapier.comboSteps[1].patternIndex == 1
                  && _rapier.comboSteps[2].patternIndex == 2 && _rapier.comboSteps[3].patternIndex == 3
                  && _rapier.swingPatterns[0].kind == SwingKind.Thrust && _rapier.swingPatterns[2].kind == SwingKind.Overhead,
                "the rapier chain is a thrust and three cuts, the third straight down (reference rapier)");
            Near(1.15f, _rapier.comboSteps[3].damageMultiplier, 1e-6f, "rapier step 4 multiplier 1.15");
            Near(1.00f, _rapier.comboSteps[0].damageMultiplier, 1e-6f, "rapier step 1 multiplier 1.00");
            Near(1.05f, _rapier.comboSteps[1].damageMultiplier, 1e-6f, "rapier step 2 multiplier 1.05");
            Near(1.10f, _rapier.comboSteps[2].damageMultiplier, 1e-6f, "rapier step 3 multiplier 1.10");

            // ---- the other D1b data
            Near(0.7f, _arcSword.ImpactMultiplier(0f), 1e-6f, "sword zone: hilt end 0.7");
            Near(0.7f, _arcSword.ImpactMultiplier(0.34f), 1e-6f, "sword zone: still 0.7 just below 0.35");
            Near(0.9f, _arcSword.ImpactMultiplier(0.35f), 1e-6f, "sword zone: 0.9 from 0.35");
            Near(0.9f, _arcSword.ImpactMultiplier(0.69f), 1e-6f, "sword zone: 0.9 up to 0.7");
            Near(1.0f, _arcSword.ImpactMultiplier(0.70f), 1e-6f, "sword zone: the tip band is full damage from 0.7");
            Near(1.0f, _arcSword.ImpactMultiplier(1f), 1e-6f, "sword zone: tip");
            Near(0.7f, _arcSword.ImpactMultiplier(float.NaN), 1e-6f, "a NaN contact parameter falls back to the first band");
            Near(1.0f, _crossbow.ImpactMultiplier(0.1f), 1e-6f, "a weapon without bands does full damage everywhere");
            Near(0.9f, _arcSword.hitSlowMultiplier, 1e-6f, "sword hit slow x0.9");
            Near(0.6f, _arcSword.hitSlowDuration, 1e-6f, "sword hit slow 0.6 s");
            Near(0.75f, _maul.hitSlowMultiplier, 1e-6f, "maul hit slow x0.75");
            Near(1.5f, _maul.hitSlowDuration, 1e-6f, "maul hit slow 1.5 s");
            Near(10f, _arcSword.heldMoveSpeedPenalty, 1e-6f, "sword held penalty 10 points");
            Near(17f, _rapier.heldMoveSpeedPenalty, 1e-6f, "rapier held penalty 17 points (reference rapier)");
            Near(8f, _shield.heldMoveSpeedPenalty, 1e-6f, "shield held penalty 8 points");
            // B1: ranged weapons use the same rule (1 - points / 300); authored in EmberdelveAssetLibrary.ApplyCombatFlow
            Near(4f, _bow.heldMoveSpeedPenalty, 1e-6f, "B1: bow held penalty 4 points (light)");
            Near(12f, _crossbow.heldMoveSpeedPenalty, 1e-6f, "B1: crossbow held penalty 12 points (heavier than the sword's 10)");
            Check(_bow.heldMoveSpeedPenalty < _rapier.heldMoveSpeedPenalty && _crossbow.heldMoveSpeedPenalty > _arcSword.heldMoveSpeedPenalty, "B1: ordering bow < rapier < sword < crossbow");
            Near(1f - 4f / 300f, CombatFlowRules.HeldFactor(_bow, null, false), 1e-6f, "B1: bow held factor 1 - 4/300 = 0.98667");
            Near(1f - 12f / 300f, CombatFlowRules.HeldFactor(_crossbow, null, false), 1e-6f, "B1: crossbow held factor 1 - 12/300 = 0.96");
            Near(1f - 12f / 300f, CombatFlowRules.HeldFactor(_crossbow, _shield, false), 1e-6f, "B1: a shield that does not contribute (two-handed crossbow) adds nothing");
            Near(10f, _arcSword.impactPower, 1e-6f, "sword impact power 10");
            Near(20f, _shield.impactResist, 1e-6f, "heater shield impact resist 20");
            Near(30f, _maul.impactPower, 1e-6f, "maul impact power 30");
            Check(!_shield.CanParry && !_shield.EffectiveCapabilities.HasFlag(DefenseCapability.Parry), "the shield cannot parry (data)");
            Near(0.65f, AssetDatabase.LoadAssetAtPath<CharacterClassData>(EmberdelvePaths.Data + "/Class_Warrior.asset").guardSpeedMultiplier, 1e-6f, "warrior blocking move multiplier is 0.65");

            // pure rules
            Check(!CombatFlowRules.IsGuardBroken(30f, 20f) && !CombatFlowRules.IsGuardBroken(20f, 20f) && !CombatFlowRules.IsGuardBroken(19.99f, 20f),
                "guard break rule: impact power never breaks a guard");
            Check(!CombatFlowRules.IsGuardBroken(0f, 0f) && !CombatFlowRules.IsGuardBroken(0f, 20f), "a source with no impact power (a skill) never breaks a guard");
            Near(1f - 18f / 300f, CombatFlowRules.HeldFactor(_arcSword, _shield, true), 1e-6f, "held factor sword + shield = 1 - 18/300");
            Near(1f - 10f / 300f, CombatFlowRules.HeldFactor(_arcSword, _shield, false), 1e-6f, "a shield that does not contribute adds no penalty");
        }

        // ------------------------------------------------------------------------------------------------
        // 2. combo chain
        // ------------------------------------------------------------------------------------------------

        private void ComboChain()
        {
            ZonesOff();
            float baseDamage = _arcSword.baseDamage;
            float[] steps = { 1.00f, 1.05f, 1.10f };

            // full chain on one body target, zero armour: damage = base x step multiplier, exactly, through the real damage path
            {
                Scenario s = NewScenario(_arcSword);
                Dummy d = AddBody(s, new Vector3(0f, 1.5f, 1.8f), 0.33f);
                for (int step = 0; step < 3; step++)
                {
                    float started = _clock;
                    MeleeSwingStatus st = SwingStep(s, step, out bool ok, out string why);
                    Check(ok, $"chain step {step + 1} is accepted ({why})");
                    Check(st.ComboStep == step, $"the status reports combo step {step}");
                    Check(st.Hits.Count == 1, $"step {step + 1} hits the target once ({st.Hits.Count})");
                    if (st.Hits.Count == 1)
                    {
                        Near(baseDamage * steps[step], st.Hits[0].Amount, 1e-3f, $"step {step + 1} damage = base {baseDamage} x {steps[step]:F2} (zero armour)");
                        Near(steps[step], st.StepMultiplier, 1e-6f, $"step {step + 1} reports its multiplier");
                    }

                    if (step < 2)
                    {
                        Check(_authority.TryGetExpectedComboStep(s.Who.CombatId, out int next) && next == step + 1, $"after step {step + 1} the authority expects step {step + 2} (it said {next})");
                    }
                    else
                    {
                        Check(_authority.TryGetExpectedComboStep(s.Who.CombatId, out int after) && after == 0, "after the last step the chain is complete (expected step 0)");
                    }
                }

                Check(d.Received.Count == 3, "three hits arrived at the target");
                Cooldown(3f);
                s.Dispose();
            }

            // with armour: the step multiplier is applied before the armour rule, so the number is exact
            foreach (float armour in new[] { 50f, 100f })
            {
                Scenario s = NewScenario(_arcSword);
                Dummy d = AddBody(s, new Vector3(0f, 1.5f, 1.8f), 0.33f);
                d.Armor = armour;
                SwingStep(s, 0, out _, out _);
                SwingStep(s, 1, out _, out _);
                MeleeSwingStatus third = SwingStep(s, 2, out bool ok, out string why);
                Check(ok && third.Hits.Count == 1, $"armour {armour}: step 3 accepted and hits ({why})");
                if (third.Hits.Count == 1)
                {
                    float expected = baseDamage * 1.10f * 100f / (100f + armour);
                    Near(expected, third.Hits[0].Amount, 1e-3f, $"step 3 with armour {armour}: base x 1.10 x 100/(100+{armour})");
                }
                Cooldown(3f);
                s.Dispose();
            }

            // timing: each step's windup / recovery are the pattern's scaled by that step's authored scales (read from the canonical weapon data,
            // Docs/Tasks/SWORD_DATA_CANONICAL_20261124.md), the finisher adds comboFinisherRecovery
            {
                Scenario s = NewScenario(_arcSword);
                AddBody(s, new Vector3(0f, 1.5f, 1.8f), 0.33f);
                MeleeSwingStatus a = SwingStep(s, 0, out _, out _);
                MeleeSwingStatus b = SwingStep(s, 1, out _, out _);
                float startOfThird = _clock;
                Check(Begin(s, 2, out int thirdId, out _), "timing: step 3 accepted");
                _authority.TryGetMeleeSwingStatus(thirdId, out MeleeSwingStatus third);
                SwingPatternData p0 = _arcSword.swingPatterns[0], p1 = _arcSword.swingPatterns[1], p2 = _arcSword.swingPatterns[2];
                Near(p0.windupTime * _arcSword.comboSteps[0].windupScale, a.Timing.Windup, 1e-5f, "step 1 winds up at its authored scale of the shared pattern");
                ComboStep c1 = _arcSword.comboSteps[1], c2 = _arcSword.comboSteps[2];
                Check(c1.windupScale > 0f, $"step 2 has an authored windup scale (x{c1.windupScale:F2})");
                Near(p1.windupTime * c1.windupScale, b.Timing.Windup, 1e-5f, $"step 2 winds up at its authored x{c1.windupScale:F2}");
                Near(p1.recoveryTime * c1.recoveryScale, b.Timing.Recovery, 1e-5f, $"step 2 recovery is the pattern's at its authored x{c1.recoveryScale:F2}");
                Near(p2.recoveryTime * c2.recoveryScale + _arcSword.comboFinisherRecovery, third.Timing.Recovery, 1e-5f, "step 3 (the finisher) recovers comboFinisherRecovery longer");
                Near(p2.windupTime * c2.windupScale + p2.activeTime + p2.recoveryTime * c2.recoveryScale + _arcSword.comboFinisherRecovery, third.Timing.Total, 1e-5f,
                    "the finisher's whole swing is its scaled pattern plus exactly the finisher recovery");

                // the authority holds the next swing back for the finisher's full length
                Finish(thirdId);
                float finisherEnd = startOfThird + third.Timing.Total;
                _clock = finisherEnd - 0.05f;
                Check(!BeginForged(s.Who, 0, 0, out string tooSoon) && tooSoon.Contains("cooldown"), $"a new chain cannot start before the finisher's recovery ends ({tooSoon})");
                _clock = finisherEnd + 0.01f;
                Check(Begin(s, 0, out int again, out string okReason), $"after the finisher's recovery a fresh chain starts ({okReason})");
                Finish(again);
                Cooldown(3f);
                s.Dispose();
            }

            // the combo window opens when the RECOVERY begins and lasts comboWindow
            {
                Scenario s = NewScenario(_arcSword);
                AddBody(s, new Vector3(0f, 1.5f, 1.8f), 0.33f);
                float t0 = _clock;
                Check(Begin(s, 0, out int id, out _), "window: step 1 accepted");
                MeleeSwingStatus st = Finish(id);
                float recoveryStart = t0 + st.Timing.ActiveEnd;
                float windowEnd = recoveryStart + _arcSword.comboWindow;

                _clock = windowEnd - 0.02f;
                Check(_authority.TryGetExpectedComboStep(s.Who.CombatId, out int inside) && inside == 1, "window: 20 ms before the window ends the authority still expects step 2");
                _clock = windowEnd + 0.05f;
                Check(_authority.TryGetExpectedComboStep(s.Who.CombatId, out int outside) && outside == 0, "window: 50 ms after it ends the chain has reset to step 0");
                Check(!Begin(s, 1, out _, out string late) && late.Contains("combo window expired"), $"window: step 2 after the window is refused ({late})");
                float tFresh = _clock;
                Check(Begin(s, 0, out int fresh, out string freshWhy), $"window: step 1 after the window starts a fresh chain ({freshWhy})");
                Finish(fresh);
                Check(_authority.TryGetExpectedComboStep(s.Who.CombatId, out int restarted) && restarted == 1, "window: the fresh chain expects step 2 again");

                // continuing inside the window works, even right at the end of it
                _authority.TryGetMeleeSwingStatus(fresh, out MeleeSwingStatus freshStatus);
                float lastMoment = tFresh + freshStatus.Timing.ActiveEnd + _arcSword.comboWindow - 0.01f;
                _clock = lastMoment;
                Check(Begin(s, 1, out int late2, out string lateWhy), $"window: step 2 accepted 10 ms before the window closes ({lateWhy})");
                Finish(late2);
                Cooldown(3f);
                s.Dispose();
            }

            // a hit taken does NOT reset the chain (documented rule); another entity's chain is independent
            {
                Scenario s = NewScenario(_arcSword);
                AddBody(s, new Vector3(0f, 1.5f, 1.8f), 0.33f);
                Scenario other = NewScenario(_arcSword, new Vector3(40f, 1.8f, 0f));
                SwingStep(s, 0, out _, out _);
                s.Who.ApplyDamage(new DamageInfo(30f, Vector3.zero, Vector3.forward, Vector3.zero, 999, EntityKind.Enemy, DamageType.Physical, false, false, 0f));
                Check(_authority.TryGetExpectedComboStep(s.Who.CombatId, out int still) && still == 1, "taking a hit does not reset the chain (it still expects step 2)");
                Check(!Begin(other, 1, out _, out string otherWhy) && otherWhy.Contains("no running combo chain"), $"another entity cannot ride on this chain ({otherWhy})");
                MeleeSwingStatus second = SwingStep(s, 1, out bool ok, out string why);
                Check(ok, $"step 2 is accepted after taking a hit ({why})");
                Cooldown(3f);
                s.Dispose();
                other.Dispose();
            }
        }

        private void Cooldown(float seconds) => _clock += seconds;

        // ------------------------------------------------------------------------------------------------
        // D1c1: IsSwingInProgress on the clock, A-B-A weapon swap, per-entity state pruning
        // ------------------------------------------------------------------------------------------------

        private void D1c1Authority()
        {
            ZonesOff();

            // 1. IsSwingInProgress is evaluated against Now, with no step in between
            {
                Scenario s = NewScenario(_arcSword);
                AddBody(s, new Vector3(0f, 1.5f, 1.8f), 0.33f);
                Check(Begin(s, 0, out int id, out _), "in-progress: step 1 begins");
                _authority.TryGetMeleeSwingStatus(id, out MeleeSwingStatus st);
                float total = st.Timing.Total;
                float t0 = _clock;
                _authority.StepSwings();
                Check(_authority.IsSwingInProgress(s.Who.CombatId), "in-progress: running right after the begin");
                _clock = t0 + total - 0.01f;
                Check(_authority.IsSwingInProgress(s.Who.CombatId), "in-progress: 10 ms before the end time it is still running (no step ran)");
                _clock = t0 + total + 0.01f;
                Check(!_authority.IsSwingInProgress(s.Who.CombatId), "in-progress: 10 ms after the end time it is over although no step ran since (it used to lag to the next step)");
                Check(_authority.ActiveSwingIdFor(s.Who.CombatId) == id, "in-progress: the record is still unstepped (the answer came from the clock, not from the record)");
                _authority.StepSwings();
                Check(!_authority.IsSwingInProgress(s.Who.CombatId), "in-progress: still over after the step");
                Cooldown(3f);
                _authority.StepSwings();
                s.Dispose();
            }

            // 2. A-B-A inside one frame: the weapon in hand is the same again, the chain must still be gone
            {
                Scenario s = NewScenario(_arcSword);
                AddBody(s, new Vector3(0f, 1.5f, 1.8f), 0.33f);
                SwingStep(s, 0, out _, out _);
                Check(_authority.TryGetExpectedComboStep(s.Who.CombatId, out int e) && e == 1, "A-B-A: step 2 expected before the swap");
                s.Who.WeaponChangeCount += 2; // sword -> rapier -> sword with no step in between: Weapon is unchanged
                Check(s.Who.Weapon == _arcSword, "A-B-A: the weapon in hand is the sword again");
                Check(_authority.TryGetExpectedComboStep(s.Who.CombatId, out e) && e == 0, "A-B-A: the expected step is 0 (the chain is not live)");
                Check(!BeginForged(s.Who, 1, 1, out string r) && r.Contains("weapon changed"), $"A-B-A: step 2 is refused ({r})");
                Check(Begin(s, 0, out int fresh, out string why), $"A-B-A: step 1 starts a fresh chain ({why})");
                Finish(fresh);
                Check(_authority.TryGetExpectedComboStep(s.Who.CombatId, out e) && e == 1, "A-B-A: the fresh chain expects step 2");
                Check(Begin(s, 1, out int second, out why), $"A-B-A: with no further change step 2 is accepted ({why})");
                Finish(second);
                Cooldown(3f);
                _authority.StepSwings();
                s.Dispose();
            }

            // A-B-A in the middle of a swing ends it as WeaponChanged
            {
                Scenario s = NewScenario(_arcSword);
                AddBody(s, new Vector3(0f, 1.5f, 1.8f), 0.33f);
                Check(Begin(s, 0, out int id, out _), "A-B-A mid-swing: step 1 begins");
                Advance(0.1f);
                s.Who.WeaponChangeCount += 2;
                Advance(0.05f);
                _authority.TryGetMeleeSwingStatus(id, out MeleeSwingStatus st);
                Check(st.Finished && st.EndReason == SwingEndReason.WeaponChanged, $"A-B-A mid-swing: the swing ended as WeaponChanged ({st.EndReason})");
                Check(_authority.TryGetExpectedComboStep(s.Who.CombatId, out int e) && e == 0, "A-B-A mid-swing: no chain survives");
                Cooldown(3f);
                _authority.StepSwings();
                s.Dispose();
            }

            // 4. per-entity swing state is pruned when the entity leaves the registry
            {
                _authority.StepSwings();
                int floors = _authority.SwingFloorEntryCount;
                int chains = _authority.ChainEntryCount;
                Scenario s = NewScenario(_arcSword);
                AddBody(s, new Vector3(0f, 1.5f, 1.8f), 0.33f);
                SwingStep(s, 0, out _, out _);
                _authority.StepSwings();
                Check(_authority.SwingFloorEntryCount == floors + 1, $"prune: the cooldown-floor entry exists for the live swinger ({_authority.SwingFloorEntryCount} vs {floors})");
                Check(_authority.ChainEntryCount == chains + 1, "prune: the chain entry exists for the live swinger");
                Advance(0.05f);
                Check(_authority.SwingFloorEntryCount == floors + 1 && _authority.ChainEntryCount == chains + 1, "prune: a live entity keeps its entries across steps");
                Check(_authority.TryGetExpectedComboStep(s.Who.CombatId, out int e) && e == 1, "prune: the live chain still works");
                s.Dispose(); // unregisters
                _authority.StepSwings();
                Check(_authority.SwingFloorEntryCount == floors, "prune: the floor entry is removed once the entity is unregistered");
                Check(_authority.ChainEntryCount == chains, "prune: the chain entry is removed once the entity is unregistered");
                Cooldown(3f);
                _authority.StepSwings();
            }
        }

        private void NoTickNeeded()
        {
            // a swing whose time is over is not "in progress" even if no tick ran since (found in the real-time smoke: the
            // finisher's length equals the client cooldown, so a press one frame after it was refused until the next fixed step)
            ZonesOff();
            Scenario s = NewScenario(_arcSword);
            AddBody(s, new Vector3(0f, 1.5f, 1.8f), 0.33f);
            Check(Begin(s, 0, out int id, out _), "no tick: step 1 begins");
            _authority.TryGetMeleeSwingStatus(id, out MeleeSwingStatus st);
            Cooldown(st.Timing.Total + 0.001f);
            Check(Begin(s, 1, out int next, out string why), $"no tick: step 2 is accepted right after step 1's last instant without any step in between ({why})");
            Cooldown(0.2f);
            Check(!Begin(s, 2, out _, out why) && why.Contains("in progress"), $"no tick: a swing really still running is still refused ({why})");
            Cooldown(3f);
            _authority.StepSwings();
            s.Dispose();
        }

        // ------------------------------------------------------------------------------------------------
        // 3. forged combo steps
        // ------------------------------------------------------------------------------------------------

        private void ForgedSteps()
        {
            ZonesOff();
            Scenario s = NewScenario(_arcSword);
            Dummy d = AddBody(s, new Vector3(0f, 1.5f, 1.8f), 0.33f);
            Swinger who = s.Who;

            // no chain yet: every step above 0 is refused with the authority's own reason
            Check(!BeginForged(who, 1, 1, out string r) && r.Contains("no running combo chain"), $"step 2 as the first swing is refused ({r})");
            Check(!BeginForged(who, 2, 2, out r) && r.Contains("no running combo chain"), $"step 3 as the first swing is refused ({r})");
            Check(!BeginForged(who, 3, 0, out r) && r.Contains("does not exist"), $"step 4 does not exist ({r})");
            Check(!BeginForged(who, -1, 0, out r) && r.Contains("does not exist"), $"a negative step does not exist ({r})");
            Check(!BeginForged(who, 0, 1, out r) && r.Contains("not the chain's pattern"), $"step 1 with the second step's pattern is refused ({r})");
            Check(!BeginForged(who, 0, 2, out r) && r.Contains("not the chain's pattern"), $"step 1 with the overhead is refused: the overhead is only reachable as step 3 ({r})");
            Check(!BeginForged(who, 0, 9, out r) && r.Contains("not the chain's pattern"), $"step 1 with an unknown pattern is refused ({r})");
            Check(_authority.ActiveSwingCount == 0 && d.Received.Count == 0, "none of the forged first requests started a swing or dealt damage");

            // a legitimate step 1, then every kind of forged continuation
            MeleeSwingStatus first = SwingStep(s, 0, out bool ok, out string why);
            Check(ok, $"step 1 accepted ({why})");
            Check(_authority.TryGetExpectedComboStep(who.CombatId, out int expected) && expected == 1, "the authority expects step 2");
            int hitsAfterFirst = d.Received.Count;

            Check(!BeginForged(who, 2, 2, out r) && r.Contains("not the expected next step 1"), $"skipping ahead to step 3 is refused ({r})");
            Check(!BeginForged(who, 3, 0, out r) && r.Contains("does not exist"), $"skipping to a step that does not exist is refused ({r})");
            Check(!BeginForged(who, 1, 2, out r) && r.Contains("not the chain's pattern"), $"step 2 with the overhead pattern (wrong chain slot) is refused ({r})");
            Check(!BeginForged(who, 1, 0, out r) && r.Contains("not the chain's pattern"), $"step 2 with the first pattern is refused ({r})");
            Check(!BeginForged(who, 1, 1, out r, "ember_rapier") && r.Contains("not the equipped weapon"), $"the rapier's chain cannot be claimed with the sword in hand ({r})");
            Check(_authority.TryGetExpectedComboStep(who.CombatId, out expected) && expected == 1, "refused forgeries left the authority's chain state unchanged");

            // the legitimate step 2 is still accepted afterwards (no cooldown or sequence was spent by the refusals)
            MeleeSwingStatus second = SwingStep(s, 1, out ok, out why);
            Check(ok, $"step 2 is accepted after the forged attempts ({why})");

            // repeat: step 2 again is refused, the chain now expects step 3
            Check(_authority.TryGetExpectedComboStep(who.CombatId, out expected) && expected == 2, "the authority now expects step 3");
            Check(!BeginForged(who, 1, 1, out r) && r.Contains("not the expected next step 2"), $"repeating step 2 is refused ({r})");

            // step 3 is accepted; claiming it again afterwards (replay) is refused because the chain is complete
            MeleeSwingStatus third = SwingStep(s, 2, out ok, out why);
            Check(ok, $"step 3 accepted ({why})");
            Check(!BeginForged(who, 2, 2, out r) && r.Contains("no running combo chain"), $"repeating step 3 after the finisher is refused ({r})");
            Check(d.Received.Count == hitsAfterFirst + 2, "exactly the two legitimate follow-up hits landed");

            // a stale sequence number cannot ride the chain either
            Cooldown(3f);
            SwingStep(s, 0, out ok, out why);
            int lastSequence = _authority.LastSequenceFor(who.CombatId);
            MeleeSwingIntent replay = new MeleeSwingIntent(who.CombatId, _arcSword.weaponId, 1, 1, who.Yaw, who.Pitch, lastSequence);
            Check(!_authority.TryBeginMeleeSwingAsOwner(replay, out _, out r) && r.Contains("stale"), $"a replayed command sequence is refused on a chain step ({r})");
            Cooldown(3f);

            // the rapier's chain forgeries (wrong chain)
            {
                Scenario rs = NewScenario(_rapier);
                AddBody(rs, new Vector3(0f, 1.6f, 1.8f), 0.33f);
                Check(!BeginForged(rs.Who, 1, _rapier.comboSteps[1].patternIndex, out r) && r.Contains("no running combo chain"), $"rapier step 2 without step 1 is refused ({r})");
                Check(!BeginForged(rs.Who, 0, 2, out r), $"a sword pattern index on the rapier is refused ({r})");
                Check(!BeginForged(rs.Who, 0, 0, out r, "ashen_short_sword") && r.Contains("not the equipped weapon"), $"the sword's id on the rapier is refused ({r})");
                rs.Dispose();
            }

            s.Dispose();
        }

        // ------------------------------------------------------------------------------------------------
        // 4. resets: weapon swap, death, wall stop
        // ------------------------------------------------------------------------------------------------

        private void ChainResets()
        {
            ZonesOff();

            // weapon swap between swings
            {
                Scenario s = NewScenario(_arcSword);
                AddBody(s, new Vector3(0f, 1.5f, 1.8f), 0.33f);
                SwingStep(s, 0, out _, out _);
                Check(_authority.TryGetExpectedComboStep(s.Who.CombatId, out int e) && e == 1, "swap: step 2 expected before the swap");
                s.Who.Weapon = _rapier;
                _authority.StepSwings();
                Check(_authority.TryGetExpectedComboStep(s.Who.CombatId, out e) && e == 0, "swap: with another weapon in hand the expected step is 0");
                s.Who.Weapon = _arcSword;
                _authority.StepSwings();
                Check(!BeginForged(s.Who, 1, 1, out string r) && r.Contains("no running combo chain"), $"swap: swapping back does not restore the chain ({r})");
                Check(Begin(s, 0, out int id, out _), "swap: step 1 starts a fresh chain");
                Finish(id);
                Cooldown(3f);
                s.Dispose();
            }

            // weapon swap in the middle of a swing
            {
                Scenario s = NewScenario(_arcSword);
                AddBody(s, new Vector3(0f, 1.5f, 1.8f), 0.33f);
                Check(Begin(s, 0, out int id, out _), "mid-swing swap: step 1 begins");
                Advance(0.1f);
                s.Who.Weapon = _rapier;
                Advance(0.05f);
                _authority.TryGetMeleeSwingStatus(id, out MeleeSwingStatus st);
                Check(st.Finished && st.EndReason == SwingEndReason.WeaponChanged, "mid-swing swap: the swing ended as WeaponChanged");
                s.Who.Weapon = _arcSword;
                _authority.StepSwings();
                Check(_authority.TryGetExpectedComboStep(s.Who.CombatId, out int e) && e == 0, "mid-swing swap: no chain survives");
                Cooldown(3f);
                s.Dispose();
            }

            // death between swings and during a swing
            {
                Scenario s = NewScenario(_arcSword);
                AddBody(s, new Vector3(0f, 1.5f, 1.8f), 0.33f);
                SwingStep(s, 0, out _, out _);
                s.Who.Dead = true;
                _authority.StepSwings();
                Check(_authority.TryGetExpectedComboStep(s.Who.CombatId, out int e) && e == 0, "death: a dead instigator has no chain");
                Check(!BeginForged(s.Who, 1, 1, out string r) && r.Contains("dead"), $"death: a dead instigator cannot swing ({r})");
                s.Who.Dead = false;
                Check(!BeginForged(s.Who, 1, 1, out r) && r.Contains("no running combo chain"), $"death: after respawn step 2 is refused ({r})");
                Check(Begin(s, 0, out int id, out _), "death: a fresh chain starts after respawn");
                Advance(0.1f);
                s.Who.Dead = true;
                Advance(0.05f);
                s.Who.Dead = false;
                _authority.StepSwings();
                Check(_authority.TryGetExpectedComboStep(s.Who.CombatId, out e) && e == 0, "death mid-swing: no chain survives");
                Cooldown(3f);
                s.Dispose();
            }

            // user decision 25 Nov 2026: a swing stopped by a wall ENDS the combo (the next attack starts the chain again)
            {
                Scenario s = NewScenario(_arcSword);
                AddBody(s, new Vector3(0f, 1.5f, 3.0f), 0.33f);
                SwingStep(s, 0, out _, out _);
                Check(_authority.TryGetExpectedComboStep(s.Who.CombatId, out int e) && e == 1, "wall: control, the chain continues after a free swing");
                AddWall(s, new Vector3(0f, 1.8f, 1.6f), new Vector3(8f, 4f, 0.3f));
                float t0 = _clock;
                Check(Begin(s, 1, out int id, out _), "wall: step 2 begins towards a wall");
                MeleeSwingStatus st = Finish(id);
                Check(st.WallStopped, $"wall: the swing was stopped by the wall ({st.EndReason})");
                float stopTime = st.Timing.Windup + st.Timing.Active * st.WallContactSample / Mathf.Max(1, st.SampleCount);
                float recoilEnd = t0 + stopTime + st.Timing.WallRecoil;
                Check(_authority.TryGetExpectedComboStep(s.Who.CombatId, out e) && e == 0, "wall: the wall stop ended the combo, the chain expects step 1 again");
                _clock = recoilEnd + 0.02f;
                _authority.StepSwings();
                Check(!Begin(s, 2, out _, out string thirdWhy) && thirdWhy.Contains("chain"), $"wall: step 3 is refused right after the recoil, inside what used to be the combo window ({thirdWhy})");
                Check(Begin(s, 0, out int restart, out string restartWhy), $"wall: the next attack starts the chain again at step 1 ({restartWhy})");
                Finish(restart);
                Cooldown(3f);
                s.Dispose();
            }
        }

        // ------------------------------------------------------------------------------------------------
        // D1c5: props (chests, gate pillars, loot, trigger colliders) are not swing blockers; level walls are
        // ------------------------------------------------------------------------------------------------

        private void PropsDoNotBlock()
        {
            ZonesOff();
            int levelBits = LayerMask.GetMask(GameConfig.LevelLayerName);
            int interactBits = LayerMask.GetMask(GameConfig.InteractLayerName);
            int lootBits = LayerMask.GetMask(GameConfig.LootLayerName);
            Check(_authority.SwingBlockerMask.value == levelBits, "D1c5 props: the swing blocker mask is the level layer alone");
            Check((_authority.SwingBlockerMask.value & (interactBits | lootBits)) == 0, "D1c5 props: no interact or loot layer is in the swing blocker mask");

            // (name, layer, trigger, expected to stop the swing)
            (string label, string layer, bool trigger, bool blocks)[] cases =
            {
                ("no obstacle (control)", null, false, false),
                ("chest on the interact layer", GameConfig.InteractLayerName, false, false),
                ("Ember Gate pillar on the interact layer", GameConfig.InteractLayerName, false, false),
                ("pickup on the loot layer", GameConfig.LootLayerName, false, false),
                ("trigger collider on the level layer", GameConfig.LevelLayerName, true, false),
                ("level wall", GameConfig.LevelLayerName, false, true)
            };

            foreach ((string label, string layer, bool trigger, bool blocks) c in cases)
            {
                Scenario s = NewScenario(_arcSword);
                Dummy target = AddBody(s, new Vector3(0f, 1.5f, 1.8f), 0.33f);
                if (c.layer != null) AddProp(s, new Vector3(0f, 1.8f, 1.0f), new Vector3(8f, 4f, 0.3f), c.layer, c.trigger);
                MeleeSwingStatus st = SwingStep(s, 0, out bool accepted, out string why);
                Check(accepted, $"D1c5 props: {c.label}: the swing begins ({why})");
                Check(st.WallStopped == c.blocks, $"D1c5 props: {c.label}: {(c.blocks ? "the swing stops" : "the swing is not stopped")} ({st.EndReason})");
                Check(WasHit(st, target) == !c.blocks, $"D1c5 props: {c.label}: the target behind it {(c.blocks ? "is not touched" : "is hit")}");
                Cooldown(3f);
                s.Dispose();
            }

            // a config whose level mask was widened to include the prop layers still cannot make them blockers
            LayerMask saved = _config.levelMask;
            _config.levelMask = levelBits | interactBits | lootBits;
            Check(_authority.SwingBlockerMask.value == levelBits, "D1c5 props: a widened level mask still derives the level layer alone");
            foreach (string layer in new[] { GameConfig.InteractLayerName, GameConfig.LootLayerName })
            {
                Scenario s = NewScenario(_arcSword);
                Dummy target = AddBody(s, new Vector3(0f, 1.5f, 1.8f), 0.33f);
                AddProp(s, new Vector3(0f, 1.8f, 1.0f), new Vector3(8f, 4f, 0.3f), layer, false);
                MeleeSwingStatus st = SwingStep(s, 0, out bool accepted, out _);
                Check(accepted && !st.WallStopped && WasHit(st, target), $"D1c5 props: widened mask, prop on {layer}: still no stop, target hit");
                Cooldown(3f);
                s.Dispose();
            }
            _config.levelMask = saved;

            // the real Level01 scene: no collider on a prop layer is in the mask, whatever built it
            int inspected = 0;
            bool leaked = false;
            foreach (Collider col in FindObjectsByType<Collider>(FindObjectsSortMode.None))
            {
                if (((1 << col.gameObject.layer) & (interactBits | lootBits)) == 0) continue;
                inspected++;
                if (((1 << col.gameObject.layer) & _authority.SwingBlockerMask.value) != 0) leaked = true;
            }
            Check(!leaked, $"D1c5 props: none of the {inspected} interact/loot-layer colliders in Level01 is a swing blocker");
            GameObject gate = GameObject.Find("Ember_Gate");
            Check(gate == null || gate.layer == LayerMask.NameToLayer(GameConfig.InteractLayerName), "D1c5 props: the Ember Gate is on the interact layer (so its pillars are not blockers)");
        }

        // ------------------------------------------------------------------------------------------------
        // D1c5: what cards / tooltips / the HUD show is the pattern's timing for a swept weapon
        // ------------------------------------------------------------------------------------------------

        private void DisplayTimingFromPattern()
        {
            foreach ((WeaponData weapon, string label) c in new[] { (_arcSword, "sword"), (_rapier, "rapier"), (_claw, "claw"), (_maul, "maul") })
            {
                SwingPatternData first = c.weapon.swingPatterns[0];
                Near(first.windupTime, c.weapon.DisplayWindup, 1e-6f, $"D1c5 display: {c.label} windup is the first pattern's");
                Near(first.activeTime, c.weapon.DisplayActive, 1e-6f, $"D1c5 display: {c.label} active is the first pattern's");
                Near(first.recoveryTime, c.weapon.DisplayRecovery, 1e-6f, $"D1c5 display: {c.label} recovery is the first pattern's");
            }

            foreach ((WeaponData weapon, string label) c in new[] { (_bow, "bow"), (_crossbow, "crossbow") })
            {
                Near(c.weapon.windupTime, c.weapon.DisplayWindup, 1e-6f, $"D1c5 display: {c.label} (no patterns) keeps the weapon's windup field");
                Near(c.weapon.activeWindow, c.weapon.DisplayActive, 1e-6f, $"D1c5 display: {c.label} keeps the weapon's active field");
                Near(c.weapon.recoveryTime, c.weapon.DisplayRecovery, 1e-6f, $"D1c5 display: {c.label} keeps the weapon's recovery field");
            }

            // a weapon whose serialized fields disagree with its pattern shows the pattern
            SwingPatternData pattern = UnityEngine.Object.Instantiate(_arcSword.swingPatterns[0]);
            _cleanup.Add(pattern);
            pattern.windupTime = 0.50f;
            pattern.activeTime = 0.21f;
            pattern.recoveryTime = 0.77f;
            WeaponData clone = UnityEngine.Object.Instantiate(_arcSword);
            _cleanup.Add(clone);
            clone.windupTime = 0.01f;
            clone.activeWindow = 0.02f;
            clone.recoveryTime = 0.03f;
            clone.swingPatterns = new[] { pattern };
            Near(0.50f, clone.DisplayWindup, 1e-6f, "D1c5 display: a pattern that disagrees with the weapon fields wins (windup 0.50, not 0.01)");
            Near(0.21f, clone.DisplayActive, 1e-6f, "D1c5 display: active 0.21, not 0.02");
            Near(0.77f, clone.DisplayRecovery, 1e-6f, "D1c5 display: recovery 0.77, not 0.03");
        }

        // ------------------------------------------------------------------------------------------------
        // 5. rapier chain
        // ------------------------------------------------------------------------------------------------

        private void RapierChain()
        {
            ZonesOff();
            float[] steps = { 1.00f, 1.05f, 1.10f, 1.15f };
            Scenario s = NewScenario(_rapier);
            AddBody(s, new Vector3(0f, 1.6f, 1.8f), 0.33f);
            for (int step = 0; step < steps.Length; step++)
            {
                MeleeSwingStatus st = SwingStep(s, step, out bool ok, out string why);
                Check(ok && st.Hits.Count == 1, $"rapier step {step + 1} accepted and hits ({why})");
                if (st.Hits.Count == 1) Near(_rapier.baseDamage * steps[step], st.Hits[0].Amount, 1e-3f, $"rapier thrust {step + 1} damage = base x {steps[step]:F2}");
                float expectedWindup = _rapier.swingPatterns[_rapier.comboSteps[step].patternIndex].windupTime * _rapier.comboSteps[step].windupScale;
                Near(expectedWindup, st.Timing.Windup, 1e-4f, $"rapier step {step + 1} windup follows the chain data (x{_rapier.comboSteps[step].windupScale:F2})");
                Advance(0.1f);
            }
            Cooldown(3f);
            s.Dispose();
        }

        // ------------------------------------------------------------------------------------------------
        // 6. impact zones
        // ------------------------------------------------------------------------------------------------

        private void ImpactZones()
        {
            ZonesOn();

            // a straight, stationary 1.0 m blade along the aim: the contact parameter is exactly the target's position
            // along it. Runtime pattern + runtime weapon clone, no assets are written.
            SwingPatternData straight = ScriptableObject.CreateInstance<SwingPatternData>();
            _cleanup.Add(straight);
            straight.patternId = "flow_gate_straight";
            straight.kind = SwingKind.Thrust;
            straight.windupTime = 0.1f;
            straight.activeTime = 0.1f;
            straight.recoveryTime = 0.1f;
            straight.sweepRadius = 0.1f;
            straight.reachScale = 1f;
            straight.reachInset = 0.2f;
            straight.maxTargets = 4;
            straight.contactWindow = 0f;
            straight.keys = new[]
            {
                new SwingKey(0f, new Vector3(0f, 0f, 0.30f), 0f, 0f),
                new SwingKey(1f, new Vector3(0f, 0f, 0.30f), 0f, 0f)
            };
            WeaponData blade = UnityEngine.Object.Instantiate(_arcSword);
            _cleanup.Add(blade);
            blade.weaponId = "flow_gate_blade";
            blade.comboSteps = new ComboStep[0];
            blade.swingPatterns = new[] { straight };
            blade.reach = 1.0f;
            blade.hitSphereRadius = 0.5f;           // effective reach 1.5, blade = 1.5 - 0.1 - 0.2 = 1.2 m
            float bladeLength = straight.BladeLength(blade);
            Near(1.2f, bladeLength, 1e-4f, "the test blade is 1.2 m");
            Near(0.7f, blade.ImpactMultiplier(0.1f), 1e-6f, "the test blade inherits the sword's bands");

            // target spheres beside the blade at known positions along it (0.15 m lateral keeps them touching: r 0.1 + sweep 0.1)
            (float t, float zone)[] cases = { (0.05f, 0.7f), (0.30f, 0.7f), (0.45f, 0.9f), (0.65f, 0.9f), (0.80f, 1.0f), (0.97f, 1.0f) };
            foreach ((float t, float zone) c in cases)
            {
                Scenario s = NewScenario(blade);
                float z = 0.30f + c.t * bladeLength;
                Dummy d = AddBody(s, new Vector3(0.15f, 1.8f, z), 0.1f);
                Check(Begin(s, 0, out int id, out string why), $"zone t={c.t:F2}: swing accepted ({why})");
                MeleeSwingStatus st = Finish(id);
                Check(st.Hits.Count == 1, $"zone t={c.t:F2}: the target is hit");
                if (st.Hits.Count == 1)
                {
                    MeleeSwingHit h = st.Hits[0];
                    Near(c.t, h.ContactT, 0.01f, $"the authority measured the contact at {c.t:F2} along the blade");
                    Near(c.zone, h.ZoneMultiplier, 1e-6f, $"zone multiplier at t={c.t:F2} is {c.zone}");
                    Near(blade.baseDamage * c.zone, h.Amount, 1e-3f, $"damage at t={c.t:F2} = base x {c.zone} (tip full damage, lower on the blade less)");
                }
                Cooldown(3f);
                s.Dispose();
            }

            // the sword's own slash: a target near the hilt end is hit weaker than one at the tip, by the reported contact
            {
                Scenario near = NewScenario(_arcSword);
                Dummy dn = AddBody(near, new Vector3(0f, 1.5f, 1.0f), 0.3f);
                MeleeSwingStatus sn = SwingStep(near, 0, out _, out _);
                Cooldown(3f);
                near.Dispose();

                Scenario far = NewScenario(_arcSword);
                Dummy df = AddBody(far, new Vector3(0f, 1.5f, 2.0f), 0.3f);
                MeleeSwingStatus sf = SwingStep(far, 0, out _, out _);
                Cooldown(3f);
                far.Dispose();

                Check(sn.Hits.Count == 1 && sf.Hits.Count == 1, "sword slash: both the near and the far target are hit");
                if (sn.Hits.Count == 1 && sf.Hits.Count == 1)
                {
                    Debug.Log($"{Tag} sword slash contact near T={sn.Hits[0].ContactT:F3} zone={sn.Hits[0].ZoneMultiplier:F2} amount={sn.Hits[0].Amount:F3}; far T={sf.Hits[0].ContactT:F3} zone={sf.Hits[0].ZoneMultiplier:F2} amount={sf.Hits[0].Amount:F3}");
                    Check(sn.Hits[0].ContactT < sf.Hits[0].ContactT, "sword slash: the near target is struck lower on the blade than the far one");
                    Near(_arcSword.ImpactMultiplier(sn.Hits[0].ContactT), sn.Hits[0].ZoneMultiplier, 1e-6f, "sword slash near: zone = the sword's band at the reported contact");
                    Near(_arcSword.ImpactMultiplier(sf.Hits[0].ContactT), sf.Hits[0].ZoneMultiplier, 1e-6f, "sword slash far: zone = the sword's band at the reported contact");
                    Near(_arcSword.baseDamage * sn.Hits[0].ZoneMultiplier, sn.Hits[0].Amount, 1e-3f, "sword slash near: damage = base x zone");
                    Near(_arcSword.baseDamage * sf.Hits[0].ZoneMultiplier, sf.Hits[0].Amount, 1e-3f, "sword slash far: damage = base x zone");
                    Check(sn.Hits[0].ZoneMultiplier < sf.Hits[0].ZoneMultiplier, "sword slash: a hit lower on the blade does less damage than a hit near the tip");
                }
            }

            // zones multiply with the chain step and the hit location (head 1.5 at the hilt end with step 3)
            {
                Scenario s = NewScenario(blade);
                float z = 0.30f + 0.10f * bladeLength;
                Dummy d = AddBody(s, new Vector3(0.15f, 1.8f, z), 0.1f, HitLocation.Head);
                Check(Begin(s, 0, out int id, out _), "zone x location: swing begins");
                MeleeSwingStatus st = Finish(id);
                Check(st.Hits.Count == 1, "zone x location: hit");
                if (st.Hits.Count == 1) Near(blade.baseDamage * 0.7f * 1.5f, st.Hits[0].Amount, 1e-3f, "zone 0.7 x head 1.5 multiply");
                Cooldown(3f);
                s.Dispose();
            }

            // zone x chain step 3 on armour 0: tip contact keeps the full 1.10 (the same number the chain test pinned with zones off)
            {
                WeaponData tipSword = UnityEngine.Object.Instantiate(_arcSword);
                _cleanup.Add(tipSword);
                tipSword.swingPatterns = new[] { straight, straight, straight };
                tipSword.reach = 1.0f;
                tipSword.hitSphereRadius = 0.5f;
                tipSword.comboSteps = new[] { new ComboStep(0, 1.00f), new ComboStep(1, 1.05f), new ComboStep(2, 1.10f) };
                // The authored weapons keep cooldown = the first step's length, so the repeat floor never outlasts the combo window.
                // This fixture swaps in a 0.3 s test pattern, so it follows the same rule for that pattern.
                tipSword.cooldown = straight.TotalTime;
                Scenario s = NewScenario(tipSword);
                Dummy d = AddBody(s, new Vector3(0.15f, 1.8f, 0.30f + 0.9f * bladeLength), 0.1f);
                MeleeSwingStatus last = default;
                for (int step = 0; step < 3; step++)
                {
                    last = SwingStep(s, step, out bool stepOk, out string stepWhy);
                    // SwingStep already waits for the authority's repeat floor (derived from the data), which is inside the combo window
                    // (the window runs 0.7 s past the active end); an extra fixed pause would push the next step past it.
                    Check(stepOk, $"tip chain: step {step + 1} accepted ({stepWhy})");
                }
                Check(last.Hits.Count == 1, "tip chain: step 3 hits");
                if (last.Hits.Count == 1)
                {
                    Near(1.0f, last.Hits[0].ZoneMultiplier, 1e-6f, "tip chain: the tip contact is full damage");
                    Near(tipSword.baseDamage * 1.10f, last.Hits[0].Amount, 1e-3f, "tip chain: step 3 at the tip = base x 1.10 exactly, with the zones ON");
                }
                Cooldown(3f);
                s.Dispose();
            }

            ZonesOff();
        }

        // ------------------------------------------------------------------------------------------------
        // 7. hit slow rules
        // ------------------------------------------------------------------------------------------------

        private Dummy AddSlowable(Scenario s, Vector3 center, float radius = 0.33f)
        {
            Dummy d = AddBody(s, center, radius);
            GameObject host = new GameObject("FlowGate_StatusHost");
            s.Props.Add(host);
            d.Status = host.AddComponent<StatusComponent>();
            return d;
        }

        private void HitSlowRules()
        {
            ZonesOff();

            // one hit: slow x0.9 for 0.6 s, applied once
            {
                Scenario s = NewScenario(_arcSword);
                Dummy d = AddSlowable(s, new Vector3(0f, 1.5f, 1.8f));
                int appliedBefore = _authority.HitSlowsApplied;
                SwingStep(s, 0, out _, out _);
                Check(d.Status.HasStatus(StatusKind.Slow), "hit slow: the target is slowed by the hit");
                Near(0.9f, d.Status.SpeedMultiplier, 1e-5f, "hit slow: -10 percent move speed");
                Near(0.6f, d.Status.RemainingOf(StatusKind.Slow), 1e-4f, "hit slow: 0.6 s");
                Check(_authority.HitSlowsApplied == appliedBefore + 1, "hit slow: applied exactly once for one hit");
                Check(d.Status.ActiveCount == 1, "hit slow: a single status entry");

                // a second hit refreshes: still 0.6 s and still x0.9 (no stacking to 0.81, no 1.2 s)
                Cooldown(0.1f);
                SwingStep(s, 1, out _, out _);
                Near(0.9f, d.Status.SpeedMultiplier, 1e-5f, "hit slow: a second hit does not stack (still x0.9, not x0.81)");
                Near(0.6f, d.Status.RemainingOf(StatusKind.Slow), 1e-4f, "hit slow: a second hit refreshes the duration, it does not add to it");
                Check(d.Status.ActiveCount == 1 && _authority.HitSlowsApplied == appliedBefore + 2, "hit slow: still one status entry, two applications counted");

                // the strongest slow wins: a skill-strength 0.55 stays, a weaker rapier slow does not replace it
                d.Status.ApplyStatus(new StatusSpec(StatusKind.Slow, 3f, 0.55f), 1);
                Cooldown(3f);
                SwingStep(s, 0, out _, out _);
                Near(0.55f, d.Status.SpeedMultiplier, 1e-5f, "hit slow: the strongest slow wins (a sword hit does not weaken a x0.55 slow)");
                Cooldown(3f);
                s.Dispose();

                Scenario rs = NewScenario(_rapier);
                Dummy rd = AddSlowable(rs, new Vector3(0f, 1.6f, 1.8f));
                SwingStep(rs, 0, out _, out _);
                Near(0.95f, rd.Status.SpeedMultiplier, 1e-5f, "rapier hit slow is x0.95 (lighter weapon, lighter slow)");
                Near(0.5f, rd.Status.RemainingOf(StatusKind.Slow), 1e-4f, "rapier hit slow lasts 0.5 s");
                Cooldown(3f);
                rs.Dispose();
            }

            // cleave: every struck target is slowed once; the data comes from the weapon, not from the intent
            {
                Scenario s = NewScenario(_arcSword);
                Dummy a = AddSlowable(s, new Vector3(-0.6f, 1.5f, 1.7f), 0.3f);
                Dummy b = AddSlowable(s, new Vector3(0.6f, 1.5f, 1.7f), 0.3f);
                int before = _authority.HitSlowsApplied;
                MeleeSwingStatus st = SwingStep(s, 0, out _, out _);
                Check(st.Hits.Count == 2, $"cleave: both targets are hit ({st.Hits.Count})");
                Check(a.Status.HasStatus(StatusKind.Slow) && b.Status.HasStatus(StatusKind.Slow), "cleave: both targets are slowed");
                Check(_authority.HitSlowsApplied == before + 2, "cleave: one application per hit target");
                string[] intentFields = Array.ConvertAll(typeof(MeleeSwingIntent).GetFields(BindingFlags.Instance | BindingFlags.Public), x => x.Name);
                Check(Array.IndexOf(intentFields, "Slow") < 0 && Array.TrueForAll(intentFields, x => !x.ToLowerInvariant().Contains("status") && !x.ToLowerInvariant().Contains("slow")),
                    "the swing intent has no status or slow field (statuses come from weapon data only)");
                Cooldown(3f);
                s.Dispose();
            }

            // a hit fully blocked by a plain guard does not slow
            {
                Scenario s = NewScenario(_arcSword);
                Dummy d = AddSlowable(s, new Vector3(0f, 1.5f, 1.8f));
                d.GuardReduction = 0.5f;
                MeleeSwingStatus st = SwingStep(s, 0, out _, out _);
                Check(st.Hits.Count == 1 && st.Hits[0].Blocked && !st.Hits[0].GuardBroken, "blocked hit: reported as blocked, not broken");
                Check(!d.Status.HasStatus(StatusKind.Slow), "blocked hit: a blocked (not broken) hit applies no slow");
                Cooldown(3f);
                s.Dispose();
            }

            // the maul's heavier slow (data) on a target
            {
                WeaponData maul = UnityEngine.Object.Instantiate(_arcSword);
                _cleanup.Add(maul);
                maul.hitSlowMultiplier = _maul.hitSlowMultiplier;
                maul.hitSlowDuration = _maul.hitSlowDuration;
                Scenario s = NewScenario(maul);
                Dummy d = AddSlowable(s, new Vector3(0f, 1.5f, 1.8f));
                SwingStep(s, 0, out _, out _);
                Near(0.75f, d.Status.SpeedMultiplier, 1e-5f, "a heavy weapon slows more (x0.75, data)");
                Near(1.5f, d.Status.RemainingOf(StatusKind.Slow), 1e-4f, "a heavy weapon slows longer (1.5 s, data)");
                Cooldown(3f);
                s.Dispose();
            }

            // a weapon with no hit slow slows nobody
            {
                WeaponData plain = UnityEngine.Object.Instantiate(_arcSword);
                _cleanup.Add(plain);
                plain.hitSlowDuration = 0f;
                plain.hitSlowMultiplier = 1f;
                Scenario s = NewScenario(plain);
                Dummy d = AddSlowable(s, new Vector3(0f, 1.5f, 1.8f));
                SwingStep(s, 0, out _, out _);
                Check(!d.Status.HasStatus(StatusKind.Slow), "a weapon with no hit slow data slows nobody");
                Cooldown(3f);
                s.Dispose();
            }
        }

        // ------------------------------------------------------------------------------------------------
        // 8. real rig: movement phases, guard, hit slow on players, no-stamina scan, HUD
        // ------------------------------------------------------------------------------------------------

        private PlayerRig _rig;
        private readonly List<DamageInfo> _playerHits = new List<DamageInfo>();
        private readonly List<string> _toasts = new List<string>();

        private void OnPlayerDamaged(int playerId, DamageInfo info) { _playerHits.Add(info); }
        private void OnToast(int playerId, string message) { _toasts.Add(message); }

        private IEnumerator RigSections()
        {
            _rig = FindFirstObjectByType<PlayerRig>();
            Check(_rig != null && !_rig.IsDead, "a living player rig is available");
            if (_rig == null) yield break;

            // park every enemy and take the rig to an empty spot; keep the rig's own movement ENABLED (walking is measured)
            foreach (EnemyAgent a in FindObjectsByType<EnemyAgent>(FindObjectsSortMode.None)) a.enabled = false;

            GameObject floor = new GameObject("FlowGate_Floor");
            floor.transform.position = Origin + new Vector3(0f, -0.5f, 0f);
            BoxCollider floorBox = floor.AddComponent<BoxCollider>();
            floorBox.size = new Vector3(80f, 1f, 80f);
            int level = LayerMask.NameToLayer(GameConfig.LevelLayerName);
            if (level >= 0) floor.layer = level;
            _cleanup.Add(floor);
            GameObject walkFloor = new GameObject("FlowGate_WalkFloor");
            walkFloor.transform.position = WalkOrigin + new Vector3(0f, -0.5f, 0f);
            walkFloor.AddComponent<BoxCollider>().size = new Vector3(80f, 1f, 80f);
            if (level >= 0) walkFloor.layer = level;
            _cleanup.Add(walkFloor);
            Physics.SyncTransforms();

            Teleport(Origin);
            yield return null;
            yield return null;

            GameEvents.PlayerDamaged += OnPlayerDamaged;
            GameEvents.ToastRaised += OnToast;

            {
                MovePhases();
                GuardBreak();
                BladeGuardCatchesByTurning();
                GuardExclusion();
                BladeDefenseOnRig();
                EnemyClawAgainstTheRig();
                NoStaminaMembers();
                HudSection();
            }

            yield return SwapLock();
            yield return ReloadSwapLock();
            yield return ReviewFollowups();
            yield return HeldMeleeInput();

            // real time: hit slow on the player, measured distance
            yield return HitSlowDistance();
            yield return SprintGuardMeasured();

            GameEvents.PlayerDamaged -= OnPlayerDamaged;
            GameEvents.ToastRaised -= OnToast;
            foreach (EnemyAgent a in FindObjectsByType<EnemyAgent>(FindObjectsSortMode.None)) a.enabled = true;
        }

        private void Teleport(Vector3 position)
        {
            CharacterController controller = _rig.GetComponent<CharacterController>();
            if (controller != null) controller.enabled = false;
            _rig.transform.SetPositionAndRotation(position + new Vector3(0f, 0.15f, 0f), Quaternion.identity);
            if (controller != null) controller.enabled = true;
            Physics.SyncTransforms();
        }

        // ---- move penalties --------------------------------------------------------------------------

        private void MovePhases()
        {
            Check(_rig.ActiveWeapon == _sword, "move phases: the rig holds the sword");
            _rig.SetGuardHeld(false);
            _rig.Health.Heal(1000f);

            float walk = _rig.Movement.ResolveWalkSpeed();
            float held = 1f - (_arcSword.heldMoveSpeedPenalty + _shield.heldMoveSpeedPenalty) / 300f;
            float idleWalk = _rig.Movement.ResolveGroundSpeed(false, false);
            float idleSprint = _rig.Movement.ResolveGroundSpeed(true, false);
            Near(walk * held, idleWalk, 1e-4f, $"held weapon: walk = {walk:F3} x (1 - (10 + 8)/300) = x{held:F3}");
            Check(idleSprint > idleWalk + 0.1f, "held weapon: sprint is still faster than walk");

            // the rig swings step 1 then step 2, the authority clock decides the phase
            ZonesOff();
            _authority.SetClock(() => _clock);
            _rig.TryGetAimPose(out _, out float yaw, out float pitch);
            float t0 = _clock;
            MeleeSwingIntent intent = new MeleeSwingIntent(_rig.CombatId, _arcSword.weaponId, 0, 0, yaw, pitch, CombatRegistry.NextSequence(_rig.CombatId));
            Check(_authority.TryBeginMeleeSwingAsOwner(intent, out int swingId, out string why), $"move phases: the rig's swing is accepted ({why})");

            SwingPatternData slash = _arcSword.swingPatterns[0];
            // the rig swings chain step 1: the pattern's timing as the authority resolves it (the combo step scales applied)
            SwingTiming stepOne = _arcSword.ComboTiming(0, slash);
            float w = stepOne.Windup, a = stepOne.Active, r = stepOne.Recovery;

            float prepare = slash.windupMoveMultiplier, hit = slash.activeMoveMultiplier, recover = slash.recoveryMoveMultiplier;
            _clock = t0 + w * 0.5f;
            Near(idleWalk * prepare, _rig.Movement.ResolveGroundSpeed(false, false), 1e-4f, $"windup: walk x{prepare:F3}");
            Near(idleSprint * prepare, _rig.Movement.ResolveGroundSpeed(true, false), 1e-4f, $"windup: sprint x{prepare:F3} too");
            _clock = t0 + w + a * 0.5f;
            Near(idleWalk * hit, _rig.Movement.ResolveGroundSpeed(false, false), 1e-4f, $"hit phase: walk x{hit:F3}");
            Near(idleSprint * hit, _rig.Movement.ResolveGroundSpeed(true, false), 1e-4f, $"hit phase: sprint x{hit:F3}");
            _clock = t0 + w + a + r * 0.5f;
            Near(idleWalk * recover, _rig.Movement.ResolveGroundSpeed(false, false), 1e-4f, $"recovery: walk x{recover:F3}");
            _clock = t0 + w + a + r + 0.02f;
            _authority.StepSwings();
            Near(idleWalk, _rig.Movement.ResolveGroundSpeed(false, false), 1e-4f, "after the swing: walk back to the held-weapon speed");

            // chain step 2 has its own phase boundaries (its step scales): the slowdown follows THAT step's windup and hit window
            float t1 = _clock;
            MeleeSwingIntent second = new MeleeSwingIntent(_rig.CombatId, _arcSword.weaponId, 1, 1, yaw, pitch, CombatRegistry.NextSequence(_rig.CombatId));
            Check(_authority.TryBeginMeleeSwingAsOwner(second, out int secondId, out why), $"move phases: step 2 accepted ({why})");
            SwingPatternData backSlash = _arcSword.swingPatterns[1];
            SwingTiming stepTwo = _arcSword.ComboTiming(1, backSlash);
            _clock = t1 + stepTwo.Windup * 0.5f;
            Near(idleWalk * backSlash.windupMoveMultiplier, _rig.Movement.ResolveGroundSpeed(false, false), 1e-4f, "step 2: its own windup is still the prepare slowdown");
            _clock = t1 + stepTwo.Windup + stepTwo.Active * 0.5f;
            Near(idleWalk * backSlash.activeMoveMultiplier, _rig.Movement.ResolveGroundSpeed(false, false), 1e-4f, "step 2: the hit-phase slowdown sits inside its own hit window");
            _clock = t1 + 2f;
            _authority.StepSwings();
            Near(idleWalk, _rig.Movement.ResolveGroundSpeed(false, false), 1e-4f, "step 2 over: speed restored");

            // blocking: the class multiplier x0.65 replaces the old x0.45, with the held penalty
            Near(0.65f, _rig.Class.guardSpeedMultiplier, 1e-6f, "blocking multiplier is 0.65");
            _rig.SetGuardHeld(true);
            Check(_rig.IsGuarding, "move phases: the guard is up");
            Near(walk * held * 0.65f, _rig.Movement.ResolveGroundSpeed(false, false), 1e-4f, "blocking: walk x0.65 (and the held penalty)");
            _rig.SetGuardHeld(false);

            // the crossbow (B1: ranged weapons use the held rule too): walk x (1 - 12/300), a two-handed weapon leaves the shield out
            Check(_rig.Loadout.SwitchToSlot(1) && _rig.ActiveWeapon == _crossbow, "move phases: swapped to the crossbow");
            float crossbowHeld = 1f - _crossbow.heldMoveSpeedPenalty / 300f;
            Near(_rig.Movement.ResolveWalkSpeed() * crossbowHeld, _rig.Movement.ResolveGroundSpeed(false, false), 1e-4f, "crossbow: walk = rule x (1 - 12/300) = x0.96 and the shield adds none");
            Near(_rig.Movement.ResolveGroundSpeed(true, false) / _rig.Movement.ResolveGroundSpeed(false, false), 5.2f / 4.3f, 1e-3f, "crossbow: sprint/walk ratio unchanged by the held factor");
            Near(_rig.GetMoveFlowFactor(), crossbowHeld, 1e-5f, "crossbow: the rig's move flow factor is the held factor (no swing)");
            Check(_rig.Loadout.SwitchToSlot(0) && _rig.ActiveWeapon == _sword, "move phases: back to the sword");
            _authority.StepSwings();
            Cooldown(3f);
        }

        // ---- guard break -----------------------------------------------------------------------------

        private DamageInfo? SwingAtRig(WeaponData weapon, bool front, int pattern = 0, bool legacyPattern = false, float distance = 1.5f)
        {
            Scenario s = NewScenario(weapon);
            s.Who.Kind = EntityKind.Enemy;
            s.Who.TeamId = CombatTeams.MonsterTeam;
            Vector3 p = _rig.transform.position;
            s.Who.Eye = p + new Vector3(0f, 1.65f, front ? distance : -distance);
            s.Who.Yaw = front ? 180f : 0f;
            _rig.Health.Heal(1000f);
            _playerHits.Clear();
            _authority.AllowLegacyPatternSelection = legacyPattern;
            bool ok = _authority.TryBeginMeleeSwingAsOwner(Intent(s.Who, 0, pattern), out int id, out string why);
            _authority.AllowLegacyPatternSelection = false;
            if (!ok)
            {
                Debug.LogError($"{Tag} attacker swing refused: {why}");
                s.Dispose();
                return null;
            }

            Finish(id);
            s.Dispose();
            return _playerHits.Count > 0 ? _playerHits[0] : (DamageInfo?)null;
        }

        private void GuardBreak()
        {
            ZonesOff();
            // Face the raised shield into the incoming slash, as in the live shield review.
            // Holding guard alone no longer intercepts a trajectory beside the visible shield.
            _rig.Look.ApplyAimPose(-35f, 0f); // the chain opens with the right-to-left slash (Dark and Darker order): meet it on the other side
            _rig.SetGuardHeld(false);
            Check(_rig.Defense.CanBlock && !_rig.CanParry, "guard: the warrior blocks and cannot parry");
            Near(20f, _rig.Defense.ImpactResist, 1e-4f, "guard: the kit's impact resist is the heater shield's 20");
            float reduction = _rig.Defense.BlockReduction;

            WeaponData heavy = UnityEngine.Object.Instantiate(_arcSword);
            _cleanup.Add(heavy);
            heavy.impactPower = 30f;
            WeaponData edge = UnityEngine.Object.Instantiate(_arcSword);
            _cleanup.Add(edge);
            edge.impactPower = 20f;
            WeaponData justUnder = UnityEngine.Object.Instantiate(_arcSword);
            _cleanup.Add(justUnder);
            justUnder.impactPower = 19.9f;
            WeaponData buckler = UnityEngine.Object.Instantiate(_shield);
            _cleanup.Add(buckler);
            buckler.impactResist = _arcSword.impactResist + 1f;   // a buckler guards better than the bare blade kit, below the heater shield

            // control: no guard raised
            DamageInfo? control = SwingAtRig(_arcSword, true);
            Check(control.HasValue && !control.Value.Blocked && !control.Value.GuardBroken, "guard control: with the guard down the hit is neither blocked nor broken");
            float unguarded = control.HasValue ? control.Value.Amount : 0f;
            float shieldContactDamage = control.HasValue
                ? unguarded / _table.Multiplier(control.Value.Location) : 0f;
            Cooldown(3f);

            // 1. sword (10) against the heater shield (20): blocked normally
            _rig.SetGuardHeld(true);
            DamageInfo? light = SwingAtRig(_arcSword, true);
            Check(light.HasValue && light.Value.Blocked && !light.Value.GuardBroken, "sword vs heater shield: blocked, not broken");
            if (light.HasValue)
            {
                Near(BladeGuardRules.HeldGuardReduction, light.Value.BlockReduction, 1e-4f, "sword vs heater: a holding guard takes the whole hit");
                Near(unguarded * (1f - BladeGuardRules.HeldGuardReduction), light.Value.Amount, 0.01f, "sword vs heater: damage = unguarded x (1 - held reduction) = 0 HP");
                Near(10f, light.Value.ImpactPower, 1e-4f, "the hit carries the sword's impact power");
            }
            Check(!_authority.IsGuardStaggered(_rig.CombatId) && _rig.IsGuarding, "sword vs heater: no stagger, the guard stays up");
            Cooldown(3f);

            // 2. impact power equal to or above the old resist no longer breaks anything: the guard holds
            DamageInfo? atEdge = SwingAtRig(edge, true);
            Check(atEdge.HasValue && atEdge.Value.Blocked && !atEdge.Value.GuardBroken, "impact power equal to the resist: the guard holds");
            Cooldown(3f);
            _rig.SetGuardHeld(true);
            DamageInfo? under = SwingAtRig(justUnder, true);
            Check(under.HasValue && under.Value.Blocked && !under.Value.GuardBroken, "impact power 19.9 against resist 20 is a normal block");
            Cooldown(3f);

            // 3. heavy (30) against the heater: held whole, no stagger, no break
            _rig.SetGuardHeld(true);
            _toasts.Clear();
            int breaksBefore = _authority.GuardBreaksApplied;
            DamageInfo? heavyHit = SwingAtRig(heavy, true);
            Check(heavyHit.HasValue && heavyHit.Value.Blocked && !heavyHit.Value.GuardBroken, "heavy vs heater: the block holds, force never breaks it");
            if (heavyHit.HasValue)
            {
                Near(BladeGuardRules.HeldGuardReduction, heavyHit.Value.BlockReduction, 1e-4f, "heavy vs heater: the whole hit is taken off");
                Near(unguarded * (1f - BladeGuardRules.HeldGuardReduction), heavyHit.Value.Amount, 0.01f, "heavy vs heater: 0 HP");
                Near(30f, heavyHit.Value.ImpactPower, 1e-4f, "the hit carries the heavy weapon's impact power");
            }
            Check(_authority.GuardBreaksApplied == breaksBefore, "the authority counted no guard break");
            Check(!_toasts.Exists(t => t.Contains("GUARD BROKEN")), "HUD: no GUARD BROKEN toast");
            Check(!_authority.IsGuardStaggered(_rig.CombatId) && _rig.IsGuarding, "the guard is not staggered and stays up after a heavy hit");
            Check(_authority.GuardBrokenEntryCount == 0, "no guard-break stamp exists");
            _rig.SetGuardHeld(false);
            Cooldown(3f);

            // 4. the overhead's impact scale does not break what the slash only blocks either
            WeaponData borderline = UnityEngine.Object.Instantiate(_arcSword);
            _cleanup.Add(borderline);
            borderline.impactPower = 17f;
            _rig.SetGuardHeld(true);
            DamageInfo? slash17 = SwingAtRig(borderline, true);
            Check(slash17.HasValue && slash17.Value.Blocked && !slash17.Value.GuardBroken, "impact 17 slash: normal block");
            Cooldown(3f);
            _rig.SetGuardHeld(true);
            // Raise the shield towards the descending blade rather than keep the slash-facing pose.
            _rig.Look.ApplyAimPose(18f, -35f);
            DamageInfo? over17 = SwingAtRig(borderline, true, pattern: 2, legacyPattern: true);
            Check(over17.HasValue && over17.Value.GuardShieldContact && over17.Value.Blocked && !over17.Value.GuardBroken,
                $"impact 17 overhead (x1.2 = 20.4): still held (contact={over17?.GuardShieldContact}, location={over17?.Location}, impact={over17?.ImpactPower})");
            _rig.Look.ApplyAimPose(-35f, 0f);
            Cooldown(3f);
            _clock += 2f;

            // 5. buckler: sword (10) and heavy (30) are both held
            _rig.SetOffhandWeapon(buckler);
            Near(buckler.impactResist, _rig.Defense.ImpactResist, 1e-4f, "buckler: the kit's impact resist is the buckler's " + buckler.impactResist);
            _rig.SetGuardHeld(true);
            DamageInfo? swordVsBuckler = SwingAtRig(_arcSword, true);
            Check(swordVsBuckler.HasValue && swordVsBuckler.Value.Blocked && !swordVsBuckler.Value.GuardBroken, "sword (10) vs buckler: blocked");
            Cooldown(3f);
            _rig.SetGuardHeld(true);
            DamageInfo? heavyVsBuckler = SwingAtRig(heavy, true);
            Check(heavyVsBuckler.HasValue && heavyVsBuckler.Value.Blocked && !heavyVsBuckler.Value.GuardBroken, "heavy (30) vs buckler: held, not broken");
            Cooldown(3f);
            _clock += 2f;
            _rig.SetOffhandWeapon(_rig.Class.offhandShield); // U5A: an empty off hand stays empty, so the class shield is put back explicitly
            Near(20f, _rig.Defense.ImpactResist, 1e-4f, "the heater shield is back in hand");

            // 6. a hit from behind is never blocked, whatever its impact power, and breaks nothing
            _rig.SetGuardHeld(true);
            int breaksBeforeBack = _authority.GuardBreaksApplied;
            DamageInfo? behind = SwingAtRig(heavy, false);
            Check(behind.HasValue && !behind.Value.Blocked && !behind.Value.GuardBroken, "a heavy hit from behind is not blocked and does not break the guard");
            Check(_authority.GuardBreaksApplied == breaksBeforeBack && !_authority.IsGuardStaggered(_rig.CombatId), "a hit from behind causes no stagger");
            Cooldown(3f);

            // 7. skills carry no impact power and never break a guard; the guard answers per hit
            // Blocks are physical: the skill's line comes in where the defender has turned its raised shield.
            Vector3 facing = _rig.AimDirection; facing.y = 0f; facing = facing.sqrMagnitude > 1e-4f ? facing.normalized : _rig.transform.forward;
            DamageInfo skillLike = new DamageInfo(20f, _rig.CombatCenter, -facing, _rig.CombatCenter + facing * 2f, 999, EntityKind.Enemy, DamageType.Holy, false, false, 0f, HitLocation.Unspecified, 0f);
            Check(_rig.TryResolveGuard(skillLike, CombatFlowTuning.Default, out GuardResult gr) && !gr.Broken && Mathf.Abs(gr.Reduction - BladeGuardRules.HeldGuardReduction) < 1e-4f,
                "a hit with no impact power (a skill's flat damage) is blocked normally and never breaks the guard");
            DamageInfo heavyLike = skillLike.WithImpactPower(99f);
            Check(_rig.TryResolveGuard(heavyLike, CombatFlowTuning.Default, out gr) && !gr.Broken && Mathf.Abs(gr.Reduction - BladeGuardRules.HeldGuardReduction) < 1e-4f, "an impact power of 99 does not break it");
            _rig.SetGuardHeld(false);
            Check(!_rig.TryResolveGuard(heavyLike, CombatFlowTuning.Default, out _), "with the guard down there is nothing to break");

            // 8. shields never parry
            WeaponData parryShield = UnityEngine.Object.Instantiate(_shield);
            _cleanup.Add(parryShield);
            parryShield.capabilities = DefenseCapability.Block | DefenseCapability.Parry;
            Check(!parryShield.CanParry && (parryShield.EffectiveCapabilities & DefenseCapability.Parry) == 0, "a shield ticked with Parry still cannot parry (sanitised)");
            _rig.SetOffhandWeapon(parryShield);
            Check(!_rig.CanParry && !_rig.Defense.CanParry, "a rig carrying that shield cannot parry");
            _rig.SetOffhandWeapon(_rig.Class.offhandShield); // U5A: the off hand stays empty on null, so the class shield is put back explicitly
            Check(!_rig.CanParry, "the default kit cannot parry");
            string[] damageFields = Array.ConvertAll(typeof(DamageInfo).GetFields(BindingFlags.Instance | BindingFlags.Public), x => x.Name.ToLowerInvariant());
            Check(Array.TrueForAll(damageFields, x => !x.Contains("parry")), "DamageInfo has no parry state: guard break did not introduce one");

            _rig.Look.ApplyAimPose(0f, 0f);
            _clock += 2f;
        }

        // ---- blocks are physical: the defender catches the strike on the blade by turning to meet it ------------------------
        // The chain opens with the right-to-left slash, which reaches a bare-sword defender's left side first. Facing the attacker
        // squarely is not what blocks it: turning the body/view into the incoming edge puts the blade in its path.
        private void BladeGuardCatchesByTurning()
        {
            ZonesOff();
            int bladeOpenerCaught = 0;
            foreach (bool shield in new[] { false, true })
            {
                _rig.SetOffhandWeapon(shield ? _rig.Class.offhandShield : null);
                if (!shield) Check(!_rig.Defense.HasShield && _rig.Defense.CanBlock, "blade guard: the bare sword kit blocks with its blade");
                for (int pattern = 0; pattern < 2; pattern++)
                foreach (float distance in new[] { 1.1f, 1.5f })
                {
                    var caught = new List<string>();
                    foreach (float pitch in new[] { 0f, 20f })
                    {
                        for (float yaw = -80f; yaw <= 80f; yaw += 5f)
                        {
                            _rig.Look.ApplyAimPose(yaw, pitch);
                            _rig.SetGuardHeld(true);
                            DamageInfo? hit = SwingAtRig(_arcSword, true, pattern, legacyPattern: pattern != 0, distance: distance);
                            bool contact = shield ? hit.HasValue && hit.Value.GuardShieldContact : hit.HasValue && hit.Value.GuardBladeContact;
                            if (hit.HasValue && hit.Value.Blocked && contact)
                            {
                                caught.Add(yaw.ToString("0") + "/" + pitch.ToString("0"));
                                if (!shield && pattern == 0) bladeOpenerCaught++;
                            }
                            _rig.SetGuardHeld(false);
                            Cooldown(3f);
                        }
                    }
                    Debug.Log(Tag + " " + (shield ? "shield" : "blade") + " guard catches " + _arcSword.swingPatterns[pattern].kind + " at " + distance.ToString("0.0") + " m, yaw/pitch: " + string.Join(", ", caught));
                }
            }
            Check(bladeOpenerCaught > 0, "blade guard: turning into the right-to-left opener catches it on the blade");

            // The shipped sword's own cuts (reference directions): the steep opener comes down from the defender's upper side, so the
            // guard has to be raised into it. Measured here so the answer is data, and logged for the network scripts' poses.
            foreach (bool shield in new[] { false, true })
            {
                _rig.SetOffhandWeapon(shield ? _rig.Class.offhandShield : null);
                for (int step = 0; step < 2; step++)
                {
                    int pattern = _sword.comboSteps[step].patternIndex;
                    foreach (float distance in new[] { 1.1f, 1.5f })
                    {
                        var caught = new List<string>();
                        foreach (float pitch in new[] { -70f, -55f, -40f, -20f, 0f, 20f })
                        {
                            for (float yaw = -80f; yaw <= 80f; yaw += 5f)
                            {
                                _rig.Look.ApplyAimPose(yaw, pitch);
                                _rig.SetGuardHeld(true);
                                DamageInfo? hit = SwingAtRig(_sword, true, pattern, legacyPattern: step != 0, distance: distance);
                                bool contact = shield ? hit.HasValue && hit.Value.GuardShieldContact : hit.HasValue && hit.Value.GuardBladeContact;
                                if (hit.HasValue && hit.Value.Blocked && contact) caught.Add(yaw.ToString("0") + "/" + pitch.ToString("0"));
                                _rig.SetGuardHeld(false);
                                Cooldown(3f);
                            }
                        }
                        Debug.Log(Tag + " REAL " + (shield ? "shield" : "blade") + " guard catches " + _sword.swingPatterns[pattern].patternId + " at " + distance.ToString("0.0") + " m, yaw/pitch: " + string.Join(", ", caught));
                        if (step == 0) Check(caught.Count > 0, "guard vs the reference opener: some raised pose catches it on the " + (shield ? "shield" : "blade") + " at " + distance.ToString("0.0") + " m");
                    }
                }
            }
            _rig.SetOffhandWeapon(_rig.Class.offhandShield);
            _rig.Look.ApplyAimPose(0f, 0f);
            _clock += 2f;
        }

        // ---- D1c5: block and attack are exclusive (authority half of M-c) --------------------------------

        private void GuardExclusion()
        {
            ZonesOff();
            _rig.SetGuardHeld(false);
            _rig.Health.Heal(1000f);
            _authority.StepSwings();
            Cooldown(3f);

            // fake instigator that reports a raised guard
            Scenario s = NewScenario(_arcSword, new Vector3(30f, 1.8f, 0f));      // 30 m from the rig
            s.Who.Guarding = true;
            MeleeSwingIntent forged = Intent(s.Who, 0, 0);
            int rejectedBefore = _authority.RejectedCount;
            int startedBefore = _authority.SwingsStarted;
            int activeBefore = _authority.ActiveSwingCount;
            int chainsBefore = _authority.ChainEntryCount;
            bool ok = _authority.TryBeginMeleeSwingAsOwner(forged, out int id, out string why);
            Check(!ok && id == 0, "D1c5 guard: a swing begin while the instigator is guarding is refused");
            Check(why.Contains("guarding") && _authority.LastAttackRejectReason == why, $"D1c5 guard: the refusal says why ({why})");
            Check(_authority.RejectedCount == rejectedBefore + 1 && _authority.SwingsStarted == startedBefore
                  && _authority.ActiveSwingCount == activeBefore && _authority.ChainEntryCount == chainsBefore,
                "D1c5 guard: refusal changes nothing (one more rejection, no swing, no chain)");
            Check(!_authority.IsSwingInProgress(s.Who.CombatId), "D1c5 guard: no swing is in progress after the refusal");
            s.Who.Guarding = false;
            ok = _authority.TryBeginMeleeSwingAsOwner(forged, out id, out why);
            Check(ok && id != 0, $"D1c5 guard: the very same intent (same sequence, same clock) is accepted once the guard is down: nothing was stamped or spent ({why})");
            s.Who.Guarding = true;      // raising the guard mid swing does not cancel or alter the running swing
            MeleeSwingStatus status = Finish(id);
            Check(status.Finished && status.EndReason == SwingEndReason.Completed, "D1c5 guard: a guard raised mid swing does not cancel the swing");
            Cooldown(3f);
            ok = Begin(s, 0, out _, out why);
            Check(!ok && why.Contains("guarding"), "D1c5 guard: while the guard stays up the next swing is refused too");

            // the real rig
            _rig.TryGetAimPose(out _, out float yaw, out float pitch);
            _rig.SetGuardHeld(true);
            Check(_rig.IsGuarding, "D1c5 guard: the rig guard is up");
            activeBefore = _authority.ActiveSwingCount;
            MeleeSwingIntent rigIntent = new MeleeSwingIntent(_rig.CombatId, _arcSword.weaponId, 0, 0, yaw, pitch, CombatRegistry.NextSequence(_rig.CombatId));
            ok = _authority.TryBeginMeleeSwingAsOwner(rigIntent, out id, out why);
            Check(!ok && why.Contains("guarding"), $"D1c5 guard: the real rig cannot begin a swing while guarding ({why})");
            Check(_authority.ActiveSwingCount == activeBefore && !_authority.IsSwingInProgress(_rig.CombatId), "D1c5 guard: the refused rig swing left no swing behind");

            _rig.SetGuardHeld(false);
            ok = _authority.TryBeginMeleeSwingAsOwner(rigIntent, out id, out why);
            Check(ok, $"D1c5 guard: with the guard down the same rig intent is accepted ({why})");

            // forged raise of the guard flag while the rig's own swing runs: it does not reduce incoming damage
            _rig.SetGuardHeld(true);
            Check(_rig.IsGuarding && _authority.IsSwingInProgress(_rig.CombatId), "D1c5 guard: guard flag up while the rig's own swing runs");
            DamageInfo front = new DamageInfo(20f, _rig.CombatCenter, Vector3.back, _rig.transform.position + Vector3.forward * 2f, 999, EntityKind.Enemy,
                DamageType.Physical, false, false, 0f, HitLocation.Body, 10f);
            Check(!_rig.TryResolveGuard(front, CombatFlowTuning.Default, out _), "D1c5 guard: a frontal hit finds no guard during the rig's own swing (TryResolveGuard)");
            Check(!_rig.TryGetGuardReduction(front, out _), "D1c5 guard: ... and no plain block reduction either (TryGetGuardReduction)");

            // end to end: an enemy swing lands on the rig while the rig's own swing is still running
            Scenario attacker = NewScenario(_arcSword);
            attacker.Who.Kind = EntityKind.Enemy;
            attacker.Who.TeamId = CombatTeams.MonsterTeam;
            attacker.Who.Eye = _rig.transform.position + new Vector3(0f, 1.65f, 1.5f);
            attacker.Who.Yaw = 180f;
            _playerHits.Clear();
            Check(Begin(attacker, 0, out int attackId, out why), $"D1c5 guard: the enemy swing begins ({why})");
            Finish(attackId);
            Check(_playerHits.Count == 1 && !_playerHits[0].Blocked && !_playerHits[0].GuardBroken,
                "D1c5 guard: the hit that lands during the rig's own swing is neither blocked nor broken");
            attacker.Dispose();

            _clock += 3f;
            _authority.StepSwings();
            Check(!_authority.IsSwingInProgress(_rig.CombatId), "D1c5 guard: the rig swing is over");
            Check(_rig.TryResolveGuard(front, CombatFlowTuning.Default, out _), "D1c5 guard: the guard works again once the swing is over");
            _rig.SetGuardHeld(false);
            _rig.Health.Heal(1000f);
            Cooldown(3f);
        }

        // ---- U3C: directional height / endurance guard on the real rig --------------------------------------------

        private void BladeDefenseOnRig()
        {
            _authority.SetClock(() => _clock);
            _clock += 3f;
            _authority.StepSwings();
            _rig.Health.Heal(1000f);

            // Reference port: a guard intercepts only after its ready time (0.15 s), measured on the authority clock from the press.
            _rig.SetGuardHeld(false);
            _authority.GuardReadyDelayEnabled = true;
            _rig.SetGuardHeld(true);
            Check(_rig.IsGuarding, "U3C rig: the guard is up");
            Near(0.15f, _authority.GuardReadySeconds, 1e-6f, "guard ready: the authored ready time is the reference 0.15 s");
            Check(!_rig.IsGuardReady, "guard ready: a guard pressed this instant does not intercept yet");
            bool early = _rig.TryResolveGuard(U3cHit(_rig.transform.position + _rig.transform.forward * 2f, -_rig.transform.forward, 1f, BladeHeight.Mid),
                new CombatFlowTuning(), out _);
            Check(!early, "guard ready: a frontal hit in the first instant of the raise is not blocked");
            _clock += _authority.GuardReadySeconds - 0.01f;
            Check(!_rig.IsGuardReady, "guard ready: 10 ms before the ready time it still does not intercept");
            _clock += 0.02f;
            Check(_rig.IsGuardReady, "guard ready: after the ready time the held guard intercepts");
            _rig.SetGuardHeld(false);
            _rig.SetGuardHeld(true);
            Check(!_rig.IsGuardReady, "guard ready: releasing and pressing again restarts the ready time");
            _authority.GuardReadyDelayEnabled = false;   // the rest of the gate raises and strikes in the same instant

            DefenseProfile kit = _rig.Defense;
            float impact = kit.ImpactResist * 0.5f;            // below the resist: never a guard break in this section
            Vector3 ahead = _rig.transform.position + _rig.transform.forward * 2f;
            Vector3 behind = _rig.transform.position - _rig.transform.forward * 2f;
            CombatFlowTuning free = new CombatFlowTuning();
            BladeHeight band = _rig.CurrentGuardHeight(free);
            Check(band != BladeHeight.Unspecified, "U3C rig: the guard has a height band from the view pitch (" + band + ")");

            // front, same band: held with the kit's own reduction
            bool front = _rig.TryResolveGuard(U3cHit(ahead, -_rig.transform.forward, impact, band), free, out GuardResult held);
            Check(front && !held.Broken, "U3C rig: a frontal hit in the guard's own band is blocked");
            Near(BladeGuardRules.HeldGuardReduction, held.Reduction, 1e-4f, "U3C rig: ... taking the whole hit");
            Check(_rig.LastGuardOutcome == GuardOutcome.Blocked, "U3C rig: outcome Blocked (" + _rig.LastGuardOutcome + ")");

            // back: the raised defence is in front, a line from behind never meets it (blocks are physical, in every band)
            foreach (BladeHeight attack in new[] { BladeHeight.Low, BladeHeight.Mid, BladeHeight.High })
                Check(!_rig.TryResolveGuard(U3cHit(behind, _rig.transform.forward, impact, attack), free, out _), "U3C rig: a hit from behind is not blocked (" + attack + ")");

            // physical, not facing: a frontal hit whose line passes beside the raised defence is not blocked, and a hit that meets it
            // is held whatever height label it carries (the contact decides, not the band)
            Vector3 beside = _rig.CombatCenter + _rig.transform.right * 1.2f + _rig.transform.forward * 0.2f;
            Check(!_rig.TryResolveGuard(U3cHit(beside, -_rig.transform.right, impact, band), free, out _), "U3C rig: a hit from the flank, beside the defence, is not blocked though the guard is up");
            CombatFlowTuning strict = new CombatFlowTuning();
            strict.bladeHeightTolerance = 0;
            strict.shieldHeightTolerance = 0;
            BladeHeight other = band == BladeHeight.High ? BladeHeight.Low : BladeHeight.High;
            Check(_rig.TryResolveGuard(U3cHit(ahead, -_rig.transform.forward, impact, other), strict, out _), "U3C rig: a frontal hit that meets the defence is held in any band (" + band + " guard vs " + other + ")");
            Check(_rig.TryResolveGuard(U3cHit(ahead, -_rig.transform.forward, impact, BladeHeight.Unspecified), strict, out _), "U3C rig: a hit with no height (skill) that meets the defence is held");

            // free blocking (the shipped tuning): the pool is never touched, however many hits land
            for (int i = 0; i < 6; i++)
            {
                Check(_rig.TryResolveGuard(U3cHit(ahead, -_rig.transform.forward, impact, band), free, out _), "U3C rig: free block #" + i);
            }

            Check(!free.GuardEnduranceEnabled, "U3C rig: the default tuning has no endurance pool (blocking is free)");

            // endurance: an empty pool cannot block; it refills with the authority clock
            CombatFlowTuning pooled = new CombatFlowTuning();
            pooled.guardEnduranceRegenPerSecond = 25f;
            pooled.guardEnduranceRegenDelay = 1f;
            float cost = BladeGuardRules.BlockCost(impact, pooled.guardBlockCostBase, pooled.guardBlockCostPerImpact);
            Check(cost > 0f, "U3C rig: a held hit has a positive cost (" + cost + ")");
            pooled.guardEnduranceMax = cost * 2.5f;
            int affordable = Mathf.FloorToInt(pooled.guardEnduranceMax / cost);
            int heldCount = 0;
            for (int i = 0; i < affordable + 2; i++)
            {
                if (_rig.TryResolveGuard(U3cHit(ahead, -_rig.transform.forward, impact, band), pooled, out _)) heldCount++;
            }

            Check(heldCount == affordable, "U3C rig: exactly " + affordable + " hits are held from a full pool (" + heldCount + ")");
            Check(_rig.LastGuardOutcome == GuardOutcome.Exhausted, "U3C rig: the next hit finds an empty pool: Exhausted (" + _rig.LastGuardOutcome + ")");
            Near(pooled.guardEnduranceMax - affordable * cost, _rig.GuardEnduranceCurrent, 1e-3f, "U3C rig: each held hit paid its cost exactly once");

            float missing = cost - _rig.GuardEnduranceCurrent;
            _clock += pooled.guardEnduranceRegenDelay + missing / pooled.guardEnduranceRegenPerSecond + 0.05f;
            Check(_rig.TryResolveGuard(U3cHit(ahead, -_rig.transform.forward, impact, band), pooled, out _), "U3C rig: after the regeneration delay and time the guard holds again");

            // shields never parry
            Check(!kit.HasShield || !kit.CanParry, "U3C rig: a shield in the kit never parries");
            Check(!kit.HasShield || !_rig.CanParry, "U3C rig: ... nor does the rig report a parry");

            _rig.SetGuardHeld(false);
            _rig.Health.Heal(1000f);
            Cooldown(3f);
        }

        private DamageInfo U3cHit(Vector3 sourcePosition, Vector3 direction, float impact, BladeHeight attackHeight)
        {
            return new DamageInfo(20f, _rig.CombatCenter, direction, sourcePosition, 999, EntityKind.Enemy,
                DamageType.Physical, false, false, 0f, HitLocation.Body, impact, false, false, false, attackHeight);
        }

        // ---- weapon swap is blocked during an attack -----------------------------------------------------

        private IEnumerator HeldMeleeInput()
        {
            ZonesOff();
            _clock += 10f;
            _authority.StepSwings();
            var melee = _rig.GetComponent<MeleeCombat>();
            _rig.Loadout.ForceSwitchToSlot(0);
            melee.Initialize(_rig.ActiveWeapon);
            _rig.SetInputEnabled(true);
            _rig.Input.PrimaryPressedOverride = false;
            _rig.Input.SecondaryHeldOverride = false;
            int swings = 0;
            Action<MeleeSwingIntent> count = _ => swings++;
            melee.AttackStarted += count;
            try
            {
                _rig.Input.PrimaryHeldOverride = true;
                yield return AdvanceHeldInput(4f);
                Check(swings >= 2, "held primary continues real authority-accepted melee without press edges");
                _rig.Input.PrimaryHeldOverride = false;
                int released = swings;
                yield return AdvanceHeldInput(2f);
                Check(swings == released, "released primary starts no further swings");

                _rig.Input.PrimaryHeldOverride = true;
                _rig.Input.SecondaryHeldOverride = true;
                yield return AdvanceHeldInput(2f);
                Check(swings == released, "held guard excludes held melee attacks");
                _rig.Input.SecondaryHeldOverride = false;
                _rig.Input.SetGameplayInputEnabled(false);
                yield return AdvanceHeldInput(2f);
                Check(swings == released, "disabled gameplay input prevents a held attack");

                _rig.Input.PrimaryHeldOverride = false;
                _rig.Input.SetGameplayInputEnabled(true);
                melee.RemoteDriven = true;
                melee.SetRemoteAttack(true);
                yield return AdvanceHeldInput(0.1f);
                Check(swings > released, "remote held intent starts an authority-accepted swing");
                yield return AdvanceHeldInput(1f);
                Check(!melee.RemoteAttackWanted, "unrefreshed remote intent expires");
                int expired = swings;
                yield return AdvanceHeldInput(2f);
                Check(swings == expired, "expired remote intent cannot keep attacking");
                melee.SetRemoteAttack(true);
                melee.SetRemoteAttack(false);
                yield return AdvanceHeldInput(1f);
                Check(swings == expired, "remote release cancels pending held intent before another swing");
            }
            finally
            {
                melee.AttackStarted -= count;
                melee.SetRemoteAttack(false);
                melee.RemoteDriven = false;
                _rig.Input.PrimaryHeldOverride = null;
                _rig.Input.PrimaryPressedOverride = null;
                _rig.Input.SecondaryHeldOverride = null;
                _rig.SetInputEnabled(true);
            }
        }

        private IEnumerator AdvanceHeldInput(float seconds)
        {
            for (float elapsed = 0f; elapsed < seconds; elapsed += Time.deltaTime)
            {
                _clock += Time.deltaTime;
                _authority.StepSwings();
                yield return null;
            }
        }

        private IEnumerator SwapLock()
        {
            ZonesOff();
            _authority.SetClock(() => _clock);
            _toasts.Clear();
            ClassLoadout loadout = _rig.Loadout;
            RangedCombat ranged = _rig.GetComponent<RangedCombat>();
            _rig.SetGuardHeld(false);
            Check(loadout.SwitchToSlot(0) || _rig.ActiveWeapon == _sword, "swap lock: the sword is in hand");
            _clock += 3f;
            _authority.StepSwings();

            _rig.TryGetAimPose(out _, out float yaw, out float pitch);
            float t0 = _clock;
            MeleeSwingIntent intent = new MeleeSwingIntent(_rig.CombatId, _arcSword.weaponId, 0, 0, yaw, pitch, CombatRegistry.NextSequence(_rig.CombatId));
            Check(_authority.TryBeginMeleeSwingAsOwner(intent, out int swingId, out string why), $"swap lock: a swing begins ({why})");
            SwingTiming p = _arcSword.ComboTiming(0, _arcSword.swingPatterns[0]);   // chain step 1 as the authority resolves it
            _authority.TryGetExpectedComboStep(_rig.CombatId, out int comboBefore);
            int acceptedBefore = _authority.AcceptedCount;

            foreach ((string phase, float at) c in new[] { ("windup", p.Windup * 0.5f), ("active", p.Windup + p.Active * 0.5f), ("recovery", p.Windup + p.Active + p.Recovery * 0.5f) })
            {
                _clock = t0 + c.at;
                _toasts.Clear();
                Check(_authority.IsSwingInProgress(_rig.CombatId), $"swap lock ({c.phase}): the authority reports the swing in progress");
                Check(loadout.IsActionLocked, $"swap lock ({c.phase}): the loadout is action-locked");
                Check(!loadout.SwitchToSlot(1), $"swap lock ({c.phase}): the swap is refused");
                Check(!loadout.SwitchWeapon(), $"swap lock ({c.phase}): SwitchWeapon is refused too");
                Check(_rig.ActiveWeapon == _sword && !loadout.UsingSecondary, $"swap lock ({c.phase}): the weapon in hand did not change");
                Check(_toasts.Exists(t => t.Contains("Cannot swap")), $"swap lock ({c.phase}): the player is told why");
                Check(_authority.TryGetExpectedComboStep(_rig.CombatId, out int comboNow) && comboNow == comboBefore, $"swap lock ({c.phase}): combo state unchanged");
                Check(_authority.IsSwingInProgress(_rig.CombatId) && _authority.AcceptedCount == acceptedBefore, $"swap lock ({c.phase}): the swing was not disturbed");
            }

            // the swing ends, nothing was queued, then the swap works again
            _clock = t0 + p.Total + 0.05f;
            _authority.StepSwings();
            Check(!_authority.IsSwingInProgress(_rig.CombatId), "swap lock: the swing is over");
            yield return null;
            yield return null;
            Check(_rig.ActiveWeapon == _sword, "swap lock: the refused swap input was not queued (the sword is still in hand after the swing)");
            Check(loadout.SwitchToSlot(1) && _rig.ActiveWeapon == _crossbow, "swap lock: the swap works right after the swing ends");

            // ranged: a draw in progress blocks the swap
            if (ranged != null && ranged.enabled)
            {
                int magazine = ranged.Magazine;
                _rig.Input.PrimaryPressedOverride = true;
                yield return null;
                _rig.Input.PrimaryPressedOverride = null;
                yield return null;
                Check(ranged.IsDrawing, "swap lock: the crossbow is drawing");
                Check(!loadout.SwitchToSlot(0) && _rig.ActiveWeapon == _crossbow && ranged.Magazine == magazine, "swap lock (draw): refused, crossbow still in hand, magazine unchanged");
                _rig.Input.PrimaryReleasedOverride = true;
                yield return null;
                _rig.Input.PrimaryReleasedOverride = null;
                yield return null;
                Check(!ranged.IsDrawing, "swap lock: the draw ended");
            }
            else
            {
                Check(false, "swap lock: the ranged component is live with the crossbow");
            }

            Check(loadout.SwitchToSlot(0) && _rig.ActiveWeapon == _sword, "swap lock: back to the sword once the draw is over");

            // forced change (respawn / class apply path) still ends a swing as WeaponChanged
            _clock += 3f;
            _authority.StepSwings();
            _rig.TryGetAimPose(out _, out yaw, out pitch);
            MeleeSwingIntent again = new MeleeSwingIntent(_rig.CombatId, _arcSword.weaponId, 0, 0, yaw, pitch, CombatRegistry.NextSequence(_rig.CombatId));
            Check(_authority.TryBeginMeleeSwingAsOwner(again, out int forcedId, out why), $"swap lock: another swing begins ({why})");
            _clock += 0.1f;
            Check(loadout.ForceSwitchToSlot(1) && _rig.ActiveWeapon == _crossbow, "swap lock: a forced change goes through");
            _clock += 0.05f;
            _authority.StepSwings();
            Check(_authority.TryGetMeleeSwingStatus(forcedId, out MeleeSwingStatus st) && st.Finished && st.EndReason == SwingEndReason.WeaponChanged, $"swap lock: the forced change ended the swing as WeaponChanged ({st.EndReason})");
            Check(loadout.SwitchToSlot(0) && _rig.ActiveWeapon == _sword, "swap lock: swap works after the forced path");
            _clock += 3f;
            _authority.StepSwings();

            // D1c1: A-B-A on the real loadout inside one frame (sword -> crossbow -> sword, no yield) drops the chain
            {
                int countBefore = _rig.WeaponChangeCount;
                _rig.TryGetAimPose(out _, out yaw, out pitch);
                MeleeSwingIntent first = new MeleeSwingIntent(_rig.CombatId, _arcSword.weaponId, 0, 0, yaw, pitch, CombatRegistry.NextSequence(_rig.CombatId));
                Check(_authority.TryBeginMeleeSwingAsOwner(first, out int abaId, out why), $"A-B-A rig: a swing begins ({why})");
                _authority.TryGetMeleeSwingStatus(abaId, out MeleeSwingStatus abaSt);
                _clock += abaSt.Timing.Total + 0.01f;
                Check(!_authority.IsSwingInProgress(_rig.CombatId) && !loadout.IsActionLocked, "A-B-A rig: the swing is over on the clock, so the swap is not locked (no step ran)");
                Check(_authority.TryGetExpectedComboStep(_rig.CombatId, out int expectedBefore) && expectedBefore == 1, "A-B-A rig: step 2 is expected inside the window");
                Check(loadout.SwitchToSlot(1) && loadout.SwitchToSlot(0) && _rig.ActiveWeapon == _sword, "A-B-A rig: swapped away and back in one frame");
                Check(_rig.WeaponChangeCount == countBefore + 2, $"A-B-A rig: the weapon-change counter went up by two ({_rig.WeaponChangeCount - countBefore})");
                Check(_authority.TryGetExpectedComboStep(_rig.CombatId, out int expectedAfter) && expectedAfter == 0, "A-B-A rig: the chain is gone although the sword is in hand again");
                _rig.TryGetAimPose(out _, out yaw, out pitch);
                MeleeSwingIntent forged = new MeleeSwingIntent(_rig.CombatId, _arcSword.weaponId, 1, 1, yaw, pitch, CombatRegistry.NextSequence(_rig.CombatId));
                Check(!_authority.TryBeginMeleeSwingAsOwner(forged, out _, out why) && why.Contains("weapon changed"), $"A-B-A rig: step 2 is refused ({why})");

                // and mid swing (forced path, the player swap itself is locked)
                _clock += 3f;
                _authority.StepSwings();
                _rig.TryGetAimPose(out _, out yaw, out pitch);
                MeleeSwingIntent midIntent = new MeleeSwingIntent(_rig.CombatId, _arcSword.weaponId, 0, 0, yaw, pitch, CombatRegistry.NextSequence(_rig.CombatId));
                Check(_authority.TryBeginMeleeSwingAsOwner(midIntent, out int midId, out why), $"A-B-A rig mid-swing: a swing begins ({why})");
                _clock += 0.1f;
                Check(loadout.ForceSwitchToSlot(1) && loadout.ForceSwitchToSlot(0) && _rig.ActiveWeapon == _sword, "A-B-A rig mid-swing: forced away and back");
                _clock += 0.05f;
                _authority.StepSwings();
                Check(_authority.TryGetMeleeSwingStatus(midId, out MeleeSwingStatus midSt) && midSt.Finished && midSt.EndReason == SwingEndReason.WeaponChanged,
                    $"A-B-A rig mid-swing: ended as WeaponChanged although the sword is back ({midSt.EndReason})");
                _clock += 3f;
                _authority.StepSwings();
            }
        }

        // ------------------------------------------------------------------------------------------------
        // B1: slow merge (each slow keeps its own magnitude and duration; the strongest active one applies)
        // ------------------------------------------------------------------------------------------------

        private StatusComponent NewStatusHost(string name)
        {
            GameObject host = new GameObject(name);
            _cleanup.Add(host);
            StatusComponent status = host.AddComponent<StatusComponent>();
            status.enabled = false;               // time is stepped by hand with Tick, real frames never touch it
            return status;
        }

        private void SlowMerge()
        {
            // a longer, weaker slow, then a short strong one (the quirk case): the strong one must end on ITS time
            {
                StatusComponent st = NewStatusHost("B1_Slow_A");
                st.ApplyStatus(new StatusSpec(StatusKind.Slow, 3f, 0.9f), 1);
                st.ApplyStatus(new StatusSpec(StatusKind.Slow, 0.6f, 0.55f), 2);
                Near(0.55f, st.SpeedMultiplier, 1e-5f, "merge A: the stronger slow applies while it lasts");
                Check(st.ActiveCount == 2, $"merge A: two entries, each with its own duration ({st.ActiveCount})");
                st.Tick(0.5f);
                Near(0.55f, st.SpeedMultiplier, 1e-5f, "merge A: still the strong slow 0.5 s in");
                st.Tick(0.2f);
                Near(0.9f, st.SpeedMultiplier, 1e-5f, "merge A: the strong slow ended at its own 0.6 s, the weak one applies for its own remaining time");
                Near(2.3f, st.RemainingOf(StatusKind.Slow), 1e-4f, "merge A: the weak slow has 3.0 - 0.7 = 2.3 s left");
                Check(st.ActiveCount == 1, "merge A: the expired entry is gone");
                st.Tick(2.4f);
                Near(1f, st.SpeedMultiplier, 1e-6f, "merge A: all slows are over after 3.0 s");
                Check(!st.HasStatus(StatusKind.Slow) && st.ActiveCount == 0, "merge A: nothing left");
            }

            // a stronger, longer slow then a short weak hit slow: the weak one is redundant, nothing is stretched or weakened
            {
                StatusComponent st = NewStatusHost("B1_Slow_B");
                st.ApplyStatus(new StatusSpec(StatusKind.Slow, 3f, 0.55f), 1);
                st.ApplyStatus(new StatusSpec(StatusKind.Slow, 0.6f, 0.9f), 2);
                Near(0.55f, st.SpeedMultiplier, 1e-5f, "merge B: the strong slow stays");
                Check(st.ActiveCount == 1, "merge B: the weaker, shorter slow adds no entry (refresh, not stack)");
                Near(3f, st.RemainingOf(StatusKind.Slow), 1e-5f, "merge B: the strong slow keeps its own 3.0 s");
                st.Tick(3.01f);
                Near(1f, st.SpeedMultiplier, 1e-6f, "merge B: the strong slow ends at 3.0 s, nothing is carried over");
            }

            // a short strong slow first, then a longer weak one: the strong one does not get the long one's time
            {
                StatusComponent st = NewStatusHost("B1_Slow_C");
                st.ApplyStatus(new StatusSpec(StatusKind.Slow, 0.6f, 0.55f), 1);
                st.ApplyStatus(new StatusSpec(StatusKind.Slow, 3f, 0.9f), 2);
                Near(0.55f, st.SpeedMultiplier, 1e-5f, "merge C: strong slow applies first");
                Check(st.ActiveCount == 2, "merge C: two entries");
                st.Tick(0.61f);
                Near(0.9f, st.SpeedMultiplier, 1e-5f, "merge C: the strong magnitude was NOT extended to the longer slow's time");
                Near(3f - 0.61f, st.RemainingOf(StatusKind.Slow), 1e-4f, "merge C: the weak slow runs its own full duration");
            }

            // refresh, not stack: the same slow again only refreshes the duration (max), the magnitude never multiplies
            {
                StatusComponent st = NewStatusHost("B1_Slow_D");
                st.ApplyStatus(new StatusSpec(StatusKind.Slow, 0.6f, 0.9f), 1);
                st.Tick(0.4f);
                st.ApplyStatus(new StatusSpec(StatusKind.Slow, 0.6f, 0.9f), 1);
                Check(st.ActiveCount == 1, "refresh: one entry after the same slow again");
                Near(0.9f, st.SpeedMultiplier, 1e-6f, "refresh: x0.9, not x0.81");
                Near(0.6f, st.RemainingOf(StatusKind.Slow), 1e-5f, "refresh: back to 0.6 s, not 0.2 + 0.6");
                st.ApplyStatus(new StatusSpec(StatusKind.Slow, 0.3f, 0.9f), 1);
                Near(0.6f, st.RemainingOf(StatusKind.Slow), 1e-5f, "refresh: a shorter repeat never shortens");
                st.ApplyStatus(new StatusSpec(StatusKind.Slow, 2f, 0.9f), 1);
                Check(st.ActiveCount == 1, "refresh: a longer repeat of the same magnitude replaces, one entry");
                Near(2f, st.RemainingOf(StatusKind.Slow), 1e-5f, "refresh: the longer duration wins (max)");
            }

            // a stronger slow applied late: it only lasts its own duration
            {
                StatusComponent st = NewStatusHost("B1_Slow_E");
                st.ApplyStatus(new StatusSpec(StatusKind.Slow, 3f, 0.9f), 1);
                st.Tick(1f);
                st.ApplyStatus(new StatusSpec(StatusKind.Slow, 0.5f, 0.6f), 2);
                st.Tick(0.4f);
                Near(0.6f, st.SpeedMultiplier, 1e-5f, "late strong slow: applies for 0.4 s of its 0.5 s");
                st.Tick(0.2f);
                Near(0.9f, st.SpeedMultiplier, 1e-5f, "late strong slow: ended on its own time, the weak slow has 1.4 s left");
                Near(1.4f, st.RemainingOf(StatusKind.Slow), 1e-4f, "late strong slow: remaining of the weak slow");
            }

            // the list stays small and clear removes everything
            {
                StatusComponent st = NewStatusHost("B1_Slow_F");
                for (int i = 0; i < 12; i++) st.ApplyStatus(new StatusSpec(StatusKind.Slow, 12f - i, 0.95f - 0.02f * i), i);   // each stronger and shorter: none dominates
                Check(st.ActiveCount <= 8, $"bound: at most 8 slow entries ({st.ActiveCount})");
                st.ClearStatuses();
                Check(st.ActiveCount == 0 && st.SpeedMultiplier == 1f, "clear: every slow entry is gone");
            }

            // other kinds keep the old rule (refresh: max duration, strongest magnitude, one entry)
            {
                StatusComponent st = NewStatusHost("B1_Buff");
                st.ApplyStatus(new StatusSpec(StatusKind.Empowered, 2f, 1.2f), 1);
                st.ApplyStatus(new StatusSpec(StatusKind.Empowered, 1f, 1.5f), 1);
                Check(st.ActiveCount == 1, "buff: Empowered still refreshes into one entry");
                Near(1.5f, st.OutgoingDamageMultiplier, 1e-6f, "buff: strongest magnitude");
                Near(2f, st.RemainingOf(StatusKind.Empowered), 1e-5f, "buff: longest duration");
            }

            // through the authority: a sword hit onto a longer, weaker slow
            ZonesOff();
            {
                Scenario s = NewScenario(_arcSword);
                Dummy d = AddSlowable(s, new Vector3(0f, 1.5f, 1.8f));
                d.Status.enabled = false;
                d.Status.ApplyStatus(new StatusSpec(StatusKind.Slow, 5f, 0.95f), 9);
                SwingStep(s, 0, out _, out _);
                Near(0.9f, d.Status.SpeedMultiplier, 1e-5f, "authority merge: the sword hit (x0.9) is stronger than the existing x0.95 and applies");
                Check(d.Status.ActiveCount == 2, "authority merge: both slows are tracked");
                d.Status.Tick(0.61f);
                Near(0.95f, d.Status.SpeedMultiplier, 1e-5f, "authority merge: after the hit slow's 0.6 s the older x0.95 slow still applies");
                Near(5f - 0.61f, d.Status.RemainingOf(StatusKind.Slow), 1e-4f, "authority merge: the older slow kept its own clock");
                Cooldown(3f);
                s.Dispose();
            }
        }

        // ------------------------------------------------------------------------------------------------
        // B1: reload swap lock, forced change cancels the reload, buffered press
        // ------------------------------------------------------------------------------------------------

        private IEnumerator ReloadSwapLock()
        {
            ZonesOff();
            _authority.SetClock(() => _clock);
            ClassLoadout loadout = _rig.Loadout;
            RangedCombat ranged = _rig.GetComponent<RangedCombat>();
            MeleeCombat melee = _rig.GetComponent<MeleeCombat>();
            _rig.SetGuardHeld(false);
            _clock += 3f;
            _authority.StepSwings();

            if (_rig.ActiveWeapon != _crossbow) Check(loadout.SwitchToSlot(1), "reload lock: switched to the crossbow");
            Check(_rig.ActiveWeapon == _crossbow && ranged != null && ranged.enabled, "reload lock: the crossbow is live");
            if (ranged == null || !ranged.enabled) yield break;

            // 1. reload in hand: swap refused (both entry points and the input path), nothing changes, nothing queued
            // SwapLock's preceding release can have fired the sole bolt depending on frame timing.
            // Establish a loaded starting state through a real reload before testing the lock itself.
            ranged.AddAmmo(3);
            if (ranged.Magazine == 0)
            {
                int setupReserve = ranged.Reserve;
                _rig.Input.ReloadPressedOverride = true;
                yield return null;
                _rig.Input.ReloadPressedOverride = null;
                yield return null;
                Check(ranged.IsReloading && ranged.Magazine == 0 && ranged.Reserve == setupReserve,
                    "reload lock setup: empty crossbow starts an ordinary reload without early refill");
                _clock += ranged.ReloadDuration + .01f;
                _authority.AdvanceKitsTo(_clock);
                yield return null;
                Check(!ranged.IsReloading && ranged.Magazine == 1 && ranged.Reserve == setupReserve - 1,
                    "reload lock setup: one real reload loads exactly one bolt");
            }
            Check(ranged.Magazine == 1 && EmberdelveKitFixtures.ConsumeAmmo(ranged, 1),
                "reload lock: spend the one loaded bolt explicitly");
            int magazine = ranged.Magazine, reserve = ranged.Reserve;
            Check(magazine < _crossbow.magazineSize && reserve > 0, $"reload lock: a reload is possible (mag {magazine}/{_crossbow.magazineSize}, reserve {reserve})");
            _rig.Input.ReloadPressedOverride = true;
            yield return null;
            _rig.Input.ReloadPressedOverride = null;
            yield return null;
            Check(ranged.IsReloading, "reload lock: the crossbow is reloading");
            Check(loadout.IsActionLocked && loadout.IsReloadLocked, "reload lock: the loadout is action-locked by the reload");
            _toasts.Clear();
            Check(!loadout.SwitchToSlot(0) && !loadout.SwitchWeapon(), "reload lock: SwitchToSlot and SwitchWeapon are refused");
            Check(_toasts.Exists(t => t.Contains("while reloading")), "reload lock: the player is told it is the reload");
            WeaponData other = UnityEngine.Object.Instantiate(_arcSword);
            _cleanup.Add(other);
            Check(loadout.TryReplaceWeaponSlot(1, other) == null && _rig.ActiveWeapon == _crossbow && loadout.Secondary == _crossbow, "reload lock: replacing the weapon in hand is refused too");
            _rig.Input.SwapPressedOverride = true;
            yield return null;
            _rig.Input.SwapPressedOverride = null;
            yield return null;
            Check(_rig.ActiveWeapon == _crossbow && ranged.IsReloading, "reload lock: the swap key press is refused, the reload is not disturbed");
            Check(ranged.Magazine == magazine && ranged.Reserve == reserve, "reload lock: no ammo changed by the refusal");

            // a primary press during the reload starts no draw and queues no shot
            _rig.Input.PrimaryPressedOverride = true;
            yield return null;
            _rig.Input.PrimaryPressedOverride = null;
            Check(!ranged.IsDrawing, "reload lock: a press during the reload starts no draw");

            // the reload finishes: exact ammo, the swap works right after, and the refused swap was not queued
            float waited = 0f;
            // The authority runs the reload on ITS clock, which this gate injects (_clock): let it run in step with real time here.
            while (ranged.IsReloading && waited < _crossbow.reloadTime + 2f) { waited += Time.deltaTime; _clock += Time.deltaTime; yield return null; }
            yield return null;
            int taken = Mathf.Min(_crossbow.magazineSize - magazine, reserve);
            Check(!ranged.IsReloading && ranged.Magazine == magazine + taken && ranged.Reserve == reserve - taken, $"reload lock: the reload finished with exactly {taken} round(s) moved");
            Check(_rig.ActiveWeapon == _crossbow && !ranged.IsDrawing, "reload lock: the refused swap was not queued and the early press did not shoot");
            Check(!loadout.IsActionLocked, "reload lock: no longer locked");
            Check(loadout.SwitchToSlot(0) && _rig.ActiveWeapon == _sword, "reload lock: the swap works right after the reload");

            // 2. forced change during a reload goes through and cancels it cleanly (no free ammo, nothing completes later)
            Check(loadout.SwitchToSlot(1) && _rig.ActiveWeapon == _crossbow, "forced: back to the crossbow");
            Check(ranged.Magazine == 1 && EmberdelveKitFixtures.ConsumeAmmo(ranged, 1),
                "forced: spend the single loaded bolt before cancellation proof");
            magazine = ranged.Magazine;
            reserve = ranged.Reserve;
            _rig.Input.ReloadPressedOverride = true;
            yield return null;
            _rig.Input.ReloadPressedOverride = null;
            yield return null;
            Check(ranged.IsReloading && reserve > 0, "forced: a reload is running");
            Check(loadout.ForceSwitchToSlot(0) && _rig.ActiveWeapon == _sword, "forced: ForceSwitchToSlot goes through during the reload");
            Check(!ranged.IsReloading && !ranged.enabled, "forced: the reload was cancelled and the ranged component is off");
            LoadoutAmmoCheck(loadout, magazine, reserve, "forced: right after the cancel");
            // (authority clock in step with real time, so a reload that wrongly survived the cancel WOULD complete here)
            for (float idle = 0f; idle < _crossbow.reloadTime + 0.4f; idle += Time.deltaTime) { _clock += Time.deltaTime; yield return null; }
            LoadoutAmmoCheck(loadout, magazine, reserve, "forced: the cancelled reload never completes in the background (no free ammo)");
            Check(loadout.SwitchToSlot(1) && _rig.ActiveWeapon == _crossbow, "forced: swapping back works");
            Check(!ranged.IsReloading && ranged.Magazine == magazine && ranged.Reserve == reserve && !loadout.IsActionLocked, "forced: back in hand the crossbow is not reloading and has exactly its old ammo");
            // a forced change that goes sword -> crossbow -> sword -> crossbow, with a reload cancelled each time, still gains nothing
            _rig.Input.ReloadPressedOverride = true;
            yield return null;
            _rig.Input.ReloadPressedOverride = null;
            yield return null;
            Check(ranged.IsReloading, "forced: reload again");
            Check(loadout.ForceSwitchToSlot(0) && loadout.ForceSwitchToSlot(1), "forced: away and back within one frame");
            Check(!ranged.IsReloading && ranged.Magazine == magazine && ranged.Reserve == reserve, "forced: away and back cancels the reload, ammo unchanged");
            Check(loadout.SwitchToSlot(0) && _rig.ActiveWeapon == _sword, "forced: swap to the sword works (reload no longer running)");

            // 3. buffered melee press: a refused swap neither drops nor redirects it
            _clock += 5f;
            _authority.StepSwings();
            if (melee != null && melee.enabled)
            {
                yield return new WaitForSeconds(1.0f);
                _clock += 5f;
                _authority.StepSwings();
                Check(melee.TryBeginSwing(), "buffer: a swing begins through the client");
                float s0 = Time.realtimeSinceStartup;
                int first = melee.LastSwingId;
                while (Time.realtimeSinceStartup - s0 < RecoveryPressAt) yield return null;
                _rig.Input.PrimaryPressedOverride = true;
                yield return null;
                _rig.Input.PrimaryPressedOverride = null;
                Check(melee.BufferedPressRemaining > 0.2f, $"buffer: a press in the recovery is buffered ({melee.BufferedPressRemaining:F2} s)");
                Check(loadout.IsActionLocked, "buffer: the swing is still running on the (frozen) authority clock");
                _rig.Input.SwapPressedOverride = true;
                yield return null;
                _rig.Input.SwapPressedOverride = null;
                Check(_rig.ActiveWeapon == _sword, "buffer: the swap key during the swing is refused");
                Check(melee.BufferedPressRemaining > 0f, "buffer: the refusal did not touch the buffered press");
                while (Time.realtimeSinceStartup - s0 < AfterFirstSwing) yield return null;   // the client's own swing is over, the press is still inside its 0.5 s
                _clock += 5f;
                yield return new WaitForSeconds(0.08f);
                Check(melee.LastSwingId != first && _rig.ActiveWeapon == _sword, "buffer: the buffered press became the next SWORD swing; the refused swap was not queued");
                _clock += 5f;
                _authority.StepSwings();

                // forced away and back (new weapon in hand) drops a buffered press: it never fires later by surprise
                yield return new WaitForSeconds(1.0f);
                _clock += 5f;
                _authority.StepSwings();
                Check(melee.TryBeginSwing(), "buffer 2: a swing begins");
                s0 = Time.realtimeSinceStartup;
                while (Time.realtimeSinceStartup - s0 < RecoveryPressAt) yield return null;
                _rig.Input.PrimaryPressedOverride = true;
                yield return null;
                _rig.Input.PrimaryPressedOverride = null;
                Check(melee.BufferedPressRemaining > 0.2f, "buffer 2: the press is buffered");
                Check(loadout.ForceSwitchToSlot(1) && loadout.ForceSwitchToSlot(0) && _rig.ActiveWeapon == _sword, "buffer 2: forced away and back");
                Check(melee.BufferedPressRemaining <= 0f, "buffer 2: the buffered press did not survive the weapon change");
                int idAfter = melee.LastSwingId;
                _clock += 5f;
                _authority.StepSwings();
                yield return new WaitForSeconds(0.3f);
                Check(melee.LastSwingId == idAfter && !melee.IsSwinging, "buffer 2: no surprise swing fires afterwards");
                _clock += 5f;
                _authority.StepSwings();
            }
            else
            {
                Check(false, "buffer: the melee client is live with the sword");
            }
        }

        private void LoadoutAmmoCheck(ClassLoadout loadout, int magazine, int reserve, string label)
        {
            ClassLoadout.RangedAmmoState state = loadout.GetAmmoState(_crossbow);
            Check(state != null && state.magazine == magazine && state.reserve == reserve, $"{label} (stored {(state != null ? state.magazine + "/" + state.reserve : "none")}, expected {magazine}/{reserve})");
        }

        // ---- review follow-ups: replace-in-hand lock, buffered press, ranged impact ------------------------

        private ItemCatalog _testCatalog;

        private IEnumerator ReviewFollowups()
        {
            ZonesOff();
            _authority.SetClock(() => _clock);
            ClassLoadout loadout = _rig.Loadout;
            _rig.SetGuardHeld(false);
            _clock += 3f;
            _authority.StepSwings();
            Check(_rig.ActiveWeapon == _sword, "follow-ups: the sword is in hand");

            // 1. replacing the weapon in hand (the inventory equip path) is locked during a swing
            ItemData blade = AssetDatabase.LoadAssetAtPath<ItemData>(EmberdelvePaths.Data + "/Item_EmberFangBlade.asset");
            Check(blade != null && blade.GrantsWeapon && blade.equipSlot != EquipmentSlot.OffHand, "follow-ups: a main-hand weapon item exists");
            if (blade == null) yield break;
            Check(EmberdelveTestCatalog.Adopt(ref _testCatalog, _rig.Inventory, blade, out string adoptReason), "follow-ups: the blade is registered (" + adoptReason + ")");
            Check(_rig.Inventory.TryAdd(blade, 1), "follow-ups: the blade is in the bag");
            WeaponData other = UnityEngine.Object.Instantiate(_arcSword);
            _cleanup.Add(other);
            int filledBefore = _rig.Equipment.FilledCount; // U5A: the kit items are worn already

            _rig.TryGetAimPose(out _, out float yaw, out float pitch);
            float t0 = _clock;
            MeleeSwingIntent intent = new MeleeSwingIntent(_rig.CombatId, _arcSword.weaponId, 0, 0, yaw, pitch, CombatRegistry.NextSequence(_rig.CombatId));
            Check(_authority.TryBeginMeleeSwingAsOwner(intent, out int swingId, out string why), $"follow-ups: a swing begins ({why})");
            foreach (float at in new[] { 0.1f, 0.4f, 0.7f })
            {
                _clock = t0 + at;
                Check(loadout.TryReplaceWeaponSlot(0, other) == null && _rig.ActiveWeapon == _sword, $"follow-ups ({at:F1} s into the swing): replacing the weapon in hand is refused, the sword stays");
                bool equipped = _rig.Equipment.TryEquipItem(blade, out string equipWhy);
                Check(!equipped && equipWhy.Contains("during an attack") && _rig.Equipment.FilledCount == filledBefore && _rig.ActiveWeapon == _sword,
                    $"follow-ups ({at:F1} s): equipping a weapon item is refused with nothing changed ({equipWhy})");
            }
            Check(_rig.Inventory.TotalUnits >= 1, "follow-ups: the blade is still in the bag");

            _clock = t0 + _arcSword.swingPatterns[0].TotalTime + 0.05f;
            _authority.StepSwings();
            bool equippedAfter = _rig.Equipment.TryEquipItem(blade, out string afterWhy);
            Check(equippedAfter && _rig.ActiveWeapon == blade.grantedWeapon, $"follow-ups: after the swing the weapon item equips ({afterWhy})");
            Check(_rig.Equipment.TryUnequip(blade.equipSlot, out string unWhy) && _rig.ActiveWeapon == _rig.Loadout.Unarmed, $"follow-ups: and comes off again, the hand is empty: the bare fists (U5A) ({unWhy})");
            _rig.Equipment.Clear(); // a clear puts the starting kit back
            Check(_rig.ActiveWeapon == _sword, "follow-ups: a clear puts the starting kit back, the class sword in hand");
            _rig.Inventory.Clear();
            _clock += 3f;
            _authority.StepSwings();

            // 2. a buffered press survives a refused begin. The authority clock is frozen while real time runs, so the client
            // believes it is ready but the authority still says "a swing is in progress"; the press is spent only when accepted.
            MeleeCombat melee = _rig.GetComponent<MeleeCombat>();
            Check(melee != null && melee.enabled, "follow-ups: the melee client is live");
            if (melee != null && melee.enabled)
            {
                yield return new WaitForSeconds(1.0f);
                Check(melee.TryBeginSwing(), "follow-ups: a swing begins through the client");
                float s0 = Time.realtimeSinceStartup;
                int firstSwing = melee.LastSwingId;
                while (Time.realtimeSinceStartup - s0 < RecoveryPressAt) yield return null;
                _rig.Input.PrimaryPressedOverride = true;          // during the client's recovery: buffered
                yield return null;
                _rig.Input.PrimaryPressedOverride = null;
                Check(melee.BufferedPressRemaining > 0.3f, $"follow-ups: the press during the recovery is buffered ({melee.BufferedPressRemaining:F2} s)");
                while (Time.realtimeSinceStartup - s0 < AfterFirstSwing) yield return null;
                Check(melee.LastSwingId == firstSwing, "follow-ups: the authority (frozen clock) has refused the buffered begin so far");
                Check(melee.BufferedPressRemaining > 0f, $"follow-ups: the refusal did not spend the buffered press ({melee.BufferedPressRemaining:F2} s left)");
                _clock += 5f;                                       // the authority clock catches up inside the buffer window
                yield return new WaitForSeconds(0.08f);
                Check(melee.LastSwingId != firstSwing && melee.BufferedPressRemaining <= 0f, "follow-ups: once the authority accepts, the buffered press became the next swing and is spent");
                _clock += 5f;
                _authority.StepSwings();
            }

            // 3. ranged weapons carry no impact power: arrows never break a guard (the guard cases below also fire them)
            Near(0f, _bow.impactPower, 1e-6f, "follow-ups: the bow's impact power is 0");
            Near(0f, _crossbow.impactPower, 1e-6f, "follow-ups: the crossbow's impact power is 0");
        }

        // ---- the enemy claw / maul through the legacy SubmitAttack path ---------------------------------

        private void EnemyClawAgainstTheRig()
        {
            ZonesOff();
            _clock += 3f;
            _rig.Health.Heal(1000f);
            _rig.Status.ClearStatuses();

            foreach ((WeaponData weapon, bool expectBreak, string label) c in new[] { (_claw, false, "claw (12)"), (_maul, false, "maul (30)"), (_bow, false, "arrow (0)"), (_crossbow, false, "bolt (0)") })
            {
                _rig.SetGuardHeld(true);
                _playerHits.Clear();
                Vector3 p = _rig.transform.position + new Vector3(0f, 1.15f, 1.8f);
                Vector3 towardRig = (_rig.CombatCenter - p).normalized;
                int attackerId = CombatRegistry.AllocateId();
                ICombatEntity attacker;
                AttackIntent intent;
                bool accepted;
                string why;
                if (c.weapon.IsRanged)
                {
                    Check(_rig.TryGetGuardShield(out GuardShieldShape shieldShape), "projectile guard: raised physical shield exists");
                    towardRig = (shieldShape.Position - p).normalized;
                    ProjectileAttacker rangedAttacker = new ProjectileAttacker
                    {
                        CombatId = attackerId, Point = p, Weapon = c.weapon, AimDirection = towardRig
                    };
                    attacker = rangedAttacker;
                    CombatRegistry.Register(attackerId, rangedAttacker);
                    intent = new AttackIntent(attackerId, EntityKind.Enemy, c.weapon, 1f, p, towardRig,
                        c.weapon.rangedRange, CombatRegistry.NextSequence(attackerId), 0f);
                    accepted = _authority.LaunchProjectileAsOwner(intent);
                    why = _authority.LastAttackRejectReason;
                    if (accepted) _authority.AdvanceProjectiles(1f);
                }
                else
                {
                    PoselessAttacker meleeAttacker = new PoselessAttacker
                    { CombatId = attackerId, Point = p, Weapon = c.weapon };
                    attacker = meleeAttacker;
                    CombatRegistry.Register(attackerId, meleeAttacker);
                    intent = new AttackIntent(attackerId, EntityKind.Enemy, c.weapon, 1f, p, towardRig,
                        c.weapon.reach + c.weapon.hitSphereRadius, CombatRegistry.NextSequence(attackerId), 0f);
                    accepted = _authority.SubmitAttackAsOwner(intent, new HitCandidate(_rig.CombatId, _rig.CombatCenter, false), out why);
                }
                CombatRegistry.Unregister(attackerId, attacker);

                Check(accepted && _playerHits.Count == 1, $"{c.label}: the enemy attack is accepted through the authority ({why})");
                if (_playerHits.Count == 1)
                {
                    DamageInfo info = _playerHits[0];
                    Check(info.Blocked && info.GuardBroken == c.expectBreak, $"{c.label}: blocked, guard broken = {c.expectBreak} (impact {info.ImpactPower})");
                    if (c.weapon.IsRanged) Check(info.GuardShieldContact, $"{c.label}: actual flight contacts the shield shape");
                }

                if (c.expectBreak)
                {
                    Check(_rig.Status.HasStatus(StatusKind.Slow), "maul: the player is slowed by the hit (hit slow works on players)");
                    Near(0.75f, _rig.Status.SpeedMultiplier, 1e-4f, "maul: player slow x0.75");
                    Near(1.5f, _rig.Status.RemainingOf(StatusKind.Slow), 0.05f, "maul: player slow 1.5 s");
                    Check(_authority.IsGuardStaggered(_rig.CombatId), "maul: the player's guard is staggered");
                }
                else
                {
                    Check(!_rig.Status.HasStatus(StatusKind.Slow), "a blocked hit applies no slow");
                }

                _rig.SetGuardHeld(false);
                _rig.Status.ClearStatuses();
                _clock += 4f;
                _rig.Health.Heal(1000f);
            }
        }

        // ---- stamina: the concept is gone (B3) ----------------------------------------------------------
        // D1b made stamina inert; B3 removed it. The source-text half (no runtime code names stamina) moved to EmberdelveSourceLint;
        // the data types may not carry it either (reflection, behavioural).

        private void NoStaminaMembers()
        {
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            int members = 0;
            foreach (Type type in new[] { typeof(PlayerRig), typeof(CharacterClassData), typeof(HudController), typeof(Emberdelve.Stats.EffectiveStatsSnapshot) })
                foreach (MemberInfo member in type.GetMembers(all))
                    if (member.Name.IndexOf("stamina", StringComparison.OrdinalIgnoreCase) >= 0) members++;
            Check(members == 0, $"PlayerRig, CharacterClassData, HudController and EffectiveStatsSnapshot have no stamina member ({members})");
        }

        // ---- HUD ----------------------------------------------------------------------------------------

        private void HudSection()
        {
            HudController hud = FindFirstObjectByType<HudController>();
            Check(hud != null, "a HudController is live in the scene");

            int staminaObjects = 0;
            foreach (Transform t in Resources.FindObjectsOfTypeAll<Transform>())
            {
                if (t == null || !t.gameObject.scene.IsValid()) continue;
                if (t.name.StartsWith("Stamina", StringComparison.Ordinal)) staminaObjects++;
            }
            Check(staminaObjects == 0, $"the HUD has no stamina bar objects in the scene ({staminaObjects})");

            // sanity: the health and mana bars are still built, so the absence is about stamina and not an empty HUD
            bool manaBar = false;
            foreach (Transform t in Resources.FindObjectsOfTypeAll<Transform>())
            {
                if (t != null && t.gameObject.scene.IsValid() && t.name == "ManaLabel") manaBar = true;
            }
            Check(manaBar, "the mana readout is still on the HUD (mana stays)");
        }

        // ---- real time: hit slow distance ----------------------------------------------------------------

        private EmberdelveInput RigInput => _rig.Input;

        private IEnumerator Hold(float seconds, Key[] keys, bool rightMouse, Action<float> sample = null)
        {
            // Headless batch mode has no focused keyboard or mouse, so the gate uses the input layer's override seams
            // (move / sprint / guard), exactly like the primary-button seam the combat smokes use. The real PlayerMovement
            // and MeleeCombat Update paths run from them.
            bool sprint = Array.IndexOf(keys, Key.LeftShift) >= 0;
            RigInput.MoveOverride = Array.IndexOf(keys, Key.W) >= 0 ? Vector2.up : Vector2.zero;
            RigInput.SprintHeldOverride = sprint;
            RigInput.SecondaryHeldOverride = rightMouse;

            float start = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - start < seconds)
            {
                yield return null;
                sample?.Invoke(Time.realtimeSinceStartup - start);
            }

            RigInput.MoveOverride = null;
            RigInput.SprintHeldOverride = null;
            RigInput.SecondaryHeldOverride = null;
            yield return null;
        }

        /// <summary>
        /// Walks forward for <paramref name="seconds"/> and returns the CRUISE speed between <paramref name="from"/> and the end
        /// (distance over time), so the acceleration ramp of a particular frame rate does not enter the number.
        /// <paramref name="hitAt"/> (when >= 0) runs <paramref name="onHit"/> once at that time of the walk.
        /// </summary>
        private IEnumerator Cruise(float seconds, float from, Action<float> result, float hitAt = -1f, Action onHit = null)
        {
            bool hit = false;
            bool haveStart = false;
            Vector3 p1 = default;
            float t1 = 0f, t2 = 0f;
            Vector3 p2 = default;
            yield return Hold(seconds, new[] { Key.W }, false, t =>
            {
                if (!hit && hitAt >= 0f && t >= hitAt) { hit = true; onHit?.Invoke(); }
                if (!haveStart && t >= from) { haveStart = true; p1 = _rig.transform.position; t1 = t; }
                if (haveStart) { p2 = _rig.transform.position; t2 = t; }
            });
            Vector3 d = p2 - p1;
            d.y = 0f;
            result(t2 > t1 ? d.magnitude / (t2 - t1) : 0f);
        }

        private IEnumerator HitSlowDistance()
        {
            _rig.Status.ClearStatuses();
            _rig.SetGuardHeld(false);
            _rig.Health.Heal(1000f);

            // baseline cruise speed
            float baseline = 0f;
            Teleport(WalkOrigin);
            yield return new WaitForSeconds(0.3f);
            yield return Cruise(1.0f, 0.6f, v => baseline = v);
            yield return new WaitForSeconds(0.5f);

            // cruising, an enemy sword hit lands (the authority puts the sword's hit slow on the player); the speed is read
            // 0.25 s later, still inside the 0.6 s slow
            Teleport(WalkOrigin);
            yield return new WaitForSeconds(0.3f);
            DamageInfo? hit = null;
            float slowed = 0f, slowAtHit = 1f;
            yield return Cruise(0.9f, 0.55f, v => slowed = v, 0.3f, () => { hit = SwingAtRig(_arcSword, true); slowAtHit = _rig.Status.SpeedMultiplier; });
            Check(hit.HasValue, "hit slow walk: the enemy sword hit the player");
            Near(0.9f, slowAtHit, 1e-4f, "hit slow walk: x0.9 on the player right after the hit");
            Near(_rig.Movement.ResolveWalkSpeed() * (1f - 18f / 300f) * 0.9f, slowed, 0.35f, "hit slow walk: the cruise speed is about the held-weapon walk speed x0.9");
            float ratio = baseline > 0f ? slowed / baseline : 0f;
            Debug.Log($"{Tag} cruise speed baseline {baseline:F3} m/s, slowed {slowed:F3} m/s, ratio {ratio:F3}");
            Check(baseline > 2f, $"hit slow walk: the baseline walk really moved the rig ({baseline:F2} m/s)");
            Check(ratio > 0.84f && ratio < 0.97f, $"hit slow walk: the slowed cruise speed is about 90 percent of the baseline (ratio {ratio:F3})");

            // after its 0.6 s the slow is gone and the cruise speed is back to the baseline
            yield return new WaitForSeconds(0.9f);
            Check(!_rig.Status.HasStatus(StatusKind.Slow), "hit slow walk: the slow expired after its 0.6 s");
            Teleport(WalkOrigin);
            yield return new WaitForSeconds(0.3f);
            float recovered = 0f;
            yield return Cruise(1.0f, 0.6f, v => recovered = v);
            float ratio2 = baseline > 0f ? recovered / baseline : 0f;
            Check(ratio2 > 0.94f && ratio2 < 1.06f, $"hit slow walk: after the slow the cruise speed is back to the baseline (ratio {ratio2:F3})");

            // an enemy agent honours the slow in its chase speed
            EnemyAgent agent = FindFirstObjectByType<EnemyAgent>(FindObjectsInactive.Include);
            if (agent != null && agent.Status != null)
            {
                // Hit-slow fixture targets an upright, awake enemy; dormant floor hit shapes are tested separately.
                agent.Brain.NotifyDamaged(DamageInfo.Invalid);
                agent.Brain.Tick(EnemyBrain.WakeSeconds, Time.time, agent.transform.position, agent.transform.forward, null);
                agent.RefreshDormantHitPose(null);
                agent.transform.position = Origin + new Vector3(0f, 0f, 20f);
                Physics.SyncTransforms();
                Scenario s = NewScenario(_arcSword);
                s.Who.Eye = agent.transform.position + new Vector3(0f, 1.65f, -1.6f);
                Check(agent.GetComponentInChildren<HitVolume>(true) != null, "chase: the enemy has hit volumes");
                float chase = agent.EffectiveChaseSpeed;
                _clock += 3f;
                int id;
                bool ok = _authority.TryBeginMeleeSwingAsOwner(Intent(s.Who, 0, 0), out id, out string why);
                Check(ok, $"chase: swing accepted ({why})");
                if (ok) Finish(id);
                Near(chase * 0.9f, agent.EffectiveChaseSpeed, 1e-4f, "chase: the enemy's chase speed is x0.9 after a sword hit (hit slow works on enemies)");
                agent.Status.ClearStatuses();
                s.Dispose();
            }
            else
            {
                Check(false, "chase: an enemy agent with a status component exists in the scene");
            }
        }

        // ---- real time: sprint and guard ----------------------------------------------------------------------------

        private IEnumerator SprintGuardMeasured()
        {
            _rig.Status.ClearStatuses();
            Teleport(WalkOrigin);
            yield return new WaitForSeconds(0.3f);

            float walkSpeed = _rig.Movement.ResolveWalkSpeed();

            // sprint for 0.6 s: it really moves faster than walking (not a vacuous test)
            float fastest = 0f;
            yield return Hold(0.6f, new[] { Key.W, Key.LeftShift }, false, _ => fastest = Mathf.Max(fastest, new Vector3(_rig.Movement.Velocity.x, 0f, _rig.Movement.Velocity.z).magnitude));
            Debug.Log($"{Tag} sprint measured: fastest {fastest:F2} m/s against walk {walkSpeed:F2}");
            Check(fastest > walkSpeed * 1.05f, $"sprint was real ({fastest:F2} m/s above the walk speed {walkSpeed:F2})");
            yield return new WaitForSeconds(0.4f);

            // block for 0.6 s: the guard is really up
            bool guardSeen = false;
            yield return Hold(0.6f, new Key[0], true, _ => guardSeen |= _rig.IsGuarding);
            Check(guardSeen, "the guard really went up while the button was held");
            _rig.SetGuardHeld(false);
        }
    }
}
