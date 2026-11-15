using System;
using System.Collections.Generic;
using Emberdelve.Core;
using Emberdelve.Data;
using UnityEngine;

namespace Emberdelve.Items
{
    /// <summary>A detached, read-only view of one item row held by a corpse.</summary>
    public readonly struct CorpseItemView
    {
        private readonly ItemSnapshot _item;

        public ulong EntryId { get; }
        public string Slot { get; }
        public int X { get; }
        public int Y { get; }
        public string ItemId => _item != null ? _item.itemId : string.Empty;
        public int Quantity => _item != null ? _item.quantity : 0;
        public bool IsGear => _item != null && !string.IsNullOrEmpty(_item.uid);
        public string Uid => _item != null ? _item.uid : string.Empty;

        internal CorpseItemView(ulong entryId, ItemSnapshot item, string slot, int x, int y)
        {
            EntryId = entryId;
            Slot = slot; X = x; Y = y;
            _item = item;
        }

        public ItemSnapshot CopySnapshot() => _item != null ? _item.Clone() : null;
    }

    /// <summary>
    /// Mutable server-side contents for one corpse. It is created from a sealed manifest and transfers one stack
    /// (or part of a carried stack) into an existing bag at a time. All Unity-facing mutations run on the main
    /// thread; expected revisions reject stale requests and the busy latch blocks synchronous event re-entry.
    /// </summary>
    public sealed class CorpseContainerModel
    {
        private sealed class Entry
        {
            public ulong Id;
            public ItemSnapshot Item;
            public string Slot;
            public int X, Y;
        }

        private readonly List<Entry> _entries = new List<Entry>();
        private ulong _nextEntryId = 1;
        private bool _busy;

        public int SourcePlayerId { get; }
        public string Kind { get; private set; } = "player";
        public string Title { get; private set; } = "FALLEN ADVENTURER";
        public int Width { get; private set; } = 10;
        public int Height { get; private set; } = 4;
        public Vector3 WorldPosition { get; }
        public ulong Revision { get; private set; }
        public int EntryCount => _entries.Count;

        private CorpseContainerModel(int sourcePlayerId, Vector3 worldPosition)
        {
            SourcePlayerId = sourcePlayerId;
            WorldPosition = worldPosition;
        }

        private static CorpseItemView View(Entry e) => new CorpseItemView(e.Id, e.Item, e.Slot, e.X, e.Y);

        public CorpseItemView[] CopyEntries()
        {
            var result = new CorpseItemView[_entries.Count];
            for (int i = 0; i < _entries.Count; i++) result[i] = View(_entries[i]);
            return result;
        }

        public bool TryGetEntry(ulong entryId, out CorpseItemView item)
        {
            int index = IndexOf(entryId);
            if (index < 0)
            {
                item = default;
                return false;
            }
            item = View(_entries[index]);
            return true;
        }

        public static bool TryCreate(CorpseLootManifest manifest, out CorpseContainerModel model, out string reason)
        {
            model = null;
            if (manifest == null)
            {
                reason = "corpse container needs a captured loadout";
                return false;
            }

            LoadoutSnapshot loadout = manifest.CopyLoadout();
            if (loadout == null || !loadout.ChecksumMatches() || loadout.bag == null || loadout.equipment == null ||
                !loadout.bag.ChecksumMatches() || !loadout.equipment.ChecksumMatches() ||
                loadout.bag.entries == null || loadout.equipment.entries == null)
            {
                reason = "corpse container loadout is incomplete or has an invalid checksum";
                return false;
            }

            var created = new CorpseContainerModel(manifest.SourcePlayerId, manifest.WorldPosition)
            { Width = loadout.bag.width, Height = loadout.bag.height };
            if (!AppendPlayerBagEntries(created, manifest, loadout.bag, out reason) ||
                !AppendEntries(created, loadout.equipment.entries, out reason)) return false;

            model = created;
            reason = null;
            return true;
        }

