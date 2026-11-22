using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using Emberdelve.AI;
using Emberdelve.Combat;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Feedback;
using Emberdelve.Interaction;
using Emberdelve.Level;
using Emberdelve.Player;

namespace Emberdelve.EditorTools
{
    /// <summary>
    /// V2 acceptance gate for the "retro dark fantasy" slice.
    ///
    ///   Unity.exe -batchmode -nographics -projectPath . -logFile Logs/retro_visual_gate.log
    ///             -executeMethod Emberdelve.EditorTools.EmberdelveRetroVisualGate.Run
    ///
    /// (Do NOT pass -quit: the gate exits the Editor itself, through EmberdelveGateExit.) It re-runs the KayKit import, rebuilds
    /// the lobby and the dungeon and asserts: vendor files + manifest hashes + licenses; model import settings; every enemy has
    /// a Visual with an EnemyVisualDriver and an Animator whose controller has the six states, while its colliders match a
    /// reference computed from the enemy data; chests keep their interactables and get a model; torches have a mesh, a light and
    /// particles; props have no colliders and only stand in eligible wall cells; the retro renderer feature is present and
    /// active on the active PC renderer (and the toggle works); texture importers are Point / &lt;= 512.
    /// Final line: [Emberdelve][RetroVisual] PASS|FAIL checks=N failures=M
    /// </summary>
    public static class EmberdelveRetroVisualGate
    {
        private const string Tag = "[Emberdelve][RetroVisual]";
        private static readonly string[] RequiredStates = { "Idle", "Walk", "Run", "Attack", "Hit", "Death" };

        private static int _checks;
        private static int _failures;

        public static void Run()
        {
            _checks = 0;
            _failures = 0;

            try { Execute(); }
            catch (System.Exception e) { Check(false, "gate threw " + e); }

            string verdict = _failures == 0 ? "PASS" : "FAIL";
            string line = Tag + " " + verdict + " checks=" + _checks + " failures=" + _failures;
            if (_failures == 0) Debug.Log(line); else Debug.LogError(line);

            EmberdelveGateExit.Exit(_failures == 0 ? 0 : 1);
        }

        private static void Execute()
        {
            EmberdelvePaths.EnsureFolders();
            EmberdelveProjectSetup.EnsureLayers();
            AssetDatabase.Refresh();

            bool imported = EmberdelveVendorImport.EnsureImported(true);
            Check(imported, "KayKit import from Logs/v2_assets succeeded");
            CheckVendorFiles();
            CheckImportSettings();
            CheckPolyHaven();
            CheckQuaternius();
            CheckViewModels();

            EmberdelveAssetLibrary.BuildAll();
            EmberdelveEnemyVisualFactory.EnsureAll(true);
            EmberdelveLobbyBuilder.Build();
            Scene scene = EmberdelveLevelBuilder.Build();
            AssetDatabase.SaveAssets();

            CheckRetroFeature();
            CheckTextures();
            CheckVolume();
            CheckTorches(scene);
            CheckEnemies(scene);
            CheckChests();
            CheckProps(scene);
        }

        // ---------------------------------------------------------------- vendor import

        private static void CheckVendorFiles()
        {
            List<string> paths = EmberdelveVendorImport.AllDestinationPaths();
            int missing = 0;
            for (int i = 0; i < paths.Count; i++) if (!File.Exists(paths[i])) { missing++; Debug.LogError(Tag + " missing " + paths[i]); }
            Check(missing == 0, "all " + paths.Count + " KayKit files are in the project (" + missing + " missing)");

            EmberdelveVendorImport.Manifest manifest = EmberdelveVendorImport.ReadManifest();
            Check(manifest != null && manifest.packs != null && manifest.packs.Count == 2, "KayKit/manifest.json parses with two packs");
            if (manifest == null || manifest.packs == null) return;

            HashSet<string> listed = new HashSet<string>();
            int badHash = 0;
            for (int p = 0; p < manifest.packs.Count; p++)
            {
                EmberdelveVendorImport.ManifestPack pack = manifest.packs[p];
                Check(pack.license == "CC0-1.0", "pack '" + pack.name + "' license CC0-1.0");
                Check(!string.IsNullOrEmpty(pack.creator) && pack.creator.Contains("Kay Lousberg"), "pack '" + pack.name + "' creator Kay Lousberg");
                Check(pack.source_repo == EmberdelveVendorImport.SkeletonsRepo || pack.source_repo == EmberdelveVendorImport.DungeonRepo, "pack '" + pack.name + "' source repository " + pack.source_repo);
                Check(!string.IsNullOrEmpty(pack.license_file) && File.Exists(pack.license_file), "pack '" + pack.name + "' license file exists: " + pack.license_file);
                if (!string.IsNullOrEmpty(pack.license_file) && File.Exists(pack.license_file))
                {
                    string text = File.ReadAllText(pack.license_file);
                    Check(text.Contains("CC0") || text.Contains("Creative Commons Zero"), "pack '" + pack.name + "' license text states CC0");
                }

                for (int f = 0; f < pack.files.Count; f++)
                {
                    EmberdelveVendorImport.ManifestFile file = pack.files[f];
                    listed.Add(file.path);
                    if (!File.Exists(file.path) || EmberdelveVendorImport.Sha256(file.path) != file.sha256 || new FileInfo(file.path).Length != file.bytes)
                    {
                        badHash++;
                        Debug.LogError(Tag + " manifest hash/size mismatch: " + file.path);
                    }
                }
            }

            Check(badHash == 0, "every manifest sha256 / size matches the file in the project (" + badHash + " bad)");

            int unlisted = 0;
            for (int i = 0; i < paths.Count; i++) if (!listed.Contains(paths[i])) unlisted++;
            Check(unlisted == 0, "every imported file is listed in the manifest (" + unlisted + " unlisted)");
        }

