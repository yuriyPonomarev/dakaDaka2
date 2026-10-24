using Emberdelve.Combat;
using Emberdelve.Data;
using Emberdelve.Items;
using Emberdelve.Skills;
using Emberdelve.Stats;
using UnityEditor;
using UnityEngine;

namespace Emberdelve.EditorTools
{
    /// <summary>Independent ordinal and live-consumer checks for the central affix routing table.</summary>
    public static class EmberdelveAffixRouteGate
    {
        [MenuItem("Emberdelve/Validation/Affix Routes")]
        public static void Run()
        {
            int checks = 0;
            int failures = 0;
            void Check(bool condition, string label)
            {
                checks++;
                if (condition) return;
                failures++;
                Debug.LogError("[AffixRoutes] mismatch: " + label);
            }

            AffixStatRouteKind[] kinds =
            {
                AffixStatRouteKind.Attribute, AffixStatRouteKind.Attribute, AffixStatRouteKind.Attribute,
                AffixStatRouteKind.Attribute, AffixStatRouteKind.Attribute, AffixStatRouteKind.Attribute,
                AffixStatRouteKind.SkillStatFlat, AffixStatRouteKind.SkillStatFlat, AffixStatRouteKind.SkillStatFlat,
                AffixStatRouteKind.SkillStatFlat, AffixStatRouteKind.WeaponBaseDamageFlat,
                AffixStatRouteKind.WeaponBaseDamageFlat, AffixStatRouteKind.SkillStatFlat,
            };
            int[] destinations =
            {
                (int)SixAttribute.Strength, (int)SixAttribute.Dexterity, (int)SixAttribute.Constitution,
                (int)SixAttribute.Intelligence, (int)SixAttribute.Wisdom, (int)SixAttribute.Charisma,
                (int)SkillStat.Armor, (int)SkillStat.MaxHealth, (int)SkillStat.MaxMana,
                (int)SkillStat.MoveSpeed, (int)WeaponBaseDamageChannel.Melee,
                (int)WeaponBaseDamageChannel.Projectile, (int)SkillStat.ManaRegen,
            };
            AffixStatKey[] keys =
            {
                AffixStatKey.Strength, AffixStatKey.Dexterity, AffixStatKey.Constitution,
                AffixStatKey.Intelligence, AffixStatKey.Wisdom, AffixStatKey.Charisma,
                AffixStatKey.Armor, AffixStatKey.MaxHealth, AffixStatKey.MaxMana,
                AffixStatKey.MoveSpeed, AffixStatKey.MeleeDamage, AffixStatKey.ProjectileDamage,
                AffixStatKey.ManaRegen,
            };
            Check(AffixStatKeys.KeyCount == 13, "13 existing affix key ordinals remain declared");
            for (int ordinal = 0; ordinal < 13; ordinal++)
            {
                Check((int)keys[ordinal] == ordinal, "existing key ordinal " + ordinal);
                bool found = AffixStatKeys.TryGetRoute(keys[ordinal], out AffixStatRoute route);
                int actualDestination = route.Kind == AffixStatRouteKind.Attribute
                    ? (int)route.Attribute
                    : route.Kind == AffixStatRouteKind.SkillStatFlat
                        ? (int)route.SkillStat : (int)route.WeaponDamageChannel;
                Check(found && (int)route.Kind == (int)kinds[ordinal]
                    && actualDestination == destinations[ordinal], "literal route for ordinal " + ordinal);
                Check(AffixStatKeys.IsAttribute(keys[ordinal])
                    == (ordinal >= 0 && ordinal <= 5), "attribute predicate for ordinal " + ordinal);
            }
            Check(!AffixStatKeys.TryGetRoute((AffixStatKey)99, out _), "unknown numeric key 99 is rejected");
            Check(!AffixStatKeys.TryAsAttribute((AffixStatKey)99, out _), "unknown numeric attribute cast is rejected");

            ItemCatalog catalog = AssetDatabase.LoadAssetAtPath<ItemCatalog>("Assets/Emberdelve/Data/EmberdelveItemCatalog.asset");
            ItemData blade = AssetDatabase.LoadAssetAtPath<ItemData>("Assets/Emberdelve/Data/Item_EmberFangBlade.asset");
            CharacterClassData warrior = AssetDatabase.LoadAssetAtPath<CharacterClassData>("Assets/Emberdelve/Data/Class_Warrior.asset");
            ItemInstanceAuthority authority = null;
            string reason = null;
            bool authorityReady = catalog != null && catalog.TryGetAuthority(out authority, out reason);
            Check(authorityReady && blade != null && warrior != null,
                "authored catalog-backed item and class fixtures exist" + (authorityReady ? string.Empty : ": " + (catalog == null ? "catalog missing" : reason)));
            if (authorityReady && blade != null && warrior != null)
            {
                bool created = ItemInstance.TryCreateWithUid(System.Guid.NewGuid(), blade, 1, AffixRarity.Rare,
                    new[]
                    {
                        new AffixRecord("affix_melee_damage", AffixStatKey.MeleeDamage, 2f),
                        new AffixRecord("affix_projectile_damage", AffixStatKey.ProjectileDamage, 3f),
                    }, authority, out ItemInstance rolledBlade, out string itemReason);
                Check(created, "catalog-backed rolled weapon instance validates: " + itemReason);
                if (created)
                {
                    StatsComponent attributes = new StatsComponent();
                    attributes.Initialize(warrior);
                    CharacterStatStack modifiers = new CharacterStatStack();
                    modifiers.Build(warrior);
                    EffectiveStatsSnapshot snapshot = EffectiveStatsResolver.Capture(warrior, attributes,
                        modifiers, new[] { rolledBlade }, null, DefenseProfile.Empty);
                    Check(snapshot.MeleeFlatDamageBonus == 2f && snapshot.ProjectileFlatDamageBonus == 3f,
                        "valid rolled weapon reaches each flat channel exactly once");
                    Check(rolledBlade.AffixTotal(AffixStatKey.MeleeDamage) == 2f
                        && rolledBlade.AffixTotal(AffixStatKey.ProjectileDamage) == 3f,
                        "affix instance totals remain distinct from multiplier/attribute routes");
                bool attributesCreated = ItemInstance.TryCreateWithUid(System.Guid.NewGuid(), blade, 1,
                    AffixRarity.Rare,
                    new[]
                    {
                        new AffixRecord("affix_strength", AffixStatKey.Strength, 2f),
                        new AffixRecord("affix_constitution", AffixStatKey.Constitution, 3f),
                    }, authority, out ItemInstance rolledAttributes, out string attributeReason);
                Check(attributesCreated, "catalog-backed rolled attribute instance validates: " + attributeReason);
                if (attributesCreated)
                {
                    StatsComponent attributeConsumer = new StatsComponent();
                    attributeConsumer.Initialize(warrior);
                    attributeConsumer.RebuildFromInstances(new[] { rolledAttributes });
                    Check(attributeConsumer.RolledBonuses.Total(SixAttribute.Strength) == 2
                        && attributeConsumer.RolledBonuses.Total(SixAttribute.Constitution) == 3
                        && attributeConsumer.EffectiveAttributes.Strength == warrior.BaseAttributes.Strength + 2
                        && attributeConsumer.EffectiveAttributes.Constitution == warrior.BaseAttributes.Constitution + 3,
                        "valid rolled attributes reach StatsComponent exactly once alongside unchanged class bases");
                }
                }
            }

            Debug.Log("[Emberdelve][AffixRoutes] " + (failures == 0 ? "PASS" : "FAIL")
                + " checks=" + checks + " failures=" + failures);
            if (Application.isBatchMode) EditorApplication.Exit(failures == 0 ? 0 : 1);
        }
    }
}