        /// <summary>Copies a trusted server roll; the caller releases its source only after registry admission.</summary>
        public static bool TryCreateLoot(string kind, string title, Vector3 position,
            IReadOnlyList<LootDrop> drops, ItemCatalog catalog, out CorpseContainerModel model, out string reason)
        {
            model = null;
            reason = "Invalid loot container";
            if ((kind != "chest" && kind != "enemy") || drops == null || catalog == null ||
                drops.Count > ContainerSnapshot.MaxEntries || !float.IsFinite(position.x) ||
                !float.IsFinite(position.y) || !float.IsFinite(position.z)) return false;
            var created = new CorpseContainerModel(0, position) { Kind = kind, Title = title ?? "LOOT", Height = 4 };
            var packed = new ContainerSnapshot { width = 10, height = 64, entries = Array.Empty<ContainerEntry>() };
            var rows = new List<ContainerEntry>();
            var uids = new HashSet<string>();
            for (int i = 0; i < drops.Count; i++)
            {
                LootDrop drop = drops[i];
                if (drop.Item == null || drop.Amount <= 0) return false;
                ItemSnapshot item = drop.Instance != null ? drop.Instance.ToSnapshot() : new ItemSnapshot
                { uid = "", itemId = drop.Item.itemId, quantity = drop.Amount, rarity = 0, affixes = Array.Empty<AffixSnapshot>() };
                if (drop.Instance != null && (drop.Amount != 1 || !uids.Add(item.uid))) return false;
                if (!catalog.TryResolveItem(item.itemId, out ItemData definition, out reason)) return false;
                if (drop.Instance != null && !ItemInstance.TryRestore(item, catalog.Authority, out _, out reason)) return false;
                if (drop.Instance == null && definition.IsEquippable) return false;
                int remaining = item.quantity;
                while (remaining > 0)
                {
                    if (rows.Count >= ContainerSnapshot.MaxEntries) return false;
                    if (!TryFindFreeOrigin(packed, catalog, definition, out Vector2Int origin, out reason)) return false;
                    ItemSnapshot part = item.Clone();
                    part.quantity = Mathf.Min(remaining, definition.StackLimitForFootprint);
                    remaining -= part.quantity;
                    rows.Add(new ContainerEntry { x = origin.x, y = origin.y, item = part });
                    packed.entries = rows.ToArray();
                    created.Height = Mathf.Max(created.Height, origin.y + definition.EffectiveFootprint.y);
                }
            }
            if (!AppendEntries(created, rows.ToArray(), out reason)) return false;
            model = created;
            reason = null;
            return true;
        }

