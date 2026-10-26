using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Emberdelve.Combat;
using Emberdelve.Data;
using Emberdelve.Items;
using Emberdelve.Skills;
using Emberdelve.Stats;
using UnityEditor;
using UnityEngine;

namespace Emberdelve.EditorTools
{
    /// <summary>Focused independent-value checks for the R10a immutable stat capture.</summary>
    public static class EmberdelveEffectiveStatsGate
    {
        private const string Data = "Assets/Emberdelve/Data/";

        [MenuItem("Emberdelve/Validation/Effective Stats Snapshot")]
        public static void Run()
        {
            int checks = 0;
            int failures = 0;
            void Check(bool condition, string label)
            {
                checks++;
                if (condition) return;
                failures++;
                Debug.LogError("[EffectiveStats] mismatch: " + label);
            }

            void Finish()
            {
                Debug.Log("[Emberdelve][EffectiveStats] " + (failures == 0 ? "PASS" : "FAIL")
                    + " checks=" + checks + " failures=" + failures);
                if (Application.isBatchMode) EditorApplication.Exit(failures == 0 ? 0 : 1);
            }

            try
            {
            CharacterClassData warrior = Load<CharacterClassData>("Class_Warrior.asset");
            CharacterClassData archer = Load<CharacterClassData>("Class_Archer.asset");
            WeaponData rapier = Load<WeaponData>("Weapon_EmberRapier.asset");
            WeaponData bow = Load<WeaponData>("Weapon_HollowBow.asset");
            Check(warrior != null && archer != null && rapier != null && bow != null,
                "authored class and weapon fixtures exist");
            if (warrior == null || archer == null || rapier == null || bow == null)
            {
                Finish();
                return;
            }

            StatsComponent warriorAttributes = new StatsComponent();
            warriorAttributes.Initialize(warrior);
            CharacterStatStack warriorStack = new CharacterStatStack();
            warriorStack.Build(warrior);
            StatsComponent archerAttributes = new StatsComponent();
            archerAttributes.Initialize(archer);
            CharacterStatStack archerStack = new CharacterStatStack();
            archerStack.Build(archer);

            StatusComponent status = new GameObject("EffectiveStatsGate_Status").AddComponent<StatusComponent>();
            try
            {
                EffectiveStatsSnapshot first = EffectiveStatsResolver.Capture(warrior, warriorAttributes,
                    warriorStack, null, status, DefenseProfile.Empty);
                EffectiveStatsSnapshot repeated = EffectiveStatsResolver.Capture(warrior, warriorAttributes,
                    warriorStack, null, status, DefenseProfile.Empty);
                // Literal expectations are calculated from authored scores, base pools and passive assets,
                // independently of AttributeRules/StatsComponent/CharacterStatStack query helpers.
                Check(first.IsReady && first.AttributesReady, "warrior capture readiness");
                Check(first.Attributes.Effective.Strength == 16 && first.Attributes.Effective.Constitution == 14
                    && first.MaxHealth == 120 && first.MaxMana == 70 && first.MaxStamina == 100
                    && Mathf.Approximately(first.ManaRegenPerSecond, 3f),
                    "warrior literals: STR16 CON14 => HP120, INT8 => mana70, stamina100");
                Check(Mathf.Approximately(first.GetOutgoingDamageMultiplier(rapier), 1.344f)
                    && Mathf.Approximately(first.GetOutgoingDamageMultiplier(bow), 1f),
                    "warrior literals: rapier finesse 1.12 x Ashen Might 1.2; bow DEX/passive neutral");
                Check(Mathf.Approximately(first.WalkSpeed, 4.3f)
                    && Mathf.Approximately(first.SprintSpeed, 5.2f)
                    && Mathf.Approximately(first.CrouchSpeed, 1.7f),
                    "warrior literal authored speeds with neutral passive terms");
                Check(first.MeleeFlatDamageBonus == 0f && first.ProjectileFlatDamageBonus == 0f
                    && first.ArmorRating == 0f && first.StatusSpeedMultiplier == 1f
                    && first.StatusOutgoingDamageMultiplier == 1f,
                    "empty equipment/status has neutral flat channels and armor");

                EffectiveStatsSnapshot archerSnapshot = EffectiveStatsResolver.Capture(archer, archerAttributes,
                    archerStack, null, null, DefenseProfile.Empty);
                Check(archerSnapshot.Attributes.Effective.Dexterity == 16
                    && archerSnapshot.Attributes.Effective.Wisdom == 14
                    && archerSnapshot.MaxHealth == 90 && archerSnapshot.MaxMana == 90
                    && Mathf.Approximately(archerSnapshot.ManaRegenPerSecond, 4f)
                    && archerSnapshot.MaxStamina == 100,
                    "archer literals: DEX16 WIS14 CON12 => HP90, mana90, regen4, stamina100");
                Check(Mathf.Approximately(archerSnapshot.GetOutgoingDamageMultiplier(bow), 1.3216f)
                    && Mathf.Approximately(archerSnapshot.GetOutgoingDamageMultiplier(rapier), 1.12f)
                    && Mathf.Approximately(archerSnapshot.WalkSpeed, 5.315875f)
                    && Mathf.Approximately(archerSnapshot.SprintSpeed, 7.912f),
                    "archer literals: bow 1.12 x Farshot 1.18, finesse 1.12, Fleet Footing speeds");

                Check(first.MaxHealth == repeated.MaxHealth && first.MaxMana == repeated.MaxMana
                    && first.ManaRegenPerSecond == repeated.ManaRegenPerSecond
                    && first.WalkSpeed == repeated.WalkSpeed
                    && first.GetOutgoingDamageMultiplier(rapier) == repeated.GetOutgoingDamageMultiplier(rapier),
                    "repeated captures do not accumulate or alter source values");

                ItemCatalog catalog = Load<ItemCatalog>("EmberdelveItemCatalog.asset");
                ItemData blade = Load<ItemData>("Item_EmberFangBlade.asset");
                ItemData cloak = Load<ItemData>("Item_AshenCloak.asset");
                ItemInstanceAuthority authority = null;
                string authorityFailure = null;
                bool authorityReady = catalog != null && catalog.TryGetAuthority(out authority, out authorityFailure);
                if (catalog == null || blade == null || cloak == null || !authorityReady)
                {
                    Check(false, "rolled item fixtures and trusted authority exist: "
                        + (catalog == null ? "catalog missing" : authorityFailure));
                    Finish();
                    return;
                }

                bool weaponCreated = ItemInstance.TryCreateWithUid(Guid.NewGuid(), blade, 1, AffixRarity.Rare,
                    new[]
                    {
                        new AffixRecord("affix_melee_damage", AffixStatKey.MeleeDamage, 2f),
                        new AffixRecord("affix_projectile_damage", AffixStatKey.ProjectileDamage, 3f),
                    }, authority, out ItemInstance rolledBlade, out string weaponReason);
                bool cloakCreated = ItemInstance.TryCreateWithUid(Guid.NewGuid(), cloak, 1, AffixRarity.Uncommon,
                    new[] { new AffixRecord("affix_armor", AffixStatKey.Armor, 2f) },
                    authority, out ItemInstance rolledCloak, out string cloakReason);
                Check(weaponCreated && cloakCreated, "authored rolled item fixtures validate: "
                    + (weaponCreated ? cloakReason : weaponReason));
                if (!weaponCreated || !cloakCreated)
                {
                    Finish();
                    return;
                }

                // Ashen Cloak contributes its authored +2 armor; its accepted armor roll adds +2.
                // The runtime stack is the already-resolved equipment/passive authority input to Capture.
                warriorStack.SetSource(CharacterStatStack.EquipmentSourceKey,
                    new[] { new StatModifier(SkillStat.Armor, SkillModifierMode.Flat, 4f) }, null);
                var worn = new List<ItemInstance> { rolledBlade, rolledCloak };
                EffectiveStatsSnapshot equipped = EffectiveStatsResolver.Capture(warrior, warriorAttributes,
                    warriorStack, worn, status, DefenseProfile.Empty);
                Check(equipped.MeleeFlatDamageBonus == 2f && equipped.ProjectileFlatDamageBonus == 3f
                    && equipped.ArmorRating == 4f,
                    "rolled rare blade damage channels and authored-plus-rolled cloak armor stay separate");
                status.ApplyStatus(new StatusSpec(StatusKind.Empowered, 10f, 1.25f), 0);
                status.ApplyStatus(new StatusSpec(StatusKind.Slow, 10f, 0.6f), 0);
                EffectiveStatsSnapshot buffed = EffectiveStatsResolver.Capture(warrior, warriorAttributes,
                    warriorStack, worn, status, DefenseProfile.Empty);
                Check(Mathf.Approximately(buffed.StatusOutgoingDamageMultiplier, 1.25f)
                    && Mathf.Approximately(buffed.StatusSpeedMultiplier, 0.6f)
                    && Mathf.Approximately(buffed.GetOutgoingDamageMultiplier(rapier), 1.68f),
                    "active status multipliers captured once over the independent warrior rapier factor");
                Check(equipped.ArmorRating == 4f && equipped.MeleeFlatDamageBonus == 2f
                    && equipped.ProjectileFlatDamageBonus == 3f,
                    "existing snapshot remains immutable after source/status changes");
                warriorStack.SetSource(CharacterStatStack.EquipmentSourceKey,
                    new[] { new StatModifier(SkillStat.Armor, SkillModifierMode.Flat, 9f) }, null);
                EffectiveStatsSnapshot changed = EffectiveStatsResolver.Capture(warrior, warriorAttributes,
                    warriorStack, worn, status, DefenseProfile.Empty);
                Check(equipped.ArmorRating == 4f && changed.ArmorRating == 9f
                    && changed.MeleeFlatDamageBonus == 2f && changed.ProjectileFlatDamageBonus == 3f,
                    "source changes affect new capture only; repeated flat-affix scans do not accumulate");
                Check(Mathf.Approximately(buffed.GetOutgoingDamageMultiplier(null), 1.25f),
                    "null-weapon damage query preserves PlayerRig status-only multiplier semantics");

                EffectiveStatsSnapshot noAttributeOwner = EffectiveStatsResolver.Capture(warrior, null,
                    warriorStack, null, null, DefenseProfile.Empty);
                Check(noAttributeOwner.IsReady && !noAttributeOwner.AttributesReady
                    && noAttributeOwner.AbilityEffectScale == 1f
                    && Mathf.Approximately(noAttributeOwner.GetOutgoingDamageMultiplier(rapier), 1.2f),
                    "missing attribute owner keeps neutral weapon factor and warrior passive multiplier");
                EffectiveStatsSnapshot missing = EffectiveStatsResolver.Capture(null, warriorAttributes,
                    warriorStack, null, status, DefenseProfile.Empty);
                Check(!missing.IsReady && missing.GetOutgoingDamageMultiplier(rapier) == 0f,
                    "missing class is rejected as an unready neutral snapshot");

                CheckRigCache(Check, warrior, archer, rapier);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(status.gameObject);
            }

            Finish();
            }
            catch (Exception exception)
            {
                failures++;
                Debug.LogException(exception);
                Finish();
            }
        }

