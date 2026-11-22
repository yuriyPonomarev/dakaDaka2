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
        private const string BrazierVisualName = "Imported_Carved_Brazier";

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
            RepairPropAdapters(room);

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
                else if (name == "Stone_Arch" || name == "Reused_Stone_Arch")
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
                // Walls retain a continuous closed masonry surface. Column fragments are not wall tiles.
            }
            ApplyBraziers(room);
            ArchitecturalJoinRepair.Apply(room);
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

        [MenuItem("Emberdelve/Visual/Repair Saved Carved Braziers")]
        public static void RepairSavedBraziers()
        {
            if (!string.Equals(Application.dataPath.Replace('\\', '/').TrimEnd('/'),
                "C:/Users/yrapa/OneDrive/Desktop/Unity_DakaDaka/DakaDaka_clean_handoff/Assets", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Repair braziers only in the primary clean_handoff checkout.");
            int bowls = 0, rooms = 0;
            foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/Emberdelve/Prefabs/DungeonModules" }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var root = PrefabUtility.LoadPrefabContents(path);
                try
                {
                    int count = ApplyBraziers(root);
                    if (count == 0) continue;
                    if (PrefabUtility.SaveAsPrefabAsset(root, path) == null)
                        throw new InvalidOperationException("Could not save carved braziers: " + path);
                    bowls += count; rooms++;
                }
                finally { PrefabUtility.UnloadPrefabContents(root); }
            }
            Debug.Log($"[CarvedBraziers] {bowls} fitted bowls in {rooms} saved modules; existing colliders, lights and gameplay nodes unchanged.");
        }

        internal static int ApplyBraziers(GameObject room)
        {
            var source = Required<GameObject>(Community + "/StoneBrazier/StoneBrazier.prefab");
            int count = 0;
            foreach (var filter in room.GetComponentsInChildren<MeshFilter>(true))
            {
                if (filter.name != "Brazier_Bowl") continue;
                Transform bowl = filter.transform, frame = bowl.parent;
                Transform light = BrazierSibling(bowl, "Brazier_Light"), shaft = BrazierSibling(bowl, "Column_Shaft");
                Transform anchor = bowl.Find(BrazierVisualName);
                if (anchor == null)
                {
                    anchor = Child(bowl, BrazierVisualName);
                    PrefabUtility.InstantiatePrefab(source, anchor);
                }
                if (anchor.GetComponentsInChildren<Collider>(true).Length != 0)
                    throw new InvalidOperationException("Imported carved bowl must remain visual-only.");
                Bounds model = VisibleMeshBounds(anchor, anchor);
                float bottom = BrazierBearingHeight(shaft, frame, bowl.localPosition) - .003f;
                float scale = (light.localPosition.y - bottom) / model.size.y;
                if (scale <= 0f || Mathf.Max(model.size.x, model.size.z) * scale > 1.10f)
                    throw new InvalidOperationException("Brazier cannot fit its existing flame and column without an oversized bowl.");
                if (Quaternion.Angle(bowl.localRotation, Quaternion.identity) > .01f)
                    throw new InvalidOperationException("Unexpected rotated primitive brazier bowl.");
                Vector3 target = new Vector3(bowl.localPosition.x - model.center.x * scale,
                    bottom - model.min.y * scale, bowl.localPosition.z - model.center.z * scale);
                anchor.localPosition = bowl.InverseTransformPoint(frame.TransformPoint(target));
                anchor.localRotation = Quaternion.identity;
                // Cancel the primitive cylinder's .85/.13/.85 scaling before applying a
                // single scale to the carved bowl; retain its hollow rim and proportions.
                Vector3 primitiveScale = bowl.localScale;
                anchor.localScale = new Vector3(scale / primitiveScale.x, scale / primitiveScale.y, scale / primitiveScale.z);
                foreach (var child in anchor.GetComponentsInChildren<Transform>(true)) child.gameObject.layer = bowl.gameObject.layer;
                filter.GetComponent<MeshRenderer>().enabled = false;
                count++;
            }
            ValidateBraziers(room);
            return count;
        }

        internal static void ValidateBraziers(GameObject room)
        {
            foreach (var filter in room.GetComponentsInChildren<MeshFilter>(true))
            {
                if (filter.name != "Brazier_Bowl") continue;
                Transform bowl = filter.transform, frame = bowl.parent, anchor = bowl.Find(BrazierVisualName);
                if (anchor == null || filter.GetComponent<MeshRenderer>().enabled)
                    throw new InvalidOperationException("Brazier still uses a primitive bowl.");
                Bounds fitted = VisibleMeshBounds(anchor, frame);
                float bearing = BrazierBearingHeight(BrazierSibling(bowl, "Column_Shaft"), frame, bowl.localPosition);
                float flame = BrazierSibling(bowl, "Brazier_Light").localPosition.y;
                Vector3 scale = Vector3.Scale(anchor.localScale, bowl.localScale);
                if (Mathf.Abs(fitted.max.y - flame) > .002f || Mathf.Abs(fitted.min.y - (bearing - .003f)) > .002f
                    || Mathf.Abs(scale.x - scale.y) > .0001f || Mathf.Abs(scale.z - scale.y) > .0001f
                    || Mathf.Max(fitted.size.x, fitted.size.z) > 1.10f)
                    throw new InvalidOperationException("Carved brazier has a support/flame gap or distorted proportions.");
            }
        }

        private static Transform BrazierSibling(Transform bowl, string name)
        {
            foreach (Transform candidate in bowl.parent)
            {
                if (candidate.name != name) continue;
                Vector3 delta = candidate.localPosition - bowl.localPosition; delta.y = 0;
                if (delta.sqrMagnitude < .0025f) return candidate;
            }
            throw new InvalidOperationException("Brazier is missing its aligned " + name);
        }

        private static float BrazierBearingHeight(Transform shaft, Transform space, Vector3 centre)
        {
            // The carved foot spans roughly .62m. Sample the actual upper triangles
            // under its central .44m bearing area, rather than an unrelated high vertex.
            var heights = new float[9]; Array.Fill(heights, float.NegativeInfinity);
            foreach (var filter in shaft.GetComponentsInChildren<MeshFilter>(true))
            {
                if (filter.sharedMesh == null || !filter.TryGetComponent<MeshRenderer>(out var renderer) || !renderer.enabled) continue;
                Matrix4x4 matrix = space.worldToLocalMatrix * filter.transform.localToWorldMatrix;
                Vector3[] points = filter.sharedMesh.vertices; int[] triangles = filter.sharedMesh.triangles;
                for (int i = 0; i < points.Length; i++) points[i] = matrix.MultiplyPoint3x4(points[i]);
                for (int i = 0; i < triangles.Length; i += 3)
                {
                    Vector3 a = points[triangles[i]], b = points[triangles[i + 1]], c = points[triangles[i + 2]];
                    float denominator = (b.z - c.z) * (a.x - c.x) + (c.x - b.x) * (a.z - c.z);
                    if (Mathf.Abs(denominator) < 1e-10f) continue;
                    for (int sample = 0; sample < heights.Length; sample++)
                    {
                        float x = centre.x + (sample % 3 - 1) * .22f, z = centre.z + (sample / 3 - 1) * .22f;
                        float u = ((b.z - c.z) * (x - c.x) + (c.x - b.x) * (z - c.z)) / denominator;
                        float v = ((c.z - a.z) * (x - c.x) + (a.x - c.x) * (z - c.z)) / denominator;
                        float w = 1f - u - v;
                        if (u >= -.00001f && v >= -.00001f && w >= -.00001f)
                            heights[sample] = Mathf.Max(heights[sample], u * a.y + v * b.y + w * c.y);
                    }
                }
            }
            float minimum = float.PositiveInfinity;
            foreach (float height in heights)
            {
                if (!float.IsFinite(height)) throw new InvalidOperationException("Carved brazier has no solid column beneath its foot.");
                minimum = Mathf.Min(minimum, height);
            }
            return minimum;
        }

        private static Bounds VisibleMeshBounds(Transform root, Transform space)
        {
            bool found = false; Bounds bounds = default;
            foreach (var filter in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (filter.sharedMesh == null || !filter.TryGetComponent<MeshRenderer>(out var renderer) || !renderer.enabled) continue;
                Matrix4x4 matrix = space.worldToLocalMatrix * filter.transform.localToWorldMatrix;
                foreach (Vector3 vertex in filter.sharedMesh.vertices)
                {
                    Vector3 point = matrix.MultiplyPoint3x4(vertex);
                    if (!found) { bounds = new Bounds(point, Vector3.zero); found = true; }
                    else bounds.Encapsulate(point);
                }
            }
            if (!found) throw new InvalidOperationException("No visible mesh for brazier support/model: " + root.name);
            return bounds;
        }

        internal static int RepairPropAdapters(GameObject room)
        {
            int repaired = 0;
            foreach (var tomb in room.GetComponentsInChildren<Transform>(true))
            {
                if (tomb == null || tomb.name != "Sarcophagus") continue;
                // The downloaded reliquary is an iron chest, not a stone coffin. Scaling
                // its short end up to this lid made a tall crate and hid the real support.
                // Restore the authored tomb whose visible lid matches its support collider.
                Transform replacement = tomb.Find(VisualName);
                if (replacement != null)
                {
                    if (replacement.GetComponentsInChildren<Collider>(true).Length != 0)
                        throw new InvalidOperationException("Unexpected collider in visual-only tomb replacement");
                    Object.DestroyImmediate(replacement.gameObject);
                }
                foreach (string part in new[] { "Footing", "Casket", "Lid", "Effigy", "Head_Relief" })
                {
                    Transform child = tomb.Find(part);
                    if (child != null && child.TryGetComponent<MeshRenderer>(out var renderer)) renderer.enabled = true;
                }
                repaired++;
            }
            return repaired;
        }

        internal static void ValidatePropAdapters(GameObject room)
        {
            foreach (var tomb in room.GetComponentsInChildren<Transform>(true))
            {
                if (tomb.name != "Sarcophagus") continue;
                Transform lid = tomb.Find("Lid");
                if (tomb.Find(VisualName) != null || lid == null
                    || !lid.TryGetComponent<MeshRenderer>(out var renderer) || !renderer.enabled
                    || !lid.TryGetComponent<BoxCollider>(out var support)
                    || !lid.TryGetComponent<MeshFilter>(out var filter) || filter.sharedMesh == null
                    || (filter.sharedMesh.bounds.center - support.center).sqrMagnitude > .000001f
                    || (filter.sharedMesh.bounds.size - support.size).sqrMagnitude > .000001f)
                    throw new InvalidOperationException("Stone tomb presentation does not match its lid support");
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

        [MenuItem("Emberdelve/Visual/Soften Downloaded Stone Surfaces")]
        public static void RefreshWeatheredStoneSurfaces()
        {
            // Update shared display assets in place: every existing room/door reference and GUID
            // remains valid. Never regenerate sources, colliders or the whole prefab catalog here.
            foreach (string name in new[] { "OvergrownArchStonePier", "OvergrownArchCrown" })
            {
                var mesh = Required<Mesh>(Folder + "/" + name + ".asset");
                RebuildWeatheredNormals(mesh);
                EditorUtility.SetDirty(mesh);
                AssetDatabase.SaveAssetIfDirty(mesh);
            }
            var material = StoneMaterial();
            AssetDatabase.SaveAssetIfDirty(material);
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
            // Weathered masonry should not acquire hard, polished highlights at every LOD facet.
            // Keep the downloaded albedo/normal maps; this only moderates their material response.
            material.SetFloat("_Smoothness", .25f);
            material.SetFloat("_BumpScale", .55f);
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
            var reduced = Reduce(mesh, name == "OvergrownArchCrown" ? 4500 : 700, weatheredStone: true);
            Object.DestroyImmediate(mesh);
            AssetDatabase.CreateAsset(reduced, path);
            return reduced;
        }

        // A deterministic display LOD of the downloaded surface. Position clusters share a centroid
        // across UV seams, while retained corners keep their original UV attributes. Cropped stone
        // reconstructs normals after displacement; other existing prop LODs retain their source normals.
        // The original FBX and source mesh remain intact for a higher-detail rebuild if needed.
        private static Mesh Reduce(Mesh source, int budget, bool weatheredStone = false)
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
            if (weatheredStone) RebuildWeatheredNormals(mesh);
            Debug.Log($"[DownloadedArchitecture] {source.name}: {original.Length / 3} -> {indices.Count / 3} triangles (budget {budget}).");
            return mesh;
        }

        private readonly struct StoneCorner
        {
            public readonly int Vertex, EdgeA, EdgeB;
            public readonly Vector3 Normal;
            public readonly float Weight;
            public StoneCorner(int vertex, int edgeA, int edgeB, Vector3 normal, float weight)
            { Vertex = vertex; EdgeA = edgeA; EdgeB = edgeB; Normal = normal; Weight = weight; }
            public bool SharesEdge(StoneCorner other) => EdgeA == other.EdgeA || EdgeA == other.EdgeB
                || EdgeB == other.EdgeA || EdgeB == other.EdgeB;
        }

        internal static void RebuildWeatheredNormals(Mesh mesh)
        {
            Vector3[] points = mesh.vertices;
            int[] indices = mesh.triangles;
            Vector2[] uv = mesh.uv;
            var positionIds = new int[points.Length];
            var positions = new Dictionary<Vector3Int, int>();
            var groups = new List<List<StoneCorner>>();
            // Derived crop/LOD corners have duplicated positions for UV seams. Welding is only
            // for adjacency; no position, UV, triangle, crop border or collider is moved.
            const float weldTolerance = .00001f;
            for (int i = 0; i < points.Length; i++)
            {
                Vector3 p = points[i] / weldTolerance;
                var key = new Vector3Int(Mathf.RoundToInt(p.x), Mathf.RoundToInt(p.y), Mathf.RoundToInt(p.z));
                if (!positions.TryGetValue(key, out int id))
                { id = groups.Count; positions.Add(key, id); groups.Add(new List<StoneCorner>()); }
                positionIds[i] = id;
            }
            for (int i = 0; i < indices.Length; i += 3)
            {
                int a = indices[i], b = indices[i + 1], c = indices[i + 2];
                Vector3 cross = Vector3.Cross(points[b] - points[a], points[c] - points[a]);
                if (cross.sqrMagnitude < 1e-20f) continue;
                Vector3 normal = cross / Mathf.Sqrt(cross.sqrMagnitude);
                for (int corner = 0; corner < 3; corner++)
                {
                    int vertex = indices[i + corner], next = indices[i + (corner + 1) % 3], previous = indices[i + (corner + 2) % 3];
                    float angle = Vector3.Angle(points[next] - points[vertex], points[previous] - points[vertex]) * Mathf.Deg2Rad;
                    groups[positionIds[vertex]].Add(new StoneCorner(vertex, positionIds[next], positionIds[previous], normal, angle));
                }
            }
            string before = StoneNormalMetrics(mesh.normals, points, indices, positionIds, groups.Count);
            var result = new Vector3[points.Length];
            float threshold = Mathf.Cos(65f * Mathf.Deg2Rad);
            foreach (var corners in groups)
            {
                var visited = new bool[corners.Count];
                var frontier = new int[corners.Count];
                for (int seed = 0; seed < corners.Count; seed++)
                {
                    Array.Clear(visited, 0, visited.Length);
                    int read = 0, count = 1;
                    frontier[0] = seed; visited[seed] = true;
                    Vector3 sum = Vector3.zero;
                    while (read < count)
                    {
                        StoneCorner current = corners[frontier[read++]];
                        sum += current.Normal * current.Weight;
                        for (int candidate = 0; candidate < corners.Count; candidate++)
                        {
                            if (visited[candidate] || !current.SharesEdge(corners[candidate])) continue;
                            // Gate against both the seed and this edge. Never smooth across an
                            // opposite-facing sheet or gradually walk around a sharp corner.
                            if (Vector3.Dot(corners[seed].Normal, corners[candidate].Normal) < threshold
                                || Vector3.Dot(current.Normal, corners[candidate].Normal) < threshold) continue;
                            visited[candidate] = true;
                            frontier[count++] = candidate;
                        }
                    }
                    result[corners[seed].Vertex] += sum.normalized * corners[seed].Weight;
                }
            }
            Vector3[] oldNormals = mesh.normals;
            for (int i = 0; i < result.Length; i++)
                result[i] = result[i].sqrMagnitude > 1e-12f ? result[i].normalized
                    : oldNormals.Length == result.Length ? oldNormals[i] : Vector3.up;
            mesh.SetNormals(result);
            mesh.RecalculateTangents();

            // A normal-only refresh must retain exact render/collision correspondence and UVs.
            Vector3[] afterPoints = mesh.vertices;
            int[] afterIndices = mesh.triangles;
            Vector2[] afterUv = mesh.uv;
            if (afterPoints.Length != points.Length || afterIndices.Length != indices.Length || afterUv.Length != uv.Length)
                throw new InvalidOperationException("Stone normal refresh changed mesh layout: " + mesh.name);
            for (int i = 0; i < points.Length; i++)
                if (!points[i].Equals(afterPoints[i])) throw new InvalidOperationException("Stone normal refresh moved a vertex: " + mesh.name);
            for (int i = 0; i < indices.Length; i++)
                if (indices[i] != afterIndices[i]) throw new InvalidOperationException("Stone normal refresh changed a face: " + mesh.name);
            for (int i = 0; i < uv.Length; i++)
                if (!uv[i].Equals(afterUv[i])) throw new InvalidOperationException("Stone normal refresh changed UVs: " + mesh.name);
            string after = StoneNormalMetrics(result, points, indices, positionIds, groups.Count);
            Debug.Log($"[WeatheredStone] {mesh.name}: {points.Length} vertices / {indices.Length / 3} triangles unchanged; positions, faces and UVs exact. Normals BEFORE {before}; AFTER {after}.");
        }

        private static string StoneNormalMetrics(Vector3[] normals, Vector3[] points, int[] indices, int[] positionIds, int groupCount)
        {
            if (normals.Length != points.Length) return "missing normals";
            int corners = 0, backwards = 0;
            double angleSum = 0;
            for (int i = 0; i < indices.Length; i += 3)
            {
                Vector3 face = Vector3.Cross(points[indices[i + 1]] - points[indices[i]], points[indices[i + 2]] - points[indices[i]]);
                if (face.sqrMagnitude < 1e-20f) continue;
                face /= Mathf.Sqrt(face.sqrMagnitude);
                for (int j = 0; j < 3; j++)
                {
                    Vector3 normal = normals[indices[i + j]].normalized;
                    float dot = Mathf.Clamp(Vector3.Dot(face, normal), -1f, 1f);
                    angleSum += Mathf.Acos(dot) * Mathf.Rad2Deg;
                    if (dot < 0) backwards++;
                    corners++;
                }
            }
            var sums = new Vector3[groupCount];
            for (int i = 0; i < normals.Length; i++) sums[positionIds[i]] += normals[i].normalized;
            var deviations = new float[normals.Length];
            for (int i = 0; i < normals.Length; i++) deviations[i] = Vector3.Angle(normals[i], sums[positionIds[i]]);
            Array.Sort(deviations);
            float p90 = deviations.Length == 0 ? 0 : deviations[Mathf.Min(deviations.Length - 1, Mathf.FloorToInt(deviations.Length * .9f))];
            return $"backwards corners {backwards}/{corners}, mean face angle {angleSum / Math.Max(1, corners):F2} deg, coincident-normal p90 {p90:F2} deg";
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
