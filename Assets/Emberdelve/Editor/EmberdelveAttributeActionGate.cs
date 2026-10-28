using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Emberdelve.AI;
using Emberdelve.Combat;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Items;
using Emberdelve.Movement;
using Emberdelve.Player;
using Emberdelve.Skills;
using Emberdelve.Stats;

namespace Emberdelve.EditorTools
{
    /// <summary>
    /// U2A4 gate: the locomotion and weapon-damage half of Docs/Design/ATTRIBUTES.md, verified through the
    /// real movement resolver and the real combat authority rather than through a mirror of the formulas.
    ///
    /// It reuses the play-mode lifecycle EmberdelveAttributeVitalsGate established (and that U2A3-R2 fixed):
    /// an [InitializeOnLoad] hook subscribes to playModeStateChanged once per editor session, Run() records
    /// the request in SessionState so it survives the play-mode domain reload, opens a transient unsaved
    /// empty scene and asks for play mode. EnteredPlayMode starts the pass exactly once, and the rigs are
    /// built there with real AddComponent calls so PlayerRig.Awake, PlayerMovement.Awake (+CharacterController,
    /// +minMoveDistance 0), PlayerLook.Awake and HealthComponent.Awake all run the way a scene rig runs them.
    /// Nothing is saved, no scene or asset is regenerated, and no Awake is invoked by hand or by reflection.
    ///
    /// Movement, asserted as numbers the resolver actually produces:
    /// - both classes author move_speed 4.3, sprint/crouch stay authored (5.2/1.7 and 6.4/2.1);
    /// - the amendment's order clamp(authored * max(0.5, 1 + 0.025*DEX modifier) * passive/gear multiplier
    ///   + flat, 2, 8), including the 2 and 8 clamps, and NOT CharacterStatStack.Get's (flat + base) * multiplier;
    /// - the bare attribute term before any passive (warrior 4.3, archer 4.6225) and the same speed after
    ///   Fleet Footing (archer 5.315875) are separate numbers, and the StatsComponent query equals the
    ///   PlayerMovement resolver for the same gear state, so DEX is applied exactly once;
    /// - a gear DEX threshold crossing (modifier 0 -> 1, archer 3 -> 4) and its exact removal;
    /// - sprint/crouch keep their authored bases with their own passive/gear terms, and the guard, aim and
    ///   status multipliers still fold in on top.
    ///
    /// Damage, asserted as the authority's own applied numbers against an isolated fixed-distance target:
    /// real intents, real range/line-of-sight/cooldown validation, real health deltas, and the accepted and
    /// rejected counters of the authority itself. STR sword, DEX bow and crossbow, finesse rapier in both
    /// modifier orders, gear recalculation on equip and unequip, the charge and distance-falloff factors,
    /// the legacy passive factor applied once, and an enemy instigator whose damage is unchanged.
    ///
    /// Rate tests use a monotonic intent clock instead of waiting real frames, because the authority compares
    /// intent timestamps: a too-fast attack is genuinely rejected without the pass standing still for it.
    ///
    /// Run from the command line, e.g.
    ///   Unity.exe -batchmode -nographics -projectPath . -logFile Logs/u2a4.log
    ///           -executeMethod Emberdelve.EditorTools.EmberdelveAttributeActionGate.Run
    /// Exit 0 only when every check passed.
    /// </summary>
    [InitializeOnLoad]
    public static class EmberdelveAttributeActionGate
    {
        private const string Tag = "[Emberdelve][AttributeAction]";

        private const string RequestKey = "Emberdelve.AttributeAction.Request";
        private const string PendingKey = "Emberdelve.AttributeAction.Pending";
        private const string DoneKey = "Emberdelve.AttributeAction.Done";
        private const string ExitCodeKey = "Emberdelve.AttributeAction.ExitCode";

        private static double _watchdogStartedAt;

        static EmberdelveAttributeActionGate()
        {
            EditorApplication.playModeStateChanged += HandlePlayModeChange;
        }

        public static void Run()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Debug.LogError(Tag + " cannot request play mode from inside play mode");
                EmberdelveGateExit.Exit(1);
                return;
            }

            SessionState.SetString(RequestKey, string.Empty);
            SessionState.SetBool(PendingKey, false);
            SessionState.SetBool(DoneKey, false);
            SessionState.SetInt(ExitCodeKey, 1);

            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            Debug.Log(Tag + " requesting play mode in a transient empty scene");

            SessionState.SetString(RequestKey, "action");
            SessionState.SetBool(PendingKey, true);

