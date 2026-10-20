using System;
using System.Collections.Generic;
using Emberdelve.Data;

namespace Emberdelve.Items
{
    /// <summary>
    /// How an item definition becomes one authoritative gear category. The snapshot never carries a category
    /// and a caller never passes one to a factory: the category is resolved from the definition by a resolver
    /// the process trusts, and an affix definition's own allowedCategories is then checked against it.
    /// </summary>
    public delegate bool ItemCategoryResolver(ItemData definition, out AffixCategory category, out string reason);

    /// <summary>
    /// The default trusted mapping from the equipment slots this project actually has to the affix categories.
    /// Weapon and OffHand are held gear, so a shield resolves to Armor (it protects) and a blade to Weapon;
    /// Head/Chest are Armor and Legs is Boots; anything carried rather than worn resolves to None, which is
    /// only ever legal for an item with no affixes.
    ///
    /// There is no cloak/back slot in <see cref="EquipmentSlot"/> yet, so a cloak cannot be mapped through
    /// this table. Until that slot exists, an authority built with an explicit resolver delegate is the
    /// documented way to give a cloak its Boots/Cloak/Armor/Weapon category - this slice does not add or
    /// rename any equipment slot.
    /// </summary>
    public static class ItemCategoryRules
    {
        /// <summary>The mapping itself, usable directly as an <see cref="ItemCategoryResolver"/>.</summary>
        public static bool TryFromEquipmentSlot(ItemData definition, out AffixCategory category, out string reason)
        {
            category = AffixCategory.None;

            if (definition == null)
            {
                reason = "no item definition to map to a gear category";
                return false;
            }

            switch (definition.equipSlot)
            {
                case EquipmentSlot.None:
                    reason = null;
                    return true;
                case EquipmentSlot.Weapon:
                    category = AffixCategory.Weapon;
                    reason = null;
                    return true;
                case EquipmentSlot.OffHand:
                    category = AffixCategory.Armor;
                    reason = null;
                    return true;
                case EquipmentSlot.Head:
                    category = AffixCategory.Armor;
                    reason = null;
                    return true;
                case EquipmentSlot.Chest:
                    category = AffixCategory.Armor;
                    reason = null;
                    return true;
                case EquipmentSlot.Legs:
                    category = AffixCategory.Boots;
                    reason = null;
                    return true;
                default:
                    reason = $"{definition.itemId} declares equipment slot value {(int)definition.equipSlot}, " +
                            $"which is not a slot this project has";
                    return false;
            }
        }

        /// <summary>The resolver an authority uses when the caller does not supply one.</summary>
        public static readonly ItemCategoryResolver DefaultResolver = TryFromEquipmentSlot;
    }

    /// <summary>
    /// The trusted catalog an item instance is allowed to be built against: item definitions by stable item
    /// id, affix definitions by stable affix id (validated once, here, with the U2B1 catalog rules), and the
    /// category resolver that decides which gear family a definition belongs to.
    ///
    /// It is a read-only index built atomically: if any entry is null, any id is malformed or any id is
    /// duplicated, the whole authority is refused with a reason and a null output, because an ambiguous
    /// lookup is exactly the hole a snapshot would be pushed through. Nothing here rolls, mints, repairs or
    /// modifies a definition asset.
    ///
    /// "Trusted" means this process's own data. It is not a network authority: a client that builds its own
    /// catalog can build whatever that catalog contains, which is why minting loot and granting ownership
    /// belong to the server (U3B/U4A) and not to these types.
    /// </summary>
    public sealed class ItemInstanceAuthority
    {
        private readonly Dictionary<string, ItemData> _items;
        private readonly Dictionary<string, AffixDefinition> _affixes;
        private readonly List<ItemData> _itemOrder;
        private readonly List<AffixDefinition> _affixOrder;
        private readonly ItemCategoryResolver _categoryResolver;

        private ItemInstanceAuthority(Dictionary<string, ItemData> items,
            Dictionary<string, AffixDefinition> affixes,
            List<ItemData> itemOrder, List<AffixDefinition> affixOrder,
            ItemCategoryResolver categoryResolver)
        {
            _items = items;
            _affixes = affixes;
            _itemOrder = itemOrder;
            _affixOrder = affixOrder;
            _categoryResolver = categoryResolver;
        }

