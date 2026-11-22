using System;
using System.Collections.Generic;
using Emberdelve.Level;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Emberdelve.EditorTools
{
    /// <summary>Fits the owner's downloaded meshes over the authored collision layout.</summary>
    internal static class DownloadedCryptArchitecture
    {
        internal const string Folder = "Assets/Emberdelve/Art/Models/AI/Meshy/DownloadedArchitecture";
        private const string Community = "Assets/Emberdelve/Art/Models/AI/Meshy/CommunityRuins";
        private const string VisualName = "Meshy_Architecture_Visual";

        [MenuItem("Emberdelve/Visual/Rebuild Downloaded Crypt Architecture")]
        public static void RebuildAll()
        {
            if (!Application.isBatchMode)
                throw new InvalidOperationException("Rebuild all room catalogs on the test mirror.");
            DungeonModuleContentBuilder.BuildCatalog();
            DungeonModuleContentBuilder.BuildRoyaleDestinationCatalogs();
            AssetDatabase.SaveAssets();
        }

        internal static void Apply(GameObject room)
        {
            var pillar = Required<GameObject>(Folder + "/StonePillar/Source.fbx");
            var barrel = Required<GameObject>(Folder + "/RusticBarrel/Source.fbx");
            var pillarMaterial = Surface("StonePillar", .13f);
            var barrelMaterial = Surface("RusticBarrel", .19f);
            var masonry = StonePier();
            var crown = ArchCrown();
            var archMaterial = StoneMaterial();
            ApplyReliquaries(room);

            // Snapshot before adding children; authored collider and light sibling identities stay unchanged.
            foreach (var filter in room.GetComponentsInChildren<MeshFilter>(true))
            {
                if (filter.sharedMesh == null || !filter.TryGetComponent<MeshRenderer>(out var renderer)
                    || !renderer.enabled || filter.transform.Find(VisualName) != null) continue;
                string name = filter.name;
                if (name == "Column_Shaft")
                {
                    var scale = filter.transform.localScale;
                    // The full column replaces the shaft, base and capital as one textured object.
                    Bounds fit = filter.sharedMesh.bounds;
                    fit.size = new Vector3(1.2f / Mathf.Abs(scale.x), fit.size.y, 1.2f / Mathf.Abs(scale.z));
                    FitPrefab(pillar, filter.transform, fit, pillarMaterial);
                    HideSiblingAt(filter.transform, "Column_Base");
                    HideSiblingAt(filter.transform, "Column_Capital");
                    renderer.enabled = false;
                }
                else if (name == "Stone_Arch" || name == "Reused_Stone_Arch" || name == "StoneArch")
                {
                    var visual = Child(filter.transform, VisualName);
                    Bounds fit = filter.sharedMesh.bounds;
                    float depth = fit.size.z * Mathf.Abs(filter.transform.lossyScale.z);
                    int ribs = Mathf.Max(1, Mathf.CeilToInt(depth / 1.3f));
                    for (int i = 0; i < ribs; i++)
                    {
                        Bounds rib = fit;
                        rib.size = new Vector3(fit.size.x, fit.size.y, fit.size.z / ribs);
                        rib.center = new Vector3(fit.center.x, fit.center.y, fit.min.z + rib.size.z * (i + .5f));
                        var piece = Child(visual, "Imported_Arch_Crown");
                        piece.gameObject.AddComponent<MeshFilter>().sharedMesh = crown;
                        piece.gameObject.AddComponent<MeshRenderer>().sharedMaterial = archMaterial;
                        Fit(piece, crown.bounds, rib);
                    }
                    renderer.enabled = false;
                }
                else if (name == "Clay_Vessel" || name == "Funerary_Urn")
                {
                    // No jar/coffin archive was supplied. Use the actual downloaded storage barrel.
                    FitPrefab(barrel, filter.transform, filter.sharedMesh.bounds, barrelMaterial);
                    if (name == "Funerary_Urn") HideSiblingAt(filter.transform, "Urn_Lip");
                    renderer.enabled = false;
                }
                else if (IsWall(name))
                {
                    var visual = Child(filter.transform, VisualName);
                    Bounds fit = filter.sharedMesh.bounds;
                    // Reuse solid stone from the downloaded arch jamb, never timber or an open arch
                    // over a closed collider. Imported tiles remain inside the authored wall envelope.
                    bool alongZ = fit.size.z * Mathf.Abs(filter.transform.lossyScale.z)
                        > fit.size.x * Mathf.Abs(filter.transform.lossyScale.x);
                    var facing = Child(visual, "Imported_Stone_Facing");
                    if (alongZ) facing.localRotation = Quaternion.Euler(0, 90, 0);
                    Bounds wall = alongZ
                        ? new Bounds(new Vector3(-fit.center.z, fit.center.y, fit.center.x), new Vector3(fit.size.z, fit.size.y, fit.size.x))
                        : fit;
                    float width = wall.size.x * Mathf.Abs(alongZ ? filter.transform.lossyScale.z : filter.transform.lossyScale.x);
                    float height = wall.size.y * Mathf.Abs(filter.transform.lossyScale.y);
                    int columns = Mathf.Max(1, Mathf.CeilToInt(width / 1.5f));
                    int rows = Mathf.Max(1, Mathf.CeilToInt(height / 2f));
                    for (int x = 0; x < columns; x++)
                    for (int y = 0; y < rows; y++)
                    {
                        float left = Mathf.Max(wall.min.x, wall.min.x + wall.size.x * (x - .08f) / columns);
                        float right = Mathf.Min(wall.max.x, wall.min.x + wall.size.x * (x + 1.08f) / columns);
                        float bottom = Mathf.Max(wall.min.y, wall.min.y + wall.size.y * (y - .08f) / rows);
                        float top = Mathf.Min(wall.max.y, wall.min.y + wall.size.y * (y + 1.08f) / rows);
                        var tile = Child(facing, "Downloaded_Stone_" + x + "_" + y);
                        tile.gameObject.AddComponent<MeshFilter>().sharedMesh = masonry;
                        tile.gameObject.AddComponent<MeshRenderer>().sharedMaterial = archMaterial;
                        Fit(tile, masonry.bounds, new Bounds(new Vector3((left + right) * .5f, (bottom + top) * .5f, wall.center.z),
                            new Vector3(right - left, top - bottom, wall.size.z)));
                    }
                    renderer.enabled = false;
                }
            }
            long triangles = 0, totalTriangles = 0;
            int draws = 0, totalDraws = 0;
            foreach (var filter in room.GetComponentsInChildren<MeshFilter>(true))
            {
                if (!filter.TryGetComponent<Renderer>(out var renderer) || !renderer.enabled || filter.sharedMesh == null) continue;
                totalDraws++;
                for (int sub = 0; sub < filter.sharedMesh.subMeshCount; sub++) totalTriangles += filter.sharedMesh.GetIndexCount(sub) / 3;
                bool imported = false;
                for (Transform t = filter.transform; t != null && t != room.transform; t = t.parent)
                    if (t.name == VisualName) { imported = true; break; }
                if (!imported) continue;
                draws++;
                for (int sub = 0; sub < filter.sharedMesh.subMeshCount; sub++) triangles += filter.sharedMesh.GetIndexCount(sub) / 3;
            }
            Debug.Log($"[DownloadedArchitecture] {room.name}: {triangles} imported triangles, {draws} renderers; TOTAL enabled {totalTriangles} triangles / {totalDraws} renderers; collider layout preserved.");
        }

        private static void ApplyReliquaries(GameObject room)
        {
            var source = Required<GameObject>(Folder + "/SealedReliquary/Source.fbx");
            var material = Surface("SealedReliquary", .2f);
            foreach (var tomb in room.GetComponentsInChildren<Transform>(true))
            {
                if (tomb.name != "Sarcophagus" || tomb.Find(VisualName) != null) continue;
                var lid = tomb.Find("Lid");
                if (lid == null || !lid.TryGetComponent<BoxCollider>(out var support)) continue;
                // Keep the same lid top for existing wayside statues/candles supported by this tomb.
                float top = tomb.InverseTransformPoint(support.transform.TransformPoint(support.center + Vector3.up * support.size.y * .5f)).y;
                FitPrefab(source, tomb, new Bounds(new Vector3(0, top * .5f, 0), new Vector3(1.8f, top, 3.2f)), material, yaw: 90f, preserveAspect: true);
                foreach (var piece in tomb.Find(VisualName).GetComponentsInChildren<Transform>(true))
                    piece.gameObject.layer = support.gameObject.layer;
                foreach (Transform piece in tomb)
                    if (piece.name != VisualName && piece.TryGetComponent<MeshRenderer>(out var renderer)) renderer.enabled = false;
            }
        }

        // Folded/chamfered outdoor walls already have mesh-matched physics and a broken crown.
        // Do not replace their irregular silhouette with a rectangular bounds fit.
        private static bool IsWall(string name) => name.StartsWith("WallRun_", StringComparison.Ordinal)
            || name.StartsWith("Closure_", StringComparison.Ordinal)
            || name == "Burial_Screen" || name == "Burial_Alcove_Back" || name == "Burial_Gallery_Screen"
            || name == "Cache_WestWall" || name == "Cache_FrontLeft" || name == "Cache_FrontRight"
            || name == "Concealed_Stone_Panel" || name == "Altar_Reredos"
            || name == "Infilled_Wall" || name == "Jamb";

        private static void HideSiblingAt(Transform shaft, string name)
        {
            Vector3 point = shaft.localPosition; point.y = 0;
            foreach (Transform sibling in shaft.parent)
            {
                Vector3 other = sibling.localPosition; other.y = 0;
                if (sibling.name == name && (point - other).sqrMagnitude < .0025f
                    && sibling.TryGetComponent<MeshRenderer>(out var renderer)) renderer.enabled = false;
            }
        }

        private static void FitPrefab(GameObject source, Transform parent, Bounds target, Material material,
            string name = VisualName, float yaw = 0f, bool preserveAspect = false)
        {
            var anchor = Child(parent, name);
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(source);
            // Measure before parenting so scaled room pieces do not contaminate source dimensions.
            instance.transform.SetPositionAndRotation(Vector3.zero, Quaternion.Euler(0, yaw, 0));
            var renderers = instance.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0) throw new InvalidOperationException("Downloaded model has no renderer: " + source.name);
            Bounds bounds = renderers[0].bounds;
            foreach (var renderer in renderers) bounds.Encapsulate(renderer.bounds);
            instance.transform.SetParent(anchor, false);
            foreach (var child in instance.GetComponentsInChildren<Transform>(true)) child.gameObject.layer = parent.gameObject.layer;
            foreach (var collider in instance.GetComponentsInChildren<Collider>()) Object.DestroyImmediate(collider);
            if (material != null)
            {
                foreach (var renderer in renderers)
                {
                    var materials = renderer.sharedMaterials;
                    Array.Fill(materials, material);
                    renderer.sharedMaterials = materials;
                }
                foreach (var filter in instance.GetComponentsInChildren<MeshFilter>())
                {
                    if (filter.sharedMesh == null) continue;
                    AssetDatabase.TryGetGUIDAndLocalFileIdentifier(filter.sharedMesh, out string guid, out long id);
                    string path = Folder + "/" + guid + "_" + id + "_Game.asset";
                    var saved = AssetDatabase.LoadAssetAtPath<Mesh>(path);
                    if (saved == null)
                    {
                        saved = Reduce(filter.sharedMesh, AssetDatabase.GetAssetPath(source).Contains("/RusticBarrel/") ? 5000 : 4000);
                        AssetDatabase.CreateAsset(saved, path);
                    }
                    filter.sharedMesh = saved;
                }
            }
            if (preserveAspect)
            {
                float scale = Mathf.Min(target.size.x / bounds.size.x, target.size.y / bounds.size.y, target.size.z / bounds.size.z);
                Vector3 size = bounds.size * scale;
                target = new Bounds(new Vector3(target.center.x, target.min.y + size.y * .5f, target.center.z), size);
            }
            Fit(anchor, bounds, target);
        }

        private static void Fit(Transform transform, Bounds source, Bounds target)
        {
            Vector3 ratio = new Vector3(target.size.x / source.size.x, target.size.y / source.size.y, target.size.z / source.size.z);
            transform.localScale = ratio;
            transform.localPosition = target.center - Vector3.Scale(source.center, ratio);
        }

        private static Material Surface(string id, float smoothness)
        {
            string path = Folder + "/" + id + "/Surface.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (id == "StonePillar")
            {
                // The downloaded albedo has baked blue/black patches. Keep its carved mesh,
                // but share the dungeon's world-space stone treatment for a coherent surface.
                var stone = Required<Material>("Assets/Emberdelve/Art/Generated/DungeonSurfaces/World_Stone_Wall.mat");
                if (material == null)
                {
                    material = new Material(stone);
                    AssetDatabase.CreateAsset(material, path);
                }
                else EditorUtility.CopySerialized(stone, material);
                material.name = "StonePillar_CryptStone";
                material.enableInstancing = true;
                EditorUtility.SetDirty(material);
                return material;
            }
            if (material == null)
            {
                material = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = id + "_Surface" };
                AssetDatabase.CreateAsset(material, path);
            }
            material.SetTexture("_BaseMap", Required<Texture2D>(Folder + "/" + id + "/Albedo.png"));
            material.SetColor("_BaseColor", Color.white);
            material.SetFloat("_Smoothness", smoothness);
            material.enableInstancing = true;
            var normal = AssetDatabase.LoadAssetAtPath<Texture2D>(Folder + "/" + id + "/Normal.png");
            if (normal != null)
            {
                material.SetTexture("_BumpMap", normal);
                material.EnableKeyword("_NORMALMAP");
            }
            EditorUtility.SetDirty(material);
            return material;
        }

        internal static void RefreshMaterials()
        {
            Surface("StonePillar", .13f);
            AssetDatabase.SaveAssets();
        }

        private static Material StoneMaterial()
        {
            string path = Folder + "/ImportedStone.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(Required<Material>(Community + "/OvergrownArch/OvergrownArch.mat"));
                AssetDatabase.CreateAsset(material, path);
            }
            material.enableInstancing = true;
            EditorUtility.SetDirty(material);
            return material;
        }

        // Crop actual imported triangles, preserving source UVs, normals and material.
        // Crown and jamb are reusable portions of the same downloaded stone arch.
        private static Mesh ArchCrown()
        {
            var source = Required<Mesh>(Community + "/OvergrownArch/OvergrownArch.asset");
            float cutoff = source.bounds.min.y + source.bounds.size.y * .55f;
            return Crop(source, "OvergrownArchCrown", new Plane(Vector3.up, new Vector3(0, cutoff, 0)));
        }

        private static Mesh StonePier()
        {
            var source = Required<Mesh>(Community + "/OvergrownArch/OvergrownArch.asset");
            float side = source.bounds.min.x + source.bounds.size.x * .30f;
            float top = source.bounds.min.y + source.bounds.size.y * .55f;
            return Crop(source, "OvergrownArchStonePier", new Plane(Vector3.left, new Vector3(side, 0, 0)),
                new Plane(Vector3.down, new Vector3(0, top, 0)));
        }

        private static Mesh Crop(Mesh source, string name, params Plane[] planes)
        {
            string path = Folder + "/" + name + ".asset";
            var saved = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (saved != null) return saved;
            Vector3[] points = source.vertices, normals = source.normals;
            Vector2[] uv = source.uv;
            int[] indices = source.triangles;
            var vertices = new List<Vector3>(); var outputNormals = new List<Vector3>();
            var outputUv = new List<Vector2>(); var triangles = new List<int>();
            for (int i = 0; i < indices.Length; i += 3)
            {
                var polygon = new List<Corner>(3);
                for (int j = 0; j < 3; j++)
                {
                    int v = indices[i + j];
                    polygon.Add(new Corner(points[v], normals[v], uv[v]));
                }
                foreach (var plane in planes)
                {
                    var clipped = new List<Corner>(5);
                    for (int j = 0; j < polygon.Count; j++)
                    {
                        Corner start = polygon[j], end = polygon[(j + 1) % polygon.Count];
                        float a = plane.GetDistanceToPoint(start.Point), b = plane.GetDistanceToPoint(end.Point);
                        bool inside = a >= 0f, nextInside = b >= 0f;
                        if (inside) clipped.Add(start);
                        if (inside != nextInside) clipped.Add(Corner.Lerp(start, end, a / (a - b)));
                    }
                    polygon = clipped;
                }
                int first = vertices.Count;
                foreach (var corner in polygon)
                { vertices.Add(corner.Point); outputNormals.Add(corner.Normal); outputUv.Add(corner.Uv); }
                for (int j = 1; j + 1 < polygon.Count; j++)
                { triangles.Add(first); triangles.Add(first + j); triangles.Add(first + j + 1); }
            }
            if (triangles.Count == 0) throw new InvalidOperationException("Imported mesh crop is empty: " + name);
            var mesh = new Mesh { name = "Downloaded_" + name, indexFormat = IndexFormat.UInt32 };
            mesh.SetVertices(vertices); mesh.SetNormals(outputNormals); mesh.SetUVs(0, outputUv); mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds(); mesh.RecalculateTangents();
            var reduced = Reduce(mesh, name == "OvergrownArchCrown" ? 4500 : 700);
            Object.DestroyImmediate(mesh);
            AssetDatabase.CreateAsset(reduced, path);
            return reduced;
        }

        // A deterministic display LOD of the downloaded surface. Position clusters share a centroid
        // across UV seams, while retained corners keep their original UV/normal attributes.
        // The original FBX and source mesh remain intact for a higher-detail rebuild if needed.
        private static Mesh Reduce(Mesh source, int budget)
        {
            Vector3[] points = source.vertices;
            int[] original = source.triangles;
            Vector3[] clustered = points;
            var retained = new List<int>(original);
            Bounds bounds = source.bounds;
            for (int resolution = 64; retained.Count / 3 > budget && resolution >= 4; resolution = Mathf.Max(3, resolution * 4 / 5))
            {
                var lookup = new Dictionary<Vector3Int, int>();
                var sums = new List<Vector3>(); var counts = new List<int>();
                var cells = new int[points.Length];
                for (int i = 0; i < points.Length; i++)
                {
                    Vector3 p = points[i] - bounds.min;
                    var key = new Vector3Int(Mathf.RoundToInt(p.x / Mathf.Max(.0001f, bounds.size.x) * resolution),
                        Mathf.RoundToInt(p.y / Mathf.Max(.0001f, bounds.size.y) * resolution),
                        Mathf.RoundToInt(p.z / Mathf.Max(.0001f, bounds.size.z) * resolution));
                    if (!lookup.TryGetValue(key, out int cell))
                    { cell = sums.Count; lookup.Add(key, cell); sums.Add(Vector3.zero); counts.Add(0); }
                    cells[i] = cell; sums[cell] += points[i]; counts[cell]++;
                }
                clustered = new Vector3[points.Length];
                for (int i = 0; i < points.Length; i++) clustered[i] = sums[cells[i]] / counts[cells[i]];
                retained.Clear();
                var seen = new HashSet<(int, int, int)>();
                for (int i = 0; i < original.Length; i += 3)
                {
                    int a = cells[original[i]], b = cells[original[i + 1]], c = cells[original[i + 2]];
                    if (a == b || b == c || a == c) continue;
                    int low = Mathf.Min(a, Mathf.Min(b, c)), high = Mathf.Max(a, Mathf.Max(b, c));
                    if (!seen.Add((low, a + b + c - low - high, high))) continue;
                    retained.Add(original[i]); retained.Add(original[i + 1]); retained.Add(original[i + 2]);
                }
            }
            if (retained.Count == 0) throw new InvalidOperationException("Downloaded mesh LOD is empty: " + source.name);
            var remap = new Dictionary<int, int>();
            var output = new List<Vector3>(); var normals = new List<Vector3>(); var uv = new List<Vector2>();
            Vector3[] sourceNormals = source.normals; Vector2[] sourceUv = source.uv;
            var indices = new List<int>(retained.Count);
            foreach (int index in retained)
            {
                if (!remap.TryGetValue(index, out int mapped))
                {
                    mapped = output.Count; remap.Add(index, mapped); output.Add(clustered[index]);
                    normals.Add(sourceNormals.Length == points.Length ? sourceNormals[index] : Vector3.up);
                    uv.Add(sourceUv.Length == points.Length ? sourceUv[index] : Vector2.zero);
                }
                indices.Add(mapped);
            }
            var mesh = new Mesh { name = source.name + "_DisplayLOD", indexFormat = IndexFormat.UInt32 };
            mesh.SetVertices(output); mesh.SetNormals(normals); mesh.SetUVs(0, uv); mesh.SetTriangles(indices, 0);
            if (sourceNormals.Length != points.Length) mesh.RecalculateNormals();
            mesh.RecalculateTangents(); mesh.RecalculateBounds();
            Debug.Log($"[DownloadedArchitecture] {source.name}: {original.Length / 3} -> {indices.Count / 3} triangles (budget {budget}).");
            return mesh;
        }

        private readonly struct Corner
        {
            public readonly Vector3 Point, Normal;
            public readonly Vector2 Uv;
            public Corner(Vector3 point, Vector3 normal, Vector2 uv) { Point = point; Normal = normal; Uv = uv; }
            public static Corner Lerp(Corner a, Corner b, float t) => new Corner(Vector3.Lerp(a.Point, b.Point, t),
                Vector3.Lerp(a.Normal, b.Normal, t).normalized, Vector2.Lerp(a.Uv, b.Uv, t));
        }

        private static Transform Child(Transform parent, string name)
        {
            var child = new GameObject(name) { layer = parent.gameObject.layer }.transform;
            child.SetParent(parent, false); return child;
        }

        private static T Required<T>(string path) where T : Object => AssetDatabase.LoadAssetAtPath<T>(path)
            ?? throw new InvalidOperationException("Missing downloaded architecture input: " + path);
    }

    internal sealed class DownloadedCryptArchitectureImport : AssetPostprocessor
    {
        private void OnPreprocessModel()
        {
            if (!assetPath.StartsWith(DownloadedCryptArchitecture.Folder + "/", StringComparison.Ordinal)) return;
            var model = (ModelImporter)assetImporter;
            model.materialImportMode = ModelImporterMaterialImportMode.None;
            model.isReadable = true;
            model.importAnimation = false;
            model.addCollider = false;
        }

        private void OnPreprocessTexture()
        {
            if (!assetPath.StartsWith(DownloadedCryptArchitecture.Folder + "/", StringComparison.Ordinal)) return;
            var texture = (TextureImporter)assetImporter;
            texture.maxTextureSize = 2048;
            texture.mipmapEnabled = true;
            if (assetPath.EndsWith("/Normal.png", StringComparison.Ordinal)) texture.textureType = TextureImporterType.NormalMap;
        }
    }
}
