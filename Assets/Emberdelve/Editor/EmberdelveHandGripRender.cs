using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using Emberdelve.Player;

namespace Emberdelve.EditorTools
{
    /// <summary>Renders the first-person right hand on the sword and the torch: the player's view plus two close-ups of the grip.</summary>
    public static class EmberdelveHandGripRender
    {
        public const string Folder = "Logs/review/grips";

        [MenuItem("Emberdelve/Debug/Render Hand Grips (Edit Mode)")]
        public static void Render()
        {
            if (EditorApplication.isPlaying) { Debug.LogError("[HandGrips] leave Play mode first"); return; }
            Directory.CreateDirectory(Folder);
            var files = new List<string>();
            foreach (string id in new[] { "ashen_short_sword", "pitch_torch" })
            {
                for (int view = 0; view < 3; view++)
                {
                    string path = Folder + "/" + id + "-" + view + ".png";
                    int captured = view;
                    EmberdelveViewmodelPreview.Capture(path, 800, 600, captured == 0 ? 70f : 30f, false, (root, camera) =>
                    {
                        camera.transform.SetPositionAndRotation(EmberdelveViewmodelPreview.Origin + Vector3.up * 1.66f, Quaternion.identity);
                        var set = WeaponViewModelSet.Load();
                        var arms = (GameObject)PrefabUtility.InstantiatePrefab(set.armsPrefab);
                        arms.transform.SetParent(camera.transform, false);
                        var driver = arms.GetComponent<FirstPersonArmsDriver>();
                        driver.Bind(null, camera, set);
                        driver.SetWeapons(EmberdelveViewmodelPreview.FindWeapon(id), null);
                        EmberdelveViewmodelPreview.PoseArms(arms, driver, camera, null, -1, 0);
                        EmberdelveViewmodelPreview.PoseArms(arms, driver, camera, null, -1, 0);
                        if (captured == 0) return;
                        Transform hand = driver.Animator.GetBoneTransform(HumanBodyBones.RightHand);
                        arms.transform.SetParent(root, true);
                        Vector3 centre = hand.position;
                        Vector3 offset = captured == 1 ? camera.transform.right * .75f + Vector3.up * .15f : -camera.transform.forward * -.0f + camera.transform.forward * .9f + Vector3.up * .25f;
                        camera.transform.position = centre + offset;
                        camera.transform.LookAt(centre);
                        camera.nearClipPlane = .01f;
                    });
                    files.Add(path);
                }
            }
            EmberdelveViewmodelPreview.Sheet(Folder + "/sheet.png", files, 3, 800, 600);
            Debug.Log("[HandGrips] rendered " + files.Count + " views to " + Folder);
        }
    }
}
