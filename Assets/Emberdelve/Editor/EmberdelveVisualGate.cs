using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using Emberdelve.Core;
using Emberdelve.Feedback;
using Emberdelve.Level;

namespace Emberdelve.EditorTools
{
    /// <summary>
    /// V1 acceptance gate for the visual-only "dark dungeon" slice.
    ///
    ///   Unity.exe -batchmode -nographics -projectPath . -logFile Logs/visual_gate.log
    ///             -executeMethod Emberdelve.EditorTools.EmberdelveVisualGate.Run
    ///
    /// (Do NOT pass -quit: the gate exits the Editor itself.) It rebuilds the lobby and the dungeon scene and asserts:
    /// texture import types, PBR materials, level collider bounds unchanged, global Volume with ACES, soft-shadowed
    /// torches with a sconce and flame, post-processing on the camera, the dark environment, the URP asset settings.
    /// Final line: [Emberdelve][VisualGate] PASS|FAIL checks=N failures=M
    /// </summary>
    public static class EmberdelveVisualGate
    {
        private const string Tag = "[Emberdelve][VisualGate]";
        private static readonly string[] TextureIds = { "Bricks100", "PavingStones142", "Rock030", "Planks023A" };

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

            if (Application.isBatchMode) EmberdelveGateExit.Exit(_failures == 0 ? 0 : 1);
        }

        private static void Execute()
        {
            EmberdelvePaths.EnsureFolders();
            EmberdelveProjectSetup.EnsureLayers();
            AssetDatabase.Refresh();

            CheckTextures();

            EmberdelveAssetLibrary.BuildAll();
            CheckMaterials();

            // Both builders must run clean; the dungeon is built last so it is the loaded scene afterwards.
            EmberdelveLobbyBuilder.Build();
            Scene scene = EmberdelveLevelBuilder.Build();
            AssetDatabase.SaveAssets();

            CheckLevelBoxes(scene);
            CheckCryptArchitecture(scene);
            CheckVolume();
            CheckTorches(scene);
            CheckCamera();
            CheckEnvironment();
            CheckRenderAssets();
        }

        // ---------------------------------------------------------------- textures / materials

        private static void CheckTextures()
        {
            for (int i = 0; i < TextureIds.Length; i++)
            {
                string id = TextureIds[i];
                CheckImporter(EmberdelveVisualSetup.TexturePath(id, "BaseColor", "jpg"), TextureImporterType.Default, true);
                CheckImporter(EmberdelveVisualSetup.TexturePath(id, "Normal", "png"), TextureImporterType.NormalMap, true);
                CheckImporter(EmberdelveVisualSetup.TexturePath(id, "MaskMS", "png"), TextureImporterType.Default, false);
                CheckImporter(EmberdelveVisualSetup.TexturePath(id, "AO", "jpg"), TextureImporterType.Default, false);
            }
        }

        private static void CheckImporter(string path, TextureImporterType type, bool srgb)
        {
            TextureImporter importer = AssetImporter.GetAtPath(path) as TextureImporter;
            Check(importer != null, "texture imported: " + path);
            if (importer == null) return;

            Check(importer.textureType == type, path + " type " + type + " (was " + importer.textureType + ")");
            if (type != TextureImporterType.NormalMap) Check(importer.sRGBTexture == srgb, path + " sRGB=" + srgb + " (was " + importer.sRGBTexture + ")");
            Check(importer.wrapMode == TextureWrapMode.Repeat, path + " wrap Repeat (was " + importer.wrapMode + ")");
            // V2 (intentional change, see Docs/Tasks/V2.md): V1 expected aniso 8 / Trilinear / 2048; the retro look is aniso 1 / Point / <= 512.
            Check(importer.anisoLevel == 1, path + " aniso 1 (V2 retro; was 8 in V1) (was " + importer.anisoLevel + ")");
            Check(importer.filterMode == FilterMode.Point, path + " Point filter (V2 retro) (was " + importer.filterMode + ")");
            Check(importer.maxTextureSize <= 512, path + " max size <= 512 (V2 retro) (was " + importer.maxTextureSize + ")");
            Check(importer.mipmapEnabled, path + " mipmaps on");
        }