        /// <summary>
        /// Transfers exactly <paramref name="amount"/> from one corpse row into the recipient's existing bag.
        /// The corpse row is changed only after the target snapshot, bag capacity, expected revisions and profile
        /// UID registry all pass and Inventory commits the new bag atomically.
        /// </summary>
        public bool TryTransferToBag(ulong entryId, int amount, ulong expectedCorpseRevision,
            Inventory recipient, ulong expectedBagRevision, ItemUidRegistry profileUidRegistry, out string reason, Vector2Int? destinationCell = null)
        {
            if (_busy)
            {
                reason = "corpse contents are already being transferred";
                return false;
            }
            if (expectedCorpseRevision != Revision)
            {
                reason = "corpse contents changed; refresh the container";
                return false;
            }
            if (recipient == null || profileUidRegistry == null)
            {
                reason = "corpse transfer needs a recipient bag and its profile UID registry";
                return false;
            }
            if (recipient.Revision != expectedBagRevision)
            {
                reason = "recipient bag changed; refresh the inventory";
                return false;
            }
            if (amount <= 0)
            {
                reason = "transfer amount must be positive";
                return false;
            }
            if (Revision == ulong.MaxValue)
            {
                reason = "corpse revision space is exhausted";
                return false;
            }
            if (!recipient.CanCommitRevision(out reason)) return false;

            int sourceIndex = IndexOf(entryId);
            if (sourceIndex < 0)
            {
                reason = "corpse item is no longer available";
                return false;
            }

            Entry source = _entries[sourceIndex];
            ItemSnapshot offered = source.Item;
            if (offered == null)
            {
                reason = "corpse item row is invalid";
                return false;
            }
            if (recipient.Catalog == null)
            {
                reason = "recipient bag has no item catalog";
                return false;
            }
            if (!recipient.Catalog.TryResolveItem(offered.itemId, out ItemData definition, out reason))
            {
                return false;
            }
            if (amount > offered.quantity || (definition.IsEquippable && amount != 1) ||
                amount > definition.StackLimitForFootprint)
            {
                reason = "transfer amount does not fit the corpse item stack";
                return false;
            }
            if (amount < offered.quantity && definition.IsEquippable)
            {
                reason = "a gear instance cannot be split";
                return false;
            }

            ContainerSnapshot destination = recipient.ToContainerSnapshot();
            ContainerSnapshot nextDestination = destination.Clone();
            int remainingToPlace = amount;
            bool ordinaryStack = string.IsNullOrEmpty(offered.uid) && !definition.IsEquippable;
            if (ordinaryStack && !destinationCell.HasValue)
            {
                int stackLimit = definition.StackLimitForFootprint;
                for (int i = 0; i < nextDestination.entries.Length && remainingToPlace > 0; i++)
                {
                    ItemSnapshot existing = nextDestination.entries[i]?.item;
                    if (existing == null || !string.IsNullOrEmpty(existing.uid) ||
                        !string.Equals(existing.itemId, offered.itemId, StringComparison.Ordinal)) continue;
                    var targetEntry = nextDestination.entries[i];
                    int targetLimit = QuickBeltRules.TryGetSlot(new Vector2Int(targetEntry.x, targetEntry.y), destination.width, out _)
                        ? QuickBeltRules.StackLimit(definition) : stackLimit;
                    int space = Math.Max(0, targetLimit - existing.quantity);
                    int moved = Math.Min(space, remainingToPlace);
                    existing.quantity += moved;
                    remainingToPlace -= moved;
                }
            }

            if (remainingToPlace > 0)
            {
                if (destination.entries.Length >= ContainerSnapshot.MaxEntries)
                {
                    reason = "recipient bag has reached its item-entry limit";
                    return false;
                }
                Vector2Int origin;
                if (destinationCell.HasValue)
                {
                    origin = destinationCell.Value;
                    bool belt = recipient.IsBeltCell(origin);
                    if ((belt && (!QuickBeltRules.CanStore(definition) || remainingToPlace > QuickBeltRules.StackLimit(definition))) ||
                        !recipient.CanPlace(origin, belt ? Vector2Int.one : definition.EffectiveFootprint))
                    {
                        reason = "That item does not fit in the selected cells";
                        return false;
                    }
                }
                else if (!TryFindFreeOrigin(destination, recipient.Catalog, definition, out origin, out reason)) return false;

                Array.Resize(ref nextDestination.entries, nextDestination.entries.Length + 1);
                ItemSnapshot delivered = offered.Clone();
                delivered.quantity = remainingToPlace;
                nextDestination.entries[nextDestination.entries.Length - 1] = new ContainerEntry
                {
                    slot = string.Empty,
                    x = origin.x,
                    y = origin.y,
                    item = delivered
                };
            }
            nextDestination.Seal();

            ItemSnapshot remainder = null;
            if (amount < offered.quantity)
            {
                remainder = offered.Clone();
                remainder.quantity -= amount;
            }

            // Keep the source locked until deferred inventory observers have run. A synchronous retry from an
            // InventoryChanged callback therefore sees either the old state or the fully committed new state.
            _busy = true;
            try
            {
                using (GameEventTransaction.Defer())
                {
                    if (recipient.Revision != expectedBagRevision)
                    {
                        reason = "recipient bag changed before transfer commit";
                        return false;
                    }
                    if (!recipient.TryRestoreLoot(nextDestination, profileUidRegistry, definition, amount, out reason)) return false;

                    if (remainder == null) _entries.RemoveAt(sourceIndex);
                    else source.Item = remainder;
                    Revision++;
                }
            }
            finally
            {
                _busy = false;
            }

            reason = null;
            return true;
        }

        private int IndexOf(ulong entryId)
        {
            for (int i = 0; i < _entries.Count; i++)
                if (_entries[i].Id == entryId) return i;
            return -1;
        }

        // Player belt slots are not bag cells. Repack them after the original bag pages so they
        // remain visible and lootable without moving or overlapping the deceased player's bag items.
        private static bool AppendPlayerBagEntries(CorpseContainerModel model, CorpseLootManifest manifest,
            ContainerSnapshot bag, out string reason)
        {
            var rows = new List<ContainerEntry>();
            var utility = new List<ContainerEntry>();
            foreach (var entry in bag.entries)
            {
                if (entry == null || entry.item == null) { reason = "corpse bag has an empty row"; return false; }
                if (QuickBeltRules.TryGetSlot(new Vector2Int(entry.x, entry.y), bag.width, out _)) utility.Add(entry);
                else rows.Add(entry);
            }
            if (utility.Count > 0)
            {
                int firstRow = ((bag.height + 4) / 5) * 5;
                var occupied = new bool[bag.width, ContainerSnapshot.MaxGridDimension];
                foreach (var entry in utility)
                {
                    if (!manifest.TryGetCapturedFootprint(entry.x, entry.y, out var size) ||
                        size.x < 1 || size.x > bag.width || size.y < 1 || size.y > 5)
                    { reason = "corpse belt item has no displayable footprint"; return false; }
                    bool placed = false;
                    for (int y = firstRow; y + size.y <= occupied.GetLength(1) && !placed; y++)
                    {
                        if (y % 5 + size.y > 5) continue; // Never split a visible item across UI pages.
                        for (int x = 0; x + size.x <= bag.width; x++)
                        {
                            if (!MarkFootprint(occupied, x, y, size)) continue;
                            var packed = entry.Clone(); packed.x = x; packed.y = y; packed.slot = string.Empty;
                            rows.Add(packed); model.Height = Math.Max(model.Height, y + size.y); placed = true; break;
                        }
                    }
                    if (!placed) { reason = "corpse utility display exceeds container bounds"; return false; }
                }
            }
            return AppendEntries(model, rows.ToArray(), out reason);
        }

