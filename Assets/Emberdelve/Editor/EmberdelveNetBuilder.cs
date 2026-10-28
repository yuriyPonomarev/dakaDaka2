using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Emberdelve.EditorTools
{
    /// <summary>
    /// Creates the tiny Emberdelve_NetBoot scene used by network proof builds. NGO does not need a
    /// NetworkManager in a scene (NetBootstrap creates it from code), so this scene is just an empty stage
    /// with a camera. It is never added to the editor build settings: the proof build passes it explicitly
    /// (EmberdelveWindowsBuild.BuildNetProofPlayer), so the regular lobby-first builds are unchanged.
    /// </summary>
    internal static class EmberdelveNetBuilder
    {
        public const string NetBootScenePath = EmberdelvePaths.Scenes + "/Emberdelve_NetBoot.unity";
        private const string Tag = "[Emberdelve][NetBuilder]";

        [MenuItem("Emberdelve/Net/Build NetBoot Scene")]
        public static void BuildNetBootScene()
        {
            if (CreateScene(out string error)) Debug.Log(Tag + " PASS scene=" + NetBootScenePath);
            else Debug.LogError(Tag + " FAIL " + error);

            if (Application.isBatchMode) EditorApplication.Exit(error == null ? 0 : 1);
        }

        /// <summary>Creates the scene only when the saved file is missing.</summary>
        public static bool EnsureNetBootScene(out string error)
        {
            error = null;
            if (File.Exists(NetBootScenePath)) return true;
            return CreateScene(out error);
        }

        private static bool CreateScene(out string error)
        {
            error = null;
            try
            {
                EmberdelvePaths.EnsureFolders();

                // An untitled loaded scene makes Additive creation fail, so batch mode or any untitled scene uses Single.
                bool hasUntitled = false;
                bool hasUntitledDirty = false;
                for (int i = 0; i < SceneManager.sceneCount; i++)
                {
                    Scene loaded = SceneManager.GetSceneAt(i);
                    if (!string.IsNullOrEmpty(loaded.path)) continue;
                    hasUntitled = true;
                    if (loaded.isDirty) hasUntitledDirty = true;
                }

                bool useSingle = Application.isBatchMode || hasUntitled;
                if (!Application.isBatchMode && hasUntitledDirty
                    && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                {
                    error = "cancelled";
                    return false;
                }

                // Additive otherwise, so the user's saved open scenes are never discarded.
                Scene scene = EditorSceneManager.NewScene(
                    NewSceneSetup.EmptyScene,
                    useSingle ? NewSceneMode.Single : NewSceneMode.Additive);

                GameObject cameraObject = new GameObject("NetBootCamera");
                SceneManager.MoveGameObjectToScene(cameraObject, scene);
                cameraObject.tag = "MainCamera";
                Camera camera = cameraObject.AddComponent<Camera>();
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Color.black;
                cameraObject.transform.position = new Vector3(0f, 1f, -10f);

                bool saved = EditorSceneManager.SaveScene(scene, NetBootScenePath);
                if (!useSingle) EditorSceneManager.CloseScene(scene, true); // the last loaded scene cannot be closed
                if (!saved)
                {
                    error = "SaveScene returned false for " + NetBootScenePath;
                    return false;
                }

                AssetDatabase.ImportAsset(NetBootScenePath, ImportAssetOptions.ForceSynchronousImport);
                return File.Exists(NetBootScenePath);
            }
            catch (System.Exception e)
            {
                error = e.Message;
                return false;
            }
        }
    }
}
