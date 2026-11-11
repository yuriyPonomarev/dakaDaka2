using System;
using System.Collections.Generic;
using Emberdelve.Core;
using UnityEditor;
using UnityEngine;

namespace Emberdelve.EditorTools
{
    // Three shared, closed masonry meshes. Crown geometry and collision are the same surface.
    internal static class RuinsWallProfile
    {
        private const string Folder = "Assets/Emberdelve/Art/Generated/RuinsWalls";
        private static readonly float[][] Crowns =
        {
            new[] { 6.5f,6.6f,6.2f,6.25f,5.5f,5.35f,5.8f,5.7f,6.65f,6.8f,6.55f,6.1f,5.3f,5.1f,5.6f,5.7f,6.4f,6.5f,6.05f,6.1f,6.45f },
            new[] { 6.1f,6.2f,5.7f,5.6f,5.15f,5.05f,5.8f,5.95f,6.4f,6.3f,6.75f,6.9f,6.5f,5.7f,5.8f,5.2f,5.3f,6.1f,6.2f,6.55f,6.4f },
            new[] { 6.75f,6.6f,6.2f,6.3f,5.9f,5.8f,5.05f,5.2f,5.7f,5.65f,6.4f,6.55f,6.05f,5.6f,5.7f,6.3f,6.5f,6.8f,6.65f,6.2f,6.3f }
        };

        internal static void Build(Transform root, bool horizontal, bool negative, Material stone, int variant = 0)
        {
            if (root == null) throw new ArgumentNullException(nameof(root));
            int layer = LayerMask.NameToLayer(GameConfig.LevelLayerName);
            if (layer < 0) throw new InvalidOperationException("Missing level collision layer: " + GameConfig.LevelLayerName);
            int profile = (int)((uint)variant % 3);
            var side = new GameObject("RuinedWall_" + (horizontal ? "H" : "V") + (negative ? "_N" : "_P"));
            side.layer = layer;
            side.transform.SetParent(root, false);
            float edge = negative ? -12f : 12f;
            side.transform.localPosition = horizontal ? new Vector3(0, 0, edge) : new Vector3(edge, 0, 0);
            side.transform.localRotation = Quaternion.Euler(0, horizontal ? 0 : 90, 0);
            for (int run = 0; run < 2; run++)
            {
                var wall = new GameObject("BrokenMasonry_" + run);
                wall.layer = layer;
                wall.transform.SetParent(side.transform, false);
                wall.transform.localPosition = new Vector3(run == 0 ? -7 : 7, 0, 0);
                Mesh mesh = ProfileMesh((profile + run + (negative ? 1 : 0)) % 3);
                wall.AddComponent<MeshFilter>().sharedMesh = mesh;
                wall.AddComponent<MeshRenderer>().sharedMaterial = stone;
                wall.AddComponent<MeshCollider>().sharedMesh = mesh;
            }
        }

        private static Mesh ProfileMesh(int profile)
        {
            string path = Folder + "/BrokenWall_" + profile + ".asset";
            Mesh saved = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (saved != null) return saved;
            EnsureFolder(Folder);
            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            var uv = new List<Vector2>();
            float[] crown = Crowns[profile];
            for (int i = 0; i < crown.Length - 1; i++)
            {
                float x = -5f + i * .5f;
                // Full-height solid base; broad coping projects only inside the original 0.7m envelope.
                Prism(x, x + .5f, 0, 0, crown[i] - .18f, crown[i + 1] - .18f, .28f, vertices, triangles, uv);
                // Narrow mortar joints expose the chipped base, never form a hole through the wall.
                float left = Mathf.Lerp(crown[i], crown[i + 1], .035f);
                float right = Mathf.Lerp(crown[i], crown[i + 1], .965f);
                Prism(x + .0175f, x + .4825f, left - .20f, right - .20f, left, right, .35f, vertices, triangles, uv);
            }
            for (int pier = 0; pier < 3; pier++)
            {
                float x = -3.5f + pier * 3.5f;
                int courses = 6 + (pier + profile) % 3;
                for (int row = 0; row < courses; row++)
                {
                    float halfWidth = row == 0 ? .43f : .31f;
                    float y = row * .53f;
                    float chipped = row == courses - 1 ? .20f : .02f;
                    Prism(x - halfWidth, x + halfWidth, y, y, y + .50f, y + .50f - chipped, .35f, vertices, triangles, uv);
                }
            }
            var mesh = new Mesh { name = "BrokenWall_" + profile };
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.SetUVs(0, uv);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            AssetDatabase.CreateAsset(mesh, path);
            return mesh;
        }

        // Extruded trapezoid with independently sloping lower and upper edges, closed on all six sides.
        private static void Prism(float x0, float x1, float low0, float low1, float high0, float high1, float depth,
            List<Vector3> v, List<int> t, List<Vector2> uv)
        {
            Vector3 a = new Vector3(x0, low0, -depth), b = new Vector3(x1, low1, -depth);
            Vector3 c = new Vector3(x1, high1, -depth), d = new Vector3(x0, high0, -depth);
            Vector3 offset = new Vector3(0, 0, depth * 2);
            Quad(a, d, c, b, v, t, uv);
            Quad(a + offset, b + offset, c + offset, d + offset, v, t, uv);
            Quad(a, a + offset, d + offset, d, v, t, uv);
            Quad(b, c, c + offset, b + offset, v, t, uv);
            Quad(d, d + offset, c + offset, c, v, t, uv);
            Quad(a, b, b + offset, a + offset, v, t, uv);
        }

        private static void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, List<Vector3> v, List<int> t, List<Vector2> uv)
        {
            int start = v.Count;
            v.Add(a); v.Add(b); v.Add(c); v.Add(d);
            Vector3 normal = Vector3.Cross(b - a, c - a);
            bool horizontal = Mathf.Abs(normal.y) > Mathf.Abs(normal.x) + Mathf.Abs(normal.z);
            bool end = Mathf.Abs(normal.x) > Mathf.Abs(normal.z);
            foreach (Vector3 point in new[] { a, b, c, d })
                uv.Add(horizontal ? new Vector2(point.x, point.z) : new Vector2(end ? point.z : point.x, point.y));
            t.Add(start); t.Add(start + 1); t.Add(start + 2);
            t.Add(start); t.Add(start + 2); t.Add(start + 3);
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            int slash = path.LastIndexOf('/');
            string parent = path.Substring(0, slash);
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, path.Substring(slash + 1));
        }
    }
}
