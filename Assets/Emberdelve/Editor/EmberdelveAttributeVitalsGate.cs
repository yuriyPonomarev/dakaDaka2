using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Emberdelve.Combat;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Items;
using Emberdelve.Movement;
using Emberdelve.Player;
using Emberdelve.Stats;

namespace Emberdelve.EditorTools
{
    /// <summary>
    /// U2A3 gate: the live attribute/vital slice of Docs/Design/ATTRIBUTES.md, verified through the real
    /// rig rather than a mirror of the formulas.
    ///
    /// U2A3-R2: the pass runs in real play mode, not in the editor. R1 built its rigs with AddComponent in
    /// edit mode, where PlayerMovement.Awake never runs, so its serialized controller stayed null and
    /// PlayerMovement.ApplyClass threw a NullReferenceException on controller.height before any assertion
    /// could be read. That was a fixture fault, not a gameplay fault. The gate now follows the lifecycle
    /// EmberdelveRuntimeSmokeTest already uses: an [InitializeOnLoad] static hook subscribes to
    /// playModeStateChanged once per editor session, Run() records the request in SessionState (which
    /// survives the play-mode domain reload), opens a transient unsaved empty scene, and asks for play
    /// mode. EnteredPlayMode starts the pass exactly once - the pending marker is cleared before the pass
    /// begins - and the rigs are built there with real AddComponent calls, so PlayerRig.Awake,
    /// PlayerMovement.Awake (+CharacterController), PlayerLook.Awake and HealthComponent.Awake all run the
    /// way a scene rig runs them. Nothing is saved, no scene or asset is regenerated, and no Awake is
    /// invoked by hand or through reflection.
    ///
    /// It builds a real PlayerRig (the same components the scene rig carries), applies a real class asset
    /// through the real ClassLoadout, then drives real Inventory/Equipment transfers and asserts on the
    /// real HealthComponent/ManaComponent/StaminaComponent pools:
    /// - both authored class baselines and their derived pools, filled once by class application;
    /// - damage/spend, then equip + unequip + re-equip leaving the CURRENT pools untouched except for the
    ///   clamp a shrinking ceiling forces;
    /// - CON+2 raising the health/stamina ceilings without healing, INT+2 raising the mana ceiling without
    ///   a refill;
    /// - the authored +1 gear bonuses producing one exact effective snapshot, with no stacking and the
    ///   class/item assets identical afterwards;
    /// - a dead pool that gear cannot revive;
    /// - the derived regeneration rates actually configured on the live pools;
    /// - health/mana notifications observed from inside the equipment notification carrying committed gear
    ///   numbers and the correct derived values.
    ///
    /// Every assertion helper matches the type the live API actually reports: HealthComponent reports its
    /// ceiling and every pool reports its current value as float, so those go through CheckClose with the
    /// gate's 0.011 tolerance and are never rounded or truncated to int; ManaComponent.MaxMana,
    /// StaminaComponent.MaxStamina, the attribute-derived ceilings and the authored class fields really are
    /// int, so those stay CheckInt. Only the single summary line is counted as PASS/FAIL; each individual
    /// assertion logs checked/mismatch.
    ///
    /// The whole assertion pass runs synchronously inside the harness's single Start callback, so no frame
    /// is rendered between assertions and no pool regenerates mid-assertion: the rates are asserted as the
    /// numbers the pools are actually configured with, and the per-frame tick stays with the play-mode
    /// smoke flow. Play mode is only asked for afterwards, from the harness's Update, and the exit code is
    /// carried back through SessionState so a pass that logged FAIL cannot leave the process with 0.
    ///
    /// Run from the command line, e.g.
    ///   Unity.exe -batchmode -nographics -projectPath . -logFile Logs/u2a3.log
    ///           -executeMethod Emberdelve.EditorTools.EmberdelveAttributeVitalsGate.Run
    /// Exit 0 only when every check passed.
    /// </summary>
    [InitializeOnLoad]
    public static class EmberdelveAttributeVitalsGate
    {
        private const string Tag = "[Emberdelve][AttributeVitals]";

        /// <summary>SessionState markers: they have to survive the play-mode domain reload, because the
        /// request is written in edit mode and consumed in play mode, and the exit code makes the trip
        /// back the other way.</summary>
        private const string RequestKey = "Emberdelve.AttributeVitals.Request";
        private const string PendingKey = "Emberdelve.AttributeVitals.Pending";
        private const string DoneKey = "Emberdelve.AttributeVitals.Done";
        private const string ExitCodeKey = "Emberdelve.AttributeVitals.ExitCode";

        private static double _watchdogStartedAt;

        static EmberdelveAttributeVitalsGate()
        {
            EditorApplication.playModeStateChanged += HandlePlayModeChange;
        }

        public static void Run()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Debug.LogError(Tag + " cannot request play mode from inside play mode");
                EditorApplication.Exit(1);
                return;
            }

