using System;
using System.Collections.Generic;
using Emberdelve.Core;
using UnityEngine;

namespace Emberdelve.Items
{
    /// <summary>
    /// Server/run-owned corpse loadouts. Death moves a complete, validated snapshot out of the dead actor before
    /// it can be discarded. A later server interaction can transfer the complete loadout to an empty recipient;
    /// partial merges are deliberately refused so a failed attempt cannot split or duplicate item ownership.
    /// </summary>
    public sealed class CorpseLootRegistry
    {
        private readonly Dictionary<ulong, CorpseLootManifest> _corpses = new Dictionary<ulong, CorpseLootManifest>();
        private readonly HashSet<int> _committedDeaths = new HashSet<int>();
        private readonly HashSet<ulong> _busyCorpses = new HashSet<ulong>();
        private readonly HashSet<Guid> _gearUids = new HashSet<Guid>();
        private ulong _nextCorpseId = 1;

        public int Count => _corpses.Count;

        public bool TryGet(ulong corpseId, out CorpseLootManifest manifest) => _corpses.TryGetValue(corpseId, out manifest);

        /// <summary>
        /// Atomically captures and removes a dead actor's full bag/equipment loadout. This is server/offline simulation
        /// code only. The registry entry is staged first; if the source changed or its empty replacement is refused,
        /// the staged record and its UID reservations are rolled back and the source remains untouched.
        /// </summary>
        public bool TryCommitDeath(int playerId, Vector3 position, Inventory bag, Equipment equipment,
            out ulong corpseId, out string reason)
        {
            corpseId = 0;
            if (NetRole.Current == NetRoleKind.Client)
            {
                reason = "only the server may create corpse loot";
                return false;
            }
            if (playerId < 0 || bag == null || equipment == null || !ReferenceEquals(equipment.BagPartner(), bag))
            {
                reason = "corpse loot needs a valid player bag and paired equipment";
                return false;
            }
            if (_committedDeaths.Contains(playerId))
            {
                reason = "this player's death has already transferred its loadout";
                return false;
            }
            if (!CorpseLootManifest.TryCapture(playerId, position, bag, equipment,
                    out CorpseLootManifest candidate, out reason)) return false;

            LoadoutSnapshot captured = candidate.CopyLoadout();
            bool hasItems = captured.bag.entries.Length > 0 || captured.equipment.entries.Length > 0;
            Guid[] gearUids = candidate.CopyGearUids();
            for (int i = 0; i < gearUids.Length; i++)
            {
                if (_gearUids.Contains(gearUids[i]))
                {
                    reason = $"gear uid {gearUids[i]:N} is already owned by another corpse";
                    return false;
                }
            }

            if (!hasItems)
            {
                _committedDeaths.Add(playerId);
                reason = null;
                return true;
            }
            if (_nextCorpseId == 0 || _nextCorpseId == ulong.MaxValue)
            {
                reason = "corpse identity space is exhausted";
                return false;
            }

            LoadoutSnapshot empty = BuildEmptyLoadout(captured);
            if (empty == null)
            {
                reason = "could not construct an empty replacement loadout";
                return false;
            }

            ulong stagedId = _nextCorpseId++;
            _corpses.Add(stagedId, candidate);
            for (int i = 0; i < gearUids.Length; i++) _gearUids.Add(gearUids[i]);

            // Main-thread server execution makes this compare-and-commit boundary atomic with respect to game RPCs.
            if (!candidate.StillMatches(bag, equipment) || !LoadoutSnapshot.TryRestore(empty, bag, equipment, null, out reason))
            {
                _corpses.Remove(stagedId);
                for (int i = 0; i < gearUids.Length; i++) _gearUids.Remove(gearUids[i]);
                if (string.IsNullOrEmpty(reason)) reason = "the source loadout changed before corpse transfer";
                return false;
            }

            _committedDeaths.Add(playerId);
            corpseId = stagedId;
            reason = null;
            return true;
        }

        /// <summary>
        /// Transfers one complete corpse loadout to an empty admitted recipient. Callers must validate the recipient's
        /// identity, life state, range and line of sight before calling. The corpse is removed only after the existing
        /// atomic loadout restore validates and commits every stack, item UID and rolled affix.
        /// </summary>
        public bool TryLootAll(ulong corpseId, Inventory recipientBag, Equipment recipientEquipment,
            ItemUidRegistry recipientUidRegistry, out string reason)
        {
            if (NetRole.Current == NetRoleKind.Client)
            {
                reason = "only the server may transfer corpse loot";
                return false;
            }
            if (!_corpses.TryGetValue(corpseId, out CorpseLootManifest manifest) || manifest == null)
            {
                reason = "corpse loot is no longer available";
                return false;
            }
            if (recipientBag == null || recipientEquipment == null ||
                !ReferenceEquals(recipientEquipment.BagPartner(), recipientBag))
            {
                reason = "the recipient bag and equipment are not paired";
                return false;
            }
            if (recipientUidRegistry == null)
            {
                reason = "the recipient profile UID registry is required";
                return false;
            }

            LoadoutSnapshot existing = LoadoutSnapshot.Capture(recipientBag, recipientEquipment);
            if (existing == null || existing.bag.entries.Length != 0 || existing.equipment.entries.Length != 0)
            {
                reason = "the recipient must have empty bag and equipment to take a complete corpse loadout";
                return false;
            }

            if (!_busyCorpses.Add(corpseId))
            {
                reason = "corpse loot is already being transferred";
                return false;
            }

            try
            {
                if (!LoadoutSnapshot.TryRestore(manifest.CopyLoadout(), recipientBag, recipientEquipment,
                        recipientUidRegistry, out reason))
                    return false;

                _corpses.Remove(corpseId);
                Guid[] uids = manifest.CopyGearUids();
                for (int i = 0; i < uids.Length; i++) _gearUids.Remove(uids[i]);
                reason = null;
                return true;
            }
            finally
            {
                _busyCorpses.Remove(corpseId);
            }
        }

        private static LoadoutSnapshot BuildEmptyLoadout(LoadoutSnapshot captured)
        {
            if (captured?.bag == null || captured.equipment == null) return null;
            LoadoutSnapshot empty = captured.Clone();
            empty.bag.entries = Array.Empty<ContainerEntry>();
            empty.equipment.entries = Array.Empty<ContainerEntry>();
            empty.weaponSet = 0;
            empty.belt = new[] { "", "", "", "" };
            return empty.Seal();
        }
    }
}
