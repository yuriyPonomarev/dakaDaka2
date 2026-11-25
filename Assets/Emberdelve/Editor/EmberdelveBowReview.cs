using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Emberdelve.Data;
using Emberdelve.Player;

namespace Emberdelve.EditorTools
{
    /// <summary>
    /// Edit-mode review frames of the first-person bow: the arms and the bow as the game poses them at rest, half draw, full
    /// draw and in the bow guard. Written to Logs/review/bow (frames and a contact sheet). No Play mode, nothing is saved to assets.
    /// </summary>
    public static class EmberdelveBowReview
    {
        public const string Folder = "Logs/review/bow";

        /// <summary>Batch entry for the test mirror (a batch Editor starts in an unsaved scene).</summary>
        public static void RenderBatch()
        {
            if (EditorBuildSettings.scenes.Length > 0)
                UnityEditor.SceneManagement.EditorSceneManager.OpenScene(EditorBuildSettings.scenes[0].path, UnityEditor.SceneManagement.OpenSceneMode.Single);
            Render();
        }

        [MenuItem("Emberdelve/Debug/Render Bow Review (Edit Mode)")]
        public static void Render()
        {
            if (EditorApplication.isPlaying) { Debug.LogError("[BowReview] leave Play mode first"); return; }
            WeaponData bowData = EmberdelveViewmodelPreview.FindWeapon("hollow_bow");
            if (bowData == null) { Debug.LogError("[BowReview] hollow_bow not found"); return; }

            var files = new List<string>();
            // Rest and guard; the draw from the quiver to full; a reload.
            var shots = new List<(string name, float draw, bool guard, float reload)> { ("rest", 0f, false, -1f), ("guard", 0f, true, -1f) };
            foreach (float d in new[] { 0.03f, 0.07f, 0.12f, 0.17f, 0.22f, 0.25f, 0.40f, 0.60f, 0.80f, 1f })
                shots.Add(("draw-" + Mathf.RoundToInt(d * 100f).ToString("000"), d, false, -1f));
            foreach (float r in new[] { 0.08f, 0.20f, 0.36f, 0.48f, 0.56f, 0.64f, 0.72f, 0.82f, 0.95f })
                shots.Add(("reload-" + Mathf.RoundToInt(r * 100f).ToString("000"), 0f, false, r));
            float fov = 68f;
            Emberdelve.Core.GameConfig config = AssetDatabase.LoadAssetAtPath<Emberdelve.Core.GameConfig>("Assets/Emberdelve/Data/EmberdelveGameConfig.asset");
            if (config != null) fov = config.fieldOfView;
            foreach ((string name, float draw, bool guard, float reload) shot in shots)
            {
                string path = Folder + "/" + shot.name + ".png";
                float draw = shot.draw, reload = shot.reload;
                bool guard = shot.guard;
                EmberdelveViewmodelPreview.Capture(path, 960, 540, fov, true, (root, camera) =>
                {
                    camera.transform.SetPositionAndRotation(EmberdelveViewmodelPreview.Origin + new Vector3(0f, 1.66f, 0f), Quaternion.identity);
                    WeaponViewModelSet set = WeaponViewModelSet.Load();
                    GameObject arms = (GameObject)PrefabUtility.InstantiatePrefab(set.armsPrefab);
                    arms.transform.SetParent(camera.transform, false);
                    FirstPersonArmsDriver driver = arms.GetComponent<FirstPersonArmsDriver>();
                    driver.Bind(null, camera, set);
                    driver.SetWeapons(bowData, null);
                    WeaponVisualRig rig = WeaponVisualBuilder.Build(bowData, camera.transform, camera.transform);
                    driver.SetVisual(rig);
                    AnimationClip idle = AssetDatabase.LoadAssetAtPath<AnimationClip>(EmberdelveFirstPersonArms.ClipPath(FirstPersonArmsDriver.IdleState));
                    EmberdelveFirstPersonArms.PoseInstance(arms, idle, 0.3f);
                    rig.SetGuardHeld(guard);
                    rig.SetDrawProgress(draw);
                    BowPresentation bow = rig.GetComponentInChildren<BowPresentation>();
                    if (guard) driver.PoseBowGuard(camera.transform, bow);
                    else driver.PoseBow(camera.transform, bow, draw, reload);
                }, 0.05f);
                files.Add(path);
            }

            EmberdelveViewmodelPreview.Sheet(Folder + "/sheet.png", files, 4, 960, 540);
            Debug.Log("[BowReview] rendered " + files.Count + " frames to " + Folder);
        }
    }
}
