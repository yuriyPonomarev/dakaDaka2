using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;
using Emberdelve.Core;
using Emberdelve.InputLayer;

namespace Emberdelve.EditorTools
{
    internal static class EmberdelvePaths
    {
        public const string Root = "Assets/Emberdelve";
        public const string Data = Root + "/Data";
        public const string Materials = Root + "/Art/Materials";
        public const string Scenes = Root + "/Scenes";
        public const string InputAssetPath = Data + "/EmberdelveInputActions.inputactions";

        public const string DungeonScenePath = Scenes + "/" + GameConfig.LegacyDungeonSceneName + ".unity";
        public const string ModularDungeonScenePath = Scenes + "/" + GameConfig.DefaultDungeonSceneName + ".unity";
        public const string LobbyScenePath = Scenes + "/" + GameConfig.DefaultLobbySceneName + ".unity";

        public static void EnsureFolders()
        {
            CreateIfMissing(Root);
            CreateIfMissing(Root + "/Art");
            CreateIfMissing(Materials);
            CreateIfMissing(Data);
            CreateIfMissing(Scenes);
        }

        private static void CreateIfMissing(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;

            string parent = System.IO.Path.GetDirectoryName(path).Replace('\\', '/');
            string leaf = System.IO.Path.GetFileName(path);

            if (!AssetDatabase.IsValidFolder(parent)) CreateIfMissing(parent);
            AssetDatabase.CreateFolder(parent, leaf);
        }
    }

    internal static class EmberdelveProjectSetup
    {
        private static readonly string[] RequiredLayers =
        {
            GameConfig.LevelLayerName,
            GameConfig.PlayerLayerName,
            GameConfig.EnemyLayerName,
            GameConfig.LootLayerName,
            GameConfig.InteractLayerName,
            GameConfig.ProjectileLayerName
        };

        /// <summary>Adds the Emberdelve layers to ProjectSettings/TagManager.asset if absent.</summary>
        public static int EnsureLayers()
        {
            Object[] assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset");
            if (assets == null || assets.Length == 0)
            {
                Debug.LogError("[Emberdelve] Could not open TagManager.asset; layers were not changed.");
                return 0;
            }

            SerializedObject serialized = new SerializedObject(assets[0]);
            SerializedProperty layers = serialized.FindProperty("layers");
            if (layers == null || !layers.isArray) return 0;

            int added = 0;

            for (int r = 0; r < RequiredLayers.Length; r++)
            {
                string wanted = RequiredLayers[r];

                bool exists = false;
                for (int i = 0; i < layers.arraySize; i++)
                {
                    if (layers.GetArrayElementAtIndex(i).stringValue == wanted)
                    {
                        exists = true;
                        break;
                    }
                }

                if (exists) continue;

                // Slots 0-7 are engine built-ins, 21-26 belong to the template's Cockpit scene.
                for (int i = 8; i < layers.arraySize; i++)
                {
                    if (i >= 21 && i <= 26) continue;

                    SerializedProperty slot = layers.GetArrayElementAtIndex(i);
                    if (!string.IsNullOrEmpty(slot.stringValue)) continue;

                    slot.stringValue = wanted;
                    added++;
                    break;
                }
            }

            if (added > 0) serialized.ApplyModifiedProperties();
            return added;
        }

        /// <summary>
        /// Writes the .inputactions asset from EmberdelveInputFallback so the authored asset and the
        /// runtime fallback can never disagree. Existing files are left alone for user edits.
        /// </summary>
        public static InputActionAsset EnsureInputAsset()
        {
            InputActionAsset existing = AssetDatabase.LoadAssetAtPath<InputActionAsset>(EmberdelvePaths.InputAssetPath);
            if (existing != null) return existing;

            string json = EmberdelveInputFallback.BuildFileJson();
            System.IO.File.WriteAllText(EmberdelvePaths.InputAssetPath, json);
            AssetDatabase.ImportAsset(EmberdelvePaths.InputAssetPath, ImportAssetOptions.ForceSynchronousImport);

            InputActionAsset imported = AssetDatabase.LoadAssetAtPath<InputActionAsset>(EmberdelvePaths.InputAssetPath);

            if (imported == null)
            {
                Debug.LogWarning("[Emberdelve] Input actions asset could not be imported; components will use the runtime fallback.");
                return null;
            }

            return imported;
        }

        public static void RebuildInputAsset()
        {
            // Rewrites the generated asset from the fallback in place. The .meta is left alone on purpose:
            // the dungeon scene references the asset by GUID, and deleting it would silently drop that link.
            string json = EmberdelveInputFallback.BuildFileJson();
            System.IO.File.WriteAllText(EmberdelvePaths.InputAssetPath, json);
            AssetDatabase.ImportAsset(EmberdelvePaths.InputAssetPath, ImportAssetOptions.ForceSynchronousImport);
            AssetDatabase.Refresh();
            Debug.Log("[Emberdelve] Regenerated " + EmberdelvePaths.InputAssetPath + " from EmberdelveInputFallback.");
        }

        public static void EnsureBuildScenes()
        {
            List<EditorBuildSettingsScene> scenes = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);

            // Keep the legacy scene asset and any unrelated scenes, but never package Level01 as a game mode.
            scenes.RemoveAll(scene => scene.path == EmberdelvePaths.DungeonScenePath);
            if (!System.IO.File.Exists(EmberdelvePaths.ModularDungeonScenePath))
                Debug.LogError("[Emberdelve] Required Modular Crypts scene is missing; player builds must fail until it is authored.");
            else AddScene(scenes, EmberdelvePaths.ModularDungeonScenePath);
            if (System.IO.File.Exists(EmberdelvePaths.LobbyScenePath)) AddScene(scenes, EmberdelvePaths.LobbyScenePath);

            MoveSceneToIndex(scenes, EmberdelvePaths.LobbyScenePath, 0);
            MoveSceneToIndex(scenes, EmberdelvePaths.ModularDungeonScenePath, 1);

            EditorBuildSettings.scenes = scenes.ToArray();
        }

        private static void MoveSceneToIndex(List<EditorBuildSettingsScene> scenes, string path, int targetIndex)
        {
            int index = IndexOf(scenes, path);
            if (index < 0) return;
            EditorBuildSettingsScene scene = scenes[index];
            scenes.RemoveAt(index);
            scenes.Insert(Mathf.Clamp(targetIndex, 0, scenes.Count), scene);
        }

        private static int IndexOf(List<EditorBuildSettingsScene> scenes, string path)
        {
            for (int i = 0; i < scenes.Count; i++)
            {
                if (scenes[i].path == path) return i;
            }

            return -1;
        }

        private static void AddScene(List<EditorBuildSettingsScene> scenes, string path)
        {
            for (int i = 0; i < scenes.Count; i++)
            {
                if (scenes[i].path == path)
                {
                    scenes[i].enabled = true;
                    return;
                }
            }

            scenes.Insert(0, new EditorBuildSettingsScene(path, true));
        }
    }
}
