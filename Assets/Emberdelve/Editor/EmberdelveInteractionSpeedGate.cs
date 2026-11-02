using System;
using System.Collections;
using System.Collections.Generic;
using Emberdelve.AI;
using Emberdelve.Combat;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Flow;
using Emberdelve.Interaction;
using Emberdelve.Items;
using Emberdelve.Player;
using Emberdelve.Stats;
using Emberdelve.UI;
using UnityEngine.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.SceneManagement;

namespace Emberdelve.EditorTools
{
    [InitializeOnLoad]
    public static class EmberdelveInteractionSpeedGate
    {
        private const string Pending = "Emberdelve.InteractionSpeed.Pending";
        static EmberdelveInteractionSpeedGate()
        {
            EditorApplication.playModeStateChanged += state =>
            {
                if (state != PlayModeStateChange.EnteredPlayMode || !SessionState.GetBool(Pending, false)) return;
                SessionState.SetBool(Pending, false);
                new GameObject("D4c1_InteractionSpeedGate").AddComponent<InteractionSpeedHarness>();
            };
        }
        public static void Run()
        {
            try
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Run from edit mode.");
                for (int i = 0; i < SceneManager.sceneCount; i++)
                    if (SceneManager.GetSceneAt(i).isDirty) throw new InvalidOperationException("Save open scenes before running the gate.");
                EmberdelveBootstrapMenu.BuildDataOnly();
                SessionState.SetBool(Pending, true);
                EditorSceneManager.OpenScene(EmberdelvePaths.DungeonScenePath);
                EditorApplication.EnterPlaymode();
            }
            catch (Exception e) { Debug.LogException(e); EmberdelveGateExit.Exit(1); }
        }
    }

    public sealed class InteractionSpeedHarness : MonoBehaviour
    {
        private int _checks, _failures, _serial;
        private readonly List<UnityEngine.Object> _cleanup = new List<UnityEngine.Object>();
        private Keyboard _keyboard, _previousKeyboard;
        private InputSettings _originalSettings, _temporarySettings;
        private float _originalMaximumDeltaTime;
        private bool _ownsMaximumDeltaTime;
        private PlayerRig _rig;
        private Interactor _interactor;
        private RunDirector _director;
        private GameConfig _config;
        private LootTable _table;
        private ItemData _coin, _ring;
        private void Check(bool ok, string label)
        {
            _checks++;
            if (ok) return;
            _failures++;
            Debug.LogError("[InteractionSpeed] mismatch " + label);
        }
        private void Close(float actual, float expected, string label, float tolerance = .00003f) =>
            Check(Mathf.Abs(actual - expected) <= tolerance, label + " actual=" + actual + " expected=" + expected);
        private static T Load<T>(string name) where T : UnityEngine.Object =>
            AssetDatabase.LoadAssetAtPath<T>(EmberdelvePaths.Data + "/" + name);
        private T Clone<T>(T value) where T : UnityEngine.Object
        {
            T copy = Instantiate(value); _cleanup.Add(copy); return copy;
        }
        private IEnumerator Start()
        {
            // Flatten nested routines here so a fixture exception still restores input and produces FAIL evidence.
            var stack = new Stack<IEnumerator>(); stack.Push(Flow());
            while (stack.Count > 0)
            {
                bool more = false; object value = null;
                try { more = stack.Peek().MoveNext(); if (more) value = stack.Peek().Current; }
                catch (Exception e) { _failures++; Debug.LogException(e); break; }
                if (!more) { (stack.Pop() as IDisposable)?.Dispose(); continue; }
                if (value is IEnumerator nested) stack.Push(nested); else yield return value;
            }
            while (stack.Count > 0) (stack.Pop() as IDisposable)?.Dispose();
            InputSettings expectedSettings = _originalSettings;
            float expectedMaximumDelta = _originalMaximumDeltaTime;
            bool ownedTiming = _ownsMaximumDeltaTime;
            Cleanup();
            Check(expectedSettings == null || ReferenceEquals(InputSystem.settings, expectedSettings), "exact original InputSettings restored");
            Check(!ownedTiming || Time.maximumDeltaTime == expectedMaximumDelta, "exact original maximumDeltaTime restored");
            Debug.Log("[Emberdelve][InteractionSpeed] " + (_failures == 0 ? "PASS" : "FAIL") +
                " checks=" + _checks + " failures=" + _failures);
            EmberdelveGateExit.Exit(_failures == 0 ? 0 : 1);
        }
        private IEnumerator Flow()
        {
            for (int i = 0; i < 10; i++) yield return null;
            _rig = FindFirstObjectByType<PlayerRig>(); _director = FindFirstObjectByType<RunDirector>();
            _config = Load<GameConfig>("EmberdelveGameConfig.asset");
            _coin = Load<ItemData>("Item_EmberCoin.asset"); _ring = Load<ItemData>("Item_CinderBandRing.asset");
            Check(_rig != null && _director != null && _config != null && _coin != null && _ring != null, "saved level has its real actor, admitted host and definitions");
            if (_rig == null || _director == null || _config == null || _coin == null || _ring == null) yield break;
            _interactor = _rig.GetComponent<Interactor>();
            Check(_interactor != null, "real ordinary Interactor exists"); if (_interactor == null) yield break;
            foreach (EnemyAgent enemy in FindObjectsByType<EnemyAgent>(FindObjectsSortMode.None)) enemy.enabled = false;
            _rig.Movement.ExternalDriven = true;
            // A deliberately brief key release must remain partial even on an unfocused slow Editor.
            // Bound only this fixture's largest game-time step; keep actual Update/input/progress logic.
            _originalMaximumDeltaTime = Time.maximumDeltaTime; _ownsMaximumDeltaTime = true;
            Time.maximumDeltaTime = Mathf.Min(_originalMaximumDeltaTime, .05f);
            CharacterController body = _rig.GetComponent<CharacterController>();
            if (body != null) body.enabled = false;
            _rig.transform.position = new Vector3(1000, 1, 1000);
            if (body != null) body.enabled = true;
            _rig.Look.ApplyAimPose(0f, 0f);
            _rig.Equipment.Clear(); _rig.Inventory.Clear(); _rig.Inventory.ConfigureGrid(10, 6);
            _table = ScriptableObject.CreateInstance<LootTable>(); _cleanup.Add(_table);
            _table.tableId = "d4c1_single_coin"; _table.maxRolls = 1; _table.nothingWeight = 0;
            _table.entries = new[] { new LootEntry { item = _coin, minAmount = 1, maxAmount = 1, weight = 1, maxCopies = 1 } };
            _originalSettings = InputSystem.settings; _temporarySettings = Instantiate(_originalSettings);
            _temporarySettings.hideFlags = HideFlags.HideAndDontSave;
            _temporarySettings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
            _temporarySettings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
            InputSystem.settings = _temporarySettings;
            _previousKeyboard = Keyboard.current; _keyboard = InputSystem.AddDevice<Keyboard>();
            InputSystem.EnableDevice(_keyboard); _keyboard.MakeCurrent();
            Check(_keyboard.enabled, "owned keyboard works without focus through transient settings");
            CharacterClassData warrior = Load<CharacterClassData>("Class_Warrior.asset"), archer = Load<CharacterClassData>("Class_Archer.asset");
            string wBefore = EditorJsonUtility.ToJson(warrior), aBefore = EditorJsonUtility.ToJson(archer);
            string profileBefore = EditorJsonUtility.ToJson(warrior.attributeCurveProfile), ringBefore = EditorJsonUtility.ToJson(_ring);
            CheckProfiles(warrior);
            yield return SealedFinalRefusal(archer);
            yield return ClassCase(warrior, 1f, 1.4f, 1.04f, 1.3461538462f, "1x", "1.04x");
            yield return ClassCase(archer, .95f, 1.4736842105f, 1f, 1.4f, "0.95x", "1x");
            yield return UntimedAndExtraction(warrior);
            Check(wBefore == EditorJsonUtility.ToJson(warrior) && aBefore == EditorJsonUtility.ToJson(archer) &&
                profileBefore == EditorJsonUtility.ToJson(warrior.attributeCurveProfile) && ringBefore == EditorJsonUtility.ToJson(_ring),
                "shared classes, profile and ring definitions unchanged");
        }
        private void CheckProfiles(CharacterClassData source)
        {
            PiecewiseLinearCurve missing = AttributeCurveProfile.CaptureInteraction(null);
            Check(ReferenceEquals(missing, InteractionCurveDefaults.Curve), "missing profile uses immutable interaction defaults");
            Close(missing.Evaluate(8f), .95f, "missing profile has independent CHA8 default");
            CharacterClassData owner = Clone(source); owner.attributeCurveProfile = null;
            var stats = new StatsComponent(); stats.Initialize(owner);
            Close(stats.InteractionSpeedFactor, 1f, "missing profile initializes actual CHA10 stats neutrally");
            AttributeCurveProfile profile = Clone(source.attributeCurveProfile); owner.attributeCurveProfile = profile;
            CurvePoint[][] malformed = {
                null, new CurvePoint[0], new[] { new CurvePoint(10, 1f) },
                new[] { new CurvePoint(1, .75f), new CurvePoint(1, 1f) },
                new[] { new CurvePoint(10, 1f), new CurvePoint(1, .75f) },
                new[] { new CurvePoint(1, 0f), new CurvePoint(30, 1f) },
                new[] { new CurvePoint(1, -.5f), new CurvePoint(30, 1f) },
                new[] { new CurvePoint(1, float.NaN), new CurvePoint(30, 1f) }
            };
            int warnings = 0;
            Application.LogCallback observe = (message, trace, type) =>
            {
                if (type == LogType.Warning && message.Contains("Invalid interaction profile") && message.Contains("Using D4c1 defaults")) warnings++;
            };
            Application.logMessageReceived += observe;
            try
            {
                foreach (CurvePoint[] points in malformed)
                {
                    profile.interaction = points;
                    Check(!profile.TryCaptureInteraction(out PiecewiseLinearCurve rejected, out string error) && rejected == null &&
                        !string.IsNullOrEmpty(error), "malformed interaction table refuses capture with a reason");
                    PiecewiseLinearCurve fallback = AttributeCurveProfile.CaptureInteraction(profile);
                    Check(ReferenceEquals(fallback, InteractionCurveDefaults.Curve), "malformed profile returns immutable defaults");
                    Close(fallback.Evaluate(8f), .95f, "malformed profile keeps literal CHA8 rate");
                }
            }
            finally { Application.logMessageReceived -= observe; }
            Check(warnings == malformed.Length, "each malformed interaction profile emits its diagnostic");
            profile.interaction = new[] { new CurvePoint(1, 1.25f), new CurvePoint(30, 1.25f) };
            stats.Initialize(owner); Close(stats.InteractionSpeedFactor, 1.25f, "valid custom interaction profile captured at initialization");
            profile.interaction[0].value = profile.interaction[1].value = 2f;
            stats.Rebuild(null); Close(stats.InteractionSpeedFactor, 1.25f, "definition rebuild keeps immutable interaction capture");
            stats.RebuildFromInstances(null); Close(stats.InteractionSpeedFactor, 1.25f, "instance rebuild keeps immutable interaction capture");
            stats.Initialize(owner); Close(stats.InteractionSpeedFactor, 2f, "explicit reinitialization captures new interaction points");
        }
        private void Apply(CharacterClassData source)
        {
            _rig.Equipment.Clear(); _rig.Inventory.Clear();
            CharacterClassData owner = Clone(source); owner.attributeCurveProfile = Clone(source.attributeCurveProfile);
            owner.manaRegenDelay = 10000f;
            _rig.ApplyClass(owner, _config); _rig.Movement.ExternalDriven = true;
            _rig.Health.Configure((int)_rig.MaxHealth, 0f, 10000f);
        }
        private ChestInteractable Chest(ChestVariant variant)
        {
            GameObject go = new GameObject("D4c1_Chest_" + ++_serial); _cleanup.Add(go);
            go.transform.position = _rig.transform.position + Vector3.forward * 1.5f;
            go.layer = LayerMask.NameToLayer(GameConfig.InteractLayerName);
            ChestInteractable chest = go.AddComponent<ChestInteractable>(); chest.Configure(variant, _table, 700 + _serial, _director);
            BoxCollider box = go.AddComponent<BoxCollider>(); box.center = Vector3.up; box.size = Vector3.one; box.isTrigger = true;
            Physics.SyncTransforms(); return chest;
        }
        private void InteractKey(bool held) => InputSystem.QueueStateEvent(_keyboard, held ? new KeyboardState(Key.E) : new KeyboardState());
        private IEnumerator Target(Component target)
        {
            InteractKey(false);
            for (int i = 0; i < 4; i++) yield return null;
            Check(ReferenceEquals(_interactor.CurrentTarget, target), "ordinary overlap/aim resolves the actual target");
        }
        private IEnumerator Progress(float value)
        {
            float until = Time.realtimeSinceStartup + 12f;
            while (_interactor.HoldProgress < value && Time.realtimeSinceStartup < until) yield return null;
            Check(_interactor.HoldProgress >= value, "real E advances hold to " + value);
        }
        private IEnumerator Finish(ChestInteractable chest)
        {
            float until = Time.realtimeSinceStartup + 12f;
            while (!chest.Opened && Time.realtimeSinceStartup < until) yield return null;
            InteractKey(false); yield return null; yield return null;
            Check(chest.Opened && chest.TakenCount == 1 && chest.RemainingUnits == 0, "completed ordinary hold pays its one deterministic unit");
            int taken = chest.TakenCount;
            Check(!chest.Open(_rig) && chest.TakenCount == taken, "empty reopen cannot duplicate payout or reroll");
            Close(_interactor.CapturedHoldDuration, 0f, "completion clears duration capture");
            chest.gameObject.SetActive(false); yield return null;
        }
        private IEnumerator ClassCase(CharacterClassData source, float bareRate, float bareDuration, float gearRate, float gearDuration, string bareText, string gearText)
        {
            Apply(source); yield return null;
            Close(_rig.InteractionSpeedFactor, bareRate, source.classId + " independent bare CHA rate");
            EffectiveStatsSnapshot old = _rig.EffectiveStats;
            CheckPane(bareText);
            ChestInteractable chest = Chest(ChestVariant.Ember); yield return Target(chest);
            Close(chest.HoldDuration, 1.4f, "authored Ember duration remains metadata");
            Close(chest.ResolveHoldDuration(_rig), bareDuration, "independent resolved Ember duration");
            InteractKey(true); yield return Progress(.25f);
            Close(_interactor.CapturedHoldDuration, bareDuration, "ordinary E captured bare duration");
            Check(!chest.Opened && chest.TakenCount == 0, "no early opening/payout at quarter progress");
            InteractKey(false); yield return null; yield return null;
            float partial = _interactor.HoldProgress;
            Check(partial > 0f && partial < .5f, "brief release retains partially decaying hold");
            string reason;
            Check(ItemInstance.TryCreateNew(_ring, 1, AffixRarity.Uncommon,
                new[] { new AffixRecord("affix_charisma", AffixStatKey.Charisma, 2) }, _rig.Inventory.Catalog.Authority,
                out ItemInstance ring, out reason), "actual accepted charisma+2 ring: " + reason);
            if (ring == null) yield break;
            string identity = ring.ToSnapshot().ToJson();
            Check(_rig.Inventory.TryAddInstance(ring, out reason), "carry exact ring UID: " + reason);
            int minted = _rig.Inventory.MintedInstanceCount;
            _rig.Health.SetHealth(_rig.MaxHealth - 10); EmberdelveKitFixtures.SpendMana(_rig.Mana, 10);
            float hp = _rig.CurrentHealth, mp = _rig.Mana.CurrentMana;
            Check(_rig.Equipment.TryEquipFromInventory(_rig.Inventory.IndexOfInstance(ring.Uid), out reason), "equip actual ring during partial hold: " + reason);
            Close(_rig.InteractionSpeedFactor, gearRate, "gear rebuild uses independently calculated CHA rate");
            Close(old.InteractionSpeedFactor, bareRate, "old effective capture remains immutable");
            CheckPane(gearText);
            Close(_interactor.CapturedHoldDuration, bareDuration, "partial release/regear keeps old duration");
            Close(_rig.CurrentHealth, hp, "regear does not heal"); Close(_rig.Mana.CurrentMana, mp, "regear does not refill mana");
            InteractKey(true); yield return Finish(chest);
            Check(_rig.Inventory.MintedInstanceCount == minted && identity == ring.ToSnapshot().ToJson(), "hold/transfer preserves ring UID/rolls without minting");
            ChestInteractable second = Chest(ChestVariant.Ember); yield return Target(second);
            InteractKey(true); yield return Progress(.2f);
            Close(_interactor.CapturedHoldDuration, gearDuration, "next reset attempt captures new rate");
            _rig.Health.ApplyDamage(new DamageInfo(1, _rig.CombatCenter, Vector3.forward, _rig.CombatCenter, 999,
                EntityKind.Enemy, DamageType.Physical, false, false, 0));
            Close(_interactor.HoldProgress, 0f, "damage immediately interrupts progress");
            Close(_interactor.CapturedHoldDuration, 0f, "damage immediately clears duration");
            InteractKey(false); yield return null; yield return null;
            InteractKey(true); yield return Progress(.2f);
            _interactor.enabled = false;
            Close(_interactor.HoldProgress, 0f, "disable interrupts progress");
            Close(_interactor.CapturedHoldDuration, 0f, "disable clears duration");
            InteractKey(false); _interactor.enabled = true; yield return Target(second);
            InteractKey(true); yield return Progress(.15f); InteractKey(false);
            float until = Time.realtimeSinceStartup + 4f;
            while (_interactor.HoldProgress > 0f && Time.realtimeSinceStartup < until) yield return null;
            Close(_interactor.HoldProgress, 0f, "release decays fully by original policy");
            Close(_interactor.CapturedHoldDuration, 0f, "full decay clears capture");
            InteractKey(true); yield return Progress(.2f);
            AttributeCurveProfile ownedProfile = _rig.Class.attributeCurveProfile;
            ownedProfile.interaction = new[] { new CurvePoint(1, 2f), new CurvePoint(30, 2f) };
            Close(_rig.InteractionSpeedFactor, gearRate, "mutating owned SO does not change initialized curve");
            Close(_interactor.CapturedHoldDuration, gearDuration, "profile mutation cannot retime active hold");
            _rig.RefreshEquipmentStats();
            Close(_rig.InteractionSpeedFactor, gearRate, "equipment refresh retains initialized curve");
            Check(_rig.Input.InteractHeld && _interactor.HoldProgress > 0f && _interactor.CapturedHoldDuration > 0f,
                "target-loss control starts held with a real positive capture");
            second.gameObject.SetActive(false); yield return null; yield return null;
            Check(_interactor.CurrentTarget == null, "removed target is actually lost");
            Close(_interactor.HoldProgress, 0f, "target loss clears progress while key stays held");
            Close(_interactor.CapturedHoldDuration, 0f, "target loss clears capture without release decay");
            InteractKey(false); yield return null;
            _rig.Equipment.TryUnequip(EquipmentSlot.RingLeft, out _);
            _rig.Equipment.TryUnequip(EquipmentSlot.RingRight, out _);
        }
        private void CheckPane(string literalText)
        {
            GameObject host = new GameObject("D4c1_CharacterPane", typeof(RectTransform)); _cleanup.Add(host);
            var pane = new CharacterStatsView(host.transform, 346f); pane.Configure(_rig); pane.Build();
            Check(pane.RowCount == 14 && pane.SectionLineCount == 4, "character pane has exactly fourteen rows and four sections");
            Close(CharacterStatsView.PreferredHeight, 336f, "new pane height is old318 plus one18px row");
            Transform root = host.transform.Find("CharacterRows");
            Transform labelObject = root != null ? root.Find("StatLabel13") : null;
            Transform valueObject = root != null ? root.Find("StatValue13") : null;
            Check(labelObject != null && valueObject != null, "last new character widgets exist");
            if (labelObject == null || valueObject == null) return;
            Text label = labelObject.GetComponent<Text>(), value = valueObject.GetComponent<Text>();
            Check(label.text == "Chest opening", "new final row names the actual interaction mechanic");
            string separator = System.Globalization.CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator;
            Check(value.text == literalText.Replace(".", separator), "character row shows independent class/gear literal " + literalText);
            int count = root.childCount;
            for (int i = 0; i < 8; i++) pane.Refresh();
            host.SetActive(false); host.SetActive(true); pane.Refresh();
            Check(pane.RowCount == 14 && root.childCount == count, "refresh/reopen do not duplicate character rows");
            // These are structural bounds only. Text-fit is verified on the real rendered inventory
            // by AgentScripts/ReviewD4c1, which rejects zero font metrics and a null graphics device.
            Close(label.rectTransform.rect.width, 168f, "new label keeps the existing column width");
            Close(value.rectTransform.rect.width, 178f, "new value keeps the existing346px pane remainder");
            Close(-value.rectTransform.anchoredPosition.y + value.rectTransform.rect.height, 336f,
                "last value ends at the declared pane height");
            host.SetActive(false);
        }
        private IEnumerator SealedFinalRefusal(CharacterClassData archer)
        {
            // RecoveredValue is cumulative earned value, not CarriedValue. Run this before any loot/ring
            // cases so its below-threshold precondition is genuine; clearing a bag cannot undo recovery.
            Apply(archer);
            Check(_coin.value == 12 && _rig.Inventory.RecoveredValue == 0,
                "fresh admitted original Archer has zero recovered value and actual12-value coins");
            ChestInteractable sealedChest = Chest(ChestVariant.Sealed); yield return Target(sealedChest);
            Close(sealedChest.HoldDuration, 2.2f, "sealed authored duration unchanged");
            Close(sealedChest.ResolveHoldDuration(_rig), 2.3157894737f, "CHA8 independent sealed duration");
            Check(!sealedChest.CanInteract(_rig, out string initialReason) && initialReason.Contains("recover 60"),
                "actual sealed value rule refuses the fresh actor below60");
            Check(!sealedChest.Open(_rig) && !sealedChest.Opened && sealedChest.TakenCount == 0,
                "below-threshold direct open has no payout");
            Check(_rig.Inventory.TryAdd(_coin, 5) && _rig.Inventory.RecoveredValue == 60,
                "five actual12-value coins establish recovered60 exactly");
            Check(sealedChest.CanInteract(_rig, out _), "sealed initially admits recovered60 value");
            InteractKey(true); yield return Progress(.25f);
            Close(_interactor.CapturedHoldDuration, 2.3157894737f, "sealed ordinary E captures scaled time");
            _rig.Inventory.Clear();
            Check(_rig.Inventory.TotalUnits == 0 && _rig.Inventory.CarriedValue == 0 && _rig.Inventory.RecoveredValue == 60,
                "empty bag preserves cumulative recovered value");
            Check(sealedChest.CanInteract(_rig, out _), "clearing carried loot does not revoke earned sealed admission");
            Check(_rig.Input.InteractHeld && ReferenceEquals(_interactor.CurrentTarget, sealedChest) && _interactor.HoldProgress > 0f,
                "dynamic final-rule control starts with the same target and a real held-E attempt");
            GameObject blocker = new GameObject("D4c1_SealedLosBlocker"); _cleanup.Add(blocker);
            blocker.layer = LayerMask.NameToLayer(GameConfig.LevelLayerName);
            blocker.transform.position = Vector3.Lerp(_rig.InteractionOrigin, sealedChest.transform.position + Vector3.up, .5f);
            BoxCollider wall = blocker.AddComponent<BoxCollider>(); wall.size = new Vector3(.8f, 1f, .14f);
            Physics.SyncTransforms();
            Check(!sealedChest.CanInteract(_rig, out string blockedReason) && blockedReason == "No line of sight",
                "real level geometry makes final LOS rule false during hold");
            int blockedCompletions = 0;
            Action<int, string> observe = (owner, message) =>
            {
                if (owner == _rig.PlayerId && message == "No line of sight") blockedCompletions++;
            };
            GameEvents.ToastRaised += observe;
            try
            {
                float until = Time.time + 2.5f;
                while (Time.time < until) yield return null;
            }
            finally { GameEvents.ToastRaised -= observe; }
            Check(blockedCompletions >= 1, "held-E attempt reaches completion and reports final LOS refusal");
            Check(!sealedChest.Opened && sealedChest.TakenCount == 0 && _rig.Inventory.TotalUnits == 0 &&
                _rig.Inventory.RecoveredValue == 60, "final admission recheck refuses stale valid prompt with no payout");
            InteractKey(false); blocker.SetActive(false); Physics.SyncTransforms(); yield return null; yield return null;
            Check(sealedChest.CanInteract(_rig, out _), "removing owned geometry restores real sealed eligibility");
            InteractKey(true); yield return Finish(sealedChest);
            Check(_rig.Inventory.RecoveredValue == 72, "subsequent valid ordinary hold pays exactly one12-value unit");
        }
        private IEnumerator UntimedAndExtraction(CharacterClassData warrior)
        {
            Apply(warrior);
            ChestInteractable supply = Chest(ChestVariant.Supply); yield return Target(supply);
            Close(supply.ResolveHoldDuration(_rig), 0f, "supply duration remains instant"); Check(!supply.RequiresHold, "supply has no hold");
            InteractKey(true); yield return null; yield return null; InteractKey(false); yield return null;
            Check(supply.Opened && supply.TakenCount == 1, "one actual E press opens supply");
            Close(_interactor.CapturedHoldDuration, 0f, "supply does not capture timed state");
            supply.gameObject.SetActive(false); yield return null;
            CharacterClassData highCha = Clone(warrior); highCha.charisma = 30;
            Apply(highCha);
            Close(_rig.InteractionSpeedFactor, 1.28f, "extraction control uses high CHA rather than neutral factor");
            GameObject go = new GameObject("D4c1_ExtractionProbe"); _cleanup.Add(go);
            go.layer = LayerMask.NameToLayer(GameConfig.InteractLayerName);
            go.transform.position = _rig.transform.position + Vector3.forward * 1.5f;
            ExtractionPoint extraction = go.AddComponent<ExtractionPoint>();
            var wiring = new SerializedObject(extraction); wiring.FindProperty("config").objectReferenceValue = _config;
            wiring.ApplyModifiedPropertiesWithoutUndo();
            BoxCollider box = go.AddComponent<BoxCollider>(); box.center = Vector3.up; box.isTrigger = true;
            Physics.SyncTransforms(); yield return Target(extraction);
            Check(!((object)extraction is IActorHoldDuration), "extraction deliberately does not opt into CHA timing");
            Close(extraction.HoldDuration, _config.extractionHoldDuration, "extraction authored duration unchanged");
            InteractKey(true); yield return Progress(.1f);
            float start = Time.time, before = _interactor.HoldProgress;
            yield return Progress(.25f);
            Close((_interactor.HoldProgress - before) / (Time.time - start), 1f / _config.extractionHoldDuration,
                "ordinary extraction progress retains raw authored-time slope", .005f);
            Close(_interactor.CapturedHoldDuration, 0f, "extraction uses existing uncaptured path");
            InteractKey(false); go.SetActive(false); yield return null; yield return null;
            ChestInteractable finalChest = Chest(ChestVariant.Ember); yield return Target(finalChest);
            InteractKey(true); yield return Progress(.2f);
            Check(_interactor.CapturedHoldDuration > 0f, "run-end control starts with a real active captured hold");
            GameEvents.RaisePlayerRunState(_rig.PlayerId, RunState.Extracted);
            Check(_interactor.CurrentTarget == null && _interactor.CapturedHoldDuration == 0f, "finished run clears interaction state");
        }
        private void Cleanup()
        {
            if (_ownsMaximumDeltaTime) Time.maximumDeltaTime = _originalMaximumDeltaTime;
            _ownsMaximumDeltaTime = false;
            if (_keyboard != null && _keyboard.added) InputSystem.RemoveDevice(_keyboard); _keyboard = null;
            if (_previousKeyboard != null && _previousKeyboard.added) _previousKeyboard.MakeCurrent(); _previousKeyboard = null;
            if (_originalSettings != null) InputSystem.settings = _originalSettings; _originalSettings = null;
            if (_temporarySettings != null) Destroy(_temporarySettings); _temporarySettings = null;
            foreach (UnityEngine.Object item in _cleanup) if (item != null) Destroy(item); _cleanup.Clear();
        }
        private void OnDestroy() => Cleanup();
    }
}
