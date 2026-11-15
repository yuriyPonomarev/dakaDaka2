using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Rendering.Universal;
using Emberdelve.Core;
using Emberdelve.Feedback;
using Emberdelve.Flow;
using Emberdelve.UI;

namespace Emberdelve.EditorTools
{
    /// <summary>
    /// Safe-room scene: a small torch-lit stone room plus the class-select screen.
    /// The UI itself is built at runtime by LobbyController/LobbyScreenView/LobbyOnlinePanel, so the scene stays tiny.
    /// </summary>
    internal static class EmberdelveLobbyBuilder
    {
        public static Scene Build()
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            EmberdelveAssetLibrary.BuildAll();
            EmberdelveVisualSetup.ApplyRenderSettings();
            EmberdelveVisualSetup.BeginMeshPass();

            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.07f, 0.06f, 0.05f, 1f);
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Exponential;
            RenderSettings.fogColor = new Color(0.05f, 0.04f, 0.035f, 1f);
            RenderSettings.fogDensity = 0.03f;

            int levelLayer = LayerMask.NameToLayer(GameConfig.LevelLayerName);
            GameObject root = new GameObject("Lobby_Room");

            AddBox(root.transform, "Lobby_Floor", new Vector3(-11f, -0.5f, -8f), new Vector3(11f, 0f, 8f),
                EmberdelveMaterials.LobbyFloor, levelLayer, 2f, 2f);
            AddBox(root.transform, "Lobby_Ceiling", new Vector3(-11f, 3.4f, -8f), new Vector3(11f, 3.9f, 8f),
                EmberdelveMaterials.StoneCeiling, levelLayer, 3f, 3f);
            AddBox(root.transform, "Lobby_Wall_North", new Vector3(-11f, 0f, 8f), new Vector3(11f, 3.4f, 8.45f),
                EmberdelveMaterials.StoneWall, levelLayer, 2f, 1f);
            AddBox(root.transform, "Lobby_Wall_South", new Vector3(-11f, 0f, -8.45f), new Vector3(11f, 3.4f, 0f),
                EmberdelveMaterials.StoneWall, levelLayer, 2f, 1f);
            AddBox(root.transform, "Lobby_Wall_West", new Vector3(-11.45f, 0f, -8f), new Vector3(-11f, 3.4f, 8f),
                EmberdelveMaterials.StoneWall, levelLayer, 2f, 1f);
            AddBox(root.transform, "Lobby_Wall_East", new Vector3(11f, 0f, -8f), new Vector3(11.45f, 3.4f, 8f),
                EmberdelveMaterials.StoneWall, levelLayer, 2f, 1f);

            AddTorch(root.transform, new Vector3(-9f, 2.5f, 7f), 12f);
            AddTorch(root.transform, new Vector3(9f, 2.5f, 7f), 12f);
            AddTorch(root.transform, new Vector3(-9f, 2.5f, -7f), 10f);
            AddTorch(root.transform, new Vector3(9f, 2.5f, -7f), 10f);

            // V3a: Poly Haven set dressing along the walls (visual only, no colliders).
            EmberdelvePropFactory.BuildLobbyProps();
            EmberdelveVisualSetup.PruneMeshes(MeshPrefix);

            EmberdelveVisualSetup.AddGlobalVolume("Post_Volume_Lobby");

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
            EmberdelveVisualSetup.ConfigureCamera(camera);

            GameObject master = new GameObject("Lobby_Master");
            LobbyController lobby = master.AddComponent<LobbyController>();
            // U3A5: the Online panel (Host / Join + address). Built at runtime by the component (UiFactory); it builds nothing when
            // networking is not in the build, and LobbyController adds one itself for a scene saved before this existed.
            master.AddComponent<LobbyOnlinePanel>();

            SerializedObject serialized = new SerializedObject(lobby);
            SetProperty(serialized, "config", EmberdelveAssetLibrary.Config);
            SetProperty(serialized, "classes", EmberdelveAssetLibrary.Classes);
            serialized.ApplyModifiedPropertiesWithoutUndo();

            AssetDatabase.SaveAssets();

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, EmberdelvePaths.LobbyScenePath);

            return scene;
        }

        private const string MeshPrefix = "Lobby_";

        private static void AddBox(Transform parent, string name, Vector3 min, Vector3 max, Material material, int layer, float tileU, float tileV)
        {
            GameObject box = GameObject.CreatePrimitive(PrimitiveType.Cube);
            box.name = name;
            box.transform.SetParent(parent, false);
            box.transform.position = (min + max) * 0.5f;
            box.transform.localScale = Vector3.Max(max - min, new Vector3(0.01f, 0.01f, 0.01f));
            if (layer >= 0) box.layer = layer;

            Renderer renderer = box.GetComponent<Renderer>();
            if (renderer != null && material != null) renderer.sharedMaterial = material;

            // V1: world-metre UVs on a generated mesh asset; transform scale and BoxCollider stay as the primitive made them.
            EmberdelveVisualSetup.ApplyWorldUv(box, MeshPrefix, tileU, tileV);
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
            light.shadows = LightShadows.Soft;
            ConfigureTorchShadowResolution(light);

            TorchFlicker flicker = torch.AddComponent<TorchFlicker>();
            EmberdelveTorchFactory.Build(torch, flicker);
        }

        private static void ConfigureTorchShadowResolution(Light light)
        {
            var data = light.GetComponent<UniversalAdditionalLightData>();
            if (data == null) data = light.gameObject.AddComponent<UniversalAdditionalLightData>();
            var serialized = new SerializedObject(data);
            var tier = serialized.FindProperty("m_AdditionalLightsShadowResolutionTier");
            if (tier == null) throw new System.InvalidOperationException("URP shadow resolution tier missing");
            // Four point lights need 24 faces. Low256 fits the 2048 atlas and matches the
            // previous automatic downscale, without changing illumination or soft shadows.
            tier.intValue = UniversalAdditionalLightData.AdditionalLightsShadowResolutionTierLow;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>Batch-only migration of saved lobby lights; preserve scene objects and identifiers.</summary>
        public static void UpdateSavedLobbyShadowResolution()
        {
            if (!Application.isBatchMode) throw new System.InvalidOperationException("Run this migration on the batch test mirror");
            Scene scene = EditorSceneManager.OpenScene(EmberdelvePaths.LobbyScenePath);
            int count = 0;
            foreach (var root in scene.GetRootGameObjects())
                foreach (var light in root.GetComponentsInChildren<Light>(true))
                    if (light.name == "Lobby_Torch" && light.type == LightType.Point)
                    { ConfigureTorchShadowResolution(light); count++; }
            if (count != 4) throw new System.InvalidOperationException("Expected four saved lobby torches, found " + count);
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log("[Emberdelve][LobbyShadows] configured four Low-tier point lights");
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
