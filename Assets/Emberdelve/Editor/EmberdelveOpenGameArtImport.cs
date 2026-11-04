using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Emberdelve.EditorTools
{
    /// <summary>
    /// U5B: the two CC0 OpenGameArt sword models (the Knight's Sword by Teh_Bucket for the arming sword, the One Handed Sword by LordNeo,
    /// a long, slender double-edged blade, for the two-handed sword). Files live byte for byte under Assets/Emberdelve/Art/ThirdParty/OpenGameArt/
    /// (renamed only where the original name had an apostrophe or spaces). This class: import settings (readable mesh for the measuring in
    /// <see cref="EmberdelveWeaponModels"/>, no rig / animation / camera / collider, textures point filtered at 512 like the other props),
    /// the project URP/Lit materials the prefabs use (never the importer's materials), and manifest.json + LICENSE_CC0.txt.
    /// Menu: Emberdelve/Visual/Configure OpenGameArt Weapons.
    /// </summary>
    public sealed class EmberdelveOpenGameArtImport : AssetPostprocessor
    {
        public const string Root = "Assets/Emberdelve/Art/ThirdParty/OpenGameArt";
        public const string ManifestPath = Root + "/manifest.json";
        public const string LicensePath = Root + "/LICENSE_CC0.txt";
        public const string KnightsSwordModel = Root + "/knights_sword/knights_sword.obj";
        public const string KnightsSwordTexture = Root + "/knights_sword/knightSword.png";
        public const string LongswordModel = Root + "/one_handed_sword/onehandedsword.fbx";
        public const string LongswordTexture = Root + "/one_handed_sword/Metal_Corrogated_Shiny.jpg";
        public const string MaterialFolder = "Assets/Emberdelve/Art/Materials/OpenGameArt";
        public const string LicenseUrl = "https://creativecommons.org/publicdomain/zero/1.0/";

        [Serializable] public sealed class ManifestFile { public string path; public string title; public string author; public string page; public string source; public string license; public string sha256; public long bytes; public string note; }

        [Serializable]
        public sealed class Manifest
        {
            public string schema = "emberdelve-opengameart-manifest/1";
            public string license = "CC0-1.0";
            public string license_url = LicenseUrl;
            public string note = "Files are byte for byte what the sources serve (renamed where the original name had an apostrophe or spaces). Import settings come from EmberdelveOpenGameArtImport; the prefabs use project URP/Lit materials built by it (solid-colour textures are generated, not third party).";
            public List<ManifestFile> files = new List<ManifestFile>();
        }

        /// <summary>path, title, author, page, source file, sha256 (of the file as downloaded), note.</summary>
        public static readonly string[][] Files =
        {
            new[] { KnightsSwordModel, "Knight's Sword", "Teh_Bucket", "https://opengameart.org/content/knights-sword", "https://opengameart.org/sites/default/files/Knight%27s%20Sword.obj", "b0f8016725ea598fc3e94f542f3f44e148d2756d99edb8d989bb938b81b65fa3", "original name \"Knight's Sword.obj\"; arming sword" },
            new[] { KnightsSwordTexture, "Knight's Sword texture", "Teh_Bucket", "https://opengameart.org/content/knights-sword", "https://opengameart.org/sites/default/files/knightSword.png", "ac8f696de904b8716e357ad21e06548a41304025318438ebdd6b0e7afd94f656", "" },
            new[] { LongswordModel, "One Handed Sword", "LordNeo", "https://opengameart.org/content/one-handed-sword", "https://opengameart.org/sites/default/files/onehandedsword.zip (onehandedsword.fbx)", "f5a55440ac8536ba40b586c4ecf74e2935692866d4dfc89cceb622e11c1a293b", "long slender blade; used as the two-handed sword" },
            new[] { LongswordTexture, "One Handed Sword texture", "LordNeo", "https://opengameart.org/content/one-handed-sword", "https://opengameart.org/sites/default/files/onehandedsword.zip (onehandedsword/Metal_Corrogated_Shiny.jpg)", "e9334d76e18c0fdd62e492693b69ff127cddfb0291038717f88700338e14cdbe", "" },
        };

        public static bool IsOpenGameArt(string assetPath) { return assetPath != null && assetPath.StartsWith(Root + "/", StringComparison.Ordinal); }

        private void OnPreprocessModel()
        {
            if (!IsOpenGameArt(assetPath)) return;
            ApplyModelSettings((ModelImporter)assetImporter);
        }

        private void OnPreprocessTexture()
        {
            if (!IsOpenGameArt(assetPath)) return;
            TextureImporter importer = (TextureImporter)assetImporter;
            importer.filterMode = FilterMode.Point;
            importer.maxTextureSize = 512;
            importer.mipmapEnabled = true;
        }

        public static bool ApplyModelSettings(ModelImporter importer)
        {
            bool changed = false;
            if (importer.animationType != ModelImporterAnimationType.None) { importer.animationType = ModelImporterAnimationType.None; changed = true; }
            if (importer.importAnimation) { importer.importAnimation = false; changed = true; }
            if (importer.importCameras) { importer.importCameras = false; changed = true; }
            if (importer.importLights) { importer.importLights = false; changed = true; }
            if (importer.addCollider) { importer.addCollider = false; changed = true; }
            if (!importer.isReadable) { importer.isReadable = true; changed = true; }
            return changed;
        }

        [MenuItem("Emberdelve/Visual/Configure OpenGameArt Weapons")]
        public static void ConfigureMenu() { Debug.Log("[Emberdelve][OpenGameArt] " + (EnsureConfigured() ? "configured" : "files missing")); }

        /// <summary>Imports the files with the project settings, builds the materials, writes the manifest. Idempotent. False when a file is missing.</summary>
        public static bool EnsureConfigured()
        {
            for (int i = 0; i < Files.Length; i++)
            {
                if (!File.Exists(Absolute(Files[i][0]))) { Debug.LogWarning("[Emberdelve][OpenGameArt] missing " + Files[i][0]); return false; }
            }

            AssetDatabase.Refresh();
            string[] models = { KnightsSwordModel, LongswordModel };
            for (int i = 0; i < models.Length; i++)
            {
                ModelImporter importer = AssetImporter.GetAtPath(models[i]) as ModelImporter;
                if (importer != null && ApplyModelSettings(importer)) importer.SaveAndReimport();
            }

            EnsureMaterials();
            WriteManifest();
            return true;
        }

        // ---------------------------------------------------------------- materials

        public const string KnightsSwordMaterialPath = MaterialFolder + "/OGA_KnightsSword.mat";
        public const string SteelMaterialPath = MaterialFolder + "/OGA_Longsword_Steel.mat";
        public const string LeatherMaterialPath = MaterialFolder + "/OGA_Longsword_Leather.mat";
        public const string DarkMaterialPath = MaterialFolder + "/OGA_Longsword_Dark.mat";

        public static void EnsureMaterials()
        {
            EmberdelveVisualSetup.EnsureFolder(MaterialFolder);
            EnsureMaterial(KnightsSwordMaterialPath, AssetDatabase.LoadAssetAtPath<Texture2D>(KnightsSwordTexture), new Color(0.9f, 0.9f, 0.9f), 0.55f, 0.45f);
            EnsureMaterial(SteelMaterialPath, AssetDatabase.LoadAssetAtPath<Texture2D>(LongswordTexture), new Color(0.82f, 0.84f, 0.88f), 0.6f, 0.5f);
            EnsureMaterial(LeatherMaterialPath, SolidTexture("Leather", new Color32(51, 45, 40, 255)), Color.white, 0f, 0.15f);
            EnsureMaterial(DarkMaterialPath, SolidTexture("Dark", new Color32(36, 36, 38, 255)), Color.white, 0.5f, 0.35f);
            AssetDatabase.SaveAssets();
        }

        private static Texture2D SolidTexture(string name, Color32 color)
        {
            string path = MaterialFolder + "/OGA_Solid_" + name + ".png";
            if (!File.Exists(Absolute(path)))
            {
                Texture2D texture = new Texture2D(4, 4, TextureFormat.RGB24, false);
                Color32[] pixels = new Color32[16];
                for (int i = 0; i < pixels.Length; i++) pixels[i] = color;
                texture.SetPixels32(pixels);
                texture.Apply();
                File.WriteAllBytes(Absolute(path), texture.EncodeToPNG());
                UnityEngine.Object.DestroyImmediate(texture);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            }

            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        private static void EnsureMaterial(string path, Texture2D map, Color color, float metallic, float smoothness)
        {
            Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
            bool create = material == null;
            if (create) material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            material.SetTexture("_BaseMap", map);
            material.SetColor("_BaseColor", color);
            material.SetFloat("_Metallic", metallic);
            material.SetFloat("_Smoothness", smoothness);
            if (create) AssetDatabase.CreateAsset(material, path); else EditorUtility.SetDirty(material);
        }

        /// <summary>The project material for a source material slot of one of the two models (by the source's material name).</summary>
        public static Material MaterialFor(string modelPath, string sourceName)
        {
            string n = sourceName ?? "";
            string path;
            if (modelPath == KnightsSwordModel) path = KnightsSwordMaterialPath;
            else if (n.StartsWith("Sophie_Shoes2", StringComparison.Ordinal)) path = DarkMaterialPath;
            else if (n.StartsWith("Sophie_Shoes", StringComparison.Ordinal)) path = LeatherMaterialPath;
            else path = SteelMaterialPath;
            return AssetDatabase.LoadAssetAtPath<Material>(path);
        }

        // ---------------------------------------------------------------- manifest

        public static Manifest BuildManifest()
        {
            Manifest manifest = new Manifest();
            for (int i = 0; i < Files.Length; i++)
            {
                string absolute = Absolute(Files[i][0]);
                manifest.files.Add(new ManifestFile
                {
                    path = Files[i][0],
                    title = Files[i][1],
                    author = Files[i][2],
                    page = Files[i][3],
                    source = Files[i][4],
                    license = "CC0-1.0",
                    sha256 = File.Exists(absolute) ? Sha256(absolute) : "",
                    bytes = File.Exists(absolute) ? new FileInfo(absolute).Length : 0,
                    note = Files[i][6],
                });
            }

            return manifest;
        }

        public static void WriteManifest()
        {
            string json = JsonUtility.ToJson(BuildManifest(), true) + "\n";
            string path = Absolute(ManifestPath);
            if (!File.Exists(path) || File.ReadAllText(path) != json) File.WriteAllText(path, json, new UTF8Encoding(false));

            const string license =
                "OpenGameArt weapon models used for the arming sword and the two-handed sword.\n" +
                "Knight's Sword by Teh_Bucket: https://opengameart.org/content/knights-sword\n" +
                "One Handed Sword by LordNeo: https://opengameart.org/content/one-handed-sword\n\n" +
                "License of both (as stated on their OpenGameArt pages): CC0 1.0 Universal (public domain dedication), https://creativecommons.org/publicdomain/zero/1.0/\n" +
                "The works may be copied, modified, distributed and used, even commercially, without asking permission. Credit is appreciated, not required.\n";
            string licensePath = Absolute(LicensePath);
            if (!File.Exists(licensePath) || File.ReadAllText(licensePath) != license) File.WriteAllText(licensePath, license, new UTF8Encoding(false));
            AssetDatabase.Refresh();
        }

        public static Manifest ReadManifest()
        {
            string path = Absolute(ManifestPath);
            if (!File.Exists(path)) return null;
            try { return JsonUtility.FromJson<Manifest>(File.ReadAllText(path)); } catch (Exception) { return null; }
        }

        public static string Sha256(string absolutePath)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream stream = File.OpenRead(absolutePath))
            {
                byte[] hash = sha.ComputeHash(stream);
                StringBuilder sb = new StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++) sb.Append(hash[i].ToString("x2"));
                return sb.ToString();
            }
        }

        public static string Absolute(string assetPath)
        {
            string project = Path.GetDirectoryName(Application.dataPath);
            return Path.Combine(project, assetPath).Replace('\\', '/');
        }
    }
}
