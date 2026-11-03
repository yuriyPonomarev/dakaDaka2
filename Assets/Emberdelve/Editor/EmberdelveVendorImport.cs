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
    /// V2: brings the CC0 KayKit packs (Kay Lousberg) into the project. The files were downloaded once, outside Assets, to
    /// &lt;project&gt;/Logs/v2_assets (skeletons/ and dungeon/ keep the layout of the two GitHub repositories); this class copies
    /// them verbatim to Assets/Emberdelve/Art/ThirdParty/KayKit/{Skeletons,Dungeon}/ only where the destination is missing or
    /// different, copies each pack's LICENSE.txt, builds the project materials that use the Point-filtered atlases, and writes
    /// KayKit/manifest.json (source repository, creator, license, sha256 and size of every file). Idempotent; batchmode safe.
    /// Import settings come from <see cref="EmberdelveKayKitImport"/> and <see cref="EmberdelveTextureImport"/>.
    /// </summary>
    internal static class EmberdelveVendorImport
    {
        public const string SourceRelative = "Logs/v2_assets";
        public const string Root = "Assets/Emberdelve/Art/ThirdParty/KayKit";
        public const string SkeletonsRoot = Root + "/Skeletons";
        public const string DungeonRoot = Root + "/Dungeon";
        public const string MaterialsFolder = Root + "/Materials";
        public const string ManifestPath = Root + "/manifest.json";

        public const string SkeletonTexturePath = SkeletonsRoot + "/Characters/fbx/skeleton_texture.png";
        public const string DungeonTexturePath = DungeonRoot + "/texture/dungeon_texture.png";
        public const string SkeletonMaterialPath = MaterialsFolder + "/KayKit_Skeleton.mat";
        public const string GlowMaterialPath = MaterialsFolder + "/KayKit_SkeletonGlow.mat";
        public const string DungeonMaterialPath = MaterialsFolder + "/KayKit_Dungeon.mat";

        public const string SkeletonsRepo = "https://github.com/KayKit-Game-Assets/KayKit-Character-Pack-Skeletons-1.0";
        public const string DungeonRepo = "https://github.com/KayKit-Game-Assets/KayKit-Dungeon-Remastered-1.0";

        public static readonly string[] SkeletonCharacters = { "Skeleton_Minion", "Skeleton_Warrior", "Skeleton_Rogue", "Skeleton_Mage" };
        public static readonly string[] SkeletonAccessories = { "Skeleton_Blade", "Skeleton_Axe", "Skeleton_Shield_Small_A", "Skeleton_Crossbow" };

        public static readonly string[] DungeonModels =
        {
            "torch_mounted", "torch_lit", "chest", "chest_gold", "trunk_medium_A", "barrel_large", "barrel_small_stack",
            "box_stacked", "crates_stacked", "pillar", "pillar_decorated", "column", "banner_red", "banner_patternA_red",
            "candle_lit", "candle_triple", "rubble_large", "rubble_half", "shelf_small_candles", "table_long_broken",
            "keyring_hanging", "sword_shield_broken", "coin_stack_small", "bottle_A_green", "floor_tile_big_spikes"
        };

        // ---------------------------------------------------------------- manifest model (JsonUtility)

        [Serializable]
        internal sealed class ManifestFile
        {
            public string path;
            public string source;
            public string sha256;
            public long bytes;
        }

        [Serializable]
        internal sealed class ManifestPack
        {
            public string name;
            public string version;
            public string source_repo;
            public string creator;
            public string license;
            public string license_url;
            public string license_file;
            public string local_source;
            public string modifications;
            public string usage;
            public List<ManifestFile> files = new List<ManifestFile>();
        }

        [Serializable]
        internal sealed class Manifest
        {
            public string schema = "emberdelve-vendor-manifest/1";
            public List<ManifestPack> packs = new List<ManifestPack>();
        }

        private sealed class Item
        {
            public string Pack;       // "skeletons" / "dungeon": folder name under Logs/v2_assets
            public string Relative;   // forward slashes, relative to the pack folder (identical in source and destination)
            public bool IsModel;
            public string Destination => (Pack == "skeletons" ? SkeletonsRoot : DungeonRoot) + "/" + Relative;
        }

        private static bool _done;
        private static bool _ok;

        // ---------------------------------------------------------------- paths / loading

        public static string CharacterPath(string name) { return SkeletonsRoot + "/Characters/fbx/" + name + ".fbx"; }
        public static string AccessoryPath(string name) { return SkeletonsRoot + "/Assets/fbx/" + name + ".fbx"; }
        public static string DungeonModelPath(string name) { return DungeonRoot + "/fbx/" + name + ".fbx"; }

        public static GameObject LoadCharacter(string name) { return AssetDatabase.LoadAssetAtPath<GameObject>(CharacterPath(name)); }
        public static GameObject LoadAccessory(string name) { return AssetDatabase.LoadAssetAtPath<GameObject>(AccessoryPath(name)); }
        public static GameObject LoadDungeonModel(string name) { return AssetDatabase.LoadAssetAtPath<GameObject>(DungeonModelPath(name)); }

        private static string ProjectRoot()
        {
            return Path.GetDirectoryName(Application.dataPath).Replace('\\', '/');
        }

        private static string Absolute(string assetPath)
        {
            return ProjectRoot() + "/" + assetPath;
        }

        private static List<Item> AllItems()
        {
            List<Item> items = new List<Item>();
            items.Add(new Item { Pack = "skeletons", Relative = "LICENSE.txt" });
            items.Add(new Item { Pack = "skeletons", Relative = "Characters/fbx/skeleton_texture.png" });
            items.Add(new Item { Pack = "dungeon", Relative = "LICENSE.txt" });
            items.Add(new Item { Pack = "dungeon", Relative = "texture/dungeon_texture.png" });

            for (int i = 0; i < SkeletonCharacters.Length; i++) items.Add(new Item { Pack = "skeletons", Relative = "Characters/fbx/" + SkeletonCharacters[i] + ".fbx", IsModel = true });
            for (int i = 0; i < SkeletonAccessories.Length; i++) items.Add(new Item { Pack = "skeletons", Relative = "Assets/fbx/" + SkeletonAccessories[i] + ".fbx", IsModel = true });
            for (int i = 0; i < DungeonModels.Length; i++) items.Add(new Item { Pack = "dungeon", Relative = "fbx/" + DungeonModels[i] + ".fbx", IsModel = true });
            return items;
        }

        /// <summary>Every file the import is responsible for, as asset paths (used by the gate).</summary>
        public static List<string> AllDestinationPaths()
        {
            List<Item> items = AllItems();
            List<string> paths = new List<string>(items.Count);
            for (int i = 0; i < items.Count; i++) paths.Add(items[i].Destination);
            return paths;
        }

        // ---------------------------------------------------------------- menu

        [MenuItem("Emberdelve/Visual/Import KayKit Assets (Logs/v2_assets)")]
        private static void MenuImport()
        {
            bool ok = EnsureImported(true);
            Debug.Log("[Emberdelve][VendorImport] " + (ok ? "done" : "FAILED - see errors above"));
        }

        // ---------------------------------------------------------------- import

        /// <summary>
        /// Copies, imports and registers the KayKit files. Returns true when every file is present in the project afterwards.
        /// <paramref name="force"/> re-checks everything (hashes) even if this editor session already did.
        /// </summary>
        public static bool EnsureImported(bool force)
        {
            if (_done && !force) return _ok;

            _ok = false;
            try { _ok = ImportAll(force); }
            catch (Exception e) { Debug.LogError("[Emberdelve][VendorImport] failed: " + e); }

            _done = true;
            return _ok;
        }

        private static bool ImportAll(bool force)
        {
            string sourceRoot = ProjectRoot() + "/" + SourceRelative;
            bool sourceExists = Directory.Exists(sourceRoot);
            List<Item> items = AllItems();
            List<string> missing = new List<string>();
            bool anyCopied = false;

            EmberdelveVisualSetup.EnsureFolder(Root);
            EmberdelveVisualSetup.EnsureFolder(MaterialsFolder);

            // Phase 1: licenses and atlases, then the materials that the models are remapped to.
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i].IsModel) continue;
                anyCopied |= CopyIfNeeded(items[i], sourceRoot, sourceExists, force, missing);
            }

            EnsureMaterials();

            // Phase 2: the models (the remaps in EmberdelveKayKitImport find the materials from phase 1).
            for (int i = 0; i < items.Count; i++)
            {
                if (!items[i].IsModel) continue;
                anyCopied |= CopyIfNeeded(items[i], sourceRoot, sourceExists, force, missing);
            }

            if (missing.Count > 0)
            {
                // A warning, not an error: other gates call this through the material builder and must not fail on a machine without the packs.
                // The retro gate fails on the return value instead.
                Debug.LogWarning("[Emberdelve][VendorImport] " + missing.Count + " KayKit file(s) are missing and could not be copied from '" +
                               sourceRoot + "'. Expected the downloaded packs there (skeletons/, dungeon/). First missing: " + missing[0]);
                return false;
            }

            // The take list of an FBX only exists after its first import, so the loop flags are (re)applied now. Also repairs
            // a project whose models were imported before the remaps or the loop flags existed.
            int fixedImporters = 0;
            for (int i = 0; i < items.Count; i++)
            {
                if (!items[i].IsModel) continue;
                ModelImporter importer = AssetImporter.GetAtPath(items[i].Destination) as ModelImporter;
                if (importer == null) continue;
                if (EmberdelveKayKitImport.Apply(importer, items[i].Destination))
                {
                    importer.SaveAndReimport();
                    fixedImporters++;
                }
            }

            bool manifestMissing = !File.Exists(Absolute(ManifestPath));
            if (anyCopied || fixedImporters > 0 || manifestMissing || force) WriteManifest(items);

            Debug.Log("[Emberdelve][VendorImport] KayKit assets in place (" + items.Count + " files, copied=" + anyCopied + ", importers fixed=" + fixedImporters + ").");
            return true;
        }

        private static bool CopyIfNeeded(Item item, string sourceRoot, bool sourceExists, bool force, List<string> missing)
        {
            string destinationAsset = item.Destination;
            string destination = Absolute(destinationAsset);
            string source = sourceRoot + "/" + item.Pack + "/" + item.Relative;
            bool destinationExists = File.Exists(destination);
            bool sourceFileExists = sourceExists && File.Exists(source);
            bool copied = false;

            if (sourceFileExists && (!destinationExists || !SameFile(source, destination, force)))
            {
                string directory = Path.GetDirectoryName(destination);
                if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
                File.Copy(source, destination, true);
                copied = true;
            }
            else if (!destinationExists)
            {
                missing.Add(item.Pack + "/" + item.Relative);
                return false;
            }

            bool known = AssetDatabase.LoadMainAssetAtPath(destinationAsset) != null || destinationAsset.EndsWith(".txt");
            if (copied || !known) AssetDatabase.ImportAsset(destinationAsset, ImportAssetOptions.ForceSynchronousImport);
            return copied;
        }

        /// <summary>Same length and either the same timestamp (File.Copy keeps it) or the same SHA-256. force always hashes.</summary>
        private static bool SameFile(string a, string b, bool force)
        {
            FileInfo fa = new FileInfo(a);
            FileInfo fb = new FileInfo(b);
            if (fa.Length != fb.Length) return false;
            if (!force && fa.LastWriteTimeUtc == fb.LastWriteTimeUtc) return true;
            return Sha256(a) == Sha256(b);
        }

        public static string Sha256(string absolutePath)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream stream = File.OpenRead(absolutePath))
            {
                byte[] hash = sha.ComputeHash(stream);
                StringBuilder builder = new StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++) builder.Append(hash[i].ToString("x2"));
                return builder.ToString();
            }
        }

        // ---------------------------------------------------------------- materials

        /// <summary>
        /// Project materials for the KayKit atlases: URP Lit, Point-filtered atlas, no specular highlights or reflections (flat,
        /// late-90s shading). Rebuilt in place on every call, so the .mat GUIDs the model remaps point to stay stable.
        /// </summary>
        public static void EnsureMaterials()
        {
            Texture2D skeleton = EmberdelveVisualSetup.LoadTexture(SkeletonTexturePath);
            Texture2D dungeon = EmberdelveVisualSetup.LoadTexture(DungeonTexturePath);

            ConfigureMaterial(SkeletonMaterialPath, skeleton, new Color(0.86f, 0.83f, 0.78f, 1f), null);
            ConfigureMaterial(DungeonMaterialPath, dungeon, new Color(0.84f, 0.82f, 0.8f, 1f), null);
            ConfigureMaterial(GlowMaterialPath, null, new Color(0.05f, 0.12f, 0.05f, 1f), new Color(0.3f, 1.4f, 0.35f, 1f));
            AssetDatabase.SaveAssets();
        }

        private static void ConfigureMaterial(string path, Texture2D texture, Color tint, Color? emission)
        {
            Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
            bool created = material == null;
            if (created)
            {
                Shader shader = Shader.Find("Universal Render Pipeline/Lit");
                if (shader == null) shader = Shader.Find("Universal Render Pipeline/Unlit");
                if (shader == null)
                {
                    Debug.LogError("[Emberdelve][VendorImport] No URP shader found; '" + path + "' was skipped.");
                    return;
                }

                material = new Material(shader);
            }

            if (texture != null)
            {
                material.SetTexture("_BaseMap", texture);
                if (material.HasProperty("_MainTex")) material.SetTexture("_MainTex", texture);
            }

            material.SetColor("_BaseColor", tint);
            if (material.HasProperty("_Color")) material.SetColor("_Color", tint);
            if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", 0f);
            if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", 0.05f);
            if (material.HasProperty("_SpecularHighlights")) material.SetFloat("_SpecularHighlights", 0f);
            if (material.HasProperty("_EnvironmentReflections")) material.SetFloat("_EnvironmentReflections", 0f);
            material.EnableKeyword("_SPECULARHIGHLIGHTS_OFF");
            material.EnableKeyword("_ENVIRONMENTREFLECTIONS_OFF");

            if (emission.HasValue)
            {
                material.SetColor("_EmissionColor", emission.Value);
                material.EnableKeyword("_EMISSION");
                material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            }

            if (created) AssetDatabase.CreateAsset(material, path);
            else EditorUtility.SetDirty(material);
        }

        // ---------------------------------------------------------------- manifest

        private static void WriteManifest(List<Item> items)
        {
            Manifest manifest = new Manifest();
            manifest.packs.Add(NewPack("KayKit Character Pack: Skeletons", SkeletonsRepo, "skeletons", SkeletonsRoot,
                "Skeleton_Minion/Warrior/Rogue/Mage rigged characters with embedded animations, plus Blade, Axe, small Shield and Crossbow."));
            manifest.packs.Add(NewPack("KayKit Dungeon Remastered", DungeonRepo, "dungeon", DungeonRoot,
                "Dungeon props: wall torch, chests, barrels, crates, pillars, banners, candles, rubble, shelves and similar."));

            for (int i = 0; i < items.Count; i++)
            {
                string destination = Absolute(items[i].Destination);
                if (!File.Exists(destination)) continue;

                ManifestFile file = new ManifestFile();
                file.path = items[i].Destination;
                file.source = SourceRelative + "/" + items[i].Pack + "/" + items[i].Relative;
                file.sha256 = Sha256(destination);
                file.bytes = new FileInfo(destination).Length;
                manifest.packs[items[i].Pack == "skeletons" ? 0 : 1].files.Add(file);
            }

            string json = JsonUtility.ToJson(manifest, true) + "\n";
            string path = Absolute(ManifestPath);
            if (!File.Exists(path) || File.ReadAllText(path) != json)
            {
                File.WriteAllText(path, json, new UTF8Encoding(false));
                AssetDatabase.ImportAsset(ManifestPath);
            }
        }

        private static ManifestPack NewPack(string name, string repo, string packFolder, string root, string usage)
        {
            ManifestPack pack = new ManifestPack();
            pack.name = name;
            pack.version = "1.0";
            pack.source_repo = repo;
            pack.creator = "Kay Lousberg (www.kaylousberg.com)";
            pack.license = "CC0-1.0";
            pack.license_url = "https://creativecommons.org/publicdomain/zero/1.0/";
            pack.license_file = root + "/LICENSE.txt";
            pack.local_source = SourceRelative + "/" + packFolder;
            pack.modifications = "None. Files are copied byte for byte; the import settings (rig, loop flags, material remap, Point filtering, 256 px atlas) are applied by EmberdelveKayKitImport / EmberdelveTextureImport.";
            pack.usage = usage;
            return pack;
        }

        /// <summary>Reads KayKit/manifest.json, or null when it is missing or unreadable.</summary>
        internal static Manifest ReadManifest()
        {
            string path = Absolute(ManifestPath);
            if (!File.Exists(path)) return null;

            try { return JsonUtility.FromJson<Manifest>(File.ReadAllText(path)); }
            catch (Exception) { return null; }
        }
    }
}
