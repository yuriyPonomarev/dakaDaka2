using System;
using System.Collections.Generic;
using Emberdelve.Feedback;
using Emberdelve.Level;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Emberdelve.EditorTools
{
    internal static class MeshyMapDressing
    {
        internal const string Folder = "Assets/Emberdelve/Art/Models/AI/Meshy/MapDressing";
        private const string RoomGroup = "Meshy_Map_Dressing";
        private const string WaysideGroup = "Wayside_Offering";
        private const string WallSconcePath = Folder + "/WallTorch/WallSconce.prefab";
        private const float KneelingHeight = 2.10f;
        private const float StandingHeight = 2.22f;

        internal static GameObject Load(string name)
        {
            return AssetDatabase.LoadAssetAtPath<GameObject>($"{Folder}/{name}/{name}.prefab");
        }

        internal static bool TryBuildSconce(Transform root, out Vector3 flame)
        {
            flame = Vector3.zero;
            var source = Load("WallTorch");
            if (source == null) return false;

            var model = (GameObject)PrefabUtility.InstantiatePrefab(source, root);
            model.name = EmberdelveTorchFactory.ModelName;
            model.transform.localPosition = new Vector3(0f, -.23f, .045f);
            foreach (var renderer in model.GetComponentsInChildren<Renderer>())
                renderer.shadowCastingMode = ShadowCastingMode.Off;

            var flameSocket = model.transform.Find("Flame_Socket");
            if (flameSocket == null) throw new InvalidOperationException("The Meshy wall torch requires a Flame_Socket.");
            flame = root.InverseTransformPoint(flameSocket.position);
            return true;
        }

        internal static bool ApplyRoom(GameObject room)
        {
            var sconceSource = AssetDatabase.LoadAssetAtPath<GameObject>(WallSconcePath);
            if (sconceSource == null) return false;
            var jamb = room.transform.Find("Arch_Jamb_North_A");
            if (jamb == null) return false;
            var old = room.transform.Find(RoomGroup);
            if (old != null) Object.DestroyImmediate(old.gameObject);
            var group = new GameObject(RoomGroup).transform;
            group.SetParent(room.transform, false);

            var jambBounds = jamb.GetComponent<Renderer>().bounds;
            var mount = new Vector3(jambBounds.center.x, jambBounds.center.y, jambBounds.min.z - .12f);
            var sconce = (GameObject)PrefabUtility.InstantiatePrefab(sconceSource, group);
            sconce.name = "Wall_Torch";
            sconce.transform.SetPositionAndRotation(mount, Quaternion.Euler(0f, 180f, 0f));

            ApplyAltar(room);
            return true;
        }

        internal static bool ApplyAltar(GameObject room)
        {
            var group = room.transform.Find(RoomGroup);
            if (group == null || room.GetComponent<DungeonModuleDefinition>()?.Kind != DungeonModuleKind.Shrine)
                return false;
            var altar = room.transform.Find("Ruined_Altar/Altar_Table")
                ?? room.transform.Find("Sepulchral_Altar") ?? room.transform.Find("Sacrificial_Anvil");
            if (altar == null) return false;

            var old = group.Find("Shrine_Display");
            if (old != null) Object.DestroyImmediate(old.gameObject);

            Bounds surface = altar.GetComponent<Renderer>().bounds;
            var origin = new Vector3(surface.center.x, surface.max.y, surface.center.z);
            Vector3 roomDirection = Vector3.ProjectOnPlane(room.transform.position - origin, Vector3.up);
            Vector3 forward = Vector3.Dot(roomDirection, altar.forward) >= 0f ? altar.forward : -altar.forward;
            var rotation = Quaternion.LookRotation(forward, Vector3.up);
            var display = new GameObject("Shrine_Display").transform;
            display.SetParent(group, false);
            display.SetPositionAndRotation(origin, rotation);
            PlaceStatue(display, "KneelingShrine", new Vector3(-.83f, 0f, -.07f));
            PlaceStatue(display, "StandingShrine", new Vector3(.83f, 0f, -.07f));
            Place(display, "VotiveStand", new Vector3(0f, 0f, .31f), .54f);
            return true;
        }

        internal static bool ApplyWayside(GameObject room)
        {
            var decoration = room.transform.Find(RoomGroup);
            var definition = room.GetComponent<DungeonModuleDefinition>();
            if (decoration == null || definition == null) return false;
            string asset;
            switch (definition.Kind)
            {
                case DungeonModuleKind.Entry:
                    asset = "KneelingShrine";
                    break;
                case DungeonModuleKind.Ossuary:
                    bool hasStandingMonk = definition.ModuleId.EndsWith("_01", StringComparison.Ordinal);
                    asset = hasStandingMonk ? "StandingShrine" : "KneelingShrine";
                    break;
                case DungeonModuleKind.Extraction:
                    asset = "VotiveStand";
                    break;
                default:
                    return false;
            }

            var old = decoration.Find(WaysideGroup);
            if (old != null) Object.DestroyImmediate(old.gameObject);
            var colliders = room.GetComponentsInChildren<Collider>();
            var scenery = room.GetComponentsInChildren<MeshRenderer>();
            var supports = new List<BoxCollider>();
            foreach (var collider in colliders)
            {
                if (collider is BoxCollider box && !box.isTrigger && SupportPriority(box) < 3)
                    supports.Add(box);
            }
            supports.Sort((a, b) => SupportPriority(a).CompareTo(SupportPriority(b)));

            var display = new GameObject(WaysideGroup).transform;
            display.SetParent(decoration, false);
            bool candles = definition.Kind == DungeonModuleKind.Extraction;
            var instance = candles ? Place(display, asset, Vector3.zero, .57f) : PlaceStatue(display, asset, Vector3.zero);
            var modelRenderer = instance.Find("Model").GetComponent<MeshRenderer>();
            var footprint = modelRenderer.gameObject.AddComponent<BoxCollider>();
            footprint.center = modelRenderer.localBounds.center;
            footprint.size = modelRenderer.localBounds.size;
            try
            {
                if (!candles && TryFloorNiche(room.transform, display, footprint, colliders, scenery))
                    return true;
                foreach (var support in supports)
                {
                    // The side of a lid has space beside the carved effigy. Altars can also use the center.
                    foreach (float z in new[] { -.36f, .36f, 0f })
                    foreach (float x in new[] { -.34f, .34f, -.30f, .30f, 0f })
                    {
                        Vector3 top = support.center + Vector3.Scale(support.size, new Vector3(x, .5f, z));
                        Vector3 origin = support.transform.TransformPoint(top);
                        Vector3 facing = Vector3.ProjectOnPlane(room.transform.position - origin, Vector3.up);
                        display.SetPositionAndRotation(origin, Quaternion.LookRotation(facing, Vector3.up));
                        if (!FitsTop(support, footprint) || !ClearOfMasonry(footprint, colliders, support)) continue;
                        return true;
                    }
                }
            }
            finally { Object.DestroyImmediate(footprint); }
            Object.DestroyImmediate(display.gameObject);
            return false;
        }

        private static bool TryFloorNiche(Transform room, Transform display, BoxCollider footprint, Collider[] colliders,
            MeshRenderer[] scenery)
        {
            // Side wings contain gallery stairs, an undercroft and a hidden cache.
            // Only the north/south ground-floor bays are eligible for new floor statues.
            float depth = DungeonModulePlan.ModuleWidthCells - 1.04f;
            var courtyardArt = room.Find("Community_Ruins_Trial");
            Vector3[] sides = { Vector3.back, Vector3.forward };
            foreach (Vector3 side in sides)
            {
                Vector3 along = Vector3.Cross(Vector3.up, side);
                // Off-center positions on the wall stay between the two-meter gameplay cell centers.
                foreach (float offset in new[] { 8f, -8f, 6f, -6f, 4f, -4f, 10f, -10f })
                {
                    Vector3 position = room.TransformPoint(side * depth + along * offset);
                    Collider floorCollider = null;
                    var floorRay = new Ray(position + room.up * .35f, -room.up);
                    foreach (var candidate in colliders)
                    {
                        if (candidate.transform.parent != room || candidate.isTrigger
                            || (candidate.name != "Floor" && !candidate.name.StartsWith("Floor_", StringComparison.Ordinal))) continue;
                        if (!candidate.Raycast(floorRay, out RaycastHit hit, .7f)) continue;
                        floorCollider = candidate;
                        position = hit.point;
                        break;
                    }
                    if (floorCollider == null) continue;
                    display.SetPositionAndRotation(position, Quaternion.LookRotation(room.TransformDirection(-side), Vector3.up));
                    if (!ClearOfMasonry(footprint, colliders, floorCollider)) continue;
                    Bounds bounds = footprint.bounds;
                    bool overlapsCourtyardArt = false;
                    if (courtyardArt != null)
                        foreach (var renderer in scenery)
                            if (renderer.enabled && renderer.transform.IsChildOf(courtyardArt) && bounds.Intersects(renderer.bounds))
                            {
                                overlapsCourtyardArt = true;
                                break;
                            }
                    if (overlapsCourtyardArt) continue;
                    Vector3 eye = position + display.forward * 3.5f + display.right * .65f + Vector3.up * 1.64f;
                    Vector3 shoulders = new Vector3(bounds.center.x, Mathf.Lerp(bounds.min.y, bounds.max.y, .73f), bounds.center.z);
                    if (HasOpenView(eye, bounds.center, scenery) && HasOpenView(eye, shoulders, scenery))
                        return true;
                }
            }
            return false;
        }

        private static bool HasOpenView(Vector3 eye, Vector3 target, MeshRenderer[] scenery)
        {
            Vector3 displacement = target - eye;
            float range = displacement.magnitude;
            var ray = new Ray(eye, displacement / range);
            foreach (var renderer in scenery)
            {
                if (renderer.enabled && renderer.bounds.IntersectRay(ray, out float distance) && distance < range - .13f)
                    return false;
            }
            return true;
        }

        private static int SupportPriority(BoxCollider box)
        {
            if (box.name == "Sepulchral_Altar" || box.name == "Sacrificial_Anvil") return 0;
            if (box.name == "Lid" && box.transform.parent.name == "Sarcophagus") return 1;
            return box.name == "Furnace_Crown" ? 2 : 3;
        }

        private static bool FitsTop(BoxCollider support, BoxCollider footprint)
        {
            var half = support.size * .5f;
            for (int i = 0; i < 4; i++)
            {
                var corner = new Vector3((i & 1) == 0 ? -.5f : .5f, -.5f, (i & 2) == 0 ? -.5f : .5f);
                Vector3 edge = footprint.transform.TransformPoint(footprint.center + Vector3.Scale(footprint.size, corner));
                Vector3 offset = support.transform.InverseTransformPoint(edge) - support.center;
                if (Mathf.Abs(offset.x) > half.x - .015f || Mathf.Abs(offset.z) > half.z - .015f)
                    return false;
            }
            return true;
        }

        private static bool ClearOfMasonry(BoxCollider footprint, Collider[] colliders, Collider support)
        {
            foreach (var collider in colliders)
            {
                if (collider == support || collider.isTrigger || !footprint.bounds.Intersects(collider.bounds)) continue;
                if (Physics.ComputePenetration(footprint, footprint.transform.position, footprint.transform.rotation,
                    collider, collider.transform.position, collider.transform.rotation, out _, out float depth) && depth > .006f)
                    return false;
            }
            return true;
        }

        private static Transform Place(Transform parent, string id, Vector3 offset, float scale)
        {
            var source = Load(id);
            if (source == null) throw new InvalidOperationException("Missing room decoration " + id);
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(source, parent);
            instance.name = id;
            instance.transform.localPosition = offset;
            instance.transform.localScale = Vector3.one * scale;
            return instance.transform;
        }

        private static Transform PlaceStatue(Transform display, string id, Vector3 offset)
        {
            var model = Place(display, id, offset, 1f);
            float desiredHeight = id == "StandingShrine" ? StandingHeight : KneelingHeight;
            float meshHeight = model.Find("Model").GetComponent<MeshRenderer>().localBounds.size.y;
            model.localScale = Vector3.one * (desiredHeight / meshHeight);
            return model;
        }

        // Apply only the new art to existing saved content, without rebuilding the maps.
        public static void RefreshSavedArt()
        {
            if (!Application.isBatchMode)
                throw new InvalidOperationException("Apply saved map art on the batch mirror.");
            EmberdelvePropFactory.EnsureChestVisualSet();
            var wallSconce = EmberdelveTorchFactory.CreateFixedMeshySconce();
            try { PrefabUtility.SaveAsPrefabAsset(wallSconce, WallSconcePath); }
            finally { Object.DestroyImmediate(wallSconce); }
            int rooms = 0;
            foreach (string id in AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/Emberdelve/Prefabs/DungeonModules" }))
            {
                string path = AssetDatabase.GUIDToAssetPath(id);
                var room = PrefabUtility.LoadPrefabContents(path);
                try
                {
                    if (!ApplyRoom(room)) continue;
                    ApplyWayside(room);
                    PrefabUtility.SaveAsPrefabAsset(room, path);
                    rooms++;
                }
                finally { PrefabUtility.UnloadPrefabContents(room); }
            }

            int sconces = 0;
            foreach (string sceneName in new[] { "Emberdelve_Level01", "Emberdelve_Crypts", "Emberdelve_ModularCrypts" })
            {
                var scene = EditorSceneManager.OpenScene($"Assets/Emberdelve/Scenes/{sceneName}.unity", OpenSceneMode.Single);
                foreach (var root in scene.GetRootGameObjects())
                    foreach (var flicker in root.GetComponentsInChildren<TorchFlicker>(true))
                    {
                        var sconce = flicker.transform.Find(EmberdelveTorchFactory.SconceName);
                        if (sconce == null) continue;
                        var flame = sconce.Find(EmberdelveTorchFactory.FlameName);
                        if (flame == null) throw new InvalidOperationException("Missing wall torch flame " + flicker.name);
                        for (int i = sconce.childCount - 1; i >= 0; i--)
                            if (sconce.GetChild(i) != flame) Object.DestroyImmediate(sconce.GetChild(i).gameObject);
                        if (!TryBuildSconce(sconce, out Vector3 flamePosition))
                            throw new InvalidOperationException("Missing imported wall torch.");
                        flame.localPosition = flamePosition;
                        sconces++;
                    }
                EditorSceneManager.SaveScene(scene);
            }
            AssetDatabase.SaveAssets();
            Debug.Log($"[MeshyMap] Applied room dressings to {rooms} room prefabs and updated {sconces} scene sconces.");
        }
    }
}
