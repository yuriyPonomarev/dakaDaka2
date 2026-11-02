using System;
using System.Collections;
using System.Collections.Generic;
using Emberdelve.Combat;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Items;
using Emberdelve.Player;
using Emberdelve.Stats;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.SceneManagement;

namespace Emberdelve.EditorTools
{
    [InitializeOnLoad]
    public static class EmberdelveSkillCooldownGate
    {
        private const string Pending = "Emberdelve.SkillCooldown.Pending";
        static EmberdelveSkillCooldownGate()
        {
            EditorApplication.playModeStateChanged += state =>
            {
                if (state != PlayModeStateChange.EnteredPlayMode || !SessionState.GetBool(Pending, false)) return;
                SessionState.SetBool(Pending, false);
                new GameObject("D4c2_SkillCooldownGate").AddComponent<SkillCooldownHarness>();
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
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                EditorApplication.EnterPlaymode();
            }
            catch (Exception e) { Debug.LogException(e); EmberdelveGateExit.Exit(1); }
        }
    }

    public sealed class SkillCooldownHarness : MonoBehaviour
    {
        private int _checks, _failures, _skillEffectErrors;
        private float _clock;
        private LocalCombatAuthority _authority;
        private GameConfig _config;
        private ItemData _ring;
        private readonly List<UnityEngine.Object> _cleanup = new List<UnityEngine.Object>();
        private readonly List<PlayerRig> _rigs = new List<PlayerRig>();
        private InputSettings _originalSettings, _temporarySettings;
        private Keyboard _keyboard, _previousKeyboard;
        private void Check(bool ok, string label)
        {
            _checks++; if (ok) return; _failures++; Debug.LogError("[SkillCooldown] mismatch " + label);
        }
        private void Close(float actual, float expected, string label, float tolerance = .0001f) =>
            Check(Mathf.Abs(actual - expected) <= tolerance, label + " actual=" + actual + " expected=" + expected);
        private static T Load<T>(string name) where T : UnityEngine.Object =>
            AssetDatabase.LoadAssetAtPath<T>(EmberdelvePaths.Data + "/" + name);
        private T Clone<T>(T source) where T : UnityEngine.Object
        {
            T copy = Instantiate(source); _cleanup.Add(copy); return copy;
        }
        private IEnumerator Start()
        {
            // Traverse nested iterators here so any fixture exception still restores input and emits FAIL.
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
            Cleanup();
            Debug.Log("[Emberdelve][SkillCooldown] " + (_failures == 0 ? "PASS" : "FAIL") +
                " checks=" + _checks + " failures=" + _failures);
            EmberdelveGateExit.Exit(_failures == 0 ? 0 : 1);
        }
        private void ObserveSkillEffectError(string message, string trace, LogType type)
        {
            if ((type == LogType.Error || type == LogType.Exception) &&
                message.StartsWith("[SkillCaster]", StringComparison.Ordinal)) _skillEffectErrors++;
        }
        private IEnumerator Flow()
        {
            _config = Load<GameConfig>("EmberdelveGameConfig.asset"); _ring = Load<ItemData>("Item_CinderBandRing.asset");
            CharacterClassData warrior = Load<CharacterClassData>("Class_Warrior.asset"), archer = Load<CharacterClassData>("Class_Archer.asset");
            Check(_config != null && _ring != null && warrior != null && archer != null, "actual saved fixtures exist");
            if (_config == null || _ring == null || warrior == null || archer == null) yield break;
            Application.logMessageReceived += ObserveSkillEffectError;
            string sharedBefore = Shared(warrior, archer);
            GameObject host = new GameObject("D4c2_Authority"); _cleanup.Add(host);
            _authority = host.AddComponent<LocalCombatAuthority>();
            var wiring = new SerializedObject(_authority); wiring.FindProperty("config").objectReferenceValue = _config;
            wiring.ApplyModifiedPropertiesWithoutUndo(); _authority.SetClock(() => _clock); _authority.AutoStepSwings = false;
            _originalSettings = InputSystem.settings; _temporarySettings = Instantiate(_originalSettings);
            _temporarySettings.hideFlags = HideFlags.HideAndDontSave;
            _temporarySettings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
            _temporarySettings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
            InputSystem.settings = _temporarySettings;
            _previousKeyboard = Keyboard.current; _keyboard = InputSystem.AddDevice<Keyboard>();
            InputSystem.EnableDevice(_keyboard); _keyboard.MakeCurrent();
            Check(_keyboard.enabled, "owned real InputSystem keyboard is enabled without focus");
            CheckProfiles(warrior);
            yield return FrozenClass(warrior, 1f, 14f, .96f, 13.44f, false);
            yield return FrozenClass(archer, 1.03f, 9.27f, 1f, 9f, true);
            // Actual fallback F/G bindings on both real kits, both bare and wearing the accepted ring.
            yield return KeyCase(warrior, 0, false, 7f, 24, 0);
            yield return KeyCase(warrior, 1, false, 14f, 32, 0);
            yield return KeyCase(archer, 0, false, 9.27f, 26, 3);
            yield return KeyCase(archer, 1, false, 11.33f, 22, 1);
            yield return KeyCase(warrior, 0, true, 6.72f, 24, 0);
            yield return KeyCase(warrior, 1, true, 13.44f, 32, 0);
            yield return KeyCase(archer, 0, true, 9f, 26, 3);
            yield return KeyCase(archer, 1, true, 11f, 22, 1);
            CheckRunningEpochReset(warrior);
            Check(sharedBefore == Shared(warrior, archer), "shared classes/profile/ring/skill/weapon data unchanged");
        }
        private void CheckProfiles(CharacterClassData source)
        {
            PiecewiseLinearCurve missing = AttributeCurveProfile.CaptureSkillCooldown(null);
            Check(ReferenceEquals(missing, SkillCooldownCurveDefaults.Curve), "missing profile uses immutable skill cooldown defaults");
            Close(missing.Evaluate(8f), 1.03f, "missing profile has independent CHA8 default");
            CharacterClassData owner = Clone(source); owner.attributeCurveProfile = null;
            var stats = new StatsComponent(); stats.Initialize(owner);
            Close(stats.SkillCooldownFactor, 1f, "missing profile initializes actual CHA10 stats neutrally");
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
                if (type == LogType.Warning && message.Contains("Invalid skill cooldown profile") && message.Contains("Using D4c2 defaults")) warnings++;
            };
            Application.logMessageReceived += observe;
            try
            {
                foreach (CurvePoint[] points in malformed)
                {
                    profile.skillCooldown = points;
                    Check(!profile.TryCaptureSkillCooldown(out PiecewiseLinearCurve rejected, out string error) && rejected == null &&
                        !string.IsNullOrEmpty(error), "malformed skill cooldown table refuses capture with a reason");
                    PiecewiseLinearCurve fallback = AttributeCurveProfile.CaptureSkillCooldown(profile);
                    Check(ReferenceEquals(fallback, SkillCooldownCurveDefaults.Curve), "malformed profile returns immutable defaults");
                    Close(fallback.Evaluate(8f), 1.03f, "malformed profile keeps literal CHA8 rate");
                }
            }
            finally { Application.logMessageReceived -= observe; }
            Check(warnings == malformed.Length, "each malformed skill cooldown profile emits its diagnostic");
            profile.skillCooldown = new[] { new CurvePoint(1, 1.25f), new CurvePoint(30, 1.25f) };
            stats.Initialize(owner); Close(stats.SkillCooldownFactor, 1.25f, "valid custom skill cooldown profile captured at initialization");
            profile.skillCooldown[0].value = profile.skillCooldown[1].value = 2f;
            stats.Rebuild(null); Close(stats.SkillCooldownFactor, 1.25f, "definition rebuild keeps immutable skill cooldown capture");
            stats.RebuildFromInstances(null); Close(stats.SkillCooldownFactor, 1.25f, "instance rebuild keeps immutable skill cooldown capture");
            stats.Initialize(owner); Close(stats.SkillCooldownFactor, 2f, "explicit reinitialization captures new skill cooldown points");
        }
        private string Shared(CharacterClassData a, CharacterClassData b)
        {
            string result = EditorJsonUtility.ToJson(a) + EditorJsonUtility.ToJson(b) + EditorJsonUtility.ToJson(_ring) +
                EditorJsonUtility.ToJson(a.attributeCurveProfile);
            foreach (CharacterClassData owner in new[] { a, b })
            {
                foreach (SkillData skill in owner.activeSkills) result += EditorJsonUtility.ToJson(skill);
                result += EditorJsonUtility.ToJson(owner.primaryWeapon);
            }
            return result;
        }
        private PlayerRig Rig(CharacterClassData source)
        {
            foreach (PlayerRig old in _rigs) old.SetInputEnabled(false);
            CharacterClassData owner = Clone(source); owner.attributeCurveProfile = Clone(source.attributeCurveProfile);
            owner.regenDelay = 10000f; owner.manaRegenDelay = 10000f; owner.startingReserveAmmo = owner.maxReserveAmmo = 20;
            GameObject body = new GameObject("D4c2_" + source.classId); _cleanup.Add(body);
            body.transform.position = new Vector3(_rigs.Count * 20f, 0, 0);
            PlayerRig rig = body.AddComponent<PlayerRig>(); rig.WireRig();
            body.AddComponent<Inventory>(); body.AddComponent<MeleeCombat>(); body.AddComponent<RangedCombat>();
            body.AddComponent<ClassLoadout>(); rig.WireRig();
            GameObject anchor = new GameObject("SkillCooldown_WeaponAnchor"); anchor.transform.SetParent(body.transform, false);
            var wiring = new SerializedObject(rig.Loadout); wiring.FindProperty("weaponAnchor").objectReferenceValue = anchor.transform;
            wiring.ApplyModifiedPropertiesWithoutUndo();
            rig.Inventory.Initialize(rig); rig.Equipment.Initialize(rig); rig.ApplyClass(owner, _config);
            rig.Movement.ExternalDriven = true; rig.Inventory.Clear(); rig.Inventory.ConfigureGrid(10, 6);
            Check(CombatSenders.Bind(CombatSenderId.ForPlayer(rig.PlayerId), rig.CombatId, out string reason), "bind actual owner: " + reason);
            rig.SetInputEnabled(false); _rigs.Add(rig); Physics.SyncTransforms(); return rig;
        }
        private void At(float time) { _clock = time; _authority.AdvanceKitsTo(time); }
        private float Remaining(PlayerRig rig, string skillId)
        {
            Check(_authority.TryGetSkillCooldown(rig.CombatId, skillId, out float remaining), "authority knows owned skill cooldown");
            return remaining;
        }
        private KitView View(PlayerRig rig)
        {
            Check(_authority.TryGetKit(rig.CombatId, out KitView view), "actual owner kit exists"); return view;
        }
        private ItemInstance EquipRing(PlayerRig rig)
        {
            Check(ItemInstance.TryCreateNew(_ring, 1, AffixRarity.Uncommon,
                new[] { new AffixRecord("affix_charisma", AffixStatKey.Charisma, 2) }, rig.Inventory.Catalog.Authority,
                out ItemInstance item, out string reason), "create accepted actual charisma+2 ring: " + reason);
            if (item == null) throw new InvalidOperationException("Ring fixture could not be created.");
            Check(rig.Inventory.TryAddInstance(item, out reason), "carry accepted ring UID: " + reason);
            Check(rig.Equipment.TryEquipFromInventory(rig.Inventory.IndexOfInstance(item.Uid), out reason), "equip exact ring UID: " + reason);
            return item;
        }
        private IEnumerator FrozenClass(CharacterClassData source, float bareRate, float bareDuration,
            float gearRate, float gearDuration, bool ranged)
        {
            At(_clock + 100f); PlayerRig rig = Rig(source); yield return null;
            int slot = ranged ? 0 : 1; // Saved Warrior G is BattleCry; Archer F is MultiShot.
            SkillData skill = rig.Skills.ActiveSkill(slot); Check(skill != null, "selected saved active skill exists");
            if (skill == null) yield break;
            Close(rig.SkillCooldownFactor, bareRate, "independent original class cooldown factor");
            Check(rig.MaxHealth > 40f, "fixture permits a real nonlethal wound");
            rig.Health.ApplyDamage(new DamageInfo(rig.MaxHealth - 40f, rig.CombatCenter, Vector3.forward,
                rig.CombatCenter + Vector3.forward, 999, EntityKind.Enemy, DamageType.Physical, false, false, 0f));
            Close(rig.CurrentHealth, 40f, "wounded pool precondition makes no-heal assertion discriminating");
            Check(!rig.Health.IsRegenerating, "actual damage suspends passive health regeneration");
            EffectiveStatsSnapshot old = rig.EffectiveStats;
            float start = _clock, beforeMana = rig.Mana.CurrentMana;
            Check(rig.Skills.TryCastSlot(slot), "ordinary SkillCaster accepts actual first skill");
            Close(Remaining(rig, skill.skillId), bareDuration, "first accepted cooldown literal");
            Close(rig.Skills.Snapshot(slot).CooldownSeconds, bareDuration, "HUD total is accepted duration");
            Close(rig.Mana.CurrentMana, beforeMana - skill.EffectiveManaCost, "cast spends authored mana once");
            if (ranged)
            {
                // Refill legitimately from existing reserve before testing the next accepted cast.
                Check(_authority.TryStartReload(rig.CombatSender, rig.CombatId, out _), "legitimate reload for the second volley");
            }
            At(start + 2f); yield return null;
            float hp = rig.CurrentHealth, mana = rig.Mana.CurrentMana; KitView pool = View(rig);
            ItemInstance ring = EquipRing(rig); string identity = ring.ToSnapshot().ToJson();
            Close(rig.SkillCooldownFactor, gearRate, "ring changes effective cooldown factor");
            Close(old.SkillCooldownFactor, bareRate, "old effective capture stays immutable");
            Close(Remaining(rig, skill.skillId), bareDuration - 2f, "regear cannot shorten old duration");
            Close(rig.Skills.Snapshot(slot).CooldownSeconds, bareDuration, "running HUD total does not retime on regear");
            Close(rig.CurrentHealth, hp, "ring does not heal"); Close(rig.Mana.CurrentMana, mana, "ring does not refill mana");
            KitView regearPool = View(rig);
            Check(pool.Magazine == regearPool.Magazine && pool.Reserve == regearPool.Reserve, "ring creates no ammo");
            CombatSenderId forged = CombatSenderId.ForPlayer(rig.PlayerId + 10000);
            Check(!_authority.TryBeginSkillCast(forged, rig.CombatId, skill.skillId, out _, out _), "forged sender cannot cast");
            Check(!_authority.TryBeginSkillCast(rig.CombatSender, rig.CombatId, "not_in_kit", out _, out _), "foreign skill refused");
            Close(rig.Mana.CurrentMana, mana, "refused casts spend no mana");
            At(start + bareDuration - .1f);
            // Existing authority tolerance is.05s; .1s early is independently outside that unchanged allowance.
            Check(!_authority.TryBeginSkillCast(rig.CombatSender, rig.CombatId, skill.skillId, out _, out _), "prior frozen interval refuses early repeat");
            Close(rig.Mana.CurrentMana, mana, "early repeat spends nothing");
            At(start + bareDuration); yield return null;
            Check(rig.Skills.TryCastSlot(slot), "exact accepted boundary permits next real cast");
            Close(Remaining(rig, skill.skillId), gearDuration, "new cast captures latest ring factor");
            Close(rig.Skills.Snapshot(slot).CooldownSeconds, gearDuration, "new HUD total matches new accepted capture");
            float secondStart = _clock;
            Check(rig.Equipment.TryUnequip(EquipmentSlot.RingLeft, out string why), "unequip same ring: " + why);
            Check(rig.Inventory.HoldsInstanceUid(ring.Uid) && identity == ring.ToSnapshot().ToJson(), "same ring UID and rolls return intact");
            Close(rig.SkillCooldownFactor, bareRate, "unequip restores original factor");
            Close(Remaining(rig, skill.skillId), gearDuration, "unequip cannot stretch accepted faster cooldown");
            rig.Class.attributeCurveProfile.skillCooldown = new[] { new CurvePoint(1, .4f), new CurvePoint(30, .4f) };
            rig.RefreshEquipmentStats();
            Close(rig.SkillCooldownFactor, bareRate, "owned profile mutation does not change initialization capture");
            Close(Remaining(rig, skill.skillId), gearDuration, "profile mutation cannot change old authority stamp");
            At(secondStart + gearDuration); yield return null;
            Close(Remaining(rig, skill.skillId), 0f, "faster captured duration reaches zero at its own boundary");
            // Explicit new class application is a new run/epoch, unlike regear, and may reset pools.
            rig.ApplyClass(rig.Class, _config); rig.Movement.ExternalDriven = true;
            Close(Remaining(rig, skill.skillId), 0f, "new kit epoch ignores the old stamp");
            Close(rig.SkillCooldownFactor, .4f, "new initialization captures edited owned profile");
            rig.ApplyDamage(new DamageInfo(99999, rig.CombatCenter, Vector3.forward, rig.CombatCenter,
                999, EntityKind.Enemy, DamageType.Physical, false, false, 0f));
            Check(rig.IsDead && !_authority.TryBeginSkillCast(rig.CombatSender, rig.CombatId, skill.skillId, out _, out _),
                "real death refuses further casts");
        }
        private void CheckRunningEpochReset(CharacterClassData source)
        {
            At(_clock + 100f); PlayerRig rig = Rig(source); SkillData cry = rig.Skills.ActiveSkill(1);
            Check(cry != null && cry.skillId == "warrior_battle_cry", "saved Warrior second active is Battle Cry");
            if (cry == null) return;
            Check(rig.Skills.TryCastSlot(1), "epoch fixture starts an actual Battle Cry");
            Close(Remaining(rig, cry.skillId), 14f, "epoch fixture has a live14s stamp before reset");
            int epoch = View(rig).Epoch;
            rig.ApplyClass(rig.Class, _config); rig.Movement.ExternalDriven = true;
            Check(View(rig).Epoch != epoch, "explicit new class application advances authority epoch");
            Close(Remaining(rig, cry.skillId), 0f, "new epoch retires a still-running old cooldown");
            Close(rig.Skills.CooldownRemaining(1), 0f, "new epoch clears local prediction too");
            Check(rig.Skills.TryCastSlot(1), "same-clock cast is legal only after explicit new epoch");
            Close(Remaining(rig, cry.skillId), 14f, "new epoch cast gets a fresh independent duration");
        }
        private IEnumerator KeyCase(CharacterClassData source, int slot, bool withRing, float duration, int manaCost, int ammoCost)
        {
            At(_clock + 100f); PlayerRig rig = Rig(source); if (withRing) EquipRing(rig);
            SkillData skill = rig.Skills.ActiveSkill(slot);
            Check(skill != null && skill.EffectiveManaCost == manaCost && skill.AmmoPerCast == ammoCost, "saved skill cost literals");
            if (skill == null) yield break;
            InputSystem.QueueStateEvent(_keyboard, new KeyboardState()); rig.SetInputEnabled(true);
            for (int i = 0; i < 4; i++) yield return null;
            int effectErrorsBefore = _skillEffectErrors;
            float mana = rig.Mana.CurrentMana; KitView before = View(rig);
            InputSystem.QueueStateEvent(_keyboard, new KeyboardState(slot == 0 ? Key.F : Key.G));
            yield return null; yield return null;
            InputSystem.QueueStateEvent(_keyboard, new KeyboardState()); yield return null; yield return null;
            Check(_skillEffectErrors == effectErrorsBefore, "actual F/G effect runs without SkillCaster effect-failure diagnostics");
            Close(Remaining(rig, skill.skillId), duration, "actual F/G input reaches accepted cooldown");
            Close(rig.Skills.CooldownRemaining(slot), duration, "actual input client remaining equals frozen authority duration");
            Close(rig.Skills.Snapshot(slot).CooldownSeconds, duration, "actual input HUD total uses literal capture");
            Close(rig.Mana.CurrentMana, mana - manaCost, "F/G spends exact authored mana once");
            KitView after = View(rig);
            Check(after.Magazine == before.Magazine - ammoCost && after.Reserve == before.Reserve,
                "F/G spends exact owned skill ammo once without refill");
            rig.SetInputEnabled(false);
        }
        private void Cleanup()
        {
            Application.logMessageReceived -= ObserveSkillEffectError;
            if (_keyboard != null && _keyboard.added) InputSystem.RemoveDevice(_keyboard); _keyboard = null;
            if (_previousKeyboard != null && _previousKeyboard.added) _previousKeyboard.MakeCurrent(); _previousKeyboard = null;
            if (_originalSettings != null)
            {
                InputSystem.settings = _originalSettings;
                Check(ReferenceEquals(InputSystem.settings, _originalSettings), "exact original InputSettings reference restored");
                _originalSettings = null;
            }
            if (_temporarySettings != null) Destroy(_temporarySettings); _temporarySettings = null;
            foreach (UnityEngine.Object item in _cleanup) if (item != null) Destroy(item); _cleanup.Clear();
        }
        private void OnDestroy() => Cleanup();
    }
}
