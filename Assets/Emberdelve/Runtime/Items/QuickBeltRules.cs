using Emberdelve.Data;
using Emberdelve.Player;
using UnityEngine;

namespace Emberdelve.Items
{
    /// <summary>Physical utility pockets use reserved cells beside the bag grid; each holds one stack.</summary>
    public static class QuickBeltRules
    {
        public const int SlotCount = 6;
        public static Vector2Int Cell(int slot, int bagWidth) => slot >= 0 && slot < SlotCount
            ? new Vector2Int(bagWidth + slot, 0) : new Vector2Int(-1, -1);
        public static bool TryGetSlot(Vector2Int cell, int bagWidth, out int slot)
        {
            slot = cell.x - bagWidth;
            return bagWidth > 0 && cell.y == 0 && slot >= 0 && slot < SlotCount;
        }
        public static bool CanStore(ItemData item) => item != null && (item.healAmount > 0 ||
            (item.grantedWeapon != null && item.grantedWeapon.weaponId == HandheldTorchVisual.WeaponId));
        public static int StackLimit(ItemData item) => !CanStore(item) ? 0 :
            Mathf.Min(item.StackLimitForFootprint, item.healAmount > 0 ? 5 : 1);
    }
}