        private static void CheckImportSettings()
        {
            for (int i = 0; i < EmberdelveVendorImport.SkeletonCharacters.Length; i++)
            {
                string path = EmberdelveVendorImport.CharacterPath(EmberdelveVendorImport.SkeletonCharacters[i]);
                ModelImporter importer = AssetImporter.GetAtPath(path) as ModelImporter;
                Check(importer != null, "model imported: " + path);
                if (importer == null) continue;

                Check(importer.animationType == ModelImporterAnimationType.Generic, path + " Generic rig");
                Check(importer.importAnimation, path + " imports animation");
                Check(!importer.optimizeGameObjects, path + " keeps its bones (hand slot lookup)");

                int loopsOk = 0;
                int loopsNeeded = 0;
                ModelImporterClipAnimation[] clips = importer.clipAnimations;
                for (int c = 0; c < clips.Length; c++)
                {
                    if (clips[c].name == "Idle" || clips[c].name == "Walking_A" || clips[c].name == "Running_A")
                    {
                        loopsNeeded++;
                        if (clips[c].loopTime) loopsOk++;
                    }
                }

                Check(loopsNeeded == 3 && loopsOk == 3, path + " Idle / Walking_A / Running_A loop (" + loopsOk + " of " + loopsNeeded + ")");
            }

            string dungeon = EmberdelveVendorImport.DungeonModelPath("barrel_large");
            ModelImporter prop = AssetImporter.GetAtPath(dungeon) as ModelImporter;
            Check(prop != null && prop.animationType == ModelImporterAnimationType.None && !prop.importAnimation, dungeon + " imported without a rig or animation");

            Material skeleton = AssetDatabase.LoadAssetAtPath<Material>(EmberdelveVendorImport.SkeletonMaterialPath);
            Check(skeleton != null && skeleton.GetTexture("_BaseMap") != null, "KayKit skeleton material uses the atlas");
            Material dungeonMaterial = AssetDatabase.LoadAssetAtPath<Material>(EmberdelveVendorImport.DungeonMaterialPath);
            Check(dungeonMaterial != null && dungeonMaterial.GetTexture("_BaseMap") != null, "KayKit dungeon material uses the atlas");

            GameObject minion = EmberdelveVendorImport.LoadCharacter("Skeleton_Minion");
            bool remapped = false;
            if (minion != null)
            {
                Renderer[] renderers = minion.GetComponentsInChildren<Renderer>(true);
                for (int i = 0; i < renderers.Length; i++)
                {
                    Material[] materials = renderers[i].sharedMaterials;
                    for (int m = 0; m < materials.Length; m++) if (materials[m] == skeleton) remapped = true;
                }
            }

            Check(remapped, "the Minion's FBX material is remapped to the project KayKit material");
        }

        // ---------------------------------------------------------------- Poly Haven (V3a)

        private static void CheckPolyHaven()
        {
            EmberdelvePolyHavenImport.Manifest manifest = EmberdelvePolyHavenImport.ReadManifest();
            Check(manifest != null && manifest.assets != null && manifest.assets.Count == EmberdelvePolyHavenImport.Catalog.Length,
                "PolyHaven/manifest.json parses and lists every catalog model (" + (manifest != null ? manifest.assets.Count : 0) + " of " + EmberdelvePolyHavenImport.Catalog.Length + ")");
            Check(File.Exists(EmberdelvePolyHavenImport.LicensePath) && File.ReadAllText(EmberdelvePolyHavenImport.LicensePath).Contains("CC0"), "PolyHaven/LICENSE_CC0.txt exists and states CC0");
            if (manifest == null || manifest.assets == null) return;

            int badLicense = 0, badHash = 0, notImported = 0, badMaterial = 0;
            for (int a = 0; a < manifest.assets.Count; a++)
            {
                EmberdelvePolyHavenImport.ManifestAsset asset = manifest.assets[a];
                if (asset.license != "CC0-1.0" || string.IsNullOrEmpty(asset.authors) || asset.page == null || !asset.page.StartsWith("https://polyhaven.com/a/") || asset.license_url != EmberdelvePolyHavenImport.LicenseUrl) badLicense++;
                for (int f = 0; f < asset.files.Count; f++)
                {
                    EmberdelvePolyHavenImport.ManifestFile file = asset.files[f];
                    if (!File.Exists(file.path) || EmberdelvePolyHavenImport.Sha256(file.path) != file.sha256 || new FileInfo(file.path).Length != file.bytes) { badHash++; Debug.LogError(Tag + " manifest hash/size mismatch: " + file.path); }
                }

                ModelImporter importer = AssetImporter.GetAtPath(asset.unity_path) as ModelImporter;
                GameObject model = AssetDatabase.LoadAssetAtPath<GameObject>(asset.unity_path);
                if (importer == null || model == null || importer.animationType != ModelImporterAnimationType.None || importer.importAnimation) { notImported++; continue; }

                // every renderer uses a project material from the model's own Materials folder (the FBX remap worked, nothing is pink or default)
                Renderer[] renderers = model.GetComponentsInChildren<Renderer>(true);
                for (int r = 0; r < renderers.Length; r++)
                {
                    Material[] materials = renderers[r].sharedMaterials;
                    for (int m = 0; m < materials.Length; m++)
                    {
                        string path = materials[m] != null ? AssetDatabase.GetAssetPath(materials[m]) : "";
                        if (!path.StartsWith(EmberdelvePolyHavenImport.MaterialsFolder(asset.id) + "/") || materials[m].shader == null || materials[m].shader.name != "Universal Render Pipeline/Lit") badMaterial++;
                    }
                }
            }

            Check(badLicense == 0, "every Poly Haven asset is CC0-1.0 with authors, page and license url (" + badLicense + " bad)");
            Check(badHash == 0, "every Poly Haven manifest sha256 / size matches the file in the project (" + badHash + " bad)");
            Check(notImported == 0, "every Poly Haven model is imported without rig / animation (" + notImported + " bad)");
            Check(badMaterial == 0, "every Poly Haven renderer uses a project URP/Lit material of its own model (" + badMaterial + " bad slots)");
        }

