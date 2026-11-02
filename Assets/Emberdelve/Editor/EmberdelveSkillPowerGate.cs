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
using UnityEngine.SceneManagement;

namespace Emberdelve.EditorTools
{
    [InitializeOnLoad]
    public static class EmberdelveSkillPowerGate
    {
        private const string Pending = "Emberdelve.SkillPower.Pending";
        static EmberdelveSkillPowerGate()
        {
            EditorApplication.playModeStateChanged += state =>
            {
                if (state != PlayModeStateChange.EnteredPlayMode || !SessionState.GetBool(Pending, false)) return;
                SessionState.SetBool(Pending, false);
                new GameObject("D4d1_SkillPowerGate").AddComponent<SkillPowerHarness>();
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

    public sealed class SkillPowerHarness : MonoBehaviour
    {
        private int _checks, _failures;
        private float _clock = 100f;
        private LocalCombatAuthority _authority;
        private GameConfig _config;
        private ItemData _ring;
        private readonly List<UnityEngine.Object> _cleanup = new List<UnityEngine.Object>();
        private void Check(bool ok, string label)
        {
            _checks++; if (ok) return; _failures++; Debug.LogError("[SkillPower] mismatch " + label);
        }
        private void Close(float actual, float expected, string label) =>
            Check(Mathf.Abs(actual - expected) <= .0001f, label + " actual=" + actual + " expected=" + expected);
        private static T Load<T>(string name) where T : UnityEngine.Object =>
            AssetDatabase.LoadAssetAtPath<T>(EmberdelvePaths.Data + "/" + name);
        private T Clone<T>(T source) where T : UnityEngine.Object
        {
            T copy = Instantiate(source); _cleanup.Add(copy); return copy;
        }
        private IEnumerator Start()
        {
            yield return null;
            try { Execute(); }
            catch (Exception e) { _failures++; Debug.LogException(e); }
            finally { foreach (UnityEngine.Object item in _cleanup) if (item != null) Destroy(item); _cleanup.Clear(); }
            Debug.Log("[Emberdelve][SkillPower] " + (_failures == 0 ? "PASS" : "FAIL") +
                " checks=" + _checks + " failures=" + _failures);
            EmberdelveGateExit.Exit(_failures == 0 ? 0 : 1);
        }
        private void Execute()
        {
            _config = Load<GameConfig>("EmberdelveGameConfig.asset"); _ring = Load<ItemData>("Item_CinderBandRing.asset");
            CharacterClassData warrior = Load<CharacterClassData>("Class_Warrior.asset"), archer = Load<CharacterClassData>("Class_Archer.asset");
            Check(_config != null && _ring != null && warrior != null && archer != null, "saved fixtures exist");
            if (_config == null || _ring == null || warrior == null || archer == null) return;
            string shared = EditorJsonUtility.ToJson(warrior) + EditorJsonUtility.ToJson(archer) +
                EditorJsonUtility.ToJson(_ring) + EditorJsonUtility.ToJson(warrior.attributeCurveProfile);
            GameObject host = new GameObject("D4d1_Authority"); _cleanup.Add(host);
            _authority = host.AddComponent<LocalCombatAuthority>();
            var wiring = new SerializedObject(_authority); wiring.FindProperty("config").objectReferenceValue = _config;
            wiring.ApplyModifiedPropertiesWithoutUndo(); _authority.SetClock(() => _clock); _authority.AutoStepSwings = false;
            CheckProfiles(warrior);
            CheckClass(warrior, 10, 1f, 13, 1.06f);
            CheckClass(archer, 14, 1.08f, 17, 1.14f);
            FrozenCry(warrior);
            Check(shared == EditorJsonUtility.ToJson(warrior) + EditorJsonUtility.ToJson(archer) +
                EditorJsonUtility.ToJson(_ring) + EditorJsonUtility.ToJson(warrior.attributeCurveProfile), "shared class/profile/ring unchanged");
        }
        private PlayerRig Rig(CharacterClassData source)
        {
            _clock += 100f; _authority.AdvanceKitsTo(_clock);
            CharacterClassData owner = Clone(source); owner.attributeCurveProfile = Clone(source.attributeCurveProfile);
            owner.regenDelay = owner.manaRegenDelay = 10000f;
            GameObject body = new GameObject("D4d1_" + source.classId); _cleanup.Add(body);
            body.transform.position = new Vector3(_cleanup.Count * 20f, 0f, 0f);
            PlayerRig rig = body.AddComponent<PlayerRig>(); rig.WireRig();
            body.AddComponent<Inventory>(); body.AddComponent<MeleeCombat>(); body.AddComponent<RangedCombat>();
            body.AddComponent<ClassLoadout>(); rig.WireRig();
            GameObject anchor = new GameObject("SkillPower_WeaponAnchor"); anchor.transform.SetParent(body.transform, false);
            var wiring = new SerializedObject(rig.Loadout); wiring.FindProperty("weaponAnchor").objectReferenceValue = anchor.transform;
            wiring.ApplyModifiedPropertiesWithoutUndo();
            rig.Inventory.Initialize(rig); rig.Equipment.Initialize(rig); rig.ApplyClass(owner, _config);
            rig.Movement.ExternalDriven = true; rig.SetInputEnabled(false); rig.Inventory.Clear(); rig.Inventory.ConfigureGrid(10, 6);
            Check(CombatSenders.Bind(CombatSenderId.ForPlayer(rig.PlayerId), rig.CombatId, out string reason), "bind owned rig: " + reason);
            Check(rig.MaxHealth > 40f, "actual pool permits nonlethal wound");
            rig.Health.ApplyDamage(new DamageInfo(rig.MaxHealth - 40f, rig.CombatCenter, Vector3.forward,
                rig.CombatCenter + Vector3.forward, 999, EntityKind.Enemy, DamageType.Physical, false, false, 0f));
            Close(rig.CurrentHealth, 40f, "actual wounded pool");
            Check(!rig.Health.IsRegenerating, "damage suspends passive health regen");
            return rig;
        }
        private ItemInstance Equip(PlayerRig rig, bool wisdom)
        {
            Check(ItemInstance.TryCreateNew(_ring, 1, AffixRarity.Uncommon,
                new[] { new AffixRecord(wisdom ? "affix_wisdom" : "affix_charisma",
                    wisdom ? AffixStatKey.Wisdom : AffixStatKey.Charisma, wisdom ? 3 : 2) },
                rig.Inventory.Catalog.Authority, out ItemInstance item, out string reason), "mint actual permitted ring: " + reason);
            if (item == null) throw new InvalidOperationException("Ring fixture missing.");
            Check(rig.Inventory.TryAddInstance(item, out reason), "carry ring UID: " + reason);
            Check(rig.Equipment.TryEquipFromInventory(rig.Inventory.IndexOfInstance(item.Uid), out reason), "equip ring UID: " + reason);
            return item;
        }
        private void CheckClass(CharacterClassData source, int baseWisdom, float baseline, int wornWisdom, float wornPower)
        {
            PlayerRig rig = Rig(source); EffectiveStatsSnapshot before = rig.EffectiveStats;
            Close(rig.AttributeStats.GetAttribute(SixAttribute.Wisdom), baseWisdom, "original class WIS preserved");
            Close(rig.AbilityEffectScale, baseline, "independent original-class WIS power");
            Check(EmberdelveKitFixtures.SpendMana(rig.Mana, 20), "nonfull mana control");
            float mana = rig.Mana.CurrentMana, damage = rig.GetOutgoingDamageMultiplier(rig.MainHandWeapon);
            float cooldown = rig.SkillCooldownFactor, opening = rig.InteractionSpeedFactor;
            ItemInstance ring = Equip(rig, true); string identity = ring.ToSnapshot().ToJson();
            Close(rig.AttributeStats.GetAttribute(SixAttribute.Wisdom), wornWisdom, "WIS+3 exact score");
            Close(rig.AbilityEffectScale, wornPower, "WIS+3 independent power");
            Close(before.AbilityEffectScale, baseline, "previous snapshot immutable");
            Close(rig.CurrentHealth, 40f, "WIS ring never heals"); Close(rig.Mana.CurrentMana, mana, "WIS ring never refills mana");
            Close(rig.GetOutgoingDamageMultiplier(rig.MainHandWeapon), damage, "ordinary weapon multiplier unchanged");
            Close(rig.SkillCooldownFactor, cooldown, "WIS does not alter CHA cooldown");
            Close(rig.InteractionSpeedFactor, opening, "WIS does not alter CHA chest opening");
            rig.RefreshEquipmentStats(); rig.RefreshEquipmentStats(); Close(rig.AbilityEffectScale, wornPower, "refresh does not accumulate power");
            Check(rig.Equipment.TryUnequip(EquipmentSlot.RingLeft, out string reason), "unequip WIS ring: " + reason);
            Check(rig.Inventory.HoldsInstanceUid(ring.Uid) && ring.ToSnapshot().ToJson() == identity, "exact WIS UID/rolls restored");
            Close(rig.AbilityEffectScale, baseline, "unequip restores baseline");
            Equip(rig, false); Close(rig.AbilityEffectScale, baseline, "CHA-only negative control leaves power unchanged");
            Check(rig.SkillCooldownFactor < cooldown && rig.InteractionSpeedFactor > opening, "CHA control still changes its two intended consumers");
            rig.Class.attributeCurveProfile.skillPower = new[] { new CurvePoint(1, 1.3f), new CurvePoint(30, 1.3f) };
            rig.RefreshEquipmentStats(); Close(rig.AbilityEffectScale, baseline, "owned profile edit cannot replace initialized capture");
            rig.ApplyClass(rig.Class, _config); rig.SetInputEnabled(false); rig.Movement.ExternalDriven = true;
            Close(rig.AbilityEffectScale, 1.3f, "explicit initialization captures new owned points");
        }
        private void FrozenCry(CharacterClassData source)
        {
            PlayerRig rig = Rig(source); SkillData cry = rig.Skills.ActiveSkill(1);
            Check(cry != null && cry.skillId == "warrior_battle_cry" && cry.selfStatus == StatusKind.Empowered,
                "saved Warrior G is actual empowering Battle Cry");
            if (cry == null) return;
            Close(cry.selfStatusDuration, 6f, "authored self duration"); Close(cry.selfStatusMagnitude, 1.25f, "authored self magnitude");
            string skillBefore = EditorJsonUtility.ToJson(cry);
            Check(_authority.TryBeginSkillCast(rig.CombatSender, rig.CombatId, cry.skillId, out int activation, out string reason), "begin actual owned activation: " + reason);
            Equip(rig, true); Close(rig.AbilityEffectScale, 1.06f, "regear after acceptance changes next cast power");
            Check(_authority.CompleteSkillCast(rig.CombatSender, rig.CombatId, activation), "complete original activation");
            Close(rig.Status.OutgoingDamageMultiplier, 1.25f, "original accepted power kept across pre-completion regear");
            Close(rig.Status.RemainingOf(StatusKind.Empowered), 6f, "original accepted duration kept across regear");
            Check(!_authority.CompleteSkillCast(rig.CombatSender, rig.CombatId, activation), "same activation cannot apply status twice");
            rig.ClearStatuses(); _clock += 14f; _authority.AdvanceKitsTo(_clock);
            Check(rig.Skills.TryCastSlot(1), "next actual SkillCaster cast uses new WIS power");
            Close(rig.Status.OutgoingDamageMultiplier, 1.265f, "delta magnitude scaled exactly once:1+.25*1.06");
            Close(rig.Status.RemainingOf(StatusKind.Empowered), 6.36f, "new duration scaled exactly once:6*1.06");
            Check(rig.Equipment.TryUnequip(EquipmentSlot.RingLeft, out reason), "remove WIS ring during active buff: " + reason);
            Close(rig.AbilityEffectScale, 1f, "next cast returns to base power");
            Close(rig.Status.OutgoingDamageMultiplier, 1.265f, "running buff magnitude stays frozen");
            Close(rig.Status.RemainingOf(StatusKind.Empowered), 6.36f, "running buff duration stays frozen");
            Close(rig.CurrentHealth, 40f, "casts and regear never heal");
            Close(rig.Mana.CurrentMana, rig.Mana.MaxMana - 64f, "two accepted cries spend authored32 mana each");
            Check(skillBefore == EditorJsonUtility.ToJson(cry), "shared skill unchanged");
        }
        private void CheckProfiles(CharacterClassData source)
        {
            Close(new StatsComponent().GetAbilityEffectScale(), 1f, "uninitialized owner neutral");
            PiecewiseLinearCurve missing = AttributeCurveProfile.CaptureSkillPower(null);
            Check(ReferenceEquals(missing, SkillPowerCurveDefaults.Curve), "missing profile uses immutable skill power defaults");
            Close(missing.Evaluate(8f), .95f, "missing profile has independent WIS8 default");
            CharacterClassData owner = Clone(source); owner.attributeCurveProfile = null;
            var stats = new StatsComponent(); stats.Initialize(owner);
            Close(stats.GetAbilityEffectScale(), 1f, "missing profile initializes actual WIS10 stats neutrally");
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
                if (type == LogType.Warning && message.Contains("Invalid skill power profile") && message.Contains("Using D4d1 defaults")) warnings++;
            };
            Application.logMessageReceived += observe;
            try
            {
                foreach (CurvePoint[] points in malformed)
                {
                    profile.skillPower = points;
                    Check(!profile.TryCaptureSkillPower(out PiecewiseLinearCurve rejected, out string error) && rejected == null &&
                        !string.IsNullOrEmpty(error), "malformed skill power table refuses capture with a reason");
                    PiecewiseLinearCurve fallback = AttributeCurveProfile.CaptureSkillPower(profile);
                    Check(ReferenceEquals(fallback, SkillPowerCurveDefaults.Curve), "malformed profile returns immutable defaults");
                    Close(fallback.Evaluate(8f), .95f, "malformed profile keeps literal WIS8 rate");
                }
            }
            finally { Application.logMessageReceived -= observe; }
            Check(warnings == malformed.Length, "each malformed skill power profile emits its diagnostic");
            profile.skillPower = new[] { new CurvePoint(1, 1.25f), new CurvePoint(30, 1.25f) };
            stats.Initialize(owner); Close(stats.GetAbilityEffectScale(), 1.25f, "valid custom skill power profile captured at initialization");
            profile.skillPower[0].value = profile.skillPower[1].value = 2f;
            stats.Rebuild(null); Close(stats.GetAbilityEffectScale(), 1.25f, "definition rebuild keeps immutable skill power capture");
            stats.RebuildFromInstances(null); Close(stats.GetAbilityEffectScale(), 1.25f, "instance rebuild keeps immutable skill power capture");
            stats.Initialize(owner); Close(stats.GetAbilityEffectScale(), 1.4f, "explicit reinitialization captures new skill power points");
        }
    }
}
