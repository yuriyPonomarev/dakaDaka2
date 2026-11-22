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
            public bool IndependentArrivals;
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

        public DungeonLayoutData ActiveLayoutFor(int floor) => _floors.TryGetValue(floor, out var target)
            && !target.Retired && target.Map != null && target.Map.gameObject.activeInHierarchy
            ? target.Map.RuntimeLayout : null;

        public bool TryPrepareDestination(int floor, out string reason)
        {
            reason = "Royale destination prerequisites missing";
            var initial = CopyPreparedPlan(1);
            var source = _director?.Layout;
            if (floor < 2 || floor > 3 || initial == null || source == null) return false;
            if (!RoyaleFloorCatalogs.TryLoad(floor, out var catalog, out reason)) return false;
            int seed = unchecked(initial.Seed + floor * 7919);
            if (!DungeonModulePlanner.TryCreate(seed, catalog.Modules, out var plan, out reason)) return false;
            float spacing = Mathf.Max(source.gridWidth, source.gridHeight) * source.CellSize + 100f;
            return TryPrepare(floor, source, catalog, plan, source.origin + Vector3.right * (spacing * (floor - 1)), out reason);
        }

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

        private bool TryReserveActor(PlayerRig rig, int floor, bool initial, out string reason, bool reserveBeforePortal = false)
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
            else if (reserveBeforePortal ? session.Floor >= floor : !rig.IsInFloorTransit || session.Floor + 1 != floor)
                return false;
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
                || !target.Map.gameObject.activeInHierarchy) return false;
            bool started = _director.TryGetRoyaleFloorClock(floor, out _, out _);
            bool refill = started && target.IndependentArrivals && _director.IsRoyaleFloorOpen(floor)
                && (!_director.IsRoyaleFloorOpen(floor - 1) || IsRetired(floor - 1));
            if (started && !refill) return false;
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
                if (!TryResolveLanding(target, index, refill, out Vector3 landing, out reason)) return false;
                if (!_director.TryCreateRoyaleBot(prefab, character, party, floor, landing, out rig, out reason, refill)) return false;
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
        internal bool HasReservedArrival(int floor, int playerId) => _floors.TryGetValue(floor, out var target)
            && target.ActorSlots.ContainsKey(playerId) && !target.Arrived.Contains(playerId);
        public bool IsAdmissionClosed(int floor) => _floors.TryGetValue(floor, out var target) && target.AdmissionClosed;

        public bool IsRetired(int floor) => _floors.TryGetValue(floor, out var target) && target.Retired;

        public bool CanRetireSource(int floor, out string reason)
        {
            reason = "Source floor cannot retire before destination arrival";
            if (NetRole.IsClientOnly || _director == null || !_director.RoyaleMode.HasValue
                || floor < 1 || floor > 2 || !_floors.TryGetValue(floor, out var target)
                || target.Map == null || target.Map.RuntimeLayout == null
                || !_director.TryGetRoyaleFloorClock(floor + 1, out _, out _)) return false;
            if (target.IndependentArrivals && _director.IsRoyaleFloorOpen(floor - 1) && !IsRetired(floor - 1))
            { reason = "Earlier floor is still admitting arrivals"; return false; }
            foreach (var session in _director.Players)
                if ((session.Floor == floor && (session.DeathCustodyPending || session.ExtractionSettlementPending
                    || !session.HasResult)) || (target.IndependentArrivals && session.Floor == floor - 1
                    && !session.HasResult && session.Rig != null && session.Rig.IsInFloorTransit
                    && target.ActorSlots.ContainsKey(session.PlayerId)))
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

        internal bool TryOpenTeamDestination(int floor, int durationSeconds, out string reason)
        {
            reason = "Team destination is unavailable";
            if (NetRole.IsClientOnly || !_director.HasTeamDescent || floor < 2 || floor > 3
                || !_floors.TryGetValue(floor - 1, out var source) || !_floors.TryGetValue(floor, out var target)
                || target.AdmissionClosed) return false;
            // Keep a seat for every live member of the source roster, including teammates who
            // are still arriving there from the previous floor. Portal use commits the move.
            foreach (int id in source.ActorSlots.Keys)
            {
                if (!_director.TryGetSession(id, out var session) || session.HasResult || session.State != RunState.Running
                    || session.Rig == null || session.Rig.IsDead || session.Rig.IsExtracted || session.Floor >= floor) continue;
                if (!TryReserveActor(session.Rig, floor, false, out reason, reserveBeforePortal: true)) return false;
            }
            if (!TryActivate(floor, out reason)) return false;
            if (target.Encounter == null && !RoyaleFloorEncounter.TryCreate(_director, target.Map, floor, durationSeconds,
                out target.Encounter, out reason)) return false;
            if (!_director.TryStartRoyaleFloor(floor, durationSeconds, out reason)) return false;
            target.AdmissionClosed = true;
            target.IndependentArrivals = true;
            return true;
        }

        internal void ReleaseUnusedTeamSeats(int floor)
        {
            if (!_floors.TryGetValue(floor, out var target) || !target.IndependentArrivals
                || (_director.IsRoyaleFloorOpen(floor - 1) && !IsRetired(floor - 1))) return;
            var unused = new List<int>();
            foreach (int id in target.ActorSlots.Keys)
            {
                if (target.Arrived.Contains(id)) continue;
                if (_director.TryGetSession(id, out var session) && session.Rig != null && session.Rig.IsInFloorTransit
                    && (session.Floor == floor - 1 || (session.Floor == floor && session.AwaitingInitialFloor))) continue;
                unused.Add(id);
            }
            foreach (int id in unused) ReleaseSeat(target, id);
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

        internal bool TryArriveParty(IReadOnlyList<PlayerSession> members, int floor, out string reason)
        {
            reason = "Team destination is unavailable";
            if (!_director.IsRoyaleFloorOpen(floor) || !_floors.TryGetValue(floor, out var target) || !target.IndependentArrivals)
                return false;
            var landings = new List<Vector3>(members.Count);
            foreach (var member in members)
            {
                if (!PrepareArrival(member.Rig, floor, target, out Vector3 landing, out reason, landings)) return false;
                landings.Add(landing);
            }
            // No yield between validating the whole party's landings and committing them.
            for (int i = 0; i < members.Count; i++)
            {
                var rig = members[i].Rig;
                if (!_director.TryArriveAtNextFloor(rig, floor, landings[i], Quaternion.identity, out reason)) return false;
                target.Arrived.Add(rig.PlayerId);
            }
            return true;
        }

        private bool PrepareArrival(PlayerRig rig, int floor, Floor target, out Vector3 landing, out string reason,
            List<Vector3> reservedLandings = null)
        {
            landing = default; reason = "Arrival actor is not ready";
            if (rig == null || rig.Movement == null || target.Arrived.Contains(rig.PlayerId)) return false;
            var session = _director.SessionOf(rig);
            if (session == null || session.HasResult || session.State != RunState.Running || rig.IsDead || rig.IsExtracted || !rig.IsInFloorTransit || (session.Floor + 1 != floor && !(session.AwaitingInitialFloor && session.Floor == floor))) return false;
            if (!target.ActorSlots.TryGetValue(rig.PlayerId, out int index))
            { reason = "Arrival requires a reserved destination seat"; return false; }
            if (!TryResolveLanding(target, index, target.IndependentArrivals, out landing, out reason, reservedLandings)) return false;
            var layout = target.Map.RuntimeLayout;
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

        private static bool TryResolveLanding(Floor target, int index, bool allowNearby, out Vector3 landing, out string reason,
            List<Vector3> reservedLandings = null)
        {
            landing = default;
            reason = "Destination landing position is unavailable";
            var layout = target.Map.RuntimeLayout;
            if (layout == null || index >= layout.playerSpawns.Count) return false;
            var anchor = layout.playerSpawns[index];
            bool TryCell(Vector2Int cell, out Vector3 point)
            {
                point = layout.CellCenterWorld(cell) + Vector3.up * .15f;
                if (reservedLandings != null)
                    foreach (var reserved in reservedLandings)
                        if ((point - reserved).sqrMagnitude < .8f * .8f) return false;
                return DungeonModuleSceneAssembler.TryFindClearSpawnCell(layout, target.Map.transform, cell, cell, cell, out _)
                    && !Physics.CheckCapsule(point + Vector3.up * .38f, point + Vector3.up * 1.44f,
                        .36f, Physics.AllLayers, QueryTriggerInteraction.Ignore);
            }
            if (TryCell(anchor, out landing)) { reason = null; return true; }
            if (!allowNearby || target.Map.RuntimeNavGrid == null) return false;
            // A teammate can already be standing on the reserved spawn when a later actor enters.
            var nav = target.Map.RuntimeNavGrid;
            var route = new List<Vector3>();
            var pathfinder = new NavGridPathfinder(nav);
            for (int radius = 1; radius <= 3; radius++)
                for (int z = -radius; z <= radius; z++)
                    for (int x = -radius; x <= radius; x++)
                    {
                        var cell = anchor + new Vector2Int(x, z);
                        if (Mathf.Max(Mathf.Abs(x), Mathf.Abs(z)) != radius || !nav.IsWalkable(cell.x, cell.y)
                            || layout.playerSpawns.Contains(cell) || !TryCell(cell, out landing)
                            || !pathfinder.TryFindPath(layout.CellCenterWorld(anchor), landing, route)
                            || route.Count > 5) continue;
                        reason = null;
                        return true;
                    }
            return false;
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
                    && (!target.IndependentArrivals || target.Arrived.Contains(session.PlayerId))) continue;
                ReleaseSeat(target, session.PlayerId);
            }
        }

        private static void ReleaseSeat(Floor target, int actorId)
        {
            if (!target.ActorSlots.Remove(actorId, out int seat)) return;
            int partySize = new RoyaleRoster(target.Mode).PartySize;
            int partySlot = seat / partySize;
            foreach (int slot in target.ActorSlots.Values)
                if (slot / partySize == partySlot) return;
            int party = -1;
            foreach (var entry in target.PartySlots)
                if (entry.Value == partySlot) { party = entry.Key; break; }
            if (party >= 0) target.PartySlots.Remove(party);
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
