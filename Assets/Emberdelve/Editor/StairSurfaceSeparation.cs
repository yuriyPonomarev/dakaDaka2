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
    /// <summary>Fits rounded visual masonry to non-overlapping joints; leaves all gameplay geometry untouched.</summary>
    public static class StairSurfaceSeparation
    {
        public const string MeshFolder = "Assets/Emberdelve/Art/Generated/TraversalSeparation";
        private const string PrefabFolder = "Assets/Emberdelve/Prefabs/DungeonModules";
        private const float Run = .3f;
        private const float HalfJoint = .002f;
        private const float GalleryLanding = 7.75f;
        private const float GroundUnderside = -.6f;
        private const float Tolerance = .00002f;
        private static readonly Dictionary<string, Mesh> Cache = new Dictionary<string, Mesh>();
        private static readonly HashSet<string> RefreshKeys = new HashSet<string>();

        internal static void RefreshDerivedMeshes()
        {
            Cache.Clear();
            RefreshKeys.Clear();
            if (!AssetDatabase.IsValidFolder(MeshFolder)) return;
            foreach (string guid in AssetDatabase.FindAssets("t:Mesh", new[] { MeshFolder }))
                RefreshKeys.Add(Path.GetFileNameWithoutExtension(AssetDatabase.GUIDToAssetPath(guid)));
        }

        /// <summary>Call after WeatheredTraversalSurfaces.Apply and before saving a room prefab.</summary>
        public static int Apply(GameObject root)
        {
            if (root == null) throw new ArgumentNullException(nameof(root));
            RequirePrimaryCheckout();
            Preflight(root);
            var components = Snapshot(root);
            var replacements = new Dictionary<MeshFilter, Mesh>();
            foreach (MeshFilter filter in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (TryStep(filter.transform, out int index, out bool gallery))
                {
                    Vector2 interval = StepInterval(index, gallery);
                    Mesh source = Source(filter);
                    float min = (interval.x - filter.transform.localPosition.z) / filter.transform.localScale.z;
                    float max = (interval.y - filter.transform.localPosition.z) / filter.transform.localScale.z;
                    string key = source.name + "__joint_v1_" + Number(min) + "_" + Number(max);
                    replacements.Add(filter, Derivative(key, () => FitRun(source, min, max)));
                }
                else if (IsRetaining(filter.transform))
                {
                    // The floor slab already supplies the upper 0.6 m of the exposed side wall.
                    // Remove its hidden backing and duplicate top, rather than offsetting depth tests.
                    Mesh source = Source(filter);
                    float cut = (GroundUnderside - filter.transform.localPosition.y) / filter.transform.localScale.y;
                    string key = source.name + "__lower_v1_" + Number(cut);
                    replacements.Add(filter, Derivative(key, () => ClipBelow(source, cut)));
                }
            }
            int changed = 0;
            foreach (var replacement in replacements)
                if (replacement.Key.sharedMesh != replacement.Value)
                { replacement.Key.sharedMesh = replacement.Value; changed++; }
            AssertSnapshot(root, components);
            Validate(root);
            Debug.Log("[TraversalSeparation] " + root.name + " fitted=" + replacements.Count + " changed=" + changed +
                "; only render meshes changed; collider, transform and other component data identical.");
            return changed;
        }

        [MenuItem("Emberdelve/Visual/Traversal/4 Repair Saved Masonry Joints")]
        public static void RepairSaved()
        {
            RequirePrimaryCheckout();
            Cache.Clear();
            string[] paths = PrefabPaths();
            int changed = 0;
            // Check the complete target set before the first save; an unfamiliar traversal layout fails closed.
            foreach (string path in paths)
            {
                GameObject root = PrefabUtility.LoadPrefabContents(path);
                try { Preflight(root); }
                finally { PrefabUtility.UnloadPrefabContents(root); }
            }
            foreach (string path in paths)
            {
                GameObject root = PrefabUtility.LoadPrefabContents(path);
                try
                {
                    if (Apply(root) == 0) continue;
                    if (PrefabUtility.SaveAsPrefabAsset(root, path) == null)
                        throw new InvalidOperationException("Could not save traversal repair: " + path);
                    changed++;
                }
                finally { PrefabUtility.UnloadPrefabContents(root); }
            }
            ValidateSaved();
            Debug.Log("[TraversalSeparation] repaired saved prefabs=" + changed + "/" + paths.Length);
        }

        [MenuItem("Emberdelve/Visual/Traversal/5 Validate Saved Masonry Joints")]
        public static void ValidateSaved()
        {
            int steps = 0;
            foreach (string path in PrefabPaths())
            {
                GameObject root = PrefabUtility.LoadPrefabContents(path);
                try { steps += Validate(root); }
                finally { PrefabUtility.UnloadPrefabContents(root); }
            }
            Debug.Log("[TraversalSeparation] QA passed: prefabs=30 steps=" + steps +
                " retainingWalls=60; tread intervals disjoint, landing clearance >=2mm, original tread top heights retained," +
                " retaining render faces stop at ground slab underside; no overlapping floor-wall coplanar area.");
        }

        /// <summary>Checks actual mesh bounds in authored metres, not merely a particular asset reference.</summary>
        public static int Validate(GameObject root)
        {
            Preflight(root);
            int steps = 0, retaining = 0;
            foreach (MeshFilter filter in root.GetComponentsInChildren<MeshFilter>(true))
            {
                Transform part = filter.transform;
                if (TryStep(part, out int index, out bool gallery))
                {
                    Bounds bounds = filter.sharedMesh.bounds;
                    Vector2 interval = StepInterval(index, gallery);
                    float min = part.localPosition.z + bounds.min.z * part.localScale.z;
                    float max = part.localPosition.z + bounds.max.z * part.localScale.z;
                    if (Mathf.Abs(min - interval.x) > Tolerance || Mathf.Abs(max - interval.y) > Tolerance ||
                        Mathf.Abs(bounds.max.y - .5f) > Tolerance || Mathf.Abs(bounds.min.y + .5f) > Tolerance)
                        throw new InvalidOperationException("Overlapping or displaced stair visual: " + part.parent.name + "/" + part.name);
                    steps++;
                }
                else if (IsRetaining(part))
                {
                    Bounds bounds = filter.sharedMesh.bounds;
                    float top = part.localPosition.y + bounds.max.y * part.localScale.y;
                    if (Mathf.Abs(top - GroundUnderside) > Tolerance)
                        throw new InvalidOperationException("Retaining visual overlaps ground slab: " + part.name);
                    retaining++;
                }
            }
            if (steps < 72 || retaining != 2)
                throw new InvalidOperationException("Incomplete traversal repair in " + root.name);
            return steps;
        }

        private static void Preflight(GameObject root)
        {
            Transform floor = root.transform.Find("Floor"), edge = root.transform.Find("Floor_EastEdge");
            RequireBox(floor, new Vector3(-3, -.3f, 0), new Vector3(30, .6f, 36));
            RequireBox(edge, new Vector3(17.5f, -.3f, -10), new Vector3(1, .6f, 10));
            foreach (MeshFilter filter in root.GetComponentsInChildren<MeshFilter>(true))
            {
                Transform t = filter.transform;
                if (TryStep(t, out int index, out bool gallery))
                {
                    float expectedDepth = t.name.StartsWith("Stair_", StringComparison.Ordinal) ? .31f : .305f;
                    if (Mathf.Abs(t.localPosition.z - (index + .5f) * Run) > Tolerance ||
                        Mathf.Abs(t.localScale.z - expectedDepth) > Tolerance ||
                        Mathf.Abs(t.localPosition.y - t.localScale.y * .5f) > Tolerance ||
                        Quaternion.Angle(t.localRotation, Quaternion.identity) > .001f)
                        throw new InvalidOperationException("Unrecognized authored step: " + t.name);
                    if (gallery && index > 25) throw new InvalidOperationException("Unrecognized gallery step count");
                    Source(filter);
                }
                else if (IsRetaining(t))
                {
                    RequireBox(t, new Vector3(t.name.EndsWith("West", StringComparison.Ordinal) ? 11.8f : 17.2f, -.75f, -10),
                        new Vector3(.4f, 1.5f, 10));
                    Source(filter);
                }
            }
        }

        private static void RequireBox(Transform t, Vector3 position, Vector3 scale)
        {
            if (t == null || (t.localPosition - position).sqrMagnitude > Tolerance * Tolerance ||
                (t.localScale - scale).sqrMagnitude > Tolerance * Tolerance ||
                Quaternion.Angle(t.localRotation, Quaternion.identity) > .001f)
                throw new InvalidOperationException("Ground/retaining layout changed; review traversal joint fit.");
        }

        private static Vector2 StepInterval(int index, bool gallery) => new Vector2(index * Run + HalfJoint,
            Mathf.Min((index + 1) * Run, gallery ? GalleryLanding : float.PositiveInfinity) - HalfJoint);

        private static bool TryStep(Transform t, out int index, out bool gallery)
        {
            index = -1;
            string parent = t.parent != null ? t.parent.name : "";
            gallery = parent == "Gallery_SouthStairs" || parent == "Gallery_NorthStairs";
            bool undercroft = parent == "Undercroft_SouthStairs" || parent == "Undercroft_NorthStairs";
            string prefix = gallery || undercroft ? "Tread_" : parent.StartsWith("Terrace_", StringComparison.Ordinal) ? "Stair_" : null;
            return prefix != null && t.name.StartsWith(prefix, StringComparison.Ordinal) &&
                int.TryParse(t.name.Substring(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out index) && index >= 0;
        }

        private static bool IsRetaining(Transform t) => t.name == "Undercroft_RetainingWest" || t.name == "Undercroft_RetainingEast";
        private static string Number(float value) => Mathf.RoundToInt(value * 1000000f).ToString(CultureInfo.InvariantCulture);

        private static Mesh Source(MeshFilter filter)
        {
            Mesh mesh = filter.sharedMesh;
            if (mesh == null) throw new InvalidOperationException("Missing rounded traversal mesh: " + filter.name);
            string path = AssetDatabase.GetAssetPath(mesh);
            if (path.StartsWith(MeshFolder + "/", StringComparison.Ordinal))
            {
                string key = mesh.name.Split(new[] { "__" }, StringSplitOptions.None)[0];
                mesh = AssetDatabase.LoadAssetAtPath<Mesh>(WeatheredTraversalSurfaces.MeshFolder + "/" + key + ".asset");
                if (mesh == null) throw new InvalidOperationException("Missing original rounded traversal mesh: " + key);
                path = AssetDatabase.GetAssetPath(mesh);
            }
            if (!path.StartsWith(WeatheredTraversalSurfaces.MeshFolder + "/", StringComparison.Ordinal) ||
                (mesh.bounds.center).sqrMagnitude > 1e-8f || (mesh.bounds.size - Vector3.one).sqrMagnitude > 1e-8f)
                throw new InvalidOperationException("Apply rounded traversal meshes before fitting joints: " + filter.name);
            return mesh;
        }

        private static Mesh Derivative(string key, Func<Mesh> build)
        {
            if (Cache.TryGetValue(key, out Mesh cached) && cached != null) return cached;
            string path = MeshFolder + "/" + key + ".asset";
            Mesh mesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (mesh == null)
            {
                if (!AssetDatabase.IsValidFolder(MeshFolder)) AssetDatabase.CreateFolder("Assets/Emberdelve/Art/Generated", "TraversalSeparation");
                mesh = build(); mesh.name = key;
                AssetDatabase.CreateAsset(mesh, path);
            }
            else if (RefreshKeys.Remove(key))
            {
                Mesh refreshed = build(); refreshed.name = key;
                try { EditorUtility.CopySerialized(refreshed, mesh); AssetDatabase.SaveAssetIfDirty(mesh); }
                finally { Object.DestroyImmediate(refreshed); }
            }
            Cache[key] = mesh;
            return mesh;
        }

        private static Mesh FitRun(Mesh source, float min, float max)
        {
            if (min < -.5001f || max > .5001f || max <= min)
                throw new InvalidOperationException("Invalid masonry joint interval");
            Mesh mesh = Object.Instantiate(source);
            Vector3[] vertices = mesh.vertices, normals = mesh.normals;
            for (int i = 0; i < vertices.Length; i++)
            {
                vertices[i].z = Mathf.Lerp(min, max, vertices[i].z + .5f);
                normals[i] = new Vector3(normals[i].x, normals[i].y, normals[i].z / (max - min)).normalized;
            }
            mesh.vertices = vertices; mesh.normals = normals;
            mesh.RecalculateBounds(); mesh.RecalculateTangents();
            return mesh;
        }

        private struct Vertex
        {
            public Vector3 p, n;
            public Vector2 uv;
            public static Vertex Lerp(Vertex a, Vertex b, float t) => new Vertex
            { p = Vector3.Lerp(a.p, b.p, t), n = Vector3.Lerp(a.n, b.n, t).normalized, uv = Vector2.Lerp(a.uv, b.uv, t) };
        }

        private static Mesh ClipBelow(Mesh source, float cut)
        {
            Vector3[] p = source.vertices, n = source.normals; Vector2[] uv = source.uv;
            int[] original = source.triangles;
            var vertices = new List<Vector3>(); var normals = new List<Vector3>();
            var texcoords = new List<Vector2>(); var indices = new List<int>();
            for (int i = 0; i < original.Length; i += 3)
            {
                var triangle = new List<Vertex>(3);
                for (int j = 0; j < 3; j++)
                { int v = original[i + j]; triangle.Add(new Vertex { p = p[v], n = n[v], uv = uv[v] }); }
                var polygon = new List<Vertex>(4);
                for (int j = 0; j < 3; j++)
                {
                    Vertex a = triangle[j], b = triangle[(j + 1) % 3];
                    bool insideA = a.p.y <= cut, insideB = b.p.y <= cut;
                    if (insideA) polygon.Add(a);
                    if (insideA != insideB) polygon.Add(Vertex.Lerp(a, b, (cut - a.p.y) / (b.p.y - a.p.y)));
                }
                for (int j = 1; j + 1 < polygon.Count; j++)
                {
                    Vertex a = polygon[0], b = polygon[j], c = polygon[j + 1];
                    if (Vector3.Cross(b.p - a.p, c.p - a.p).sqrMagnitude < 1e-16f) continue;
                    foreach (Vertex v in new[] { a, b, c })
                    { indices.Add(vertices.Count); vertices.Add(v.p); normals.Add(v.n); texcoords.Add(v.uv); }
                }
            }
            // No cap: the cut lies exactly beneath the existing solid ground slab, which supplies the closure.
            var mesh = new Mesh(); mesh.SetVertices(vertices); mesh.SetNormals(normals);
            mesh.SetUVs(0, texcoords); mesh.SetTriangles(indices, 0);
            mesh.RecalculateBounds(); mesh.RecalculateTangents(); return mesh;
        }

        private static Dictionary<Component, string> Snapshot(GameObject root) => root.GetComponentsInChildren<Component>(true)
            .Where(c => !(c is MeshFilter)).ToDictionary(c => c, c => EditorJsonUtility.ToJson(c));

        private static void AssertSnapshot(GameObject root, Dictionary<Component, string> before)
        {
            Component[] after = root.GetComponentsInChildren<Component>(true).Where(c => !(c is MeshFilter)).ToArray();
            if (after.Length != before.Count || after.Any(c => !before.TryGetValue(c, out string json) || json != EditorJsonUtility.ToJson(c)))
                throw new InvalidOperationException("Traversal joint fit changed data other than render meshes");
        }

        private static string[] PrefabPaths()
        {
            string[] paths = AssetDatabase.FindAssets("t:Prefab", new[] { PrefabFolder }).Select(AssetDatabase.GUIDToAssetPath)
                .OrderBy(p => p, StringComparer.Ordinal).ToArray();
            if (paths.Length != 30) throw new InvalidOperationException("Expected all 30 room prefabs");
            return paths;
        }

        private static void RequirePrimaryCheckout()
        {
            string project = Path.GetFullPath(Path.GetDirectoryName(Application.dataPath)).Replace('\\', '/').TrimEnd('/');
            if (!string.Equals(project, "C:/Users/yrapa/OneDrive/Desktop/Unity_DakaDaka/DakaDaka_clean_handoff", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Traversal authoring is restricted to the primary checkout");
        }
    }
}
