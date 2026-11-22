using System;
using System.Collections.Generic;
using Emberdelve.Core;
using Emberdelve.Level;
using UnityEditor;
using UnityEngine;

namespace Emberdelve.EditorTools
{
    // Original closed profiles own collision; softened derivatives are visual only.
    internal static class RuinsWallProfile
    {
        private const string Folder = "Assets/Emberdelve/Art/Generated/RuinsWalls";
        private const float Edge = DungeonModulePlan.ModuleWidthCells;
        // The expanded perimeter borders gallery stairs, the undercroft and the cache.
        // Keep folded masonry within their existing clear strip along the room edge.
        private const float PerimeterDepth = .25f;
        private const float DoorHalf = 2f;
        private static readonly float[][] Crowns =
        {
            new[] { 5.20f, 5.33f, 5.62f, 5.44f, 5.22f, 4.50f, 3.53f, 3.23f, 2.96f, 2.85f, 3.08f, 3.02f, 3.31f, 3.81f, 4.36f, 5.34f, 5.46f, 5.84f, 6.05f, 5.84f, 5.56f },
            new[] { 5.75f, 6.12f, 5.92f, 6.52f, 6.35f, 5.76f, 5.08f, 4.64f, 3.81f, 3.14f, 3.35f, 3.14f, 3.05f, 3.88f, 4.20f, 5.30f, 5.38f, 5.30f, 5.48f, 5.25f, 5.18f },
            new[] { 5.45f, 5.56f, 5.35f, 5.47f, 5.62f, 5.17f, 4.69f, 4.54f, 3.92f, 3.58f, 3.32f, 3.66f, 4.41f, 4.62f, 5.34f, 5.61f, 6.16f, 6.29f, 5.83f, 5.91f, 5.50f }
        };
        // Setbacks are strongest away from both ends so joints and door jambs keep their support.
        private static readonly float[][] Folds =
        {
            new[] { 0f, .03f, .36f, 1.30f, 1.52f, 1.43f, .65f, .17f, 0f },
            new[] { 0f, .08f, .12f, .27f, .86f, 1.43f, 1.24f, .20f, 0f },
            new[] { 0f, .19f, .68f, .96f, .39f, .35f, .89f, .45f, 0f }
        };
        private static readonly Mesh[,] Profiles = new Mesh[Crowns.Length, 2];

        internal static void BuildRoom(Transform root, Material stone, int seed, bool highDeck)
        {
            float cornerCut = .90f + .05f * (seed % 4);
            for (int side = 0; side < 4; side++)
                BuildSide(root, side < 2, side % 2 == 1, seed + side, cornerCut, highDeck, stone);
            BuildCorner(root, -1, 1, seed + 1, cornerCut, stone);
            BuildCorner(root, 1, -1, seed + 2, cornerCut, stone);
        }

        internal static void RebuildMeshes()
        {
            for (int i = 0; i < Crowns.Length; i++)
            {
                Profile(i, false, rebuild: true);
                Profile(i, true, rebuild: true);
            }
        }

        private static void BuildSide(Transform root, bool horizontal, bool negative, int seed, float cut, bool highDeck, Material stone)
        {
            var side = Child(root, "RuinedWall_" + (horizontal ? "H" : "V") + (negative ? "_N" : "_P"));
            float edge = negative ? -Edge : Edge;
            side.localPosition = horizontal ? new Vector3(0, 0, edge) : new Vector3(edge, 0, 0);
            side.localRotation = Quaternion.Euler(0, horizontal ? 0 : 90, 0);
            int cutRun = horizontal == negative ? 1 : 0;
            for (int run = 0; run < 2; run++)
            {
                float extent = run == cutRun ? Edge - cut : Edge;
                float sign = run == 0 ? -1f : 1f;
                var wall = Child(side, "BrokenMasonry_" + run);
                wall.localPosition = new Vector3(sign * (DoorHalf + extent) * .5f, 0, 0);
                float height = highDeck && !negative && run != cutRun ? 1.18f : 1f;
                wall.localScale = new Vector3((extent - DoorHalf) / 10f, height, PerimeterDepth);
                SetMasonry(wall.gameObject, Profile((seed + run) % Crowns.Length, negative), stone);
            }
        }

