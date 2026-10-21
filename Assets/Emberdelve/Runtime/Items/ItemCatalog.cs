using System;
using System.Collections.Generic;
using UnityEngine;
using Emberdelve.Data;

namespace Emberdelve.Items
{
    /// <summary>
    /// R3: the ONE runtime item catalog. A ScriptableObject asset (Assets/Emberdelve/Data/EmberdelveItemCatalog.asset,
    /// referenced from <see cref="Emberdelve.Core.GameConfig.itemCatalog"/>) that lists every authored
    /// <see cref="ItemData"/> (weapons, armour, cloaks, boots, shield, potions, coins, relics) plus the affix
    /// definitions gear may roll, populated idempotently by the editor asset library.
    ///
    /// What it owns:
    /// <list type="bullet">
    /// <item>lookup of a definition by stable item id (<see cref="ItemIdentityRules.IsStableItemId"/> rules);</item>
    /// <item>the single <see cref="ItemInstanceAuthority"/> every minted or restored instance is validated
    /// against, built once and cached (<see cref="TryGetAuthority"/>);</item>
    /// <item>the only door that mints a Common gear identity for the bag (<see cref="TryMintCommon"/>), which
    /// refuses any definition object that is not a catalog entry;</item>
    /// <item>snapshot restore by id (<see cref="TryRestore"/>, <see cref="TryRestoreAll"/>), atomic: an unknown
    /// id, a malformed record or a duplicate UID refuses the whole batch and produces nothing.</item>
    /// </list>
    ///
    /// Validation (<see cref="Validate"/>): null references, malformed or duplicate item ids, an invalid affix
    /// catalog, a gear category that cannot be resolved, and - for a production catalog - equippable
    /// (rarity-capable) gear with no affix catalog at all are all build errors with a reason. The shared
    /// definition assets are never modified; the catalog is an index over them.
    ///
    /// Nothing at runtime can add to an asset-backed catalog. Tests that need ad-hoc definitions build an
    /// in-memory catalog through <see cref="CreateForTest"/> and extend it with <see cref="RegisterForTest"/>,
    /// which refuses on any catalog that was not created for tests - the production rule is never weakened.
    /// This is this process's own data, not a network authority: a server (U3/U4) still owns minting.
    /// </summary>
    [CreateAssetMenu(menuName = "Emberdelve/09 Item Catalog", fileName = "EmberdelveItemCatalog")]
    public sealed class ItemCatalog : ScriptableObject
    {
        [Tooltip("Every ItemData asset in the project. Filled by the asset library; never edited at runtime.")]
        [SerializeField] private ItemData[] items = new ItemData[0];

        [Tooltip("The affix definitions rolled gear may carry. Required whenever the catalog lists equippable gear.")]
        [SerializeField] private AffixDefinition[] affixes = new AffixDefinition[0];

        [NonSerialized] private ItemInstanceAuthority _authority;
        [NonSerialized] private string _failure;
        [NonSerialized] private bool _evaluated;
        [NonSerialized] private bool _testOnly;

        public IReadOnlyList<ItemData> Items => items ?? new ItemData[0];
        public IReadOnlyList<AffixDefinition> Affixes => affixes ?? new AffixDefinition[0];
        public int ItemCount => items != null ? items.Length : 0;
        public int AffixCount => affixes != null ? affixes.Length : 0;

        /// <summary>True for an in-memory catalog made by <see cref="CreateForTest"/>. Never true for an asset.</summary>
        public bool IsTestCatalog => _testOnly;

        private void OnEnable()
        {
            Invalidate();
        }

        private void OnValidate()
        {
            Invalidate();
        }

        /// <summary>Drops the cached authority so the next use re-validates the current lists.</summary>
        public void Invalidate()
        {
            _authority = null;
            _failure = null;
            _evaluated = false;
        }

        // ------------------------------------------------------------------ validation

