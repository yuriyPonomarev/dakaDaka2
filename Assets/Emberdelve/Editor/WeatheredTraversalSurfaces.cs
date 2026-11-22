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
    /// <summary>Visual-only, dimension-specific traversal bevels. Never replaces collision or changes a transform.</summary>
    public static class WeatheredTraversalSurfaces
    {
        public const string MeshFolder = "Assets/Emberdelve/Art/Generated/WeatheredTraversal";
        public const string SourceFolder = "SourceArt/Environment/WeatheredTraversal";
        private const string PrefabFolder = "Assets/Emberdelve/Prefabs/DungeonModules";
        private const int Version = 1;
        private const int Segments = 3;
        private const int ExpectedPrefabs = 30;
        private static readonly Dictionary<string, Mesh> Cache = new Dictionary<string, Mesh>();

        [Serializable] public sealed class Variant
        {
            public string key;
            public Vector3 size;
            public float bevel;
            public int segments = Segments;
            public int instances;
            public string example;
        }
        [Serializable] public sealed class PrefabRecord { public string path; public int surfaces; }
        [Serializable] public sealed class Manifest
        {
            public int version = Version;
            public int surfaceCount;
            public Variant[] variants;
            public PrefabRecord[] prefabs;
        }
        [Serializable] public sealed class Surface
        {
            public int version;
            public string key;
            public Vector3 size;
            public float bevel;
            public int segments;
            public Vector3[] vertices, normals;
            public Vector2[] uv;
            public int[] triangles;
        }

        private static string DiskPath(string relative) => Path.Combine(Path.GetDirectoryName(Application.dataPath), relative);
        private static void RequirePrimaryCheckout()
        {
            const string primary = "C:/Users/yrapa/OneDrive/Desktop/Unity_DakaDaka/DakaDaka_clean_handoff";
            string project = Path.GetFullPath(Path.GetDirectoryName(Application.dataPath)).Replace('\\', '/').TrimEnd('/');
            if (!string.Equals(project, primary, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Traversal asset authoring is restricted to the primary checkout: " + primary);
            // LoadPrefabContents uses isolated preview scenes. The user's active scene and Play session are never touched.
        }
        private static string[] PrefabPaths()
        {
            string[] paths = AssetDatabase.FindAssets("t:Prefab", new[] { PrefabFolder })
                .Select(AssetDatabase.GUIDToAssetPath).OrderBy(p => p, StringComparer.Ordinal).ToArray();
            if (paths.Length != ExpectedPrefabs)
                throw new InvalidOperationException("Expected all 30 saved room/theme prefabs; found " + paths.Length);
            return paths;
        }

        [MenuItem("Emberdelve/Visual/Traversal/1 Export Dimensions")]
        public static void ExportDimensions()
        {
            RequirePrimaryCheckout();
            var variants = new Dictionary<string, Variant>();
            var records = new List<PrefabRecord>();
            int total = 0;
            foreach (string path in PrefabPaths())
            {
                GameObject root = PrefabUtility.LoadPrefabContents(path);
                try
                {
                    MeshFilter[] selected = Selected(root);
                    if (selected.Length == 0) throw new InvalidOperationException("No traversal kit in " + path);
                    foreach (MeshFilter filter in selected)
                    {
                        Variant variant = Describe(filter, root.transform);
                        if (!variants.TryGetValue(variant.key, out Variant saved))
                        {
                            variant.example = path + ":" + HierarchyPath(filter.transform, root.transform);
                            variants.Add(variant.key, saved = variant);
                        }
                        saved.instances++;
                    }
                    total += selected.Length;
                    records.Add(new PrefabRecord { path = path, surfaces = selected.Length });
                }
                finally { PrefabUtility.UnloadPrefabContents(root); }
            }
            var manifest = new Manifest { surfaceCount = total, prefabs = records.ToArray(),
                variants = variants.Values.OrderBy(v => v.key, StringComparer.Ordinal).ToArray() };
            Directory.CreateDirectory(DiskPath(SourceFolder));
            File.WriteAllText(DiskPath(SourceFolder + "/dimensions.json"), JsonUtility.ToJson(manifest, true));
            foreach (Variant variant in manifest.variants)
                Debug.Log("[Traversal] dimension " + variant.key + " metres=" + variant.size.ToString("F5") +
                    " bevel=" + variant.bevel.ToString("F3", CultureInfo.InvariantCulture) + " uses=" + variant.instances);
            Debug.Log("[Traversal] exported prefabs=" + records.Count + " surfaces=" + total + " variants=" + variants.Count +
                "; no assets modified. Next: Blender --background --python Tools/Art/BevelTraversal.py");
        }

        [MenuItem("Emberdelve/Visual/Traversal/2 Import And Apply")]
        public static void ImportAndApply()
        {
            RequirePrimaryCheckout();
            Manifest manifest = ReadManifest();
            var meshes = new Dictionary<string, Mesh>();
            try
            {
                // Validate every output and every current prefab before modifying any asset.
                foreach (Variant variant in manifest.variants)
                {
                    string path = DiskPath(SourceFolder + "/" + variant.key + ".bevel.json");
                    if (!File.Exists(path)) throw new InvalidOperationException("Missing Blender output: " + path);
                    Surface surface = JsonUtility.FromJson<Surface>(File.ReadAllText(path));
                    ValidateSurface(surface, variant);
                    var mesh = new Mesh { name = variant.key, vertices = surface.vertices,
                        normals = surface.normals, uv = surface.uv, triangles = surface.triangles };
                    mesh.RecalculateBounds();
                    mesh.RecalculateTangents();
                    meshes.Add(variant.key, mesh);
                }
                var usage = new Dictionary<string, int>();
                foreach (PrefabRecord record in manifest.prefabs)
                {
                    GameObject root = PrefabUtility.LoadPrefabContents(record.path);
                    try
                    {
                        MeshFilter[] selected = Selected(root);
                        if (selected.Length != record.surfaces)
                            throw new InvalidOperationException("Traversal layout changed; export again: " + record.path);
                        foreach (MeshFilter filter in selected)
                        {
                            string key = Describe(filter, root.transform).key;
                            if (!meshes.ContainsKey(key)) throw new InvalidOperationException("New traversal dimension; export again: " + key);
                            usage.TryGetValue(key, out int count); usage[key] = count + 1;
                        }
                    }
                    finally { PrefabUtility.UnloadPrefabContents(root); }
                }
                foreach (Variant variant in manifest.variants)
                    if (!usage.TryGetValue(variant.key, out int count) || count != variant.instances)
                        throw new InvalidOperationException("Dimension usage changed; export again: " + variant.key);
                EnsureFolder(MeshFolder);
                Cache.Clear();
                foreach (Variant variant in manifest.variants)
                {
                    Mesh mesh = meshes[variant.key];
                    string path = MeshFolder + "/" + variant.key + ".asset";
                    Mesh saved = AssetDatabase.LoadAssetAtPath<Mesh>(path);
                    if (saved == null) { AssetDatabase.CreateAsset(mesh, path); saved = mesh; }
                    else { EditorUtility.CopySerialized(mesh, saved); AssetDatabase.SaveAssetIfDirty(saved); }
                    Cache[variant.key] = saved;
                    Debug.Log("[Traversal] imported " + variant.key + " vertices=" + saved.vertexCount +
                        " triangles=" + saved.triangles.Length / 3 + " unit bounds=" + saved.bounds);
                }
                int changedPrefabs = 0, changedMeshes = 0;
                foreach (PrefabRecord record in manifest.prefabs)
                {
                    GameObject root = PrefabUtility.LoadPrefabContents(record.path);
                    try
                    {
                        int changed = Apply(root);
                        if (changed == 0) continue;
                        if (PrefabUtility.SaveAsPrefabAsset(root, record.path) == null)
                            throw new InvalidOperationException("Prefab save failed: " + record.path);
                        changedPrefabs++; changedMeshes += changed;
                    }
                    finally { PrefabUtility.UnloadPrefabContents(root); }
                }
                Debug.Log("[Traversal] applied changedPrefabs=" + changedPrefabs + " changedMeshFilters=" + changedMeshes +
                    " selected=" + manifest.surfaceCount + "; only explicitly targeted meshes/prefabs saved.");
                ValidateSaved();
            }
            finally
            {
                foreach (Mesh mesh in meshes.Values)
                    if (mesh != null && !EditorUtility.IsPersistent(mesh)) Object.DestroyImmediate(mesh);
            }
        }

        /// <summary>Builder hook after traversal creation and before saving the prefab. Missing dimensions fail before any swaps.</summary>
        public static int Apply(GameObject root)
        {
            if (root == null) throw new ArgumentNullException(nameof(root));
            MeshFilter[] selected = Selected(root);
            var replacements = new Mesh[selected.Length];
            for (int i = 0; i < selected.Length; i++)
            {
                Variant variant = Describe(selected[i], root.transform);
                replacements[i] = LoadMesh(variant.key);
            }
            var invariant = new Invariant(root);
            int changed = 0;
            for (int i = 0; i < selected.Length; i++)
                if (selected[i].sharedMesh != replacements[i]) { selected[i].sharedMesh = replacements[i]; changed++; }
            invariant.AssertUnchanged(root);
            Debug.Log("[Traversal] " + root.name + " selected=" + selected.Length + " swaps=" + changed +
                " transforms=" + invariant.TransformCount + " colliders=" + invariant.ColliderCount +
                " exactNonMeshComponents=" + invariant.ComponentCount + " transformDelta=0 colliderDelta=0");
            return changed;
        }

        [MenuItem("Emberdelve/Visual/Traversal/3 Validate Saved Meshes")]
        public static void ValidateSaved()
        {
            Cache.Clear();
            var checkedKeys = new HashSet<string>();
            int surfaces = 0, transforms = 0, colliders = 0;
            foreach (string path in PrefabPaths())
            {
                GameObject root = PrefabUtility.LoadPrefabContents(path);
                try
                {
                    transforms += root.GetComponentsInChildren<Transform>(true).Length;
                    colliders += root.GetComponentsInChildren<Collider>(true).Length;
                    foreach (MeshFilter filter in Selected(root))
                    {
                        Variant variant = Describe(filter, root.transform);
                        Mesh mesh = LoadMesh(variant.key);
                        if (filter.sharedMesh != mesh) throw new InvalidOperationException("Unapplied traversal surface: " + path + ":" + filter.name);
                        if (checkedKeys.Add(variant.key))
                            ValidateSurface(new Surface { version = Version, key = variant.key, size = variant.size,
                                bevel = variant.bevel, segments = Segments, vertices = mesh.vertices, normals = mesh.normals,
                                uv = mesh.uv, triangles = mesh.triangles }, variant);
                        surfaces++;
                    }
                }
                finally { PrefabUtility.UnloadPrefabContents(root); }
            }
            Debug.Log("[Traversal] QA passed prefabs=30 surfaces=" + surfaces + " variants=" + checkedKeys.Count +
                " transforms=" + transforms + " colliders=" + colliders +
                "; finite unit bounds, outward placed normals, nondegenerate triangles and original horizontal top plane verified.");
        }

        private static Manifest ReadManifest()
        {
            Manifest manifest = JsonUtility.FromJson<Manifest>(File.ReadAllText(DiskPath(SourceFolder + "/dimensions.json")));
            string[] paths = PrefabPaths();
            if (manifest == null || manifest.version != Version || manifest.variants == null || manifest.variants.Length == 0 ||
                manifest.prefabs == null || !paths.SequenceEqual(manifest.prefabs.Select(p => p.path)) ||
                manifest.surfaceCount != manifest.prefabs.Sum(p => p.surfaces) || manifest.surfaceCount != manifest.variants.Sum(v => v.instances))
                throw new InvalidOperationException("Stale/incomplete traversal manifest; run ExportDimensions again.");
            if (manifest.variants.Select(v => v.key).Distinct().Count() != manifest.variants.Length)
                throw new InvalidOperationException("Duplicate traversal dimension keys.");
            return manifest;
        }

        private static Mesh LoadMesh(string key)
        {
            if (Cache.TryGetValue(key, out Mesh mesh) && mesh != null) return mesh;
            mesh = AssetDatabase.LoadAssetAtPath<Mesh>(MeshFolder + "/" + key + ".asset");
            if (mesh == null) throw new InvalidOperationException("Missing traversal visual " + key +
                ". ExportDimensions, run Tools/Art/BevelTraversal.py in Blender, then ImportAndApply before rebuilding.");
            Cache[key] = mesh;
            return mesh;
        }

        private static MeshFilter[] Selected(GameObject root) => root.GetComponentsInChildren<MeshFilter>(true)
            .Where(f => IsTraversal(f.transform)).ToArray();

        private static bool IsTraversal(Transform part)
        {
            string name = part.name, parent = part.parent != null ? part.parent.name : "";
            if (parent == "Undercroft_SouthStairs" || parent == "Undercroft_NorthStairs" ||
                parent == "Gallery_SouthStairs" || parent == "Gallery_NorthStairs") return Numbered(name, "Tread_");
            if (parent.StartsWith("Terrace_", StringComparison.Ordinal))
                return Numbered(name, "Stair_") || name == "Upper_Deck" || name == "Parapet" || name == "Rear_Parapet";
            if (parent == "Western_Gallery") return name == "Gallery_Deck" || name == "Gallery_EndParapet" ||
                name == "Gallery_Parapet" || name == "Gallery_OuterParapet";
            return name == "Undercroft_Floor" || name == "Undercroft_RetainingWest" || name == "Undercroft_RetainingEast";
        }

        private static bool Numbered(string name, string prefix) => name.StartsWith(prefix, StringComparison.Ordinal) &&
            int.TryParse(name.Substring(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int index) && index >= 0;

        private static Variant Describe(MeshFilter filter, Transform root)
        {
            Mesh source = filter.sharedMesh;
            if (source == null) throw new InvalidOperationException("Traversal surface has no mesh: " + filter.name);
            string path = AssetDatabase.GetAssetPath(source);
            bool box = (source.name == "Cube" && !path.StartsWith("Assets/", StringComparison.Ordinal)) ||
                (path.StartsWith("Assets/Emberdelve/Art/Generated/ArchitectureSmooth/", StringComparison.Ordinal) &&
                    (source.name.StartsWith("Bevel_", StringComparison.Ordinal) || source.name.StartsWith("Box_", StringComparison.Ordinal))) ||
                path.StartsWith(MeshFolder + "/", StringComparison.Ordinal);
            if (!box || (source.bounds.center.sqrMagnitude > 1e-8f) || (source.bounds.size - Vector3.one).sqrMagnitude > 1e-8f)
                throw new InvalidOperationException("Expected centered unit-box traversal render mesh: " + filter.name + " / " + source.name);
            for (Transform t = filter.transform.parent; t != null; t = t.parent)
            {
                if ((t.localScale - Vector3.one).sqrMagnitude > 1e-10f)
                    throw new InvalidOperationException("Traversal parent scale must remain unit to preserve metre bevel width: " + t.name);
                if (t == root) break;
            }
            Vector3 size = filter.transform.localScale;
            if (!Finite(size) || Mathf.Min(size.x, Mathf.Min(size.y, size.z)) < .06f)
                throw new InvalidOperationException("Invalid traversal dimensions: " + filter.name);
            // Stable keys absorb harmless floating-point products such as 13 * .15f; transforms are never rounded.
            int x = Mathf.RoundToInt(size.x * 100000f), y = Mathf.RoundToInt(size.y * 100000f), z = Mathf.RoundToInt(size.z * 100000f);
            float bevel = Numbered(filter.name, "Tread_") || Numbered(filter.name, "Stair_") ? .025f : .04f;
            return new Variant { key = "Traversal_v" + Version + "_" + x + "_" + y + "_" + z + "_b" + Mathf.RoundToInt(bevel * 1000f),
                size = new Vector3(x, y, z) / 100000f, bevel = bevel };
        }

        private static void ValidateSurface(Surface s, Variant variant)
        {
            if (s == null || s.version != Version || s.key != variant.key || (s.size - variant.size).sqrMagnitude > 1e-10f ||
                Mathf.Abs(s.bevel - variant.bevel) > 1e-6f || s.segments != Segments || s.vertices == null ||
                s.vertices.Length < 24 || s.vertices.Length > 20000 || s.normals == null || s.normals.Length != s.vertices.Length ||
                s.uv == null || s.uv.Length != s.vertices.Length || s.triangles == null || s.triangles.Length <= 36 || s.triangles.Length % 3 != 0)
                throw new InvalidOperationException("Invalid/stale Blender traversal output: " + variant.key);
            Vector3 min = Vector3.one, max = -Vector3.one;
            for (int i = 0; i < s.vertices.Length; i++)
            {
                Vector3 p = s.vertices[i], n = s.normals[i];
                if (!Finite(p) || !Finite(n) || !float.IsFinite(s.uv[i].x) || !float.IsFinite(s.uv[i].y) ||
                    Mathf.Abs(n.magnitude - 1f) > .005f || Mathf.Max(Mathf.Abs(p.x), Mathf.Abs(p.y), Mathf.Abs(p.z)) > .50002f)
                    throw new InvalidOperationException("Invalid bounds/normal/UV in " + variant.key);
                min = Vector3.Min(min, p); max = Vector3.Max(max, p);
            }
            if ((min + Vector3.one * .5f).sqrMagnitude > 1e-9f || (max - Vector3.one * .5f).sqrMagnitude > 1e-9f)
                throw new InvalidOperationException("Traversal outer extents changed: " + variant.key);
            double topArea = 0;
            for (int i = 0; i < s.triangles.Length; i += 3)
            {
                int a = s.triangles[i], b = s.triangles[i + 1], c = s.triangles[i + 2];
                if (a < 0 || b < 0 || c < 0 || a >= s.vertices.Length || b >= s.vertices.Length || c >= s.vertices.Length)
                    throw new InvalidOperationException("Invalid triangle index: " + variant.key);
                Vector3 pa = Vector3.Scale(s.vertices[a], variant.size), pb = Vector3.Scale(s.vertices[b], variant.size), pc = Vector3.Scale(s.vertices[c], variant.size);
                Vector3 cross = Vector3.Cross(pb - pa, pc - pa);
                if (cross.sqrMagnitude < 1e-16f || Vector3.Dot(cross, (pa + pb + pc) / 3f) <= 0f)
                    throw new InvalidOperationException("Degenerate/inverted traversal triangle: " + variant.key);
                foreach (int vertex in new[] { a, b, c })
                {
                    Vector3 n = s.normals[vertex];
                    n = new Vector3(n.x / variant.size.x, n.y / variant.size.y, n.z / variant.size.z).normalized;
                    if (Vector3.Dot(n, cross.normalized) < .01f)
                        throw new InvalidOperationException("Normal fails inverse-scale placement: " + variant.key);
                }
                if (Mathf.Abs(s.vertices[a].y - .5f) < 1e-6f && Mathf.Abs(s.vertices[b].y - .5f) < 1e-6f && Mathf.Abs(s.vertices[c].y - .5f) < 1e-6f)
                    topArea += cross.magnitude * .5;
            }
            double flatCore = (variant.size.x - 2 * variant.bevel) * (variant.size.z - 2 * variant.bevel);
            if (topArea < flatCore * .99) throw new InvalidOperationException("Horizontal walk-plane core was lost: " + variant.key);
        }

        private static bool Finite(Vector3 v) => float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);
        private static string HierarchyPath(Transform t, Transform root) => t == root ? root.name :
            HierarchyPath(t.parent, root) + "/" + t.name + "[" + t.GetSiblingIndex() + "]";
        private static void EnsureFolder(string path)
        {
            string[] parts = path.Split('/'); string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }

        private sealed class Invariant
        {
            private readonly Dictionary<Component, string> _components = new Dictionary<Component, string>();
            private readonly HashSet<MeshFilter> _filters;
            public readonly int TransformCount, ColliderCount;
            public int ComponentCount => _components.Count;
            public Invariant(GameObject root)
            {
                _filters = new HashSet<MeshFilter>(root.GetComponentsInChildren<MeshFilter>(true));
                foreach (Component component in root.GetComponentsInChildren<Component>(true))
                {
                    if (component == null) throw new InvalidOperationException("Missing script in traversal prefab " + root.name);
                    if (component is MeshFilter) continue;
                    _components.Add(component, EditorJsonUtility.ToJson(component));
                    if (component is Transform) TransformCount++;
                    if (component is Collider) ColliderCount++;
                }
            }
            public void AssertUnchanged(GameObject root)
            {
                Component[] current = root.GetComponentsInChildren<Component>(true);
                if (current.Length != _components.Count + _filters.Count)
                    throw new InvalidOperationException("Traversal pass changed component/hierarchy count.");
                foreach (Component component in current)
                {
                    if (component is MeshFilter filter)
                    { if (!_filters.Contains(filter)) throw new InvalidOperationException("Traversal pass changed MeshFilter identity."); continue; }
                    if (component == null || !_components.TryGetValue(component, out string before) || before != EditorJsonUtility.ToJson(component))
                        throw new InvalidOperationException("Traversal pass changed non-render-mesh data: " + component);
                }
            }
        }
    }
}
