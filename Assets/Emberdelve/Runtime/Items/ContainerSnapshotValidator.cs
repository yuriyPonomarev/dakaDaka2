using System;
using System.Collections.Generic;
using UnityEngine;
using Emberdelve.Data;

namespace Emberdelve.Items
{
    /// <summary>One entry that passed validation: the catalog's own definition, the restored instance (gear) or null (stack), and where it goes.</summary>
    public sealed class ValidatedEntry
    {
        public ItemData Definition { get; internal set; }
        /// <summary>The restored gear instance (exact uid and rolls), or null for a carried stack.</summary>
        public ItemInstance Instance { get; internal set; }
        public int Quantity { get; internal set; }
        public Vector2Int Origin { get; internal set; }
        public EquipmentSlot Slot { get; internal set; }

        internal ValidatedEntry() { }
    }

    /// <summary>A snapshot that was fully validated, resolved against the catalog and is safe to commit. Restoring from it cannot fail.</summary>
    public sealed class ValidatedContainer
    {
        public string Kind { get; internal set; }
        public string ContainerId { get; internal set; }
        public int Width { get; internal set; }
        public int Height { get; internal set; }

        internal ValidatedContainer() { }
        public readonly List<ValidatedEntry> Entries = new List<ValidatedEntry>();
        public readonly List<Guid> Uids = new List<Guid>();
    }