        /// <summary>
        /// The build-time validation, usable on any lists without an asset (that is how a gate proves each
        /// refusal). On success the out authority is the one index every lookup, mint and restore uses.
        /// <paramref name="requireAffixesForGear"/> is the production rule: a catalog that lists equippable
        /// gear must also list affix definitions, because gear is rarity-capable and a catalog that cannot
        /// roll or validate affixes would silently mint only Common stand-ins.
        /// </summary>
        public static bool Validate(IReadOnlyList<ItemData> itemList, IReadOnlyList<AffixDefinition> affixList,
            bool requireAffixesForGear, out ItemInstanceAuthority authority, out string reason)
        {
            authority = null;

            if (itemList == null)
            {
                reason = "item catalog list is null";
                return false;
            }

            if (affixList == null)
            {
                reason = "affix catalog list is null";
                return false;
            }

            // Null refs, malformed ids, duplicate ids, an invalid affix catalog: the authority is already the
            // strict, atomic index builder for all of those.
            if (!ItemInstanceAuthority.TryCreate(itemList, affixList, out ItemInstanceAuthority built, out reason))
            {
                return false;
            }

            for (int i = 0; i < built.Items.Count; i++)
            {
                ItemData item = built.Items[i];
                if (!item.IsEquippable) continue;

                if (requireAffixesForGear && affixList.Count == 0)
                {
                    reason = $"item '{item.itemId}' is rarity-capable gear but the catalog has no affix catalog";
                    return false;
                }

                if (!built.TryCategory(item, out _, out string categoryReason))
                {
                    reason = $"item '{item.itemId}' has no resolvable gear category: {categoryReason}";
                    return false;
                }
            }

            authority = built;
            reason = null;
            return true;
        }

        /// <summary>
        /// The one authority for this catalog, built once and cached (failures are cached too, so a broken
        /// catalog says the same thing every time). False with a null authority when the catalog is invalid.
        /// </summary>
        public bool TryGetAuthority(out ItemInstanceAuthority authority, out string reason)
        {
            if (!_evaluated)
            {
                _evaluated = true;
                if (!Validate(Items, Affixes, !_testOnly, out _authority, out _failure))
                {
                    _authority = null;
                }
            }

            authority = _authority;
            reason = _authority != null ? null : (_failure ?? "item catalog is not valid");
            return _authority != null;
        }

        /// <summary>The validated authority, or null when the catalog is invalid (see <see cref="TryGetAuthority"/>).</summary>
        public ItemInstanceAuthority Authority
        {
            get
            {
                TryGetAuthority(out ItemInstanceAuthority authority, out _);
                return authority;
            }
        }

        public bool IsValid(out string reason)
        {
            return TryGetAuthority(out _, out reason);
        }

        // ------------------------------------------------------------------ lookup

        /// <summary>True when this exact definition OBJECT is a catalog entry (an id match alone is not enough).</summary>
        public bool Contains(ItemData definition)
        {
            if (definition == null) return false;
            if (!TryGetAuthority(out ItemInstanceAuthority authority, out _)) return false;
            if (!authority.TryResolveItem(definition.itemId, out ItemData trusted, out _)) return false;
            return ReferenceEquals(trusted, definition);
        }

        /// <summary>Exact ordinal lookup by stable item id. Unknown or malformed ids are a refusal with a reason.</summary>
        public bool TryResolveItem(string itemId, out ItemData definition, out string reason)
        {
            definition = null;
            if (!TryGetAuthority(out ItemInstanceAuthority authority, out reason)) return false;
            return authority.TryResolveItem(itemId, out definition, out reason);
        }

        public bool TryResolveAffix(string affixId, out AffixDefinition definition, out string reason)
        {
            definition = null;
            if (!TryGetAuthority(out ItemInstanceAuthority authority, out reason)) return false;
            return authority.TryResolveAffix(affixId, out definition, out reason);
        }

        // ------------------------------------------------------------------ minting

        /// <summary>
        /// Cheap pre-check the bag runs before it touches any state: could this definition be given a gear
        /// identity at all? True only for an equippable definition that is a catalog entry.
        /// </summary>
        public bool CanMint(ItemData definition, out string reason)
        {
            if (definition == null)
            {
                reason = "no item definition to give an identity";
                return false;
            }

            if (!TryGetAuthority(out _, out reason))
            {
                reason = $"the item catalog is not usable: {reason}";
                return false;
            }

            if (!definition.IsEquippable)
            {
                reason = $"{definition.itemId} is carried rather than worn, so it has no gear identity to mint";
                return false;
            }

            if (!Contains(definition))
            {
                reason = $"'{definition.itemId}' is not an entry of the item catalog (the supplied definition " +
                         "object is not the one the catalog owns for that id), so no identity is minted for it";
                return false;
            }

            reason = null;
            return true;
        }

