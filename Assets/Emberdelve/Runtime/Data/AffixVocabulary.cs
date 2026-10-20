using System;
using Emberdelve.Stats;

namespace Emberdelve.Data
{
    /// <summary>
    /// The allowlisted affix stat keys from Docs/Design/ITEM_AFFIXES.md: the six ability scores plus the
    /// functional equipment stats an affix is allowed to move. Typed on purpose - a definition, an instance
    /// record or a server packet names one of these values and never a free-form string, so an unknown key
    /// is a rejection at the type boundary instead of a dictionary lookup that quietly yields zero.
    ///
    /// The first six values deliberately mirror SixAttribute's numbering, but the conversion below is an
    /// explicit checked mapping, not a cast the callers are expected to know: AffixStatKey and SkillStat
    /// (what a passive changes) are separate vocabularies and neither is derived from the other.
    /// </summary>
    public enum AffixStatKey
    {
        Strength = 0,
        Dexterity = 1,
        Constitution = 2,
        Intelligence = 3,
        Wisdom = 4,
        Charisma = 5,
        Armor = 6,
        MaxHealth = 7,
        MaxMana = 8,
        MoveSpeed = 9,
        MeleeDamage = 10,
        ProjectileDamage = 11,
        ManaRegen = 12,
    }

    /// <summary>
    /// Canonical names, prototype per-key bands and the checked mappings for AffixStatKey. Pure and
    /// stateless: nothing here reads an asset, and every method is total over the enum's legal values.
    /// </summary>
    public static class AffixStatKeys
    {
        /// <summary>Number of allowlisted keys.</summary>
        public const int KeyCount = 13;

        /// <summary>Only the declared enum values count; a cast of an arbitrary int is not a key.</summary>
        public static bool IsKnown(AffixStatKey key)
        {
            return key >= AffixStatKey.Strength && key <= AffixStatKey.ManaRegen;
        }

        /// <summary>The six ability-score keys.</summary>
        public static bool IsAttribute(AffixStatKey key)
        {
            return key >= AffixStatKey.Strength && key <= AffixStatKey.Charisma;
        }

        /// <summary>
        /// Keys whose authored min/max/step and whose rolled values must be whole numbers (the amendment:
        /// "Attribute values are integers; other steps configured"). Armor, HP, mana, speed, damage and
        /// regen keep their own configured steps and are not forced integral.
        /// </summary>
        public static bool IsIntegralKey(AffixStatKey key)
        {
            return IsAttribute(key);
        }

        /// <summary>Canonical lowercase key, spelled exactly as the amendment's allowlist. Empty for an
        /// unknown value, which is how an illegal key shows up in a message instead of a silent "0".</summary>
        public static string Key(AffixStatKey key)
        {
            switch (key)
            {
                case AffixStatKey.Strength: return "strength";
                case AffixStatKey.Dexterity: return "dexterity";
                case AffixStatKey.Constitution: return "constitution";
                case AffixStatKey.Intelligence: return "intelligence";
                case AffixStatKey.Wisdom: return "wisdom";
                case AffixStatKey.Charisma: return "charisma";
                case AffixStatKey.Armor: return "armor";
                case AffixStatKey.MaxHealth: return "max_health";
                case AffixStatKey.MaxMana: return "max_mana";
                case AffixStatKey.MoveSpeed: return "move_speed";
                case AffixStatKey.MeleeDamage: return "melee_damage";
                case AffixStatKey.ProjectileDamage: return "projectile_damage";
                case AffixStatKey.ManaRegen: return "mana_regen";
                default: return string.Empty;
            }
        }

        /// <summary>
        /// Strict allowlist parse for the persistence/transfer boundary: only the canonical keys are
        /// accepted, compared after trimming and case folding. Anything else returns false and leaves the
        /// out parameter at the first legal key rather than guessing.
        /// </summary>
        public static bool TryParseKey(string raw, out AffixStatKey key)
        {
            key = AffixStatKey.Strength;
            if (string.IsNullOrEmpty(raw)) return false;

            string normalized = raw.Trim().ToLowerInvariant();
            for (int i = 0; i < KeyCount; i++)
            {
                AffixStatKey candidate = (AffixStatKey)i;
                if (Key(candidate) != normalized) continue;
                key = candidate;
                return true;
            }
            return false;
        }

        /// <summary>Checked conversion into the attribute rules' own enum. False for the seven non-attribute keys.</summary>
        public static bool TryAsAttribute(AffixStatKey key, out SixAttribute attribute)
        {
            attribute = SixAttribute.Strength;
            if (!IsAttribute(key)) return false;

            SixAttribute candidate = (SixAttribute)(int)key;
            if (candidate < SixAttribute.Strength || candidate > SixAttribute.Charisma) return false;

            attribute = candidate;
            return true;
        }

