using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Emberdelve.EditorTools
{
    /// <summary>
    /// V3a: realistic CC0 props from Poly Haven (https://polyhaven.com, license https://polyhaven.com/license) that replace the
    /// cartoony KayKit dungeon props. Everything is reproducible from this file:
    ///   Fetch(ids)          downloads the 1k FBX plus its texture set (api.polyhaven.com/files/ID, fbx -> 1k, with the "include" map
    ///                       keeping the relative paths) into Assets/Emberdelve/Art/ThirdParty/PolyHaven/ID/. Poly Haven ships the
    ///                       normal / roughness / metal maps of many models as EXR; those are fetched as PNG instead (same name).
    ///   BuildMaterials(id)  one URP/Lit material per FBX material under ID/Materials: base colour + OpenGL normal map (Unity convention)
    ///                       + a packed _MaskMS (R metallic * 0.5, A smoothness = 1 - roughness; the project convention), the FBX is remapped to them.
    ///   RegisterGeneratedFiles()  adds the generated mask maps to PolyHaven/manifest.json (+ LICENSE_CC0.txt is written by Fetch).
    /// Import settings (<see cref="EmberdelvePolyHavenPostprocessor"/>): textures retain the shipped 1k detail with
    /// Trilinear filtering, anisotropy 8 and mipmaps; models without rig / animation / cameras / lights / colliders.
    /// </summary>
    internal static class EmberdelvePolyHavenImport
    {
        public const string Root = "Assets/Emberdelve/Art/ThirdParty/PolyHaven";
        public const string ManifestPath = Root + "/manifest.json";
        public const string LicensePath = Root + "/LICENSE_CC0.txt";
        public const string Resolution = "1k";
        public const string LicenseUrl = "https://polyhaven.com/license";

        /// <summary>The photo-scanned albedos are very dark for a crypt lit only by torches; the base colour multiplies them up so props stay readable (still moody, not flat).</summary>
        public const float AlbedoLift = 1.8f;

        /// <summary>The models in use (id, role). Order is irrelevant.</summary>
        public static readonly string[][] Catalog =
        {
            new[] { "treasure_chest", "chest (replaces KayKit chest / chest_gold)" },
            new[] { "Barrel_01", "barrel scatter" },
            new[] { "wine_barrel_01", "barrel scatter" },
            new[] { "wooden_barrels_01", "barrel cluster scatter" },
            new[] { "wooden_crate_01", "crate scatter" },
            new[] { "wooden_crate_02", "crate scatter" },
            new[] { "wooden_bucket_01", "clutter scatter" },
            new[] { "Lantern_01", "wall lantern (replaces KayKit torch_mounted)" },
            new[] { "wooden_candlestick", "candle prop" },
            new[] { "brass_candleholders", "candle prop" },
            new[] { "WoodenTable_01", "furniture scatter" },
            new[] { "wooden_stool_01", "furniture scatter" },
            new[] { "wooden_bookshelf_worn", "library shelf" },
            new[] { "worn_metal_rack", "armory rack" },
            new[] { "GothicCabinet_01", "furniture scatter" },
            new[] { "rock_moss_set_01", "rubble / mossy rocks" },
            new[] { "root_cluster_01", "roots in corners" },
            // V3c: weapons (first person view model and enemy hands)
            new[] { "antique_estoc", "weapon: sword / rapier view model, enemy rusty blade" },
            new[] { "sledgehammer_01", "weapon: maul view model" },
            new[] { "ornate_medieval_mace", "weapon: mace (no WeaponData yet)" },
            new[] { "ornate_war_hammer", "weapon: war hammer (no WeaponData yet)" },
            new[] { "ornate_medieval_dagger", "weapon: dagger, enemy rogue" },
            new[] { "hatchet", "weapon: hatchet / axe, enemy warrior" },
            new[] { "kite_shield", "weapon: shield view model, enemy warrior" }
        };

        /// <summary>V3c: the weapon models (a subset of <see cref="Catalog"/>).</summary>
        public static readonly string[] WeaponIds = { "antique_estoc", "sledgehammer_01", "ornate_medieval_mace", "ornate_war_hammer", "ornate_medieval_dagger", "hatchet", "kite_shield" };

        public static string ModelPath(string id) { return Root + "/" + id + "/" + id + "_" + Resolution + ".fbx"; }
        public static GameObject LoadModel(string id) { return AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath(id)); }
        public static string MaterialsFolder(string id) { return Root + "/" + id + "/Materials"; }

        private static string _projectRoot;
        [InitializeOnLoadMethod] private static void CacheProjectRoot() { ProjectRoot(); }
        private static string ProjectRoot() { if (_projectRoot == null) _projectRoot = Path.GetDirectoryName(Application.dataPath).Replace('\\', '/'); return _projectRoot; }
        private static string Absolute(string assetPath) { return ProjectRoot() + "/" + assetPath; }

        // ---------------------------------------------------------------- manifest model

        [Serializable] internal sealed class ManifestFile { public string path; public string source; public string sha256; public long bytes; }

        [Serializable]
        internal sealed class ManifestAsset
        {
            public string id;
            public string name;
            public string page;
            public string authors;
            public string license = "CC0-1.0";
            public string license_url = LicenseUrl;
            public string resolution = Resolution;
            public string unity_path;
            public string usage;
            public List<ManifestFile> files = new List<ManifestFile>();
        }

        [Serializable]
        internal sealed class Manifest
        {
            public string schema = "emberdelve-polyhaven-manifest/1";
            public string note = "Fetched by EmberdelvePolyHavenImport.Fetch. FBX and source textures are byte for byte what Poly Haven serves (EXR maps replaced by their PNG twin); *_MaskMS.png and Materials/*.mat are generated.";
            public List<ManifestAsset> assets = new List<ManifestAsset>();
        }

        internal static Manifest ReadManifest()
        {
            string path = Absolute(ManifestPath);
            if (!File.Exists(path)) return null;
            try { return JsonUtility.FromJson<Manifest>(File.ReadAllText(path)); } catch (Exception) { return null; }
        }

        // ---------------------------------------------------------------- fetch

        [MenuItem("Emberdelve/Visual/Fetch Poly Haven Props")]
        private static void MenuFetch()
        {
            List<string> ids = new List<string>();
            for (int i = 0; i < Catalog.Length; i++) ids.Add(Catalog[i][0]);
            Debug.Log("[Emberdelve][PolyHaven] " + Fetch(ids.ToArray()));
        }

        /// <summary>Downloads the given ids (skips files already present). Returns a short report.</summary>
        public static string Fetch(string[] ids, bool refresh = true)
        {
            StringBuilder report = new StringBuilder();
            using (HttpClient http = new HttpClient())
            {
                http.Timeout = TimeSpan.FromSeconds(120);
                http.DefaultRequestHeaders.UserAgent.ParseAdd("EmberdelveAssetFetch/1.0");
                for (int i = 0; i < ids.Length; i++)
                {
                    try { report.AppendLine(FetchOne(http, ids[i])); }
                    catch (Exception e) { report.AppendLine(ids[i] + ": FAILED " + e.Message); }
                }
            }

            if (refresh) AssetDatabase.Refresh();
            return report.ToString();
        }

        private static string FetchOne(HttpClient http, string id)
        {
            string filesJson = Get(http, "https://api.polyhaven.com/files/" + id);
            object root = MiniJson.Parse(filesJson);
            Dictionary<string, object> entry = MiniJson.Path(root, "fbx", Resolution, "fbx") as Dictionary<string, object>;
            if (entry == null) return id + ": no fbx/" + Resolution + " entry";

            string infoJson = Get(http, "https://api.polyhaven.com/info/" + id);
            Dictionary<string, object> info = MiniJson.Parse(infoJson) as Dictionary<string, object>;
            string name = info != null && info.ContainsKey("name") ? Convert.ToString(info["name"], CultureInfo.InvariantCulture) : id;
            List<string> authors = new List<string>();
            Dictionary<string, object> authorMap = info != null && info.ContainsKey("authors") ? info["authors"] as Dictionary<string, object> : null;
            if (authorMap != null) foreach (KeyValuePair<string, object> pair in authorMap) authors.Add(pair.Key);

            ManifestAsset asset = new ManifestAsset();
            asset.id = id;
            asset.name = name;
            asset.page = "https://polyhaven.com/a/" + id;
            asset.authors = string.Join(", ", authors.ToArray());
            asset.unity_path = ModelPath(id);
            asset.usage = UsageOf(id);

            int count = 0;
            string fbxUrl = Convert.ToString(entry["url"], CultureInfo.InvariantCulture);
            count += Save(http, asset, fbxUrl, ModelPath(id));

            Dictionary<string, object> include = entry.ContainsKey("include") ? entry["include"] as Dictionary<string, object> : null;
            if (include != null)
            {
                foreach (KeyValuePair<string, object> pair in include)
                {
                    Dictionary<string, object> file = pair.Value as Dictionary<string, object>;
                    if (file == null) continue;
                    string url = Convert.ToString(file["url"], CultureInfo.InvariantCulture);
                    string relative = pair.Key; // e.g. textures/x_diff_1k.jpg
                    if (relative.EndsWith(".exr", StringComparison.OrdinalIgnoreCase))
                    {
                        // EXR -> PNG twin (Unity's normal-map / data-texture import wants 8 bit here)
                        string png = url.Replace("/exr/", "/png/").Replace(".exr", ".png");
                        string pngRelative = relative.Substring(0, relative.Length - 4) + ".png";
                        count += Save(http, asset, png, Root + "/" + id + "/" + pngRelative);
                    }
                    else
                    {
                        count += Save(http, asset, url, Root + "/" + id + "/" + relative);
                    }
                }
            }

            UpsertManifest(asset);
            return id + ": " + count + " files, authors=" + asset.authors;
        }

        private static int Save(HttpClient http, ManifestAsset asset, string url, string assetPath)
        {
            string destination = Absolute(assetPath);
            if (!File.Exists(destination) || new FileInfo(destination).Length == 0)
            {
                byte[] data = null;
                Exception last = null;
                for (int attempt = 0; attempt < 3 && data == null; attempt++)
                {
                    try { data = http.GetByteArrayAsync(url).Result; }
                    catch (Exception e) { last = e; }
                }

                if (data == null) throw new IOException("download failed: " + url + " (" + (last != null ? last.GetBaseException().Message : "?") + ")");
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                File.WriteAllBytes(destination, data);
            }

            ManifestFile file = new ManifestFile { path = assetPath, source = url, sha256 = Sha256(destination), bytes = new FileInfo(destination).Length };
            asset.files.RemoveAll(f => f.path == assetPath);
            asset.files.Add(file);
            return 1;
        }

        private static string Get(HttpClient http, string url)
        {
            Exception last = null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try { return http.GetStringAsync(url).Result; }
                catch (Exception e) { last = e; }
            }

            throw new IOException("GET failed: " + url + " (" + (last != null ? last.GetBaseException().Message : "?") + ")");
        }

        private static string UsageOf(string id)
        {
            for (int i = 0; i < Catalog.Length; i++) if (Catalog[i][0] == id) return Catalog[i][1];
            return "";
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

        private static void UpsertManifest(ManifestAsset asset)
        {
            Manifest manifest = ReadManifest() ?? new Manifest();
            manifest.assets.RemoveAll(a => a.id == asset.id);
            manifest.assets.Add(asset);
            manifest.assets.Sort((a, b) => string.CompareOrdinal(a.id, b.id));
            Directory.CreateDirectory(Absolute(Root));
            File.WriteAllText(Absolute(ManifestPath), JsonUtility.ToJson(manifest, true) + "\n", new UTF8Encoding(false));
            File.WriteAllText(Absolute(LicensePath),
                "Poly Haven assets in this folder are licensed CC0 1.0 Universal (public domain dedication).\n" +
                "https://creativecommons.org/publicdomain/zero/1.0/\n" +
                "Poly Haven license page: " + LicenseUrl + "\n" +
                "Authors and sources of every model: manifest.json (authors per asset, source URL per file).\n" +
                "No attribution is required; it is recorded anyway.\n", new UTF8Encoding(false));
        }

        // ---------------------------------------------------------------- materials

        /// <summary>Creates/updates the URP/Lit materials of one model and remaps its FBX to them. Returns the number of materials.</summary>
        public static int BuildMaterials(string id)
        {
            string fbx = ModelPath(id);
            if (AssetDatabase.LoadMainAssetAtPath(fbx) == null) return 0;

            EmberdelveVisualSetup.EnsureFolder(MaterialsFolder(id));
            List<string> names = new List<string>();
            UnityEngine.Object[] all = AssetDatabase.LoadAllAssetsAtPath(fbx);
            for (int i = 0; i < all.Length; i++)
                if (all[i] is Material && !names.Contains(all[i].name)) names.Add(all[i].name);

            string textureDir = Absolute(Root + "/" + id + "/textures");
            int built = 0;
            for (int n = 0; n < names.Count; n++)
            {
                string matName = names[n];
                if (matName.ToLowerInvariant().Contains("glass") && FindFile(textureDir, matName + "_diff_" + Resolution) == null)
                {
                    BuildGlass(id, matName);
                    built++;
                    continue;
                }

                string prefix = FindPrefix(textureDir, matName, id);
                if (prefix == null) { Debug.LogWarning("[Emberdelve][PolyHaven] " + id + ": no textures for material '" + matName + "'"); continue; }

                string folder = Root + "/" + id + "/textures/";
                string diffuse = FindFile(textureDir, prefix + "_diff_" + Resolution);
                string normal = FindFile(textureDir, prefix + "_nor_gl_" + Resolution);
                string rough = FindFile(textureDir, prefix + "_rough_" + Resolution) ?? FindFile(textureDir, prefix + "_roughness_" + Resolution);
                string metal = FindFile(textureDir, prefix + "_metal_" + Resolution) ?? FindFile(textureDir, prefix + "_metallic_" + Resolution);

                string maskAsset = null;
                if (rough != null)
                {
                    maskAsset = folder + prefix + "_MaskMS.png";
                    PackMask(Path.Combine(textureDir, rough), metal != null ? Path.Combine(textureDir, metal) : null, Absolute(maskAsset));
                    AssetDatabase.ImportAsset(maskAsset, ImportAssetOptions.ForceSynchronousImport);
                }

                if (diffuse != null) AssetDatabase.ImportAsset(folder + diffuse, ImportAssetOptions.ForceSynchronousImport);
                if (normal != null) AssetDatabase.ImportAsset(folder + normal, ImportAssetOptions.ForceSynchronousImport);

                string matPath = MaterialsFolder(id) + "/" + matName + ".mat";
                Material material = AssetDatabase.LoadAssetAtPath<Material>(matPath);
                bool created = material == null;
                if (created) material = new Material(Shader.Find("Universal Render Pipeline/Lit"));

                material.SetTexture("_BaseMap", diffuse != null ? AssetDatabase.LoadAssetAtPath<Texture2D>(folder + diffuse) : null);
                material.SetColor("_BaseColor", new Color(AlbedoLift, AlbedoLift, AlbedoLift, 1f));
                if (material.HasProperty("_Color")) material.SetColor("_Color", new Color(AlbedoLift, AlbedoLift, AlbedoLift, 1f));
                if (normal != null)
                {
                    material.SetTexture("_BumpMap", AssetDatabase.LoadAssetAtPath<Texture2D>(folder + normal));
                    material.SetFloat("_BumpScale", 0.8f);
                    material.EnableKeyword("_NORMALMAP");
                }

                if (maskAsset != null)
                {
                    material.SetTexture("_MetallicGlossMap", AssetDatabase.LoadAssetAtPath<Texture2D>(maskAsset));
                    material.SetFloat("_Metallic", 1f);
                    material.SetFloat("_Smoothness", 0.55f);
                    material.SetFloat("_SmoothnessTextureChannel", 0f);
                    material.EnableKeyword("_METALLICSPECGLOSSMAP");
                }
                else
                {
                    material.SetFloat("_Metallic", 0f);
                    material.SetFloat("_Smoothness", 0.2f);
                }

                if (matName.ToLowerInvariant().Contains("flame"))
                {
                    // painted candle flame: self lit (the lit candles are decoration, they cast no light)
                    material.SetColor("_EmissionColor", new Color(1.5f, 0.8f, 0.3f, 1f));
                    material.EnableKeyword("_EMISSION");
                    material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
                }

                if (created) AssetDatabase.CreateAsset(material, matPath); else EditorUtility.SetDirty(material);
                built++;
            }

            AssetDatabase.SaveAssets();
            EmberdelveWeaponModels.ApplyLook(id); // V3c: weapon models get their own base colour, not the prop lift

            ModelImporter importer = AssetImporter.GetAtPath(fbx) as ModelImporter;
            if (importer != null && EmberdelvePolyHavenPostprocessor.Apply(importer, fbx)) importer.SaveAndReimport();
            return built;
        }

        /// <summary>Lantern glass: a dark, faint, alpha blended pane (the flame inside must stay visible).</summary>
        private static void BuildGlass(string id, string matName)
        {
            string matPath = MaterialsFolder(id) + "/" + matName + ".mat";
            Material material = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            bool created = material == null;
            if (created) material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            material.SetFloat("_Surface", 1f);
            material.SetFloat("_Blend", 0f);
            material.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            material.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            material.SetFloat("_ZWrite", 0f);
            material.SetOverrideTag("RenderType", "Transparent");
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            material.SetColor("_BaseColor", new Color(0.35f, 0.25f, 0.12f, 0.18f));
            material.SetFloat("_Smoothness", 0.8f);
            material.SetFloat("_Metallic", 0f);
            if (created) AssetDatabase.CreateAsset(material, matPath); else EditorUtility.SetDirty(material);
        }

        private static string FindPrefix(string textureDir, string materialName, string id)
        {
            if (!Directory.Exists(textureDir)) return null;
            string[] candidates = { materialName, id + "_" + materialName, id };
            for (int i = 0; i < candidates.Length; i++)
                if (FindFile(textureDir, candidates[i] + "_diff_" + Resolution) != null) return candidates[i];

            string[] files = Array.FindAll(Directory.GetFiles(textureDir, "*_diff_" + Resolution + ".*"), f => !f.EndsWith(".meta", StringComparison.Ordinal));
            if (files.Length == 1)
            {
                string file = Path.GetFileName(files[0]);
                return file.Substring(0, file.IndexOf("_diff_" + Resolution, StringComparison.Ordinal));
            }

            // Several texture sets (kite_shield: body + clamps_trim for the materials clamps_and_bolts / metal_trim): the set whose name shares
            // the most words with the material, when one set is strictly better than the others.
            string[] words = materialName.ToLowerInvariant().Split('_');
            string best = null;
            int bestScore = 0;
            bool tie = false;
            for (int f = 0; f < files.Length; f++)
            {
                string file = Path.GetFileName(files[f]);
                string setName = file.Substring(0, file.IndexOf("_diff_" + Resolution, StringComparison.Ordinal));
                string[] setWords = setName.ToLowerInvariant().Split('_');
                int score = 0;
                for (int w = 0; w < words.Length; w++) if (words[w].Length > 0 && Array.IndexOf(setWords, words[w]) >= 0) score++;
                if (score > bestScore) { best = setName; bestScore = score; tie = false; }
                else if (score == bestScore && score > 0) tie = true;
            }

            if (best != null && !tie) return best;
            return null;
        }

        private static string FindFile(string dir, string stem)
        {
            string[] ext = { ".png", ".jpg" };
            for (int i = 0; i < ext.Length; i++) if (File.Exists(Path.Combine(dir, stem + ext[i]))) return stem + ext[i];
            return null;
        }

        /// <summary>Packs roughness (and optional metal) into the project mask convention: R = metallic * 0.5, A = smoothness.</summary>
        private static void PackMask(string roughPath, string metalPath, string outputPath)
        {
            Texture2D rough = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            rough.LoadImage(File.ReadAllBytes(roughPath));
            Texture2D metal = null;
            if (metalPath != null)
            {
                metal = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
                metal.LoadImage(File.ReadAllBytes(metalPath));
            }

            int w = rough.width, h = rough.height;
            Color32[] r = rough.GetPixels32();
            Color32[] m = metal != null && metal.width == w && metal.height == h ? metal.GetPixels32() : null;
            Color32[] outPixels = new Color32[r.Length];
            for (int i = 0; i < r.Length; i++)
            {
                byte smooth = (byte)(255 - r[i].r);
                byte metallic = m != null ? (byte)(m[i].r / 2) : (byte)0;
                outPixels[i] = new Color32(metallic, 0, 0, smooth);
            }

            Texture2D result = new Texture2D(w, h, TextureFormat.RGBA32, false, true);
            result.SetPixels32(outPixels);
            File.WriteAllBytes(outputPath, result.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(rough);
            if (metal != null) UnityEngine.Object.DestroyImmediate(metal);
            UnityEngine.Object.DestroyImmediate(result);
        }

        /// <summary>Adds the generated mask maps to the manifest (idempotent).</summary>
        public static void RegisterGeneratedFiles()
        {
            Manifest manifest = ReadManifest();
            if (manifest == null) return;
            for (int a = 0; a < manifest.assets.Count; a++)
            {
                ManifestAsset asset = manifest.assets[a];
                string dir = Absolute(Root + "/" + asset.id + "/textures");
                if (!Directory.Exists(dir)) continue;
                string[] masks = Directory.GetFiles(dir, "*_MaskMS.png");
                for (int i = 0; i < masks.Length; i++)
                {
                    string assetPath = Root + "/" + asset.id + "/textures/" + Path.GetFileName(masks[i]);
                    asset.files.RemoveAll(f => f.path == assetPath);
                    asset.files.Add(new ManifestFile { path = assetPath, source = "generated by EmberdelvePolyHavenImport.PackMask from the roughness / metal maps", sha256 = Sha256(masks[i]), bytes = new FileInfo(masks[i]).Length });
                }
            }

            File.WriteAllText(Absolute(ManifestPath), JsonUtility.ToJson(manifest, true) + "\n", new UTF8Encoding(false));
            AssetDatabase.ImportAsset(ManifestPath);
        }

        // ---------------------------------------------------------------- tiny JSON reader (dictionaries / lists / strings / numbers)

        private static class MiniJson
        {
            public static object Parse(string text)
            {
                int index = 0;
                return Value(text, ref index);
            }

            public static object Path(object root, params string[] keys)
            {
                object current = root;
                for (int i = 0; i < keys.Length; i++)
                {
                    Dictionary<string, object> map = current as Dictionary<string, object>;
                    if (map == null || !map.TryGetValue(keys[i], out current)) return null;
                }

                return current;
            }

            private static void Skip(string s, ref int i) { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }

            private static object Value(string s, ref int i)
            {
                Skip(s, ref i);
                char c = s[i];
                if (c == '{')
                {
                    Dictionary<string, object> map = new Dictionary<string, object>();
                    i++;
                    Skip(s, ref i);
                    if (s[i] == '}') { i++; return map; }
                    while (true)
                    {
                        Skip(s, ref i);
                        string key = Str(s, ref i);
                        Skip(s, ref i);
                        i++; // :
                        map[key] = Value(s, ref i);
                        Skip(s, ref i);
                        if (s[i] == ',') { i++; continue; }
                        i++; // }
                        return map;
                    }
                }

                if (c == '[')
                {
                    List<object> list = new List<object>();
                    i++;
                    Skip(s, ref i);
                    if (s[i] == ']') { i++; return list; }
                    while (true)
                    {
                        list.Add(Value(s, ref i));
                        Skip(s, ref i);
                        if (s[i] == ',') { i++; continue; }
                        i++; // ]
                        return list;
                    }
                }

                if (c == '"') return Str(s, ref i);

                int start = i;
                while (i < s.Length && ",}]".IndexOf(s[i]) < 0) i++;
                string token = s.Substring(start, i - start).Trim();
                if (token == "true") return true;
                if (token == "false") return false;
                if (token == "null") return null;
                double number;
                return double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out number) ? (object)number : token;
            }

            private static string Str(string s, ref int i)
            {
                StringBuilder sb = new StringBuilder();
                i++; // opening quote
                while (s[i] != '"')
                {
                    if (s[i] == '\\')
                    {
                        i++;
                        char e = s[i];
                        if (e == 'n') sb.Append('\n');
                        else if (e == 't') sb.Append('\t');
                        else if (e == 'u') { sb.Append((char)Convert.ToInt32(s.Substring(i + 1, 4), 16)); i += 4; }
                        else sb.Append(e);
                    }
                    else sb.Append(s[i]);
                    i++;
                }

                i++;
                return sb.ToString();
            }
        }
    }

    /// <summary>V3a: import settings for everything under Art/ThirdParty/PolyHaven (models and textures); runs on every (re)import.</summary>
    public sealed class EmberdelvePolyHavenPostprocessor : AssetPostprocessor
    {
        public const string Root = "Assets/Emberdelve/Art/ThirdParty/PolyHaven/";
        public const int MaxTextureSize = 1024;

        public override uint GetVersion() { return 3; }

        /// <summary>V3c: the weapon models stay CPU readable (the editor measures their length axis and head end once to build the normalised weapon prefabs).</summary>
        public static bool KeepReadable(string path)
        {
            string file = System.IO.Path.GetFileName(path);
            for (int i = 0; i < EmberdelvePolyHavenImport.WeaponIds.Length; i++)
                if (file.StartsWith(EmberdelvePolyHavenImport.WeaponIds[i] + "_", System.StringComparison.Ordinal)) return true;
            return false;
        }

        private void OnPreprocessModel()
        {
            ModelImporter importer = assetImporter as ModelImporter;
            if (importer != null) Apply(importer, assetPath);
        }

        private void OnPreprocessTexture()
        {
            TextureImporter importer = assetImporter as TextureImporter;
            if (importer != null) Apply(importer, assetPath);
        }

        public static bool Apply(ModelImporter importer, string path)
        {
            if (importer == null || string.IsNullOrEmpty(path) || !path.StartsWith(Root)) return false;
            bool changed = false;
            if (importer.importCameras) { importer.importCameras = false; changed = true; }
            if (importer.importLights) { importer.importLights = false; changed = true; }
            if (importer.importBlendShapes) { importer.importBlendShapes = false; changed = true; }
            if (importer.addCollider) { importer.addCollider = false; changed = true; }
            if (importer.isReadable != KeepReadable(path)) { importer.isReadable = KeepReadable(path); changed = true; }
            if (importer.animationType != ModelImporterAnimationType.None) { importer.animationType = ModelImporterAnimationType.None; changed = true; }
            if (importer.importAnimation) { importer.importAnimation = false; changed = true; }
            if (importer.meshCompression != ModelImporterMeshCompression.Off) { importer.meshCompression = ModelImporterMeshCompression.Off; changed = true; }

            // Remap each FBX material to the project material of the same name (Materials/<name>.mat next to the model).
            string materials = System.IO.Path.GetDirectoryName(path).Replace('\\', '/') + "/Materials";
            if (AssetDatabase.IsValidFolder(materials))
            {
                string[] guids = AssetDatabase.FindAssets("t:Material", new[] { materials });
                for (int i = 0; i < guids.Length; i++)
                {
                    string matPath = AssetDatabase.GUIDToAssetPath(guids[i]);
                    Material material = AssetDatabase.LoadAssetAtPath<Material>(matPath);
                    if (material == null) continue;
                    AssetImporter.SourceAssetIdentifier id = new AssetImporter.SourceAssetIdentifier(typeof(Material), material.name);
                    UnityEngine.Object current;
                    if (importer.GetExternalObjectMap().TryGetValue(id, out current) && current == material) continue;
                    importer.AddRemap(id, material);
                    changed = true;
                }
            }

            return changed;
        }

        public static bool Apply(TextureImporter importer, string path)
        {
            if (importer == null || string.IsNullOrEmpty(path) || !path.StartsWith(Root)) return false;
            string file = System.IO.Path.GetFileNameWithoutExtension(path);
            bool normal = file.Contains("_nor_gl_") || file.Contains("_nor_dx_");
            bool data = normal || file.EndsWith("_MaskMS") || file.Contains("_rough_") || file.Contains("_metal_") || file.Contains("_ao_") || file.Contains("_arm_") || file.Contains("_opacity_") || file.Contains("_roughness_") || file.Contains("_metallic_");

            bool changed = false;
            TextureImporterType type = normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
            if (importer.textureType != type) { importer.textureType = type; changed = true; }
            if (!normal && importer.sRGBTexture == data) { importer.sRGBTexture = !data; changed = true; }
            if (file.EndsWith("_MaskMS"))
            {
                if (importer.alphaSource != TextureImporterAlphaSource.FromInput) { importer.alphaSource = TextureImporterAlphaSource.FromInput; changed = true; }
                if (importer.alphaIsTransparency) { importer.alphaIsTransparency = false; changed = true; }
            }

            if (importer.wrapMode != TextureWrapMode.Repeat) { importer.wrapMode = TextureWrapMode.Repeat; changed = true; }
            if (importer.filterMode != FilterMode.Trilinear) { importer.filterMode = FilterMode.Trilinear; changed = true; }
            if (importer.anisoLevel != 8) { importer.anisoLevel = 8; changed = true; }
            if (!importer.mipmapEnabled) { importer.mipmapEnabled = true; changed = true; }
            if (importer.maxTextureSize != MaxTextureSize) { importer.maxTextureSize = MaxTextureSize; changed = true; }
            if (importer.isReadable) { importer.isReadable = false; changed = true; }
            return changed;
        }
    }
}
