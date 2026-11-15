using System.Collections.Generic;
using Emberdelve.Core;
using Emberdelve.AI;
using Emberdelve.Data;
using Emberdelve.Level;
using Emberdelve.Player;
using Emberdelve.Rules;
using UnityEngine;

namespace Emberdelve.Flow
{
    /// <summary>Owns prepared destination geometry and party landing slots. The match driver supplies
    /// population/content readiness and the network arrival barrier before opening a floor.</summary>
    public sealed class RoyaleFloorCoordinator
    {
        private sealed class Floor
        {
            public DungeonModuleSceneAssembler Map;
            public bool OwnsMap = true;
            public bool AdmissionClosed;
            public bool Retired;
            public RoyaleFloorEncounter Encounter;
            public ChestSpawner Chests;
            public FloorContentSpawner Content;
            public RoyalePartyMode Mode;
            public readonly Dictionary<int, int> PartySlots = new Dictionary<int, int>();
            public readonly HashSet<int> Arrived = new HashSet<int>();
            public readonly Dictionary<int, int> ActorSlots = new Dictionary<int, int>();
        }

        private readonly RunDirector _director;
        private readonly Dictionary<int, Floor> _floors = new Dictionary<int, Floor>();
        internal RoyaleFloorCoordinator(RunDirector director) { _director = director; }

        public bool TryAdoptInitialMap(DungeonModuleSceneAssembler map, out string reason)
        {
            reason = "Initial map is not ready";
            if (NetRole.IsClientOnly || _director == null || !_director.RoyaleMode.HasValue
                || map == null || !map.AcceptsSessionPlan || !map.RuntimeMapValidationComplete
                || map.RuntimeLayout != _director.Layout || map.AppliedRoyaleMode != _director.RoyaleMode
                || _director.Chests == null || _director.Chests.BuiltLayout != map.RuntimeLayout || !_director.RuntimeLayoutInstallationComplete
                || map.RuntimeLayout.playerSpawns.Count != new RoyaleRoster(_director.RoyaleMode.Value).Capacity
                || _director.TryGetRoyaleFloorClock(1, out _, out _)) return false;
            if (_floors.TryGetValue(1, out var existing))
            { reason = existing.Map == map ? null : "Another initial map is registered"; return existing.Map == map; }
            if (!map.EnsureInitialContentReady(out reason)) return false;
            _floors.Add(1, new Floor { Map = map, OwnsMap = false, Chests = _director.Chests, Mode = _director.RoyaleMode.Value });
            reason = null; return true;
        }

        internal bool CanAdmitInitialParty(int party, out string reason)
        {
            reason = "Initial population is unavailable or closed";
            if (party < 0 || !_floors.TryGetValue(1, out var target) || target.Map == null || target.AdmissionClosed
                || _director.TryGetRoyaleFloorClock(1, out _, out _)) return false;
            var roster = new RoyaleRoster(target.Mode);
            if (!target.PartySlots.TryGetValue(party, out int group))
            {
                if (target.PartySlots.Count < roster.PartyCapacity) { reason = null; return true; }
            }
            else
            {
                int members = 0;
                foreach (int slot in target.ActorSlots.Values) if (slot / roster.PartySize == group) members++;
                if (members < roster.PartySize) { reason = null; return true; }
            }
            reason = "Initial party or population is full"; return false;
        }

        public DungeonModulePlan CopyPreparedPlan(int floor) => _floors.TryGetValue(floor, out var target)
            && target.Map != null ? target.Map.CopyAppliedPlan() : null;