        // ---------------------------------------------------------------- weapon view models and first-person arms (V3c)

        private static void CheckViewModels()
        {
            string built = EmberdelveWeaponModels.EnsureAll(false);
            EmberdelveFirstPersonArms.EnsureAll(false);
            WeaponViewModelSet set = WeaponViewModelSet.Load();
            Check(set != null && set.armsPrefab != null, "WeaponViewModelSet exists with the arms prefab (" + built + ")");
            if (set == null) return;

            // Every WeaponData either has a model or is explicitly unmapped (with a reason).
            string[] guids = AssetDatabase.FindAssets("t:WeaponData");
            int unresolved = 0;
            for (int i = 0; i < guids.Length; i++)
            {
                WeaponData weapon = AssetDatabase.LoadAssetAtPath<WeaponData>(AssetDatabase.GUIDToAssetPath(guids[i]));
                if (weapon == null) continue;
                bool mapped = set.Find(weapon.weaponId) != null;
                bool unmapped = set.IsUnmapped(weapon.weaponId);
                if (mapped == unmapped) { unresolved++; Debug.LogError(Tag + " weapon " + weapon.weaponId + " is " + (mapped ? "both mapped and unmapped" : "neither mapped nor unmapped")); }
            }

            Check(guids.Length > 0 && unresolved == 0, "every WeaponData ("+ guids.Length + ") maps to a model xor is explicitly unmapped (" + unresolved + " unresolved)");

            // Mesh models stay URP Lit. The held torch additionally owns the existing authored
            // transparent particle flame; it must not turn arbitrary unlit meshes into valid models.
            Material heldFlame = AssetDatabase.LoadAssetAtPath<Material>(EmberdelvePaths.Materials + "/Torch_Flame.mat");
            int bad = 0;
            for (int i = 0; i < set.entries.Length; i++)
            {
                GameObject prefab = set.entries[i] != null ? set.entries[i].prefab : null;
                bool ok = prefab != null && prefab.GetComponentsInChildren<Collider>(true).Length == 0 && set.entries[i].length > 0.1f;
                Renderer[] renderers = prefab != null ? prefab.GetComponentsInChildren<Renderer>(true) : new Renderer[0];
                var torch = prefab != null && set.entries[i].weaponId == HandheldTorchVisual.WeaponId
                    && AssetDatabase.GetAssetPath(prefab) == EmberdelveTorchContent.PrefabPath
                    ? prefab.GetComponent<HandheldTorchVisual>() : null;
                ok &= renderers.Length > 0;
                for (int r = 0; r < renderers.Length; r++)
                {
                    Material[] materials = renderers[r].sharedMaterials;
                    for (int m = 0; m < materials.Length; m++)
                    {
                        Material material = materials[m];
                        if (renderers[r] is ParticleSystemRenderer && torch != null)
                        {
                            ok &= torch.flame != null && renderers[r].transform.IsChildOf(torch.flame)
                                && (renderers[r].name == EmberdelveTorchFactory.FlameName || renderers[r].name == EmberdelveTorchFactory.EmbersName)
                                && renderers[r].GetComponent<ParticleSystem>() != null
                                && material != null && material == heldFlame && material.shader != null
                                && material.shader.name == "Universal Render Pipeline/Particles/Unlit"
                                && material.HasProperty("_Surface") && material.GetFloat("_Surface") == 1f
                                && material.HasProperty("_ZWrite") && material.GetFloat("_ZWrite") == 0f
                                && material.HasProperty("_BaseMap") && material.GetTexture("_BaseMap") != null;
                        }
                        else ok &= material != null && material.shader != null && material.shader.name == "Universal Render Pipeline/Lit";
                    }
                }

                if (!ok) { bad++; Debug.LogError(Tag + " weapon model of " + (set.entries[i] != null ? set.entries[i].weaponId : "null") + " has invalid mesh/flame material or colliders"); }
            }

            Check(set.entries.Length > 0 && bad == 0, "every weapon model ("+ set.entries.Length + ") has URP Lit mesh materials, only the authored held-torch particle exception, and no colliders (" + bad + " bad)");

            // Enemy prefabs no longer depend on a KayKit weapon mesh.
            int kay = 0;
            for (int v = 0; v < EmberdelveEnemyVisualFactory.Variants.Length; v++)
            {
                string path = EmberdelveEnemyVisualFactory.PrefabPath(EmberdelveEnemyVisualFactory.Variants[v].Name);
                if (AssetDatabase.LoadAssetAtPath<GameObject>(path) == null) continue;
                string[] dependencies = AssetDatabase.GetDependencies(path, true);
                for (int d = 0; d < dependencies.Length; d++) if (dependencies[d].Contains("/KayKit/") && dependencies[d].Contains("Skeleton_")) kay++;
            }

            Check(kay == 0, "no enemy prefab depends on a KayKit Skeleton_* mesh (" + kay + ")");
        }

        // ---------------------------------------------------------------- Quaternius (V3b)

