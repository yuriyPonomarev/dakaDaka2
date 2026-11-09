using System.IO;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Emberdelve.Player;
namespace Emberdelve.EditorTools
{
    public static class EmberdelveGripReview
    {
        public static void Capture()
        {
            UnityEditor.SceneManagement.EditorSceneManager.OpenScene("Assets/Emberdelve/Scenes/Lobby.unity");
            const string folder = "Logs/grip-review";
            Directory.CreateDirectory(folder);
            var files = new List<string>();
            foreach (var id in new[] { "ashen_short_sword", "arming_sword", "two_handed_sword" })
            {
                string path = folder + "/" + id + ".png";
                EmberdelveViewmodelPreview.FirstPersonPattern(path, id, null, null, -1, 0, 70, 960, 720, false);
                files.Add(path);
            }
            foreach (float roll in new[] { 90f, -90f, 180f })
            {
                string path = folder + "/sword-roll-" + roll + ".png";
                EmberdelveViewmodelPreview.Capture(path, 960, 720, 70, false, (root, camera) =>
                {
                    camera.transform.SetPositionAndRotation(EmberdelveViewmodelPreview.Origin + Vector3.up * 1.66f, Quaternion.identity);
                    var set = WeaponViewModelSet.Load();
                    var arms = (GameObject)PrefabUtility.InstantiatePrefab(set.armsPrefab);
                    arms.transform.SetParent(camera.transform, false);
                    var driver = arms.GetComponent<FirstPersonArmsDriver>();
                    driver.Bind(null, camera, set);
                    driver.SetWeapons(EmberdelveViewmodelPreview.FindWeapon("ashen_short_sword"), null);
                    EmberdelveViewmodelPreview.PoseArms(arms, driver, camera, null, -1, 0);
                    driver.GripRight.localRotation *= Quaternion.AngleAxis(roll, Vector3.up);
                    EmberdelveViewmodelPreview.PoseArms(arms, driver, camera, null, -1, 0);
                });
                files.Add(path);
            }
            foreach (float draw in new[] { 0f, 1f })
            {
                string path = folder + "/bow-" + draw + ".png";
                EmberdelveViewmodelPreview.Capture(path, 960, 720, 70, false, (root, camera) =>
                {
                    camera.transform.SetPositionAndRotation(EmberdelveViewmodelPreview.Origin + Vector3.up * 1.66f, Quaternion.identity);
                    var set = WeaponViewModelSet.Load();
                    var arms = (GameObject)PrefabUtility.InstantiatePrefab(set.armsPrefab);
                    arms.transform.SetParent(camera.transform, false);
                    var driver = arms.GetComponent<FirstPersonArmsDriver>();
                    driver.Bind(null, camera, set);
                    var weapon = EmberdelveViewmodelPreview.FindWeapon("hollow_bow");
                    driver.SetWeapons(weapon, null);
                    var visual = WeaponVisualBuilder.Build(weapon, camera.transform);
                    driver.SetVisual(visual);
                    EmberdelveViewmodelPreview.PoseArms(arms, driver, camera, null, -1, 0);
                    driver.PoseBow(camera.transform, visual.GetComponentInChildren<BowPresentation>(), draw);
                });
                files.Add(path);
            }
            EmberdelveViewmodelPreview.Sheet(folder + "/sheet.png", files, 3, 960, 720);
            Debug.Log("[GripReview] rendered " + files.Count + " poses");
        }
    }
}