        public bool TryPrepare(int floor, DungeonLayoutData source, DungeonModuleCatalog catalog,
            DungeonModulePlan plan, Vector3 origin, out string reason)
        {
            reason = "Floor preparation is unavailable";
            if (_director == null || NetRole.IsClientOnly || !_director.RoyaleMode.HasValue
                || floor < 2 || floor > 3 || _floors.ContainsKey(floor)) return false;
            if (source == null) return false;
            float width = DungeonModulePlan.Width * DungeonModulePlan.ModuleWidthCells * source.CellSize;
            float depth = DungeonModulePlan.Height * DungeonModulePlan.ModuleHeightCells * source.CellSize;
            var bounds = new Rect(origin.x - 2f, origin.z - 2f, width + 4f, depth + 4f);
            bool Overlaps(DungeonLayoutData other) => other != null && bounds.Overlaps(new Rect(other.origin.x,
                other.origin.z, other.gridWidth * other.CellSize, other.gridHeight * other.CellSize));
            if (Overlaps(_director.Layout)) { reason = "Destination overlaps the current map"; return false; }
            foreach (var existing in _floors.Values)
                if (existing.Map != null && Overlaps(existing.Map.RuntimeLayout))
                { reason = "Destination overlaps another floor"; return false; }
            if (plan == null || (plan.RoyaleMode.HasValue && plan.RoyaleMode != _director.RoyaleMode))
            { reason = "Destination mode differs from the run"; return false; }
            plan = plan.WithRoyaleMode(_director.RoyaleMode);
            if (!DungeonModuleSceneAssembler.TryStageFloor(source, catalog, plan, origin,
                    out var stage, out reason, _director.RoyaleMode.Value)) return false;
            try
            {
                if (!stage.BindStagedFloorIdentity(floor, out reason)) return false;
                var chests = ChestSpawner.CreateForFloor(stage.RuntimeLayout, _director.Config, _director, stage.transform, floor);
                if (chests == null) { reason = "Destination chests could not be prepared"; return false; }
                var content = FloorContentSpawner.CreateForFloor(stage.RuntimeLayout, stage.RuntimeNavGrid,
                    _director.Config, _director, stage.transform, floor, out reason);
                if (content == null) return false;
                _floors.Add(floor, new Floor { Map = stage, Chests = chests, Content = content, Mode = _director.RoyaleMode.Value });
                return true;
            }
            finally { if (!_floors.ContainsKey(floor)) stage.DiscardStagedFloor(); }
        }

        // This does not start the clock: the match driver must admit/fill the population and finish
        // content + network readiness first. Keeping those steps separate prevents a preparation delay
        // from consuming the destination's raid time.
        public bool TryActivate(int floor, out string reason)
        {
            reason = "Destination map is not prepared";
            if (NetRole.IsClientOnly || !_floors.TryGetValue(floor, out var target)
                || target.Retired || target.Map == null || !target.Map.RuntimeMapValidationComplete || target.Chests == null || (target.OwnsMap && target.Content == null)) return false;
            target.Map.gameObject.SetActive(true);
            Physics.SyncTransforms();
            if (!target.OwnsMap) return target.Map.EnsureInitialContentReady(out reason);
            foreach (var enemy in target.Content.Enemies)
                if (enemy == null || !enemy.EnsureRuntimeInitialized())
                {
                    target.Map.gameObject.SetActive(false);
                    reason = "Destination monster initialization failed";
                    return false;
                }
            reason = null;
            return true;
        }

        /// <summary>Reserve survivors before filling vacant seats or starting the destination clock.</summary>
        public bool TryReserveIncoming(PlayerRig rig, int floor, out string reason) => TryReserveActor(rig, floor, false, out reason);
        public bool TryHoldInitialPlayer(PlayerRig rig, out string reason) => TryReserveActor(rig, 1, true, out reason);

        private bool TryReserveActor(PlayerRig rig, int floor, bool initial, out string reason)
        {
            reason = "Destination reservation is unavailable";
            if (NetRole.IsClientOnly || _director == null || rig == null
                || !_director.RoyaleMode.HasValue || !_floors.TryGetValue(floor, out var target)
                || target.Map == null || target.AdmissionClosed || target.Mode != _director.RoyaleMode.Value
                || _director.TryGetRoyaleFloorClock(floor, out _, out _)) return false;
            var session = _director.SessionOf(rig);
            if (session == null || session.State != RunState.Running || session.HasResult || rig.IsDead || rig.IsExtracted) return false;
            if (initial)
            {
                if (session.Floor != 1 || session.IsBot || (rig.IsInFloorTransit && !session.AwaitingInitialFloor)) return false;
            }
            else if (!rig.IsInFloorTransit || session.Floor + 1 != floor) return false;
            if (target.ActorSlots.ContainsKey(rig.PlayerId)) { reason = null; return true; }
            var roster = new RoyaleRoster(target.Mode);
            bool newParty = !target.PartySlots.TryGetValue(session.PartyId, out int partySlot);
            if (newParty)
            {
                partySlot = 0;
                while (target.PartySlots.ContainsValue(partySlot)) partySlot++;
            }
            if (partySlot >= roster.PartyCapacity) { reason = "Destination parties are full"; return false; }
            for (int member = 0; member < roster.PartySize; member++)
            {
                int index = partySlot * roster.PartySize + member;
                if (target.ActorSlots.ContainsValue(index)) continue;
                if (index >= target.Map.RuntimeLayout.playerSpawns.Count) return false;
                if (initial)
                {
                    if (!rig.IsInFloorTransit && !rig.TryBeginFloorTransit(out reason)) return false;
                    session.AwaitingInitialFloor = true;
                }
                if (newParty) target.PartySlots.Add(session.PartyId, partySlot);
                target.ActorSlots.Add(rig.PlayerId, index);
                reason = null;
                return true;
            }
            reason = "Destination party is full";
            return false;
        }

