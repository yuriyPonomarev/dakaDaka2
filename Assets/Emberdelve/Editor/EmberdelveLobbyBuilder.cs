using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Emberdelve.Core;
using Emberdelve.Feedback;
using Emberdelve.Flow;

namespace Emberdelve.EditorTools
{
    /// <summary>
    /// Safe-room scene: a small torch-lit stone room plus the class-select screen.
    /// The UI itself is built at runtime by LobbyController/LobbyScreenView, so the scene stays tiny.
    /// </summary>
    internal static class EmberdelveLobbyBuilder
    {
        public static Scene Build()
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            EmberdelveAssetLibrary.BuildAll();

            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.07f, 0.06f, 0.05f, 1f);
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Exponential;
            RenderSettings.fogColor = new Color(0.05f, 0.04f, 0.035f, 1f);
            RenderSettings.fogDensity = 0.03f;

            int levelLayer = LayerMask.NameToLayer(GameConfig.LevelLayerName);
            GameObject root = new GameObject("Lobby_Room");

            AddBox(root.transform, "Lobby_Floor", new Vector3(-11f, -0.5f, -8f), new Vector3(11f, 0f, 8f),
                EmberdelveMaterials.LobbyFloor, levelLayer);
            AddBox(root.transform, "Lobby_Ceiling", new Vector3(-11f, 3.4f, -8f), new Vector3(11f, 3.9f, 8f),
                EmberdelveMaterials.StoneCeiling, levelLayer);
            AddBox(root.transform, "Lobby_Wall_North", new Vector3(-11f, 0f, 8f), new Vector3(11f, 3.4f, 8.45f),
                EmberdelveMaterials.StoneWall, levelLayer);
            AddBox(root.transform, "Lobby_Wall_South", new Vector3(-11f, 0f, -8.45f), new Vector3(11f, 3.4f, 0f),
                EmberdelveMaterials.StoneWall, levelLayer);
            AddBox(root.transform, "Lobby_Wall_West", new Vector3(-11.45f, 0f, -8f), new Vector3(-11f, 3.4f, 8f),
                EmberdelveMaterials.StoneWall, levelLayer);
            AddBox(root.transform, "Lobby_Wall_East", new Vector3(11f, 0f, -8f), new Vector3(11.45f, 3.4f, 8f),
                EmberdelveMaterials.StoneWall, levelLayer);

            AddTorch(root.transform, new Vector3(-9f, 2.5f, 7f), 12f);
            AddTorch(root.transform, new Vector3(9f, 2.5f, 7f), 12f);
            AddTorch(root.transform, new Vector3(-9f, 2.5f, -7f), 10f);
            AddTorch(root.transform, new Vector3(9f, 2.5f, -7f), 10f);

            GameObject cameraGo = new GameObject("Lobby_Camera");
            cameraGo.tag = "MainCamera";
            cameraGo.transform.position = new Vector3(0f, 1.62f, -5.5f);
            cameraGo.transform.rotation = Quaternion.Euler(4f, 0f, 0f);

            Camera camera = cameraGo.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.015f, 0.013f, 0.012f, 1f);
            camera.nearClipPlane = 0.05f;
            camera.farClipPlane = 200f;
            camera.fieldOfView = 62f;

            GameObject master = new GameObject("Lobby_Master");
            LobbyController lobby = master.AddComponent<LobbyController>();

            SerializedObject serialized = new SerializedObject(lobby);
            SetProperty(serialized, "config", EmberdelveAssetLibrary.Config);
            SetProperty(serialized, "classes", EmberdelveAssetLibrary.Classes);
            serialized.ApplyModifiedPropertiesWithoutUndo();

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, EmberdelvePaths.LobbyScenePath);

            return scene;
        }

        private static void AddBox(Transform parent, string name, Vector3 min, Vector3 max, Material material, int layer)
        {
            GameObject box = GameObject.CreatePrimitive(PrimitiveType.Cube);
            box.name = name;
            box.transform.SetParent(parent, false);
            box.transform.position = (min + max) * 0.5f;
            box.transform.localScale = Vector3.Max(max - min, new Vector3(0.01f, 0.01f, 0.01f));
            if (layer >= 0) box.layer = layer;

            Renderer renderer = box.GetComponent<Renderer>();
            if (renderer != null && material != null) renderer.sharedMaterial = material;
        }

        private static void AddTorch(Transform parent, Vector3 position, float intensity)
        {
            GameObject torch = new GameObject("Lobby_Torch");
            torch.transform.SetParent(parent, false);
            torch.transform.position = position;

            Light light = torch.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = new Color(1f, 0.66f, 0.34f, 1f);
            light.range = 12f;
            light.intensity = intensity;
            light.shadows = LightShadows.None;

            torch.AddComponent<TorchFlicker>();
        }

        private static void SetProperty(SerializedObject serialized, string name, Object value)
        {
            SerializedProperty property = serialized.FindProperty(name);
            if (property == null)
            {
                Debug.LogWarning("[Emberdelve] LobbyController has no serialized field '" + name + "'.");
                return;
            }

            property.objectReferenceValue = value;
        }

        private static void SetProperty(SerializedObject serialized, string name, Object[] values)
        {
            SerializedProperty property = serialized.FindProperty(name);
            if (property == null)
            {
                Debug.LogWarning("[Emberdelve] LobbyController has no serialized field '" + name + "'.");
                return;
            }

            if (!property.isArray)
            {
                Debug.LogWarning("[Emberdelve] LobbyController field '" + name + "' is not an array.");
                return;
            }

            property.arraySize = values.Length;
            for (int i = 0; i < values.Length; i++)
            {
                property.GetArrayElementAtIndex(i).objectReferenceValue = values[i];
            }
        }
    }
}
