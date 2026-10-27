using System.Collections.Generic;
using Emberdelve.Combat;
using Emberdelve.Data;
using Emberdelve.Items;
using Emberdelve.Skills;
using UnityEngine;

namespace Emberdelve.Stats
{
    /// <summary>
    /// An immutable capture of the current effective stat reads. This is a read model, not an owner of
    /// equipment, pools, or status state. Callers decide when to capture it; taking a capture never mutates
    /// its inputs, and applying its vital ceilings remains the caller's responsibility.
    /// </summary>
    public readonly struct EffectiveStatsSnapshot
    {
        public readonly bool IsReady;
        public readonly bool AttributesReady;
        public readonly AttributeSnapshot Attributes;
        public readonly DerivedStats Derived;
        public readonly DefenseProfile Defense;
        public readonly int MaxHealth;
        public readonly float HealthRegenPerSecond;
        public readonly float HealthRegenDelay;
        public readonly int MaxMana;
        public readonly float ManaRegenPerSecond;
        public readonly float ManaRegenDelay;
        public readonly float WalkSpeed;
        public readonly float SprintSpeed;
        public readonly float CrouchSpeed;
        public readonly float ArmorRating;
        public readonly float MeleeDamageMultiplier;
        public readonly float RangedDamageMultiplier;
        public readonly float MeleeFlatDamageBonus;
        public readonly float ProjectileFlatDamageBonus;
        public readonly float AbilityEffectScale;
        public readonly float StatusSpeedMultiplier;
        public readonly float StatusOutgoingDamageMultiplier;

        internal EffectiveStatsSnapshot(bool isReady, bool attributesReady, AttributeSnapshot attributes, DerivedStats derived,
            DefenseProfile defense, int maxHealth, float healthRegenPerSecond, float healthRegenDelay,
            int maxMana, float manaRegenPerSecond, float manaRegenDelay,
            float walkSpeed, float sprintSpeed, float crouchSpeed,
            float armorRating, float meleeDamageMultiplier, float rangedDamageMultiplier,
            float meleeFlatDamageBonus, float projectileFlatDamageBonus, float abilityEffectScale, float statusSpeedMultiplier,
            float statusOutgoingDamageMultiplier)
        {
            IsReady = isReady;
            AttributesReady = attributesReady;
            Attributes = attributes;
            Derived = derived;
            Defense = defense;
            MaxHealth = maxHealth;
            HealthRegenPerSecond = healthRegenPerSecond;
            HealthRegenDelay = healthRegenDelay;
            MaxMana = maxMana;
            ManaRegenPerSecond = manaRegenPerSecond;
            ManaRegenDelay = manaRegenDelay;
            WalkSpeed = walkSpeed;
            SprintSpeed = sprintSpeed;
            CrouchSpeed = crouchSpeed;
            ArmorRating = armorRating;
            MeleeDamageMultiplier = meleeDamageMultiplier;
            RangedDamageMultiplier = rangedDamageMultiplier;
            MeleeFlatDamageBonus = meleeFlatDamageBonus;
            ProjectileFlatDamageBonus = projectileFlatDamageBonus;
            AbilityEffectScale = abilityEffectScale;
            StatusSpeedMultiplier = statusSpeedMultiplier;
            StatusOutgoingDamageMultiplier = statusOutgoingDamageMultiplier;
        }

        /// <summary>
        /// The weapon-specific attribute and passive/status multiplier already used by PlayerRig. Damage base,
        /// charge, falloff, and hit-zone rules remain in DamageResolver and are not folded into this value.
        /// </summary>
        public float GetOutgoingDamageMultiplier(WeaponData weapon)
        {
            if (!IsReady) return 0f;
            // PlayerRig's legacy query keeps the current temporary damage status multiplier even when there
            // is no weapon; the caller owns whether a weapon is required to submit an attack.
            if (weapon == null) return Mathf.Max(0f, StatusOutgoingDamageMultiplier);
            float attributeFactor = AttributesReady
                ? AttributeRules.WeaponDamage(1f, weapon.DamageScale, Attributes.Effective) : 1f;
            float channel = weapon.IsRanged ? RangedDamageMultiplier : MeleeDamageMultiplier;
            return Mathf.Max(0f, attributeFactor * channel * StatusOutgoingDamageMultiplier);
        }

        /// <summary>Flat rolled base bonus, kept separate from multipliers and selected by the weapon's channel.</summary>
        public float GetFlatWeaponDamageBonus(WeaponData weapon)
        {
            if (!IsReady || weapon == null) return 0f;
            return weapon.IsRanged ? ProjectileFlatDamageBonus : MeleeFlatDamageBonus;
        }
    }