        public bool TrySpawnBot(int floor, int party, GameObject prefab, CharacterClassData character,
            out PlayerRig rig, out string reason)
        {
            rig = null; reason = "Destination bot admission is unavailable";
            if (NetRole.IsClientOnly || _director == null || party < 0
                || !_floors.TryGetValue(floor, out var target) || target.Map == null
                || !target.Map.gameObject.activeInHierarchy
                || _director.TryGetRoyaleFloorClock(floor, out _, out _)) return false;
            var roster = new RoyaleRoster(target.Mode);
            bool newParty = !target.PartySlots.TryGetValue(party, out int partySlot);
            if (newParty)
            {
                partySlot = 0;
                while (target.PartySlots.ContainsValue(partySlot)) partySlot++;
            }
            if (partySlot >= roster.PartyCapacity) { reason = "Destination parties are full"; return false; }
            for (int member = 0; member < roster.PartySize; member++)
            {
                int index = partySlot * roster.PartySize + member;
                if (target.ActorSlots.ContainsValue(index)) continue;
                var layout = target.Map.RuntimeLayout;
                if (index >= layout.playerSpawns.Count) return false;
                var cell = layout.playerSpawns[index];
                if (!DungeonModuleSceneAssembler.TryFindClearSpawnCell(layout, target.Map.transform, cell, cell, cell, out _)) return false;
                Vector3 landing = layout.CellCenterWorld(cell) + Vector3.up * .15f;
                if (Physics.CheckCapsule(landing + Vector3.up * .38f, landing + Vector3.up * 1.44f,
                    .36f, Physics.AllLayers, QueryTriggerInteraction.Ignore)) return false;
                if (!_director.TryCreateRoyaleBot(prefab, character, party, floor, landing, out rig, out reason)) return false;
                if (newParty) target.PartySlots.Add(party, partySlot);
                target.ActorSlots.Add(rig.PlayerId, index);
                return true;
            }
            reason = "Destination party is full"; return false;
        }

        public bool TryCopyMapSnapshot(int floor, out RoyaleMapSnapshot snapshot)
        {
            snapshot = null;
            var initial = CopyPreparedPlan(1);
            return initial != null && _floors.TryGetValue(floor, out var target) && target.Map != null
                && RoyaleMapSnapshot.TryCreate(floor, initial.LayoutHash, target.Map.RuntimeLayout.origin,
                    target.Map.CopyAppliedPlan(), out snapshot);
        }

        public RoyaleFloorEncounter EncounterFor(int floor) => _floors.TryGetValue(floor, out var target) ? target.Encounter : null;

        public int ReservedCount(int floor) => _floors.TryGetValue(floor, out var target) ? target.ActorSlots.Count : 0;
        public bool IsAdmissionClosed(int floor) => _floors.TryGetValue(floor, out var target) && target.AdmissionClosed;

        public bool IsRetired(int floor) => _floors.TryGetValue(floor, out var target) && target.Retired;

        public bool CanRetireSource(int floor, out string reason)
        {
            reason = "Source floor cannot retire before destination arrival";
            if (NetRole.IsClientOnly || _director == null || !_director.RoyaleMode.HasValue
                || floor < 1 || floor > 2 || !_floors.TryGetValue(floor, out var target)
                || target.Map == null || target.Map.RuntimeLayout == null
                || !_director.TryGetRoyaleFloorClock(floor + 1, out _, out _)) return false;
            foreach (var session in _director.Players)
                if (session.Floor == floor && (session.DeathCustodyPending || session.ExtractionSettlementPending
                    || !session.HasResult))
                { reason = "Source actor still awaits arrival or terminal custody"; return false; }
            reason = null; return true;
        }

