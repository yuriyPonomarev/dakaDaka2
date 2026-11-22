using System;
using Emberdelve.Interaction;
using UnityEditor;
using UnityEngine;

namespace Emberdelve.EditorTools
{
    public static class DungeonRoomExpansionReview
    {
        public static void RebuildBatch()
        {
            RebuildAssetsBatch();
            RenderBatch();
        }

        public static void RebuildAssetsBatch()
        {
            if (!Application.isBatchMode) throw new InvalidOperationException("Use the isolated batch mirror");
            DungeonDoorAuthoring.Build();
            DungeonModuleContentBuilder.BuildCatalog();
            DungeonModuleContentBuilder.BuildRoyaleDestinationCatalogs();
        }

        public static void RefreshDownloadedMaterials() => DownloadedCryptArchitecture.RefreshMaterials();

        public static void RenderBatch()
        {
            if (!Application.isBatchMode) throw new InvalidOperationException("Use the isolated batch mirror");
            UnityEditor.SceneManagement.EditorSceneManager.OpenScene("Assets/Emberdelve/Scenes/Emberdelve_NetBoot.unity");
            foreach (string theme in new[] { "", "Crypt/", "Infernal/" })
            {
                string prefix = theme.Length == 0 ? "" : theme.TrimEnd('/') + "_";
                string path = "Assets/Emberdelve/Prefabs/DungeonModules/" + theme + prefix + "Crypt_Hall_02.prefab";
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null) throw new InvalidOperationException(path);
                Capture(prefab, prefix + "gallery", new Vector3(-14.5f, 5.56f, -7.2f), new Vector3(-14.5f, 4.7f, 6), false);
                Capture(prefab, prefix + "lower", new Vector3(14.5f, .16f, -11.5f), new Vector3(14.5f, 1.2f, -4), false);
                Capture(prefab, prefix + "secret_closed", new Vector3(14.7f, 1.66f, 7.2f), new Vector3(14.7f, 1.6f, 15), false);
                Capture(prefab, prefix + "secret_open", new Vector3(14.7f, 1.66f, 7.2f), new Vector3(14.7f, 1.6f, 15), true);
                Capture(prefab, prefix + "nave", new Vector3(0, 1.66f, -15), new Vector3(0, 2.5f, 9), false);
            }
            Debug.Log("[RoomExpansion] Built all30 saved modules; rendered15 views in Logs/review/room-expansion");
        }

        private static void Capture(GameObject prefab, string name, Vector3 from, Vector3 to, bool opened)
        {
            EmberdelveViewmodelPreview.Capture("Logs/review/room-expansion/" + name + ".png", 1280, 720, 74, false, (root, camera) =>
            {
                var room = UnityEngine.Object.Instantiate(prefab, root, false);
                if (opened)
                    foreach (var door in room.GetComponentsInChildren<DungeonDoorInteractable>())
                        if (door.IsSecret) door.ToggleAuthoritatively();
                camera.transform.position = root.position + from;
                camera.transform.LookAt(root.position + to);
            });
        }
    }
}
