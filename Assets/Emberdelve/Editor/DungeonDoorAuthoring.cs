using System;
using System.Collections.Generic;
using Emberdelve.Core;
using Emberdelve.Interaction;
using Emberdelve.Level;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Emberdelve.EditorTools
{
    internal static class DungeonDoorAuthoring
    {
        private const string Source = "Assets/Emberdelve/Art/Models/AI/Meshy/MapDressing/RustboundGate/";
        private const string PrefabPath = "Assets/Emberdelve/Resources/" + DungeonDoorwaySpawner.PrefabResource + ".prefab";
        private const float Width = 1.72f;
        private const float Height = 2.32f;
        private const float SpringLine = 1.60f;
        private const float Thickness = .21f;
        // A module seam falls between two columns of navigation cells. Center the passage on one lane.
        private const float LaneOffset = .77f;
        private const float SocketHalf = 2.02f;
        private const float FrameTop = 3.86f;
        private const float FrameDepth = .65f;

        [MenuItem("Emberdelve/Visual/Build Dungeon Door Prefab")]
        public static void Build()
        {
            var model = Load<GameObject>(Source + "RustboundGate.fbx");
            var frame = Load<Material>("Assets/Emberdelve/Art/Generated/DungeonSurfaces/World_Stone_Wall.mat");
            var doorMaterial = DoorMaterial();
            var root = new GameObject("DungeonDoor");
            try
            {
                int levelLayer = LayerMask.NameToLayer(GameConfig.LevelLayerName);
                int interactLayer = LayerMask.NameToLayer(GameConfig.InteractLayerName);
                if (levelLayer < 0 || interactLayer < 0) throw new InvalidOperationException("Door layers are missing.");
                float left = LaneOffset - Width * .5f;
                float right = LaneOffset + Width * .5f;
                // Frame and leaf are siblings. Navigation treats only the leaf as a toggleable obstacle.
                var masonry = Child(root.transform, "StoneFrame", Vector3.zero);
                AddMasonry(masonry, "Infilled_Wall", -SocketHalf, left, 0f, FrameTop, frame, levelLayer);
                AddMasonry(masonry, "Jamb", right, SocketHalf, 0f, FrameTop, frame, levelLayer);
                var lintel = AddArchedPiece(masonry, "StoneArch", new Vector3(LaneOffset, 0, 0),
                    ArchMesh("StoneArch", Width, SpringLine, Height, FrameTop, FrameDepth, cutout: true), frame);
                lintel.gameObject.layer = levelLayer;
                lintel.gameObject.AddComponent<MeshCollider>().sharedMesh = lintel.GetComponent<MeshFilter>().sharedMesh;

                var doorRoot = Child(root.transform, "Door", new Vector3(LaneOffset, 0, 0));
                var hinge = Child(doorRoot, "Hinge", new Vector3(-Width * .5f, 0, 0));
                var panel = Child(hinge, "LeafCollider", new Vector3(Width * .5f, Height * .5f, 0));
                panel.gameObject.layer = levelLayer;
                var blocker = panel.gameObject.AddComponent<BoxCollider>();
                blocker.size = new Vector3(Width, Height, Thickness);

                AddArchedPiece(hinge, "DarkWoodBacking", new Vector3(Width * .5f, 0, 0),
                    ArchMesh("DoorBacking", Width - .055f, SpringLine - .04f, Height - .055f, .14f, .068f, cutout: false),
                    Load<Material>("Assets/Emberdelve/Art/Materials/Wood_Planks.mat"));

                var visual = Child(hinge, "RustboundLeaf", Vector3.zero);
                var instance = (GameObject)PrefabUtility.InstantiatePrefab(model, visual);
                var renderers = instance.GetComponentsInChildren<MeshRenderer>();
                var bounds = ModelBounds(renderers);
                visual.localScale = new Vector3((Width - .015f) / bounds.size.x, (Height - .025f) / bounds.size.y, Thickness / bounds.size.z);
                bounds = ModelBounds(renderers);
                Vector3 baseCenter = new Vector3(bounds.center.x, bounds.min.y, bounds.center.z);
                visual.localPosition = new Vector3(Width * .5f, .012f, 0f) - hinge.InverseTransformPoint(baseCenter);
                foreach (var renderer in renderers)
                {
                    var slots = renderer.sharedMaterials;
                    Array.Fill(slots, doorMaterial);
                    renderer.sharedMaterials = slots;
                }

                var probe = Child(doorRoot, "InteractionVolume", Vector3.up * Height * .5f);
                probe.gameObject.layer = interactLayer;
                var trigger = probe.gameObject.AddComponent<BoxCollider>();
                trigger.isTrigger = true;
                trigger.size = new Vector3(Width, Height, 1.6f);
                var door = doorRoot.gameObject.AddComponent<DungeonDoorInteractable>();
                door.Configure("door_unbound", hinge, blocker, 96f);
                DownloadedCryptArchitecture.Apply(root);
                if (PrefabUtility.SaveAsPrefabAsset(root, PrefabPath) == null)
                    throw new InvalidOperationException("Could not save " + PrefabPath);
                AssetDatabase.SaveAssets();
                Debug.Log($"[DungeonDoorAuthoring] Saved {Width:F2} x {Height:F2} m doorway with {renderers.Length} Rustbound mesh renderers.");
            }
            finally { Object.DestroyImmediate(root); }
        }

        public static void BuildWithPreview()
        {
            Build();
            var scene = EditorSceneManager.OpenScene("Assets/Emberdelve/Scenes/Lobby.unity");
            foreach (var root in scene.GetRootGameObjects()) root.SetActive(false);
            foreach (bool open in new[] { false, true })
                EmberdelveViewmodelPreview.Capture("Logs/small-doors/" + (open ? "open" : "closed") + ".png",
                    1120, 730, 51, true, (root, camera) =>
                    {
                        var instance = Object.Instantiate(Load<GameObject>(PrefabPath), root);
                        var door = instance.GetComponentInChildren<DungeonDoorInteractable>();
                        door.ApplyReplicatedState(door.DoorId, open, immediate: true);
                        camera.transform.position = EmberdelveViewmodelPreview.Origin + new Vector3(2.7f, 1.73f, -4.4f);
                        camera.transform.LookAt(EmberdelveViewmodelPreview.Origin + new Vector3(.1f, 1.71f, 0));
                    });
        }

        private static void AddMasonry(Transform parent, string name, float left, float right, float bottom, float top,
            Material material, int layer)
        {
            var piece = GameObject.CreatePrimitive(PrimitiveType.Cube);
            piece.name = name;
            piece.layer = layer;
            piece.transform.SetParent(parent, false);
            piece.transform.localPosition = new Vector3((left + right) * .5f, (bottom + top) * .5f, 0);
            piece.transform.localScale = new Vector3(right - left, top - bottom, FrameDepth);
            piece.GetComponent<MeshRenderer>().sharedMaterial = material;
        }

        private static Transform AddArchedPiece(Transform parent, string name, Vector3 position, Mesh mesh, Material material)
        {
            var piece = Child(parent, name, position);
            piece.gameObject.AddComponent<MeshFilter>().sharedMesh = mesh;
            piece.gameObject.AddComponent<MeshRenderer>().sharedMaterial = material;
            return piece;
        }

        private static Mesh ArchMesh(string name, float width, float spring, float peak, float straightEdge, float depth, bool cutout)
        {
            const int segments = 20;
            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            var uv = new List<Vector2>();
            float halfDepth = depth * .5f;
            for (int i = 0; i < segments; i++)
            {
                float u0 = 2f * i / segments - 1f;
                float u1 = 2f * (i + 1) / segments - 1f;
                float x0 = u0 * width * .5f;
                float x1 = u1 * width * .5f;
                float y0 = Mathf.Lerp(spring, peak, Mathf.Sqrt(1f - u0 * u0));
                float y1 = Mathf.Lerp(spring, peak, Mathf.Sqrt(1f - u1 * u1));
                Vector3 a = new Vector3(x0, cutout ? y0 : straightEdge, -halfDepth);
                Vector3 b = new Vector3(x1, cutout ? y1 : straightEdge, -halfDepth);
                Vector3 c = new Vector3(x1, cutout ? straightEdge : y1, -halfDepth);
                Vector3 d = new Vector3(x0, cutout ? straightEdge : y0, -halfDepth);
                Vector3 offset = Vector3.forward * depth;
                AddQuad(a, d, c, b, vertices, triangles, uv);
                AddQuad(a + offset, b + offset, c + offset, d + offset, vertices, triangles, uv);
                AddQuad(d, d + offset, c + offset, c, vertices, triangles, uv);
                AddQuad(a, b, b + offset, a + offset, vertices, triangles, uv);
                if (i == 0) AddQuad(a, a + offset, d + offset, d, vertices, triangles, uv);
                if (i == segments - 1) AddQuad(b, c, c + offset, b + offset, vertices, triangles, uv);
            }
            var mesh = new Mesh { name = name };
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.SetUVs(0, uv);
            mesh.RecalculateNormals();
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            string path = Source + name + ".asset";
            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (existing == null)
            {
                AssetDatabase.CreateAsset(mesh, path);
                return mesh;
            }
            EditorUtility.CopySerialized(mesh, existing);
            EditorUtility.SetDirty(existing);
            Object.DestroyImmediate(mesh);
            return existing;
        }

        private static void AddQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d,
            List<Vector3> vertices, List<int> triangles, List<Vector2> uv)
        {
            int first = vertices.Count;
            foreach (var point in new[] { a, b, c, d })
            {
                vertices.Add(point);
                uv.Add(new Vector2(point.x + .5f, point.y * .45f));
            }
            triangles.Add(first);
            triangles.Add(first + 1);
            triangles.Add(first + 2);
            triangles.Add(first);
            triangles.Add(first + 2);
            triangles.Add(first + 3);
        }

        private static Material DoorMaterial()
        {
            string path = Source + "RustboundGate.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(Load<Material>("Assets/Emberdelve/Resources/Emberdelve/DoorPanel.mat"));
                AssetDatabase.CreateAsset(material, path);
            }
            material.SetTexture("_BaseMap", Load<Texture2D>(Source + "RustboundGate_Albedo.png"));
            material.SetColor("_BaseColor", new Color(.83f, .80f, .75f));
            material.SetFloat("_Smoothness", .22f);
            EditorUtility.SetDirty(material);
            return material;
        }

        private static Bounds ModelBounds(MeshRenderer[] renderers)
        {
            if (renderers.Length == 0) throw new InvalidOperationException("Rustbound Gate has no mesh renderers.");
            Bounds result = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++) result.Encapsulate(renderers[i].bounds);
            if (result.size.x <= .01f || result.size.y <= .01f || result.size.z <= .001f)
                throw new InvalidOperationException("Rustbound Gate has invalid bounds.");
            return result;
        }

        private static Transform Child(Transform parent, string name, Vector3 position)
        {
            var child = new GameObject(name).transform;
            child.SetParent(parent, false);
            child.localPosition = position;
            return child;
        }

        private static T Load<T>(string path) where T : Object
        {
            var loaded = AssetDatabase.LoadAssetAtPath<T>(path);
            if (loaded == null) throw new InvalidOperationException("Missing dungeon door input " + path);
            return loaded;
        }
    }
}
