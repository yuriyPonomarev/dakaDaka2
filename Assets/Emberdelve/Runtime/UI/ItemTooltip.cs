using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using Emberdelve.Combat;
using Emberdelve.Data;
using Emberdelve.Items;
using Emberdelve.Stats;

namespace Emberdelve.UI
{
    /// <summary>
    /// What an item tooltip says, as plain data: the rarity (name and colour), the FIXED properties the base
    /// definition always carries (armour, weapon damage, authored modifiers, authored attribute bonuses) and,
    /// separately, the ROLLED AFFIXES this one instance rolled. It is built from an <see cref="ItemInstance"/>,
    /// not from an <see cref="ItemData"/>: two drops of the same base item share every fixed line and differ
    /// in the rolled lines and in the short uid, which is what lets a player tell them apart. A Common
    /// instance has no rolled section at all (no header, no empty list).
    ///
    /// Pure and read-only. It never touches the shared definition, rolls nothing and mints nothing, so the
    /// screen can build it every frame and a gate can assert on <see cref="ToPlainText"/> without any UI.
    /// </summary>
    public sealed class ItemTooltipModel
    {
        public const string FixedHeader = "FIXED PROPERTIES";
        public const string RolledHeader = "ROLLED AFFIXES";

        public string Title;
        public bool HasRarity;
        public AffixRarity Rarity;
        public string RarityName = string.Empty;
        public Color RarityColor = ItemTooltip.NeutralColor;
        public string SlotName = string.Empty;
        public string UidShort = string.Empty;
        public readonly List<string> FixedLines = new List<string>(6);
        public readonly List<string> RolledLines = new List<string>(2);
        /// <summary>The same rolls as players read them ("+9 Max health"); RolledLines keeps the canonical keys for gates and tooling.</summary>
        public readonly List<string> RolledDisplayLines = new List<string>(2);

        public bool HasRolledSection => RolledLines.Count > 0;

        /// <summary>One line for the strip under the grid: rarity, name and the short uid.</summary>
        public string Headline
        {
            get
            {
                string text = HasRarity ? RarityName + "  " + Title : Title;
                if (UidShort.Length > 0) text += "   [" + UidShort + "]";
                return text;
            }
        }

        /// <summary>The whole tooltip as text, section by section; this is what the gates assert on.</summary>
        public string ToPlainText()
        {
            StringBuilder builder = new StringBuilder(160);
            builder.Append(Title).Append('\n');
            if (HasRarity) builder.Append("Rarity: ").Append(RarityName).Append('\n');
            if (SlotName.Length > 0) builder.Append("Slot: ").Append(SlotName).Append('\n');

            builder.Append(FixedHeader).Append('\n');
            for (int i = 0; i < FixedLines.Count; i++) builder.Append("  ").Append(FixedLines[i]).Append('\n');

            if (HasRolledSection)
            {
                builder.Append(RolledHeader).Append('\n');
                for (int i = 0; i < RolledLines.Count; i++) builder.Append("  ").Append(RolledLines[i]).Append('\n');
            }

            if (UidShort.Length > 0) builder.Append("uid ").Append(UidShort);
            return builder.ToString().TrimEnd('\n');
        }
    }

    /// <summary>Builds <see cref="ItemTooltipModel"/>s and owns the rarity names and colours.</summary>
    public static class ItemTooltip
    {
        public static readonly Color NeutralColor = new Color(0.88f, 0.84f, 0.78f, 1f);
        // D3a rarity ladder colours (DND_PARITY.md): Poor grey, Common white, Uncommon green, Rare blue, Epic purple,
        // Legendary orange, Unique gold. Common moved from the old blue-grey to white when Poor took the grey; Uncommon
        // and Rare keep their previous values. Each is checked against the panel backgrounds by the inventory UI gate.
        public static readonly Color PoorColor = new Color(0.6f, 0.6f, 0.6f, 1f);
        public static readonly Color CommonColor = new Color(1f, 1f, 1f, 1f);
        public static readonly Color UncommonColor = new Color(0.36f, 0.8f, 0.38f, 1f);
        public static readonly Color RareColor = new Color(0.38f, 0.6f, 1f, 1f);
        public static readonly Color EpicColor = new Color(0.7f, 0.45f, 0.95f, 1f);
        public static readonly Color LegendaryColor = new Color(1f, 0.58f, 0.15f, 1f);
        public static readonly Color UniqueColor = new Color(1f, 0.84f, 0.25f, 1f);

