using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Emberdelve.Data;
using Emberdelve.Player;

namespace Emberdelve.EditorTools
{
    /// <summary>Edit-mode review frames of the shield block and a chained sword combo, posed the way the game drives the arms.</summary>
    public static class EmberdelveComboReview
    {
        public const string Folder = "Logs/review/combo";

        [MenuItem("Emberdelve/Debug/Render Combo And Block Review (Edit Mode)")]
        public static void Render()
        {
            if (EditorApplication.isPlaying) { Debug.LogError("[ComboReview] leave Play mode first"); return; }
            WeaponData sword = EmberdelveViewmodelPreview.FindWeapon("ashen_short_sword");
            WeaponData shield = EmberdelveViewmodelPreview.FindWeapon("emberbulwark_shield");
            var files = new List<string>();
            // Block: rest then raised (the second frame is the eased entry halfway).
            files.Add(EmberdelveViewmodelPreview.FirstPersonPattern(Folder + "/rest.png", sword.weaponId, shield.weaponId, null, -1f, 0f, 70f, 640, 360));
            files.Add(EmberdelveViewmodelPreview.FirstPersonPattern(Folder + "/block.png", sword.weaponId, shield.weaponId, null, -1f, 1f, 70f, 640, 360));
            // Combo: every step from where the previous strike ended, a frame every 0.1 s.
            Vector3 startBase = FirstPersonArmsDriver.PatternRest;
            Quaternion startRotation = WeaponVisualRig.ReadyRotation;
            int n = 0;
            for (int step = 0; step < sword.comboSteps.Length; step++)
            {
                ComboStep cs = sword.comboSteps[step];
                SwingPatternData pattern = sword.swingPatterns[cs.patternIndex];
                SwingTiming timing = pattern.TimingFor(cs.windupScale, cs.recoveryScale, step == sword.comboSteps.Length - 1 ? sword.comboFinisherRecovery : 0f);
                for (float t = 0f; t < timing.Total; t += 0.1f)
                {
                    string path = Folder + "/combo-" + (n++).ToString("00") + "-s" + step + ".png";
                    Vector3 sb = startBase; Quaternion sr = startRotation;
                    float time = t;
                    EmberdelveViewmodelPreview.Capture(path, 640, 360, 70f, true, (root, camera) =>
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
                        WeaponVisualRig.SamplePresentation(pattern, timing, time, sb, sr, false, out Vector3 p, out Quaternion r);
                        if (time > timing.ActiveEnd) { SwingPose end = pattern.Evaluate(timing.ActiveEnd, timing); p = end.Base; r = end.LocalRotation; }
                        driver.Pose(camera.transform, camera, p, r, 0f, Vector3.zero, Vector3.zero, swingBlend: 1f);
                    }, 0.05f);
                    files.Add(path);
                }
                SwingPose last = pattern.Evaluate(timing.ActiveEnd, timing);
                startBase = last.Base; startRotation = last.LocalRotation;
            }
            EmberdelveViewmodelPreview.Sheet(Folder + "/sheet.png", files, 4, 640, 360);
            Debug.Log("[ComboReview] rendered " + files.Count + " frames to " + Folder);
        }
    }
}
