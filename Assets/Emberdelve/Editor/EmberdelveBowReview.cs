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

        [MenuItem("Emberdelve/Debug/Render Bow Review (Edit Mode)")]
        public static void Render()
        {
            if (EditorApplication.isPlaying) { Debug.LogError("[BowReview] leave Play mode first"); return; }
            WeaponData bowData = EmberdelveViewmodelPreview.FindWeapon("hollow_bow");
            if (bowData == null) { Debug.LogError("[BowReview] hollow_bow not found"); return; }

            var files = new List<string>();
            foreach ((string name, float draw, bool guard) shot in new[]
                     { ("rest", 0f, false), ("draw-050", 0.5f, false), ("draw-100", 1f, false), ("guard", 0f, true) })
            {
                string path = Folder + "/" + shot.name + ".png";
                float draw = shot.draw;
                bool guard = shot.guard;
                EmberdelveViewmodelPreview.Capture(path, 960, 540, 70f, true, (root, camera) =>
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
                    else driver.PoseBow(camera.transform, bow, draw);
                }, 0.05f);
                files.Add(path);
            }

            EmberdelveViewmodelPreview.Sheet(Folder + "/sheet.png", files, 2, 960, 540);
            Debug.Log("[BowReview] rendered " + files.Count + " frames to " + Folder);
        }
    }
}