        private static void CheckQuaternius()
        {
            EmberdelveQuaterniusImport.Manifest manifest = EmberdelveQuaterniusImport.ReadManifest();
            Check(manifest != null && manifest.files != null && manifest.files.Count == EmberdelveQuaterniusImport.Files.Length && manifest.license == "CC0-1.0",
                "Quaternius/manifest.json parses, CC0-1.0, lists every file");
            Check(File.Exists(EmberdelveQuaterniusImport.Absolute(EmberdelveQuaterniusImport.LicensePath)), "Quaternius/LICENSE_CC0.txt exists");

            int bad = 0;
            for (int i = 0; i < EmberdelveQuaterniusImport.Files.Length; i++)
            {
                string[] file = EmberdelveQuaterniusImport.Files[i];
                string absolute = EmberdelveQuaterniusImport.Absolute(file[0]);
                ModelImporter importer = AssetImporter.GetAtPath(file[0]) as ModelImporter;
                bool ok = File.Exists(absolute) && EmberdelveQuaterniusImport.Sha256(absolute) == file[2];
                ok &= importer != null && importer.animationType == ModelImporterAnimationType.Human && importer.importAnimation == (file[3] == "1");
                if (!ok) { bad++; Debug.LogError(Tag + " Quaternius file / hash / Humanoid import: " + file[0]); }
            }

            Check(bad == 0, "every Quaternius file matches its sha256 and is imported Humanoid (" + bad + " bad)");
        }

        // ---------------------------------------------------------------- textures

        private static void CheckTextures()
        {
            string[] roots = { EmberdelveTextureImport.TextureRoot.TrimEnd('/'), EmberdelveTextureImport.KayKitRoot.TrimEnd('/'), EmberdelvePolyHavenPostprocessor.Root.TrimEnd('/') };
            for (int r = 0; r < roots.Length; r++)
            {
                string[] guids = AssetDatabase.FindAssets("t:Texture2D", new[] { roots[r] });
                Check(guids.Length > 0, roots[r] + " holds textures (" + guids.Length + ")");
                int bad = 0;
                for (int i = 0; i < guids.Length; i++)
                {
                    string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                    TextureImporter importer = AssetImporter.GetAtPath(path) as TextureImporter;
                    bool palette = path.StartsWith(EmberdelveTextureImport.KayKitRoot);
                    int maxSize = palette ? 256 : path.StartsWith(EmberdelveTextureImport.TextureRoot) ? 2048 : 1024;
                    FilterMode filter = palette ? FilterMode.Point : FilterMode.Trilinear;
                    bool correct = importer != null && importer.filterMode == filter && importer.maxTextureSize == maxSize;
                    if (!palette) correct = correct && importer.mipmapEnabled && importer.anisoLevel == 8;
                    if (!correct)
                    {
                        bad++;
                        Debug.LogError(Tag + " texture import does not match its material family: " + path +
                            " expected=" + filter + "/" + maxSize + " actual=" +
                            (importer != null ? importer.filterMode.ToString() : "?") + "/" +
                            (importer != null ? importer.maxTextureSize : -1));
                    }
                }

                Check(bad == 0, roots[r] + ": palette or source-detail settings match every texture (" + bad + " bad)");
            }

            TextureImporter atlas = AssetImporter.GetAtPath(EmberdelveVendorImport.SkeletonTexturePath) as TextureImporter;
            Check(atlas != null && atlas.maxTextureSize == 256 && !atlas.mipmapEnabled && atlas.textureCompression == TextureImporterCompression.Uncompressed,
                "KayKit atlas: 256 px, no mipmaps, uncompressed");

            // Filtering changes presentation, never the normal-map encoding.
            TextureImporter normal = AssetImporter.GetAtPath(EmberdelveVisualSetup.TexturePath("Bricks100", "Normal", "png")) as TextureImporter;
            Check(normal != null && normal.textureType == TextureImporterType.NormalMap, "Bricks100 normal map is still a NormalMap");
        }

        // ---------------------------------------------------------------- retro pass

        private static void CheckRetroFeature()
        {
            Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(EmberdelveRetroSetup.ShaderPath);
            Check(shader != null && shader.name == EmberdelveRetroSetup.ShaderName, "retro shader loads as " + EmberdelveRetroSetup.ShaderName);
            Check(shader != null && !ShaderUtil.ShaderHasError(shader), "retro shader compiles without errors");

            Material material = AssetDatabase.LoadAssetAtPath<Material>(EmberdelveRetroSetup.MaterialPath);
            Check(material != null && material.shader == shader, "retro material uses the retro shader");
            if (material != null)
            {
                Check(material.GetFloat("_ColorLevels") >= 2f && material.GetFloat("_ColorLevels") <= 64f, "retro colour levels " + material.GetFloat("_ColorLevels") + " are a low bit depth");
                Check(material.GetFloat("_PixelSize") >= 1f, "retro pixel size " + material.GetFloat("_PixelSize"));
                Check(Mathf.Approximately(material.GetFloat("_Strength"), 0f), "reference-readability preset leaves retro quantisation/pixelation off");
            }

            RenderPipelineAsset active = QualitySettings.renderPipeline != null ? QualitySettings.renderPipeline : GraphicsSettings.defaultRenderPipeline;
            Check(active != null, "an active render pipeline asset exists");
            if (active != null)
            {
                string activePath = AssetDatabase.GetAssetPath(active);
                Check(System.Array.IndexOf(EmberdelveVisualSetup.PcPipelineAssets, activePath) >= 0, "the active pipeline asset is a PC asset (" + activePath + ")");
                CheckFeatureOn(EmberdelveRetroSetup.RenderersOf(active), activePath, material);
            }

            string[] pcPaths = EmberdelveVisualSetup.PcPipelineAssets;
            for (int i = 0; i < pcPaths.Length; i++)
            {
                RenderPipelineAsset asset = AssetDatabase.LoadAssetAtPath<RenderPipelineAsset>(pcPaths[i]);
                if (asset == null) continue;
                CheckFeatureOn(EmberdelveRetroSetup.RenderersOf(asset), pcPaths[i], material);
            }

            // The comparison toggle really switches the feature, and is restored.
            List<ScriptableRendererData> pc = EmberdelveRetroSetup.PcRenderers();
            if (pc.Count > 0)
            {
                EmberdelveRetroSetup.SetEnabled(false);
                FullScreenPassRendererFeature off = EmberdelveRetroSetup.FindFeature(pc[0]);
                Check(off != null && !off.isActive, "toggle: Retro Pass OFF deactivates the feature");
                EmberdelveRetroSetup.SetEnabled(true);
                FullScreenPassRendererFeature on = EmberdelveRetroSetup.FindFeature(pc[0]);
                Check(on != null && on.isActive, "toggle: Retro Pass ON reactivates the feature");
            }
        }

