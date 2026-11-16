using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Emberdelve.EditorTools
{
    /// <summary>
    /// V1 ("Dark and Darker" look): everything that is purely visual and shared by the level and lobby builders.
    /// Texture import enforcement, world-scale box meshes, the global post-processing volume profile, camera
    /// post-processing and the URP asset / renderer settings. Every method is idempotent and batchmode safe
    /// (no GUI, no dialogs).
    /// </summary>
    public static class EmberdelveVisualSetup
    {
        public const string SettingsFolder = "Assets/Emberdelve/Settings";
        public const string DungeonVolumePath = SettingsFolder + "/Emberdelve_Dungeon_Volume.asset";
        public const string MeshFolder = "Assets/Emberdelve/Art/Generated/LevelMeshes";

        /// <summary>The URP pipeline assets the PC quality levels use.</summary>
        public static readonly string[] PcPipelineAssets =
        {
            "Assets/Settings/PC/PC_High.asset",
            "Assets/Settings/PC/PC_Low.asset"
        };

        private static readonly HashSet<string> UsedMeshPaths = new HashSet<string>();

        // ---------------------------------------------------------------- folders / textures

        public static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;

            string parent = Path.GetDirectoryName(path).Replace('\\', '/');
            string leaf = Path.GetFileName(path);

            if (!AssetDatabase.IsValidFolder(parent)) EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, leaf);
        }

        /// <summary>
        /// Belt and braces next to <see cref="EmberdelveTextureImport"/>: re-applies the import settings to every
        /// texture of the sets and reimports the ones that differ (e.g. imported before the postprocessor existed).
        /// </summary>
        public static int EnsureTextureImportSettings()
        {
            if (!AssetDatabase.IsValidFolder("Assets/Emberdelve/Art")) return 0;

            int reimported = 0;
            string[] guids = AssetDatabase.FindAssets("t:Texture2D", new[] { "Assets/Emberdelve/Art" });
            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                if (!path.StartsWith(EmberdelveTextureImport.TextureRoot) && !path.StartsWith(EmberdelveTextureImport.KayKitRoot)
                    && path != EmberdelveTextureImport.FlameTexturePath) continue;

                TextureImporter importer = AssetImporter.GetAtPath(path) as TextureImporter;
                if (importer == null) continue;

                if (EmberdelveTextureImport.Apply(importer, path))
                {
                    importer.SaveAndReimport();
                    reimported++;
                }
            }

            return reimported;
        }

        /// <summary>Loads a texture, importing it synchronously if the database does not know it yet. Null if missing.</summary>
        public static Texture2D LoadTexture(string path)
        {
            Texture2D texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (texture == null && File.Exists(path))
            {
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            }

            return texture;
        }

        public static string TexturePath(string id, string suffix, string extension)
        {
            return EmberdelveTextureImport.TextureRoot + id + "/" + id + "_" + suffix + "." + extension;
        }

        // ---------------------------------------------------------------- world-scale box meshes

        /// <summary>Call before a build pass that uses <see cref="ApplyWorldUv"/>; pair with <see cref="PruneMeshes"/>.</summary>
        public static void BeginMeshPass()
        {
            UsedMeshPaths.Clear();
        }

        /// <summary>
        /// Replaces the MeshFilter mesh of a primitive cube (already positioned and scaled) with a box mesh of the same
        /// bounds whose UVs are in world metres divided by the tile size, per face. Transform, scale and the
        /// BoxCollider are untouched. The mesh is saved as an asset so the scene reference persists.
        /// </summary>
        public static Mesh ApplyWorldUv(GameObject box, string prefix, float tileU, float tileV)
        {
            if (box == null) return null;
            MeshFilter filter = box.GetComponent<MeshFilter>();
            if (filter == null) return null;

            EnsureFolder(MeshFolder);

            string path = MeshFolder + "/" + prefix + box.name + ".asset";
            Mesh built = BuildBoxMesh(box.transform.position, box.transform.localScale, tileU, tileV);
            built.name = prefix + box.name;

            Mesh existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            Mesh result;
            if (existing != null)
            {
                EditorUtility.CopySerialized(built, existing);
                UnityEngine.Object.DestroyImmediate(built);
                EditorUtility.SetDirty(existing);
                result = existing;
            }
            else
            {
                AssetDatabase.CreateAsset(built, path);
                result = built;
            }

            UsedMeshPaths.Add(path);
            filter.sharedMesh = result;
            return result;
        }

        /// <summary>Deletes generated meshes with this prefix that the last pass did not produce.</summary>
        public static void PruneMeshes(string prefix)
        {
            if (!AssetDatabase.IsValidFolder(MeshFolder)) return;

            string[] guids = AssetDatabase.FindAssets("t:Mesh", new[] { MeshFolder });
            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                if (!Path.GetFileName(path).StartsWith(prefix)) continue;
                if (UsedMeshPaths.Contains(path)) continue;
                AssetDatabase.DeleteAsset(path);
            }
        }

        // Faces: +X, -X, +Y, -Y, +Z, -Z. For every face cross(u, v) == normal, so triangles (0,1,3),(0,3,2) wind the way
        // Unity expects (clockwise seen from outside) and the texture is never mirrored.
        private static readonly Vector3[] FaceNormal = { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };
        private static readonly Vector3[] FaceU = { Vector3.back, Vector3.forward, Vector3.right, Vector3.right, Vector3.right, Vector3.left };
        private static readonly Vector3[] FaceV = { Vector3.up, Vector3.up, Vector3.back, Vector3.forward, Vector3.up, Vector3.up };

        private static Mesh BuildBoxMesh(Vector3 center, Vector3 size, float tileU, float tileV)
        {
            Vector3[] vertices = new Vector3[24];
            Vector3[] normals = new Vector3[24];
            Vector2[] uvs = new Vector2[24];
            int[] triangles = new int[36];

            float[] signU = { -1f, 1f, -1f, 1f };
            float[] signV = { -1f, -1f, 1f, 1f };

            for (int face = 0; face < 6; face++)
            {
                Vector3 n = FaceNormal[face];
                Vector3 u = FaceU[face];
                Vector3 v = FaceV[face];

                for (int corner = 0; corner < 4; corner++)
                {
                    Vector3 local = n * 0.5f + u * (0.5f * signU[corner]) + v * (0.5f * signV[corner]);
                    Vector3 world = center + Vector3.Scale(local, size);
                    int index = face * 4 + corner;

                    vertices[index] = local;
                    normals[index] = n;
                    uvs[index] = new Vector2(Vector3.Dot(world, u) / tileU, Vector3.Dot(world, v) / tileV);
                }

                int b = face * 4;
                int t = face * 6;
                triangles[t + 0] = b + 0;
                triangles[t + 1] = b + 1;
                triangles[t + 2] = b + 3;
                triangles[t + 3] = b + 0;
                triangles[t + 4] = b + 3;
                triangles[t + 5] = b + 2;
            }

            Mesh mesh = new Mesh();
            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.uv = uvs;
            mesh.triangles = triangles;
            mesh.RecalculateBounds();
            mesh.RecalculateTangents();
            return mesh;
        }

        // ---------------------------------------------------------------- post-processing

        /// <summary>
        /// Creates (or updates in place) the dungeon VolumeProfile asset: ACES, bloom, grade, vignette, film grain.
        /// </summary>
        public static VolumeProfile EnsureDungeonVolumeProfile()
        {
            EnsureFolder(SettingsFolder);

            VolumeProfile profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(DungeonVolumePath);
            if (profile == null)
            {
                profile = ScriptableObject.CreateInstance<VolumeProfile>();
                profile.name = "Emberdelve_Dungeon_Volume";
                AssetDatabase.CreateAsset(profile, DungeonVolumePath);
            }

            Tonemapping tonemapping = GetOrAdd<Tonemapping>(profile);
            tonemapping.mode.Override(TonemappingMode.ACES);

            Bloom bloom = GetOrAdd<Bloom>(profile);
            // V2: slightly less bloom than V1 (0.5 / 1.0 / 0.6); the retro pass wants crisper highlights.
            bloom.threshold.Override(1.1f);
            bloom.intensity.Override(0.32f);
            bloom.scatter.Override(0.5f);
            bloom.tint.Override(new Color(1f, 0.82f, 0.62f, 1f));
            bloom.highQualityFiltering.Override(true);

            ColorAdjustments grade = GetOrAdd<ColorAdjustments>(profile);
            grade.postExposure.Override(0.6f);
            grade.contrast.Override(6f);
            grade.saturation.Override(-12f);
            grade.colorFilter.Override(new Color(1f, 0.93f, 0.84f, 1f));

            Vignette vignette = GetOrAdd<Vignette>(profile);
            vignette.color.Override(Color.black);
            // V2: stronger vignette than V1 (0.35 / 0.4).
            vignette.intensity.Override(0.45f);
            vignette.smoothness.Override(0.45f);
            vignette.rounded.Override(false);

            FilmGrain grain = GetOrAdd<FilmGrain>(profile);
            grain.type.Override(FilmGrainLookup.Medium1);
            grain.intensity.Override(0.2f);
            grain.response.Override(0.8f);

            EditorUtility.SetDirty(profile);
            AssetDatabase.SaveAssets();
            return profile;
        }

        private static T GetOrAdd<T>(VolumeProfile profile) where T : VolumeComponent
        {
            T component;
            if (profile.TryGet(out component) && component != null) return component;

            component = profile.Add<T>(false);
            component.name = typeof(T).Name;
            if (string.IsNullOrEmpty(AssetDatabase.GetAssetPath(component)))
            {
                AssetDatabase.AddObjectToAsset(component, profile);
            }

            EditorUtility.SetDirty(component);
            return component;
        }

        /// <summary>Adds a global Volume that uses the dungeon profile to the active scene.</summary>
        public static Volume AddGlobalVolume(string objectName)
        {
            VolumeProfile profile = EnsureDungeonVolumeProfile();

            GameObject go = new GameObject(objectName);
            Volume volume = go.AddComponent<Volume>();
            volume.isGlobal = true;
            volume.priority = 0f;
            volume.weight = 1f;
            volume.sharedProfile = profile;
            return volume;
        }

        /// <summary>Post-processing on, HDR, SMAA and dithering (banding in dark fog) for a scene camera.</summary>
        public static void ConfigureCamera(Camera camera)
        {
            if (camera == null) return;

            camera.allowHDR = true;

            UniversalAdditionalCameraData data = camera.GetUniversalAdditionalCameraData();
            data.renderPostProcessing = true;
            data.renderShadows = true;
            data.requiresDepthTexture = true;
            data.dithering = true;
            data.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
            data.antialiasingQuality = AntialiasingQuality.High;
        }

        // ---------------------------------------------------------------- URP asset / renderer settings

        /// <summary>
        /// Makes sure the PC URP assets support per-pixel additional lights with soft shadows, HDR, and a 2048+ shadow
        /// atlas, and reports whether the renderer has Screen Space Ambient Occlusion. Written through SerializedObject
        /// (field names of URP 17), only touching fields that differ. Renderer features are never added from code; a
        /// missing SSAO feature is logged as a warning for a manual step.
        /// </summary>
        public static void ApplyRenderSettings()
        {
            bool anyChanged = false;

            for (int i = 0; i < PcPipelineAssets.Length; i++)
            {
                string path = PcPipelineAssets[i];
                RenderPipelineAsset asset = AssetDatabase.LoadAssetAtPath<RenderPipelineAsset>(path);
                if (asset == null)
                {
                    Debug.LogWarning("[Emberdelve][Visual] URP asset not found: " + path);
                    continue;
                }

                SerializedObject serialized = new SerializedObject(asset);
                bool changed = false;
                changed |= SetBool(serialized, "m_SupportsHDR", true);
                changed |= SetBool(serialized, "m_RequireDepthTexture", true);
                changed |= SetInt(serialized, "m_ColorGradingMode", 1, false);            // HighDynamicRange
                changed |= SetInt(serialized, "m_AdditionalLightsRenderingMode", 1, false); // PerPixel
                changed |= SetInt(serialized, "m_AdditionalLightsPerObjectLimit", 4, true);
                changed |= SetBool(serialized, "m_AdditionalLightShadowsSupported", true);
                changed |= SetInt(serialized, "m_AdditionalLightsShadowmapResolution", 2048, true);
                changed |= SetBool(serialized, "m_SoftShadowsSupported", true);
                changed |= SetInt(serialized, "m_SoftShadowQuality", 2, true);              // at least Medium
                changed |= SetBool(serialized, "m_MainLightShadowsSupported", true);

                if (changed)
                {
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                    EditorUtility.SetDirty(asset);
                    anyChanged = true;
                    Debug.Log("[Emberdelve][Visual] Updated render settings on " + path);
                }

                ReportSsao(serialized, path);
            }

            if (anyChanged) AssetDatabase.SaveAssets();

            // V2: the retro full-screen pass goes onto the PC renderers together with the other render settings.
            EmberdelveRetroSetup.Apply();

            RenderPipelineAsset active = QualitySettings.renderPipeline != null ? QualitySettings.renderPipeline : GraphicsSettings.defaultRenderPipeline;
            Debug.Log("[Emberdelve][Visual] active render pipeline asset: " + (active != null ? AssetDatabase.GetAssetPath(active) : "none"));
        }

        private static void ReportSsao(SerializedObject pipeline, string pipelinePath)
        {
            SerializedProperty list = pipeline.FindProperty("m_RendererDataList");
            if (list == null || !list.isArray) return;

            // Only the default renderer matters for the PC quality levels.
            SerializedProperty first = list.arraySize > 0 ? list.GetArrayElementAtIndex(0) : null;
            ScriptableRendererData data = first != null ? first.objectReferenceValue as ScriptableRendererData : null;
            if (data == null) return;

            bool found = false;
            for (int i = 0; i < data.rendererFeatures.Count; i++)
            {
                ScriptableRendererFeature feature = data.rendererFeatures[i];
                if (feature == null || !(feature is ScreenSpaceAmbientOcclusion)) continue;

                found = true;
                if (!feature.isActive)
                {
                    feature.SetActive(true);
                    EditorUtility.SetDirty(feature);
                    EditorUtility.SetDirty(data);
                    AssetDatabase.SaveAssets();
                    Debug.Log("[Emberdelve][Visual] Enabled SSAO on " + AssetDatabase.GetAssetPath(data));
                }
            }

            if (!found)
            {
                Debug.LogWarning("[Emberdelve][Visual] " + AssetDatabase.GetAssetPath(data) + " (used by " + pipelinePath +
                                 ") has no Screen Space Ambient Occlusion renderer feature. Add it manually: select the renderer asset, Add Renderer Feature > Screen Space Ambient Occlusion.");
            }
        }

        private static bool SetBool(SerializedObject serialized, string name, bool wanted)
        {
            SerializedProperty property = serialized.FindProperty(name);
            if (property == null)
            {
                Debug.LogWarning("[Emberdelve][Visual] URP asset has no field '" + name + "'.");
                return false;
            }

            if (property.boolValue == wanted) return false;
            property.boolValue = wanted;
            return true;
        }

        private static bool SetInt(SerializedObject serialized, string name, int wanted, bool atLeast)
        {
            SerializedProperty property = serialized.FindProperty(name);
            if (property == null)
            {
                Debug.LogWarning("[Emberdelve][Visual] URP asset has no field '" + name + "'.");
                return false;
            }

            if (atLeast ? property.intValue >= wanted : property.intValue == wanted) return false;
            property.intValue = wanted;
            return true;
        }
    }
}
