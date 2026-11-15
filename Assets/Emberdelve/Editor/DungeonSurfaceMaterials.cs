using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Emberdelve.EditorTools
{
    /// <summary>Saved stone surfaces: physical texture scale, without changing room geometry.</summary>
    internal static class DungeonSurfaceMaterials
    {
        private const string Folder = "Assets/Emberdelve/Art/Generated/DungeonSurfaces";
        private static readonly Dictionary<string, Material> Materials = new Dictionary<string, Material>();

        [MenuItem("Emberdelve/Visual/Apply Dungeon Stone Surfaces")]
        public static void ApplyAll()
        {
            EnsureMaterials();
            int changed = 0;
            foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/Emberdelve/Prefabs/DungeonModules" }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var root = PrefabUtility.LoadPrefabContents(path);
                try
                {
                    if (!Apply(root)) continue;
                    PrefabUtility.SaveAsPrefabAsset(root, path);
                    changed++;
                }
                finally { PrefabUtility.UnloadPrefabContents(root); }
            }
            AssetDatabase.SaveAssets();
            Debug.Log("[DungeonSurfaces] Updated materials only in " + changed + " room prefabs; transforms, meshes and colliders preserved.");
        }

        public static bool Apply(GameObject root)
        {
            EnsureMaterials();
            bool changed = false;
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                var slots = renderer.sharedMaterials;
                bool replace = false;
                for (int i = 0; i < slots.Length; i++)
                {
                    if (slots[i] == null || !Materials.TryGetValue(slots[i].name, out var material) || slots[i] == material) continue;
                    slots[i] = material; replace = true;
                }
                if (replace) { renderer.sharedMaterials = slots; changed = true; }
            }
            return changed;
        }

        private static void EnsureMaterials()
        {
            if (Materials.Count != 0) return;
            var shader = Shader.Find("Emberdelve/Dungeon World Stone");
            if (shader == null) throw new InvalidOperationException("Dungeon World Stone shader is missing");
            if (!AssetDatabase.IsValidFolder(Folder)) AssetDatabase.CreateFolder("Assets/Emberdelve/Art/Generated", "DungeonSurfaces");
            Create(shader, "Stone_Wall", "Bricks100", new Color(.82f, .79f, .73f), .333333f, .42f, .65f);
            Create(shader, "Stone_Floor", "PavingStones142", new Color(.80f, .78f, .73f), .14f, .38f, .6f);
            Create(shader, "Stone_Ceiling", "Rock030", new Color(.65f, .64f, .61f), .4f, .32f, .55f);
            Create(shader, "Infernal_Basalt", "Rock030", new Color(.48f, .43f, .4f), .4f, .32f, .65f);
            Create(shader, "Infernal_Paving", "PavingStones142", new Color(.63f, .53f, .44f), .14f, .35f, .6f);
            Create(shader, "Infernal_Vault", "Rock030", new Color(.43f, .39f, .37f), .4f, .3f, .55f);
        }

        private static void Create(Shader shader, string source, string texture, Color color, float tiling, float smoothness, float normal)
        {
            string name = "World_" + source;
            string path = Folder + "/" + name + ".mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null) { material = new Material(shader); AssetDatabase.CreateAsset(material, path); }
            material.shader = shader; material.name = name; material.enableInstancing = true;
            material.SetColor("_BaseColor", color);
            material.SetFloat("_WorldTiling", tiling);
            material.SetFloat("_Smoothness", smoothness);
            material.SetFloat("_BumpScale", normal);
            material.SetFloat("_OcclusionStrength", .65f);
            material.SetFloat("_MacroVariation", .12f);
            string basePath = "Assets/Emberdelve/Art/ThirdParty/ambientCG/" + texture + "/" + texture;
            SetTexture(material, "_BaseMap", basePath + "_BaseColor.jpg");
            SetTexture(material, "_BumpMap", basePath + "_Normal.png");
            SetTexture(material, "_MetallicGlossMap", basePath + "_MaskMS.png");
            SetTexture(material, "_OcclusionMap", basePath + "_AO.jpg");
            EditorUtility.SetDirty(material);
            Materials[source] = material;
            Materials[name] = material;
        }

        private static void SetTexture(Material material, string property, string path)
        {
            var texture = AssetDatabase.LoadAssetAtPath<Texture>(path);
            if (texture == null) throw new InvalidOperationException("Missing stone texture: " + path);
            material.SetTexture(property, texture);
        }
    }
}