        private static void BuildCorner(Transform root, int xSign, int zSign, int seed, float cut, Material stone)
        {
            Vector3 a = new Vector3(xSign * (Edge - cut), 0, zSign * Edge);
            Vector3 b = new Vector3(xSign * Edge, 0, zSign * (Edge - cut));
            Vector3 delta = b - a;
            var corner = Child(root, "ChamferedCorner_" + (xSign < 0 ? "NW" : "SE"));
            corner.localPosition = (a + b) * .5f;
            corner.localRotation = Quaternion.FromToRotation(Vector3.right, delta.normalized);
            corner.localScale = new Vector3((delta.magnitude + .14f) / 10f, .81f, PerimeterDepth);
            SetMasonry(corner.gameObject, Profile(seed % Crowns.Length, true), stone);
        }

        private static Transform Child(Transform parent, string name)
        {
            Transform child = parent.Find(name);
            if (child != null) return child;
            var wall = new GameObject(name) { layer = LayerMask.NameToLayer(GameConfig.LevelLayerName) };
            wall.transform.SetParent(parent, false);
            return wall.transform;
        }

        private static void SetMasonry(GameObject wall, Mesh mesh, Material stone)
        {
            if (!wall.TryGetComponent<MeshFilter>(out var filter)) filter = wall.AddComponent<MeshFilter>();
            if (!wall.TryGetComponent<MeshRenderer>(out var renderer)) renderer = wall.AddComponent<MeshRenderer>();
            if (!wall.TryGetComponent<MeshCollider>(out var collider)) collider = wall.AddComponent<MeshCollider>();
            string visualPath = AssetDatabase.GetAssetPath(mesh).Replace("/FoldedMasonry_", "/WeatheredMasonry_");
            var visual = AssetDatabase.LoadAssetAtPath<Mesh>(visualPath);
            // If authored profiles change, use the matching collision surface until the
            // documented Export -> Blender -> Import pass refreshes its bevel derivative.
            filter.sharedMesh = visual != null && visual.name.EndsWith("_" + GeometryFingerprint(mesh), StringComparison.Ordinal) ? visual : mesh;
            renderer.sharedMaterial = stone;
            collider.sharedMesh = mesh;
        }

        internal static string GeometryFingerprint(Mesh mesh)
        {
            using var bytes = new System.IO.MemoryStream();
            using var writer = new System.IO.BinaryWriter(bytes);
            foreach (Vector3 p in mesh.vertices) { writer.Write(p.x); writer.Write(p.y); writer.Write(p.z); }
            foreach (int index in mesh.triangles) writer.Write(index);
            writer.Flush();
            using var sha = System.Security.Cryptography.SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(bytes.ToArray())).Replace("-", "").Substring(0, 16);
        }