    /// <summary>
    /// Captures the existing authoritative stat owners without changing their state. This deliberately reuses
    /// StatsComponent's already-derived six-attribute result and CharacterStatStack's flat/multiplier reads;
    /// it does not aggregate item bonuses a second time or promise automatic cache invalidation.
    /// </summary>
    public static class EffectiveStatsResolver
    {
        public static EffectiveStatsSnapshot Capture(CharacterClassData classData, StatsComponent attributes,
            CharacterStatStack modifiers, IReadOnlyList<ItemInstance> worn, StatusComponent status,
            DefenseProfile defense)
        {
            if (classData == null)
                return default;

            bool attributesReady = attributes != null && attributes.IsReady;
            AttributeSnapshot attributeSnapshot = attributesReady ? attributes.Snapshot : default;
            DerivedStats derived = attributesReady ? attributes.Derived : default;

            int baseHealth = Mathf.Max(1, classData.maxHealth);
            int baseMana = Mathf.Max(1, classData.EffectiveMaxMana);
            float baseManaRegen = Mathf.Max(0f, classData.manaRegenPerSecond);
            if (attributesReady)
            {
                baseHealth = attributes.MaxHealth;
                baseMana = attributes.MaxMana;
                baseManaRegen = attributes.ManaRegen;
            }

            int maxHealth = modifiers != null
                ? Mathf.Max(1, Mathf.RoundToInt(modifiers.Get(SkillStat.MaxHealth, baseHealth))) : baseHealth;
            int maxMana = modifiers != null
                ? Mathf.Max(1, Mathf.RoundToInt(modifiers.Get(SkillStat.MaxMana, baseMana))) : baseMana;
            float manaRegen = modifiers != null
                ? Mathf.Max(0f, modifiers.Get(SkillStat.ManaRegen, baseManaRegen)) : baseManaRegen;

            float moveMultiplier = modifiers != null ? modifiers.Multiplier(SkillStat.MoveSpeed) : 1f;
            float moveFlat = modifiers != null ? modifiers.Flat(SkillStat.MoveSpeed) : 0f;
            float sprintMultiplier = modifiers != null ? modifiers.Multiplier(SkillStat.SprintSpeed) : 1f;
            float sprintFlat = modifiers != null ? modifiers.Flat(SkillStat.SprintSpeed) : 0f;
            float crouchMultiplier = modifiers != null ? modifiers.Multiplier(SkillStat.CrouchSpeed) : 1f;
            float crouchFlat = modifiers != null ? modifiers.Flat(SkillStat.CrouchSpeed) : 0f;
            float walkSpeed = attributesReady
                ? attributes.ResolveMoveSpeed(moveMultiplier, moveFlat)
                : (modifiers != null ? modifiers.Get(SkillStat.MoveSpeed, classData.moveSpeed) : classData.moveSpeed);
            float sprintSpeed = attributesReady
                ? attributes.ResolveAuthoredSpeed(classData.sprintSpeed, sprintMultiplier, sprintFlat)
                : (modifiers != null ? modifiers.Get(SkillStat.SprintSpeed, classData.sprintSpeed) : classData.sprintSpeed);
            float crouchSpeed = attributesReady
                ? attributes.ResolveAuthoredSpeed(classData.crouchSpeed, crouchMultiplier, crouchFlat)
                : (modifiers != null ? modifiers.Get(SkillStat.CrouchSpeed, classData.crouchSpeed) : classData.crouchSpeed);

            float armor = modifiers != null ? modifiers.Flat(SkillStat.Armor) : 0f;
            armor = DamageResolver.SanitizeArmorRating(armor);
            float meleeMultiplier = modifiers != null ? modifiers.Multiplier(SkillStat.MeleeDamage) : 1f;
            float rangedMultiplier = modifiers != null ? modifiers.Multiplier(SkillStat.RangedDamage) : 1f;
            SumFlatDamage(worn, out float meleeFlatBonus, out float projectileFlatBonus);

            float speedStatus = status != null ? status.SpeedMultiplier : 1f;
            float damageStatus = status != null ? status.OutgoingDamageMultiplier : 1f;

            return new EffectiveStatsSnapshot(true, attributesReady, attributeSnapshot, derived, defense,
                maxHealth, Mathf.Max(0f, classData.healthRegenPerSecond), Mathf.Max(0f, classData.regenDelay),
                maxMana, manaRegen, Mathf.Max(0f, classData.manaRegenDelay),
                walkSpeed, sprintSpeed, crouchSpeed, armor, meleeMultiplier, rangedMultiplier,
                WeaponData.SanitizeBaseDamageBonus(meleeFlatBonus),
                WeaponData.SanitizeBaseDamageBonus(projectileFlatBonus),
                attributesReady ? attributes.GetAbilityEffectScale() : 1f,
                Mathf.Clamp(speedStatus, 0f, 4f), Mathf.Clamp(damageStatus, 0f, 8f));
        }

        private static void SumFlatDamage(IReadOnlyList<ItemInstance> worn, out float melee, out float projectile)
        {
            melee = 0f;
            projectile = 0f;
            if (worn == null) return;

            for (int i = 0; i < worn.Count; i++)
            {
                ItemInstance item = worn[i];
                if (item == null) continue;
                IReadOnlyList<AffixRecord> affixes = item.Affixes;
                if (affixes == null) continue;
                for (int a = 0; a < affixes.Count; a++)
                {
                    AffixRecord affix = affixes[a];
                    if (!AffixStatKeys.TryGetRoute(affix.StatKey, out AffixStatRoute route)
                        || route.Kind != AffixStatRouteKind.WeaponBaseDamageFlat) continue;
                    if (route.WeaponDamageChannel == WeaponBaseDamageChannel.Melee) melee += affix.Value;
                    else if (route.WeaponDamageChannel == WeaponBaseDamageChannel.Projectile) projectile += affix.Value;
                }
            }
        }
    }
}