        /// <summary>
        /// Mints one explicit Common instance (quantity 1) of a catalog gear entry through the supplied issuer.
        /// A definition that is not a catalog entry is refused with a reason and nothing is minted.
        /// </summary>
        public bool TryMintCommon(ItemIdentityIssuer issuer, ItemData definition, out ItemInstance instance,
            out string reason)
        {
            instance = null;

            if (issuer == null)
            {
                reason = "no identity issuer was supplied";
                return false;
            }

            if (!CanMint(definition, out reason)) return false;

            return issuer.TryCreateInstance(definition, 1, AffixRarity.Common, null, Authority,
                out instance, out reason);
        }

        /// <summary>
        /// A rolling authority for one container (a loot table): the table's own item and affix subsets, but
        /// every member must be this catalog's own object. A definition or affix that is not in the catalog is
        /// refused, so a table can restrict what it rolls but can never widen the set of items that exist.
        /// Repeated references to one object are listed once.
        /// </summary>
        public bool TryDeriveAuthority(IReadOnlyList<ItemData> subsetItems, IReadOnlyList<AffixDefinition> subsetAffixes,
            out ItemInstanceAuthority authority, out string reason)
        {
            authority = null;

            if (!TryGetAuthority(out ItemInstanceAuthority root, out reason))
            {
                reason = $"the item catalog is not usable: {reason}";
                return false;
            }

            List<ItemData> itemList = new List<ItemData>();
            if (subsetItems != null)
            {
                for (int i = 0; i < subsetItems.Count; i++)
                {
                    ItemData item = subsetItems[i];
                    if (item == null)
                    {
                        reason = $"derived item list entry {i} is null";
                        return false;
                    }

                    if (!Contains(item))
                    {
                        reason = $"'{item.itemId}' is not an entry of the item catalog";
                        return false;
                    }

                    if (!itemList.Contains(item)) itemList.Add(item);
                }
            }

            List<AffixDefinition> affixList = new List<AffixDefinition>();
            if (subsetAffixes != null)
            {
                for (int i = 0; i < subsetAffixes.Count; i++)
                {
                    AffixDefinition affix = subsetAffixes[i];
                    if (affix == null)
                    {
                        reason = $"derived affix list entry {i} is null";
                        return false;
                    }

                    if (!root.TryResolveAffix(affix.id, out AffixDefinition owned, out _) || !ReferenceEquals(owned, affix))
                    {
                        reason = $"affix '{affix.id}' is not an entry of the item catalog's affix catalog";
                        return false;
                    }

                    if (!affixList.Contains(affix)) affixList.Add(affix);
                }
            }

            if (itemList.Count == 0)
            {
                reason = "derived authority needs at least one catalog item";
                return false;
            }

            return ItemInstanceAuthority.TryCreate(itemList, affixList, out authority, out reason);
        }

        // ------------------------------------------------------------------ restore

        /// <summary>
        /// Restore one snapshot against this catalog: the definition and affix definitions are resolved by id
        /// through the catalog's authority, the UID is read and never minted. Any problem (unknown item id
        /// included) refuses with a null instance.
        /// </summary>
        public bool TryRestore(ItemSnapshot snapshot, out ItemInstance instance, out string reason)
        {
            instance = null;
            if (!TryGetAuthority(out ItemInstanceAuthority authority, out reason))
            {
                reason = $"the item catalog is not usable: {reason}";
                return false;
            }

            return ItemInstance.TryRestore(snapshot, authority, out instance, out reason);
        }

