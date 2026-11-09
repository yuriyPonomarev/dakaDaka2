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
        public string ItemId => _item != null ? _item.itemId : string.Empty;
        public int Quantity => _item != null ? _item.quantity : 0;
        public bool IsGear => _item != null && !string.IsNullOrEmpty(_item.uid);
        public string Uid => _item != null ? _item.uid : string.Empty;

        internal CorpseItemView(ulong entryId, ItemSnapshot item)
        {
            EntryId = entryId;
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
        }

        private readonly List<Entry> _entries = new List<Entry>();
        private ulong _nextEntryId = 1;
        private bool _busy;

        public int SourcePlayerId { get; }
        public Vector3 WorldPosition { get; }
        public ulong Revision { get; private set; }
        public int EntryCount => _entries.Count;

        private CorpseContainerModel(int sourcePlayerId, Vector3 worldPosition)
        {
            SourcePlayerId = sourcePlayerId;
            WorldPosition = worldPosition;
        }

        public CorpseItemView[] CopyEntries()
        {
            var result = new CorpseItemView[_entries.Count];
            for (int i = 0; i < _entries.Count; i++) result[i] = new CorpseItemView(_entries[i].Id, _entries[i].Item);
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
            item = new CorpseItemView(_entries[index].Id, _entries[index].Item);
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

            var created = new CorpseContainerModel(manifest.SourcePlayerId, manifest.WorldPosition);
            if (!AppendEntries(created, loadout.bag.entries, out reason) ||
                !AppendEntries(created, loadout.equipment.entries, out reason)) return false;

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
            Inventory recipient, ulong expectedBagRevision, ItemUidRegistry profileUidRegistry, out string reason)
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
            if (ordinaryStack)
            {
                int stackLimit = definition.StackLimitForFootprint;
                for (int i = 0; i < nextDestination.entries.Length && remainingToPlace > 0; i++)
                {
                    ItemSnapshot existing = nextDestination.entries[i]?.item;
                    if (existing == null || !string.IsNullOrEmpty(existing.uid) ||
                        !string.Equals(existing.itemId, offered.itemId, StringComparison.Ordinal)) continue;
                    int space = Math.Max(0, stackLimit - existing.quantity);
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
                if (!TryFindFreeOrigin(destination, recipient.Catalog, definition, out Vector2Int origin, out reason)) return false;

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
                model._entries.Add(new Entry { Id = model._nextEntryId++, Item = entry.item.Clone() });
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
