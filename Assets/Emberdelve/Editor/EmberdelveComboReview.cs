using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Player;

namespace Emberdelve.EditorTools
{
    /// <summary>Edit-mode review frames of the shield block and a chained sword combo, posed the way the game drives the arms.</summary>
    public static class EmberdelveComboReview
    {
        public const string Folder = "Logs/review/combo";

        /// <summary>Batch entry for the test mirror (a batch Editor starts in an unsaved scene, which the capture cannot add to).</summary>
        public static void RenderBatch()
        {
            if (EditorBuildSettings.scenes.Length > 0)
                UnityEditor.SceneManagement.EditorSceneManager.OpenScene(EditorBuildSettings.scenes[0].path, UnityEditor.SceneManagement.OpenSceneMode.Single);
            Render();
        }

        [MenuItem("Emberdelve/Debug/Render Combo And Block Review (Edit Mode)")]
        public static void Render()
        {
            if (EditorApplication.isPlaying) { Debug.LogError("[ComboReview] leave Play mode first"); return; }
            Render("ashen_short_sword", "emberbulwark_shield", "sheet");
            Render("arming_sword", null, "sheet-arming");
            Render("ember_rapier", null, "sheet-rapier");
            Render("two_handed_sword", null, "sheet-two");
            Render("pitch_torch", null, "sheet-torch");
        }

        private static void Render(string swordId, string shieldId, string sheet)
        {
            WeaponData sword = EmberdelveViewmodelPreview.FindWeapon(swordId);
            WeaponData shield = shieldId != null ? EmberdelveViewmodelPreview.FindWeapon(shieldId) : null;
            if (sword == null || sword.comboSteps == null) { Debug.LogWarning("[ComboReview] no weapon " + swordId); return; }
            string Folder = EmberdelveComboReview.Folder + "/" + swordId;
            var files = new List<string>();
            // Block: rest then raised (the second frame is the eased entry halfway).
            files.Add(EmberdelveViewmodelPreview.FirstPersonPattern(Folder + "/rest.png", sword.weaponId, shieldId, null, -1f, 0f, 70f, 640, 360));
            files.Add(EmberdelveViewmodelPreview.FirstPersonPattern(Folder + "/block.png", sword.weaponId, shieldId, null, -1f, 1f, 70f, 640, 360));
            // Combo: the steps chained the way a held attack chains them (each starts when the previous one ends), then half a
            // second with no swing. The driver's own blender (CutBlend) is stepped at 60 Hz, so a frame shows what the game shows
            // at that moment, including the change from one step to the next and the return to the ready pose.
            const float dt = 1f / 60f;
            float fov = 68f;
            GameConfig config = AssetDatabase.LoadAssetAtPath<GameConfig>("Assets/Emberdelve/Data/EmberdelveGameConfig.asset");
            if (config != null) fov = config.fieldOfView;
            var cut = new FirstPersonArmsDriver.CutBlend();
            FirstPersonArmsDriver.ReadyPoseFor(sword, null, out Vector3 readyHand, out Quaternion readyRotation);
            cut.SetReady(readyHand, readyRotation);
            int n = 0, tick = 0;
            float shieldLower = 0f, swingWeight = 0f;
            // A weapon without a chain (the torch) is swung three times in a row: its attack repeats as soon as the last one ends.
            bool chain = sword.comboSteps.Length > 0;
            int steps = chain ? sword.comboSteps.Length : 3;
            for (int step = 0; step <= steps; step++)
            {
                bool tail = step == steps;
                SwingPatternData pattern = null; SwingTiming timing = default;
                if (!tail && chain)
                {
                    ComboStep cs = sword.comboSteps[step];
                    pattern = sword.swingPatterns[cs.patternIndex];
                    timing = pattern.TimingFor(cs.windupScale, cs.recoveryScale, step == sword.comboSteps.Length - 1 ? sword.comboFinisherRecovery : 0f, cs.activeScale);
                }
                else if (!tail)
                {
                    pattern = sword.swingPatterns[0];
                    timing = pattern.Timing;
                }
                float length = tail ? 0.5f : timing.Total;
                for (float t = 0f; t < length; t += dt, tick++)
                {
                    FirstPersonArmsDriver.SwingView view = cut.Step(!tail, pattern, timing, t, sword.twoHanded, dt);
                    shieldLower = Mathf.MoveTowards(shieldLower, tail ? 0f : 1f, (tail ? FirstPersonArmsDriver.ShieldReturnRate : FirstPersonArmsDriver.ShieldDropRate) * dt);
                    swingWeight = Mathf.MoveTowards(swingWeight, tail ? 0f : 1f, (tail ? 6f : 10f) * dt);
                    if (tick % 2 != 0) continue;   // a frame every 1/30 s: a strike lasts 0.1-0.2 s
                    string path = Folder + "/combo-" + (n++).ToString("000") + "-s" + step + ".png";
                    float lowerNow = shieldLower, weightNow = swingWeight;
                    EmberdelveViewmodelPreview.Capture(path, 640, 360, fov, true, (root, camera) =>
                    {
                        camera.transform.SetPositionAndRotation(EmberdelveViewmodelPreview.Origin + new Vector3(0f, 1.66f, 0f), Quaternion.identity);
                        WeaponViewModelSet set = WeaponViewModelSet.Load();
                        GameObject arms = (GameObject)PrefabUtility.InstantiatePrefab(set.armsPrefab);
                        arms.transform.SetParent(camera.transform, false);
                        FirstPersonArmsDriver driver = arms.GetComponent<FirstPersonArmsDriver>();
                        driver.Bind(null, camera, set);
                        driver.SetWeapons(sword, shield);
                        driver.SetBlock(false);
                        EmberdelveFirstPersonArms.PoseInstance(arms, AssetDatabase.LoadAssetAtPath<AnimationClip>(EmberdelveFirstPersonArms.ClipPath(FirstPersonArmsDriver.IdleState)), 0.3f);
                        driver.Pose(camera.transform, camera, FirstPersonArmsDriver.PatternRest, WeaponVisualRig.ReadyRotation, 0f, Vector3.zero, Vector3.zero,
                            swingBlend: weightNow, strikeBlend: 0f, shieldLower: lowerNow, swingTrail: 0f, swingView: view);
                    }, 0.05f);
                    files.Add(path);
                }
            }
            EmberdelveViewmodelPreview.Sheet(EmberdelveComboReview.Folder + "/" + sheet + ".png", files, 8, 640, 360);
            Debug.Log("[ComboReview] rendered " + files.Count + " frames to " + Folder);
        }
    }
}
