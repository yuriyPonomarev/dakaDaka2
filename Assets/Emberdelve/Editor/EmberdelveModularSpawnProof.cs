using System;
using Emberdelve.Level;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Emberdelve.EditorTools
{
    public static class EmberdelveModularSpawnProof
    {
        // Reconstruct the server's deterministic plan from its seed and authored prefabs in an
        // independent editor scene. Do not use the server's reported player positions as expectations.
        public static bool TryExpectedModularSpawns(int seed, string expectedHash, out Vector3[] spawns, out string reason)
        {
            spawns = null;
            reason = string.Empty;
            if (!Application.isBatchMode) { reason = "modular reconstruction requires the batch mirror"; return false; }
            Scene scene = default;
            DungeonLayoutData scratch = null;
            GameObject root = null;
            try
            {
                scene = EditorSceneManager.OpenScene(EmberdelvePaths.ModularDungeonScenePath, OpenSceneMode.Additive);
                DungeonModuleSceneAssembler assembler = null;
                foreach (GameObject sceneRoot in scene.GetRootGameObjects())
                {
                    assembler = sceneRoot.GetComponentInChildren<DungeonModuleSceneAssembler>(true);
                    if (assembler != null) break;
                }
                if (assembler == null) { reason = "authored modular assembler missing"; return false; }
                SerializedObject serialized = new SerializedObject(assembler);
                DungeonLayoutData source = serialized.FindProperty("sourceLayout").objectReferenceValue as DungeonLayoutData;
                DungeonModuleCatalog catalog = serialized.FindProperty("catalog").objectReferenceValue as DungeonModuleCatalog;
                Vector3 origin = serialized.FindProperty("mapOrigin").vector3Value;
                if (source == null || catalog == null) { reason = "authored layout or catalog missing"; return false; }
                if (!DungeonModulePlanner.TryCreate(seed, catalog.Modules, out DungeonModulePlan plan, out reason)) return false;
                if (!string.Equals(plan.LayoutHash, expectedHash, StringComparison.Ordinal))
                { reason = "reconstructed plan hash differs from server"; return false; }
                scratch = UnityEngine.Object.Instantiate(source);
                scratch.origin = origin;
                scratch.gridWidth = DungeonModulePlan.Width * DungeonModulePlan.ModuleWidthCells;
                scratch.gridHeight = DungeonModulePlan.Height * DungeonModulePlan.ModuleHeightCells;
                root = new GameObject("NetFinalIndependentModules");
                SceneManager.MoveGameObjectToScene(root, scene);
                spawns = new Vector3[plan.Placements.Length];
                for (int i = 0; i < plan.Placements.Length; i++)
                {
                    DungeonModulePlacement p = plan.Placements[i];
                    Vector3 center = origin + new Vector3((p.X * DungeonModulePlan.ModuleWidthCells + DungeonModulePlan.ModuleWidthCells / 2f) * source.CellSize,
                        0f, (p.Y * DungeonModulePlan.ModuleHeightCells + DungeonModulePlan.ModuleHeightCells / 2f) * source.CellSize);
                    DungeonModuleDefinition instance = UnityEngine.Object.Instantiate(catalog.Modules[p.ModuleIndex], center,
                        Quaternion.Euler(0f, p.QuarterTurnsClockwise * 90f, 0f), root.transform);
                    foreach (DungeonModuleSockets local in new[] { DungeonModuleSockets.North, DungeonModuleSockets.East, DungeonModuleSockets.South, DungeonModuleSockets.West })
                    {
                        Transform closure = instance.Closure(local);
                        if (closure != null) closure.gameObject.SetActive(!DungeonModulePlan.Has(p.OpenSockets, DungeonModulePlanner.Rotate(local, p.QuarterTurnsClockwise)));
                    }
                    Physics.SyncTransforms();
                    Vector2Int anchor = scratch.CellFromWorld(instance.PlayerSpawnAnchor.position);
                    Vector2Int min = new Vector2Int(p.X * DungeonModulePlan.ModuleWidthCells + 1, p.Y * DungeonModulePlan.ModuleHeightCells + 1);
                    Vector2Int max = new Vector2Int((p.X + 1) * DungeonModulePlan.ModuleWidthCells - 2, (p.Y + 1) * DungeonModulePlan.ModuleHeightCells - 2);
                    if (!DungeonModuleSceneAssembler.TryFindClearSpawnCell(scratch, instance.transform, anchor, min, max, out Vector2Int cell))
                    { reason = "independent spawn reconstruction failed at module " + i; return false; }
                    spawns[i] = scratch.CellCenterWorld(cell);
                }
                return true;
            }
            catch (Exception e) { reason = e.ToString(); spawns = null; return false; }
            finally
            {
                if (root != null) UnityEngine.Object.DestroyImmediate(root);
                if (scratch != null) UnityEngine.Object.DestroyImmediate(scratch);
                if (scene.IsValid() && scene.isLoaded) EditorSceneManager.CloseScene(scene, true);
            }
        }

    }
}
