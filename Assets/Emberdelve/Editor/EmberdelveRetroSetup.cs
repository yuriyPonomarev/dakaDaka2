using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Emberdelve.EditorTools
{
    /// <summary>
    /// V2: the full-screen retro pass. URP's built-in <see cref="FullScreenPassRendererFeature"/> (a Render Graph pass in
    /// URP 17) runs the material Art/Materials/Retro_Fullscreen.mat (shader Emberdelve/RetroFullscreen: pixelation, 5-bit
    /// colour quantisation, 4x4 Bayer dither) after URP's post-processing, on the renderers of the PC pipeline assets. There
    /// is no custom C# pass: the built-in feature binds the camera colour as _BlitTexture and draws the material.
    ///
    /// Idempotent: <see cref="Apply"/> adds the feature to a renderer only if it is not there yet, refreshes its settings
    /// and switches it on. The comparison toggle is the feature's active flag: menu Emberdelve/Visual/Retro Pass, or
    /// <see cref="SetEnabled"/> from a script (also works in Play mode; the next BuildEverything switches it back on).
    /// The look is tuned on the material (pixel size, levels, dither strength, overall strength).
    /// </summary>
    internal static class EmberdelveRetroSetup
    {
        public const string ShaderPath = "Assets/Emberdelve/Art/Shaders/Retro_Fullscreen.shader";
        public const string ShaderName = "Emberdelve/RetroFullscreen";
        public const string MaterialPath = EmberdelvePaths.Materials + "/Retro_Fullscreen.mat";
        public const string FeatureName = "Emberdelve Retro Pass";

        public const float DefaultPixelSize = 2f;        // 2 screen pixels per retro pixel (1080p -> 960x540)
        public const float DefaultColorLevels = 32f;     // 5 bits per channel
        public const float DefaultDitherStrength = 1f;
        public const float DefaultStrength = 1f;

        // ---------------------------------------------------------------- material

        public static Material EnsureMaterial()
        {
            Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath);
            if (shader == null)
            {
                Debug.LogError("[Emberdelve][Retro] shader missing: " + ShaderPath);
                return null;
            }

            EmberdelvePaths.EnsureFolders();

            Material material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            bool created = material == null;
            if (created) material = new Material(shader) { name = "Retro_Fullscreen" };
            else if (material.shader != shader) material.shader = shader;

            // Only the defaults are written on creation; a hand-tuned material keeps its values.
            if (created)
            {
                material.SetFloat("_PixelSize", DefaultPixelSize);
                material.SetFloat("_ColorLevels", DefaultColorLevels);
                material.SetFloat("_DitherStrength", DefaultDitherStrength);
                material.SetFloat("_Strength", DefaultStrength);
                AssetDatabase.CreateAsset(material, MaterialPath);
            }
            else
            {
                EditorUtility.SetDirty(material);
            }

            return material;
        }

        // ---------------------------------------------------------------- renderers

        /// <summary>The renderer assets used by the given pipeline asset (its whole m_RendererDataList), without duplicates.</summary>
        public static List<ScriptableRendererData> RenderersOf(RenderPipelineAsset pipeline)
        {
            List<ScriptableRendererData> result = new List<ScriptableRendererData>();
            if (pipeline == null) return result;

            SerializedProperty list = new SerializedObject(pipeline).FindProperty("m_RendererDataList");
            if (list == null || !list.isArray) return result;

            for (int i = 0; i < list.arraySize; i++)
            {
                ScriptableRendererData data = list.GetArrayElementAtIndex(i).objectReferenceValue as ScriptableRendererData;
                if (data != null && !result.Contains(data)) result.Add(data);
            }

            return result;
        }

        /// <summary>The renderers of the PC pipeline assets (PC_High, PC_Low).</summary>
        public static List<ScriptableRendererData> PcRenderers()
        {
            List<ScriptableRendererData> result = new List<ScriptableRendererData>();
            string[] paths = EmberdelveVisualSetup.PcPipelineAssets;
            for (int i = 0; i < paths.Length; i++)
            {
                RenderPipelineAsset asset = AssetDatabase.LoadAssetAtPath<RenderPipelineAsset>(paths[i]);
                List<ScriptableRendererData> renderers = RenderersOf(asset);
                for (int k = 0; k < renderers.Count; k++) if (!result.Contains(renderers[k])) result.Add(renderers[k]);
            }

            return result;
        }

        /// <summary>The retro feature on a renderer, or null.</summary>
        public static FullScreenPassRendererFeature FindFeature(ScriptableRendererData data)
        {
            if (data == null) return null;
            for (int i = 0; i < data.rendererFeatures.Count; i++)
            {
                FullScreenPassRendererFeature feature = data.rendererFeatures[i] as FullScreenPassRendererFeature;
                if (feature != null && feature.name == FeatureName) return feature;
            }

            return null;
        }

        /// <summary>
        /// Adds (or refreshes) the retro feature on every PC renderer and switches it on. Returns the number of renderers that
        /// now carry it. Never throws: a missing pipeline asset or shader only logs.
        /// </summary>
        public static int Apply()
        {
            Material material = EnsureMaterial();
            if (material == null) return 0;

            List<ScriptableRendererData> renderers = PcRenderers();
            if (renderers.Count == 0)
            {
                Debug.LogWarning("[Emberdelve][Retro] no renderer found on " + string.Join(", ", EmberdelveVisualSetup.PcPipelineAssets) + "; the retro pass was not added.");
                return 0;
            }

            int count = 0;
            for (int i = 0; i < renderers.Count; i++)
            {
                if (EnsureFeature(renderers[i], material)) count++;
            }

            AssetDatabase.SaveAssets();
            Debug.Log("[Emberdelve][Retro] retro pass present and active on " + count + " renderer(s).");
            return count;
        }

        private static bool EnsureFeature(ScriptableRendererData data, Material material)
        {
            FullScreenPassRendererFeature feature = FindFeature(data);
            bool added = false;

            if (feature == null)
            {
                feature = ScriptableObject.CreateInstance<FullScreenPassRendererFeature>();
                feature.name = FeatureName;
                AssetDatabase.AddObjectToAsset(feature, data);

                // Same bookkeeping as the renderer inspector: the feature list and its local-file-id map stay parallel.
                SerializedObject serialized = new SerializedObject(data);
                SerializedProperty features = serialized.FindProperty("m_RendererFeatures");
                SerializedProperty map = serialized.FindProperty("m_RendererFeatureMap");
                if (features == null || map == null)
                {
                    Debug.LogError("[Emberdelve][Retro] " + AssetDatabase.GetAssetPath(data) + " has no m_RendererFeatures / m_RendererFeatureMap; the feature was not added.");
                    Object.DestroyImmediate(feature, true);
                    return false;
                }

                if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(feature, out string guid, out long localId)) localId = 0L;

                int index = features.arraySize;
                features.arraySize = index + 1;
                features.GetArrayElementAtIndex(index).objectReferenceValue = feature;
                map.arraySize = index + 1;
                map.GetArrayElementAtIndex(index).longValue = localId;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                added = true;
            }

            feature.passMaterial = material;
            feature.passIndex = 0;
            feature.injectionPoint = FullScreenPassRendererFeature.InjectionPoint.AfterRenderingPostProcessing;
            feature.fetchColorBuffer = true;
            feature.requirements = ScriptableRenderPassInput.None;
            feature.bindDepthStencilAttachment = false;
            feature.SetActive(true);

            EditorUtility.SetDirty(feature);
            EditorUtility.SetDirty(data);
            if (added) Debug.Log("[Emberdelve][Retro] added '" + FeatureName + "' to " + AssetDatabase.GetAssetPath(data));
            return true;
        }

        // ---------------------------------------------------------------- comparison toggle

        /// <summary>Switches the retro pass on or off on every PC renderer (the comparison toggle). Returns how many renderers changed.</summary>
        public static int SetEnabled(bool enabled)
        {
            int changed = 0;
            List<ScriptableRendererData> renderers = PcRenderers();
            for (int i = 0; i < renderers.Count; i++)
            {
                FullScreenPassRendererFeature feature = FindFeature(renderers[i]);
                if (feature == null) continue;

                feature.SetActive(enabled);
                EditorUtility.SetDirty(feature);
                EditorUtility.SetDirty(renderers[i]);
                changed++;
            }

            if (changed > 0) AssetDatabase.SaveAssets();
            Debug.Log("[Emberdelve][Retro] retro pass " + (enabled ? "ON" : "OFF") + " on " + changed + " renderer(s).");
            return changed;
        }

        [MenuItem("Emberdelve/Visual/Retro Pass/Apply (add and switch on)")]
        private static void MenuApply() { Apply(); }

        [MenuItem("Emberdelve/Visual/Retro Pass/Switch ON")]
        private static void MenuOn() { SetEnabled(true); }

        [MenuItem("Emberdelve/Visual/Retro Pass/Switch OFF")]
        private static void MenuOff() { SetEnabled(false); }
    }
}