        private static Mesh Profile(int index, bool inwardPositive, bool rebuild = false)
        {
            int sign = inwardPositive ? 1 : 0;
            if (!rebuild && Profiles[index, sign] != null) return Profiles[index, sign];

            string name = $"FoldedMasonry_{index}_{(inwardPositive ? "Positive" : "Negative")}";
            string path = $"{Folder}/{name}.asset";
            var saved = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (!rebuild && saved != null) return Profiles[index, sign] = saved;

            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            var uv = new List<Vector2>();
            float[] crown = Crowns[index];
            for (int i = 0; i < crown.Length - 1; i++)
            {
                float x = -5f + i * .5f;
                float z0 = Bend(index, x, inwardPositive);
                float z1 = Bend(index, x + .5f, inwardPositive);
                Prism(x, x + .5f, z0, z1, -.12f, -.12f, crown[i] - .16f, crown[i + 1] - .16f, .35f, .25f,
                    vertices, triangles, uv);
                float left = Mathf.Lerp(crown[i], crown[i + 1], .06f);
                float right = Mathf.Lerp(crown[i], crown[i + 1], .94f);
                float lip = (i + index) % 4 == 1 ? .31f : .39f;
                Prism(x + .03f, x + .47f, Mathf.Lerp(z0, z1, .06f), Mathf.Lerp(z0, z1, .94f),
                    left - .21f, right - .21f, left, right, lip, lip - .026f, vertices, triangles, uv);
            }

            // Weathered buttresses taper upwards and break the long horizontal faces.
            for (int pier = 0; pier < 2; pier++)
            {
                float x = pier == 0 ? -3.67f + index * .37f : 2.88f - index * .25f;
                float baseZ = Bend(index, x, inwardPositive);
                int courses = 5 + (pier + index) % 3;
                for (int row = 0; row < courses; row++)
                {
                    float width = Mathf.Lerp(.64f, .31f, row / (float)courses);
                    float depth = Mathf.Lerp(.65f, .35f, row / (float)courses);
                    float y = row * .48f;
                    float erosion = row == courses - 1 ? .22f : .03f;
                    Prism(x - width, x + width, baseZ, baseZ, y, y, y + .48f, y + .48f - erosion, depth, depth - .06f,
                        vertices, triangles, uv);
                }
            }

            var mesh = new Mesh { name = name };
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.SetUVs(0, uv);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            if (saved != null)
            {
                EditorUtility.CopySerialized(mesh, saved);
                EditorUtility.SetDirty(saved);
                UnityEngine.Object.DestroyImmediate(mesh);
                return Profiles[index, sign] = saved;
            }
            if (!AssetDatabase.IsValidFolder(Folder)) AssetDatabase.CreateFolder("Assets/Emberdelve/Art/Generated", "RuinsWalls");
            AssetDatabase.CreateAsset(mesh, path);
            return Profiles[index, sign] = mesh;
        }

        private static float Bend(int index, float x, bool positive)
        {
            float[] offsets = Folds[index];
            float sample = Mathf.Clamp01((x + 5f) / 10f) * (offsets.Length - 1);
            int segment = Mathf.Min((int)sample, offsets.Length - 2);
            return Mathf.Lerp(offsets[segment], offsets[segment + 1], sample - segment) * (positive ? 1f : -1f);
        }

        private static void Prism(float x0, float x1, float z0, float z1, float lower0, float lower1,
            float upper0, float upper1, float baseDepth, float topDepth, List<Vector3> vertices, List<int> triangles, List<Vector2> uv)
        {
            Vector3 a = new Vector3(x0, lower0, z0 - baseDepth), b = new Vector3(x1, lower1, z1 - baseDepth);
            Vector3 c = new Vector3(x1, upper1, z1 - topDepth), d = new Vector3(x0, upper0, z0 - topDepth);
            Vector3 e = new Vector3(x0, lower0, z0 + baseDepth), f = new Vector3(x1, lower1, z1 + baseDepth);
            Vector3 g = new Vector3(x1, upper1, z1 + topDepth), h = new Vector3(x0, upper0, z0 + topDepth);
            Quad(a, d, c, b, vertices, triangles, uv);
            Quad(e, f, g, h, vertices, triangles, uv);
            Quad(a, e, h, d, vertices, triangles, uv);
            Quad(b, c, g, f, vertices, triangles, uv);
            Quad(d, h, g, c, vertices, triangles, uv);
            Quad(a, b, f, e, vertices, triangles, uv);
        }

        private static void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, List<Vector3> vertices, List<int> triangles, List<Vector2> uv)
        {
            int start = vertices.Count;
            vertices.Add(a);
            vertices.Add(b);
            vertices.Add(c);
            vertices.Add(d);
            Vector3 normal = Vector3.Cross(b - a, c - a);
            bool horizontal = Mathf.Abs(normal.y) > Mathf.Abs(normal.x) + Mathf.Abs(normal.z);
            bool end = Mathf.Abs(normal.x) > Mathf.Abs(normal.z);
            foreach (Vector3 point in new[] { a, b, c, d })
                uv.Add(horizontal ? new Vector2(point.x, point.z) : new Vector2(end ? point.z : point.x, point.y));
            triangles.Add(start);
            triangles.Add(start + 1);
            triangles.Add(start + 2);
            triangles.Add(start);
            triangles.Add(start + 2);
            triangles.Add(start + 3);
        }
    }
}
