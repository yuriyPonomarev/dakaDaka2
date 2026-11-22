using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Emberdelve.EditorTools
{
    // Shallow, non-colliding surface relief. The original floor remains the gameplay surface.
    internal static class DungeonFloorWear
    {
        private const string Folder = "Assets/Emberdelve/Art/Generated/FloorWear";
        private const string ChildName = "Worn_Flagstones";

        [MenuItem("Emberdelve/Visual/Apply Worn Dungeon Floors")]
        public static void ApplyAll()
        {
            int count = 0;
            foreach (var guid in AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/Emberdelve/Prefabs/DungeonModules" }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var root = PrefabUtility.LoadPrefabContents(path);
                try
                {
                    var colliders = root.GetComponentsInChildren<Collider>(true);
                    var original = new string[colliders.Length];
                    for (int i = 0; i < colliders.Length; i++) original[i] = EditorJsonUtility.ToJson(colliders[i]) + colliders[i].transform.localToWorldMatrix.ToString();
                    if (!Apply(root)) continue;
                    if (colliders.Length != root.GetComponentsInChildren<Collider>(true).Length) throw new InvalidOperationException("Floor relief changed collider count");
                    for (int i = 0; i < colliders.Length; i++)
                        if (original[i] != EditorJsonUtility.ToJson(colliders[i]) + colliders[i].transform.localToWorldMatrix.ToString()) throw new InvalidOperationException("Floor relief changed collision geometry");
                    PrefabUtility.SaveAsPrefabAsset(root, path);
                    count++;
                }
                finally { PrefabUtility.UnloadPrefabContents(root); }
            }
            AssetDatabase.SaveAssets();
            Debug.Log("[FloorWear] Saved shallow flagstone relief in " + count + " rooms; colliders and routes unchanged.");
        }

        public static bool Apply(GameObject root)
        {
            var floor = root.transform.Find("Floor");
            if (floor == null || floor.GetComponent<MeshRenderer>() == null) return false;
            if (!AssetDatabase.IsValidFolder(Folder)) AssetDatabase.CreateFolder("Assets/Emberdelve/Art/Generated", "FloorWear");
            int seed = 17;
            foreach (char c in root.name) seed = unchecked(seed * 31 + c);
            var random = new System.Random(seed & int.MaxValue);
            var vertices = new List<Vector3>();
            var indices = new List<int>();
            // Different widths, chipped corners, shallow settling, and missing patches interrupt the perfect plane.
            float half = Emberdelve.Level.DungeonModulePlan.ModuleWidthCells;
            for (int row = 0; row < Mathf.FloorToInt((half * 2f - .4f) / 1.07f); row++)
            {
                float z = -half + .2f + row * 1.07f;
                float x = -half + .2f;
                while (x < half - .4f)
                {
                    float width = Mathf.Min(.75f + (float)random.NextDouble() * .95f, half - .2f - x);
                    float centerX = x + width * .5f;
                    float centerZ = z + .49f;
                    float patch = Mathf.PerlinNoise(centerX * .19f + seed % 97, centerZ * .19f + 31);
                    bool doorway = (Mathf.Abs(centerX) < 2.3f && Mathf.Abs(centerZ) > half - 1.6f) || (Mathf.Abs(centerZ) < 2.3f && Mathf.Abs(centerX) > half - 1.6f);
                    bool undercroft = x + width > 12f && x < 17f && z + .98f > -15f && z < -5f;
                    if (width > .25f && !doorway && !undercroft && patch > .27f && random.NextDouble() > .10)
                    {
                        float height = .025f + (float)random.NextDouble() * .055f;
                        AddStone(vertices, indices, random, centerX, centerZ, width - .055f, .94f, height);
                    }
                    x += width;
                }
            }
            string assetPath = Folder + "/" + root.name + ".asset";
            var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(assetPath);
            if (mesh == null) { mesh = new Mesh(); AssetDatabase.CreateAsset(mesh, assetPath); }
            mesh.Clear(); mesh.name = root.name + "_WornFlagstones";
            mesh.indexFormat = IndexFormat.UInt32;
            mesh.SetVertices(vertices); mesh.SetTriangles(indices, 0);
            mesh.RecalculateNormals(); mesh.RecalculateBounds();
            EditorUtility.SetDirty(mesh);
            var child = root.transform.Find(ChildName);
            if (child == null) { var go = new GameObject(ChildName); child = go.transform; child.SetParent(root.transform, false); }
            child.gameObject.layer = 2; // Ignore Raycast: cosmetic relief cannot intercept interactions or combat.
            var filter = child.GetComponent<MeshFilter>();
            if (filter == null) filter = child.gameObject.AddComponent<MeshFilter>();
            var renderer = child.GetComponent<MeshRenderer>();
            if (renderer == null) renderer = child.gameObject.AddComponent<MeshRenderer>();
            filter.sharedMesh = mesh;
            renderer.sharedMaterial = floor.GetComponent<MeshRenderer>().sharedMaterial;
            renderer.shadowCastingMode = ShadowCastingMode.On;
            renderer.receiveShadows = true;
            return true;
        }

        private static void AddStone(List<Vector3> verts, List<int> tris, System.Random random, float x, float z, float width, float depth, float height)
        {
            float hx = width * .5f, hz = depth * .5f;
            float cut = .07f + (float)random.NextDouble() * .10f;
            var ring = new[] { new Vector2(-hx+cut,-hz), new Vector2(hx-cut,-hz), new Vector2(hx,-hz+cut), new Vector2(hx,hz-cut), new Vector2(hx-cut,hz), new Vector2(-hx+cut,hz), new Vector2(-hx,hz-cut), new Vector2(-hx,-hz+cut) };
            float slopeX = ((float)random.NextDouble() - .5f) * .018f;
            float slopeZ = ((float)random.NextDouble() - .5f) * .018f;
            var top = new Vector3[8]; var baseRing = new Vector3[8];
            for (int i = 0; i < 8; i++)
            {
                var p = ring[i];
                float jitter = .01f + (float)random.NextDouble() * .02f;
                top[i] = new Vector3(x + p.x * .94f, height + p.x * slopeX + p.y * slopeZ, z + p.y * .94f);
                baseRing[i] = new Vector3(x + p.x + jitter, .004f, z + p.y);
            }
            var center = new Vector3(x, height, z);
            for (int i = 0; i < 8; i++)
            {
                int next = (i + 1) % 8;
                Triangle(verts, tris, center, top[next], top[i]);
                Triangle(verts, tris, top[i], top[next], baseRing[next]);
                Triangle(verts, tris, top[i], baseRing[next], baseRing[i]);
            }
        }

        private static void Triangle(List<Vector3> verts, List<int> tris, Vector3 a, Vector3 b, Vector3 c)
        {
            int i = verts.Count; verts.Add(a); verts.Add(b); verts.Add(c);
            tris.Add(i); tris.Add(i+1); tris.Add(i+2);
        }
    }
}