        private static void CheckMaterials()
        {
            CheckPbr(EmberdelveMaterials.StoneFloor, "Stone_Floor");
            CheckPbr(EmberdelveMaterials.StoneWall, "Stone_Wall");
            CheckPbr(EmberdelveMaterials.StoneCeiling, "Stone_Ceiling");
            CheckPbr(EmberdelveMaterials.LobbyFloor, "Lobby_StoneFloor");
            CheckPbr(EmberdelveMaterials.Wood, "Wood_Planks");

            Material flame = EmberdelveMaterials.FlameMat;
            Check(flame != null, "flame material exists");
            if (flame != null)
            {
                Check(flame.HasProperty("_Surface") && flame.GetFloat("_Surface") > 0.5f, "flame material is transparent");
                Check(flame.HasProperty("_DstBlend") && Mathf.Approximately(flame.GetFloat("_DstBlend"), (float)BlendMode.One), "flame material is additive");
                Check(flame.GetColor("_BaseColor").maxColorComponent > 1f, "flame colour is HDR");
            }

            Material blade = EmberdelveMaterials.Blade;
            Check(blade != null, "blade material exists");
            if (blade != null)
            {
                Color bladeColor = blade.GetColor("_BaseColor");
                Check(bladeColor.maxColorComponent <= 0.45f, "blade base colour is dark enough not to blow out under the hand torch");
                Check(blade.GetFloat("_Metallic") >= 0.8f, "blade is metallic");
            }
        }

        private static void CheckPbr(Material material, string label)
        {
            Check(material != null, label + " exists");
            if (material == null) return;

            Check(material.GetTexture("_BaseMap") != null, label + " has BaseMap");
            Check(material.GetTexture("_BumpMap") != null, label + " has BumpMap");
            Check(material.IsKeywordEnabled("_NORMALMAP"), label + " has _NORMALMAP");
            Check(material.GetTexture("_MetallicGlossMap") != null, label + " has MetallicGlossMap");
            Check(material.GetTexture("_OcclusionMap") != null, label + " has OcclusionMap");
        }

        // ---------------------------------------------------------------- level geometry

        private static void CheckLevelBoxes(Scene scene)
        {
            int levelLayer = LayerMask.NameToLayer(GameConfig.LevelLayerName);
            Transform root = FindRoot(scene, "Level_Blockout");
            Check(root != null, "Level_Blockout exists");
            if (root == null) return;

            Check(EmberdelveLevelBuilder.LastBoxes.Count > 0, "builder recorded level boxes (" + EmberdelveLevelBuilder.LastBoxes.Count + ")");

            int found = 0;
            int boxFailures = 0;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform child = root.GetChild(i);
                string n = child.name;
                if (!(n.StartsWith("Floor_") || n.StartsWith("Wall_") || n.StartsWith("Ceiling_"))) continue;
                found++;

                bool ok = true;
                BoxCollider box = child.GetComponent<BoxCollider>();
                Bounds expected;
                if (box == null || !EmberdelveLevelBuilder.LastBoxes.TryGetValue(n, out expected))
                {
                    Debug.LogError(Tag + " box '" + n + "' has no BoxCollider or no recorded bounds");
                    boxFailures++;
                    continue;
                }

                // collider bounds from the transform, independent of the renderer and of the physics scene
                Vector3 actualCenter = child.TransformPoint(box.center);
                Vector3 lossy = child.lossyScale;
                Vector3 actualSize = new Vector3(Mathf.Abs(box.size.x * lossy.x), Mathf.Abs(box.size.y * lossy.y), Mathf.Abs(box.size.z * lossy.z));
                Vector3 expectedSize = Vector3.Max(expected.size, new Vector3(0.01f, 0.01f, 0.01f));
                if ((actualCenter - expected.center).sqrMagnitude > 1e-6f) { ok = false; Debug.LogError(Tag + " " + n + " collider centre " + actualCenter + " expected " + expected.center); }
                if ((actualSize - expectedSize).sqrMagnitude > 1e-6f) { ok = false; Debug.LogError(Tag + " " + n + " collider size " + actualSize + " expected " + expectedSize); }
                if (box.isTrigger) { ok = false; Debug.LogError(Tag + " " + n + " collider became a trigger"); }
                if (levelLayer >= 0 && child.gameObject.layer != levelLayer) { ok = false; Debug.LogError(Tag + " " + n + " left the Level layer"); }

                MeshFilter filter = child.GetComponent<MeshFilter>();
                Mesh mesh = filter != null ? filter.sharedMesh : null;
                if (mesh == null || mesh.uv.Length != 24 || !AssetDatabase.Contains(mesh))
                {
                    ok = false;
                    Debug.LogError(Tag + " " + n + " has no saved world-UV mesh asset");
                }
                else if ((mesh.bounds.size - Vector3.one).sqrMagnitude > 1e-6f)
                {
                    ok = false;
                    Debug.LogError(Tag + " " + n + " mesh is not a unit box: " + mesh.bounds.size);
                }

                if (!ok) boxFailures++;
            }