        private static void CheckRigCache(Action<bool, string> check, CharacterClassData warrior,
            CharacterClassData archer, WeaponData rapier)
        {
            GameObject body = new GameObject("EffectiveStatsGate_RigCache");
            WeaponData noParryMainHand = ScriptableObject.CreateInstance<WeaponData>();
            WeaponData parryOffhand = ScriptableObject.CreateInstance<WeaponData>();
            try
            {
                Emberdelve.Player.PlayerRig rig = body.AddComponent<Emberdelve.Player.PlayerRig>();
                rig.WireRig();
                InvokeAwake(rig.Movement);
                check(Mathf.Approximately(rig.AbilityEffectScale, 1f),
                    "unready rig ability scale uses the neutral 1 fallback");
                StatsComponent attributes = new StatsComponent();
                attributes.Initialize(warrior);
                SetField(rig, "_class", warrior);
                SetField(rig, "_attributeStats", attributes);
                rig.Stats.Build(warrior);
                noParryMainHand.defenseRole = DefenseRole.Weapon;
                noParryMainHand.capabilities = DefenseCapability.None;
                noParryMainHand.twoHanded = false;
                rig.ApplyMainHandWeapon(noParryMainHand);
                rig.Movement.ApplyClass(warrior, 0);
                rig.Movement.ApplyStatStack(rig.Stats);
                rig.Movement.ApplyAttributeStats(attributes);
                rig.Movement.ApplyEffectiveStatsSource(() => rig.EffectiveStats);

                EffectiveStatsSnapshot initial = rig.EffectiveStats;
                check(initial.IsReady && Mathf.Approximately(rig.Movement.ResolveWalkSpeed(), initial.WalkSpeed),
                    "rig cache feeds the same captured walking speed to movement");

                float warriorDamage = rig.GetOutgoingDamageMultiplier(rapier);
                rig.Stats.Build(archer);
                float archerPassiveDamage = rig.GetOutgoingDamageMultiplier(rapier);
                check(Mathf.Approximately(warriorDamage, 1.344f)
                    && Mathf.Approximately(archerPassiveDamage, 1.12f),
                    "direct passive stack rebuild invalidates the rig capture");

                rig.Stats.SetSource(CharacterStatStack.EquipmentSourceKey,
                    new[] { new StatModifier(SkillStat.MaxHealth, SkillModifierMode.Flat, 7f) }, null);
                EffectiveStatsSnapshot afterStackMutation = rig.EffectiveStats;
                check(afterStackMutation.MaxHealth == rig.AttributeStats.MaxHealth + 7,
                    "direct CharacterStatStack source mutation invalidates rig capture");

                float abilityScaleBeforeAttributeMutation = rig.EffectiveStats.AbilityEffectScale;
                attributes.Initialize(archer);
                EffectiveStatsSnapshot afterAttributeMutation = rig.EffectiveStats;
                check(afterAttributeMutation.Attributes.Effective.Dexterity == 16
                    && afterAttributeMutation.Attributes.Effective.Strength == 10,
                    "direct StatsComponent.Initialize invalidates rig capture");
                check(Mathf.Approximately(afterAttributeMutation.AbilityEffectScale, 0.96f)
                    && Mathf.Approximately(rig.AbilityEffectScale, afterAttributeMutation.AbilityEffectScale)
                    && !Mathf.Approximately(abilityScaleBeforeAttributeMutation, afterAttributeMutation.AbilityEffectScale),
                    "CHA mutation invalidates the old capture and updates the public snapshot-backed ability scale");

                float speedBeforeSlow = rig.Movement.ResolveGroundSpeed(false, false);
                int statusVersionBeforeSlow = rig.Status.Version;
                rig.Status.ApplyStatus(new StatusSpec(StatusKind.Slow, 10f, 0.5f), 0);
                EffectiveStatsSnapshot afterSlow = rig.EffectiveStats;
                check(rig.Status.Version > statusVersionBeforeSlow
                    && Mathf.Approximately(afterSlow.StatusSpeedMultiplier, 0.5f)
                    && Mathf.Approximately(rig.Movement.ResolveGroundSpeed(false, false), speedBeforeSlow * 0.5f),
                    "status version refreshes the snapshot movement factor");
                ExpireStatuses(rig.Status);
                check(Mathf.Approximately(rig.EffectiveStats.StatusSpeedMultiplier, 1f)
                    && Mathf.Approximately(rig.Movement.ResolveGroundSpeed(false, false), speedBeforeSlow),
                    "status expiry restores the snapshot-backed movement speed");

                rig.Status.ApplyStatus(new StatusSpec(StatusKind.Empowered, 10f, 1.25f), 0);
                EffectiveStatsSnapshot afterStatusApply = rig.EffectiveStats;
                check(Mathf.Approximately(afterStatusApply.StatusOutgoingDamageMultiplier, 1.25f),
                    "status apply invalidates rig capture");
                ExpireStatuses(rig.Status);
                check(Mathf.Approximately(rig.EffectiveStats.StatusOutgoingDamageMultiplier, 1f),
                    "status expiry invalidates rig capture");
                rig.Status.ApplyStatus(new StatusSpec(StatusKind.Empowered, 10f, 1.25f), 0);
                rig.Status.ClearStatuses();
                check(Mathf.Approximately(rig.EffectiveStats.StatusOutgoingDamageMultiplier, 1f),
                    "status clear invalidates rig capture");

                EffectiveStatsSnapshot beforeHandChange = rig.EffectiveStats;
                parryOffhand.defenseRole = DefenseRole.Weapon;
                parryOffhand.capabilities = DefenseCapability.Parry;
                rig.SetOffhandWeapon(parryOffhand);
                EffectiveStatsSnapshot afterHandChange = rig.EffectiveStats;
                check(!beforeHandChange.Defense.CanParry && afterHandChange.Defense.CanParry,
                    "offhand changes invalidate the cached defense profile");
                check(rig.CanParry && !rig.CanGuard,
                    "guard capability properties use swapped snapshot defense and preserve no-block parity");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(body);
                UnityEngine.Object.DestroyImmediate(noParryMainHand);
                UnityEngine.Object.DestroyImmediate(parryOffhand);
            }
        }

