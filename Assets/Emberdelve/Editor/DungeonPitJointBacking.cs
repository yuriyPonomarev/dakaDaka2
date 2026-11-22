using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Emberdelve.EditorTools
{
    /// <summary>Closes below-ground bevel and tread joints using render geometry only.</summary>
    public static class DungeonPitJointBacking
    {
        public const string MeshFolder = "Assets/Emberdelve/Art/Generated/PitJointBacking";
        private const string ChildName = "Pit_Joint_Backing";
        private const string FloorPrefix = "PitFloor_v1__";
        private const float Epsilon = .00002f;
        private static readonly Dictionary<string, Mesh> Cache = new();

        public static int Apply(GameObject root)
        {
            RequirePrimary();
            if (root == null) throw new ArgumentNullException(nameof(root));
            MeshFilter floor = LowerFloor(root);
            if (floor == null) return 0;
            Transform[] runs = Runs(root);
            Preflight(floor, runs);
            var before = new GameplaySnapshot(root);
            Mesh source = ResolveOriginalMesh(floor.sharedMesh);
            string guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(source));
            if (string.IsNullOrEmpty(guid)) throw new InvalidOperationException("Lower floor must use a saved source mesh");
            Vector3 scale = floor.transform.localScale;
            string key = FloorPrefix + guid + "__" + Number(scale.x) + "_" + Number(scale.z) + "_" +
                AssetDatabase.GetAssetDependencyHash(AssetDatabase.GetAssetPath(source));
            Mesh fitted = Asset(key, () => ExpandFloor(source, scale));
            int changed = floor.sharedMesh == fitted ? 0 : 1;
            floor.sharedMesh = fitted;
            foreach (Transform run in runs)
            {
                MeshFilter[] steps = Steps(run);
                float width = steps[0].sharedMesh.bounds.size.x * steps[0].transform.localScale.x;
                float length = steps.Length * .3f;
                Mesh wedge = Asset("PitWedge_v1_" + Number(width) + "_" + Number(length), () => Wedge(width, length));
                Transform child = run.Find(ChildName);
                if (child == null)
                {
                    child = new GameObject(ChildName).transform;
                    child.SetParent(run, false);
                    child.gameObject.layer = run.gameObject.layer;
                    child.gameObject.AddComponent<MeshFilter>();
                    child.gameObject.AddComponent<MeshRenderer>();
                    changed++;
                }
                RequireOwnedChild(child);
                if (child.localPosition != Vector3.zero || child.localRotation != Quaternion.identity || child.localScale != Vector3.one) changed++;
                child.localPosition = Vector3.zero;
                child.localRotation = Quaternion.identity;
                child.localScale = Vector3.one;
                MeshFilter filter = child.GetComponent<MeshFilter>();
                MeshRenderer renderer = child.GetComponent<MeshRenderer>();
                Material material = steps[0].GetComponent<MeshRenderer>().sharedMaterial;
                if (filter.sharedMesh != wedge || renderer.sharedMaterial != material || !renderer.enabled) changed++;
                filter.sharedMesh = wedge;
                renderer.sharedMaterial = material;
                renderer.enabled = true;
            }
            before.AssertUnchanged(root);
            Validate(root);
            return changed;
        }

        /// <summary>Unwraps the widened floor for the base bevel exporter and validator.</summary>
        public static Mesh ResolveOriginalMesh(Mesh mesh)
        {
            if (mesh == null) throw new InvalidOperationException("Missing lower floor mesh");
            string path = AssetDatabase.GetAssetPath(mesh);
            if (!path.StartsWith(MeshFolder + "/", StringComparison.Ordinal) || !mesh.name.StartsWith(FloorPrefix, StringComparison.Ordinal)) return mesh;
            string guid = mesh.name.Substring(FloorPrefix.Length).Split(new[] { "__" }, StringSplitOptions.None)[0];
            Mesh original = AssetDatabase.LoadAssetAtPath<Mesh>(AssetDatabase.GUIDToAssetPath(guid));
            if (original == null || original == mesh) throw new InvalidOperationException("Missing source of pit floor derivative");
            return original;
        }

        [MenuItem("Emberdelve/Visual/Traversal/6 Seal Saved Pit Joints")]
        public static void RepairSaved()
        {
            RequirePrimary();
            Cache.Clear();
            string[] paths = AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/Emberdelve/Prefabs/DungeonModules" })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(p => p.EndsWith("_Hall_02.prefab", StringComparison.Ordinal) || p.EndsWith("_Ossuary_02.prefab", StringComparison.Ordinal))
                .OrderBy(p => p, StringComparer.Ordinal).ToArray();
            if (paths.Length != 6) throw new InvalidOperationException("Expected exactly six gallery/pit rooms");
            foreach (string path in paths)
            {
                GameObject root = PrefabUtility.LoadPrefabContents(path);
                try { Preflight(LowerFloor(root), Runs(root)); }
                finally { PrefabUtility.UnloadPrefabContents(root); }
            }
            int changed = 0;
            foreach (string path in paths)
            {
                GameObject root = PrefabUtility.LoadPrefabContents(path);
                try
                {
                    var before = new GameplaySnapshot(root);
                    int edits = Apply(root);
                    before.AssertUnchanged(root);
                    if (edits != 0)
                    {
                        if (PrefabUtility.SaveAsPrefabAsset(root, path) == null) throw new InvalidOperationException("Could not save " + path);
                        before.AssertUnchanged(root);
                        changed++;
                    }
                    Validate(root);
                }
                finally { PrefabUtility.UnloadPrefabContents(root); }
                root = PrefabUtility.LoadPrefabContents(path);
                try { Validate(root); }
                finally { PrefabUtility.UnloadPrefabContents(root); }
            }
            Debug.Log("[PitJointBacking] repaired=" + changed + "/6; existing transforms and serialized colliders/scripts unchanged");
        }

        public static int Validate(GameObject root)
        {
            MeshFilter floor = LowerFloor(root);
            if (floor == null) return 0;
            Transform[] runs = Runs(root);
            Preflight(floor, runs);
            Mesh original = ResolveOriginalMesh(floor.sharedMesh);
            if (original == floor.sharedMesh) throw new InvalidOperationException("Lower floor has no joint underlap");
            Vector3 scale = floor.transform.localScale;
            Bounds a = original.bounds, b = floor.sharedMesh.bounds;
            Near((b.size.x - a.size.x) * scale.x, .20f, "floor X underlap");
            Near((b.size.z - a.size.z) * scale.z, .12f, "floor Z underlap");
            Near(b.max.y, a.max.y, "floor walking height");
            Near(b.min.y, a.min.y, "floor underside");
            if ((a.center - b.center).sqrMagnitude > Epsilon * Epsilon) throw new InvalidOperationException("Floor centre moved");
            foreach (Transform run in runs)
            {
                Transform child = run.Find(ChildName);
                RequireOwnedChild(child);
                if (child.localPosition != Vector3.zero || child.localRotation != Quaternion.identity || child.localScale != Vector3.one)
                    throw new InvalidOperationException("Backing transform moved");
                MeshFilter[] steps = Steps(run);
                float width = steps[0].sharedMesh.bounds.size.x * steps[0].transform.localScale.x;
                Mesh mesh = child.GetComponent<MeshFilter>().sharedMesh;
                if (mesh == null || mesh.vertexCount != 8 || mesh.triangles.Length != 36)
                    throw new InvalidOperationException("Backing must be a closed eight-vertex wedge");
                Vector3[] expected = WedgeVertices(width, steps.Length * .3f), actual = mesh.vertices;
                for (int i = 0; i < expected.Length; i++)
                    if ((expected[i] - actual[i]).sqrMagnitude > Epsilon * Epsilon) throw new InvalidOperationException("Backing profile changed");
                if (!mesh.triangles.SequenceEqual(WedgeTriangles)) throw new InvalidOperationException("Backing closure changed");
                if (child.GetComponent<MeshRenderer>().sharedMaterial != steps[0].GetComponent<MeshRenderer>().sharedMaterial)
                    throw new InvalidOperationException("Backing lost the stair stone material");
            }
            return runs.Length;
        }

        private static Mesh ExpandFloor(Mesh source, Vector3 scale)
        {
            var mesh = Object.Instantiate(source);
            Bounds bounds = source.bounds;
            Vector3 stretch = new(1 + .20f / (bounds.size.x * scale.x), 1, 1 + .12f / (bounds.size.z * scale.z));
            Vector3[] vertices = mesh.vertices, normals = mesh.normals;
            for (int i = 0; i < vertices.Length; i++) vertices[i] = bounds.center + Vector3.Scale(vertices[i] - bounds.center, stretch);
            for (int i = 0; i < normals.Length; i++) normals[i] = new Vector3(normals[i].x / stretch.x, normals[i].y, normals[i].z / stretch.z).normalized;
            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.RecalculateBounds();
            if (mesh.uv.Length == mesh.vertexCount) mesh.RecalculateTangents();
            return mesh;
        }

        // Eight shared vertices, outward winding, two triangles on each of six closed faces.
        private static readonly int[] WedgeTriangles =
        {
            0,1,2, 0,2,3, 4,6,5, 4,7,6,
            0,5,1, 0,4,5, 3,6,7, 3,2,6,
            0,7,4, 0,3,7, 1,6,2, 1,5,6
        };
        private static Vector3[] WedgeVertices(float width, float run)
        {
            float x = (width + .20f) * .5f, z0 = -.06f, z1 = run + .06f, bottom = -.15f;
            float y0 = Mathf.Max(bottom + .01f, .5f * z0 - .05f), y1 = .5f * z1 - .05f;
            return new[]
            {
                new Vector3(-x,bottom,z0), new Vector3(x,bottom,z0), new Vector3(x,bottom,z1), new Vector3(-x,bottom,z1),
                new Vector3(-x,y0,z0), new Vector3(x,y0,z0), new Vector3(x,y1,z1), new Vector3(-x,y1,z1)
            };
        }
        private static Mesh Wedge(float width, float run)
        {
            var mesh = new Mesh { vertices = WedgeVertices(width, run), triangles = WedgeTriangles };
            mesh.uv = mesh.vertices.Select(v => new Vector2(v.x, v.z)).ToArray();
            mesh.RecalculateNormals(); mesh.RecalculateBounds(); mesh.RecalculateTangents();
            return mesh;
        }
        private static Mesh Asset(string key, Func<Mesh> create)
        {
            if (Cache.TryGetValue(key, out Mesh cached) && cached != null) return cached;
            if (!AssetDatabase.IsValidFolder(MeshFolder)) AssetDatabase.CreateFolder("Assets/Emberdelve/Art/Generated", "PitJointBacking");
            string path = MeshFolder + "/" + key + ".asset";
            Mesh mesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (mesh == null) { mesh = create(); mesh.name = key; AssetDatabase.CreateAsset(mesh, path); }
            Cache[key] = mesh;
            return mesh;
        }
        private static MeshFilter LowerFloor(GameObject root)
        {
            Transform floor = root.transform.Find("Undercroft_Floor") ?? root.transform.Find("Floor_SunkenCrypt");
            return floor == null ? null : floor.GetComponent<MeshFilter>();
        }
        private static Transform[] Runs(GameObject root) => root.GetComponentsInChildren<Transform>(true)
            .Where(t => t.name == "Undercroft_NorthStairs" || t.name == "Undercroft_SouthStairs" ||
                t.name == "Layout_Stairs_PitNorth" || t.name == "Layout_Stairs_PitSouth").ToArray();
        private static MeshFilter[] Steps(Transform run) => run.Cast<Transform>()
            .Where(t => t.name.StartsWith("Step_", StringComparison.Ordinal) || t.name.StartsWith("Tread_", StringComparison.Ordinal))
            .OrderBy(t => int.Parse(t.name.Substring(t.name.IndexOf('_') + 1), CultureInfo.InvariantCulture))
            .Select(t => t.GetComponent<MeshFilter>()).ToArray();
        private static void Preflight(MeshFilter floor, Transform[] runs)
        {
            if (floor == null || runs.Length != 2) throw new InvalidOperationException("Expected one lower floor and two pit stairs");
            if (floor.transform.localScale.x <= 0 || floor.transform.localScale.z <= 0) throw new InvalidOperationException("Invalid lower floor scale");
            Near(floor.transform.localPosition.y + ResolveOriginalMesh(floor.sharedMesh).bounds.max.y * floor.transform.localScale.y, -1.5f, "lower floor height");
            foreach (Transform run in runs)
            {
                Near(run.localPosition.y, -1.5f, "stair base");
                if (run.localScale != Vector3.one) throw new InvalidOperationException("Scaled pit stair root");
                MeshFilter[] steps = Steps(run);
                if (steps.Length != 10 || steps.Any(s => s == null || s.sharedMesh == null || s.GetComponent<MeshRenderer>() == null))
                    throw new InvalidOperationException("Unrecognized pit stair run");
                for (int i = 0; i < steps.Length; i++)
                {
                    Transform t = steps[i].transform;
                    Near(t.localPosition.z, (i + .5f) * .3f, "tread run");
                    Near(t.localPosition.y + steps[i].sharedMesh.bounds.max.y * t.localScale.y, (i + 1) * .15f, "tread rise");
                }
                Transform child = run.Find(ChildName);
                if (child != null) RequireOwnedChild(child);
            }
        }
        private static void RequireOwnedChild(Transform child)
        {
            if (child == null || child.childCount != 0 || child.GetComponent<MeshFilter>() == null || child.GetComponent<MeshRenderer>() == null ||
                child.GetComponents<Component>().Any(c => !(c is Transform) && !(c is MeshFilter) && !(c is MeshRenderer)))
                throw new InvalidOperationException("Pit backing must contain render components only");
        }
        private sealed class GameplaySnapshot
        {
            private readonly Dictionary<Component, string> components;
            private readonly Dictionary<Transform, (Vector3 position, Quaternion rotation, Vector3 scale)> transforms;
            public GameplaySnapshot(GameObject root)
            {
                components = Protected(root).ToDictionary(c => c, c => EditorJsonUtility.ToJson(c));
                transforms = root.GetComponentsInChildren<Transform>(true).Where(t => t.name != ChildName)
                    .ToDictionary(t => t, t => (t.localPosition, t.localRotation, t.localScale));
            }
            public void AssertUnchanged(GameObject root)
            {
                Component[] after = Protected(root);
                if (after.Length != components.Count || after.Any(c => !components.TryGetValue(c, out string json) || json != EditorJsonUtility.ToJson(c)))
                    throw new InvalidOperationException("Pit visual repair changed serialized gameplay components");
                foreach (var pair in transforms)
                    if (pair.Key == null || !pair.Key.localPosition.Equals(pair.Value.position) || !pair.Key.localRotation.Equals(pair.Value.rotation) || !pair.Key.localScale.Equals(pair.Value.scale))
                        throw new InvalidOperationException("Pit visual repair moved an existing transform");
            }
            private static Component[] Protected(GameObject root) => root.GetComponentsInChildren<Component>(true)
                .Where(c => c is Collider || c is MonoBehaviour || c is Rigidbody || c is Joint).ToArray();
        }
        private static void Near(float actual, float expected, string label)
        { if (Mathf.Abs(actual - expected) > Epsilon) throw new InvalidOperationException("Incorrect " + label + ": " + actual); }
        private static string Number(float value) => Mathf.RoundToInt(value * 100000f).ToString(CultureInfo.InvariantCulture);
        private static void RequirePrimary()
        {
            string path = Path.GetFullPath(Application.dataPath).Replace('\\', '/');
            if (!path.Equals("C:/Users/yrapa/OneDrive/Desktop/Unity_DakaDaka/DakaDaka_clean_handoff/Assets", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Pit joint authoring is restricted to clean_handoff");
        }
    }
}