        public bool TryRetireSource(int floor, out string reason)
        {
            if (!CanRetireSource(floor, out reason)) return false;
            var target = _floors[floor];
            if (target.Retired) return true;
            var layout = target.Map.RuntimeLayout;
            var bounds = new Rect(layout.origin.x, layout.origin.z,
                layout.gridWidth * layout.CellSize, layout.gridHeight * layout.CellSize);
            target.Retired = true;
            target.Map.RetireRuntimeContent();
            // Initial chests live on the director, not beneath the assembler's geometry root.
            if (target.Chests != null)
                foreach (var chest in target.Chests.Chests) if (chest != null) chest.gameObject.SetActive(false);
            if (target.Encounter != null) target.Encounter.gameObject.SetActive(false);
            foreach (var pickup in Object.FindObjectsByType<Emberdelve.Items.LootPickup>(FindObjectsSortMode.None))
                if (bounds.Contains(new Vector2(pickup.HomePosition.x, pickup.HomePosition.z))) pickup.gameObject.SetActive(false);
            if (GameServices.TryGet<IRetiredFloorLootSink>(out var loot)) loot.RetireFloorLoot(bounds);
            return true;
        }

        public bool TryCloseAdmission(int floor)
        {
            if (NetRole.IsClientOnly || !_floors.TryGetValue(floor, out var target)
                || target.ActorSlots.Count == 0 || _director.TryGetRoyaleFloorClock(floor, out _, out _)) return false;
            target.AdmissionClosed = true;
            return true;
        }

        public bool TryGetVacantParty(int floor, out int party)
        {
            party = -1;
            if (!_floors.TryGetValue(floor, out var target) || !target.AdmissionClosed) return false;
            var roster = new RoyaleRoster(target.Mode);
            if (target.ActorSlots.Count >= roster.Capacity) return false;
            for (int group = 0; group < roster.PartyCapacity; group++)
            {
                int members = 0;
                foreach (int slot in target.ActorSlots.Values) if (slot / roster.PartySize == group) members++;
                if (members >= roster.PartySize) continue;
                foreach (var entry in target.PartySlots)
                    if (entry.Value == group) { party = entry.Key; return true; }
                // New parties cannot accidentally ally with historical source-floor parties.
                party = 0;
                var used = new HashSet<int>(target.PartySlots.Keys);
                foreach (var session in _director.Players) used.Add(session.PartyId);
                while (used.Contains(party)) party++;
                return true;
            }
            return false;
        }

        public bool TryStartPopulation(int floor, int durationSeconds, out string reason)
        {
            reason = "Population is not complete";
            if (NetRole.IsClientOnly || !_floors.TryGetValue(floor, out var target) || !target.AdmissionClosed
                || target.ActorSlots.Count != new RoyaleRoster(target.Mode).Capacity
                || _director.TryGetRoyaleFloorClock(floor, out _, out _)) return false;
            if (!TryActivate(floor, out reason)) return false;
            if (target.Encounter == null && !RoyaleFloorEncounter.TryCreate(_director, target.Map, floor, durationSeconds,
                out target.Encounter, out reason)) return false;
            var arrivals = new List<PlayerRig>();
            foreach (int id in target.ActorSlots.Keys)
            {
                PlayerRig rig = null;
                foreach (var session in _director.Players) if (session.PlayerId == id) { rig = session.Rig; break; }
                if (!PrepareArrival(rig, floor, target, out _, out reason)) return false;
                arrivals.Add(rig);
            }
            // No frame/yield occurs between validating the full cohort and releasing it.
            if (!_director.TryStartRoyaleFloor(floor, durationSeconds, out reason)) return false;
            foreach (var rig in arrivals) if (!TryArrive(rig, floor, out reason)) return false;
            return true;
        }

        public bool TryArrive(PlayerRig rig, int floor, out string reason)
        {
            reason = "Destination is not ready for arrival";
            if (NetRole.IsClientOnly || _director == null || !_director.RoyaleMode.HasValue
                || !_floors.TryGetValue(floor, out var target) || target.Map == null
                || target.Mode != _director.RoyaleMode.Value || !target.Map.gameObject.activeInHierarchy
                || !_director.IsRoyaleFloorOpen(floor)) return false;
            if (!PrepareArrival(rig, floor, target, out var landing, out reason)) return false;
            if (!_director.TryArriveAtNextFloor(rig, floor, landing, Quaternion.identity, out reason)) return false;
            target.Arrived.Add(rig.PlayerId);
            return true;
        }