        private static void SetField(object target, string name, object value)
        {
            FieldInfo field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null) throw new MissingFieldException(target.GetType().Name, name);
            field.SetValue(target, value);
        }

        private static void InvokeAwake(MonoBehaviour component)
        {
            if (component == null) throw new ArgumentNullException(nameof(component));
            MethodInfo awake = component.GetType().GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic);
            if (awake == null) throw new MissingMethodException(component.GetType().Name, "Awake");
            awake.Invoke(component, null);
        }

        private static void ExpireStatuses(StatusComponent status)
        {
            FieldInfo entriesField = typeof(StatusComponent).GetField("_entries", BindingFlags.Instance | BindingFlags.NonPublic);
            IList entries = entriesField != null ? entriesField.GetValue(status) as IList : null;
            if (entries == null || entries.Count == 0) throw new InvalidOperationException("Status fixture has no active entry.");

            object entry = entries[0];
            FieldInfo remainingField = entry.GetType().GetField("Remaining", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (remainingField == null) throw new MissingFieldException(entry.GetType().Name, "Remaining");
            remainingField.SetValue(entry, 0f);
            entries[0] = entry;

            MethodInfo update = typeof(StatusComponent).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic);
            if (update == null) throw new MissingMethodException(typeof(StatusComponent).Name, "Update");
            update.Invoke(status, null);
        }

        private static T Load<T>(string file) where T : UnityEngine.Object =>
            AssetDatabase.LoadAssetAtPath<T>(Data + file);
    }
}