        private static void CheckFeatureOn(List<ScriptableRendererData> renderers, string owner, Material material)
        {
            Check(renderers.Count > 0, owner + " lists at least one renderer");
            for (int i = 0; i < renderers.Count; i++)
            {
                FullScreenPassRendererFeature feature = EmberdelveRetroSetup.FindFeature(renderers[i]);
                bool ok = feature != null && feature.isActive && feature.passMaterial == material && feature.fetchColorBuffer
                          && feature.injectionPoint == FullScreenPassRendererFeature.InjectionPoint.AfterRenderingPostProcessing;
                Check(ok, owner + " renderer '" + renderers[i].name + "' has the retro feature, active, with the retro material, after post-processing");
            }
        }

        private static void CheckVolume()
        {
            VolumeProfile profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(EmberdelveVisualSetup.DungeonVolumePath);
            Check(profile != null, "dungeon volume profile exists");
            if (profile == null) return;

            Bloom bloom;
            Vignette vignette;
            FilmGrain grain;
            Check(profile.TryGet(out bloom) && bloom.intensity.value > 0f && bloom.intensity.value <= 0.4f, "bloom is active but lower than V1 (0.5): <= 0.4");
            Check(profile.TryGet(out vignette) && vignette.intensity.value >= 0.45f, "vignette is stronger than V1 (0.35): >= 0.45");
            Check(profile.TryGet(out grain) && grain.intensity.value > 0f, "film grain is kept");
        }

        // ---------------------------------------------------------------- torches

        private static void CheckTorches(Scene scene)
        {
            Transform torches = FindRoot(scene, "Torches");
            Check(torches != null, "Torches root exists");
            if (torches == null) return;

            var meshyTorch = MeshyMapDressing.Load("WallTorch");
            bool vendorTorch = EmberdelvePolyHavenImport.LoadModel("Lantern_01") != null;
            int bad = 0;
            for (int i = 0; i < torches.childCount; i++)
            {
                Transform torch = torches.GetChild(i);
                Light light = torch.GetComponent<Light>();
                Transform sconce = torch.Find("Sconce");
                bool ok = light != null && sconce != null;
                if (ok)
                {
                    MeshRenderer[] meshes = sconce.GetComponentsInChildren<MeshRenderer>(true);
                    ParticleSystem[] systems = sconce.GetComponentsInChildren<ParticleSystem>(true);
                    ok = meshes.Length > 0 && systems.Length > 0 && sconce.GetComponentsInChildren<Collider>(true).Length == 0;
                    var model = sconce.Find(EmberdelveTorchFactory.ModelName);
                    if (meshyTorch != null)
                        ok &= model != null && PrefabUtility.GetCorrespondingObjectFromSource(model.gameObject) == meshyTorch;
                    else if (vendorTorch) ok &= model != null;

                    if (ok)
                    {
                        ParticleSystem flame = systems[0];
                        ok = flame.main.startSize.constantMax <= 0.2f && flame.main.maxParticles >= 20 && flame.main.maxParticles <= 60;
                        Gradient gradient = flame.colorOverLifetime.color.gradient;
                        GradientColorKey[] keys = gradient != null ? gradient.colorKeys : new GradientColorKey[0];
                        // warm, never dark red: the last colour key still has a strong green channel
                        ok &= keys.Length > 0 && keys[keys.Length - 1].color.g >= 0.3f;
                    }
                }

                if (!ok) { bad++; Debug.LogError(Tag + " torch '" + torch.name + "' lacks a model/mesh, a light, collider-free particles or the warm small flame"); }
            }

            Check(torches.childCount > 0 && bad == 0, "every torch has a mesh, a light and particles, no colliders, and the retuned flame (" + bad + " bad of " + torches.childCount + ")");
            Check(meshyTorch != null || vendorTorch, "a wall sconce model is imported");

            Material flameMaterial = EmberdelveMaterials.FlameMat;
            Check(flameMaterial != null && flameMaterial.GetColor("_BaseColor").maxColorComponent <= 2.2f && flameMaterial.GetColor("_BaseColor").maxColorComponent > 1f,
                "flame HDR colour is smaller than V1 (3.12) but still HDR");
            Check(flameMaterial != null && Mathf.Approximately(flameMaterial.GetFloat("_DstBlend"), (float)UnityEngine.Rendering.BlendMode.One), "flame stays additive");
        }

        // ---------------------------------------------------------------- enemies

