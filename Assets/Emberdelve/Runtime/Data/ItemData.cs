using UnityEngine;

namespace Emberdelve.Data
{
    public enum ItemKind { Currency, Consumable, Relic, AmmoPack }

    /// <summary>Loot and consumable definition. Value feeds the extraction score.</summary>
    [CreateAssetMenu(menuName = "Emberdelve/04 Item", fileName = "ItemData")]
    public class ItemData : ScriptableObject
    {
        [Header("Identity")]
        public string itemId = "item";
        public string displayName = "Item";
        [TextArea(2, 4)] public string description = "";

        [Header("Behaviour")]
        public ItemKind kind = ItemKind.Relic;
        public int value = 15;
        public int maxStack = 3;
        [Tooltip("Health restored when used from the inventory (0 = not healable).")]
        public int healAmount = 0;
        [Tooltip("Ammo granted when used (0 = none).")]
        public int ammoGrant = 0;

        [Header("Blockout presentation")]
        public Color iconColor = new Color(0.85f, 0.72f, 0.35f);
        public Material worldMaterial;
        public Vector3 worldSize = new Vector3(0.28f, 0.28f, 0.28f);
    }
}