        private bool PrepareArrival(PlayerRig rig, int floor, Floor target, out Vector3 landing, out string reason)
        {
            landing = default; reason = "Arrival actor is not ready";
            if (rig == null || rig.Movement == null || target.Arrived.Contains(rig.PlayerId)) return false;
            var session = _director.SessionOf(rig);
            if (session == null || session.HasResult || session.State != RunState.Running || rig.IsDead || rig.IsExtracted || !rig.IsInFloorTransit || (session.Floor + 1 != floor && !(session.AwaitingInitialFloor && session.Floor == floor))) return false;
            if (!target.ActorSlots.TryGetValue(rig.PlayerId, out int index))
            { reason = "Arrival requires a reserved destination seat"; return false; }
            var layout = target.Map.RuntimeLayout;
            if (layout == null || index >= layout.playerSpawns.Count) return false;
            Vector2Int cell = layout.playerSpawns[index];
            // Recheck geometry after activation; a failed transfer retains its reserved seat.
            if (!DungeonModuleSceneAssembler.TryFindClearSpawnCell(layout, target.Map.transform, cell, cell, cell, out _))
            { reason = "Destination landing position is obstructed"; return false; }
            landing = layout.CellCenterWorld(cell) + Vector3.up * .15f;
            // Existing players/bots are not children of the map; they must also block landing.
            if (Physics.CheckCapsule(landing + Vector3.up * .38f, landing + Vector3.up * 1.44f,
                    .36f, Physics.AllLayers, QueryTriggerInteraction.Ignore))
            { reason = "Destination landing position is occupied"; return false; }
            if (session.IsBot)
            {
                var controller = rig.GetComponent<RoyaleBotController>();
                if (controller == null) controller = rig.gameObject.AddComponent<RoyaleBotController>();
                var patrol = new List<Vector3>();
                foreach (var spawn in layout.playerSpawns) patrol.Add(layout.CellCenterWorld(spawn));
                if (!controller.Configure(_director, rig, floor, target.Map.RuntimeNavGrid, patrol))
                { reason = "Bot controller could not bind destination"; return false; }
            }
            reason = null;
            return true;
        }

        /// <summary>Only discard a destination without reservations or a started clock.</summary>
        public bool TryDiscardUnused(int floor)
        {
            if (NetRole.IsClientOnly || !_floors.TryGetValue(floor, out var target)
                || !target.OwnsMap || target.ActorSlots.Count != 0 || _director.TryGetRoyaleFloorClock(floor, out _, out _)) return false;
            if (target.Map != null) target.Map.DiscardStagedFloor();
            _floors.Remove(floor);
            return true;
        }

        // Removing an actor releases every unstarted reservation, including a survivor waiting
        // on another floor. Started rosters never reopen seats after death/disconnection.
        internal void ReleasePendingArrival(PlayerSession session)
        {
            if (session == null) return;
            foreach (var entry in _floors)
            {
                var target = entry.Value;
                if (_director.TryGetRoyaleFloorClock(entry.Key, out _, out _)
                    || !target.ActorSlots.Remove(session.PlayerId)) continue;
                if (!target.PartySlots.TryGetValue(session.PartyId, out int partySlot)) continue;
                int partySize = new RoyaleRoster(target.Mode).PartySize;
                bool occupied = false;
                foreach (int slot in target.ActorSlots.Values)
                    if (slot / partySize == partySlot) { occupied = true; break; }
                if (!occupied) target.PartySlots.Remove(session.PartyId);
            }
        }

        internal void ReleaseOwnedMaps()
        {
            foreach (var floor in _floors.Values)
                if (floor.OwnsMap && floor.Map != null) floor.Map.DiscardStagedFloor();
            _floors.Clear();
        }

        internal void ParentRuntimeDrop(GameObject drop, Vector3 position)
        {
            foreach (var floor in _floors.Values)
            {
                if (floor.Map == null || !floor.Map.gameObject.activeInHierarchy) continue;
                var layout = floor.Map.RuntimeLayout;
                if (layout == null) continue;
                var bounds = new Rect(layout.origin.x, layout.origin.z, layout.gridWidth * layout.CellSize, layout.gridHeight * layout.CellSize);
                if (!bounds.Contains(new Vector2(position.x, position.z))) continue;
                drop.transform.SetParent(floor.Map.transform, true);
                return;
            }
        }
    }
}