    /// <summary>
    /// R9: the one place that decides whether a <see cref="ContainerSnapshot"/> is a legal container. All-or-nothing:
    /// the result is either a fully resolved <see cref="ValidatedContainer"/> or a null with ONE reason; nothing is
    /// mutated either way. Item and affix rules are not re-implemented here: gear goes through
    /// <see cref="ItemCatalog.TryRestore"/> (ItemInstance validation: uid, rarity/affix count, ids, keys, bands,
    /// steps, categories, quantity 1), carried stacks use <see cref="ItemInstance.TryValidateQuantity"/>. This class
    /// adds only what a container adds: version, kind, grid, checksum, placement/slot rules and uid uniqueness.
    /// </summary>
    public static class ContainerSnapshotValidator
    {
        /// <param name="others">uids held by OTHER containers of the same profile (may be null). A uid found there under a different owner is refused.</param>
        /// <param name="expectedKind">when not null, the snapshot must be exactly this kind.</param>
        public static bool TryValidate(ContainerSnapshot snapshot, ItemCatalog catalog, ItemUidRegistry others,
            string expectedKind, out ValidatedContainer validated, out string reason)
        {
            validated = null;

            if (snapshot == null)
            {
                reason = "no container snapshot";
                return false;
            }

            if (catalog == null)
            {
                reason = "no item catalog to validate the container against";
                return false;
            }

            if (!ContainerSchema.IsCurrent(snapshot.schemaVersion, "container snapshot", out reason)) return false;

            if (!ContainerKinds.IsKnown(snapshot.kind))
            {
                reason = $"container kind '{snapshot.kind}' is not one of bag, equipment, stash";
                return false;
            }

            if (expectedKind != null && snapshot.kind != expectedKind)
            {
                reason = $"expected a '{expectedKind}' container but the snapshot is a '{snapshot.kind}'";
                return false;
            }

            if (!ItemIdentityRules.IsStableItemId(snapshot.containerId, out string idReason))
            {
                reason = "container id is not a stable id: " + idReason;
                return false;
            }

            bool grid = ContainerKinds.IsGrid(snapshot.kind);
            if (grid)
            {
                if (snapshot.width < 1 || snapshot.height < 1 ||
                    snapshot.width > ContainerSnapshot.MaxGridDimension || snapshot.height > ContainerSnapshot.MaxGridDimension)
                {
                    reason = $"grid {snapshot.width}x{snapshot.height} is outside 1..{ContainerSnapshot.MaxGridDimension} per side";
                    return false;
                }
            }
            else if (snapshot.width != 0 || snapshot.height != 0)
            {
                reason = $"an equipment container has no grid, but it declares {snapshot.width}x{snapshot.height}";
                return false;
            }

            if (snapshot.entries == null)
            {
                reason = "container snapshot has no entries array";
                return false;
            }

            if (snapshot.entries.Length > ContainerSnapshot.MaxEntries)
            {
                reason = $"container snapshot has {snapshot.entries.Length} entries, above the limit of {ContainerSnapshot.MaxEntries}";
                return false;
            }

            if (string.IsNullOrEmpty(snapshot.checksum))
            {
                reason = "container snapshot has no checksum";
                return false;
            }

            if (!ContainerSnapshot.IsChecksumShape(snapshot.checksum))
            {
                reason = "container checksum is malformed (expected 'sha256:' and 64 lowercase hex digits)";
                return false;
            }

            if (!snapshot.ChecksumMatches())
            {
                reason = "container checksum does not match its content: the snapshot was changed after it was written";
                return false;
            }

            ValidatedContainer result = new ValidatedContainer
            {
                Kind = snapshot.kind,
                ContainerId = snapshot.containerId,
                Width = snapshot.width,
                Height = snapshot.height
            };

            bool[] occupied = grid ? new bool[snapshot.width * snapshot.height] : null;
            HashSet<Guid> seenUids = new HashSet<Guid>();
            HashSet<EquipmentSlot> seenSlots = new HashSet<EquipmentSlot>();

            for (int i = 0; i < snapshot.entries.Length; i++)
            {
                ContainerEntry entry = snapshot.entries[i];
                string at = $"entry {i}";

                if (entry == null)
                {
                    reason = $"{at} is null";
                    return false;
                }

                if (entry.item == null)
                {
                    reason = $"{at} has no item";
                    return false;
                }

                ItemSnapshot item = entry.item;

                if (!catalog.TryResolveItem(item.itemId, out ItemData definition, out string resolveReason))
                {
                    reason = $"{at}: unknown item: {resolveReason}";
                    return false;
                }

                ValidatedEntry validatedEntry = new ValidatedEntry { Definition = definition, Quantity = item.quantity };

                // ---- where it sits
                if (grid)
                {
                    if (!string.IsNullOrEmpty(entry.slot))
                    {
                        reason = $"{at}: a {snapshot.kind} entry sits in a cell, not in the equipment slot '{entry.slot}'";
                        return false;
                    }
                }
                else
                {
                    if (entry.x != 0 || entry.y != 0)
                    {
                        reason = $"{at}: an equipment entry has no cell, but it declares ({entry.x},{entry.y})";
                        return false;
                    }

                    if (!ContainerSnapshot.TryParseSlot(entry.slot, out EquipmentSlot slot))
                    {
                        reason = $"{at}: '{entry.slot}' is not an equipment slot name";
                        return false;
                    }

                    if (!seenSlots.Add(slot))
                    {
                        reason = $"{at}: equipment slot {slot} holds two items";
                        return false;
                    }

                    validatedEntry.Slot = slot;
                }

                // ---- what it is
                if (definition.IsEquippable)
                {
                    if (!grid && !EquipmentSlots.Accepts(definition.equipSlot, validatedEntry.Slot))
                    {
                        reason = $"{at}: {definition.itemId} belongs in slot {definition.equipSlot}, not {validatedEntry.Slot}";
                        return false;
                    }

                    // Everything about an instance (uid shape, rarity, affix count/ids/keys/bands/steps/category,
                    // quantity 1) is ItemInstance's rule, reached through the catalog.
                    if (!catalog.TryRestore(item, out ItemInstance instance, out string instanceReason))
                    {
                        reason = $"{at}: {instanceReason}";
                        return false;
                    }

                    if (!ReferenceEquals(instance.Definition, definition))
                    {
                        reason = $"{at}: restored definition does not match the catalog entry";
                        return false;
                    }

                    if (!seenUids.Add(instance.Uid))
                    {
                        reason = $"{at}: duplicate uid {instance.UidText} inside the snapshot: one uid is one item";
                        return false;
                    }

                    validatedEntry.Instance = instance;
                    validatedEntry.Quantity = 1;
                    result.Uids.Add(instance.Uid);
                }
                else
                {
                    if (!grid)
                    {
                        reason = $"{at}: {definition.itemId} is not wearable, so it cannot be in an equipment slot";
                        return false;
                    }

                    if (!string.IsNullOrEmpty(item.uid))
                    {
                        reason = $"{at}: {definition.itemId} is a carried stack and has no uid, but it carries '{item.uid}'";
                        return false;
                    }

                    if (item.rarity != 0)
                    {
                        reason = $"{at}: a carried stack has no rarity, but it declares {item.rarity}";
                        return false;
                    }

                    if (item.affixes != null && item.affixes.Length != 0)
                    {
                        reason = $"{at}: a carried stack has no affixes, but it declares {item.affixes.Length}";
                        return false;
                    }

                    if (!ItemInstance.TryValidateQuantity(definition, item.quantity, out string quantityReason))
                    {
                        reason = $"{at}: {quantityReason}";
                        return false;
                    }
                }

                // ---- grid placement
                if (grid)
                {
                    Vector2Int size = definition.EffectiveFootprint;
                    if (entry.x < 0 || entry.y < 0 || entry.x > snapshot.width - size.x || entry.y > snapshot.height - size.y)
                    {
                        reason = $"{at}: {definition.itemId} ({size.x}x{size.y}) at cell ({entry.x},{entry.y}) is outside the {snapshot.width}x{snapshot.height} grid";
                        return false;
                    }

                    for (int yy = entry.y; yy < entry.y + size.y; yy++)
                    {
                        for (int xx = entry.x; xx < entry.x + size.x; xx++)
                        {
                            int cell = yy * snapshot.width + xx;
                            if (occupied[cell])
                            {
                                reason = $"{at}: {definition.itemId} at ({entry.x},{entry.y}) overlaps another item at cell ({xx},{yy})";
                                return false;
                            }
                            occupied[cell] = true;
                        }
                    }

                    validatedEntry.Origin = new Vector2Int(entry.x, entry.y);
                }

                result.Entries.Add(validatedEntry);
            }

            // ---- canonical order: one container, one canonical entry order
            for (int i = 1; i < snapshot.entries.Length; i++)
            {
                if (ContainerSnapshot.CompareEntries(snapshot.entries[i - 1], snapshot.entries[i], grid) >= 0)
                {
                    reason = $"entries {i - 1} and {i} are not in canonical order (grid: row then column; equipment: slot number)";
                    return false;
                }
            }

            // ---- uids held by other containers of the same profile
            if (others != null && !others.CanHold(snapshot.containerId, result.Uids, out reason)) return false;

            validated = result;
            reason = null;
            return true;
        }
    }
}