            _watchdogStartedAt = EditorApplication.timeSinceStartup;
            EditorApplication.update += WatchForPlayModeStart;
            EditorApplication.isPlaying = true;
        }

        private static void WatchForPlayModeStart()
        {
            if (EditorApplication.isPlaying)
            {
                EditorApplication.update -= WatchForPlayModeStart;
                return;
            }

            if (EditorApplication.timeSinceStartup - _watchdogStartedAt > 45.0)
            {
                EditorApplication.update -= WatchForPlayModeStart;
                SessionState.SetString(RequestKey, string.Empty);
                SessionState.SetBool(PendingKey, false);
                SessionState.SetInt(ExitCodeKey, 1);
                Debug.LogError(Tag + " play mode never started - headless play mode is unavailable in this session.");
                EmberdelveGateExit.Exit(1);
            }
        }

        private static void HandlePlayModeChange(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.EnteredPlayMode)
            {
                if (string.IsNullOrEmpty(SessionState.GetString(RequestKey, string.Empty))) return;
                if (!SessionState.GetBool(PendingKey, false)) return;

                SessionState.SetBool(PendingKey, false);

                Debug.Log(Tag + " entered play mode, building the rigs through their real Awake");

                GameObject harnessObject = new GameObject("Emberdelve_AttributeActionHarness");
                harnessObject.AddComponent<Pass>();
            }
            else if (change == PlayModeStateChange.ExitingPlayMode)
            {
                if (string.IsNullOrEmpty(SessionState.GetString(RequestKey, string.Empty))) return;

                if (SessionState.GetBool(DoneKey, false))
                {
                    int exitCode = SessionState.GetInt(ExitCodeKey, 1);
                    SessionState.SetBool(DoneKey, false);
                    SessionState.SetString(RequestKey, string.Empty);
                    Debug.Log(Tag + $" play mode finished, exiting with stored code {exitCode}");
                    EditorApplication.delayCall += () => EmberdelveGateExit.Exit(exitCode);
                }
                else
                {
                    Debug.LogError(Tag + " play mode ended before the pass finished its assertions - reporting failure");
                    SessionState.SetInt(ExitCodeKey, 1);
                    SessionState.SetString(RequestKey, string.Empty);
                    EditorApplication.delayCall += () => EmberdelveGateExit.Exit(1);
                }
            }
        }

        /// <summary>One synchronous verification pass, running inside real play mode.</summary>
        private sealed class Pass : MonoBehaviour
        {
            /// <summary>
            /// Seconds of intent clock between accepted attacks. Longer than any weapon's cooldown times the
            /// authority's grace, so a test never waits a real frame for a cooldown to expire - the authority's
            /// own rule is still what decides acceptance.
            /// </summary>
            private const float AttackSpacing = 3f;

            private const float Tolerance = 0.011f;

            private int _checks;
            private int _failures;
            private float _startedAt;
            private bool _finished;
            private bool _stopping;

            private readonly List<GameObject> _objects = new List<GameObject>();
            private readonly List<ItemData> _tempItems = new List<ItemData>();
            private ItemCatalog _testCatalog;

            private CharacterClassData _warrior;
            private CharacterClassData _archer;
            private WeaponData _sword;
            private WeaponData _shieldWeapon;
            private WeaponData _bow;
            private WeaponData _crossbow;
            private WeaponData _rapier;
            private WeaponData _claw;
            private ItemData _cuirass;
            private ItemData _boots;
            private ItemData _fangBlade;
            private GameConfig _config;
            private EnemyData _servitor;
            private NavGridData _navGrid;

            private LocalCombatAuthority _authority;
            private TestTarget _target;
            private PlayerRig _warriorRig;
            private float _clock;

            private string _pristineWarrior;
            private string _pristineArcher;
            private string _pristineSword;
            private string _pristineBow;
            private string _pristineRapier;
            private string _pristineCuirass;

            private void Start()
            {
                _startedAt = Time.realtimeSinceStartup;

                try
                {
                    LoadAssets();
                    BuildAuthority();

                    CheckAuthoredData();

                    RunWarrior();
                    RunArcher();
                    CheckEnemyDamageUnchanged();

                    CheckDefinitionsUnchanged();
                }
                catch (System.Exception exception)
                {
                    _failures++;
                    Debug.LogError(Tag + " gate threw: " + exception);
                }
                finally
                {
                    for (int i = 0; i < _objects.Count; i++) if (_objects[i] != null) Object.Destroy(_objects[i]);
                    for (int i = 0; i < _tempItems.Count; i++) if (_tempItems[i] != null) Object.Destroy(_tempItems[i]);
                    _objects.Clear();
                    _tempItems.Clear();
                    EmberdelveTestCatalog.Release(ref _testCatalog);
                }

                int exitCode = _failures == 0 ? 0 : 1;
                Debug.Log($"{Tag} {(_failures == 0 ? "PASS" : "FAIL")} checks={_checks} failures={_failures} " +
                          $"authorityAccepted={(_authority != null ? _authority.AcceptedCount : -1)} " +
                          $"authorityRejected={(_authority != null ? _authority.RejectedCount : -1)} exit={exitCode}");

                SessionState.SetInt(ExitCodeKey, exitCode);
                SessionState.SetBool(DoneKey, true);
                _finished = true;
            }

            private void Update()
            {
                if (_stopping) return;

                if (_finished)
                {
                    _stopping = true;
                    EditorApplication.isPlaying = false;
                    return;
                }

                if (Time.realtimeSinceStartup - _startedAt > 60f)
                {
                    Debug.LogError(Tag + " watchdog: the pass never finished - reporting failure");
                    SessionState.SetInt(ExitCodeKey, 1);
                    SessionState.SetBool(DoneKey, true);
                    _stopping = true;
                    EditorApplication.isPlaying = false;
                }
            }

            // ------------------------------------------------------------------ authored data

            private void LoadAssets()
            {
                _warrior = LoadAsset<CharacterClassData>(EmberdelvePaths.Data + "/Class_Warrior.asset", "Class_Warrior");
                _archer = LoadAsset<CharacterClassData>(EmberdelvePaths.Data + "/Class_Archer.asset", "Class_Archer");
                _sword = LoadAsset<WeaponData>(EmberdelvePaths.Data + "/Weapon_AshenShortSword.asset", "Weapon_AshenShortSword");
                _shieldWeapon = LoadAsset<WeaponData>(EmberdelvePaths.Data + "/Weapon_EmberbulwarkShield.asset", "Weapon_EmberbulwarkShield");
                _bow = LoadAsset<WeaponData>(EmberdelvePaths.Data + "/Weapon_HollowBow.asset", "Weapon_HollowBow");
                _crossbow = LoadAsset<WeaponData>(EmberdelvePaths.Data + "/Weapon_EmberCrossbow.asset", "Weapon_EmberCrossbow");
                _rapier = LoadAsset<WeaponData>(EmberdelvePaths.Data + "/Weapon_EmberRapier.asset", "Weapon_EmberRapier");
                _claw = LoadAsset<WeaponData>(EmberdelvePaths.Data + "/Weapon_HollowClaw.asset", "Weapon_HollowClaw");
                _cuirass = LoadAsset<ItemData>(EmberdelvePaths.Data + "/Item_BoiledCuirass.asset", "Item_BoiledCuirass");
                _boots = LoadAsset<ItemData>(EmberdelvePaths.Data + "/Item_FootpadBoots.asset", "Item_FootpadBoots");
                _fangBlade = LoadAsset<ItemData>(EmberdelvePaths.Data + "/Item_EmberFangBlade.asset", "Item_EmberFangBlade");
                _servitor = LoadAsset<EnemyData>(EmberdelvePaths.Data + "/Enemy_HollowServitor.asset", "Enemy_HollowServitor");
                _navGrid = LoadAsset<NavGridData>(EmberdelvePaths.Data + "/EmberdelveNavGrid.asset", "EmberdelveNavGrid");
                _config = LoadAsset<GameConfig>(EmberdelvePaths.Data + "/EmberdelveGameConfig.asset", "EmberdelveGameConfig");

                _pristineWarrior = EditorJsonUtility.ToJson(_warrior);
                _pristineArcher = EditorJsonUtility.ToJson(_archer);
                _pristineSword = EditorJsonUtility.ToJson(_sword);
                _pristineBow = EditorJsonUtility.ToJson(_bow);
                _pristineRapier = EditorJsonUtility.ToJson(_rapier);
                _pristineCuirass = EditorJsonUtility.ToJson(_cuirass);
            }

            private void BuildAuthority()
            {
                GameObject authorityObject = new GameObject("U2A4_CombatAuthority");
                _objects.Add(authorityObject);
                _authority = authorityObject.AddComponent<LocalCombatAuthority>();

                // The same wiring the level builder performs for a runtime-created object: the authority's
                // own serialized GameConfig, so its line-of-sight check uses the real level mask.
                SetReference(_authority, "config", _config);

                EnsureTarget();

                // The gate's monotonic clock IS the authority's clock (injected), so rate tests advance
                // authority time deterministically instead of forging a timestamp inside the intent.
                _clock = Time.time;
                _authority.SetClock(() => _clock);
                _authority.SimulateBallistics = false;   // D2: this gate's literals assert straight-line geometry and the legacy range falloff; the ballistic sim has its own gate (EmberdelveProjectileSimGate).

                // D1a: this gate probes the range / line-of-sight / rate rules of SubmitAttack with the warrior's
                // sword. The player's melee goes through the swept swing now, so the arc-less melee path is refused
                // by default; this fixture opts in explicitly (its expectations are unchanged).
                _authority.AllowLegacyMeleeAttacks = true;
            }

            private void CheckAuthoredData()
            {
                CheckClose("warrior authored moveSpeed migrated to 4.3", 4.3f, _warrior.moveSpeed, "Docs/Design/ATTRIBUTES.md");
                CheckClose("archer authored moveSpeed migrated to 4.3", 4.3f, _archer.moveSpeed, "Docs/Design/ATTRIBUTES.md");
                CheckClose("warrior authored sprintSpeed unchanged", 5.2f, _warrior.sprintSpeed, "sprint ratios were not invented");
                CheckClose("warrior authored crouchSpeed unchanged", 1.7f, _warrior.crouchSpeed, "authored base retained");
                CheckClose("archer authored sprintSpeed unchanged", 6.4f, _archer.sprintSpeed, "sprint ratios were not invented");
                CheckClose("archer authored crouchSpeed unchanged", 2.1f, _archer.crouchSpeed, "authored base retained");

                CheckClose("warrior base handed to the rules: moveSpeed", 4.3f, _warrior.AttributeBases.MoveSpeed, "pristine authored base");
                CheckClose("archer base handed to the rules: moveSpeed", 4.3f, _archer.AttributeBases.MoveSpeed, "pristine authored base");

                Check(_rapier.finesse, "the rapier definition carries the explicit finesse marker");
                Check(_rapier.DamageScale == WeaponDamageScale.Finesse, "the rapier resolves to the finesse damage scale from its own data");
                Check(_sword.DamageScale == WeaponDamageScale.Melee, "the sword resolves to melee (STR) from its own data");
                Check(_crossbow.DamageScale == WeaponDamageScale.Ranged, "the crossbow resolves to ranged (DEX) from its own data");
                Check(_bow.DamageScale == WeaponDamageScale.Ranged, "the bow resolves to ranged (DEX) from its own data");
                Check(_claw.DamageScale == WeaponDamageScale.Melee, "the enemy claw resolves to melee and is not finesse-marked");
                Check(!_sword.finesse && !_bow.finesse && !_crossbow.finesse && !_claw.finesse,
                    "no other definition claims the finesse marker");

                CheckClose("sword baseDamage untouched", 26f, _sword.baseDamage, "damage numbers are not attribute data");
                CheckClose("bow baseDamage untouched", 34f, _bow.baseDamage, "damage numbers are not attribute data");
                CheckClose("crossbow baseDamage untouched", 24f, _crossbow.baseDamage, "damage numbers are not attribute data");
                CheckClose("rapier baseDamage untouched", 19f, _rapier.baseDamage, "damage numbers are not attribute data");
                CheckClose("claw baseDamage untouched", 15f, _claw.baseDamage, "enemy damage numbers untouched");
            }

            // -------------------------------------------------------------------- warrior

            private void RunWarrior()
            {
                PlayerRig rig = BuildRig("U2A4_Warrior", new Vector3(0f, 0f, 0f));
                _warriorRig = rig;
                rig.ApplyClass(_warrior, _config);

                Debug.Log($"{Tag} warrior snapshot: {rig.AttributeStats.Describe()} bonuses={rig.AttributeStats.DescribeBonuses()}");

                CheckInt("warrior STR modifier", 3, rig.AttributeStats.GetModifier(SixAttribute.Strength), "score 16");
                CheckInt("warrior DEX modifier", 0, rig.AttributeStats.GetModifier(SixAttribute.Dexterity), "score 10");

                // 1. Bare walking: authored 4.3, DEX modifier 0, no MoveSpeed passive on this class.
                CheckClose("warrior attribute move term 4.3 (DEX modifier 0)", 4.3f, rig.AttributeStats.MoveSpeed, "before any passive or gear");
                CheckWalk("warrior bare", rig, 4.3f, WarriorHeld);
                CheckClose("warrior sprint keeps its authored 5.2 (DEX modifier 0)", 5.2f * WarriorHeld, rig.Movement.ResolveGroundSpeed(true, false), "no invented sprint ratio");
                CheckClose("warrior crouch keeps its authored 1.7", 1.7f * WarriorHeld, rig.Movement.ResolveGroundSpeed(false, true), "the 2..8 clamp belongs to walking, not to crouch");

                CheckClose("warrior sword query 26 * 1.12 (STR modifier 3)", 29.12f, rig.AttributeStats.GetWeaponDamage(_sword), "melee scales off STR");
                CheckClose("warrior crossbow query 24 * 1.0 (DEX modifier 0)", 24f, rig.AttributeStats.GetWeaponDamage(_crossbow), "a ranged weapon never reads STR");
                CheckClose("warrior rapier query 19 * 1.12 (finesse picks STR 3 over DEX 0)", 21.28f, rig.AttributeStats.GetWeaponDamage(_rapier), "finesse, first modifier order");

                // 2. The existing guard / aim / status multipliers still fold in on top of the new rule.
                rig.SetGuardHeld(true);
                Check(rig.IsGuarding, "warrior can guard with the shield the class asset carries");
                CheckClose("warrior guarded walk 4.3 * 0.65 (D1b: blocking x0.65, was 0.45)", 2.795f * WarriorHeld, rig.Movement.ResolveGroundSpeed(false, false), "authored guardSpeedMultiplier retained");
                rig.SetGuardHeld(false);
                CheckClose("warrior walk back to 4.3 when the guard is lowered", 4.3f * WarriorHeld, rig.Movement.ResolveGroundSpeed(false, false), "guard is not baked into the rule");

                rig.SetAimingPenalty(0.5f);
                CheckClose("warrior aim-penalised walk 4.3 * 0.5", 2.15f * WarriorHeld, rig.Movement.ResolveGroundSpeed(false, false), "aim penalty retained");
                rig.SetAimingPenalty(1f);

                rig.ApplyStatus(new StatusSpec(StatusKind.Slow, 4f, 0.55f), 0);
                CheckClose("warrior slowed walk 4.3 * 0.55", 2.365f * WarriorHeld, rig.Movement.ResolveGroundSpeed(false, false), "status slow retained on top of the attribute rule");
                rig.ClearStatuses();
                CheckClose("warrior walk back to 4.3 once the slow is cleared", 4.3f * WarriorHeld, rig.Movement.ResolveGroundSpeed(false, false), "a status is not a permanent modifier");

                // 3. Real authored gear: Footpad Boots (MoveSpeed x1.18, SprintSpeed x1.12, DEX +1).
                CarryItem(rig, _boots);
                Equip(rig, _boots);
                CheckInt("boots raise effective DEX to 11, still modifier 0", 11, rig.AttributeStats.GetAttribute(SixAttribute.Dexterity), "floor((11-10)/2) = 0");
                CheckWalk("warrior with the authored boots", rig, 5.074f, WarriorHeld);
                CheckClose("warrior sprint with boots 5.2 * 1.12", 5.824f * WarriorHeld, rig.Movement.ResolveGroundSpeed(true, false), "the boots' own sprint term");
                Unequip(rig, EquipmentSlot.Legs);
                CheckWalk("warrior bare again after unequipping the boots", rig, 4.3f, WarriorHeld);

                // 4. Gear DEX threshold crossing: modifier 0 -> 1, and its exact removal.
                ItemData dexCharm = MakeModifierItem("u2a4_dex_charm", "U2A4 DEX+2 Charm", EquipmentSlot.Head,
                    2, null);
                CarryItem(rig, dexCharm);
                Equip(rig, dexCharm);
                CheckInt("DEX+2 crosses the threshold: effective DEX 12", 12, rig.AttributeStats.GetAttribute(SixAttribute.Dexterity), "modifier becomes 1");
                CheckInt("warrior DEX modifier after the crossing", 1, rig.AttributeStats.GetModifier(SixAttribute.Dexterity), "floor((12-10)/2) = 1");
                CheckClose("warrior attribute move term after the crossing 4.3 * 1.025", 4.4075f, rig.AttributeStats.MoveSpeed, "the crossing is live in the query");
                CheckWalk("warrior with the DEX charm", rig, 4.4075f, WarriorHeld);
                CheckClose("warrior sprint after the crossing 5.2 * 1.025", 5.33f * WarriorHeld, rig.Movement.ResolveGroundSpeed(true, false), "the same factor on the authored sprint base");
                CheckClose("warrior crouch after the crossing 1.7 * 1.025", 1.7425f * WarriorHeld, rig.Movement.ResolveGroundSpeed(false, true), "the same factor on the authored crouch base");
                Unequip(rig, EquipmentSlot.Head);
                CheckInt("DEX back to 10 when the charm comes off", 10, rig.AttributeStats.GetAttribute(SixAttribute.Dexterity), "recomputed from the pristine base");
                CheckWalk("warrior walk back to 4.3 after the crossing test", rig, 4.3f, WarriorHeld);

                // 5. The amendment's order, with an explicit multiplier AND flat on the same gear.
                ItemData orderCharm = MakeModifierItem("u2a4_order_charm", "U2A4 Long Stride", EquipmentSlot.Head,
                    2, new[] { new StatModifier(SkillStat.MoveSpeed, SkillModifierMode.Multiplier, 1.2f),
                               new StatModifier(SkillStat.MoveSpeed, SkillModifierMode.Flat, 1f) });
                CarryItem(rig, orderCharm);
                Equip(rig, orderCharm);
                CheckWalk("warrior with DEX+2, x1.2 and +1.0 flat", rig, 6.289f, WarriorHeld);
                float wrongOrder = (4.4075f + 1f) * 1.2f;
                Check(Mathf.Abs(rig.Movement.ResolveGroundSpeed(false, false) - wrongOrder) > 0.15f,
                    $"the resolver is NOT CharacterStatStack.Get's (flat + base) * multiplier order ({wrongOrder:F3} would have been wrong)");
                Unequip(rig, EquipmentSlot.Head);
                CheckWalk("warrior walk back to 4.3 after the order test", rig, 4.3f, WarriorHeld);

                // 6. The 2..8 clamp of the walking rule.
                ItemData fastCharm = MakeModifierItem("u2a4_fast_charm", "U2A4 Overlong Stride", EquipmentSlot.Head,
                    2, new[] { new StatModifier(SkillStat.MoveSpeed, SkillModifierMode.Multiplier, 3f) });
                CarryItem(rig, fastCharm);
                Equip(rig, fastCharm);
                CheckWalk("warrior walking clamps at the 8 ceiling", rig, 8f, WarriorHeld);
                Unequip(rig, EquipmentSlot.Head);

                ItemData slowCharm = MakeModifierItem("u2a4_slow_charm", "U2A4 Weighted Stride", EquipmentSlot.Head,
                    2, new[] { new StatModifier(SkillStat.MoveSpeed, SkillModifierMode.Multiplier, 0.2f) });
                CarryItem(rig, slowCharm);
                Equip(rig, slowCharm);
                CheckWalk("warrior walking clamps at the 2 floor", rig, 2f, WarriorHeld);
                Unequip(rig, EquipmentSlot.Head);
                CheckWalk("warrior walking is 4.3 again with nothing worn", rig, 4.3f, WarriorHeld);

                // 7. Real authority damage: STR sword.
                SubmitAttack("warrior sword at 2.4m", rig, _sword, 2.4f, 1f, _sword.reach + _sword.hitSphereRadius,
                    true, 34.944f, "26 * 1.12 (STR) * 1.2 (Ashen Might), applied once");
                CheckClose("warrior outgoing multiplier query 1.12 * 1.2", 1.344f, rig.GetOutgoingDamageMultiplier(_sword),
                    "attribute factor and legacy passive factor, one each");
                Check(Mathf.Abs(LastDelta - 26f * 1.344f * 1.12f) > 1f,
                    $"the sword delta is not the attribute factor applied twice ({LastDelta:F3} against {26f * 1.344f * 1.12f:F3})");

                // 8. DEX crossbow: a ranged weapon on a STR-heavy class still reads DEX. The authority only
                // accepts the weapon the rig actually holds, so the crossbow comes into the hand through the
                // class's authored secondary slot (real loadout swap) instead of being named in the intent.
                Check(rig.Loadout.SwitchToSlot(1), "the warrior loadout switched to its authored crossbow secondary");
                Check(rig.MainHandWeapon == _crossbow, "the crossbow is the weapon the warrior actually holds");
                SubmitAttack("warrior crossbow at 10m (inside its 12m falloff start)", rig, _crossbow, 10f, 1f,
                    _crossbow.rangedRange, true, 24f, "24 * 1.0 (DEX modifier 0), no STR contribution");
                Check(rig.Loadout.SwitchToSlot(0), "the warrior loadout switched back to the sword");

                // 9. Finesse rapier through the real gear path: the Fang Blade puts it in the main hand.
                CarryItem(rig, _fangBlade);
                Equip(rig, _fangBlade);
                Check(rig.MainHandWeapon == _rapier, "equipping the Fang Blade really put the rapier in the main hand");
                SubmitAttack("warrior rapier at 2.6m (finesse picks STR 3)", rig, _rapier, 2.6f, 1f,
                    _rapier.reach + _rapier.hitSphereRadius, true, 25.536f, "19 * 1.12 * 1.2");

                ItemData dexBigCharm = MakeModifierItem("u2a4_dex_big", "U2A4 DEX+8 Charm", EquipmentSlot.Head,
                    8, null);
                CarryItem(rig, dexBigCharm);
                Equip(rig, dexBigCharm);
                CheckInt("warrior DEX+8 gives DEX 18, modifier 4", 4, rig.AttributeStats.GetModifier(SixAttribute.Dexterity), "now better than STR 3");
                SubmitAttack("warrior rapier with DEX 18 (finesse picks DEX 4)", rig, _rapier, 2.6f, 1f,
                    _rapier.reach + _rapier.hitSphereRadius, true, 26.448f, "19 * 1.16 * 1.2, gear recalculated");
                Unequip(rig, EquipmentSlot.Head);
                CheckInt("warrior DEX back to 10, STR 16 wins again", 3, rig.AttributeStats.GetModifier(SixAttribute.Strength), "STR modifier unchanged by the gear cycle");
                SubmitAttack("warrior rapier after unequipping (back to STR)", rig, _rapier, 2.6f, 1f,
                    _rapier.reach + _rapier.hitSphereRadius, true, 25.536f, "no stacking from the removed charm");

                Unequip(rig, EquipmentSlot.Weapon);
                Check(rig.MainHandWeapon == _sword, "unequipping the blade puts the class sword back in the hand");

                // 10. The legacy temporary buff factor still multiplies on top of the attribute factor.
                rig.ApplyStatus(new StatusSpec(StatusKind.Empowered, 6f, 1.25f), 0);
                CheckClose("warrior outgoing multiplier with the self buff 1.12 * 1.2 * 1.25", 1.68f,
                    rig.GetOutgoingDamageMultiplier(_sword), "status factor preserved alongside the attribute factor");
                SubmitAttack("warrior sword while Empowered", rig, _sword, 2.4f, 1f,
                    _sword.reach + _sword.hitSphereRadius, true, 43.68f, "26 * 1.12 * 1.2 * 1.25");
                rig.ClearStatuses();

                // 11. The authority's own validation is still real: cooldown, range and line of sight.
                int rejectedBefore = _authority.RejectedCount;
                float healthBefore = _target.CurrentHealth;
                SubmitAttackExpecting("warrior sword swung twice inside the cooldown", rig, _sword, 2.4f, 1f,
                    _sword.reach + _sword.hitSphereRadius, false, _clock + 0.05f);
                Check(_authority.RejectedCount == rejectedBefore + 1, "the authority counted the too-fast attack as a rejection");
                CheckClose("the rejected attack applied no damage", 0f, healthBefore - _target.CurrentHealth, "a rejection is not a partial hit");

                Check(rig.Loadout.SwitchToSlot(1) && rig.MainHandWeapon == _crossbow, "the crossbow is back in the warrior's hand for the range and line of sight checks");
                rejectedBefore = _authority.RejectedCount;
                healthBefore = _target.CurrentHealth;
                SubmitAttackExpecting("warrior crossbow claimed 60m outside its 34m range", rig, _crossbow, 60f, 1f,
                    _crossbow.rangedRange, false, _clock + AttackSpacing);
                Check(_authority.RejectedCount == rejectedBefore + 1, "the authority counted the out-of-range claim as a rejection");
                CheckClose("the out-of-range claim applied no damage", 0f, healthBefore - _target.CurrentHealth, "range validation is real");

                GameObject wall = BuildLevelWall(rig, 10f);
                _objects.Add(wall);
                int acceptedBeforeWallShot = _authority.AcceptedCount;
                rejectedBefore = _authority.RejectedCount;
                healthBefore = _target.CurrentHealth;
                AssertWallStopsProjectile("warrior crossbow legally launches into level wall", rig, _crossbow,
                    10f, _crossbow.rangedRange, wall.GetComponent<Collider>());
                Check(_authority.AcceptedCount == acceptedBeforeWallShot + 1,
                    "the wall-obstructed projectile is accepted by launch authority");
                Check(_authority.RejectedCount == rejectedBefore,
                    "a physical wall impact does not turn a legal projectile launch into a launch rejection");
                CheckClose("the accepted projectile stopped at the wall before target damage", 0f,
                    healthBefore - _target.CurrentHealth, "actual in-flight collision with level geometry");

                // The legacy direct-hit endpoint remains a separate negative route for a ranged weapon.
                SubmitAttackExpecting("warrior crossbow claim sent through the direct-hit-only endpoint (wall present)", rig, _crossbow, 10f, 1f,
                    _crossbow.rangedRange, false, _clock + AttackSpacing);
                Check(_authority.RejectedCount == rejectedBefore + 1, "the authority rejected the direct-hit-only ranged claim");
                CheckClose("the rejected direct-hit claim applied no damage", 0f, healthBefore - _target.CurrentHealth, "ranged hits must resolve through projectile flight");

                // The collider is switched off rather than the object destroyed: Object.Destroy is deferred to
                // the end of the frame, and the very next assertion in this same frame needs a clear line.
                Collider wallCollider = wall != null ? wall.GetComponent<Collider>() : null;
                if (wallCollider != null) wallCollider.enabled = false;
                Physics.SyncTransforms();

                SubmitAttack("warrior crossbow again once the wall is gone", rig, _crossbow, 10f, 1f,
                    _crossbow.rangedRange, true, 24f, "the same shot is legal with a clear line");
                Check(rig.Loadout.SwitchToSlot(0), "the warrior loadout ends on the sword");

                Debug.Log($"{Tag} warrior movement end: walk={rig.Movement.ResolveGroundSpeed(false, false):F4} " +
                          $"sprint={rig.Movement.ResolveGroundSpeed(true, false):F4} crouch={rig.Movement.ResolveGroundSpeed(false, true):F4} " +
                          $"snapshot={rig.AttributeStats.Describe()}");
            }

            // --------------------------------------------------------------------- archer

            private void RunArcher()
            {
                PlayerRig rig = BuildRig("U2A4_Archer", new Vector3(0f, 0f, 24f));
                rig.ApplyClass(_archer, _config);

                Debug.Log($"{Tag} archer snapshot: {rig.AttributeStats.Describe()} bonuses={rig.AttributeStats.DescribeBonuses()}");

                CheckInt("archer STR modifier", 0, rig.AttributeStats.GetModifier(SixAttribute.Strength), "score 10");
                CheckInt("archer DEX modifier", 3, rig.AttributeStats.GetModifier(SixAttribute.Dexterity), "score 16");

                // 1. The bare attribute term (4.6225) and the same speed after Fleet Footing are separate numbers.
                CheckClose("archer attribute move term 4.3 * 1.075 BEFORE Fleet Footing", 4.6225f, rig.AttributeStats.MoveSpeed, "DEX modifier 3, no passive folded in");
                CheckClose("archer DEX locomotion factor 1 + 0.025*3", 1.075f, rig.AttributeStats.MoveSpeedFactor, "the amendment's DEX term");
                CheckWalk("archer walking with Fleet Footing on top", rig, 5.315875f, ArcherHeld);
                CheckClose("archer sprint 6.4 * 1.075 * 1.15", 7.912f * ArcherHeld, rig.Movement.ResolveGroundSpeed(true, false), "authored sprint base with its own passive term, x the ranged held factor (B1: bow 4 points of 300)");
                CheckClose("archer crouch 2.1 * 1.075", 2.2575f * ArcherHeld, rig.Movement.ResolveGroundSpeed(false, true), "DEX applies to crouch too, base retained, x the ranged held factor");
                Check(rig.Movement.ResolveGroundSpeed(true, false) > rig.Movement.ResolveGroundSpeed(false, false) + 0.1f,
                    "sprint is still faster than walking after the migration");

                // 2. Gear DEX crossing 3 -> 4 and its exact removal.
                ItemData dexCharm = MakeModifierItem("u2a4_archer_dex_charm", "U2A4 DEX+2 Charm", EquipmentSlot.Head,
                    2, null);
                CarryItem(rig, dexCharm);
                Equip(rig, dexCharm);
                CheckInt("archer DEX+2 gives 18, modifier 4", 4, rig.AttributeStats.GetModifier(SixAttribute.Dexterity), "crossed one threshold");
                CheckClose("archer attribute move term after the crossing 4.3 * 1.1", 4.73f, rig.AttributeStats.MoveSpeed, "before Fleet Footing again");
                CheckWalk("archer walking after the crossing", rig, 5.4395f, ArcherHeld);
                Unequip(rig, EquipmentSlot.Head);
                CheckWalk("archer walking back to 5.315875 after unequipping", rig, 5.315875f, ArcherHeld);

                // 3. Authored gear on top of the passive.
                CarryItem(rig, _boots);
                Equip(rig, _boots);
                CheckWalk("archer walking with Fleet Footing and the boots", rig, 6.2727325f, ArcherHeld);
                CheckClose("archer sprint with Fleet Footing and the boots", 8.86144f * ArcherHeld, rig.Movement.ResolveGroundSpeed(true, false),
                    "sprint is not clamped into the walking band: its authored base and both gear terms are retained");
                Unequip(rig, EquipmentSlot.Legs);
                CheckWalk("archer walking back to 5.315875", rig, 5.315875f, ArcherHeld);

                // 4. The amendment's order with a passive multiplier, a gear multiplier and a gear flat together.
                ItemData orderCharm = MakeModifierItem("u2a4_archer_order_charm", "U2A4 Long Stride", EquipmentSlot.Head,
                    2, new[] { new StatModifier(SkillStat.MoveSpeed, SkillModifierMode.Multiplier, 1.2f),
                               new StatModifier(SkillStat.MoveSpeed, SkillModifierMode.Flat, 1f) });
                CarryItem(rig, orderCharm);
                Equip(rig, orderCharm);
                CheckWalk("archer with DEX+2, x1.2 and +1.0 flat", rig, 7.5274f, ArcherHeld);
                float wrongOrder = (4.73f * 1.15f + 1f) * 1.2f * ArcherHeld;
                Check(Mathf.Abs(rig.Movement.ResolveGroundSpeed(false, false) - wrongOrder) > 0.15f,
                    $"the archer resolver is NOT (flat + base) * multiplier either ({wrongOrder:F3} would have been wrong)");
                Unequip(rig, EquipmentSlot.Head);

                // 5. Real authority damage: DEX bow, its charge factor and its distance falloff.
                SubmitAttack("archer bow at 15m (inside its 18m falloff start)", rig, _bow, 15f, 1f, _bow.rangedRange,
                    true, 44.9344f, "34 * 1.12 (DEX) * 1.18 (Farshot), applied once");
                CheckClose("archer outgoing multiplier query 1.12 * 1.18", 1.3216f, rig.GetOutgoingDamageMultiplier(_bow),
                    "attribute factor and legacy passive factor, one each");
                CheckClose("archer bow query * legacy factor equals the authority delta", 44.9344f,
                    rig.AttributeStats.GetWeaponDamage(_bow) * rig.Stats.Multiplier(SkillStat.RangedDamage),
                    "the query agrees with the applied damage before any target defense");

                SubmitAttack("archer bow at 15m at half charge", rig, _bow, 15f, 0.5f, _bow.rangedRange,
                    true, 31.45408f, "the authored minChargeDamageScale lerp is preserved");
                SubmitAttack("archer bow at 30m (inside the falloff ramp)", rig, _bow, 30f, 1f, _bow.rangedRange,
                    true, 34.806106f, "30m muzzle distance minus independent .525m swept-contact radius, then authored falloff before attribute factor");

                // 6. Finesse rapier in the other modifier order: DEX 3 beats STR 0, then STR 4 beats DEX 3.
                // The rapier is this class's authored secondary, so it reaches the hand through the real
                // loadout swap rather than by naming a weapon the rig is not holding.
                Check(rig.Loadout.SwitchToSlot(1), "the archer loadout switched to its authored rapier secondary");
                Check(rig.MainHandWeapon == _rapier, "the rapier is the weapon the archer actually holds");
                SubmitAttack("archer rapier at 2.6m (finesse picks DEX 3)", rig, _rapier, 2.6f, 1f,
                    _rapier.reach + _rapier.hitSphereRadius, true, 21.28f, "19 * 1.12, no melee passive on this class");

                ItemData strCharm = MakeModifierItem("u2a4_archer_str_charm", "U2A4 STR+8 Charm", EquipmentSlot.Head,
                    0, null, 8);
                CarryItem(rig, strCharm);
                Equip(rig, strCharm);
                CheckInt("archer STR+8 gives STR 18, modifier 4", 4, rig.AttributeStats.GetModifier(SixAttribute.Strength), "now better than DEX 3");
                SubmitAttack("archer rapier with STR 18 (finesse picks STR 4)", rig, _rapier, 2.6f, 1f,
                    _rapier.reach + _rapier.hitSphereRadius, true, 22.04f, "19 * 1.16, the other order");
                Unequip(rig, EquipmentSlot.Head);
                SubmitAttack("archer rapier after unequipping (DEX 3 wins again)", rig, _rapier, 2.6f, 1f,
                    _rapier.reach + _rapier.hitSphereRadius, true, 21.28f, "no stacking from the removed charm");
                Check(rig.Loadout.SwitchToSlot(0), "the archer loadout switched back to the bow");
                Check(rig.MainHandWeapon == _bow, "the bow is back in the archer's main hand");

                // 7. Gear recalculation of damage: DEX+2 raises the bow, unequipping removes it exactly.
                Equip(rig, dexCharm);
                SubmitAttack("archer bow with DEX 18 equipped", rig, _bow, 15f, 1f, _bow.rangedRange,
                    true, 46.5392f, "34 * 1.16 * 1.18");
                Unequip(rig, EquipmentSlot.Head);
                SubmitAttack("archer bow again with nothing worn", rig, _bow, 15f, 1f, _bow.rangedRange,
                    true, 44.9344f, "the same shot, the same number as before the gear");

                Debug.Log($"{Tag} archer movement end: walk={rig.Movement.ResolveGroundSpeed(false, false):F4} " +
                          $"sprint={rig.Movement.ResolveGroundSpeed(true, false):F4} crouch={rig.Movement.ResolveGroundSpeed(false, true):F4} " +
                          $"snapshot={rig.AttributeStats.Describe()}");
            }

            // ----------------------------------------------------------------------- enemy

            private void CheckEnemyDamageUnchanged()
            {
                PlayerRig rig = _warriorRig;
                if (rig == null) { Check(false, "the warrior rig is available for the enemy damage check"); return; }

                GameObject enemyObject = new GameObject("U2A4_Enemy");
                enemyObject.transform.position = rig.transform.position + new Vector3(_claw.reach, 0f, 0f);
                int enemyLayer = LayerMask.NameToLayer(GameConfig.EnemyLayerName);
                if (enemyLayer >= 0) enemyObject.layer = enemyLayer;
                _objects.Add(enemyObject);

                enemyObject.AddComponent<HealthComponent>();
                EnemyAgent enemy = enemyObject.AddComponent<EnemyAgent>();
                SetReference(enemy, "data", _servitor);
                SetReference(enemy, "navGrid", _navGrid);
                SetReference(enemy, "config", _config);

                // Awake registered it as a monster-team combat entity. It is then disabled so it cannot
                // wander, attack or pollute the authority counters this pass is asserting.
                enemy.enabled = false;
                Physics.SyncTransforms();

                Check(enemy != null && !enemy.IsDead, "the enemy agent is a live combat entity");
                CheckClose("enemy outgoing multiplier is still status-only (1)", 1f, enemy.GetOutgoingDamageMultiplier(_claw),
                    "no attribute factor reaches an entity that has no attribute component");

                rig.SetGuardHeld(false);
                float playerHealthBefore = rig.CurrentHealth;
                int acceptedBefore = _authority.AcceptedCount;

                _clock += AttackSpacing;
                Vector3 origin = enemy.CombatCenter;
                Vector3 hitPoint = rig.CombatCenter;
                AttackIntent intent = new AttackIntent(enemy.CombatId, enemy.Kind, _claw, 1f, origin,
                    hitPoint - origin, _claw.reach + _claw.hitSphereRadius,
                    CombatRegistry.NextSequence(enemy.CombatId), _clock);

                bool accepted = _authority.SubmitAttackAsOwner(intent, new HitCandidate(rig.CombatId, hitPoint, false));

                Check(accepted, "the authority accepted the enemy's claw attack against the player");
                Check(_authority.AcceptedCount == acceptedBefore + 1, "the enemy attack counted as one accepted attack");
                CheckClose("enemy claw damage is the authored 15, unchanged", 15f, playerHealthBefore - rig.CurrentHealth,
                    "15 base damage, no attribute factor, no headshot, the player was not guarding");
                CheckClose("player health after the enemy hit", 105f, rig.CurrentHealth, "120 - 15");
            }

            private void CheckDefinitionsUnchanged()
            {
                Check(_pristineWarrior == EditorJsonUtility.ToJson(_warrior), "the warrior class asset is untouched by the whole pass");
                Check(_pristineArcher == EditorJsonUtility.ToJson(_archer), "the archer class asset is untouched by the whole pass");
                Check(_pristineSword == EditorJsonUtility.ToJson(_sword), "the sword definition is untouched");
                Check(_pristineBow == EditorJsonUtility.ToJson(_bow), "the bow definition is untouched");
                Check(_pristineRapier == EditorJsonUtility.ToJson(_rapier), "the rapier definition is untouched");
                Check(_pristineCuirass == EditorJsonUtility.ToJson(_cuirass), "the cuirass definition is untouched");
            }

            // ------------------------------------------------------------------ helpers

            private PlayerRig BuildRig(string name, Vector3 position)
            {
                GameObject body = new GameObject(name);
                body.transform.position = position;
                _objects.Add(body);

                PlayerRig rig = body.AddComponent<PlayerRig>();
                rig.WireRig();

                body.AddComponent<Inventory>();
                // R1b: the authority reads the magazine through the rig (IAmmoSource -> RangedCombat); a rig with
                // no ranged component has no ammunition and its ranged attacks are refused.
                body.AddComponent<RangedCombat>();
                body.AddComponent<ClassLoadout>();
                rig.WireRig();

                rig.Inventory.Initialize(rig);
                rig.Equipment.Initialize(rig);

                Physics.SyncTransforms();
                return rig;
            }

            /// <summary>
            /// The isolated fixed-distance target: no defense, no AI, no collider, a pool big enough that
            /// nothing in this pass can kill it. Its CombatCenter is placed exactly <c>distance</c> from the
            /// attacker's CombatCenter along the attacker's facing. Since R1a the authority measures reach and
            /// falloff from the instigator's own CombatCenter (not from a client-supplied eye/muzzle origin), so
            /// this is exactly the distance the authority measures and the distance this pass asserts.
            /// </summary>
            private void PlaceTarget(PlayerRig rig, float distance)
            {
                Vector3 origin = rig.CombatCenter;
                Vector3 hitPoint = origin + rig.Facing * distance;
                _target.transform.position = hitPoint - Vector3.up * 1.1f;
                Physics.SyncTransforms();
            }

            private float LastDelta { get; set; }

            private void SubmitAttack(string label, PlayerRig rig, WeaponData weapon, float distance, float charge,
                float maxRange, bool expectAccepted, float expectedDamage, string note)
            {
                _clock += AttackSpacing;
                SubmitAttackAtClock(label, rig, weapon, distance, charge, maxRange, expectAccepted, expectedDamage, note);
            }

            private void AssertWallStopsProjectile(string label, PlayerRig rig, WeaponData weapon, float distance,
                float maxRange, Collider expectedWall)
            {
                Vector3 muzzle = rig.GetMuzzlePosition();
                Vector3 direction = rig.AimDirection.normalized;
                Vector3 targetCenter = muzzle + direction * distance;
                _target.transform.position = targetCenter - Vector3.up * 1.1f;
                Physics.SyncTransforms();

                float radius = Mathf.Max(0.01f, weapon.projectileRadius * 0.5f);
                LayerMask levelMask = _config != null ? _config.levelMask : ~0;
                bool wallIsInFlightPath = Physics.SphereCast(muzzle, radius, direction, out RaycastHit wallHit,
                    distance, levelMask, QueryTriggerInteraction.Ignore);
                Check(wallIsInFlightPath && wallHit.collider == expectedWall && wallHit.distance < distance,
                    $"{label}: independent swept-ray precondition finds the expected wall first ({(wallIsInFlightPath && wallHit.collider != null ? wallHit.collider.name : "none")})");

                RangedCombat ranged = rig.GetComponent<RangedCombat>();
                if (ranged != null && ranged.Magazine < 1) EmberdelveKitFixtures.RefundAmmo(ranged, 3);
                _clock += AttackSpacing;
                AttackIntent launch = new AttackIntent(rig.CombatId, rig.Kind, weapon, 1f, muzzle, direction,
                    maxRange, CombatRegistry.NextSequence(rig.CombatId), _clock);
                float healthBefore = _target.CurrentHealth;
                bool accepted = _authority.LaunchProjectileAsOwner(launch);
                Check(accepted, $"{label}: valid projectile launch accepted ({_authority.LastAttackRejectReason})");
                if (accepted)
                {
                    for (int frame = 0; frame < 300; frame++) _authority.AdvanceProjectiles(1f / 60f);
                }
                CheckClose($"{label}: target remains unharmed after the flight", 0f,
                    healthBefore - _target.CurrentHealth, "the accepted arrow terminates at level geometry");
            }

            private void SubmitAttackExpecting(string label, PlayerRig rig, WeaponData weapon, float distance,
                float charge, float maxRange, bool expectAccepted, float timestamp)
            {
                _clock = timestamp;
                SubmitAttackAtClock(label, rig, weapon, distance, charge, maxRange, expectAccepted, 0f,
                    "expected rejection, so no damage at all");
            }

            private void SubmitAttackAtClock(string label, PlayerRig rig, WeaponData weapon, float distance,
                float charge, float maxRange, bool expectAccepted, float expectedDamage, string note)
            {
                PlaceTarget(rig, distance);

                // R1b: the authority now refuses a ranged attack from an empty magazine. These fixtures test
                // damage/range/LOS, not ammunition, so a legitimate round is loaded in the weapon in hand
                // (RangedCombat.RefundAmmo) whenever the magazine is empty. The accepted attacks spend it.
                RangedCombat ranged = weapon.IsRanged ? rig.GetComponent<RangedCombat>() : null;
                if (ranged != null && ranged.Magazine < 1) EmberdelveKitFixtures.RefundAmmo(ranged, 3);

                Vector3 origin = rig.AimOrigin;
                Vector3 hitPoint = _target.CombatCenter;

                AttackIntent intent = new AttackIntent(rig.CombatId, rig.Kind, weapon, charge, origin,
                    hitPoint - origin, maxRange, CombatRegistry.NextSequence(rig.CombatId), _clock);

                int acceptedBefore = _authority.AcceptedCount;
                int rejectedBefore = _authority.RejectedCount;
                float healthBefore = _target.CurrentHealth;

                if (weapon.IsRanged && expectAccepted)
                {
                    Vector3 muzzle = rig.GetMuzzlePosition();
                    Vector3 direction = rig.AimDirection.normalized;
                    // Put this analytic body fixture on the actual launch ray. Its authored center distance
                    // is the scenario distance; expected falloff uses the independently predicted first
                    // swept-sphere contact, including the authority's body and projectile radii.
                    Vector3 targetCenter = muzzle + direction * distance;
                    _target.transform.position = targetCenter - Vector3.up * 1.1f;
                    Physics.SyncTransforms();
                    float collisionRadius = LocalCombatAuthority.TargetBodyRadius + Mathf.Max(0.01f, weapon.projectileRadius * 0.5f);
                    float contactDistance = Mathf.Max(0f, Vector3.Distance(muzzle, _target.CombatCenter) - collisionRadius);
                    float flatBonus = rig.GetFlatWeaponDamageBonus(weapon);
                    float boostedBase = weapon.baseDamage + flatBonus;
                    float falloffT = weapon.falloffEndRange > weapon.falloffStartRange
                        ? Mathf.Clamp01((contactDistance - weapon.falloffStartRange) / (weapon.falloffEndRange - weapon.falloffStartRange))
                        : 0f;
                    float independentFalloff = contactDistance <= weapon.falloffStartRange
                        ? boostedBase
                        : Mathf.Lerp(boostedBase, boostedBase * weapon.falloffMinScale, falloffT);
                    float independentDamage = independentFalloff
                        * Mathf.Lerp(weapon.minChargeDamageScale, 1f, charge)
                        * rig.GetOutgoingDamageMultiplier(weapon);
                    CheckClose($"{label}: literal expectation matches independent muzzle-to-swept-contact calculation",
                        expectedDamage, independentDamage, $"muzzle distance {contactDistance:F3} m after body/projectile radii");
                    AttackIntent launch = new AttackIntent(rig.CombatId, rig.Kind, weapon, charge,
                        muzzle, direction, maxRange, CombatRegistry.NextSequence(rig.CombatId), _clock);
                    bool launched = _authority.LaunchProjectileAsOwner(launch);
                    Check(launched, $"{label}: authority accepted the real projectile launch ({_authority.LastAttackRejectReason})");
                    if (launched)
                    {
                        // The editor gate has no rendered frame loop. Advance the actual projectile in small
                        // bounded steps so world geometry and body contact use the same authority path.
                        for (int frame = 0; frame < 300 && _target.CurrentHealth >= healthBefore; frame++)
                            _authority.AdvanceProjectiles(1f / 60f);
                    }

                    LastDelta = healthBefore - _target.CurrentHealth;
                    Check(_authority.AcceptedCount == acceptedBefore + (launched ? 1 : 0),
                        $"{label}: launch counter moved {acceptedBefore} -> {_authority.AcceptedCount}");
                    Check(_authority.RejectedCount == rejectedBefore,
                        $"{label}: rejected counter stayed at {rejectedBefore} after launch");
                    CheckClose($"{label} applied health delta", expectedDamage, LastDelta, note);
                    Debug.Log($"{Tag} {label}: projectile distance={Vector3.Distance(muzzle, _target.CombatCenter):F2} " +
                              $"charge={charge:F2} delta={LastDelta:F4} expected={expectedDamage:F4} launched={launched}");
                    return;
                }

                bool accepted = _authority.SubmitAttackAsOwner(intent, new HitCandidate(_target.CombatId, hitPoint, false));
                LastDelta = healthBefore - _target.CurrentHealth;

                Check(accepted == expectAccepted,
                    $"{label}: authority accepted={accepted} (expected {expectAccepted}) at {_clock:F2}s");
                Check(_authority.AcceptedCount == acceptedBefore + (expectAccepted ? 1 : 0),
                    $"{label}: accepted counter moved {acceptedBefore} -> {_authority.AcceptedCount}");
                Check(_authority.RejectedCount == rejectedBefore + (expectAccepted ? 0 : 1),
                    $"{label}: rejected counter moved {rejectedBefore} -> {_authority.RejectedCount}");
                CheckClose($"{label} applied health delta", expectedDamage, LastDelta, note);

                Debug.Log($"{Tag} {label}: distance={Vector3.Distance(origin, hitPoint):F2} charge={charge:F2} " +
                          $"delta={LastDelta:F4} expected={expectedDamage:F4} accepted={accepted} " +
                          $"authority(accepted={_authority.AcceptedCount}, rejected={_authority.RejectedCount})");
            }

            /// <summary>
            /// D1b fixture migration. The warrior holds the sword and carries the shield, and since D1b the resolved ground speed
            /// includes the held-weapon penalty (sword 10 + shield 8 points on a 300 base, Docs/Tasks/D1b.md). Every number in this
            /// gate is the ATTRIBUTE rule's number (authored base x DEX factor x passives + flat, clamped), which is unchanged, so the
            /// warrior's resolved speeds are asserted as that rule number x this held factor. Since B1 the archer's bow carries a held penalty too (see ArcherHeld).
            /// </summary>
            /// <summary>B1: ranged weapons use the same held rule (1 - points / 300, bow 4 points); the archer holds the bow, so its resolved speeds are the attribute rule's number x this factor.</summary>
            private float ArcherHeld => 1f - _bow.heldMoveSpeedPenalty / 300f;

            private float WarriorHeld => 1f - (_sword.heldMoveSpeedPenalty + _shieldWeapon.heldMoveSpeedPenalty) / 300f;

            private void CheckWalk(string label, PlayerRig rig, float expected, float held = 1f)
            {
                float resolver = rig.Movement.ResolveGroundSpeed(false, false);

                CharacterStatStack stack = rig.Stats;
                float multiplier = stack != null ? stack.Multiplier(SkillStat.MoveSpeed) : 1f;
                float flat = stack != null ? stack.Flat(SkillStat.MoveSpeed) : 0f;
                float query = rig.AttributeStats.ResolveMoveSpeed(multiplier, flat);

                CheckClose($"{label} resolved walk speed", expected * held, resolver,
                    "clamp(authored * max(0.5, 1 + 0.025*DEX modifier) * stack multiplier + stack flat, 2, 8), x the held-weapon factor");
                CheckClose($"{label} walking query equals the movement resolver", resolver, query * held,
                    $"same source (multiplier {multiplier:F4}, flat {flat:F4}), DEX applied exactly once, x the held-weapon factor");
            }

            private GameObject BuildLevelWall(PlayerRig rig, float distance)
            {
                int levelLayer = LayerMask.NameToLayer(GameConfig.LevelLayerName);
                Check(levelLayer >= 0, $"the '{GameConfig.LevelLayerName}' layer exists for the line-of-sight wall");

                Vector3 mid = rig.AimOrigin + rig.Facing * (distance * 0.5f);
                GameObject wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
                wall.name = "U2A4_LOS_Wall";
                wall.transform.position = mid + Vector3.up * 1.5f;
                wall.transform.localScale = new Vector3(1.2f, 4f, 1.2f);
                if (levelLayer >= 0) wall.layer = levelLayer;
                Physics.SyncTransforms();
                return wall;
            }

            private void CarryItem(PlayerRig rig, ItemData item)
            {
                // R3 fixture: the charms/gems this gate builds are ad-hoc definitions, not production catalog
                // entries, so the bag mints against an explicit TEST catalog (production items + these fixtures).
                bool adopted = EmberdelveTestCatalog.Adopt(ref _testCatalog, rig.Inventory, item, out string adoptReason);
                Check(adopted, $"{item.itemId} registered in the test catalog{(adopted ? string.Empty : ": " + adoptReason)}");
                Check(rig.Inventory.TryAdd(item, 1), $"{item.itemId} placed in the bag");
            }

            private void Equip(PlayerRig rig, ItemData item)
            {
                bool ok = rig.Equipment.TryEquipItem(item, out string reason);
                Check(ok, $"equip {item.itemId}{(ok ? string.Empty : " refused: " + reason)}");
            }

            private void Unequip(PlayerRig rig, EquipmentSlot slot)
            {
                bool ok = rig.Equipment.TryUnequip(slot, out string reason);
                Check(ok, $"unequip {slot}{(ok ? string.Empty : " refused: " + reason)}");
            }

            /// <summary>
            /// An ephemeral definition the project authors no asset for: one attribute bonus and, optionally,
            /// the exact StatModifier set a movement-order test needs. Created as an instance, never saved,
            /// and destroyed with the pass.
            /// </summary>
            private ItemData MakeModifierItem(string id, string displayName, EquipmentSlot slot, int dexterityBonus,
                StatModifier[] modifiers, int strengthBonus = 0)
            {
                ItemData item = ScriptableObject.CreateInstance<ItemData>();
                _tempItems.Add(item);

                item.itemId = id;
                item.displayName = displayName;
                item.description = "Ephemeral U2A4 gate item: authored attribute bonuses and locomotion modifiers.";
                item.kind = ItemKind.Armor;
                item.equipSlot = slot;
                item.gridFootprint = new Vector2Int(1, 1);
                item.maxStack = 1;
                item.value = 1;
                item.statModifiers = modifiers;

                List<AttributeBonusEntry> bonuses = new List<AttributeBonusEntry>(2);
                if (dexterityBonus != 0) bonuses.Add(new AttributeBonusEntry(SixAttribute.Dexterity, dexterityBonus));
                if (strengthBonus != 0) bonuses.Add(new AttributeBonusEntry(SixAttribute.Strength, strengthBonus));
                item.attributeBonuses = bonuses.Count > 0 ? bonuses.ToArray() : null;

                return item;
            }

            private void Check(bool condition, string name)
            {
                _checks++;
                if (condition)
                {
                    Debug.Log($"{Tag} checked {name}");
                    return;
                }

                _failures++;
                Debug.LogError($"{Tag} mismatch {name}");
            }

            private void CheckInt(string name, int expected, int actual, string note)
            {
                Check(expected == actual, $"{name}: got {actual}, expected {expected} ({note})");
            }

            private void CheckClose(string name, float expected, float actual, string note)
            {
                Check(Mathf.Abs(expected - actual) <= Tolerance, $"{name}: got {actual:F4}, expected {expected:F4} ({note})");
            }

            /// <summary>The gate reads the saved data assets; it does not rebuild or save any of them.</summary>
            private static T LoadAsset<T>(string path, string label) where T : UnityEngine.Object
            {
                T asset = AssetDatabase.LoadAssetAtPath<T>(path);
                if (asset == null) throw new System.Exception($"{Tag} missing asset: {label} at {path}");
                return asset;
            }

            /// <summary>
            /// The same wiring EmberdelveLevelBuilder uses for a component it creates at runtime: assign the
            /// component's own serialized references. Used here only for the authority's GameConfig and the
            /// enemy's EnemyData/GameConfig, never to fake a value a test is asserting.
            /// </summary>
            private static void SetReference(Component target, string propertyName, UnityEngine.Object value)
            {
                if (target == null) return;

                SerializedObject serialized = new SerializedObject(target);
                SerializedProperty property = serialized.FindProperty(propertyName);
                if (property == null)
                {
                    Debug.LogWarning($"{Tag} cannot wire '{propertyName}' on {target.GetType().Name}");
                    return;
                }

                property.objectReferenceValue = value;
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }

            private TestTarget EnsureTarget()
            {
                if (_target != null) return _target;

                GameObject targetObject = new GameObject("U2A4_Target");
                _objects.Add(targetObject);
                targetObject.AddComponent<HealthComponent>();
                _target = targetObject.AddComponent<TestTarget>();
                _target.ConfigurePool(100000);
                return _target;
            }

            /// <summary>
            /// A monster-team ICombatEntity with no defense, no AI and no collider: the authority resolves it
            /// from its own registry, exactly as it resolves a monster, and nothing about it can absorb or
            /// redirect the damage this pass is asserting.
            /// </summary>
            private sealed class TestTarget : MonoBehaviour, ICombatEntity
            {
                private HealthComponent _health;
                private int _combatId;

                public EntityKind Kind => EntityKind.Enemy;
                public int CombatId => _combatId;
                public int TeamId => CombatTeams.MonsterTeam;
                public Vector3 CombatCenter => transform.position + Vector3.up * 1.1f;
                public Collider HeadCollider => null;

                public bool IsDead => _health != null && _health.IsDead;
                public float CurrentHealth => _health != null ? _health.CurrentHealth : 0f;
                public float MaxHealth => _health != null ? _health.MaxHealth : 0f;

                public void ApplyDamage(DamageInfo damage)
                {
                    if (_health != null) _health.ApplyDamage(damage);
                }

                public void ConfigurePool(int maxHealth)
                {
                    if (_health != null) _health.Configure(maxHealth, 0f, 0f);
                }

                private void Awake()
                {
                    _health = GetComponent<HealthComponent>();
                    _combatId = CombatRegistry.AllocateId();
                    CombatRegistry.Register(_combatId, this);
                }

                private void OnDestroy()
                {
                    // Unregister(int, ICombatEntity) as PlayerRig/EnemyAgent call it: own id + own instance.
                    CombatRegistry.Unregister(_combatId, this);
                }
            }
        }
    }
}