        /// <summary>
        /// Atomic batch restore: either every snapshot restores and none repeats a UID, or the result list is
        /// null and one reason names the first offender. Nothing is produced for a partly valid batch.
        /// </summary>
        public bool TryRestoreAll(IReadOnlyList<ItemSnapshot> snapshots, out List<ItemInstance> restored,
            out string reason)
        {
            restored = null;

            if (snapshots == null)
            {
                reason = "no snapshots to restore";
                return false;
            }

            List<ItemInstance> staged = new List<ItemInstance>(snapshots.Count);
            HashSet<Guid> seen = new HashSet<Guid>();

            for (int i = 0; i < snapshots.Count; i++)
            {
                if (!TryRestore(snapshots[i], out ItemInstance instance, out string one))
                {
                    reason = $"snapshot {i} refused: {one}";
                    return false;
                }

                if (!seen.Add(instance.Uid))
                {
                    reason = $"snapshot {i} repeats uid {instance.UidText}: one uid is one item";
                    return false;
                }

                staged.Add(instance);
            }

            restored = staged;
            reason = null;
            return true;
        }

        // ------------------------------------------------------------------ test seam

        /// <summary>
        /// TEST-ONLY seam. Builds an in-memory catalog (never an asset, never saved) from explicit lists. It is
        /// validated lazily like any catalog, except that gear without an affix catalog is allowed, because a
        /// fixture may only need Common gear. Add more ad-hoc definitions with <see cref="RegisterForTest"/>.
        ///
        /// Internal: the editor assembly sees it through InternalsVisibleTo (declared in LocalCombatAuthority.cs).
        /// There is no asmdef split, so within Assembly-CSharp any runtime code could still call it; the R3 gate's
        /// production source scan fails if one does.
        /// </summary>
        internal static ItemCatalog CreateForTest(IEnumerable<ItemData> testItems, IEnumerable<AffixDefinition> testAffixes)
        {
            ItemCatalog catalog = CreateInstance<ItemCatalog>();
            catalog.hideFlags = HideFlags.HideAndDontSave;
            catalog._testOnly = true;
            catalog.items = testItems != null ? new List<ItemData>(testItems).ToArray() : new ItemData[0];
            catalog.affixes = testAffixes != null ? new List<AffixDefinition>(testAffixes).ToArray() : new AffixDefinition[0];
            catalog.Invalidate();
            return catalog;
        }

        /// <summary>
        /// TEST-ONLY seam: add one ad-hoc definition to a catalog made by <see cref="CreateForTest"/>. Refused
        /// (and nothing changes) on a production/asset catalog, for a null definition, for a malformed id and
        /// for a second, different object that shares an id already in the catalog. Registering the same object
        /// twice is a harmless no-op.
        /// </summary>
        internal bool RegisterForTest(ItemData definition, out string reason)
        {
            if (!_testOnly)
            {
                reason = "RegisterForTest is only allowed on a catalog created by CreateForTest: a production " +
                         "item catalog cannot be extended at runtime";
                return false;
            }

            if (definition == null)
            {
                reason = "no item definition to register";
                return false;
            }

            if (Array.IndexOf(items, definition) >= 0)
            {
                reason = null;
                return true;
            }

            ItemData[] candidate = new ItemData[items.Length + 1];
            Array.Copy(items, candidate, items.Length);
            candidate[items.Length] = definition;

            if (!Validate(candidate, Affixes, false, out _, out reason)) return false;

            items = candidate;
            Invalidate();
            reason = null;
            return true;
        }

        internal bool RegisterForTest(ItemData definition)
        {
            return RegisterForTest(definition, out _);
        }

#if UNITY_EDITOR
        /// <summary>
        /// Editor builder entry point: writes the lists only when they differ from what is stored, so running the
        /// builder twice leaves the asset byte-identical. Returns true when anything changed.
        /// </summary>
        public bool EditorSetContents(ItemData[] newItems, AffixDefinition[] newAffixes)
        {
            bool same = SameReferences(items, newItems) && SameReferences(affixes, newAffixes);
            if (same) return false;

            items = newItems ?? new ItemData[0];
            affixes = newAffixes ?? new AffixDefinition[0];
            Invalidate();
            return true;
        }

        private static bool SameReferences<T>(T[] a, T[] b) where T : UnityEngine.Object
        {
            int lengthA = a != null ? a.Length : 0;
            int lengthB = b != null ? b.Length : 0;
            if (lengthA != lengthB) return false;
            for (int i = 0; i < lengthA; i++)
            {
                if (!ReferenceEquals(a[i], b[i])) return false;
            }
            return true;
        }
#endif
    }
}
