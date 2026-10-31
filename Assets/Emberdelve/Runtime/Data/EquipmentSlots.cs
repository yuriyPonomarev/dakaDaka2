using System;

namespace Emberdelve.Data
{
    /// <summary>
    /// D3b: pure rules over <see cref="EquipmentSlot"/> that more than one place needs. An item definition authors ONE
    /// slot (<see cref="ItemData.equipSlot"/>); the only family with more than one place to stand is the ring: a ring
    /// authors <see cref="EquipmentSlot.RingLeft"/> and is accepted by either ring slot. Every other slot accepts
    /// exactly the items that author it. No item id and no display name is ever consulted.
    /// </summary>
    public static class EquipmentSlots
    {
        /// <summary>D3c: the number of weapon sets (each is a main hand plus an off hand).</summary>
        public const int WeaponSetCount = 2;

        /// <summary>D3c: the main-hand slot of weapon set 0 (<see cref="EquipmentSlot.Weapon"/>) or 1 (<see cref="EquipmentSlot.AltWeapon"/>); None for any other number.</summary>
        public static EquipmentSlot MainHandOf(int set)
        {
            return set == 0 ? EquipmentSlot.Weapon : set == 1 ? EquipmentSlot.AltWeapon : EquipmentSlot.None;
        }

        /// <summary>D3c: the off-hand slot of weapon set 0 (<see cref="EquipmentSlot.OffHand"/>) or 1 (<see cref="EquipmentSlot.AltOffHand"/>); None for any other number.</summary>
        public static EquipmentSlot OffHandOf(int set)
        {
            return set == 0 ? EquipmentSlot.OffHand : set == 1 ? EquipmentSlot.AltOffHand : EquipmentSlot.None;
        }

        /// <summary>D3c: which weapon set a hand slot belongs to (0 or 1), or -1 for a slot that is not a hand slot.</summary>
        public static int SetOf(EquipmentSlot slot)
        {
            if (slot == EquipmentSlot.Weapon || slot == EquipmentSlot.OffHand) return 0;
            if (slot == EquipmentSlot.AltWeapon || slot == EquipmentSlot.AltOffHand) return 1;
            return -1;
        }

        public static bool IsMainHand(EquipmentSlot slot)
        {
            return slot == EquipmentSlot.Weapon || slot == EquipmentSlot.AltWeapon;
        }

        public static bool IsOffHand(EquipmentSlot slot)
        {
            return slot == EquipmentSlot.OffHand || slot == EquipmentSlot.AltOffHand;
        }

        public static bool IsRing(EquipmentSlot slot)
        {
            return slot == EquipmentSlot.RingLeft || slot == EquipmentSlot.RingRight;
        }

        /// <summary>True when the value is one of the defined real slots (None and any hand-cast integer are not).</summary>
        public static bool IsRealSlot(EquipmentSlot slot)
        {
            return slot != EquipmentSlot.None && Enum.IsDefined(typeof(EquipmentSlot), slot);
        }

        /// <summary>The other ring slot, or None for a slot that is not a ring slot.</summary>
        public static EquipmentSlot OtherRing(EquipmentSlot slot)
        {
            if (slot == EquipmentSlot.RingLeft) return EquipmentSlot.RingRight;
            if (slot == EquipmentSlot.RingRight) return EquipmentSlot.RingLeft;
            return EquipmentSlot.None;
        }

        /// <summary>
        /// May an item authored for <paramref name="authored"/> be worn in <paramref name="target"/>? Same slot, or both
        /// are ring slots, or (D3c) a main-hand item for either main-hand slot and an off-hand item for either off-hand slot
        /// (the two weapon sets). The set-2 slots are targets only: no item authors them. None never fits anywhere and
        /// nothing fits None.
        /// </summary>
        public static bool Accepts(EquipmentSlot authored, EquipmentSlot target)
        {
            if (!IsRealSlot(authored) || !IsRealSlot(target)) return false;
            if (authored == EquipmentSlot.AltWeapon || authored == EquipmentSlot.AltOffHand) return false;
            if (authored == target) return true;
            if (IsRing(authored) && IsRing(target)) return true;
            return (authored == EquipmentSlot.Weapon && target == EquipmentSlot.AltWeapon) ||
                   (authored == EquipmentSlot.OffHand && target == EquipmentSlot.AltOffHand);
        }

        /// <summary>
        /// The slots an item authored for <paramref name="authored"/> may be worn in, in preference order (a ring prefers
        /// RingLeft, then RingRight). Empty for None or an unknown value.
        /// </summary>
        public static EquipmentSlot[] CandidatesFor(EquipmentSlot authored)
        {
            if (!IsRealSlot(authored)) return new EquipmentSlot[0];
            if (IsRing(authored)) return new[] { EquipmentSlot.RingLeft, EquipmentSlot.RingRight };
            return new[] { authored };
        }
    }
}