            int recordedLevel = 0;
            foreach (string key in EmberdelveLevelBuilder.LastBoxes.Keys)
                if (key.StartsWith("Floor_") || key.StartsWith("Wall_") || key.StartsWith("Ceiling_")) recordedLevel++;
            Check(found == recordedLevel, "every recorded level box exists in the scene (" + found + " of " + recordedLevel + ")");
            Check(boxFailures == 0, "level box colliders / meshes match the layout (" + boxFailures + " bad)");
        }

        // ---------------------------------------------------------------- volume / camera / environment

        private static void CheckVolume()
        {
            Volume[] volumes = Object.FindObjectsByType<Volume>(FindObjectsSortMode.None);
            bool found = false;
            bool aces = false;
            bool bloom = false;
            bool grain = false;
            bool grade = false;

            for (int i = 0; i < volumes.Length; i++)
            {
                Volume volume = volumes[i];
                if (!volume.isGlobal || volume.sharedProfile == null) continue;
                if (AssetDatabase.GetAssetPath(volume.sharedProfile) != EmberdelveVisualSetup.DungeonVolumePath) continue;

                found = true;
                Tonemapping tonemapping;
                if (volume.sharedProfile.TryGet(out tonemapping)) aces = tonemapping.mode.overrideState && tonemapping.mode.value == TonemappingMode.ACES;
                Bloom b;
                if (volume.sharedProfile.TryGet(out b)) bloom = b.intensity.overrideState && b.intensity.value > 0f;
                FilmGrain g;
                if (volume.sharedProfile.TryGet(out g)) grain = g.intensity.overrideState && g.intensity.value > 0f;
                ColorAdjustments adj;
                if (volume.sharedProfile.TryGet(out adj)) grade = adj.postExposure.value >= 0.3f && adj.saturation.value >= -15f;
            }

            Check(found, "dungeon scene has a global Volume using " + EmberdelveVisualSetup.DungeonVolumePath);
            Check(aces, "volume profile: Tonemapping = ACES");
            Check(grade, "volume profile: ColorAdjustments postExposure >= 0.3 and saturation >= -15");
            Check(bloom, "volume profile: Bloom active");
            Check(grain, "volume profile: Film Grain active");
        }

        private static void CheckCamera()
        {
            GameObject cameraGo = GameObject.FindGameObjectWithTag("MainCamera");
            Camera camera = cameraGo != null ? cameraGo.GetComponent<Camera>() : null;
            Check(camera != null, "MainCamera exists");
            if (camera == null) return;

            UniversalAdditionalCameraData data = camera.GetUniversalAdditionalCameraData();
            Check(data != null && data.renderPostProcessing, "camera renderPostProcessing is on");

            Transform handTorch = camera.transform.Find("Player_Torch");
            Check(handTorch != null && handTorch.GetComponent<Light>() != null && handTorch.GetComponent<TorchFlicker>() != null,
                "the player's hand torch (light + TorchFlicker) is intact");
        }

        private static void CheckEnvironment()
        {
            Color ambient = RenderSettings.ambientLight;
            Check(ambient.maxColorComponent <= 0.1f && ambient.b > ambient.r, "ambient remains near-black and cool (" + ambient + ")");
            Check(RenderSettings.fog && RenderSettings.fogMode == FogMode.Exponential, "exponential fog on");
            Check(RenderSettings.fogDensity >= 0.025f && RenderSettings.fogDensity <= 0.032f, "fog density " + RenderSettings.fogDensity + " in crypt atmosphere range 0.025-0.032");
            Check(RenderSettings.skybox != null && RenderSettings.skybox.shader != null && RenderSettings.skybox.shader.name == "Skybox/Procedural",
                "broken-roof court has a procedural skybox");

            GameObject moon = GameObject.Find("Moon_Slit_Directional");
            Light moonLight = moon != null ? moon.GetComponent<Light>() : null;
            Check(moonLight != null && moonLight.intensity >= 0.3f && moonLight.intensity <= 0.4f &&
                  moonLight.shadows == LightShadows.Soft && moonLight.shadowStrength >= 0.5f,
                "moon slit gives restrained directional fill with soft shadows");
        }

        private static void CheckCryptArchitecture(Scene scene)
        {
            DungeonLayoutData layout = EmberdelveAssetLibrary.Layout;
            Transform level = FindRoot(scene, "Level_Blockout");
            Transform architecture = level != null ? level.Find("Crypt_Architecture") : null;
            Transform arches = level != null ? level.Find("Crypt_Archways") : null;
            Transform tombs = level != null ? level.Find("Crypt_Wall_Tombs") : null;
            Check(layout != null && level != null && architecture != null && arches != null && tombs != null,
                "crypt architecture, archways, and wall tombs are present");
            if (layout == null || level == null || architecture == null || arches == null || tombs == null) return;

            CheckOpenRoofAperture(level, layout);
            CheckArchwayCenter(arches, layout, false);
            CheckArchwayCenter(arches, layout, true);
            CheckArchColliders(arches);
            CheckWallMountedDetails(architecture, tombs, layout);
        }

        private static void CheckOpenRoofAperture(Transform level, DungeonLayoutData layout)
        {
            RoomSpec court = layout.rooms.Find(r => r != null && r.roomName == "Ember Gate");
            Check(court != null, "Ember Gate court is defined");
            if (court == null) return;

            float cell = layout.CellSize;
            float xMin = layout.origin.x + (court.minCell.x + 1) * cell;
            float xMax = layout.origin.x + court.maxCell.x * cell;
            float zMin = layout.origin.z + (court.minCell.y + 1) * cell;
            float zMax = layout.origin.z + court.maxCell.y * cell;
            bool roofClear = true;
            for (int i = 0; i < level.childCount; i++)
            {
                Transform child = level.GetChild(i);
                if (!child.name.StartsWith("Ceiling_")) continue;
                Renderer renderer = child.GetComponent<Renderer>();
                if (renderer == null) { roofClear = false; continue; }
                Bounds b = renderer.bounds;
                bool overlaps = b.max.x > xMin && b.min.x < xMax && b.max.z > zMin && b.min.z < zMax;
                if (overlaps) roofClear = false;
            }
            Check(roofClear, "Ember Gate aperture is clear of ceiling meshes");
        }

        private static void CheckArchwayCenter(Transform arches, DungeonLayoutData layout, bool vertical)
        {
            CorridorSpec corridor = layout.corridors.Find(c => vertical
                ? c.fromCell.x == c.toCell.x && c.fromCell.y != c.toCell.y
                : c.fromCell.y == c.toCell.y && c.fromCell.x != c.toCell.x);
            Check(corridor != null, vertical ? "a vertical corridor exists for arch alignment" : "a horizontal corridor exists for arch alignment");
            if (corridor == null) return;

            int axis = vertical ? corridor.fromCell.x : corridor.fromCell.y;
            int width = Mathf.Max(1, corridor.width);
            int lowOffset = -(width / 2);
            int highOffset = (width - 1) / 2;
            float expected = (vertical ? layout.origin.x : layout.origin.z) +
                             (axis + (lowOffset + highOffset + 1f) * 0.5f) * layout.CellSize;
            Transform left = arches.Find(corridor.corridorName + "_Entry_Jamb_L");
            Transform right = arches.Find(corridor.corridorName + "_Entry_Jamb_R");
            Check(left != null && right != null, (vertical ? "vertical" : "horizontal") + " corridor entry jambs exist");
            if (left == null || right == null) return;

            float actual = vertical ? (left.position.x + right.position.x) * 0.5f : (left.position.z + right.position.z) * 0.5f;
            Check(Mathf.Abs(actual - expected) <= 0.001f,
                (vertical ? "vertical" : "horizontal") + " arch is centered on BuildWalkableGrid corridor width (" + actual + " vs " + expected + ")");
        }

        private static void CheckArchColliders(Transform arches)
        {
            int layer = LayerMask.NameToLayer(GameConfig.LevelLayerName);
            string[] parts = { "Entry to Library_Entry_Jamb_L", "Entry to Library_Entry_Voussoir_3", "Entry to Library_Entry_Keystone" };
            bool valid = true;
            for (int i = 0; i < parts.Length; i++)
            {
                Transform part = arches.Find(parts[i]);
                if (part == null || part.GetComponent<BoxCollider>() == null || (layer >= 0 && part.gameObject.layer != layer)) valid = false;
            }
            Check(valid, "arch jambs, voussoirs, and keystones block on the Level layer");
        }

        private static void CheckWallMountedDetails(Transform architecture, Transform tombs, DungeonLayoutData layout)
        {
            Transform pilaster = architecture.Find("Entry Hall_Pilaster_0");
            Renderer pilasterRenderer = pilaster != null ? pilaster.GetComponent<Renderer>() : null;
            Check(pilasterRenderer != null && pilaster.GetComponent<Collider>() == null && pilasterRenderer.bounds.size.z <= 0.08f,
                "pilasters are thin wall-mounted visuals without ghost colliders");

            RoomSpec crypt = layout.rooms.Find(r => r != null && r.roomName == "Crypt");
            Transform recess = tombs.Find("Crypt_Tomb_Left_Recess");
            Renderer recessRenderer = recess != null ? recess.GetComponent<Renderer>() : null;
            if (crypt == null || recessRenderer == null)
            {
                Check(false, "crypt tomb recess is visible in front of the blocking wall");
                return;
            }
            float wallFace = layout.origin.z + (crypt.maxCell.y + 1) * layout.CellSize - Mathf.Max(0.05f, layout.wallThickness) * 0.5f;
            Bounds recessBounds = recessRenderer.bounds;
            Check(recess.GetComponent<Collider>() == null && recessBounds.size.z <= 0.05f &&
                  recessBounds.max.z < wallFace && wallFace - recessBounds.min.z <= 0.06f,
                "crypt tomb recess is a visible shallow overlay in front of the blocking wall");
            Transform sarcophagus = tombs.Find("Crypt_Tomb_Left_Sarcophagus");
            Renderer sarcophagusRenderer = sarcophagus != null ? sarcophagus.GetComponent<Renderer>() : null;
            Check(sarcophagusRenderer != null && sarcophagus.GetComponent<Collider>() == null && sarcophagusRenderer.bounds.size.z <= 0.05f,
                "sarcophagus is a shallow wall relief without a hidden passage volume");
        }

        // ---------------------------------------------------------------- torches

        private static void CheckTorches(Scene scene)
        {
            Transform torches = FindRoot(scene, "Torches");
            Check(torches != null, "Torches root exists");
            if (torches == null) return;

            DungeonLayoutData layout = EmberdelveAssetLibrary.Layout;
            Check(layout != null && torches.childCount == layout.torches.Count, "one torch per layout entry (" + torches.childCount + ")");

            int bad = 0;
            for (int i = 0; i < torches.childCount; i++)
            {
                Transform torch = torches.GetChild(i);
                Light light = torch.GetComponent<Light>();
                Transform sconce = torch.Find("Sconce");
                bool ok = light != null && light.type == LightType.Point && light.shadows != LightShadows.None
                          && torch.GetComponent<TorchFlicker>() != null && sconce != null && sconce.GetComponent<Collider>() == null;

                if (ok)
                {
                    ParticleSystem[] systems = sconce.GetComponentsInChildren<ParticleSystem>(true);
                    ok = systems.Length >= 1 && systems[0].main.maxParticles >= 20 && systems[0].main.maxParticles <= 60;
                    Collider[] colliders = sconce.GetComponentsInChildren<Collider>(true);
                    if (colliders.Length > 0) ok = false;
                }

                if (!ok)
                {
                    bad++;
                    Debug.LogError(Tag + " torch '" + torch.name + "' is missing a soft-shadow light, a collider-free sconce or its flame");
                }
            }

            Check(bad == 0, "every torch: point light with shadows, TorchFlicker, collider-free sconce with a flame (" + bad + " bad)");
        }

        // ---------------------------------------------------------------- URP assets

        private static void CheckRenderAssets()
        {
            string[] paths = EmberdelveVisualSetup.PcPipelineAssets;
            for (int i = 0; i < paths.Length; i++)
            {
                RenderPipelineAsset asset = AssetDatabase.LoadAssetAtPath<RenderPipelineAsset>(paths[i]);
                Check(asset != null, paths[i] + " loads");
                if (asset == null) continue;

                SerializedObject so = new SerializedObject(asset);
                Check(Bool(so, "m_SupportsHDR"), paths[i] + " HDR on");
                Check(Bool(so, "m_AdditionalLightShadowsSupported"), paths[i] + " additional light shadows on");
                Check(Bool(so, "m_SoftShadowsSupported"), paths[i] + " soft shadows on");
                Check(Int(so, "m_AdditionalLightsRenderingMode") == 1, paths[i] + " additional lights per pixel");
                Check(Int(so, "m_AdditionalLightsShadowmapResolution") >= 2048, paths[i] + " additional light shadow atlas >= 2048");
            }

            // SSAO is required on the high preset (the one Standalone uses); the low preset is only reported.
            Check(HasSsao(paths[0]), paths[0] + " renderer has Screen Space Ambient Occlusion");
            if (paths.Length > 1 && !HasSsao(paths[1])) Debug.LogWarning(Tag + " " + paths[1] + " renderer has no SSAO (low preset, not required)");
        }

        private static bool HasSsao(string pipelinePath)
        {
            RenderPipelineAsset asset = AssetDatabase.LoadAssetAtPath<RenderPipelineAsset>(pipelinePath);
            if (asset == null) return false;

            SerializedProperty list = new SerializedObject(asset).FindProperty("m_RendererDataList");
            if (list == null || list.arraySize == 0) return false;

            ScriptableRendererData data = list.GetArrayElementAtIndex(0).objectReferenceValue as ScriptableRendererData;
            if (data == null) return false;

            for (int i = 0; i < data.rendererFeatures.Count; i++)
            {
                if (data.rendererFeatures[i] is ScreenSpaceAmbientOcclusion) return true;
            }

            return false;
        }

        private static bool Bool(SerializedObject so, string name)
        {
            SerializedProperty p = so.FindProperty(name);
            return p != null && p.boolValue;
        }

        private static int Int(SerializedObject so, string name)
        {
            SerializedProperty p = so.FindProperty(name);
            return p != null ? p.intValue : -1;
        }

        // ---------------------------------------------------------------- helpers

        private static Transform FindRoot(Scene scene, string name)
        {
            GameObject[] roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
            {
                if (roots[i].name == name) return roots[i].transform;
            }

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
