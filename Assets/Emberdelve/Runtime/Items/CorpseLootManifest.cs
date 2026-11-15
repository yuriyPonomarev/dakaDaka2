using System;
using System.Collections.Generic;
using UnityEngine;

namespace Emberdelve.Items
{
    /// <summary>
    /// A read-only, lossless capture of a player's carried and worn items for a future corpse container.
    /// Capturing this value does not mutate either source container or transfer UID ownership. A caller must
    /// commit the later world/container transfer before despawning the source rig.
    /// </summary>
    public sealed class CorpseLootManifest
    {
        private readonly LoadoutSnapshot _loadout;
        private readonly Guid[] _gearUids;
        private readonly Dictionary<Vector2Int, Vector2Int> _bagFootprints;

        public int SourcePlayerId { get; }
        public Vector3 WorldPosition { get; }
        public ulong SourceRevision { get; }
        public int GearUidCount => _gearUids.Length;

        private CorpseLootManifest(int sourcePlayerId, Vector3 worldPosition, ulong sourceRevision,
            LoadoutSnapshot loadout, Guid[] gearUids, Dictionary<Vector2Int, Vector2Int> bagFootprints)
        {
            SourcePlayerId = sourcePlayerId;
            WorldPosition = worldPosition;
            SourceRevision = sourceRevision;
            _loadout = loadout;
            _gearUids = gearUids;
            _bagFootprints = bagFootprints;
        }

        internal bool TryGetCapturedFootprint(int x, int y, out Vector2Int size) =>
            _bagFootprints.TryGetValue(new Vector2Int(x, y), out size);

        /// <summary>Returns a detached deep copy; callers cannot mutate the manifest's captured item data.</summary>
        public LoadoutSnapshot CopyLoadout() => _loadout.Clone();

        /// <summary>Returns the exact gear identities across bag and equipment, in snapshot order.</summary>
        public Guid[] CopyGearUids() => (Guid[])_gearUids.Clone();

        /// <summary>True only while both the shared revision and the exact captured loadout are unchanged.</summary>
        public bool StillMatches(Inventory bag, Equipment equipment)
        {
            if (bag == null || equipment == null || !ReferenceEquals(equipment.BagPartner(), bag) ||
                bag.Revision != SourceRevision) return false;
            LoadoutSnapshot current = LoadoutSnapshot.Capture(bag, equipment);
            return current != null && string.Equals(current.checksum, _loadout.checksum, StringComparison.Ordinal);
        }

        /// <summary>
        /// Captures the current bag and equipment without changing them. Refuses broken pairing, missing content,
        /// checksum drift, malformed gear identities, or duplicate identities across the two containers.
        /// Full catalog/slot validation remains the responsibility of LoadoutSnapshot.TryRestore at the destination.
        /// </summary>
        public static bool TryCapture(int sourcePlayerId, Vector3 worldPosition, Inventory bag, Equipment equipment,
            out CorpseLootManifest manifest, out string reason)
        {
            manifest = null;
            if (sourcePlayerId < 0)
            {
                reason = "corpse loot needs a nonnegative source player id";
                return false;
            }
            if (bag == null || equipment == null)
            {
                reason = "corpse loot capture needs both the bag and equipment";
                return false;
            }
            if (!ReferenceEquals(equipment.BagPartner(), bag))
            {
                reason = "the equipment does not belong to the supplied bag";
                return false;
            }
            if (float.IsNaN(worldPosition.x) || float.IsInfinity(worldPosition.x) ||
                float.IsNaN(worldPosition.y) || float.IsInfinity(worldPosition.y) ||
                float.IsNaN(worldPosition.z) || float.IsInfinity(worldPosition.z))
            {
                reason = "corpse position must be finite";
                return false;
            }

            LoadoutSnapshot captured = LoadoutSnapshot.Capture(bag, equipment);
            if (captured == null || captured.bag == null || captured.equipment == null ||
                !captured.ChecksumMatches() || !captured.bag.ChecksumMatches() || !captured.equipment.ChecksumMatches())
            {
                reason = "the captured loadout is incomplete or its checksum does not match";
                return false;
            }
            if (captured.bag.kind != ContainerKinds.Bag || captured.equipment.kind != ContainerKinds.Equipment)
            {
                reason = "the captured loadout has unexpected container kinds";
                return false;
            }

            List<Guid> gearUids = new List<Guid>();
            HashSet<Guid> seen = new HashSet<Guid>();
            if (!CollectGearUids(captured.bag, seen, gearUids, out reason) ||
                !CollectGearUids(captured.equipment, seen, gearUids, out reason)) return false;

            var footprints = new Dictionary<Vector2Int, Vector2Int>();
            foreach (var stack in bag.Slots)
            {
                if (footprints.ContainsKey(stack.Origin)) { reason = "captured bag repeats an origin"; return false; }
                // Belt display is one slot, but corpse looting needs the item's actual bag footprint.
                footprints.Add(stack.Origin, stack.Item.EffectiveFootprint);
            }
            manifest = new CorpseLootManifest(sourcePlayerId, worldPosition, bag.Revision, captured.Clone(), gearUids.ToArray(), footprints);
            reason = null;
            return true;
        }

        private static bool CollectGearUids(ContainerSnapshot container, HashSet<Guid> seen, List<Guid> collected,
            out string reason)
        {
            if (container.entries == null)
            {
                reason = "a captured container has no entry list";
                return false;
            }

            for (int i = 0; i < container.entries.Length; i++)
            {
                ContainerEntry entry = container.entries[i];
                if (entry == null || entry.item == null)
                {
                    reason = "a captured container contains an empty entry";
                    return false;
                }

                string uidText = entry.item.uid;
                if (string.IsNullOrEmpty(uidText)) continue; // ordinary carried stack; quantity is retained in snapshot.
                if (!Guid.TryParseExact(uidText, "N", out Guid uid) || uid == Guid.Empty)
                {
                    reason = "a captured gear item has a malformed or empty uid";
                    return false;
                }
                if (!seen.Add(uid))
                {
                    reason = $"uid {uid:N} occurs more than once in the captured loadout";
                    return false;
                }
                collected.Add(uid);
            }

            reason = null;
            return true;
        }
    }
}