            // Cleared before the run starts, so a marker left behind by an aborted earlier run can never
            // start a second pass, and so ExitingPlayMode can tell "this gate asked for play mode" from
            // somebody else entering play mode in the same editor session.
            SessionState.SetString(RequestKey, string.Empty);
            SessionState.SetBool(PendingKey, false);
            SessionState.SetBool(DoneKey, false);
            SessionState.SetInt(ExitCodeKey, 1);

            // A transient empty scene: nothing is saved, no level or lobby is rebuilt, and the rigs the
            // pass builds exist only inside this play-mode session.
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            Debug.Log(Tag + " requesting play mode in a transient empty scene");

            SessionState.SetString(RequestKey, "vitals");
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
                EditorApplication.Exit(1);
            }
        }

        private static void HandlePlayModeChange(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.EnteredPlayMode)
            {
                if (string.IsNullOrEmpty(SessionState.GetString(RequestKey, string.Empty))) return;
                if (!SessionState.GetBool(PendingKey, false)) return;

                // Cleared before the pass is created: EnteredPlayMode can only ever start it once.
                SessionState.SetBool(PendingKey, false);

                Debug.Log(Tag + " entered play mode, building the rigs through their real Awake");

                GameObject harnessObject = new GameObject("Emberdelve_AttributeVitalsHarness");
                harnessObject.AddComponent<Pass>();
            }
            else if (change == PlayModeStateChange.ExitingPlayMode)
            {
                if (string.IsNullOrEmpty(SessionState.GetString(RequestKey, string.Empty))) return;

                if (SessionState.GetBool(DoneKey, false))
                {
                    // Whatever the pass recorded is the code the process has to leave with. A pass that
                    // logged FAIL must not come out of the editor as 0.
                    int exitCode = SessionState.GetInt(ExitCodeKey, 1);
                    SessionState.SetBool(DoneKey, false);
                    SessionState.SetString(RequestKey, string.Empty);
                    Debug.Log(Tag + $" play mode finished, exiting with stored code {exitCode}");
                    EditorApplication.delayCall += () => EditorApplication.Exit(exitCode);
                }
                else
                {
                    // Play mode ended before the pass finished its assertions. An unfinished run is not a
                    // pass, so it leaves with a failure code instead of hanging the batch process.
                    Debug.LogError(Tag + " play mode ended before the pass finished its assertions - reporting failure");
                    SessionState.SetInt(ExitCodeKey, 1);
                    SessionState.SetString(RequestKey, string.Empty);
                    EditorApplication.delayCall += () => EditorApplication.Exit(1);
                }
            }
        }

        /// <summary>One synchronous verification pass, running inside real play mode.</summary>
        private sealed class Pass : MonoBehaviour
        {
            private int _checks;
            private int _failures;
            private float _startedAt;
            private bool _finished;
            private bool _stopping;

            private readonly List<GameObject> _rigs = new List<GameObject>();
            private readonly List<ItemData> _tempItems = new List<ItemData>();
            private ItemCatalog _testCatalog;

            private PlayerRig _notifyRig;
            private bool _notifyArmed;

            private int _healthNotifications;
            private int _manaNotifications;
            private int _equipmentNotifications;
            private float _notifyHealthCurrent;
            private float _notifyHealthMax;
            private float _notifyManaCurrent;
            private float _notifyManaMax;
            private float _notifyRigMaxHealth;
            private float _notifyRigDerivedHealth;
            private float _notifyRigDerivedMana;

            private void Start()
            {
                _startedAt = Time.realtimeSinceStartup;

                try
                {
                    CharacterClassData warrior = LoadAsset<CharacterClassData>(EmberdelvePaths.Data + "/Class_Warrior.asset", "Class_Warrior");
                    CharacterClassData archer = LoadAsset<CharacterClassData>(EmberdelvePaths.Data + "/Class_Archer.asset", "Class_Archer");
                    ItemData cuirass = LoadAsset<ItemData>(EmberdelvePaths.Data + "/Item_BoiledCuirass.asset", "Item_BoiledCuirass");
                    ItemData boots = LoadAsset<ItemData>(EmberdelvePaths.Data + "/Item_FootpadBoots.asset", "Item_FootpadBoots");
                    WeaponData bow = LoadAsset<WeaponData>(EmberdelvePaths.Data + "/Weapon_HollowBow.asset", "Weapon_HollowBow");
                    GameConfig config = LoadAsset<GameConfig>(EmberdelvePaths.Data + "/EmberdelveGameConfig.asset", "EmberdelveGameConfig");

                    CheckAuthoredBases(warrior, 100, 75, 3f, 90, 5f, "warrior authored bases");
                    CheckAuthoredBases(archer, 80, 90, 3.5f, 95, 5f, "archer authored bases");

                    RunWarrior(warrior, cuirass, boots, config);
                    RunArcher(archer, cuirass, bow, config);
                }
                catch (System.Exception exception)
                {
                    _failures++;
                    Debug.LogError(Tag + " gate threw: " + exception);
                }
                finally
                {
                    UnwireNotifications();
                    for (int i = 0; i < _rigs.Count; i++) if (_rigs[i] != null) Object.Destroy(_rigs[i]);
                    for (int i = 0; i < _tempItems.Count; i++) if (_tempItems[i] != null) Object.Destroy(_tempItems[i]);
                    _rigs.Clear();
                    _tempItems.Clear();
                    EmberdelveTestCatalog.Release(ref _testCatalog);
                }

                int exitCode = _failures == 0 ? 0 : 1;
                Debug.Log($"{Tag} {(_failures == 0 ? "PASS" : "FAIL")} checks={_checks} failures={_failures} exit={exitCode}");

                SessionState.SetInt(ExitCodeKey, exitCode);
                SessionState.SetBool(DoneKey, true);
                _finished = true;
            }

            private void Update()
            {
                if (_stopping) return;

                // The assertions themselves never yield, so play mode is only left once Start has
                // returned: changing play mode state from inside a MonoBehaviour callback is not allowed,
                // and this is the same frame-level callback the smoke harness already stops from.
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

            // ------------------------------------------------------------------ warrior

            private void RunWarrior(CharacterClassData warrior, ItemData cuirass, ItemData boots, GameConfig config)
            {
                PlayerRig rig = BuildRig("U2A3_Warrior");
                rig.ApplyClass(warrior, config);

                Debug.Log($"{Tag} warrior snapshot: {rig.AttributeStats.Describe()} bonuses={rig.AttributeStats.DescribeBonuses()}");

                // 1. Initial-full values, filled exactly once by class application.
                CheckClose("warrior max health 120 (100 + 10*2)", 120f, rig.Health.MaxHealth, "CON modifier 2, HealthComponent.MaxHealth is float");
                CheckClose("warrior health filled to 120", 120f, rig.Health.CurrentHealth, "a new run starts full");
                CheckInt("warrior max mana 70 (75 + 5*-1)", 70, rig.Mana.MaxMana, "INT modifier -1");
                CheckClose("warrior mana filled to 70", 70f, rig.Mana.CurrentMana, "a new run starts full");
                CheckInt("warrior max stamina 100 (90 + 5*2)", 100, rig.Stamina.MaxStamina, "CON modifier 2");
                CheckClose("warrior stamina filled to 100", 100f, rig.Stamina.CurrentStamina, "a new run starts full");

                // 2. Effective scores and modifiers, derived from the pristine class scores.
                CheckAttributes(rig.AttributeStats, new[] { 16, 10, 14, 8, 10, 10 }, "warrior effective scores (no gear)");
                CheckModifiers(rig.AttributeStats, new[] { 3, 0, 2, -1, 0, 0 }, "warrior modifiers (no gear)");

                // 3. Derived regeneration rates are the numbers the live pools are actually configured with.
                CheckClose("warrior health regen 1.5 (class authored)", 1.5f, rig.Health.RegenPerSecond, "no attribute rule for health regen yet");
                CheckClose("warrior mana regen 3.0 (3 + 0.25*0)", 3f, rig.Mana.RegenPerSecond, "WIS modifier 0");
                CheckClose("warrior stamina regen 6.0 (5 + 0.5*2)", 6f, rig.Stamina.RegenPerSecond, "CON modifier 2");
                EffectiveStatsSnapshot warriorSnapshot = rig.EffectiveStats;
                CheckClose("health regen is configured from the snapshot", warriorSnapshot.HealthRegenPerSecond, rig.Health.RegenPerSecond, "same normalized rate");
                CheckClose("health delay matches the snapshot", warriorSnapshot.HealthRegenDelay, ReadFloatField(rig.Health, "regenDelay"), "private pool configuration parity");
                CheckClose("mana regen is configured from the snapshot", warriorSnapshot.ManaRegenPerSecond, rig.Mana.RegenPerSecond, "same derived rate");
                CheckClose("mana delay matches the snapshot", warriorSnapshot.ManaRegenDelay, ReadFloatField(rig.Mana, "regenDelay"), "private pool configuration parity");

                // 4. Movement. U2A8 fixture migration only: the authored walking base is the migrated 4.3
                // (Docs/Design/ATTRIBUTES.md), the DEX term is max(0.5, 1 + 0.025 * modifier), and the warrior
                // authors DEX 10 -> floor((10-10)/2) = 0 -> factor 1. The class declares no MoveSpeed passive,
                // so walk = 4.3 * 1 * 1 + 0 = 4.3 and the query is the same term with no passive folded in.
                // Sprint keeps its authored 5.2 base with that same factor (5.2 * 1 = 5.2).
                CheckClose("warrior walk speed resolves the migrated authored 4.3", 4.3f * (1f - 18f / 300f), rig.Movement.ResolveGroundSpeed(false, false), "authored moveSpeed 4.3 x the D1b held factor (sword 10 + shield 8 points of 300), DEX modifier 0, no MoveSpeed passive");
                CheckClose("warrior sprint speed still authored 5.2", 5.2f * (1f - 18f / 300f), rig.Movement.ResolveGroundSpeed(true, false), "sprintSpeed base retained x the held factor, DEX modifier 0");
                CheckClose("warrior attribute move-speed query 4.3", 4.3f, rig.AttributeStats.MoveSpeed, "DEX modifier 0, query only");

                // 5. Damage and spend, so every later no-refill check has a pool below its ceiling.
                Damage(rig, 40f);
                CheckClose("warrior damaged to 80/120", 80f, rig.Health.CurrentHealth, "40 damage");
                Check(rig.Mana.TrySpend(30), "warrior spent 30 mana");
                CheckClose("warrior mana at 40/70", 40f, rig.Mana.CurrentMana, "30 spent");
                Check(rig.Stamina.TrySpend(25), "warrior spent 25 stamina");
                CheckClose("warrior stamina at 75/100", 75f, rig.Stamina.CurrentStamina, "25 spent");

                CarryItem(rig, cuirass);
                CarryItem(rig, boots);

                string pristineClass = EditorJsonUtility.ToJson(warrior);
                string pristineItem = EditorJsonUtility.ToJson(cuirass);

                // 6. Equip / unequip / re-equip: ceilings move, current pools do not.
                float healthClockBeforeRefresh = ReadFloatField(rig.Health, "_timeSinceDamage");
                float manaClockBeforeRefresh = ReadFloatField(rig.Mana, "_timeSinceSpend");
                Equip(rig, cuirass);
                CheckClose("warrior cuirass ceiling 150 (120 derived + 30 gear flat)", 150f, rig.Health.MaxHealth, "combined exactly once");
                CheckClose("warrior current health still 80 after equipping", 80f, rig.Health.CurrentHealth, "a bigger ceiling pays out nothing");
                CheckInt("warrior mana ceiling still 70 with cuirass", 70, rig.Mana.MaxMana, "CON +1 keeps the modifier at 2");
                CheckClose("warrior current mana still 40 after equipping", 40f, rig.Mana.CurrentMana, "no refill");
                EffectiveStatsSnapshot refreshedVitals = rig.EffectiveStats;
                CheckClose("post-refresh health delay still matches the snapshot", refreshedVitals.HealthRegenDelay, ReadFloatField(rig.Health, "regenDelay"), "equipment refresh does not change class delay");
                CheckClose("post-refresh mana delay still matches the snapshot", refreshedVitals.ManaRegenDelay, ReadFloatField(rig.Mana, "regenDelay"), "equipment refresh uses captured delay");
                CheckClose("health regen clock is not restarted by refresh", healthClockBeforeRefresh, ReadFloatField(rig.Health, "_timeSinceDamage"), "SetMax leaves the damage clock intact");
                CheckClose("mana regen clock is not restarted by refresh", manaClockBeforeRefresh, ReadFloatField(rig.Mana, "_timeSinceSpend"), "SetRegen leaves the spend clock intact");
                CheckInt("warrior stamina ceiling still 100 with cuirass", 100, rig.Stamina.MaxStamina, "CON +1 keeps the modifier at 2");
                CheckClose("warrior current stamina still 75 after equipping", 75f, rig.Stamina.CurrentStamina, "no refill");

                Unequip(rig, EquipmentSlot.Chest);
                CheckClose("warrior ceiling back to 120 without cuirass", 120f, rig.Health.MaxHealth, "gear contribution removed exactly");
                CheckClose("warrior current health still 80 after unequipping", 80f, rig.Health.CurrentHealth, "removing gear does not move the pool");

                Equip(rig, cuirass);
                CheckClose("warrior re-equip ceiling still 150 (no stacking)", 150f, rig.Health.MaxHealth, "wearing it twice is wearing it once");
                CheckClose("warrior current health still 80 after re-equipping", 80f, rig.Health.CurrentHealth, "re-equipping is not a heal");

                // 7. The authored +1 gear bonuses, as one exact effective snapshot.
                Equip(rig, boots);
                CheckAttributes(rig.AttributeStats, new[] { 16, 11, 15, 8, 10, 10 }, "warrior effective scores (cuirass CON+1, boots DEX+1)");
                Check(rig.AttributeStats.EquippedBonuses.Total(SixAttribute.Constitution) == 1, "equipped CON total is 1, not 2");
                Check(rig.AttributeStats.EquippedBonuses.Total(SixAttribute.Dexterity) == 1, "equipped DEX total is 1, not 2");
                CheckInt("warrior derived health still 120 with both worn", 120, rig.AttributeStats.MaxHealth, "CON 15 is still modifier 2");
                CheckClose("warrior pool health 150 with both worn", 150f, rig.Health.MaxHealth, "derived base plus the gear flat, once");

                Unequip(rig, EquipmentSlot.Legs);
                Unequip(rig, EquipmentSlot.Chest);
                CheckAttributes(rig.AttributeStats, new[] { 16, 10, 14, 8, 10, 10 }, "warrior effective scores after unequipping both");
                Check(rig.AttributeStats.EquippedBonuses.IsEmpty, "no attribute bonuses remain once nothing is worn");

                // 8. Three more cycles, and nothing accumulates anywhere.
                for (int cycle = 0; cycle < 3; cycle++)
                {
                    Equip(rig, cuirass);
                    Equip(rig, boots);
                    CheckClose($"warrior ceiling after cycle {cycle + 1} is still 150", 150f, rig.Health.MaxHealth, "no stacking across cycles");
                    CheckClose($"warrior current health after cycle {cycle + 1} is still 80", 80f, rig.Health.CurrentHealth, "no cycle heals");
                    Unequip(rig, EquipmentSlot.Legs);
                    Unequip(rig, EquipmentSlot.Chest);
                }

                CheckAttributes(rig.AttributeStats, new[] { 16, 10, 14, 8, 10, 10 }, "warrior effective scores after four cycles");
                Check(pristineClass == EditorJsonUtility.ToJson(warrior), "class asset untouched by any equip cycle");
                Check(pristineItem == EditorJsonUtility.ToJson(cuirass), "item asset untouched by any equip cycle");

                // 9. The explicit clamp-decrease: a pool above the new ceiling is pulled down to it, once.
                Equip(rig, cuirass);
                rig.Health.SetHealth(145f);
                CheckClose("warrior health set to 145/150 for the clamp test", 145f, rig.Health.CurrentHealth, "explicit SetHealth");
                Unequip(rig, EquipmentSlot.Chest);
                CheckClose("warrior clamp-decrease lands on 120", 120f, rig.Health.CurrentHealth, "145 was above the new 120 ceiling");
                CheckClose("warrior ceiling after the clamp 120", 120f, rig.Health.MaxHealth, "gear removed");

                // 10. CON +2 and INT +2 raise ceilings without touching the current pools. The notification
                // wiring is live for this whole stretch, so the same events a HUD sees are asserted here too.
                WireNotifications(rig);

                Damage(rig, 30f);
                CheckClose("warrior damaged to 90/120 for the CON test", 90f, rig.Health.CurrentHealth, "30 damage");
                Check(_healthNotifications == 1, $"one health notification for one committed change ({_healthNotifications})");
                CheckClose("that notification carried max 120", 120f, _notifyHealthMax, "the ceiling the pool had at the time");

                ItemData conGem = MakeBonusItem("u2a3_con_gem", "U2A3 CON+2", EquipmentSlot.Head, SixAttribute.Constitution, 2);
                CarryItem(rig, conGem);
                Equip(rig, conGem);
                CheckClose("CON+2 raises the health ceiling to 130 (100 + 10*3)", 130f, rig.Health.MaxHealth, "CON 16 is modifier 3");
                CheckClose("CON+2 did not heal: current health still 90", 90f, rig.Health.CurrentHealth, "no free healing");
                CheckInt("CON+2 raises the stamina ceiling to 105 (90 + 5*3)", 105, rig.Stamina.MaxStamina, "same CON modifier");
                CheckClose("CON+2 did not refill stamina: still 75", 75f, rig.Stamina.CurrentStamina, "no free refill");
                Check(_equipmentNotifications == 1, $"one equipment notification for the CON equip ({_equipmentNotifications})");
                CheckClose("from inside the notification the derived health was 130", 130f, _notifyRigDerivedHealth,
                    "observed from inside the equipment notification");
                CheckClose("from inside the notification the pool ceiling was already 130", 130f, _notifyRigMaxHealth,
                    "observed from inside the equipment notification");

                Unequip(rig, EquipmentSlot.Head);
                CheckClose("CON+2 removed: ceiling back to 120", 120f, rig.Health.MaxHealth, "recomputed from the pristine base");

                Check(rig.Mana.TrySpend(20), "warrior spent 20 mana for the INT test");
                CheckClose("warrior mana at 20/70 before the INT test", 20f, rig.Mana.CurrentMana, "40 - 20");

                ItemData intGem = MakeBonusItem("u2a3_int_gem", "U2A3 INT+2", EquipmentSlot.Legs, SixAttribute.Intelligence, 2);
                CarryItem(rig, intGem);
                _manaNotifications = 0;
                _equipmentNotifications = 0;
                Equip(rig, intGem);
                CheckInt("INT+2 raises the mana ceiling to 75 (75 + 5*0)", 75, rig.Mana.MaxMana, "INT 10 is modifier 0");
                CheckClose("INT+2 did not refill mana: still 20", 20f, rig.Mana.CurrentMana, "no free mana");
                Check(_manaNotifications == 1, $"one mana notification for the committed ceiling change ({_manaNotifications})");
                CheckClose("the mana notification carried the new ceiling", 75f, _notifyManaMax, "GameEvents.PlayerManaChanged carries (float current, float max)");
                CheckClose("the mana notification carried the unchanged current value", 20f, _notifyManaCurrent, "a ceiling change is not a refill");
                Unequip(rig, EquipmentSlot.Legs);
                CheckInt("INT+2 removed: mana ceiling back to 70", 70, rig.Mana.MaxMana, "recomputed from the pristine base");

                // 11. Gear notification carries committed gear numbers plus the attribute-derived values.
                _equipmentNotifications = 0;
                Equip(rig, cuirass);
                Check(_equipmentNotifications == 1, "one equipment notification for one committed equip");
                CheckClose("from inside the notification the pool ceiling was the worn number", 150f, _notifyRigMaxHealth,
                    "observed from inside the equipment notification");
                CheckClose("from inside the notification the attribute-derived health was 120", 120f, _notifyRigDerivedHealth,
                    "observed from inside the equipment notification");
                CheckClose("from inside the notification the attribute-derived mana was 70", 70f, _notifyRigDerivedMana,
                    "observed from inside the equipment notification");
                CheckClose("the health notification carried the committed ceiling", 150f, _notifyHealthMax,
                    "the ceiling the pool had when the event was raised");
                CheckClose("the health notification carried the unchanged current value", 90f, _notifyHealthCurrent, "no healing on equip");
                Check(_healthNotifications >= 1, "health change notification observed by the rig's own wiring");

                _equipmentNotifications = 0;
                Unequip(rig, EquipmentSlot.Chest);
                Check(_equipmentNotifications == 1, "one equipment notification for one committed unequip");
                CheckClose("from inside the unequip notification the ceiling was already the unworn number", 120f, _notifyRigMaxHealth,
                    "observed from inside the unequip notification");

                UnwireNotifications();

                // 12. Death is not a gear problem: no equip, no ceiling payout, no revive.
                Damage(rig, 9999f);
                Check(rig.IsDead, "warrior rig reports dead after lethal damage");
                CheckClose("warrior health at 0 when dead", 0f, rig.Health.CurrentHealth, "dead pool");

                float ceilingBeforeRefusal = rig.Health.MaxHealth;
                string refusedReason;
                bool equippedWhileDead = rig.Equipment.TryEquipItem(cuirass, out refusedReason);
                Check(!equippedWhileDead, $"dead player cannot equip ({refusedReason})");
                Check(rig.IsDead, "still dead after the refused equip");
                CheckClose("dead pool stays at 0", 0f, rig.Health.CurrentHealth, "gear cannot revive");
                CheckClose("ceiling unchanged by the refused equip", ceilingBeforeRefusal, rig.Health.MaxHealth, "a refusal touched nothing");

                rig.Equipment.Clear();
                Check(rig.IsDead, "clearing gear does not revive");
                CheckClose("clearing gear leaves the pool at 0", 0f, rig.Health.CurrentHealth, "clamp-only ceiling pass");

                Debug.Log($"{Tag} warrior end snapshot: {rig.AttributeStats.Describe()}");
            }

            // ------------------------------------------------------------------- archer

            private void RunArcher(CharacterClassData archer, ItemData cuirass, WeaponData bow, GameConfig config)
            {
                PlayerRig rig = BuildRig("U2A3_Archer");
                rig.ApplyClass(archer, config);

                Debug.Log($"{Tag} archer snapshot: {rig.AttributeStats.Describe()}");

                CheckClose("archer max health 90 (80 + 10*1)", 90f, rig.Health.MaxHealth, "CON modifier 1, HealthComponent.MaxHealth is float");
                CheckClose("archer health filled to 90", 90f, rig.Health.CurrentHealth, "a new run starts full");
                CheckInt("archer max mana 90 (90 + 5*0)", 90, rig.Mana.MaxMana, "INT modifier 0");
                CheckClose("archer mana filled to 90", 90f, rig.Mana.CurrentMana, "a new run starts full");
                CheckInt("archer max stamina 100 (95 + 5*1)", 100, rig.Stamina.MaxStamina, "CON modifier 1");
                CheckClose("archer stamina filled to 100", 100f, rig.Stamina.CurrentStamina, "a new run starts full");

                CheckAttributes(rig.AttributeStats, new[] { 10, 16, 12, 10, 14, 8 }, "archer effective scores (no gear)");
                CheckModifiers(rig.AttributeStats, new[] { 0, 3, 1, 0, 2, -1 }, "archer modifiers (no gear)");

                CheckClose("archer mana regen 4.0 (3.5 + 0.25*2)", 4f, rig.Mana.RegenPerSecond, "WIS modifier 2");
                CheckClose("archer stamina regen 5.5 (5 + 0.5*1)", 5.5f, rig.Stamina.RegenPerSecond, "CON modifier 1");
                CheckClose("archer health regen 2.0 (class authored)", 2f, rig.Health.RegenPerSecond, "no attribute rule for health regen yet");

                // U2A8 fixture migration of the same two numbers to the migrated authored 4.3. The query is
                // the bare attribute term 4.3 * (1 + 0.025 * 3) = 4.6225 (DEX 16 -> floor((16-10)/2) = 3), and
                // the resolver is that same term with the class' Fleet Footing 1.15 on top:
                // 4.6225 * 1.15 = 5.315875, i.e. DEX counted exactly once.
                CheckClose("archer walk speed 4.3 * 1.075 * 1.15 Fleet Footing", 5.315875f, rig.Movement.ResolveGroundSpeed(false, false), "authored 4.3, DEX modifier 3, Fleet Footing 1.15");
                CheckClose("archer attribute move-speed query 4.3 * 1.075", 4.6225f, rig.AttributeStats.MoveSpeed, "DEX modifier 3, query only, no passive folded in");
                CheckClose("archer ability effect scale 0.96 (1 + 0.04*-1)", 0.96f, rig.AttributeStats.GetAbilityEffectScale(), "CHA modifier -1, query only");
                CheckClose("archer bow damage query 34 * 1.12", 38.08f, rig.AttributeStats.GetWeaponDamage(bow), "DEX modifier 3, attacks unchanged");

                CarryItem(rig, cuirass);
                Equip(rig, cuirass);
                CheckClose("archer cuirass ceiling 120 (90 derived + 30 gear flat)", 120f, rig.Health.MaxHealth, "combined exactly once");
                CheckClose("archer current health still 90 after equipping", 90f, rig.Health.CurrentHealth, "a bigger ceiling pays out nothing");
                Unequip(rig, EquipmentSlot.Chest);

                Damage(rig, 30f);
                CheckClose("archer damaged to 60/90", 60f, rig.Health.CurrentHealth, "30 damage");
                Equip(rig, cuirass);
                CheckClose("archer current health still 60 after equipping", 60f, rig.Health.CurrentHealth, "no free healing");
                Unequip(rig, EquipmentSlot.Chest);
                CheckClose("archer current health still 60 after unequipping", 60f, rig.Health.CurrentHealth, "no clamp needed and no refill either");

                Check(rig.Mana.TrySpend(40), "archer spent 40 mana");
                CheckClose("archer mana at 50/90", 50f, rig.Mana.CurrentMana, "40 spent");
                Check(rig.Stamina.TrySpend(30), "archer spent 30 stamina");
                // Fixture correction, not a gameplay change: the archer's ceiling is asserted as 100 two
                // blocks above and 30 stamina is spent here, so the pool lands on 70. R1 carried 75f, which
                // contradicted its own label and was never reached because the edit-mode rig threw first.
                CheckClose("archer stamina at 70/100", 70f, rig.Stamina.CurrentStamina, "30 spent");

                Debug.Log($"{Tag} archer end snapshot: {rig.AttributeStats.Describe()}");
            }

            // ------------------------------------------------------------------ helpers

            /// <summary>
            /// A real rig, built the way the scene rig is wired, and built inside play mode so every
            /// component's real Awake runs: RequireComponent pulls in EmberdelveInput, PlayerMovement
            /// (+CharacterController), PlayerLook and HealthComponent, and WireRig performs the same Ensure
            /// pass Awake performs for mana, stamina, status, equipment, skills and the attribute component.
            /// Inventory and ClassLoadout are the two the rig does not create for itself. No Awake is
            /// called by hand and no private field is injected.
            /// </summary>
            private PlayerRig BuildRig(string name)
            {
                GameObject body = new GameObject(name);
                _rigs.Add(body);

                PlayerRig rig = body.AddComponent<PlayerRig>();
                rig.WireRig();

                body.AddComponent<Inventory>();
                body.AddComponent<ClassLoadout>();
                rig.WireRig();

                rig.Inventory.Initialize(rig);
                rig.Equipment.Initialize(rig);
                return rig;
            }

            private ItemData MakeBonusItem(string id, string displayName, EquipmentSlot slot,
                SixAttribute attribute, int amount)
            {
                ItemData item = ScriptableObject.CreateInstance<ItemData>();
                _tempItems.Add(item);

                item.itemId = id;
                item.displayName = displayName;
                item.description = "Ephemeral U2A3 gate item: one authored attribute bonus, no other modifiers.";
                item.kind = ItemKind.Armor;
                item.equipSlot = slot;
                item.gridFootprint = new Vector2Int(1, 1);
                item.maxStack = 1;
                item.value = 1;
                item.statModifiers = null;
                item.attributeBonuses = new[] { new AttributeBonusEntry(attribute, amount) };
                return item;
            }

            private void CarryItem(PlayerRig rig, ItemData item)
            {
                // R3 fixture: the charms/gems this gate builds are ad-hoc definitions, not production catalog
                // entries, so the bag mints against an explicit TEST catalog (production items + these fixtures).
                bool adopted = EmberdelveTestCatalog.Adopt(ref _testCatalog, rig.Inventory, item, out string adoptReason);
                Check(adopted, $"{item.itemId} registered in the test catalog{(adopted ? string.Empty : ": " + adoptReason)}");
                Check(rig.Inventory.TryAdd(item, 1), $"{item.itemId} placed in the bag");
            }

            private void Damage(PlayerRig rig, float amount)
            {
                rig.ApplyDamage(new DamageInfo(amount, rig.transform.position, Vector3.forward,
                    rig.transform.position + Vector3.forward, 0, EntityKind.Enemy, DamageType.Physical,
                    false, false, 0f));
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

            private void CheckAuthoredBases(CharacterClassData data, int maxHealth, int maxMana,
                float manaRegen, int maxStamina, float staminaRegen, string label)
            {
                Check(data != null, label + ": class asset present");
                if (data == null) return;

                CheckInt(label + " maxHealth", maxHealth, data.maxHealth, "authored base");
                CheckInt(label + " maxMana", maxMana, data.maxMana, "authored base");
                CheckClose(label + " manaRegenPerSecond", manaRegen, data.manaRegenPerSecond, "authored base");
                CheckInt(label + " maxStamina", maxStamina, data.maxStamina, "authored base");
                CheckClose(label + " staminaRegenPerSecond", staminaRegen, data.staminaRegenPerSecond, "authored prototype tuning");

                DerivedStatBases bases = data.AttributeBases;
                CheckInt(label + " bases handed to the rules: health", maxHealth, bases.MaxHealth, "pristine");
                CheckInt(label + " bases handed to the rules: mana", maxMana, bases.MaxMana, "pristine");
                CheckInt(label + " bases handed to the rules: stamina", maxStamina, bases.MaxStamina, "pristine");
                CheckClose(label + " bases handed to the rules: mana regen", manaRegen, bases.ManaRegen, "pristine");
                CheckClose(label + " bases handed to the rules: stamina regen", staminaRegen, bases.StaminaRegen, "pristine");
            }

            private void CheckAttributes(StatsComponent stats, int[] expected, string label)
            {
                for (int i = 0; i < AttributeRules.AttributeCount; i++)
                {
                    SixAttribute attribute = (SixAttribute)i;
                    int actual = stats.GetAttribute(attribute);
                    Check(actual == expected[i],
                        $"{label}: {AttributeRules.Key(attribute)} {actual} against {expected[i]} expected");
                }
            }

            private void CheckModifiers(StatsComponent stats, int[] expected, string label)
            {
                for (int i = 0; i < AttributeRules.AttributeCount; i++)
                {
                    SixAttribute attribute = (SixAttribute)i;
                    int actual = stats.GetModifier(attribute);
                    Check(actual == expected[i],
                        $"{label}: {AttributeRules.Key(attribute)} modifier {actual} against {expected[i]} expected");
                }
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
                Check(Mathf.Abs(expected - actual) <= 0.011f, $"{name}: got {actual:F3}, expected {expected:F3} ({note})");
            }

            private static float ReadFloatField(object target, string fieldName)
            {
                FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
                if (field == null) throw new System.MissingFieldException(target.GetType().Name, fieldName);
                return (float)field.GetValue(target);
            }

            /// <summary>The gate reads the saved data assets; it does not rebuild or save any of them.</summary>
            private static T LoadAsset<T>(string path, string label) where T : UnityEngine.Object
            {
                T asset = AssetDatabase.LoadAssetAtPath<T>(path);
                if (asset == null) throw new System.Exception($"{Tag} missing asset: {label} at {path}");
                return asset;
            }

            private void WireNotifications(PlayerRig rig)
            {
                UnwireNotifications();

                _notifyRig = rig;
                _notifyArmed = true;
                _healthNotifications = 0;
                _manaNotifications = 0;
                _equipmentNotifications = 0;
                GameEvents.PlayerHealthChanged += OnHealthEvent;
                GameEvents.PlayerManaChanged += OnManaEvent;
                GameEvents.EquipmentChanged += OnEquipmentEvent;
            }

            private void UnwireNotifications()
            {
                if (!_notifyArmed) return;
                _notifyArmed = false;
                GameEvents.PlayerHealthChanged -= OnHealthEvent;
                GameEvents.PlayerManaChanged -= OnManaEvent;
                GameEvents.EquipmentChanged -= OnEquipmentEvent;
                _notifyRig = null;
            }

            private void OnHealthEvent(int playerId, float current, float max)
            {
                _healthNotifications++;
                _notifyHealthCurrent = current;
                _notifyHealthMax = max;
            }

            private void OnManaEvent(int playerId, float current, float max)
            {
                _manaNotifications++;
                _notifyManaCurrent = current;
                _notifyManaMax = max;
            }

            private void OnEquipmentEvent(int playerId, ItemData item, bool equipped)
            {
                _equipmentNotifications++;
                if (_notifyRig == null) return;

                // Read the rig at the moment the notification arrives: an observer must never see a
                // half-committed transfer, so the pools and the derived values are already final here.
                _notifyRigMaxHealth = _notifyRig.MaxHealth;
                _notifyRigDerivedHealth = _notifyRig.AttributeStats != null ? _notifyRig.AttributeStats.MaxHealth : 0f;
                _notifyRigDerivedMana = _notifyRig.AttributeStats != null ? _notifyRig.AttributeStats.MaxMana : 0f;
            }
        }
    }
}