        /// <summary>
        /// Build the authority from the supplied lists. The affix catalog is validated with
        /// <see cref="AffixCatalog"/> (every definition valid, no duplicate id, no duplicate stat key) before
        /// it is indexed; an empty affix list is legal and simply means this authority can only ever build
        /// Common items with no affixes. Item ids must be stable and unique, because a snapshot names an item
        /// by id and two definitions claiming one id would make a restore mean two different things.
        /// </summary>
        public static bool TryCreate(IReadOnlyList<ItemData> items, IReadOnlyList<AffixDefinition> affixes,
            ItemCategoryResolver categoryResolver, out ItemInstanceAuthority authority, out string reason)
        {
            authority = null;

            if (items == null)
            {
                reason = "item catalog is null";
                return false;
            }

            if (items.Count == 0)
            {
                reason = "item catalog is empty";
                return false;
            }

            if (affixes == null)
            {
                reason = "affix catalog is null (pass an empty list for a Common-only authority)";
                return false;
            }

            Dictionary<string, ItemData> itemIndex = new Dictionary<string, ItemData>(StringComparer.Ordinal);
            List<ItemData> itemOrder = new List<ItemData>(items.Count);

            for (int i = 0; i < items.Count; i++)
            {
                ItemData item = items[i];
                if (item == null)
                {
                    reason = $"item catalog entry {i} is null";
                    return false;
                }

                if (!ItemIdentityRules.IsStableItemId(item.itemId, out reason))
                {
                    reason = $"item catalog entry {i}: {reason}";
                    return false;
                }

                if (itemIndex.ContainsKey(item.itemId))
                {
                    reason = $"item catalog entry {i}: duplicate item id '{item.itemId}'";
                    return false;
                }

                itemIndex[item.itemId] = item;
                itemOrder.Add(item);
            }

            if (affixes.Count > 0 && !AffixCatalog.TryValidate(affixes, out reason))
            {
                reason = $"affix catalog is invalid: {reason}";
                return false;
            }

            Dictionary<string, AffixDefinition> affixIndex = new Dictionary<string, AffixDefinition>(StringComparer.Ordinal);
            List<AffixDefinition> affixOrder = new List<AffixDefinition>(affixes.Count);

            for (int i = 0; i < affixes.Count; i++)
            {
                AffixDefinition affix = affixes[i];
                if (affix == null)
                {
                    reason = $"affix catalog entry {i} is null";
                    return false;
                }

                if (affixIndex.ContainsKey(affix.id))
                {
                    reason = $"affix catalog entry {i}: duplicate affix id '{affix.id}'";
                    return false;
                }

                affixIndex[affix.id] = affix;
                affixOrder.Add(affix);
            }

            authority = new ItemInstanceAuthority(itemIndex, affixIndex, itemOrder, affixOrder,
                categoryResolver ?? ItemCategoryRules.DefaultResolver);
            reason = null;
            return true;
        }

        public static bool TryCreate(IReadOnlyList<ItemData> items, IReadOnlyList<AffixDefinition> affixes,
            out ItemInstanceAuthority authority, out string reason)
        {
            return TryCreate(items, affixes, null, out authority, out reason);
        }

        public int ItemCount => _items.Count;
        public int AffixCount => _affixes.Count;

        /// <summary>Read-only views of what was accepted, in the order it was supplied.</summary>
        public IReadOnlyList<ItemData> Items => _itemOrder;
        public IReadOnlyList<AffixDefinition> AffixDefinitions => _affixOrder;

        public bool HasItem(string itemId)
        {
            return !string.IsNullOrEmpty(itemId) && _items.ContainsKey(itemId);
        }

        public bool HasAffix(string affixId)
        {
            return !string.IsNullOrEmpty(affixId) && _affixes.ContainsKey(affixId);
        }

        /// <summary>
        /// Exact ordinal lookup by the id a snapshot names. An unknown, empty or malformed id is a refusal
        /// with a reason and a null definition - never a nearest match, a prefix match or a case-insensitive
        /// one.
        /// </summary>
        public bool TryResolveItem(string itemId, out ItemData definition, out string reason)
        {
            definition = null;

            if (!ItemIdentityRules.IsStableItemId(itemId, out reason))
            {
                reason = $"cannot resolve an item: {reason}";
                return false;
            }

            if (!_items.TryGetValue(itemId, out definition))
            {
                reason = $"unknown item id '{itemId}': it is not in the trusted item catalog";
                return false;
            }

            reason = null;
            return true;
        }

        public bool TryResolveAffix(string affixId, out AffixDefinition definition, out string reason)
        {
            definition = null;

            if (!AffixDefinition.IsStableId(affixId))
            {
                reason = $"affix id '{affixId}' is not a stable affix id";
                return false;
            }

            if (!_affixes.TryGetValue(affixId, out definition))
            {
                reason = $"unknown affix id '{affixId}': it is not in the trusted affix catalog";
                return false;
            }

            reason = null;
            return true;
        }

        /// <summary>
        /// The authoritative gear category of one definition, from the resolver this authority was built
        /// with. A resolver that reports a failure is a failure; a resolver that returns a multi-category or
        /// unknown set is reported by the instance validation, not narrowed here.
        /// </summary>
        public bool TryCategory(ItemData definition, out AffixCategory category, out string reason)
        {
            return _categoryResolver(definition, out category, out reason);
        }
    }
}