        public const int ShortUidLength = 8;

        public static string RarityName(AffixRarity rarity)
        {
            return AffixRarities.Name(rarity);
        }

        public static Color RarityColor(AffixRarity rarity)
        {
            switch (rarity)
            {
                case AffixRarity.Poor: return PoorColor;
                case AffixRarity.Common: return CommonColor;
                case AffixRarity.Uncommon: return UncommonColor;
                case AffixRarity.Rare: return RareColor;
                case AffixRarity.Epic: return EpicColor;
                case AffixRarity.Legendary: return LegendaryColor;
                case AffixRarity.Unique: return UniqueColor;
                default: return NeutralColor; // an unknown value is never painted as a real tier
            }
        }

        /// <summary>The tooltip of one concrete item: its rarity, its fixed properties and its own rolls.</summary>
        public static ItemTooltipModel ForInstance(ItemInstance instance)
        {
            if (instance == null || instance.Definition == null) return null;

            ItemData definition = instance.Definition;
            ItemTooltipModel model = new ItemTooltipModel
            {
                Title = definition.displayName,
                HasRarity = true,
                Rarity = instance.Rarity,
                RarityName = RarityName(instance.Rarity),
                RarityColor = RarityColor(instance.Rarity),
                SlotName = definition.IsEquippable ? ItemSlotLabel(definition.equipSlot) : string.Empty,
                UidShort = ShortUid(instance.UidText),
            };

            AddFixedLines(definition, model.FixedLines);

            IReadOnlyList<AffixRecord> records = instance.Affixes;
            if (records != null)
            {
                for (int i = 0; i < records.Count; i++) { model.RolledLines.Add(DescribeAffix(records[i])); model.RolledDisplayLines.Add(DescribeAffixDisplay(records[i])); }
            }

            return model;
        }

        /// <summary>A carried item with no identity of its own (coins, potions, ammo): fixed lines only, no rarity.</summary>
        public static ItemTooltipModel ForDefinition(ItemData definition)
        {
            if (definition == null) return null;

            ItemTooltipModel model = new ItemTooltipModel
            {
                Title = definition.displayName,
                SlotName = definition.IsEquippable ? ItemSlotLabel(definition.equipSlot) : string.Empty,
            };

            AddFixedLines(definition, model.FixedLines);
            return model;
        }

        public static string SlotLabel(EquipmentSlot slot)
        {
            switch (slot)
            {
                case EquipmentSlot.Weapon: return "Main hand";
                case EquipmentSlot.OffHand: return "Off hand";
                case EquipmentSlot.AltWeapon: return "Main hand II";
                case EquipmentSlot.AltOffHand: return "Off hand II";
                case EquipmentSlot.Back: return "Back";
                case EquipmentSlot.RingLeft: return "Left ring";
                case EquipmentSlot.RingRight: return "Right ring";
                default: return slot.ToString();
            }
        }

        /// <summary>
        /// What an ITEM's tooltip calls the slot it is authored for. A ring fits either ring slot, so it is just "Ring";
        /// every other slot reads as <see cref="SlotLabel"/>.
        /// </summary>
        public static string ItemSlotLabel(EquipmentSlot authored)
        {
            return EquipmentSlots.IsRing(authored) ? "Ring" : SlotLabel(authored);
        }

        public static string ShortUid(string uidText)
        {
            if (string.IsNullOrEmpty(uidText)) return string.Empty;
            return uidText.Length <= ShortUidLength ? uidText : uidText.Substring(0, ShortUidLength);
        }

        /// <summary>One rolled record as "+3 strength" or "+0.07 move_speed": the canonical allowlisted key and the exact rolled value.</summary>
        public static string DescribeAffix(AffixRecord record)
        {
            string key = record.HasKnownKey ? record.CanonicalKey : "unknown(" + record.Id + ")";
            return Signed(record.Value, record.HasKnownKey && AffixStatKeys.IsIntegralKey(record.StatKey)) + " " + key;
        }

        /// <summary>One rolled record for players: "+9 Max health", "-2 Strength", never a raw key.</summary>
        public static string DescribeAffixDisplay(AffixRecord record)
        {
            if (!record.HasKnownKey) return "Unknown effect";
            return AffixStatKeys.DisplayLine(record.StatKey, record.Value);
        }