        /// <summary>
        /// The prototype per-key band and default step from Docs/Design/ITEM_AFFIXES.md: attributes +1..3,
        /// armor +1..6, max_health +5..15, max_mana +5..15, move_speed +0.03..0.12 m/s, melee_damage +1..4,
        /// projectile_damage +1..4, mana_regen +0.1..0.4/s. A definition may author a narrower band or step
        /// inside it; it may never author a wider one. False (and zeros) for an unknown key.
        /// </summary>
        public static bool TryPrototypeBand(AffixStatKey key, out float minValue, out float maxValue, out float step)
        {
            switch (key)
            {
                case AffixStatKey.Strength:
                case AffixStatKey.Dexterity:
                case AffixStatKey.Constitution:
                case AffixStatKey.Intelligence:
                case AffixStatKey.Wisdom:
                case AffixStatKey.Charisma:
                    minValue = 1f; maxValue = 3f; step = 1f; return true;
                case AffixStatKey.Armor:
                    minValue = 1f; maxValue = 6f; step = 1f; return true;
                case AffixStatKey.MaxHealth:
                    minValue = 5f; maxValue = 15f; step = 1f; return true;
                case AffixStatKey.MaxMana:
                    minValue = 5f; maxValue = 15f; step = 1f; return true;
                case AffixStatKey.MoveSpeed:
                    minValue = 0.03f; maxValue = 0.12f; step = 0.01f; return true;
                case AffixStatKey.MeleeDamage:
                    minValue = 1f; maxValue = 4f; step = 1f; return true;
                case AffixStatKey.ProjectileDamage:
                    minValue = 1f; maxValue = 4f; step = 1f; return true;
                case AffixStatKey.ManaRegen:
                    minValue = 0.1f; maxValue = 0.4f; step = 0.1f; return true;
                default:
                    minValue = 0f; maxValue = 0f; step = 0f; return false;
            }
        }
    }

    /// <summary>
    /// Which gear families an affix may roll on (the amendment's "profiles may restrict which affixes can
    /// roll on boots, cloak, armour, weapon"). Flags so one definition can name several families and so a
    /// later ItemData mapping is one lookup from an equipment slot to one of these bits - this slice does
    /// not add or rename any equipment slot.
    /// </summary>
    [Flags]
    public enum AffixCategory
    {
        None = 0,
        Boots = 1 << 0,
        Cloak = 1 << 1,
        Armor = 1 << 2,
        Weapon = 1 << 3,
    }

    /// <summary>Category-set rules: legal bits only, no silent widening of an authored set.</summary>
    public static class AffixCategories
    {
        /// <summary>The only bits this vocabulary knows.</summary>
        public const AffixCategory All =
            AffixCategory.Boots | AffixCategory.Cloak | AffixCategory.Armor | AffixCategory.Weapon;

        /// <summary>At least one legal bit and no unknown bit (a hand-cast 16 or 3 is a failure, not a typo).</summary>
        public static bool IsKnownSet(AffixCategory categories)
        {
            int bits = (int)categories;
            if (bits == 0) return false;
            return (bits & ~(int)All) == 0;
        }

        /// <summary>Exactly one legal bit - the shape a rolled affix's target category must have.</summary>
        public static bool IsSingle(AffixCategory categories)
        {
            int bits = (int)categories;
            if (bits == 0) return false;
            if ((bits & ~(int)All) != 0) return false;
            return (bits & (bits - 1)) == 0;
        }

        /// <summary>Canonical names for a message. An unknown bit is reported as such rather than dropped.</summary>
        public static string Describe(AffixCategory categories)
        {
            int bits = (int)categories;
            if (bits == 0) return "none";

            System.Text.StringBuilder builder = new System.Text.StringBuilder();
            for (int i = 0; i < 4; i++)
            {
                int bit = 1 << i;
                if ((bits & bit) == 0) continue;
                if (builder.Length > 0) builder.Append('|');
                builder.Append(((AffixCategory)bit).ToString().ToLowerInvariant());
            }

            int unknown = bits & ~(int)All;
            if (unknown != 0)
            {
                if (builder.Length > 0) builder.Append('|');
                builder.Append("unknown(").Append(unknown).Append(')');
            }

            return builder.ToString();
        }
    }

    /// <summary>
    /// Item rarity from Docs/Design/ITEM_AFFIXES.md (Common 0 affixes, Uncommon 1, Rare 2). This is the
    /// affix layer's own enum: no existing ItemKind/EquipmentSlot/SkillStat numeric value is changed, and
    /// no existing enum gains a member.
    /// </summary>
    public enum AffixRarity
    {
        Common = 0,
        Uncommon = 1,
        Rare = 2,
    }

    /// <summary>Rarity-to-affix-count rules that reject an unknown rarity instead of clamping it.</summary>
    public static class AffixRarities
    {
        public const int MinRarityValue = 0;
        public const int MaxRarityValue = 2;

        public static bool IsKnown(AffixRarity rarity)
        {
            return rarity >= AffixRarity.Common && rarity <= AffixRarity.Rare;
        }

        /// <summary>For data that has not been converted to the enum yet (a save file, a packet).</summary>
        public static bool IsKnownValue(int rawRarity)
        {
            return rawRarity >= MinRarityValue && rawRarity <= MaxRarityValue;
        }

        /// <summary>0/1/2 for a known rarity. An unknown rarity returns false with count -1, never 0.</summary>
        public static bool TryExpectedAffixCount(AffixRarity rarity, out int count)
        {
            if (!IsKnown(rarity))
            {
                count = -1;
                return false;
            }

            switch (rarity)
            {
                case AffixRarity.Common: count = 0; return true;
                case AffixRarity.Uncommon: count = 1; return true;
                default: count = 2; return true;
            }
        }

        /// <summary>Same rule over a raw numeric rarity; an out-of-range value is rejected, not clamped.</summary>
        public static bool TryExpectedAffixCount(int rawRarity, out int count)
        {
            if (!IsKnownValue(rawRarity))
            {
                count = -1;
                return false;
            }

            return TryExpectedAffixCount((AffixRarity)rawRarity, out count);
        }
    }
}
