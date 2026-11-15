using System.Collections.Generic;
using Emberdelve.AI;
using Emberdelve.Core;
using Emberdelve.Interaction;
using Emberdelve.Level;
using Emberdelve.Rules;
using UnityEngine;

namespace Emberdelve.Flow
{
    /// <summary>One authority floor's zone and physical descent waves. Network views use a separate transport adapter.</summary>
    public sealed class RoyaleFloorEncounter : MonoBehaviour
    {
        private RunDirector _director;
        private int _floor;
        private float _lastDamageAt;
        private readonly List<DescentPortalGroup> _groups = new List<DescentPortalGroup>();
        public IReadOnlyList<DescentPortalGroup> Groups => _groups;
        public ExtractionPoint FinalExit { get; private set; }
        public SwarmSchedule Swarm { get; private set; }
        public DescentPortalSchedule Portals { get; private set; }
        public bool TrySample(out SwarmSample sample)
        {
            sample = default;
            return _director != null && _director.TryGetRoyaleFloorClock(_floor, out float elapsed, out _)
                && Swarm.TrySample(elapsed, out sample);
        }

        public static bool TryCreate(RunDirector director, DungeonModuleSceneAssembler map, int floor,
            int duration, out RoyaleFloorEncounter encounter, out string reason)
        {
            encounter = null; reason = "Floor encounter is not ready";
            if (NetRole.IsClientOnly || director == null || !director.RoyaleMode.HasValue || map == null
                || !map.RuntimeMapValidationComplete || floor < 1 || floor > 3 || duration < 90) return false;
            var nav = map.RuntimeNavGrid; var layout = map.RuntimeLayout;
            if (nav == null || layout == null) return false;
            float width = nav.width * nav.cellSize, depth = nav.height * nav.cellSize;
            Vector3 center = nav.origin + new Vector3(width * .5f, 0, depth * .5f);
            float initial = Mathf.Sqrt(width * width + depth * depth) * .5f + 2f;
            float final = Mathf.Min(initial * .65f, Mathf.Max(12f, Mathf.Min(width, depth) * .28f));
            var swarm = new SwarmSchedule(center.x, center.z, initial,
                new SwarmPhase(duration * .2, duration * .25, center.x, center.z, initial * .7, 1),
                new SwarmPhase(duration * .1, duration * .25, center.x, center.z, final, 2),
                new SwarmPhase(duration * .1, duration * .1, center.x, center.z, final, 3));
            int size = floor == 3 ? 1 : (int)director.RoyaleMode.Value;
            var candidates = new List<PortalLocation>();
            var footprints = new Dictionary<Vector2Int, Vector3[]>();
            var navigation = new NavGridPathfinder(nav); var route = new List<Vector3>();
            float clearance = size == 1 ? 1f : size == 2 ? 2f : 3f;
            Material material = null;
            {
                foreach (var renderer in Object.FindObjectsByType<MeshRenderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                    if (renderer.name == "Gate_Portal") { material = renderer.sharedMaterial; break; }
                if (material == null) { reason = "Missing existing portal material"; return false; }
                for (int z = 1; z < nav.height - 1; z += 2) for (int x = 1; x < nav.width - 1; x += 2)
                {
                    var point = nav.CellCenter(x, z);
                    if (!nav.IsWalkable(x, z) || !swarm.ContainsThroughEnd(point.x, point.z, clearance)) continue;
                    var slots = new Vector3[size]; bool valid = true;
                    for (int i = 0; i < size; i++)
                    {
                        var cell = nav.CellFromWorld(point + Vector3.right * ((i - (size - 1) * .5f) * 2f));
                        var gridCell = new Vector2Int(cell.x, cell.y);
                        if (!nav.IsWalkable(cell.x, cell.y)
                            || !DungeonModuleSceneAssembler.TryFindClearSpawnCell(layout, map.transform, gridCell, gridCell, gridCell, out _))
                        { valid = false; break; }
                        slots[i] = nav.CellCenter(cell.x, cell.y);
                        if (Mathf.Abs(slots[i].y - point.y) > .15f || !swarm.ContainsThroughEnd(slots[i].x, slots[i].z, .75f)
                            || Physics.CheckCapsule(slots[i] + Vector3.up * .38f, slots[i] + Vector3.up * 1.44f,
                                .36f, Physics.AllLayers, QueryTriggerInteraction.Ignore))
                        { valid = false; break; }
                    }
                    if (!valid || !navigation.TryFindPath(layout.PlayerSpawnWorld(0), point, route)) continue;
                    candidates.Add(new PortalLocation(point.x, point.z)); footprints[new Vector2Int(x, z)] = slots;
                }
            }
            PortalLocation[] anchors = System.Array.Empty<PortalLocation>();
            if (!DescentPortalPlacement.TryChoose(swarm, candidates, floor == 3 ? 1 : 3, clearance, clearance * 2 + 2,
                unchecked((uint)map.CopyAppliedPlan().Seed ^ (uint)(floor * 7919)), out anchors))
            { reason = "No reachable portal footprints inside final zone"; return false; }
            var root = new GameObject("Floor" + floor + "_ZoneAndPortals"); root.transform.SetParent(map.transform, false);
            encounter = root.AddComponent<RoyaleFloorEncounter>(); encounter._director = director; encounter._floor = floor; encounter.Swarm = swarm;
            if (floor == 3)
            {
                var cell = nav.CellFromWorld(new Vector3((float)anchors[0].X, 0, (float)anchors[0].Z));
                encounter.FinalExit = ExtractionPoint.CreateFinalView(root.transform, footprints[new Vector2Int(cell.x, cell.y)][0] + Vector3.up * 1.1f,
                    director.Config != null ? director.Config.extractionHoldDuration : 3f, director);
                if (encounter.FinalExit == null)
                {
                    if (Application.isPlaying) Destroy(root); else DestroyImmediate(root);
                    encounter = null; reason = "Final extraction view could not be created"; return false;
                }
                encounter.FinalExit.gameObject.SetActive(false);
            }
            if (floor < 3)
            {
                double last = duration - 60;
                encounter.Portals = new DescentPortalSchedule(duration, director.RoyaleMode.Value,
                    new DescentPortalWave(last * .5, 1), new DescentPortalWave(last * .8, 1), new DescentPortalWave(last, 1));
                int layer = LayerMask.NameToLayer(GameConfig.InteractLayerName);
                for (int group = 0; group < anchors.Length; group++)
                {
                    var cell = nav.CellFromWorld(new Vector3((float)anchors[group].X, 0, (float)anchors[group].Z));
                    var positions = footprints[new Vector2Int(cell.x, cell.y)];
                    var go = new GameObject("DescentGroup_" + group); go.transform.SetParent(root.transform, false);
                    go.transform.position = nav.CellCenter(cell.x, cell.y);
                    var portal = go.AddComponent<DescentPortalGroup>();
                    portal.Configure(director, encounter.Portals, floor, group); encounter._groups.Add(portal);
                    for (int slot = 0; slot < size; slot++)
                    {
                        var view = DescentPortalVisual.Create(go.transform, "DescentSlot_" + slot, material, layer);
                        view.transform.position = positions[slot] + Vector3.up * 1.1f;
                        view.AddComponent<DescentPortalSlot>().Configure(portal, slot);
                    }
                    go.SetActive(false);
                }
            }
            reason = null; return true;
        }

        private RoyaleZoneBoundary _localBoundary;
        private void Update() { Tick(); UpdateLocalBoundary(); }
        private void UpdateLocalBoundary()
        {
            if (Application.isBatchMode || NetRole.IsClientOnly || _director == null) return;
            var actor = _director.Player;
            var session = _director.SessionOf(actor);
            if (actor == null || session == null || session.Floor != _floor || !actor.IsInPlay || actor.IsDead
                || !_director.IsRoyaleFloorOpen(_floor) || !TrySample(out var sample))
            { if (_localBoundary != null) _localBoundary.Hide(); return; }
            if (_localBoundary == null) _localBoundary = new GameObject("Local_ZoneBoundary").AddComponent<RoyaleZoneBoundary>();
            _localBoundary.Apply(new Vector2((float)sample.X, (float)sample.Z), (float)sample.Radius, actor.transform.position.y);
        }
        private void OnDisable() { if (_localBoundary != null) _localBoundary.Hide(); }
        private void OnDestroy() { if (_localBoundary != null) Destroy(_localBoundary.gameObject); }
        public void Tick()
        {
            if (NetRole.IsClientOnly || _director == null) return;
            bool open = _director.IsRoyaleFloorOpen(_floor);
            if (FinalExit != null) FinalExit.gameObject.SetActive(open);
            _director.TryGetRoyaleFloorClock(_floor, out float elapsed, out _);
            for (int i = 0; i < _groups.Count; i++)
                if (_groups[i] != null) _groups[i].gameObject.SetActive(open && Portals.IsGroupOpen(i, elapsed));
            if (!open || _director.State != RunState.Running || elapsed - _lastDamageAt < 1f || !Swarm.TrySample(elapsed, out var zone)) return;
            float damageSeconds = Mathf.Min(1f, elapsed - _lastDamageAt); _lastDamageAt = elapsed;
            foreach (var session in new List<PlayerSession>(_director.Players))
            {
                var rig = session.Rig;
                if (session.Floor != _floor || !session.IsRunning || rig == null || rig.IsDead || !rig.IsInPlay
                    || zone.Contains(rig.transform.position.x, rig.transform.position.z)) continue;
                rig.ApplyDamage(new DamageInfo((float)zone.DamagePerSecond * damageSeconds, rig.transform.position,
                    Vector3.zero, rig.transform.position, 0, EntityKind.Neutral, DamageType.Physical, false, false, 0));
            }
        }
    }
}