        /// <summary>WCAG-style contrast ratio between two opaque colours (used by the gates for rarity vs panel / body text).</summary>
        public static float ContrastRatio(Color a, Color b)
        {
            float la = RelativeLuminance(a), lb = RelativeLuminance(b);
            float hi = Mathf.Max(la, lb), lo = Mathf.Min(la, lb);
            return (hi + 0.05f) / (lo + 0.05f);
        }

        private static float RelativeLuminance(Color c)
        {
            return 0.2126f * Mathf.GammaToLinearSpace(c.r) + 0.7152f * Mathf.GammaToLinearSpace(c.g) + 0.0722f * Mathf.GammaToLinearSpace(c.b);
        }

        private static void AddFixedLines(ItemData definition, List<string> lines)
        {
            if (definition.GrantsWeapon && definition.grantedWeapon != null)
            {
                WeaponData weapon = definition.grantedWeapon;
                lines.Add("Damage " + weapon.baseDamage.ToString("0.##", CultureInfo.InvariantCulture) +
                          " " + weapon.damageType.ToString().ToLowerInvariant());
            }

            StatModifier[] modifiers = definition.statModifiers;
            if (modifiers != null)
            {
                for (int i = 0; i < modifiers.Length; i++) lines.Add(DescribeModifier(modifiers[i]));
            }

            AttributeBonuses totals = definition.AttributeBonusTotals;
            if (!totals.IsEmpty)
            {
                for (int i = 0; i < AttributeRules.AttributeCount; i++)
                {
                    SixAttribute attribute = (SixAttribute)i;
                    int value = totals.Total(attribute);
                    if (value == 0) continue;
                    lines.Add(Signed(value, true) + " " + AttributeRules.Key(attribute));
                }
            }

            // D3c: a worn piece's move-speed penalty, as the percent of the final ground speed it takes (points on the 300 base).
            if (definition.IsEquippable && !definition.GrantsWeapon && definition.moveSpeedPenalty > 0f)
            {
                lines.Add("Move speed -" + (definition.moveSpeedPenalty / CombatFlowRules.PenaltyPointScale * 100f).ToString("0.#", CultureInfo.InvariantCulture) + "%");
            }

            if (!definition.IsEquippable)
            {
                if (definition.healAmount > 0 && definition.healOverTimeSeconds > 0f)
                    lines.Add("Restores " + definition.healAmount + " health over " + definition.healOverTimeSeconds.ToString("0.#", CultureInfo.InvariantCulture) + " s");
                else if (definition.healAmount > 0) lines.Add("Restores " + definition.healAmount + " health");
                if (definition.healAmount > 0 && definition.useChannelSeconds > 0f)
                    lines.Add("Takes " + definition.useChannelSeconds.ToString("0.#", CultureInfo.InvariantCulture) + " s to drink; moving fast, a hit or an attack interrupts it");
                if (definition.ammoGrant > 0) lines.Add("Grants " + definition.ammoGrant + " ammo");
                lines.Add("Value " + definition.value);
            }

            if (lines.Count == 0) lines.Add("none");
        }

        private static string DescribeModifier(StatModifier modifier)
        {
            string name = modifier.stat == SkillStat.Armor ? "Armour" : Words(modifier.stat.ToString());
            string number = modifier.value.ToString("0.##", CultureInfo.InvariantCulture);
            return modifier.mode == SkillModifierMode.Multiplier ? name + " x" + number : name + (modifier.value < 0f ? " " : " +") + number;
        }

        /// <summary>"MaxHealth" to "Max health".</summary>
        private static string Words(string pascal)
        {
            StringBuilder builder = new StringBuilder(pascal.Length + 2);
            for (int i = 0; i < pascal.Length; i++)
            {
                char c = pascal[i];
                if (i > 0 && char.IsUpper(c)) builder.Append(' ').Append(char.ToLowerInvariant(c));
                else builder.Append(c);
            }
            return builder.ToString();
        }

        private static string Signed(float value, bool whole)
        {
            string number = whole
                ? Mathf.RoundToInt(value).ToString(CultureInfo.InvariantCulture)
                : value.ToString("0.##", CultureInfo.InvariantCulture);
            return value >= 0f ? "+" + number : number;
        }
    }
}
