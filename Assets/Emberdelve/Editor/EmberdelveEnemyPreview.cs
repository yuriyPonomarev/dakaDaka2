using System.IO;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace Emberdelve.EditorTools
{
    /// <summary>
    /// V3b review tool: renders the four enemy visual prefabs side by side, posed by one of their clips, to a PNG. Works in the
    /// open Editor without touching the open scenes: a temporary additive scene is created, rendered with a temporary camera and
    /// closed again (nothing is saved). The retro fullscreen features are switched off for the few milliseconds of the render
    /// (the mask pass blanks an off-screen camera) and restored; the renderer assets are not saved.
    /// Example: EmberdelveEnemyPreview.Render(@"...\Logs\quick\v3b\idle.png", "idle", 0.3f, 180f, 7f)
    /// pose = idle | walk | run | attack | hit | death; time = fraction of the clip; yaw = camera yaw (180 looks at the fronts).
    /// </summary>
    public static class EmberdelveEnemyPreview
    {
        public static string Render(string pngPath, string pose, float time, float yaw, float distance)
        {
            return Render(pngPath, pose, time, yaw, distance, new[] { "Minion", "Rogue", "Warrior", "Mage" }, 1600, 800, 1.0f, 32f);
        }

        public static string Render(string pngPath, string pose, float time, float yaw, float distance, string[] variants, int width, int height, float aimHeight, float fov)
        {
            Scene previous = SceneManager.GetActiveScene();
            Scene preview = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            List<ScriptableRendererFeature> switched = new List<ScriptableRendererFeature>();
            try
            {
                SceneManager.SetActiveScene(preview);
                Vector3 origin = new Vector3(4000f, 0f, 4000f);
                GameObject root = new GameObject("V3bPreview");
                root.transform.position = origin;

                float spacing = 1.6f;
                for (int i = 0; i < variants.Length; i++)
                {
                    EmberdelveEnemyVisualFactory.Variant variant = EmberdelveEnemyVisualFactory.Find(variants[i]);
                    GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(EmberdelveEnemyVisualFactory.PrefabPath(variants[i]));
                    if (variant == null || prefab == null) continue;

                    GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                    instance.transform.SetParent(root.transform, false);
                    instance.transform.localPosition = new Vector3((i - (variants.Length - 1) * 0.5f) * -spacing, 0f, 0f);

                    string clipName = pose == "walk" ? variant.Walk : pose == "run" ? variant.Run : pose == "attack" ? variant.Attack : pose == "hit" ? variant.Hit : pose == "death" ? variant.Death : variant.Idle;
                    AnimationClip clip = EmberdelveEnemyVisualFactory.FindClip(clipName);
                    Animator animator = instance.GetComponentInChildren<Animator>();
                    if (clip != null && animator != null) EmberdelveFirstPersonArms.Evaluate(animator, clip, clip.length * Mathf.Clamp01(time));
                }

                GameObject floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
                floor.transform.SetParent(root.transform, false);
                floor.transform.localScale = new Vector3(4f, 1f, 4f);
                Collider floorCollider = floor.GetComponent<Collider>();
                if (floorCollider != null) Object.DestroyImmediate(floorCollider);
                floor.GetComponent<Renderer>().sharedMaterial = AssetDatabase.LoadAssetAtPath<Material>("Assets/Emberdelve/Art/Materials/Stone_Floor.mat");

                // torch-like warm key from the front-left, cold fill from the back
                GameObject key = new GameObject("Key");
                key.transform.SetParent(root.transform, false);
                Light keyLight = key.AddComponent<Light>();
                keyLight.type = LightType.Directional;
                keyLight.color = new Color(1f, 0.72f, 0.48f);
                keyLight.intensity = 1.3f;
                key.transform.rotation = Quaternion.Euler(30f, yaw + 150f, 0f);

                GameObject fill = new GameObject("Fill");
                fill.transform.SetParent(root.transform, false);
                Light fillLight = fill.AddComponent<Light>();
                fillLight.type = LightType.Directional;
                fillLight.color = new Color(0.45f, 0.55f, 0.8f);
                fillLight.intensity = 0.35f;
                fill.transform.rotation = Quaternion.Euler(20f, yaw - 40f, 0f);

                GameObject camObject = new GameObject("PreviewCamera");
                camObject.transform.SetParent(root.transform, false);
                Camera camera = camObject.AddComponent<Camera>();
                camera.fieldOfView = fov;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(0.05f, 0.05f, 0.06f);
                camera.nearClipPlane = 0.1f;
                camera.farClipPlane = 80f;
                Quaternion look = Quaternion.Euler(6f, yaw, 0f);
                Vector3 aim = origin + Vector3.up * aimHeight;
                camObject.transform.SetPositionAndRotation(aim - look * Vector3.forward * distance, look);

                SwitchOffRetro(switched);

                RenderTexture target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                camera.targetTexture = target;
                camera.Render();
                RenderTexture.active = target;
                Texture2D texture = new Texture2D(width, height, TextureFormat.RGB24, false);
                texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                texture.Apply();
                Directory.CreateDirectory(Path.GetDirectoryName(pngPath));
                File.WriteAllBytes(pngPath, texture.EncodeToPNG());
                RenderTexture.active = null;
                camera.targetTexture = null;
                Object.DestroyImmediate(target);
                Object.DestroyImmediate(texture);
                return pngPath;
            }
            finally
            {
                for (int i = 0; i < switched.Count; i++) if (switched[i] != null) switched[i].SetActive(true);
                if (previous.IsValid()) SceneManager.SetActiveScene(previous);
                EditorSceneManager.CloseScene(preview, true);
            }
        }

        private static void SwitchOffRetro(List<ScriptableRendererFeature> switched)
        {
            UniversalRenderPipelineAsset asset = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (asset == null) return;

            System.Reflection.FieldInfo field = typeof(UniversalRenderPipelineAsset).GetField("m_RendererDataList", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            ScriptableRendererData[] datas = field != null ? field.GetValue(asset) as ScriptableRendererData[] : null;
            if (datas == null) return;

            for (int d = 0; d < datas.Length; d++)
            {
                if (datas[d] == null) continue;
                for (int f = 0; f < datas[d].rendererFeatures.Count; f++)
                {
                    ScriptableRendererFeature feature = datas[d].rendererFeatures[f];
                    if (feature != null && feature.isActive && (feature.name.Contains("Retro") || feature.name == "FullscreenMask" || feature.name == "ScreenStencil"))
                    {
                        switched.Add(feature);
                        feature.SetActive(false);
                    }
                }
            }
        }
    }
}
