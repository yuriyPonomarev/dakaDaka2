using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace Emberdelve.EditorTools
{
    /// <summary>
    /// V3c review tool: renders weapon lineups and first-person views of the arms to PNG files in the open Editor without Play mode and
    /// without touching the open scenes (a temporary additive scene is created and closed again, nothing is saved; the retro fullscreen
    /// features are switched off for the render and restored, like <see cref="EmberdelveEnemyPreview"/>). Arm poses are made by sampling
    /// the clips of the viewmodel controller with AnimationClip.SampleAnimation.
    /// </summary>
    public static class EmberdelveViewmodelPreview
    {
        public static readonly Vector3 Origin = new Vector3(4000f, 0f, 4000f);

        /// <summary>Renders a temporary scene: <paramref name="populate"/> fills it (under the given root, at <see cref="Origin"/>) and places the camera.</summary>
        public static string Capture(string pngPath, int width, int height, float fov, bool floor, Action<Transform, Camera> populate, float near = 0.05f)
        {
            Scene previous = SceneManager.GetActiveScene();
            Scene preview = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            List<ScriptableRendererFeature> switched = new List<ScriptableRendererFeature>();
            try
            {
                SceneManager.SetActiveScene(preview);
                GameObject root = new GameObject("V3cPreview");
                root.transform.position = Origin;

                if (floor)
                {
                    GameObject plane = GameObject.CreatePrimitive(PrimitiveType.Plane);
                    plane.transform.SetParent(root.transform, false);
                    plane.transform.localScale = new Vector3(4f, 1f, 4f);
                    Collider collider = plane.GetComponent<Collider>();
                    if (collider != null) UnityEngine.Object.DestroyImmediate(collider);
                    plane.GetComponent<Renderer>().sharedMaterial = AssetDatabase.LoadAssetAtPath<Material>("Assets/Emberdelve/Art/Materials/Stone_Floor.mat");
                }

                GameObject key = new GameObject("Key");
                key.transform.SetParent(root.transform, false);
                Light keyLight = key.AddComponent<Light>();
                keyLight.type = LightType.Directional;
                keyLight.color = new Color(1f, 0.74f, 0.5f);
                keyLight.intensity = 1.5f;
                key.transform.rotation = Quaternion.Euler(35f, 200f, 0f);

                GameObject fill = new GameObject("Fill");
                fill.transform.SetParent(root.transform, false);
                Light fillLight = fill.AddComponent<Light>();
                fillLight.type = LightType.Directional;
                fillLight.color = new Color(0.55f, 0.62f, 0.85f);
                fillLight.intensity = 0.55f;
                fill.transform.rotation = Quaternion.Euler(25f, 20f, 0f);

                GameObject camObject = new GameObject("PreviewCamera");
                camObject.transform.SetParent(root.transform, false);
                Camera camera = camObject.AddComponent<Camera>();
                camera.fieldOfView = fov;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(0.06f, 0.06f, 0.07f);
                camera.nearClipPlane = near;
                camera.farClipPlane = 80f;

                populate(root.transform, camera);
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
                UnityEngine.Object.DestroyImmediate(target);
                UnityEngine.Object.DestroyImmediate(texture);
                return pngPath;
            }
            finally
            {
                for (int i = 0; i < switched.Count; i++) if (switched[i] != null) switched[i].SetActive(true);
                if (previous.IsValid()) SceneManager.SetActiveScene(previous);
                EditorSceneManager.CloseScene(preview, true);
            }
        }

        /// <summary>All normalised weapon prefabs side by side, upright, grip at the same height (face towards the camera).</summary>
        public static string WeaponLineup(string pngPath, bool rusty)
        {
            return Capture(pngPath, 1800, 700, 28f, false, (root, camera) =>
            {
                int n = EmberdelveWeaponModels.Specs.Length;
                for (int i = 0; i < n; i++)
                {
                    EmberdelveWeaponModels.Spec spec = EmberdelveWeaponModels.Specs[i];
                    GameObject prefab = EmberdelveWeaponModels.LoadPrefab(spec.Id);
                    if (prefab == null) continue;
                    GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                    instance.transform.SetParent(root, false);
                    instance.transform.localPosition = new Vector3((i - (n - 1) * 0.5f) * 0.75f, 0.85f, 0f);
                    if (rusty)
                    {
                        Dictionary<string, Material> map = EmberdelveWeaponModels.RustyMaterials(spec.Id);
                        Renderer[] renderers = instance.GetComponentsInChildren<Renderer>(true);
                        for (int r = 0; r < renderers.Length; r++)
                        {
                            Material[] mats = renderers[r].sharedMaterials;
                            for (int m = 0; m < mats.Length; m++) if (mats[m] != null && map.TryGetValue(mats[m].name, out Material rustyMaterial)) mats[m] = rustyMaterial;
                            renderers[r].sharedMaterials = mats;
                        }
                    }
                }

                camera.transform.SetPositionAndRotation(Origin + new Vector3(0f, 1.0f, 6.2f), Quaternion.Euler(0f, 180f, 0f));
            });
        }

        public static Emberdelve.Data.WeaponData FindWeapon(string weaponId)
        {
            string[] guids = AssetDatabase.FindAssets("t:WeaponData");
            for (int i = 0; i < guids.Length; i++)
            {
                Emberdelve.Data.WeaponData weapon = AssetDatabase.LoadAssetAtPath<Emberdelve.Data.WeaponData>(AssetDatabase.GUIDToAssetPath(guids[i]));
                if (weapon != null && weapon.weaponId == weaponId) return weapon;
            }

            return null;
        }

        /// <summary>
        /// The first-person view of the arms: the camera at the player's eye height (1.66 m) in a small stone room, the arms prefab dressed with the
        /// weapon of <paramref name="weaponId"/> (and the shield of <paramref name="shieldId"/> when given), posed by the clip of <paramref name="state"/> at
        /// <paramref name="normalized"/> (0..1 of the clip). Same camera values as the game (near 0.05).
        /// </summary>
        public static string FirstPerson(string pngPath, string state, float normalized, string weaponId, string shieldId, float fov = 70f, int width = 1280, int height = 720, bool wall = true)
        {
            return Capture(pngPath, width, height, fov, true, (root, camera) =>
            {
                Emberdelve.Player.WeaponViewModelSet set = Emberdelve.Player.WeaponViewModelSet.Load();
                camera.transform.SetPositionAndRotation(Origin + new Vector3(0f, 1.66f, 0f), Quaternion.identity);

                if (wall)
                {
                    GameObject back = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    back.transform.SetParent(root, false);
                    back.transform.localPosition = new Vector3(0f, 1.5f, 3.5f);
                    back.transform.localScale = new Vector3(8f, 3f, 0.3f);
                    Collider collider = back.GetComponent<Collider>();
                    if (collider != null) UnityEngine.Object.DestroyImmediate(collider);
                    Material wallMaterial = AssetDatabase.LoadAssetAtPath<Material>("Assets/Emberdelve/Art/Materials/Stone_Wall.mat");
                    if (wallMaterial != null) back.GetComponent<Renderer>().sharedMaterial = wallMaterial;
                }

                GameObject arms = (GameObject)PrefabUtility.InstantiatePrefab(set.armsPrefab);
                arms.transform.SetParent(camera.transform, false);
                Emberdelve.Player.FirstPersonArmsDriver driver = arms.GetComponent<Emberdelve.Player.FirstPersonArmsDriver>();
                driver.Bind(null, camera, set);
                driver.SetWeapons(FindWeapon(weaponId), string.IsNullOrEmpty(shieldId) ? null : FindWeapon(shieldId));

                bool shield = !string.IsNullOrEmpty(shieldId);
                AnimationClip clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(EmberdelveFirstPersonArms.ClipPath(state));
                AnimationClip offHand = shield ? EmberdelveEnemyVisualFactory.FindClip(EmberdelveFirstPersonArms.ShieldIdleLibrary) : null;
                if (clip != null) EmberdelveFirstPersonArms.PoseInstance(arms, clip, clip.length * Mathf.Clamp01(normalized), offHand);
            }, 0.05f);
        }

        /// <summary>
        /// V3d: a first-person frame of the arms driven the way the game drives them: the idle base pose of the controller, then the solver puts the
        /// right hand on the weapon pose of <paramref name="pattern"/> at <paramref name="time"/> (seconds into the swing; negative = the guard / rest
        /// pose) and the left hand on the shield. <paramref name="guard"/> raises the shield.
        /// </summary>
        public static string FirstPersonPattern(string pngPath, string weaponId, string shieldId, Emberdelve.Data.SwingPatternData pattern, float time, float guard, float fov = 70f, int width = 960, int height = 540, bool wall = true, Emberdelve.Data.SwingTiming? timing = null, bool swapFirst = false, GameObject armsPrefab = null)
        {
            return Capture(pngPath, width, height, fov, true, (root, camera) =>
            {
                camera.transform.SetPositionAndRotation(Origin + new Vector3(0f, 1.66f, 0f), Quaternion.identity);
                if (wall)
                {
                    GameObject back = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    back.transform.SetParent(root, false);
                    back.transform.localPosition = new Vector3(0f, 1.5f, 3.5f);
                    back.transform.localScale = new Vector3(8f, 3f, 0.3f);
                    Collider collider = back.GetComponent<Collider>();
                    if (collider != null) UnityEngine.Object.DestroyImmediate(collider);
                    Material wallMaterial = AssetDatabase.LoadAssetAtPath<Material>("Assets/Emberdelve/Art/Materials/Stone_Wall.mat");
                    if (wallMaterial != null) back.GetComponent<Renderer>().sharedMaterial = wallMaterial;
                }

                Emberdelve.Player.WeaponViewModelSet set = Emberdelve.Player.WeaponViewModelSet.Load();
                GameObject arms = (GameObject)PrefabUtility.InstantiatePrefab(armsPrefab != null ? armsPrefab : set.armsPrefab);
                arms.transform.SetParent(camera.transform, false);
                Emberdelve.Player.FirstPersonArmsDriver driver = arms.GetComponent<Emberdelve.Player.FirstPersonArmsDriver>();
                driver.Bind(null, camera, set);
                if (swapFirst) driver.SetWeapons(FindWeapon("ember_crossbow"), string.IsNullOrEmpty(shieldId) ? null : FindWeapon(shieldId));
                driver.SetWeapons(FindWeapon(weaponId), string.IsNullOrEmpty(shieldId) ? null : FindWeapon(shieldId));
                PoseArms(arms, driver, camera, pattern, time, guard, timing);
            }, 0.05f);
        }

        /// <summary>Poses an arms instance (edit mode): idle base pose, then the solver on the pattern pose at <paramref name="time"/> (negative: the rest pose).</summary>
        public static void PoseArms(GameObject arms, Emberdelve.Player.FirstPersonArmsDriver driver, Camera camera, Emberdelve.Data.SwingPatternData pattern, float time, float guard, Emberdelve.Data.SwingTiming? timing = null)
        {
            driver.SetBlock(guard > 0.5f);
            AnimationClip idle = AssetDatabase.LoadAssetAtPath<AnimationClip>(EmberdelveFirstPersonArms.ClipPath(Emberdelve.Player.FirstPersonArmsDriver.IdleState));
            EmberdelveFirstPersonArms.PoseInstance(arms, idle, 0.3f);
            if (guard > 0.5f && time < 0f)
            {
                Emberdelve.Player.GuardBladePose.ResolveRoot(camera.transform.position, Quaternion.LookRotation(camera.transform.forward, Vector3.up), true, out Vector3 gp, out Quaternion gr);
                Vector3 lp = camera.transform.InverseTransformPoint(gp);
                driver.Pose(camera.transform, camera, lp, Quaternion.Inverse(camera.transform.rotation) * gr, guard, Vector3.zero, Vector3.zero);
                return;
            }

            if (time < 0f)
            {
                driver.Pose(camera.transform, camera, Emberdelve.Player.FirstPersonArmsDriver.PatternRest,
                    Emberdelve.Player.WeaponVisualRig.ReadyRotation, guard, Vector3.zero, Vector3.zero);
                return;
            }

            if (pattern != null)
            {
                Emberdelve.Player.WeaponVisualRig.SamplePresentation(pattern, timing ?? pattern.Timing, time,
                    Emberdelve.Player.FirstPersonArmsDriver.PatternRest, Emberdelve.Player.WeaponVisualRig.ReadyRotation,
                    guard > .5f, out Vector3 position, out Quaternion rotation);
                driver.Pose(camera.transform, camera, position, rotation, guard, Vector3.zero, Vector3.zero, swingBlend: 1f);
            }
            else driver.Pose(camera.transform, camera, Emberdelve.Player.FirstPersonArmsDriver.PatternRest,
                Emberdelve.Player.WeaponVisualRig.ReadyRotation, guard, Vector3.zero, Vector3.zero);
        }

        /// <summary>Stitches PNG files into one contact sheet (columns x rows of equally sized frames).</summary>
        public static string Sheet(string sheetPath, List<string> files, int columns, int cellWidth, int cellHeight)
        {
            int rows = (files.Count + columns - 1) / columns;
            Texture2D sheet = new Texture2D(columns * cellWidth, rows * cellHeight, TextureFormat.RGB24, false);
            Color32[] clear = new Color32[sheet.width * sheet.height];
            sheet.SetPixels32(clear);
            for (int i = 0; i < files.Count; i++)
            {
                Texture2D t = new Texture2D(2, 2, TextureFormat.RGB24, false);
                t.LoadImage(File.ReadAllBytes(files[i]));
                RenderTexture rt = RenderTexture.GetTemporary(cellWidth, cellHeight, 0);
                Graphics.Blit(t, rt);
                RenderTexture.active = rt;
                Texture2D small = new Texture2D(cellWidth, cellHeight, TextureFormat.RGB24, false);
                small.ReadPixels(new Rect(0, 0, cellWidth, cellHeight), 0, 0);
                small.Apply();
                RenderTexture.active = null;
                RenderTexture.ReleaseTemporary(rt);
                int cx = (i % columns) * cellWidth;
                int cy = (rows - 1 - i / columns) * cellHeight;
                sheet.SetPixels(cx, cy, cellWidth, cellHeight, small.GetPixels());
                UnityEngine.Object.DestroyImmediate(t);
                UnityEngine.Object.DestroyImmediate(small);
            }

            sheet.Apply();
            Directory.CreateDirectory(Path.GetDirectoryName(sheetPath));
            File.WriteAllBytes(sheetPath, sheet.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(sheet);
            return sheetPath;
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
