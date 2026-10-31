using System;
using System.Collections.Generic;
using Emberdelve.Data;

namespace Emberdelve.Items
{
    /// <summary>
    /// D3a: the gear score of what a character wears, for display. The arithmetic is
    /// <see cref="GearScoreRules"/> (pure, in Emberdelve.Rules, unit tested); this class only maps the equipment
    /// slots to weights and reads the worn instances' rarity. Nothing in the game reads the score for a rule - no
    /// matchmaking, no stat, no loot - it is a number shown in the lobby.
    ///
    /// Slot weights: the main weapon counts double (2), every other worn slot counts once (1). D3b added the new
    /// slots (hands, feet, necklace, both rings) with a weight of 1 each, so a full kit has 11 slots and 12 weight.
    /// </summary>
    public static class GearScore
    {
        /// <summary>Weight of one equipment slot; 0 for None (not a worn slot), which is how it is skipped.</summary>
        public static int SlotWeight(EquipmentSlot slot)
        {
            switch (slot)
            {
                case EquipmentSlot.Weapon:
                case EquipmentSlot.AltWeapon: return 2;
                case EquipmentSlot.OffHand:
                case EquipmentSlot.AltOffHand:
                case EquipmentSlot.Head:
                case EquipmentSlot.Chest:
                case EquipmentSlot.Legs:
                case EquipmentSlot.Back:
                case EquipmentSlot.Hands:
                case EquipmentSlot.Feet:
                case EquipmentSlot.Necklace:
                case EquipmentSlot.RingLeft:
                case EquipmentSlot.RingRight:
                    return 1;
                default: return 0;
            }
        }

        /// <summary>Score of the active kit (inactive weapon-set hands do not contribute). 0 for null equipment or an empty character. Read-only.</summary>
        public static int ForEquipment(Equipment equipment)
        {
            return TryForEquipment(equipment, out int score, out _) ? score : 0;
        }

        public static bool TryForEquipment(Equipment equipment, out int score, out string reason)
        {
            score = 0;
            if (equipment == null)
            {
                reason = null;
                return true;
            }

            List<int> rarities = new List<int>(8);
            List<int> weights = new List<int>(8);

            foreach (EquipmentSlot slot in Enum.GetValues(typeof(EquipmentSlot)))
            {
                int weight = SlotWeight(slot);
                if (weight == 0 || !equipment.IsSlotLive((int)slot)) continue;

                ItemInstance worn = equipment.EquippedInstance(slot);
                rarities.Add(worn != null ? worn.RarityValue : GearScoreRules.EmptySlot);
                weights.Add(weight);
            }

            return GearScoreRules.TryTotal(rarities, weights, out score, out reason);
        }

        /// <summary>The lobby/inventory text for a score: "Gear score  42".</summary>
        public static string Label(int score)
        {
            return "Gear score  " + score.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
