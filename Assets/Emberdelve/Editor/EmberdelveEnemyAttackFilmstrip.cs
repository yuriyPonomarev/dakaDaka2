using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Emberdelve.EditorTools
{
    /// <summary>
    /// Review only: poses the shipped enemy visual prefabs exactly as EnemyVisualDriver does in play (their own controller,
    /// Play("Attack", 0, t) then Update(0)) and renders the frames to Logs/review/enemy-attack. Nothing is saved.
    /// </summary>
    public static class EmberdelveEnemyAttackFilmstrip
    {
        static readonly float[] Times = { -1f, 0f, 0.15f, 0.3f, 0.385f, 0.46f, 0.54f, 0.62f, 0.69f, 0.8f, 0.89f, -2f };

        [MenuItem("Emberdelve/Visual/Review Enemy Attack Filmstrip")]
        public static void Render()
        {
            string folder = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Logs", "review", "enemy-attack"));
            Directory.CreateDirectory(folder);
            foreach (string f in Directory.GetFiles(folder, "*.png")) File.Delete(f);
            string[] variants = { "Minion", "Rogue", "Warrior" };
            Scene previous = SceneManager.GetActiveScene();
            Scene preview = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            try
            {
                SceneManager.SetActiveScene(preview);
                Vector3 origin = new Vector3(4000f, 0f, 4000f);
                var animators = new Animator[variants.Length];
                for (int i = 0; i < variants.Length; i++)
                {
                    GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(EmberdelveEnemyVisualFactory.PrefabPath(variants[i]));
                    if (prefab == null) continue;
                    GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, preview);
                    instance.transform.position = origin + new Vector3((i - 1) * 1.8f, 0f, 0f);
                    animators[i] = instance.GetComponentInChildren<Animator>();
                    if (animators[i] != null) { animators[i].applyRootMotion = false; animators[i].Rebind(); animators[i].Update(0f); }
                }
                GameObject floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
                floor.transform.position = origin; floor.transform.localScale = new Vector3(3f, 1f, 3f);
                Object.DestroyImmediate(floor.GetComponent<Collider>());
                var key = new GameObject("Key").AddComponent<Light>(); key.type = LightType.Directional; key.intensity = 1.3f;
                key.color = new Color(1f, .78f, .58f); key.transform.rotation = Quaternion.Euler(35f, 230f, 0f);
                var fill = new GameObject("Fill").AddComponent<Light>(); fill.type = LightType.Directional; fill.intensity = .45f;
                fill.color = new Color(.5f, .6f, .85f); fill.transform.rotation = Quaternion.Euler(20f, 60f, 0f);
                // Grid lines on the floor make sliding feet and a moving pelvis visible frame to frame.
                for (int g = -6; g <= 6; g++)
                {
                    var line = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    Object.DestroyImmediate(line.GetComponent<Collider>());
                    line.transform.position = origin + new Vector3(0f, .002f, g * .5f); line.transform.localScale = new Vector3(8f, .002f, .01f);
                }
                var cameraObject = new GameObject("Cam"); Camera camera = cameraObject.AddComponent<Camera>();
                camera.fieldOfView = 34f; camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(.06f, .06f, .07f);
                var target = new RenderTexture(960, 540, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                camera.targetTexture = target;
                var texture = new Texture2D(960, 540, TextureFormat.RGB24, false);
                foreach (float yaw in new[] { 200f, 110f })
                {
                    Quaternion look = Quaternion.Euler(8f, yaw, 0f);
                    cameraObject.transform.SetPositionAndRotation(origin + Vector3.up * 1f - look * Vector3.forward * 8f, look);
                    for (int t = 0; t < Times.Length; t++)
                    {
                        for (int i = 0; i < animators.Length; i++)
                        {
                            Animator a = animators[i]; if (a == null) continue;
                            if (Times[t] < 0f) { a.Play("Idle", 0, Times[t] < -1.5f ? .5f : 0f); a.Update(0f); }
                            else { a.SetFloat(Emberdelve.AI.EnemyVisualDriver.AttackSpeedParameter, 0f); a.Play("Attack", 0, Times[t]); a.Update(0f); }
                        }
                        camera.Render();
                        RenderTexture.active = target;
                        texture.ReadPixels(new Rect(0, 0, 960, 540), 0, 0); texture.Apply();
                        RenderTexture.active = null;
                        string label = Times[t] < 0f ? "idle" : Times[t].ToString("0.000");
                        File.WriteAllBytes(Path.Combine(folder, "yaw" + (int)yaw + "_" + t.ToString("00") + "_" + label + ".png"), texture.EncodeToPNG());
                    }
                }
                camera.targetTexture = null; Object.DestroyImmediate(target); Object.DestroyImmediate(texture);
                Debug.Log("[EnemyAttackFilmstrip] wrote frames to " + folder);
            }
            finally
            {
                if (previous.IsValid()) SceneManager.SetActiveScene(previous);
                EditorSceneManager.CloseScene(preview, true);
            }
        }
    }
}
