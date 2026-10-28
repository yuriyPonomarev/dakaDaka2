using System.IO;
using Emberdelve.Net;
using Emberdelve.Player;
using Unity.Netcode;
using Unity.Netcode.Components;
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
    ///
    /// U3A2 adds the network player prefab: <c>Assets/Emberdelve/Prefabs/Net/Emberdelve_NetPlayer.prefab</c>, derived from
    /// the Level01 scene rig (every current component and wiring) plus NetworkObject, NetworkTransform (server authority,
    /// interpolated, position + yaw only, no scale) and NetPlayerRig, and the small NetPrefabCatalog asset
    /// (<c>Assets/Emberdelve/Resources/Net/EmberdelveNetPrefabs.asset</c>) that NetBootstrap registers it from.
    /// </summary>
    internal static class EmberdelveNetBuilder
    {
        public const string NetBootScenePath = EmberdelvePaths.Scenes + "/Emberdelve_NetBoot.unity";
        private const string Tag = "[Emberdelve][NetBuilder]";

        public const string PrefabFolder = EmberdelvePaths.Root + "/Prefabs/Net";
        public const string PlayerPrefabPath = PrefabFolder + "/Emberdelve_NetPlayer.prefab";
        public const string CatalogFolder = EmberdelvePaths.Root + "/Resources/Net";
        public const string CatalogPath = CatalogFolder + "/EmberdelveNetPrefabs.asset";

        [MenuItem("Emberdelve/Net/Build NetBoot Scene")]
        public static void BuildNetBootScene()
        {
            if (CreateScene(out string error)) Debug.Log(Tag + " PASS scene=" + NetBootScenePath);
            else Debug.LogError(Tag + " FAIL " + error);

            if (Application.isBatchMode) EmberdelveGateExit.Exit(error == null ? 0 : 1);
        }

        [MenuItem("Emberdelve/Net/Build Net Player Prefab")]
        public static void BuildNetPlayerPrefab()
        {
            string error;
            bool ok = BuildPlayerPrefab(out error);
            if (ok) Debug.Log(Tag + " PASS prefab=" + PlayerPrefabPath + " catalog=" + CatalogPath);
            else Debug.LogError(Tag + " FAIL " + error);

            if (Application.isBatchMode) EmberdelveGateExit.Exit(ok ? 0 : 1);
        }

        /// <summary>Creates the player prefab and its catalog only when either is missing.</summary>
        public static bool EnsureNetPlayerPrefab(out string error)
        {
            error = null;
            if (File.Exists(PlayerPrefabPath) && File.Exists(CatalogPath)) return true;
            return BuildPlayerPrefab(out error);
        }

        /// <summary>
        /// (Re)generates the network player prefab from the saved Level01 scene's PlayerRig and points the catalog at it. The
        /// scene is only read: it is opened additively when it is not open already and closed again, and the rig is saved as a
        /// prefab straight from the scene object, so nothing in the scene changes. Requires the Level01 scene to exist (build
        /// the slice first).
        /// </summary>
        public static bool BuildPlayerPrefab(out string error)
        {
            error = null;
            Scene scene = default;
            bool openedHere = false;

            try
            {
                if (!File.Exists(EmberdelvePaths.DungeonScenePath))
                {
                    error = "Level01 scene is missing (" + EmberdelvePaths.DungeonScenePath + "); build the slice first";
                    return false;
                }

                scene = SceneManager.GetSceneByPath(EmberdelvePaths.DungeonScenePath);
                if (!scene.IsValid() || !scene.isLoaded)
                {
                    scene = EditorSceneManager.OpenScene(EmberdelvePaths.DungeonScenePath, OpenSceneMode.Additive);
                    openedHere = true;
                }

                PlayerRig sceneRig = null;
                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    sceneRig = root.GetComponentInChildren<PlayerRig>(true);
                    if (sceneRig != null) break;
                }

                if (sceneRig == null)
                {
                    error = "no PlayerRig in " + EmberdelvePaths.DungeonScenePath;
                    return false;
                }

                EnsureFolder(PrefabFolder);
                EnsureFolder(CatalogFolder);

                bool saved;
                PrefabUtility.SaveAsPrefabAsset(sceneRig.gameObject, PlayerPrefabPath, out saved);
                if (!saved)
                {
                    error = "SaveAsPrefabAsset failed for " + PlayerPrefabPath;
                    return false;
                }

                GameObject contents = PrefabUtility.LoadPrefabContents(PlayerPrefabPath);
                try
                {
                    ConfigureNetworkComponents(contents);
                    PrefabUtility.SaveAsPrefabAsset(contents, PlayerPrefabPath, out saved);
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(contents);
                }

                if (!saved)
                {
                    error = "saving the networked prefab failed for " + PlayerPrefabPath;
                    return false;
                }

                AssetDatabase.ImportAsset(PlayerPrefabPath, ImportAssetOptions.ForceSynchronousImport);
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPrefabPath);
                if (prefab == null)
                {
                    error = "prefab did not load back from " + PlayerPrefabPath;
                    return false;
                }

                NetPrefabCatalog catalog = AssetDatabase.LoadAssetAtPath<NetPrefabCatalog>(CatalogPath);
                if (catalog == null)
                {
                    catalog = ScriptableObject.CreateInstance<NetPrefabCatalog>();
                    AssetDatabase.CreateAsset(catalog, CatalogPath);
                }

                catalog.SetPlayerPrefab(prefab);
                EditorUtility.SetDirty(catalog);
                AssetDatabase.SaveAssets();
                return true;
            }
            catch (System.Exception e)
            {
                error = e.Message;
                return false;
            }
            finally
            {
                if (openedHere && scene.IsValid() && scene.isLoaded) EditorSceneManager.CloseScene(scene, true);
            }
        }

        /// <summary>
        /// The networked half of the prefab. NetworkTransform: server authority (its default; the AuthorityMode field is not
        /// assigned so the build does not depend on its name), position and yaw replicated, scale never, interpolated, so
        /// observers see the server's CharacterController result and the owner cannot place itself.
        /// </summary>
        private static void ConfigureNetworkComponents(GameObject root)
        {
            if (root.GetComponent<NetworkObject>() == null) root.AddComponent<NetworkObject>();

            NetworkTransform transform = root.GetComponent<NetworkTransform>();
            if (transform == null) transform = root.AddComponent<NetworkTransform>();
            transform.SyncPositionX = true;
            transform.SyncPositionY = true;
            transform.SyncPositionZ = true;
            transform.SyncRotAngleX = false;
            transform.SyncRotAngleY = true;
            transform.SyncRotAngleZ = false;
            transform.SyncScaleX = false;
            transform.SyncScaleY = false;
            transform.SyncScaleZ = false;
            transform.Interpolate = true;

            if (root.GetComponent<NetPlayerRig>() == null) root.AddComponent<NetPlayerRig>();
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path).Replace('\\', '/');
            string leaf = Path.GetFileName(path);
            if (!AssetDatabase.IsValidFolder(parent)) EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, leaf);
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
