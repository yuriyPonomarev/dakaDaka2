using System.Collections.Generic;
using System;
using Emberdelve.Interaction;
using UnityEngine;

namespace Emberdelve.Level
{
    /// <summary>Creates one deterministic, collidable door for each planned east/north socket pair.</summary>
    public static class DungeonDoorwaySpawner
    {
        public const string PrefabResource = "Emberdelve/DungeonDoor";

        public static bool TryBuild(DungeonModulePlan plan, Transform[] moduleRoots,
            out List<DungeonDoorInteractable> doors, out string reason)
        {
            doors = new List<DungeonDoorInteractable>();
            reason = string.Empty;
            if (plan == null || !plan.Validate(out reason)) return false;
            if (moduleRoots == null || moduleRoots.Length != plan.Placements.Length)
            { reason = "doorway build requires one module root per plan placement"; return false; }
            GameObject doorPrefab = Resources.Load<GameObject>(PrefabResource);
            if (doorPrefab == null || doorPrefab.GetComponentInChildren<DungeonDoorInteractable>() == null)
            { reason = "the dungeon door prefab is missing"; return false; }
            var ids = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < plan.Placements.Length; i++)
            {
                DungeonModulePlacement p = plan.Placements[i];
                if (p.X + 1 < DungeonModulePlan.Width && DungeonModulePlan.Has(p.OpenSockets, DungeonModuleSockets.East))
                    if (!AddDoor(doors, ids, moduleRoots[i], moduleRoots[i + 1], "door_" + p.X + "_" + p.Y + "_E", DungeonModuleSockets.East, DungeonModuleSockets.West, p.QuarterTurnsClockwise, plan.Placements[i + 1].QuarterTurnsClockwise, doorPrefab, out reason)) { DestroyCreated(doors); return false; }
                if (p.Y + 1 < DungeonModulePlan.Height && DungeonModulePlan.Has(p.OpenSockets, DungeonModuleSockets.North))
                    if (!AddDoor(doors, ids, moduleRoots[i], moduleRoots[i + DungeonModulePlan.Width], "door_" + p.X + "_" + p.Y + "_N", DungeonModuleSockets.North, DungeonModuleSockets.South, p.QuarterTurnsClockwise, plan.Placements[i + DungeonModulePlan.Width].QuarterTurnsClockwise, doorPrefab, out reason)) { DestroyCreated(doors); return false; }
            }
            int expected = CountSocketPairs(plan);
            if (doors.Count != expected)
            { reason = "created " + doors.Count + " doors for " + expected + " planned socket pairs"; DestroyCreated(doors); return false; }
            // Authored levers share the existing authoritative door state stream, with cell-scoped identities.
            for (int i = 0; i < moduleRoots.Length; i++)
            {
                if (moduleRoots[i] == null) { reason = "secret module root is missing"; DestroyCreated(doors); return false; }
                DungeonDoorInteractable[] attached = moduleRoots[i].GetComponentsInChildren<DungeonDoorInteractable>(true);
                foreach (DungeonDoorInteractable secret in attached)
                {
                    if (!secret.IsSecret) continue;
                    DungeonModulePlacement p = plan.Placements[i];
                    if (!secret.BindModuleCell(p.X, p.Y) || !ids.Add(secret.DoorId))
                    { reason = "secret lever is invalid or has a duplicate room-local key"; DestroyCreated(doors); return false; }
                    doors.Add(secret);
                }
            }
            var actualIds = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < doors.Count; i++)
                if (doors[i] == null || !DungeonDoorInteractable.IsValidDoorId(doors[i].DoorId) || !actualIds.Add(doors[i].DoorId))
                { reason = "created doorway list contains a missing, invalid, or duplicate ID"; DestroyCreated(doors); return false; }
            return true;
        }

        public static bool ValidateBuilt(DungeonModulePlan plan, Transform[] moduleRoots, out string reason)
        {
            reason = string.Empty;
            if (plan == null || !plan.Validate(out reason)) return false;
            if (plan.Placements == null || moduleRoots == null || moduleRoots.Length != plan.Placements.Length)
            { reason = "doorway validation has no matching plan/module roots"; return false; }
            var expected = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < plan.Placements.Length; i++)
            {
                DungeonModulePlacement p = plan.Placements[i];
                if (p.X + 1 < DungeonModulePlan.Width && DungeonModulePlan.Has(p.OpenSockets, DungeonModuleSockets.East))
                    expected.Add("door_" + p.X + "_" + p.Y + "_E");
                if (p.Y + 1 < DungeonModulePlan.Height && DungeonModulePlan.Has(p.OpenSockets, DungeonModuleSockets.North))
                    expected.Add("door_" + p.X + "_" + p.Y + "_N");
            }
            var actual = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < moduleRoots.Length; i++)
            {
                if (moduleRoots[i] == null) { reason = "doorway module root " + i + " is missing"; return false; }
                DungeonDoorInteractable[] attached = moduleRoots[i].GetComponentsInChildren<DungeonDoorInteractable>(true);
                for (int d = 0; d < attached.Length; d++)
                {
                    if (attached[d] != null && attached[d].IsSecret)
                    {
                        DungeonModulePlacement p = plan.Placements[i];
                        string id = attached[d].SecretIdForCell(p.X, p.Y);
                        if (!attached[d].ValidateSecret(out reason) || !expected.Add(id) || attached[d].DoorId != id)
                        { reason = "secret lever is invalid, duplicated, or bound to a different room"; return false; }
                    }
                    if (attached[d] == null || !DungeonDoorInteractable.IsValidDoorId(attached[d].DoorId) || !actual.Add(attached[d].DoorId))
                    { reason = "doorway objects contain a missing, invalid, or duplicate id"; return false; }
                }
            }
            if (actual.Count != expected.Count || !actual.SetEquals(expected))
            { reason = "present " + actual.Count + " doors for " + expected.Count + " planned socket pairs"; return false; }
            return true;
        }

        private static bool AddDoor(List<DungeonDoorInteractable> doors, HashSet<string> ids, Transform aRoot, Transform bRoot,
            string id, DungeonModuleSockets aSide, DungeonModuleSockets bSide, byte aRotation, byte bRotation, GameObject doorPrefab, out string reason)
        {
            reason = string.Empty;
            if (!DungeonDoorInteractable.IsValidDoorId(id) || !ids.Add(id)) { reason = "door id is invalid or duplicated: " + id; return false; }
            if (aRoot == null || bRoot == null) { reason = "module root is missing for door " + id; return false; }
            DungeonModuleDefinition a = aRoot.GetComponent<DungeonModuleDefinition>();
            DungeonModuleDefinition b = bRoot.GetComponent<DungeonModuleDefinition>();
            Transform aSocket = a != null ? a.Socket(DungeonModulePlanner.ToLocalSocketDirection(aSide, aRotation)) : null;
            Transform bSocket = b != null ? b.Socket(DungeonModulePlanner.ToLocalSocketDirection(bSide, bRotation)) : null;
            if (aSocket == null || bSocket == null || Vector3.Distance(aSocket.position, bSocket.position) > 0.1f)
            { reason = "planned doorway sockets do not align for " + id; return false; }

            Vector3 position = (aSocket.position + bSocket.position) * 0.5f;
            bool alongX = aSide == DungeonModuleSockets.North || aSide == DungeonModuleSockets.South;
            Quaternion rotation = Quaternion.Euler(0f, alongX ? 0f : 90f, 0f);
            // Parent to the canonical lower-index room so assembler cleanup cannot leave stale doors behind.
            GameObject root = UnityEngine.Object.Instantiate(doorPrefab, position, rotation, aRoot);
            root.name = "Doorway_" + id;
            var door = root.GetComponentInChildren<DungeonDoorInteractable>();
            door.name = "Door_" + id;
            door.Configure(id);
            doors.Add(door);
            return true;
        }

        private static int CountSocketPairs(DungeonModulePlan plan)
        {
            int count = 0;
            for (int i = 0; i < plan.Placements.Length; i++)
            {
                DungeonModulePlacement p = plan.Placements[i];
                if (p.X + 1 < DungeonModulePlan.Width && DungeonModulePlan.Has(p.OpenSockets, DungeonModuleSockets.East)) count++;
                if (p.Y + 1 < DungeonModulePlan.Height && DungeonModulePlan.Has(p.OpenSockets, DungeonModuleSockets.North)) count++;
            }
            return count;
        }

        private static void DestroyCreated(List<DungeonDoorInteractable> doors)
        {
            for (int i = 0; i < doors.Count; i++)
                if (doors[i] != null && !doors[i].IsSecret)
                {
                    var root = doors[i].transform.parent.gameObject;
                    root.SetActive(false);
                    if (Application.isPlaying) UnityEngine.Object.Destroy(root);
                    else UnityEngine.Object.DestroyImmediate(root);
                }
        }
    }
}