        private static void CheckEnemies(Scene scene)
        {
            DungeonLayoutData layout = EmberdelveAssetLibrary.Layout;
            int expected = 0;
            for (int i = 0; i < layout.enemySpawns.Count; i++) if (layout.enemySpawns[i].enemy != null) expected++;

            EnemyAgent[] agents = Object.FindObjectsByType<EnemyAgent>(FindObjectsSortMode.None);
            Check(agents.Length == expected && expected > 0, "one EnemyAgent per layout spawn (" + agents.Length + " of " + expected + ")");

            int enemyLayer = LayerMask.NameToLayer(GameConfig.EnemyLayerName);
            HashSet<string> variants = new HashSet<string>();
            int bad = 0;

            for (int i = 0; i < agents.Length; i++)
            {
                EnemyAgent agent = agents[i];
                string label = agent.name;
                EnemyData data = agent.Data;
                float s = data != null && data.isElite ? 1.25f : 1f;

                // ---- gameplay colliders: pre-V2 reference from data (Body 0.78 x 1.45 x 0.58 at 0.95, Head 0.44 at 1.85, both scaled by s)
                Collider[] colliders = agent.GetComponentsInChildren<Collider>(true);
                bool collidersOk = colliders.Length == 2;
                collidersOk &= ColliderMatches(agent.transform, "Body", new Vector3(0f, 0.95f * s, 0f), new Vector3(0.78f, 1.45f, 0.58f) * s, enemyLayer);
                collidersOk &= ColliderMatches(agent.transform, "Head", new Vector3(0f, 1.85f * s, 0f), Vector3.one * (0.44f * s), enemyLayer);
                SerializedObject serialized = new SerializedObject(agent);
                Transform body = agent.transform.Find("Body");
                Transform head = agent.transform.Find("Head");
                collidersOk &= serialized.FindProperty("bodyTransform").objectReferenceValue == body;
                collidersOk &= head != null && serialized.FindProperty("headCollider").objectReferenceValue == head.GetComponent<Collider>();
                if (!collidersOk) { bad++; Debug.LogError(Tag + " " + label + ": gameplay colliders differ from the pre-V2 reference"); }

                // ---- visual
                Transform visual = agent.transform.Find(EmberdelveEnemyVisualFactory.VisualChildName);
                EnemyVisualDriver driver = visual != null ? visual.GetComponent<EnemyVisualDriver>() : null;
                Animator animator = visual != null ? visual.GetComponentInChildren<Animator>(true) : null;
                bool visualOk = visual != null && driver != null && animator != null && animator.runtimeAnimatorController != null && !animator.applyRootMotion;
                visualOk &= visual != null && visual.GetComponentsInChildren<Collider>(true).Length == 0 && visual.GetComponentsInChildren<HitVolume>(true).Length == 0;
                visualOk &= visual != null && visual.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length > 0;
                visualOk &= animator != null && animator.avatar != null && animator.avatar.isHuman; // V3b: Quaternius Humanoid mannequin

                // the cubes are hidden but still there with their materials (the runtime smoke test reads the Body renderer)
                Renderer bodyRenderer = body != null ? body.GetComponent<Renderer>() : null;
                Renderer headRenderer = head != null ? head.GetComponent<Renderer>() : null;
                visualOk &= bodyRenderer != null && !bodyRenderer.enabled && bodyRenderer.sharedMaterial != null;
                visualOk &= headRenderer != null && !headRenderer.enabled;

                if (visualOk) visualOk = CheckController(animator.runtimeAnimatorController as AnimatorController, label);

                // The builder names enemies "Enemy_<display name>_<spawn index>" and picks the variant from that index.
                int spawnIndex = 0;
                int underscore = agent.name.LastIndexOf('_');
                if (underscore < 0 || !int.TryParse(agent.name.Substring(underscore + 1), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out spawnIndex)) visualOk = false;
                string expectedVariant = EmberdelveEnemyVisualFactory.VariantFor(data, spawnIndex);
                visualOk &= visual != null && visual.name == EmberdelveEnemyVisualFactory.VisualChildName;
                if (visual != null)
                {
                    GameObject source = PrefabUtility.GetCorrespondingObjectFromSource(visual.gameObject);
                    visualOk &= source != null && source.name == "EnemyVisual_" + expectedVariant;
                }
                if (visual != null)
                {
                    // each variant carries its weapon on a hand bone
                    EmberdelveEnemyVisualFactory.Variant variant = EmberdelveEnemyVisualFactory.Find(expectedVariant);
                    if (variant != null && !string.IsNullOrEmpty(variant.MainHand))
                    {
                        Transform hand = EmberdelveEnemyVisualFactory.FindHandBone(visual, true);
                        visualOk &= hand != null && hand.Find(variant.MainHand) != null;
                    }
                    if (variant != null && !string.IsNullOrEmpty(variant.OffHand))
                    {
                        Transform hand = EmberdelveEnemyVisualFactory.FindHandBone(visual, false);
                        visualOk &= hand != null && hand.Find(variant.OffHand) != null;
                    }

                    variants.Add(expectedVariant);

                    // glowing eyes on the head bone exist exactly where the variant asks for them (the Mage)
                    Transform headBone = animator != null ? animator.GetBoneTransform(HumanBodyBones.Head) : null;
                    bool hasEyes = headBone != null && headBone.Find(EmberdelveEnemyVisualFactory.EyeNameLeft) != null && headBone.Find(EmberdelveEnemyVisualFactory.EyeNameRight) != null;
                    visualOk &= headBone != null && hasEyes == (variant != null && variant.GlowingEyes);
                    float expectedScale = data != null && data.isElite ? EmberdelveEnemyVisualFactory.EliteScale : 1f;
                    visualOk &= Mathf.Abs(visual.localScale.x - expectedScale) < 1e-4f;
                    Bounds bounds = BoundsOf(visual);
                    float height = bounds.size.y;
                    visualOk &= height > 1.3f * expectedScale && height < 2.6f * expectedScale;
                }

                if (!visualOk) { bad++; Debug.LogError(Tag + " " + label + ": visual is incomplete (driver, animator controller, hidden cubes, weapons, size)"); }
            }

            Check(bad == 0, "every enemy: unchanged gameplay colliders + a Visual with driver, animator and controller (" + bad + " bad of " + agents.Length + ")");
            Check(variants.Count >= 2, "at least two undead variants are in use (" + string.Join(", ", variants) + ")");
        }