        private static bool AppendEntries(CorpseContainerModel model, ContainerEntry[] entries, out string reason)
        {
            for (int i = 0; i < entries.Length; i++)
            {
                ContainerEntry entry = entries[i];
                if (entry == null || entry.item == null || entry.item.quantity <= 0 || model._nextEntryId == 0)
                {
                    reason = "corpse loadout contains an invalid item row";
                    return false;
                }
                model._entries.Add(new Entry { Id = model._nextEntryId++, Item = entry.item.Clone(), Slot = entry.slot, X = entry.x, Y = entry.y });
            }
            reason = null;
            return true;
        }

        private static bool TryFindFreeOrigin(ContainerSnapshot bag, ItemCatalog catalog, ItemData incoming,
            out Vector2Int origin, out string reason)
        {
            origin = new Vector2Int(-1, -1);
            if (bag == null || catalog == null || incoming == null || bag.entries == null ||
                bag.width < 1 || bag.height < 1 || bag.width > ContainerSnapshot.MaxGridDimension ||
                bag.height > ContainerSnapshot.MaxGridDimension)
            {
                reason = "recipient bag grid or catalog is invalid";
                return false;
            }

            bool[,] occupied = new bool[bag.width, bag.height];
            for (int i = 0; i < bag.entries.Length; i++)
            {
                ContainerEntry entry = bag.entries[i];
                if (entry?.item == null)
                {
                    reason = "recipient bag contains an invalid row";
                    return false;
                }
                if (!catalog.TryResolveItem(entry.item.itemId, out ItemData existing, out reason))
                {
                    return false;
                }
                if (QuickBeltRules.TryGetSlot(new Vector2Int(entry.x, entry.y), bag.width, out _))
                {
                    if (!QuickBeltRules.CanStore(existing) || entry.item.quantity > QuickBeltRules.StackLimit(existing))
                    { reason = "recipient belt contains an invalid utility stack"; return false; }
                    continue;
                }
                Vector2Int size = existing.EffectiveFootprint;
                if (!MarkFootprint(occupied, entry.x, entry.y, size))
                {
                    reason = "recipient bag contains overlapping or out-of-bounds items";
                    return false;
                }
            }

            Vector2Int wanted = incoming.EffectiveFootprint;
            for (int y = 0; y + wanted.y <= bag.height; y++)
            for (int x = 0; x + wanted.x <= bag.width; x++)
            {
                bool free = true;
                for (int dy = 0; dy < wanted.y && free; dy++)
                for (int dx = 0; dx < wanted.x; dx++)
                    if (occupied[x + dx, y + dy]) { free = false; break; }
                if (!free) continue;
                origin = new Vector2Int(x, y);
                reason = null;
                return true;
            }

            reason = $"no {wanted.x}x{wanted.y} space for {incoming.displayName} in recipient bag";
            return false;
        }

        private static bool MarkFootprint(bool[,] occupied, int x, int y, Vector2Int size)
        {
            if (x < 0 || y < 0 || size.x < 1 || size.y < 1 ||
                x > occupied.GetLength(0) - size.x || y > occupied.GetLength(1) - size.y) return false;
            for (int dy = 0; dy < size.y; dy++)
            for (int dx = 0; dx < size.x; dx++)
            {
                if (occupied[x + dx, y + dy]) return false;
            }
            for (int dy = 0; dy < size.y; dy++)
            for (int dx = 0; dx < size.x; dx++) occupied[x + dx, y + dy] = true;
            return true;
        }
    }
}
