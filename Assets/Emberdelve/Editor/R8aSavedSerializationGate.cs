using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Emberdelve.Player;
using Emberdelve.Stats;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Emberdelve.EditorTools
{
    /// <summary>
    /// Read-only saved-asset and scene serialization gate for the R8a assembly split.
    /// Enumerates every ScriptableObject asset under Assets/Emberdelve/Data, opens the two saved
    /// scenes additively, and never saves or mutates anything. Prints exactly one summary line:
    /// [Emberdelve][R8aSavedSerialization] PASS|FAIL checks=N failures=M (failure details are
    /// separate "detail" lines). In batch mode exits 0 on pass, 1 on failure.
    ///
    ///   Unity.exe -batchmode -nographics -projectPath . -logFile Logs/r8a-saved.log
    ///             -executeMethod Emberdelve.EditorTools.R8aSavedSerializationGate.Run
    /// </summary>
    public static class R8aSavedSerializationGate
    {
        private const string Tag = "[Emberdelve][R8aSavedSerialization]";
        private const string DataRoot = "Assets/Emberdelve/Data";
        private const string RuntimeAssemblyName = "Emberdelve.Runtime";
        private const string UiAssemblyName = "Emberdelve.UI";
        private const string RulesAssemblyName = "Emberdelve.Rules";
        private const string EditorAssemblyName = "Emberdelve.Editor";
        private const string LobbyScene = "Assets/Emberdelve/Scenes/Lobby.unity";
        private const string LevelScene = "Assets/Emberdelve/Scenes/Emberdelve_Level01.unity";

        private static int _checks;
        private static List<string> _failures;

        [MenuItem("Emberdelve/Verification/R8a Saved Serialization Gate")]
        public static void Run()
        {
            _checks = 0;
            _failures = new List<string>();
            SceneSetup[] originalSetup = null;
            bool sceneChanged = false;

            try
            {
                Assembly rulesAssembly = typeof(AttributeRules).Assembly;
                Assembly runtimeAssembly = typeof(PlayerRig).Assembly;
                Assembly uiAssembly = FindAssembly(UiAssemblyName);
                Assembly editorAssembly = typeof(R8aSavedSerializationGate).Assembly;
                Check(rulesAssembly.GetName().Name == RulesAssemblyName, "Rules identity is " + RulesAssemblyName);
                Check(runtimeAssembly.GetName().Name == RuntimeAssemblyName, "Runtime identity is " + RuntimeAssemblyName);
                Check(uiAssembly != null, "UI identity is " + UiAssemblyName);
                Check(editorAssembly.GetName().Name == EditorAssemblyName, "Editor identity is " + EditorAssemblyName);

                string[] guids = AssetDatabase.FindAssets("t:ScriptableObject", new[] { DataRoot });
                var paths = new List<string>();
                foreach (string guid in guids)
                {
                    string path = AssetDatabase.GUIDToAssetPath(guid);
                    if (path.EndsWith(".asset", StringComparison.OrdinalIgnoreCase) && !paths.Contains(path))
                        paths.Add(path);
                }
                paths.Sort(StringComparer.Ordinal);
                Check(paths.Count > 0, DataRoot + " enumerates at least one ScriptableObject asset (" + paths.Count + ")");

                foreach (string path in paths)
                {
                    UnityEngine.Object asset = AssetDatabase.LoadMainAssetAtPath(path);
                    if (asset == null)
                    {
                        Check(false, path + " loads its saved main asset");
                        continue;
                    }

                    Type type = asset.GetType();
                    Check(typeof(ScriptableObject).IsAssignableFrom(type) && type != typeof(ScriptableObject),
                        path + " loads as a concrete ScriptableObject (" + type.FullName + ")");
                    Check(type.Assembly.GetName().Name == RuntimeAssemblyName,
                        path + " type resolves from " + RuntimeAssemblyName + " (" + type.Assembly.GetName().Name + ")");
                }

                bool anyDirty = false;
                for (int i = 0; i < SceneManager.sceneCount; i++)
                    if (SceneManager.GetSceneAt(i).isDirty) anyDirty = true;
                Check(!anyDirty, "no unsaved open scenes would be disturbed");
                if (anyDirty) return;

                originalSetup = EditorSceneManager.GetSceneManagerSetup();
                var loaded = new Dictionary<string, Scene>();
                foreach (string path in new[] { LobbyScene, LevelScene })
                {
                    Scene scene = SceneManager.GetSceneByPath(path);
                    if (!scene.IsValid() || !scene.isLoaded)
                    {
                        sceneChanged = true;
                        scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
                    }
                    loaded[path] = scene;
                    bool open = scene.IsValid() && scene.isLoaded;
                    Check(open, path + " opens additively from saved YAML");
                    if (!open) continue;

                    int missing = 0;
                    foreach (GameObject root in scene.GetRootGameObjects())
                        foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                            missing += GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(t.gameObject);
                    Check(missing == 0, path + " has no missing MonoBehaviour scripts (" + missing + ")");
                }

                CheckComponent(loaded, LobbyScene, "Emberdelve.Flow.LobbyController", UiAssemblyName);
                CheckComponent(loaded, LevelScene, "Emberdelve.UI.HudController", UiAssemblyName);
                CheckComponent(loaded, LevelScene, "Emberdelve.UI.ResultScreenController", UiAssemblyName);
                CheckComponent(loaded, LevelScene, "Emberdelve.Player.PlayerRig", RuntimeAssemblyName);
            }
            catch (Exception exception)
            {
                Check(false, "gate execution raised " + exception.GetType().Name + ": " + exception.Message);
            }
            finally
            {
                if (sceneChanged && originalSetup != null)
                {
                    try
                    {
                        if (originalSetup.Length > 0)
                            EditorSceneManager.RestoreSceneManagerSetup(originalSetup);
                        else
                            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                        Check(true, "original editor scene setup restored without saving");
                    }
                    catch (Exception exception)
                    {
                        Check(false, "scene setup restore raised " + exception.GetType().Name + ": " + exception.Message);
                    }
                }

                int failureCount = _failures.Count;
                string line = Tag + " " + (failureCount == 0 ? "PASS" : "FAIL")
                    + " checks=" + _checks + " failures=" + failureCount;
                if (failureCount == 0) Debug.Log(line);
                else
                {
                    foreach (string failure in _failures) Debug.LogError(Tag + " detail: " + failure);
                    Debug.LogError(line);
                }

                if (Application.isBatchMode)
                    EmberdelveGateExit.Exit(failureCount == 0 ? 0 : 1);
            }
        }

        private static void Check(bool ok, string label)
        {
            _checks++;
            if (!ok) _failures.Add(label);
        }

        private static void CheckComponent(Dictionary<string, Scene> scenes, string scenePath,
            string typeName, string expectedAssembly)
        {
            Type type = FindType(typeName);
            Check(type != null, scenePath + " resolves component type " + typeName);
            if (type == null) return;

            Check(type.Assembly.GetName().Name == expectedAssembly,
                typeName + " resolves from " + expectedAssembly + " (" + type.Assembly.GetName().Name + ")");
            if (!scenes.TryGetValue(scenePath, out Scene scene) || !scene.IsValid() || !scene.isLoaded)
            {
                Check(false, scenePath + " is loaded for " + typeName + " lookup");
                return;
            }

            bool found = false;
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (MonoBehaviour component in root.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (component != null && component.GetType() == type) { found = true; break; }
                }
                if (found) break;
            }
            Check(found, scenePath + " contains a resolved " + typeName + " component");
        }

        private static Assembly FindAssembly(string name)
        {
            return AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == name);
        }

        private static Type FindType(string fullName)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type = assembly.GetType(fullName, false);
                if (type != null) return type;
            }
            return null;
        }
    }
}