        private static bool ColliderMatches(Transform enemy, string childName, Vector3 localCentre, Vector3 worldSize, int layer)
        {
            Transform child = enemy.Find(childName);
            BoxCollider box = child != null ? child.GetComponent<BoxCollider>() : null;
            if (box == null || box.isTrigger) return false;
            if (layer >= 0 && child.gameObject.layer != layer) return false;

            Vector3 centre = child.TransformPoint(box.center);
            Vector3 lossy = child.lossyScale;
            Vector3 size = new Vector3(Mathf.Abs(box.size.x * lossy.x), Mathf.Abs(box.size.y * lossy.y), Mathf.Abs(box.size.z * lossy.z));
            return (centre - (enemy.position + localCentre)).sqrMagnitude < 1e-6f && (size - worldSize).sqrMagnitude < 1e-6f;
        }

        private static bool CheckController(AnimatorController controller, string label)
        {
            if (controller == null) return false;

            AnimatorStateMachine machine = controller.layers[0].stateMachine;
            Dictionary<string, AnimatorState> states = new Dictionary<string, AnimatorState>();
            for (int i = 0; i < machine.states.Length; i++) states[machine.states[i].state.name] = machine.states[i].state;

            bool ok = true;
            for (int i = 0; i < RequiredStates.Length; i++)
            {
                AnimatorState state;
                if (!states.TryGetValue(RequiredStates[i], out state) || state.motion == null)
                {
                    ok = false;
                    Debug.LogError(Tag + " " + label + ": controller '" + controller.name + "' has no state '" + RequiredStates[i] + "' with a clip");
                }
            }

            if (!ok) return false;

            ok &= machine.defaultState != null && machine.defaultState.name == "Idle";
            string[] looping = { "Idle", "Walk", "Run" };
            for (int i = 0; i < looping.Length; i++)
            {
                AnimationClip clip = states[looping[i]].motion as AnimationClip;
                ok &= clip != null && clip.isLooping;
            }

            ok &= HasParameter(controller, "Speed", AnimatorControllerParameterType.Float);
            ok &= HasParameter(controller, "Attack", AnimatorControllerParameterType.Trigger);
            ok &= HasParameter(controller, "Hit", AnimatorControllerParameterType.Trigger);
            ok &= HasParameter(controller, "Dead", AnimatorControllerParameterType.Bool);
            ok &= machine.anyStateTransitions.Length >= 3;
            return ok;
        }

        private static bool HasParameter(AnimatorController controller, string name, AnimatorControllerParameterType type)
        {
            AnimatorControllerParameter[] parameters = controller.parameters;
            for (int i = 0; i < parameters.Length; i++) if (parameters[i].name == name && parameters[i].type == type) return true;
            return false;
        }

        // ---------------------------------------------------------------- chests

        private static void CheckChests()
        {
            DungeonLayoutData layout = EmberdelveAssetLibrary.Layout;
            GameConfig config = EmberdelveAssetLibrary.Config;
            Check(layout != null && layout.chestSpawns.Count > 0, "the layout declares chests (" + (layout != null ? layout.chestSpawns.Count : 0) + ")");
            Check(AssetDatabase.LoadAssetAtPath<ChestVisualSet>(EmberdelvePropFactory.ChestSetPath) != null, "ChestVisualSet asset exists in Resources");
            if (layout == null || layout.chestSpawns.Count == 0) return;

            // The runtime spawner, run in edit mode on a throw-away host (Awake does not run for plain components in edit mode).
            GameObject hostObject = new GameObject("RetroGate_ChestHost");
            try
            {
                ChestSpawner spawner = hostObject.AddComponent<ChestSpawner>();
                spawner.Build(layout, config, spawner);

                int expected = 0;
                for (int i = 0; i < layout.chestSpawns.Count; i++) if (layout.chestSpawns[i] != null && layout.chestSpawns[i].lootTable != null) expected++;
                Check(spawner.Count == expected && expected > 0, "the spawner built every chest (" + spawner.Count + " of " + expected + ")");

                int interactLayer = LayerMask.NameToLayer(GameConfig.InteractLayerName);
                int bad = 0;
                for (int i = 0; i < spawner.Chests.Count; i++)
                {
                    ChestInteractable chest = spawner.Chests[i];
                    GameObject body = chest.gameObject;
                    Vector3 size = ChestSpawner.SizeFor(chest.Variant);

                    BoxCollider bodyCollider = body.GetComponent<BoxCollider>();
                    Transform lid = chest.Lid;
                    Collider lidCollider = lid != null ? lid.GetComponent<Collider>() : null;
                    bool ok = chest.GetComponent<IInteractable>() != null && bodyCollider != null && !bodyCollider.isTrigger && lid != null && lidCollider != null;
                    ok &= (body.transform.localScale - size).sqrMagnitude < 1e-6f && (interactLayer < 0 || body.layer == interactLayer);
                    ok &= chest.BodySize == size;

                    Transform visual = body.transform.parent.Find(body.name + "_Visual");
                    ChestVisualDriver driver = visual != null ? visual.GetComponent<ChestVisualDriver>() : null;
                    ok &= visual != null && driver != null && driver.HasLid;
                    ok &= visual != null && visual.GetComponentsInChildren<Renderer>(true).Length > 0 && visual.GetComponentsInChildren<Collider>(true).Length == 0;
                    ok &= body.GetComponent<Renderer>() != null && !body.GetComponent<Renderer>().enabled;
                    ok &= lid != null && lid.GetComponent<Renderer>() != null && !lid.GetComponent<Renderer>().enabled;

                    if (!ok) { bad++; Debug.LogError(Tag + " chest '" + body.name + "' lost its interactable/colliders or has no model with a lid hinge"); }
                }

                Check(bad == 0, "every chest keeps its ChestInteractable, collider, lid and layer, and has a model with a lid hinge (" + bad + " bad)");
            }
            finally
            {
                Object.DestroyImmediate(hostObject);
            }
        }

