using System;
using System.Collections.Generic;
using Emberdelve.Core;
using UnityEngine;

namespace Emberdelve.Level
{
    /// <summary>Client-owned destination geometry and chest presentation. Does not spawn authority loot/monsters or start clocks.</summary>
    public sealed class RoyaleClientMaps : IDisposable
    {
        private readonly DungeonModuleSceneAssembler _initial;
        private readonly DungeonModuleCatalog _catalog;
        private readonly HashSet<int> _retired = new HashSet<int>();
        private readonly Dictionary<int, (RoyaleMapSnapshot snapshot, DungeonModuleSceneAssembler map)> _maps
            = new Dictionary<int, (RoyaleMapSnapshot, DungeonModuleSceneAssembler)>();
        public RoyaleClientMaps(DungeonModuleSceneAssembler initial, DungeonModuleCatalog catalog)
        { _initial = initial; _catalog = catalog; }
        public int Count => _maps.Count;
        public bool IsRetired(int floor) => _retired.Contains(floor);

        // Retain plan/layout records: a later destination still validates its initial-map identity.
        public bool TryRetire(int floor, uint initialHash, uint destinationHash, out string reason)
        {
            reason = "Retirement does not match installed destination";
            if (!NetRole.IsClientOnly || floor < 1 || floor > 2 || _initial == null
                || !_maps.TryGetValue(floor + 1, out var destination)
                || destination.snapshot.InitialHash != initialHash || destination.snapshot.Hash != destinationHash) return false;
            if (_retired.Contains(floor)) { reason = null; return true; }
            var source = floor == 1 ? _initial : _maps.TryGetValue(floor, out var previous) ? previous.map : null;
            if (source == null) return false;
            source.RetireRuntimeContent();
            var layout = source.RuntimeLayout;
            var bounds = new Rect(layout.origin.x, layout.origin.z, layout.gridWidth * layout.CellSize, layout.gridHeight * layout.CellSize);
            foreach (var chest in UnityEngine.Object.FindObjectsByType<Emberdelve.Interaction.ChestInteractable>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (bounds.Contains(new Vector2(chest.transform.position.x, chest.transform.position.z))) chest.gameObject.SetActive(false);
            _retired.Add(floor); reason = null; return true;
        }
        public bool TryApply(RoyaleMapSnapshot snapshot, out string reason)
        {
            reason = "Destination geometry is not ready";
            if (!NetRole.IsClientOnly || snapshot == null || _initial == null || _catalog == null
                || !_initial.AcceptsSessionPlan || !_initial.RuntimeMapValidationComplete) return false;
            if (_retired.Contains(snapshot.Floor)) { reason = "Destination has retired"; return false; }
            var initialPlan = _initial.CopyAppliedPlan(); var plan = snapshot.CopyPlan();
            if (!uint.TryParse(initialPlan.LayoutHash, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out uint hash) || hash != snapshot.InitialHash
                || !initialPlan.RoyaleMode.HasValue || initialPlan.RoyaleMode != plan.RoyaleMode)
            { reason = "Destination does not belong to the initial map and mode"; return false; }
            if (_maps.TryGetValue(snapshot.Floor, out var existing))
            {
                bool same = existing.snapshot.Hash == snapshot.Hash && existing.map != null
                    && existing.map.RuntimeMapValidationComplete && existing.map.gameObject.activeInHierarchy;
                reason = same ? null : "Cannot replace an installed destination"; return same;
            }
            if (snapshot.Floor == 3 && !_maps.ContainsKey(2)) { reason = "Missing preceding destination"; return false; }
            var layout = _initial.RuntimeLayout;
            float width = DungeonModulePlan.Width * DungeonModulePlan.ModuleWidthCells * layout.CellSize;
            float depth = DungeonModulePlan.Height * DungeonModulePlan.ModuleHeightCells * layout.CellSize;
            var bounds = new Rect(snapshot.Origin.x - 2, snapshot.Origin.z - 2, width + 4, depth + 4);
            bool Overlaps(DungeonModuleSceneAssembler map) => bounds.Overlaps(new Rect(map.RuntimeLayout.origin.x,
                map.RuntimeLayout.origin.z, map.RuntimeLayout.gridWidth * layout.CellSize, map.RuntimeLayout.gridHeight * layout.CellSize));
            if (Overlaps(_initial)) { reason = "Destination overlaps the initial map"; return false; }
            foreach (var value in _maps.Values)
                if (Overlaps(value.map)) { reason = "Destination overlaps another map"; return false; }
            if (!RoyaleFloorCatalogs.TryLoad(snapshot.Floor, out var destinationCatalog, out reason)) return false;
            if (!DungeonModuleSceneAssembler.TryStageFloor(layout, destinationCatalog, plan, snapshot.Origin,
                out var stage, out reason, plan.RoyaleMode)) return false;
            if (!stage.BindStagedFloorIdentity(snapshot.Floor, out reason))
            { stage.DiscardStagedFloor(); return false; }
            if (ChestSpawner.CreateClientPresentationForFloor(stage.RuntimeLayout, stage.transform, snapshot.Floor) == null)
            { stage.DiscardStagedFloor(); reason = "Destination chest presentation failed"; return false; }
            stage.gameObject.SetActive(true); Physics.SyncTransforms();
            _maps.Add(snapshot.Floor, (snapshot, stage)); reason = null; return true;
        }
        public bool PublicStateReady(int floor, Func<string, bool> chestKnown, Func<string, bool> doorKnown)
        {
            if (!_maps.TryGetValue(floor, out var entry) || entry.map == null || chestKnown == null || doorKnown == null) return false;
            var chests = entry.map.GetComponentInChildren<ChestSpawner>();
            if (chests == null) return false;
            foreach (var chest in chests.Chests) if (chest == null || !chestKnown(chest.ChestId)) return false;
            foreach (var door in entry.map.GetComponentsInChildren<Emberdelve.Interaction.DungeonDoorInteractable>())
                if (!doorKnown(door.DoorId)) return false;
            return true;
        }

        public void Dispose()
        {
            foreach (var value in _maps.Values) if (value.map != null) value.map.DiscardStagedFloor();
            _maps.Clear();
            _retired.Clear();
        }
    }
}
