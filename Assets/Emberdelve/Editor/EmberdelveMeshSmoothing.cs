using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Emberdelve.EditorTools
{
    /// <summary>
    /// Cosmetic smoothing only. Architecture: swaps the render mesh of primitive cubes/cylinders in the saved room
    /// prefabs for bevelled boxes and lathe-turned columns, bowls and urns. Transforms, colliders, materials, sockets
    /// and hierarchy order are untouched. Enemies: one curved subdivision of the undead body meshes, in place, from a
    /// kept *_Source copy so running twice never subdivides twice.
    /// </summary>
    public static class EmberdelveMeshSmoothing
    {
        const string Folder = "Assets/Emberdelve/Art/Generated/ArchitectureSmooth";
        const string UndeadFolder = "Assets/Emberdelve/Art/Models/Undead/";
        // Abutting structural runs keep their exact box faces; bevels there would open grooves between segments.
        static readonly string[] Structural = { "Floor", "Roof", "WallRun", "Closure", "Arch_Jamb", "Arch_Lintel", "Wall", "Ceiling" };
        static readonly Dictionary<string, Mesh> Cache = new Dictionary<string, Mesh>();

        // ---------------------------------------------------------------- architecture

        [MenuItem("Emberdelve/Visual/Smooth Dungeon Architecture")]
        public static void SmoothArchitecture()
        {
            Cache.Clear();
            EmberdelveVisualSetup.EnsureFolder(Folder);
            Mesh cube = Resources.GetBuiltinResource<Mesh>("Cube.fbx"), cylinder = Resources.GetBuiltinResource<Mesh>("Cylinder.fbx");
            int prefabs = 0, parts = 0;
            foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/Emberdelve/Prefabs/DungeonModules" }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                GameObject root = PrefabUtility.LoadPrefabContents(path);
                try
                {
                    int changed = 0;
                    foreach (MeshFilter filter in root.GetComponentsInChildren<MeshFilter>(true))
                    {
                        Mesh current = filter.sharedMesh;
                        if (current == null) continue;
                        bool legacyBox = current.name.StartsWith("Box_", StringComparison.Ordinal);
                        bool box = current == cube || Primitive(current, "Cube") || legacyBox || current.name.StartsWith("Bevel_", StringComparison.Ordinal);
                        bool round = current == cylinder || Primitive(current, "Cylinder") || current.name.StartsWith("Lathe_", StringComparison.Ordinal);
                        if (!box && !round) continue;
                        Mesh wanted = box ? BoxFor(filter) : LatheFor(filter.name);
                        // A structural part that an earlier pass rounded goes back to its exact box faces.
                        if (wanted == null && legacyBox) wanted = cube;
                        if (wanted == null || wanted == current) continue;
                        filter.sharedMesh = wanted;
                        changed++;
                    }
                    if (changed == 0) continue;
                    PrefabUtility.SaveAsPrefabAsset(root, path);
                    prefabs++; parts += changed;
                }
                finally { PrefabUtility.UnloadPrefabContents(root); }
            }
            AssetDatabase.SaveAssets();
            Debug.Log("[MeshSmoothing] architecture: " + parts + " render meshes in " + prefabs + " room prefabs, " + Cache.Count + " shared meshes; colliders and transforms preserved.");
        }

        // Built-in primitives saved in prefabs are not always the same instance GetBuiltinResource returns.
        static bool Primitive(Mesh mesh, string name)
        {
            return mesh.name == name && !AssetDatabase.GetAssetPath(mesh).StartsWith("Assets/", StringComparison.Ordinal);
        }

        static Mesh BoxFor(MeshFilter filter)
        {
            string name = filter.name;
            for (int i = 0; i < Structural.Length; i++)
                if (name.StartsWith(Structural[i], StringComparison.Ordinal)) return null;
            Vector3 s = filter.transform.lossyScale;
            s = new Vector3(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z));
            float min = Mathf.Min(s.x, Mathf.Min(s.y, s.z)), max = Mathf.Max(s.x, Mathf.Max(s.y, s.z));
            if (min < .01f || max > 6f) return null;
            float bevel = Mathf.Clamp(min * .12f, .004f, .04f);
            string key = "Bevel_" + Mathf.RoundToInt(s.x * 1000) + "_" + Mathf.RoundToInt(s.y * 1000) + "_" + Mathf.RoundToInt(s.z * 1000);
            return Saved(key, () => BevelBox(s, bevel));
        }

        static Mesh LatheFor(string name)
        {
            if (name.StartsWith("Column_Shaft", StringComparison.Ordinal)) return Saved("Lathe_Column", Column);
            if (name.StartsWith("Brazier_Bowl", StringComparison.Ordinal))
                return Saved("Lathe_Bowl", () => Lathe(new[] { P(0, -1), P(.2f, -1), P(.23f, -.86f), P(.27f, -.45f), P(.36f, .1f), P(.46f, .62f), P(.5f, .9f), P(.5f, 1), P(.465f, 1), P(.44f, .78f), P(.36f, .6f), P(.2f, .5f), P(0, .47f) }, 64, 0));
            if (name.StartsWith("Funerary_Urn", StringComparison.Ordinal))
                return Saved("Lathe_Urn", () => Lathe(new[] { P(0, -1), P(.28f, -1), P(.31f, -.93f), P(.36f, -.7f), P(.45f, -.3f), P(.5f, .1f), P(.48f, .42f), P(.41f, .7f), P(.32f, .9f), P(.28f, 1), P(0, 1) }, 48, 0));
            return Saved("Lathe_Drum", () => Lathe(new[] { P(0, -1), P(.44f, -1), P(.485f, -.97f), P(.5f, -.9f), P(.5f, .9f), P(.485f, .97f), P(.44f, 1), P(0, 1) }, 48, 0));
        }

        static Vector2 P(float radius, float y) { return new Vector2(radius, y); }

        static Mesh Column()
        {
            // Unit cylinder space (radius .5, y -1..1): gentle entasis, plain neck bands, twenty flutes between them.
            var profile = new List<Vector2> { P(0, -1) };
            const int rows = 28;
            for (int i = 0; i <= rows; i++)
            {
                float t = i / (float)rows;
                profile.Add(P(.5f * (1f - .12f * Mathf.Pow(t, 1.7f)), t * 2 - 1));
            }
            profile.Add(P(0, 1));
            return Lathe(profile.ToArray(), 160, 20);
        }

        /// <summary>Profile runs bottom centre, up the outside, to the top centre (and back down the inside for bowls).</summary>
        static Mesh Lathe(Vector2[] profile, int sides, int flutes)
        {
            int stride = sides + 1;
            var vertices = new Vector3[profile.Length * stride];
            var uv = new Vector2[vertices.Length];
            float length = 0;
            for (int row = 0; row < profile.Length; row++)
            {
                if (row > 0) length += Vector2.Distance(profile[row], profile[row - 1]);
                float t = (profile[row].y + 1) * .5f;
                float fluted = flutes > 0 ? Mathf.SmoothStep(0, 1, Mathf.InverseLerp(.05f, .1f, t)) * Mathf.SmoothStep(0, 1, Mathf.InverseLerp(.95f, .9f, t)) : 0;
                for (int i = 0; i <= sides; i++)
                {
                    float angle = (i % sides) * Mathf.PI * 2 / sides;
                    float radius = profile[row].x * (1f - .05f * fluted * Mathf.Abs(Mathf.Sin(angle * flutes * .5f)));
                    vertices[row * stride + i] = new Vector3(Mathf.Cos(angle) * radius, profile[row].y, Mathf.Sin(angle) * radius);
                    uv[row * stride + i] = new Vector2(i / (float)sides * 4, length);
                }
            }
            var triangles = new List<int>();
            for (int row = 0; row < profile.Length - 1; row++)
                for (int i = 0; i < sides; i++)
                {
                    int a = row * stride + i, b = a + stride, c = a + 1, d = b + 1;
                    triangles.Add(a); triangles.Add(b); triangles.Add(c);
                    triangles.Add(b); triangles.Add(d); triangles.Add(c);
                }
            var mesh = new Mesh { vertices = vertices, uv = uv, triangles = triangles.ToArray() };
            mesh.RecalculateNormals();
            Vector3[] normals = mesh.normals;
            for (int row = 0; row < profile.Length; row++)
            {
                Vector3 seam = (normals[row * stride] + normals[row * stride + sides]).normalized;
                normals[row * stride] = seam; normals[row * stride + sides] = seam;
            }
            mesh.normals = normals;
            mesh.RecalculateTangents(); mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>Unit box for a part of world size <paramref name="size"/>, with a rounded edge of world width bevel.</summary>
        static Mesh BevelBox(Vector3 size, float bevel)
        {
            Vector3 half = size * .5f;
            var vertices = new List<Vector3>(); var normals = new List<Vector3>(); var uv = new List<Vector2>(); var triangles = new List<int>();
            for (int axis = 0; axis < 3; axis++)
                for (int sign = -1; sign <= 1; sign += 2)
                {
                    int u = (axis + 1) % 3, v = (axis + 2) % 3, start = vertices.Count;
                    float[] us = Steps(half[u], bevel), vs = Steps(half[v], bevel);
                    for (int iv = 0; iv < 6; iv++)
                        for (int iu = 0; iu < 6; iu++)
                        {
                            Vector3 p = Vector3.zero; p[axis] = sign * half[axis]; p[u] = us[iu]; p[v] = vs[iv];
                            Vector3 inner = new Vector3(Mathf.Clamp(p.x, -half.x + bevel, half.x - bevel), Mathf.Clamp(p.y, -half.y + bevel, half.y - bevel), Mathf.Clamp(p.z, -half.z + bevel, half.z - bevel));
                            Vector3 normal = (p - inner).normalized;
                            Vector3 world = inner + normal * bevel;
                            vertices.Add(new Vector3(world.x / size.x, world.y / size.y, world.z / size.z));
                            // Normals go through the inverse transpose of the part's non-uniform scale.
                            normals.Add(Vector3.Scale(normal, size).normalized);
                            uv.Add(new Vector2(p[u] / size[u] + .5f, p[v] / size[v] + .5f));
                        }
                    for (int iv = 0; iv < 5; iv++)
                        for (int iu = 0; iu < 5; iu++)
                        {
                            int a = start + iv * 6 + iu, b = a + 1, c = a + 6, d = c + 1;
                            Vector3 outward = Vector3.zero; outward[axis] = sign;
                            // Front faces follow Cross(b - a, c - a); orient against the face direction in world space.
                            Vector3 wa = Vector3.Scale(vertices[a], size), wb = Vector3.Scale(vertices[b], size), wc = Vector3.Scale(vertices[c], size);
                            bool flip = Vector3.Dot(Vector3.Cross(wb - wa, wc - wa), outward) < 0;
                            if (flip) { triangles.Add(a); triangles.Add(c); triangles.Add(b); triangles.Add(b); triangles.Add(c); triangles.Add(d); }
                            else { triangles.Add(a); triangles.Add(b); triangles.Add(c); triangles.Add(b); triangles.Add(d); triangles.Add(c); }
                        }
                }
            var mesh = new Mesh();
            mesh.SetVertices(vertices); mesh.SetNormals(normals); mesh.SetUVs(0, uv); mesh.SetTriangles(triangles, 0);
            mesh.RecalculateTangents(); mesh.RecalculateBounds();
            return mesh;
        }

        static float[] Steps(float half, float bevel)
        {
            return new[] { -half, -half + bevel * .5f, -half + bevel, half - bevel, half - bevel * .5f, half };
        }

        static Mesh Saved(string key, Func<Mesh> build)
        {
            Mesh cached;
            if (Cache.TryGetValue(key, out cached)) return cached;
            string path = Folder + "/" + key + ".asset";
            Mesh mesh = build();
            mesh.name = key;
            Mesh existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (existing != null)
            {
                EditorUtility.CopySerialized(mesh, existing);
                Object.DestroyImmediate(mesh);
                EditorUtility.SetDirty(existing);
                mesh = existing;
            }
            else AssetDatabase.CreateAsset(mesh, path);
            Cache[key] = mesh;
            return mesh;
        }

        // ---------------------------------------------------------------- enemies

        [MenuItem("Emberdelve/Visual/Smooth Enemy Bodies")]
        public static void SmoothEnemies()
        {
            foreach (string name in new[] { "Undead_Mannequin_M", "Undead_Mannequin_F" })
            {
                string path = UndeadFolder + name + ".asset", sourcePath = UndeadFolder + name + "_Source.asset";
                Mesh live = AssetDatabase.LoadAssetAtPath<Mesh>(path);
                if (live == null) { Debug.LogWarning("[MeshSmoothing] missing enemy mesh " + path); continue; }
                Mesh source = AssetDatabase.LoadAssetAtPath<Mesh>(sourcePath);
                if (source == null)
                {
                    source = Object.Instantiate(live);
                    source.name = name + "_Source";
                    AssetDatabase.CreateAsset(source, sourcePath);
                }
                int before = source.vertexCount;
                Mesh smooth = Subdivide(source);
                smooth.name = live.name;
                int after = smooth.vertexCount;
                EditorUtility.CopySerialized(smooth, live);
                Object.DestroyImmediate(smooth);
                EditorUtility.SetDirty(live);
                Debug.Log("[MeshSmoothing] " + name + ": " + before + " -> " + after + " vertices");
            }
            AssetDatabase.SaveAssets();
        }

        /// <summary>One level of curved (Phong) subdivision with position-welded smooth normals. Keeps skinning, UVs, colours.</summary>
        public static Mesh Subdivide(Mesh source)
        {
            Mesh result = Object.Instantiate(source);
            int[][] submeshes = new int[source.subMeshCount][];
            for (int s = 0; s < submeshes.Length; s++) submeshes[s] = source.GetTriangles(s);
            var positions = new List<Vector3>(source.vertices);
            Vector3[] guide = WeldedNormals(positions, submeshes);
            if (source.blendShapeCount > 0 || source.vertexCount > 20000)
            {
                // Too dense or shape-keyed: only remove the faceting.
                result.normals = guide;
                return result;
            }
            var uv = new List<Vector2>(source.uv); var colors = new List<Color>(source.colors); var weights = new List<BoneWeight>(source.boneWeights);
            bool hasUv = uv.Count == positions.Count, hasColors = colors.Count == positions.Count, hasWeights = weights.Count == positions.Count;
            var guides = new List<Vector3>(guide);
            var midpoints = new Dictionary<long, int>();
            Func<int, int, int> mid = (i, j) =>
            {
                long key = i < j ? ((long)i << 32) | (uint)j : ((long)j << 32) | (uint)i;
                int index;
                if (midpoints.TryGetValue(key, out index)) return index;
                Vector3 m = (positions[i] + positions[j]) * .5f;
                Vector3 qi = m - Vector3.Dot(m - positions[i], guides[i]) * guides[i];
                Vector3 qj = m - Vector3.Dot(m - positions[j], guides[j]) * guides[j];
                index = positions.Count;
                positions.Add(Vector3.Lerp(m, (qi + qj) * .5f, .75f));
                guides.Add((guides[i] + guides[j]).normalized);
                if (hasUv) uv.Add((uv[i] + uv[j]) * .5f);
                if (hasColors) colors.Add((colors[i] + colors[j]) * .5f);
                if (hasWeights) weights.Add(Blend(weights[i], weights[j]));
                midpoints[key] = index;
                return index;
            };
            for (int s = 0; s < submeshes.Length; s++)
            {
                int[] old = submeshes[s];
                var next = new List<int>(old.Length * 4);
                for (int t = 0; t + 2 < old.Length; t += 3)
                {
                    int a = old[t], b = old[t + 1], c = old[t + 2];
                    int ab = mid(a, b), bc = mid(b, c), ca = mid(c, a);
                    next.AddRange(new[] { a, ab, ca, ab, b, bc, ca, bc, c, ab, bc, ca });
                }
                submeshes[s] = next.ToArray();
            }
            result.Clear();
            result.indexFormat = positions.Count > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
            result.SetVertices(positions);
            if (hasUv) result.SetUVs(0, uv);
            if (hasColors) result.SetColors(colors);
            if (hasWeights) { result.boneWeights = weights.ToArray(); result.bindposes = source.bindposes; }
            result.subMeshCount = submeshes.Length;
            for (int s = 0; s < submeshes.Length; s++) result.SetTriangles(submeshes[s], s);
            result.normals = WeldedNormals(positions, submeshes);
            if (hasUv) result.RecalculateTangents();
            result.RecalculateBounds();
            return result;
        }

        static Vector3[] WeldedNormals(List<Vector3> positions, int[][] submeshes)
        {
            var groups = new Dictionary<Vector3Int, int>();
            var group = new int[positions.Count];
            for (int i = 0; i < positions.Count; i++)
            {
                Vector3 p = positions[i];
                var key = new Vector3Int(Mathf.RoundToInt(p.x * 20000f), Mathf.RoundToInt(p.y * 20000f), Mathf.RoundToInt(p.z * 20000f));
                int id;
                if (!groups.TryGetValue(key, out id)) { id = groups.Count; groups[key] = id; }
                group[i] = id;
            }
            var sums = new Vector3[groups.Count];
            for (int s = 0; s < submeshes.Length; s++)
            {
                int[] triangles = submeshes[s];
                for (int t = 0; t + 2 < triangles.Length; t += 3)
                {
                    int a = triangles[t], b = triangles[t + 1], c = triangles[t + 2];
                    Vector3 face = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]); // area weighted
                    sums[group[a]] += face; sums[group[b]] += face; sums[group[c]] += face;
                }
            }
            var normals = new Vector3[positions.Count];
            for (int i = 0; i < normals.Length; i++)
            {
                Vector3 n = sums[group[i]];
                normals[i] = n.sqrMagnitude > 1e-20f ? n.normalized : Vector3.up;
            }
            return normals;
        }

        static BoneWeight Blend(BoneWeight a, BoneWeight b)
        {
            var total = new Dictionary<int, float>();
            Action<int, float> add = (bone, weight) =>
            {
                if (weight <= 0) return;
                float current; total.TryGetValue(bone, out current); total[bone] = current + weight * .5f;
            };
            add(a.boneIndex0, a.weight0); add(a.boneIndex1, a.weight1); add(a.boneIndex2, a.weight2); add(a.boneIndex3, a.weight3);
            add(b.boneIndex0, b.weight0); add(b.boneIndex1, b.weight1); add(b.boneIndex2, b.weight2); add(b.boneIndex3, b.weight3);
            var top = total.OrderByDescending(pair => pair.Value).Take(4).ToArray();
            float sum = top.Sum(pair => pair.Value);
            if (sum <= 0) return a;
            var result = new BoneWeight();
            if (top.Length > 0) { result.boneIndex0 = top[0].Key; result.weight0 = top[0].Value / sum; }
            if (top.Length > 1) { result.boneIndex1 = top[1].Key; result.weight1 = top[1].Value / sum; }
            if (top.Length > 2) { result.boneIndex2 = top[2].Key; result.weight2 = top[2].Value / sum; }
            if (top.Length > 3) { result.boneIndex3 = top[3].Key; result.weight3 = top[3].Value / sum; }
            return result;
        }
    }
}