        // ---------------------------------------------------------------- props

        private static void CheckProps(Scene scene)
        {
            Transform props = FindRoot(scene, EmberdelvePropFactory.PropsRootName);
            Check(props != null, "Props root exists");
            if (props == null) return;

            Check(props.childCount >= 4 && props.childCount == EmberdelvePropFactory.LastPlacements.Count,
                "props were scattered (" + props.childCount + " built, " + EmberdelvePropFactory.LastPlacements.Count + " recorded)");
            Check(props.GetComponentsInChildren<Collider>(true).Length == 0, "props have no colliders at all (so none on nav-walkable cells)");
            int vendorSlots = 0;
            Renderer[] propRenderers = props.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < propRenderers.Length; i++)
            {
                Material[] materials = propRenderers[i].sharedMaterials;
                for (int m = 0; m < materials.Length; m++)
                {
                    string path = materials[m] != null ? AssetDatabase.GetAssetPath(materials[m]) : "";
                    if (!path.StartsWith(EmberdelvePolyHavenPostprocessor.Root)) vendorSlots++;
                }
            }

            Check(vendorSlots == 0, "every prop material is a Poly Haven project material - no KayKit or default material is left on a prop (" + vendorSlots + " bad slots)");

            DungeonLayoutData layout = EmberdelveAssetLibrary.Layout;
            bool[,] walkable = layout.BuildWalkableGrid();
            HashSet<Vector2Int> reserved = EmberdelvePropFactory.ReservedCells(layout);
            int bad = 0;

            for (int i = 0; i < EmberdelvePropFactory.LastPlacements.Count; i++)
            {
                EmberdelvePropFactory.Placement placement = EmberdelvePropFactory.LastPlacements[i];
                RoomSpec room = null;
                for (int r = 0; r < layout.rooms.Count; r++) if (layout.rooms[r].roomName == placement.Room) room = layout.rooms[r];

                bool ok = room != null && EmberdelvePropFactory.IsEligible(layout, walkable, room, placement.Cell.x, placement.Cell.y, reserved);

                Transform prop = props.Find("Prop_" + placement.Model + "_" + placement.Cell.x + "_" + placement.Cell.y);
                ok &= prop != null;
                if (prop != null)
                {
                    Bounds bounds = BoundsOf(prop);
                    Vector3 centre = layout.CellCenterWorld(placement.Cell);
                    ok &= Mathf.Abs(bounds.center.x - centre.x) < layout.CellSize && Mathf.Abs(bounds.center.z - centre.z) < layout.CellSize;
                    ok &= bounds.min.y > -0.2f && bounds.min.y < 2.5f;
                }

                if (!ok) { bad++; Debug.LogError(Tag + " prop " + placement.Model + " at " + placement.Cell + " is not in an eligible wall cell"); }
            }

            Check(bad == 0, "every prop stands in an eligible wall cell of a room, clear of doorways and spawns (" + bad + " bad)");

            // determinism: a second pass over the same layout makes the same placements
            List<EmberdelvePropFactory.Placement> first = new List<EmberdelvePropFactory.Placement>(EmberdelvePropFactory.LastPlacements);
            GameObject again = EmberdelvePropFactory.BuildProps(layout);
            bool same = first.Count == EmberdelvePropFactory.LastPlacements.Count;
            for (int i = 0; same && i < first.Count; i++) same = first[i].Model == EmberdelvePropFactory.LastPlacements[i].Model && first[i].Cell == EmberdelvePropFactory.LastPlacements[i].Cell;
            Object.DestroyImmediate(again);
            Check(same, "the scatter is deterministic (same layout, same props)");
        }

        // ---------------------------------------------------------------- helpers

        private static Bounds BoundsOf(Transform root)
        {
            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
            Bounds bounds = new Bounds(root.position, Vector3.zero);
            bool any = false;
            for (int i = 0; i < renderers.Length; i++)
            {
                if (!any) { bounds = renderers[i].bounds; any = true; }
                else bounds.Encapsulate(renderers[i].bounds);
            }

            return bounds;
        }

        private static Transform FindRoot(Scene scene, string name)
        {
            GameObject[] roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++) if (roots[i].name == name) return roots[i].transform;
            return null;
        }

        private static void Check(bool condition, string what)
        {
            _checks++;
            if (condition)
            {
                Debug.Log(Tag + " ok: " + what);
            }
            else
            {
                _failures++;
                Debug.LogError(Tag + " FAIL: " + what);
            }
        }
    }
}
