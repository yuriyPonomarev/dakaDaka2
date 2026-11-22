using System;
using System.IO;
using Emberdelve.Feedback;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Emberdelve.EditorTools
{
    /// <summary>Lobby-only reuse of the imported carved bowl and its authored stone support.</summary>
    public static class LobbyMeshyBraziers
    {
        private const string GroupName = "Lobby_Meshy_Braziers";
        private const string BowlPath = "Assets/Emberdelve/Art/Models/AI/Meshy/CommunityRuins/StoneBrazier/StoneBrazier.prefab";
        private const string PedestalPath = "Assets/Emberdelve/Art/Generated/WeatheredBrazierPedestal/WeatheredPedestal.prefab";
        private const float PedestalTop = .9f;
        private const float BowlTop = 1.4f;

        internal static void RequireWallTorchSource()
        {
            const string path = "Assets/Emberdelve/Art/Models/AI/Meshy/MapDressing/WallTorch/WallTorch.prefab";
            var source = RequireVisualPrefab(path);
            if (source.transform.Find("Flame_Socket") == null)
                throw new InvalidOperationException("The lobby Meshy torch requires its authored Flame_Socket.");
        }

        internal static void Apply(Transform room)
        {
            if (room == null || room.name != "Lobby_Room")
                throw new InvalidOperationException("Expected the existing Lobby_Room.");
            var bowl = RequireVisualPrefab(BowlPath);
            var pedestal = RequireVisualPrefab(PedestalPath);
            // Existing sources only: do not let the flame factory generate a missing shared material.
            foreach (string path in new[] { "Assets/Emberdelve/Art/Materials/Torch_Flame.mat",
                         "Assets/Emberdelve/Art/Generated/Brazier_Smoke.mat" })
                if (AssetDatabase.LoadAssetAtPath<Material>(path) == null)
                    throw new InvalidOperationException("Missing existing brazier material: " + path);

            var group = new GameObject(GroupName).transform;
            group.SetParent(room, false);
            try
            {
                // Original lobby composition, not claimed to reproduce decoded reference transforms.
                // Local reference: Tavern/Meshes/SM_StandingBrazier01 + MI_StandingBrazier02.
                // Fit the two silhouettes into the existing menu gaps using its saved camera;
                // uniform scale preserves the smaller eastern bowl and support proportions.
                Build(group, "West", new Vector3(-4.96f, 0f, 6.2f), 25f, 1f, bowl, pedestal);
                Build(group, "East", new Vector3(10.505f, 0f, 5.4f), -25f, .72f, bowl, pedestal);
                if (group.GetComponentsInChildren<Collider>(true).Length != 0)
                    throw new InvalidOperationException("Lobby braziers must remain visual-only.");
            }
            catch
            {
                Object.DestroyImmediate(group.gameObject);
                throw;
            }
            // Replace only this owned decoration group after the replacement is complete.
            foreach (Transform child in room)
                if (child != group && child.name == GroupName)
                {
                    Object.DestroyImmediate(child.gameObject);
                    break;
                }
        }

        private static GameObject RequireVisualPrefab(string path)
        {
            var source = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (source == null || source.GetComponentsInChildren<MeshRenderer>(true).Length == 0)
                throw new InvalidOperationException("Missing imported brazier visual: " + path);
            if (source.GetComponentsInChildren<Collider>(true).Length != 0
                || source.GetComponentsInChildren<MonoBehaviour>(true).Length != 0)
                throw new InvalidOperationException("Expected visual-only brazier source: " + path);
            return source;
        }

        private static void Build(Transform group, string side, Vector3 position, float yaw, float scale,
            GameObject bowl, GameObject pedestal)
        {
            var stand = new GameObject("Lobby_Brazier_" + side).transform;
            stand.SetParent(group, false);
            stand.localPosition = position;
            stand.localScale = Vector3.one * scale;
            Place(stand, pedestal, Vector3.zero, 0f);
            Place(stand, bowl, Vector3.up * PedestalTop, yaw);
            var fire = new GameObject("Brazier_Light");
            fire.transform.SetParent(stand, false);
            fire.transform.localPosition = Vector3.up * BowlTop;
            var light = fire.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = new Color(1f, .66f, .34f);
            light.intensity = 7.5f;
            light.range = 5f;
            light.shadows = LightShadows.None;
            fire.AddComponent<TorchFlicker>();
            EmberdelveTorchFactory.BuildBrazierFlame(fire.transform, false);
        }

        private static void Place(Transform parent, GameObject source, Vector3 position, float yaw)
        {
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(source, parent);
            instance.transform.localPosition = position;
            instance.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
            instance.transform.localScale = Vector3.one;
        }

        public const string PrefabPath = "Assets/Emberdelve/Prefabs/Environment/Lobby_Meshy_Braziers.prefab";

        /// <summary>Bake isolated asset contents; never enter, stop or modify the active Play scene.</summary>
        public static void BakePrefab()
        {
            RequirePrimary();
            if (!AssetDatabase.IsValidFolder("Assets/Emberdelve/Prefabs/Environment"))
                AssetDatabase.CreateFolder("Assets/Emberdelve/Prefabs", "Environment");
            Scene preview = EditorSceneManager.NewPreviewScene();
            try
            {
                var root = new GameObject("Lobby_Room");
                SceneManager.MoveGameObjectToScene(root, preview);
                Apply(root.transform);
                var group = root.transform.Find(GroupName).gameObject;
                var saved = PrefabUtility.SaveAsPrefabAsset(group, PrefabPath);
                if (saved == null) throw new InvalidOperationException("Could not save isolated lobby brazier prefab.");
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(saved, out string guid, out long rootId);
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(saved.transform, out string _, out long transformId);
                Debug.Log($"[LobbyMeshyBraziers] prefab={PrefabPath} guid={guid} rootFileID={rootId} transformFileID={transformId}");
            }
            finally { EditorSceneManager.ClosePreviewScene(preview); }
        }

        private static void RequirePrimary()
        {
            const string primary = "C:/Users/yrapa/OneDrive/Desktop/Unity_DakaDaka/DakaDaka_clean_handoff/Assets";
            if (!Path.GetFullPath(Application.dataPath).Replace('\\', '/').Equals(primary, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Author lobby only in DakaDaka_clean_handoff.");
        }

        /// <summary>Apply only the lobby decoration, without rebuilding the room, UI or other scenes.</summary>
        public static void RepairSavedLobby()
        {
            RequirePrimary();
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Leave the current Play session untouched; repair in Edit mode later.");
            if (SceneManager.GetSceneByPath(EmberdelvePaths.LobbyScenePath).isLoaded)
                throw new InvalidOperationException("Lobby is already open; repair requires an unopened saved lobby scene.");
            Scene scene = EditorSceneManager.OpenScene(EmberdelvePaths.LobbyScenePath, OpenSceneMode.Additive);
            try
            {
                Transform room = null;
                foreach (var root in scene.GetRootGameObjects())
                    if (root.name == "Lobby_Room") room = root.transform;
                Apply(room);
                EditorSceneManager.MarkSceneDirty(scene);
                if (!EditorSceneManager.SaveScene(scene)) throw new InvalidOperationException("Could not save lobby braziers.");
                Debug.Log("[LobbyMeshyBraziers] Saved two imported carved bowls on their existing stone pedestals.");
            }
            finally { EditorSceneManager.CloseScene(scene, true); }
        }
    }
}
