using UnityEditor;
using UnityEngine;

namespace Emberdelve.EditorTools
{
    /// <summary>Import and material setup for the reduced, skinned Grab3D skeleton.</summary>
    public static class EmberdelveSkeletonAssets
    {
        public const string Folder = "Assets/Emberdelve/Art/ThirdParty/Grab3D/HumanSkeleton";
        public const string ModelPath = Folder + "/Skeleton_Optimized.fbx";
        public const string TexturePath = Folder + "/Skeleton_BaseColor.png";
        public static string MaterialPath(string name) => EmberdelveUndeadMaterials.Folder + "/Skeleton_" + name + ".mat";

        public static bool EnsureConfigured()
        {
            var importer = AssetImporter.GetAtPath(ModelPath) as ModelImporter;
            if (importer == null) return false;
            if (importer.animationType != ModelImporterAnimationType.Human ||
                importer.avatarSetup != ModelImporterAvatarSetup.CreateFromThisModel || importer.optimizeGameObjects || !importer.isReadable)
            {
                importer.animationType = ModelImporterAnimationType.Human;
                importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
                importer.optimizeGameObjects = false;
                importer.isReadable = true;
                importer.SaveAndReimport();
            }
            var textureImporter = AssetImporter.GetAtPath(TexturePath) as TextureImporter;
            if (textureImporter != null && (textureImporter.filterMode != FilterMode.Point || textureImporter.maxTextureSize != 512))
            {
                textureImporter.filterMode = FilterMode.Point;
                textureImporter.maxTextureSize = 512;
                textureImporter.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath) != null && AssetDatabase.LoadAssetAtPath<Texture2D>(TexturePath) != null;
        }

        public static Material MaterialFor(string name)
        {
            string path = MaterialPath(name);
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                AssetDatabase.CreateAsset(material, path);
            }
            Color tint = name == "Warrior" ? new Color(0.58f, 0.53f, 0.44f) :
                name == "Rogue" ? new Color(0.49f, 0.50f, 0.46f) :
                name == "Mage" ? new Color(0.65f, 0.60f, 0.53f) :
                name == "Archer" ? new Color(0.52f, 0.52f, 0.47f) : new Color(0.62f, 0.58f, 0.49f);
            material.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>(TexturePath));
            material.SetColor("_BaseColor", tint);
            material.SetFloat("_Smoothness", 0.1f);
            EditorUtility.SetDirty(material);
            return material;
        }
    }
}
