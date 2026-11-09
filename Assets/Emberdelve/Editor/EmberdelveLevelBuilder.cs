using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using Emberdelve.AI;
using Emberdelve.Core;
using Emberdelve.Combat;
using Emberdelve.Data;
using Emberdelve.Feedback;
using Emberdelve.Flow;
using Emberdelve.InputLayer;
using Emberdelve.Interaction;
using Emberdelve.Items;
using Emberdelve.Level;
using Emberdelve.Movement;
using Emberdelve.Player;
using Emberdelve.Skills;
using Emberdelve.UI;

namespace Emberdelve.EditorTools
{
    /// <summary>
    /// Generates the blockout dungeon scene from DungeonLayoutData: merged floor/ceiling slabs,
    /// merged wall runs, torch lights, loot, enemies, the Ember Gate, the player rig and the
    /// scene managers. Re-running it regenerates the level from the same layout asset.
    /// </summary>
    internal static class EmberdelveLevelBuilder
    {
        public static Scene Build()
        {
            return BuildScene(EmberdelvePaths.DungeonScenePath, NewSceneMode.Single, false);
        }

        [MenuItem("Emberdelve/Scenes/Build Modular Crypts Scene")]
        public static void BuildModularScene()
        {
            string absoluteTarget = System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath, "Emberdelve/Scenes/Emberdelve_ModularCrypts.unity"));
            if (System.IO.File.Exists(absoluteTarget))
            {
                Debug.LogError("Modular crypt scene already exists; refusing to overwrite it. The current scene was left untouched.");
                return;
            }
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene loaded = SceneManager.GetSceneAt(i);
                if (loaded.path == EmberdelvePaths.ModularDungeonScenePath)
                {
                    Debug.LogError("Modular crypt scene is already open; close it before generating a separate copy. No scene was changed.");
                    return;
                }
            }

            DungeonModuleCatalog catalog = AssetDatabase.LoadAssetAtPath<DungeonModuleCatalog>(
                EmberdelvePaths.Root + "/Resources/Emberdelve/DungeonModuleCatalog.asset");
            if (catalog == null)
            {
                Debug.LogError("Build the Dungeon Module Catalog first (Emberdelve/Build Dungeon Module Catalog). The current scene was left untouched.");
                return;
            }

            Scene previousActive = SceneManager.GetActiveScene();
            Scene generated = default;
            try
            {
                generated = BuildScene(EmberdelvePaths.ModularDungeonScenePath, NewSceneMode.Additive, true);
                if (!generated.IsValid() || !System.IO.File.Exists(absoluteTarget))
                {
                    Debug.LogError("Modular crypt scene was not saved; it was not added to Build Settings.");
                    return;
                }
                EnsureBuildSettingsScene(EmberdelvePaths.ModularDungeonScenePath);
                Debug.Log("Built modular crypt scene without closing or replacing the previously open scene.");
            }
            finally
            {
                if (generated.IsValid() && generated.isLoaded) EditorSceneManager.CloseScene(generated, true);
                if (previousActive.IsValid() && previousActive.isLoaded) SceneManager.SetActiveScene(previousActive);
            }
        }

        private static void EnsureBuildSettingsScene(string path)
        {
            EditorBuildSettingsScene[] current = EditorBuildSettings.scenes;
            for (int i = 0; i < current.Length; i++)
                if (current[i].path == path) return;

            List<EditorBuildSettingsScene> updated = new List<EditorBuildSettingsScene>(current)
            {
                new EditorBuildSettingsScene(path, true)
            };
            EditorBuildSettings.scenes = updated.ToArray();
        }

        private static Scene BuildScene(string scenePath, NewSceneMode sceneMode, bool modularScene)
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, sceneMode);
            SceneManager.SetActiveScene(scene);

            // Re-load every data asset and material now: creating a new scene unloads assets that
            // no loaded scene references yet, which can leave earlier references destroyed.
            EmberdelveAssetLibrary.BuildAll();
            EmberdelveVisualSetup.ApplyRenderSettings();

            // V2: KayKit models (enemy prefabs + animator controllers, chest model set). Each step is skipped, with a warning,
            // when the models are not imported, and the level then keeps the blockout cubes.
            EmberdelveEnemyVisualFactory.EnsureAll(false);
            EmberdelvePropFactory.EnsureChestVisualSet();

            DungeonLayoutData layout = EmberdelveAssetLibrary.Layout;
            GameConfig config = EmberdelveAssetLibrary.Config;

            SetupEnvironment();
            BuildGeometry(layout);
            BuildTorches(layout);

            GameObject master = BuildManagers(config);
            PlayerRig rig = BuildPlayer(layout, config);
            RunDirector director = BuildRunDirector(master, config, rig);

            BuildLoot(layout, director);
            BuildEnemies(layout, config, director);
            EmberdelvePropFactory.BuildProps(layout);
            BuildExtractionGate(layout, config, director);
            BuildDungeonModuleAssembler(layout, modularScene);
            BuildNetSceneRoles(master, rig, modularScene);

            // V1: material / mesh / volume-profile assets were created or modified through the AssetDatabase.
            AssetDatabase.SaveAssets();

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, scenePath);

            return scene;
        }

        // ---------------------------------------------------------------- environment

        private static void SetupEnvironment()
        {
            // An underground crypt opens into a broken-roof court over the Ember Gate.
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.055f, 0.064f, 0.09f, 1f);
            RenderSettings.ambientIntensity = 1f;
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Exponential;
            RenderSettings.fogColor = new Color(0.075f, 0.067f, 0.075f, 1f);
            RenderSettings.fogDensity = 0.028f;
            Material skybox = EnsureCryptSkybox();
            if (skybox != null) RenderSettings.skybox = skybox;

            // The object stays (gates and tooling may look it up), but it is practically off: no sky underground.
            GameObject lightGo = new GameObject("Moon_Slit_Directional");
            lightGo.transform.rotation = Quaternion.Euler(38f, 32f, 0f);
            Light light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.color = new Color(0.42f, 0.52f, 0.78f, 1f);
            light.intensity = 0.35f;
            light.shadows = LightShadows.Soft;
            light.shadowStrength = 0.65f;
            RenderSettings.sun = light;

            EmberdelveVisualSetup.AddGlobalVolume("Post_Volume_Dungeon");
        }

        // ---------------------------------------------------------------- geometry

        private static void BuildGeometry(DungeonLayoutData layout)
        {
            bool[,] walkable = layout.BuildWalkableGrid();
            int width = layout.gridWidth;
            int height = layout.gridHeight;
            float cell = layout.cellSize;
            Vector3 origin = layout.origin;

            GameObject levelRoot = new GameObject("Level_Blockout");
            LastBoxes.Clear();
            EmberdelveVisualSetup.BeginMeshPass();
            int levelLayer = LayerMask.NameToLayer(GameConfig.LevelLayerName);

            List<RectInt> patches = MergeRectangles(walkable);

            bool[,] roof = (bool[,])walkable.Clone();
            // The extraction chamber is an open-air ruin. A broad aperture over the gate lets the sky
            // read from the floor while leaving the surrounding crypt roof intact.
            RoomSpec court = layout.rooms.Find(r => r != null && r.roomName == "Ember Gate");
            if (court != null)
            {
                int xMin = Mathf.Clamp(court.minCell.x + 1, 0, width - 1);
                int xMax = Mathf.Clamp(court.maxCell.x - 1, 0, width - 1);
                int yMin = Mathf.Clamp(court.minCell.y + 1, 0, height - 1);
                int yMax = Mathf.Clamp(court.maxCell.y - 1, 0, height - 1);
                for (int y = yMin; y <= yMax; y++)
                    for (int x = xMin; x <= xMax; x++)
                        roof[x, y] = false;
            }
            List<RectInt> roofPatches = MergeRectangles(roof);

            for (int i = 0; i < patches.Count; i++)
            {
                RectInt patch = patches[i];
                Vector3 min = origin + new Vector3(patch.xMin * cell, -layout.floorThickness, patch.yMin * cell);
                Vector3 max = origin + new Vector3(patch.xMax * cell, 0f, patch.yMax * cell);
                CreateBox(levelRoot.transform, "Floor_" + i, min, max, EmberdelveMaterials.StoneFloor, levelLayer, FloorTile, FloorTile);
            }
            for (int i = 0; i < roofPatches.Count; i++)
            {
                RectInt patch = roofPatches[i];
                Vector3 ceilingMin = origin + new Vector3(patch.xMin * cell, layout.wallHeight, patch.yMin * cell);
                Vector3 ceilingMax = origin + new Vector3(patch.xMax * cell, layout.wallHeight + layout.ceilingThickness, patch.yMax * cell);
                CreateBox(levelRoot.transform, "Ceiling_" + i, ceilingMin, ceilingMax, EmberdelveMaterials.StoneCeiling, levelLayer, CeilingTile, CeilingTile);
            }

            bool[,] edgeZ = new bool[height + 1, width];
            bool[,] edgeX = new bool[width + 1, height];

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    if (!walkable[x, y]) continue;
                    if (!IsWalkable(walkable, x, y + 1)) edgeZ[y + 1, x] = true;
                    if (!IsWalkable(walkable, x, y - 1)) edgeZ[y, x] = true;
                    if (!IsWalkable(walkable, x + 1, y)) edgeX[x + 1, y] = true;
                    if (!IsWalkable(walkable, x - 1, y)) edgeX[x, y] = true;
                }
            }

            float half = Mathf.Max(0.05f, layout.wallThickness) * 0.5f;

            for (int edge = 0; edge <= height; edge++)
            {
                int x = 0;
                while (x < width)
                {
                    if (!edgeZ[edge, x]) { x++; continue; }

                    int start = x;
                    while (x < width && edgeZ[edge, x]) x++;

                    float z = origin.z + edge * cell;
                    Vector3 min = origin + new Vector3(start * cell - half, 0f, z - half);
                    Vector3 max = origin + new Vector3(x * cell + half, layout.wallHeight, z + half);
                    CreateBox(levelRoot.transform, "Wall_Z" + edge + "_" + start, min, max, EmberdelveMaterials.StoneWall, levelLayer, WallTileU, WallTileV);
                }
            }

            for (int edge = 0; edge <= width; edge++)
            {
                int y = 0;
                while (y < height)
                {
                    if (!edgeX[edge, y]) { y++; continue; }

                    int start = y;
                    while (y < height && edgeX[edge, y]) y++;

                    float x = origin.x + edge * cell;
                    Vector3 min = origin + new Vector3(x - half, 0f, start * cell - half);
                    Vector3 max = origin + new Vector3(x + half, layout.wallHeight, y * cell + half);
                    CreateBox(levelRoot.transform, "Wall_X" + edge + "_" + start, min, max, EmberdelveMaterials.StoneWall, levelLayer, WallTileU, WallTileV);
                }
            }

            BuildRoomMarkers(levelRoot.transform, layout);
            BuildCryptArchitecture(levelRoot.transform, layout, levelLayer);
            BuildArchPortals(levelRoot.transform, layout, levelLayer);
            BuildCryptTombs(levelRoot.transform, layout, levelLayer);
            EmberdelveVisualSetup.PruneMeshes(MeshPrefix);
        }

        private const string CryptSkyboxPath = "Assets/Emberdelve/Art/Materials/Crypt_Skybox.mat";

        private static Material EnsureCryptSkybox()
        {
            Material sky = AssetDatabase.LoadAssetAtPath<Material>(CryptSkyboxPath);
            if (sky == null)
            {
                Shader shader = Shader.Find("Skybox/Procedural");
                if (shader == null) return null;
                sky = new Material(shader) { name = "Crypt_Skybox" };
                AssetDatabase.CreateAsset(sky, CryptSkyboxPath);
            }
            sky.SetColor("_SkyTint", new Color(0.48f, 0.56f, 0.76f));
            sky.SetColor("_GroundColor", new Color(0.11f, 0.105f, 0.13f));
            sky.SetFloat("_Exposure", 0.55f);
            sky.SetFloat("_AtmosphereThickness", 0.7f);
            EditorUtility.SetDirty(sky);
            return sky;
        }

        private static void BuildCryptArchitecture(Transform parent, DungeonLayoutData layout, int layer)
        {
            GameObject architecture = new GameObject("Crypt_Architecture");
            architecture.transform.SetParent(parent, false);
            float cell = layout.CellSize;
            float wallHalf = Mathf.Max(0.05f, layout.wallThickness) * 0.5f;
            for (int i = 0; i < layout.rooms.Count; i++)
            {
                RoomSpec room = layout.rooms[i];
                float x0 = layout.origin.x + room.minCell.x * cell + 0.7f;
                float x1 = layout.origin.x + (room.maxCell.x + 1) * cell - 0.7f;
                float northFace = layout.origin.z + (room.maxCell.y + 1) * cell - wallHalf;
                float southFace = layout.origin.z + room.minCell.y * cell + wallHalf;
                float h = layout.wallHeight;
                float width = room.roomName == "Central Hall" ? 0.8f : 0.62f;
                // Flat wall pilasters keep the carved-stone rhythm without projecting columns into routes.
                for (int p = 0; p < 4; p++)
                {
                    float x = p % 2 == 0 ? x0 : x1;
                    float z = p < 2 ? northFace - 0.035f : southFace + 0.035f;
                    Vector3 c = new Vector3(x, h * 0.5f, z);
                    AddVisualBlock(architecture.transform, room.roomName + "_Pilaster_" + p, c, new Vector3(width, h, 0.06f), EmberdelveMaterials.StoneWall, layer);
                    c.y = 0.18f;
                    AddVisualBlock(architecture.transform, room.roomName + "_Plinth_" + p, c, new Vector3(width + 0.2f, 0.36f, 0.08f), EmberdelveMaterials.StoneCeiling, layer);
                    c.y = h - 0.2f;
                    AddVisualBlock(architecture.transform, room.roomName + "_Capital_" + p, c, new Vector3(width + 0.3f, 0.4f, 0.08f), EmberdelveMaterials.StoneCeiling, layer);
                }
                // Broken parapet stones around the roof aperture make it read as a ruined courtyard.
                if (room.roomName == "Ember Gate")
                {
                    float y = layout.wallHeight + 0.2f;
                    float span = Mathf.Max(cell, (room.maxCell.x - room.minCell.x - 1) * cell);
                    float courtNorth = layout.origin.z + (room.minCell.y + 1) * cell;
                    float courtSouth = layout.origin.z + room.maxCell.y * cell;
                    Vector3 mid = new Vector3((x0 + x1) * 0.5f, y, courtNorth - 0.45f);
                    AddVisualBlock(architecture.transform, "Court_Ruined_Parapet_N", mid, new Vector3(span, 0.4f, 0.9f), EmberdelveMaterials.StoneWall, layer);
                    mid.z = courtSouth + 0.45f;
                    AddVisualBlock(architecture.transform, "Court_Ruined_Parapet_S", mid, new Vector3(span, 0.4f, 0.9f), EmberdelveMaterials.StoneWall, layer);
                }
            }
        }

        private static void BuildArchPortals(Transform parent, DungeonLayoutData layout, int layer)
        {
            GameObject root = new GameObject("Crypt_Archways");
            root.transform.SetParent(parent, false);
            float cell = layout.CellSize;
            for (int i = 0; i < layout.corridors.Count; i++)
            {
                CorridorSpec corridor = layout.corridors[i];
                float span = Mathf.Max(1, corridor.width) * cell;
                int lowOffset = -(Mathf.Max(1, corridor.width) / 2);
                int highOffset = (Mathf.Max(1, corridor.width) - 1) / 2;
                // Match DungeonLayoutData.BuildWalkableGrid: an even-width corridor straddles
                // its authored axis, while an odd-width corridor is centered on the axis cell.
                float crossOffset = (lowOffset + highOffset + 1f) * 0.5f;
                if (corridor.fromCell.y == corridor.toCell.y)
                {
                    int low = Mathf.Min(corridor.fromCell.x, corridor.toCell.x);
                    int high = Mathf.Max(corridor.fromCell.x, corridor.toCell.x);
                    float z = layout.origin.z + (corridor.fromCell.y + crossOffset) * cell;
                    AddArch(root.transform, corridor.corridorName + "_Entry", new Vector3(layout.origin.x + (low + 1) * cell, 0f, z), span, true, layout.wallHeight, layer);
                    AddArch(root.transform, corridor.corridorName + "_Exit", new Vector3(layout.origin.x + high * cell, 0f, z), span, true, layout.wallHeight, layer);
                }
                else if (corridor.fromCell.x == corridor.toCell.x)
                {
                    int low = Mathf.Min(corridor.fromCell.y, corridor.toCell.y);
                    int high = Mathf.Max(corridor.fromCell.y, corridor.toCell.y);
                    float x = layout.origin.x + (corridor.fromCell.x + crossOffset) * cell;
                    AddArch(root.transform, corridor.corridorName + "_Entry", new Vector3(x, 0f, layout.origin.z + (low + 1) * cell), span, false, layout.wallHeight, layer);
                    AddArch(root.transform, corridor.corridorName + "_Exit", new Vector3(x, 0f, layout.origin.z + high * cell), span, false, layout.wallHeight, layer);
                }
            }
        }

        private static void AddArch(Transform parent, string name, Vector3 center, float span, bool alongX, float wallHeight, int layer)
        {
            float radius = Mathf.Min(span * 0.43f, wallHeight * 0.46f);
            float spring = wallHeight - radius - 0.16f;
            Vector3 across = alongX ? Vector3.forward : Vector3.right;
            Vector3 normal = alongX ? Vector3.right : Vector3.forward;
            float depth = 0.52f;
            float jambOffset = radius + 0.12f;
            AddVisualBlock(parent, name + "_Jamb_L", center + across * -jambOffset + Vector3.up * (spring * 0.5f), alongX ? new Vector3(depth, spring, 0.42f) : new Vector3(0.42f, spring, depth), EmberdelveMaterials.StoneCeiling, layer, true);
            AddVisualBlock(parent, name + "_Jamb_R", center + across * jambOffset + Vector3.up * (spring * 0.5f), alongX ? new Vector3(depth, spring, 0.42f) : new Vector3(0.42f, spring, depth), EmberdelveMaterials.StoneCeiling, layer, true);
            // Seven individually keyed voussoirs make a readable rounded silhouette from the existing stone material.
            const int stones = 7;
            for (int i = 0; i < stones; i++)
            {
                float angle = Mathf.PI * i / (stones - 1);
                Vector3 p = center + across * (radius * Mathf.Cos(angle)) + Vector3.up * (spring + radius * Mathf.Sin(angle));
                GameObject voussoir = GameObject.CreatePrimitive(PrimitiveType.Cube);
                voussoir.name = name + "_Voussoir_" + i;
                voussoir.transform.SetParent(parent, false);
                voussoir.transform.position = p;
                voussoir.transform.localScale = alongX ? new Vector3(depth, 0.48f, radius * 0.55f) : new Vector3(radius * 0.55f, 0.48f, depth);
                voussoir.transform.rotation = Quaternion.AngleAxis(alongX ? -Mathf.Cos(angle) * 42f : Mathf.Cos(angle) * 42f, normal) * Quaternion.identity;
                Renderer renderer = voussoir.GetComponent<Renderer>();
                if (renderer != null)
                {
                    renderer.sharedMaterial = i == stones / 2 ? EmberdelveMaterials.StoneWall : EmberdelveMaterials.StoneCeiling;
                    renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    renderer.receiveShadows = true;
                }
                if (layer >= 0) voussoir.layer = layer;
            }
            Vector3 key = center + Vector3.up * (spring + radius - 0.18f);
            AddVisualBlock(parent, name + "_Keystone", key, alongX ? new Vector3(depth + 0.08f, 0.45f, 0.62f) : new Vector3(0.62f, 0.45f, depth + 0.08f), EmberdelveMaterials.StoneWall, layer, true);
        }

        private static void BuildCryptTombs(Transform parent, DungeonLayoutData layout, int layer)
        {
            RoomSpec crypt = layout.rooms.Find(r => r != null && r.roomName == "Crypt");
            if (crypt == null) return;
            GameObject root = new GameObject("Crypt_Wall_Tombs");
            root.transform.SetParent(parent, false);
            float cell = layout.CellSize;
            float z = layout.origin.z + (crypt.maxCell.y + 1) * cell - Mathf.Max(0.05f, layout.wallThickness) * 0.5f;
            float left = layout.origin.x + (crypt.minCell.x + 1.5f) * cell;
            float right = layout.origin.x + (crypt.maxCell.x - 1.5f) * cell;
            BuildTomb(root.transform, "Crypt_Tomb_Left", new Vector3(left, 0f, z), layer);
            BuildTomb(root.transform, "Crypt_Tomb_Right", new Vector3(right, 0f, z), layer);
        }

        private static void BuildTomb(Transform parent, string name, Vector3 center, int layer)
        {
            // Shallow bas-relief sits in front of the existing wall face, with no hidden bulky volume.
            float overlayZ = center.z - 0.025f;
            AddVisualBlock(parent, name + "_Recess", new Vector3(center.x, 1.0f, overlayZ), new Vector3(2.5f, 1.8f, 0.04f), EmberdelveMaterials.IronDark, layer);
            AddVisualBlock(parent, name + "_Frame_L", new Vector3(center.x - 1.28f, 1.0f, overlayZ), new Vector3(0.18f, 2.0f, 0.04f), EmberdelveMaterials.StoneCeiling, layer);
            AddVisualBlock(parent, name + "_Frame_R", new Vector3(center.x + 1.28f, 1.0f, overlayZ), new Vector3(0.18f, 2.0f, 0.04f), EmberdelveMaterials.StoneCeiling, layer);
            AddVisualBlock(parent, name + "_Lintel", new Vector3(center.x, 1.92f, overlayZ), new Vector3(2.7f, 0.24f, 0.05f), EmberdelveMaterials.StoneCeiling, layer);
            AddVisualBlock(parent, name + "_Sarcophagus", new Vector3(center.x, 0.34f, overlayZ - 0.012f), new Vector3(2.2f, 0.62f, 0.04f), EmberdelveMaterials.StoneWall, layer);
            AddVisualBlock(parent, name + "_Lid", new Vector3(center.x, 0.72f, overlayZ - 0.012f), new Vector3(2.28f, 0.22f, 0.04f), EmberdelveMaterials.StoneCeiling, layer);
        }

        private static void AddVisualBlock(Transform parent, string name, Vector3 position, Vector3 scale, Material material, int layer, bool physical = false)
        {
            GameObject block = GameObject.CreatePrimitive(PrimitiveType.Cube);
            block.name = name;
            block.transform.SetParent(parent, false);
            block.transform.position = position;
            block.transform.localScale = scale;
            if (!physical)
            {
                Collider collider = block.GetComponent<Collider>();
                if (collider != null) UnityEngine.Object.DestroyImmediate(collider);
            }
            if (layer >= 0) block.layer = layer;
            Renderer renderer = block.GetComponent<Renderer>();
            if (renderer != null)
            {
                renderer.sharedMaterial = material;
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = true;
            }
        }

        private static void BuildRoomMarkers(Transform parent, DungeonLayoutData layout)
        {
            for (int i = 0; i < layout.rooms.Count; i++)
            {
                RoomSpec room = layout.rooms[i];
                Vector3 center = layout.CellCenterWorld((room.minCell + room.maxCell) / 2);

                GameObject marker = new GameObject("RoomMarker_" + room.roomName);
                marker.transform.SetParent(parent, false);
                marker.transform.position = center;
            }
        }

        private static bool IsWalkable(bool[,] walkable, int x, int y)
        {
            if (x < 0 || y < 0 || x >= walkable.GetLength(0) || y >= walkable.GetLength(1)) return false;
            return walkable[x, y];
        }

        private static List<RectInt> MergeRectangles(bool[,] walkable)
        {
            int width = walkable.GetLength(0);
            int height = walkable.GetLength(1);
            bool[,] used = new bool[width, height];
            List<RectInt> result = new List<RectInt>();

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    if (!walkable[x, y] || used[x, y]) continue;

                    int xMax = x;
                    while (xMax + 1 < width && walkable[xMax + 1, y] && !used[xMax + 1, y]) xMax++;

                    int yMax = y;
                    while (yMax + 1 < height)
                    {
                        bool canGrow = true;
                        for (int cx = x; cx <= xMax; cx++)
                        {
                            if (!walkable[cx, yMax + 1] || used[cx, yMax + 1])
                            {
                                canGrow = false;
                                break;
                            }
                        }

                        if (!canGrow) break;
                        yMax++;
                    }

                    for (int cy = y; cy <= yMax; cy++)
                    {
                        for (int cx = x; cx <= xMax; cx++) used[cx, cy] = true;
                    }

                    result.Add(new RectInt(x, y, xMax - x + 1, yMax - y + 1));
                }
            }

            return result;
        }

        // V1 world-scale UVs: one texture tile covers this many metres. Bricks100 is a 2:1 texture, so walls tile 2 m x 1 m.
        private const string MeshPrefix = "Dungeon_";
        private const float FloorTile = 2f;
        private const float CeilingTile = 3f;
        private const float WallTileU = 2f;
        private const float WallTileV = 1f;

        /// <summary>Min/max each level box was asked for (renderer-independent); the visual gate compares colliders to it.</summary>
        internal static readonly Dictionary<string, Bounds> LastBoxes = new Dictionary<string, Bounds>();

        private static void CreateBox(Transform parent, string name, Vector3 min, Vector3 max, Material material, int layer, float tileU, float tileV)
        {
            GameObject box = GameObject.CreatePrimitive(PrimitiveType.Cube);
            box.name = name;
            box.transform.SetParent(parent, false);
            box.transform.position = (min + max) * 0.5f;
            box.transform.localScale = Vector3.Max(max - min, new Vector3(0.01f, 0.01f, 0.01f));

            if (layer >= 0) box.layer = layer;

            Renderer renderer = box.GetComponent<Renderer>();
            if (renderer != null && material != null) renderer.sharedMaterial = material;

            Bounds bounds = new Bounds();
            bounds.SetMinMax(min, max);
            LastBoxes[name] = bounds;

            // Same primitive, same transform scale, same BoxCollider; only the visible mesh gets world-metre UVs.
            EmberdelveVisualSetup.ApplyWorldUv(box, MeshPrefix, tileU, tileV);
        }

        // ---------------------------------------------------------------- lights

        private static void BuildTorches(DungeonLayoutData layout)
        {
            GameObject root = new GameObject("Torches");

            for (int i = 0; i < layout.torches.Count; i++)
            {
                TorchSpec spec = layout.torches[i];
                Vector3 center = layout.CellCenterWorld(spec.cell);

                GameObject torch = new GameObject("Torch_" + i);
                torch.transform.SetParent(root.transform, false);
                torch.transform.position = center + Vector3.up * spec.height;

                Light light = torch.AddComponent<Light>();
                light.type = LightType.Point;
                light.color = new Color(1f, 0.62f, 0.32f, 1f);
                light.range = spec.range;
                light.intensity = spec.intensity;
                light.shadows = LightShadows.Soft;
                light.shadowStrength = 1f;
                light.shadowNearPlane = 0.1f;

                // The sconce is a child called "Sconce" (TorchFlicker mounts it on the nearest wall at runtime).
                TorchFlicker flicker = torch.AddComponent<TorchFlicker>();
                EmberdelveTorchFactory.Build(torch, flicker);
            }
        }

        // ---------------------------------------------------------------- managers

        private static GameObject BuildManagers(GameConfig config)
        {
            GameObject master = new GameObject("Game_Master");

            LocalCombatAuthority authority = master.AddComponent<LocalCombatAuthority>();
            SetReference(authority, "config", config);

            master.AddComponent<HudController>();
            master.AddComponent<ResultScreenController>();
            master.AddComponent<FeedbackDirector>();

            return master;
        }

        private static RunDirector BuildRunDirector(GameObject master, GameConfig config, PlayerRig rig)
        {
            RunDirector director = master.AddComponent<RunDirector>();
            SetReference(director, "config", config);
            SetReference(director, "fallbackClass", EmberdelveAssetLibrary.Warrior);
            SetReference(director, "playerRig", rig);
            SetReference(director, "layout", EmberdelveAssetLibrary.Layout);

            // U3A2: the classes a network client may ask for by id. Offline never reads this list.
            SerializedObject serializedDirector = new SerializedObject(director);
            SerializedProperty approved = serializedDirector.FindProperty("approvedClasses");
            if (approved != null)
            {
                CharacterClassData[] classes = EmberdelveAssetLibrary.Classes;
                approved.arraySize = classes.Length;
                for (int i = 0; i < classes.Length; i++) approved.GetArrayElementAtIndex(i).objectReferenceValue = classes[i];
                serializedDirector.ApplyModifiedPropertiesWithoutUndo();
            }

            return director;
        }

        // ---------------------------------------------------------------- network roles (U3A2)

        /// <summary>
        /// One scene object that switches the offline-only parts off for networked roles (see NetSceneRoles). Offline it is a
        /// no-op, so the authored offline scene behaves exactly as before. The scene rig is switched off for every networked
        /// role (players come from the network prefab); clients also lose the game master (combat authority, run director,
        /// HUD) and enemies. The modular scene keeps a visible, read-only floor-loot presentation and its extraction interactable;
        /// the legacy scene keeps its original client deactivations. A dedicated server disables its HUD, result screen and feedback.
        /// </summary>
        private static void BuildNetSceneRoles(GameObject master, PlayerRig rig, bool modularScene)
        {
            GameObject enemies = GameObject.Find("Enemies");
            GameObject loot = GameObject.Find("Loot");
            GameObject gate = GameObject.Find("Ember_Gate");

            List<GameObject> client = new List<GameObject> { master };
            if (enemies != null) client.Add(enemies);
            if (loot != null && !modularScene) client.Add(loot);
            if (gate != null && !modularScene) client.Add(gate);

            List<Behaviour> server = new List<Behaviour>();
            HudController hud = master.GetComponent<HudController>();
            ResultScreenController result = master.GetComponent<ResultScreenController>();
            FeedbackDirector feedback = master.GetComponent<FeedbackDirector>();
            if (hud != null) server.Add(hud);
            if (result != null) server.Add(result);
            if (feedback != null) server.Add(feedback);

            GameObject roles = new GameObject("Net_SceneRoles");
            NetSceneRoles component = roles.AddComponent<NetSceneRoles>();
            component.Configure(new[] { rig.gameObject }, client.ToArray(), server.ToArray(), modularScene);
            EditorUtility.SetDirty(component);
        }

        /// <summary>Wires authored prefab modules into newly built dungeon scenes; existing/open scenes are never rewritten by this helper.</summary>
        private static void BuildDungeonModuleAssembler(DungeonLayoutData layout, bool enableOfflineModules)
        {
            string catalogPath = EmberdelvePaths.Root + "/Resources/Emberdelve/DungeonModuleCatalog.asset";
            DungeonModuleCatalog catalog = AssetDatabase.LoadAssetAtPath<DungeonModuleCatalog>(catalogPath);
            GameObject host = new GameObject("Dungeon_Module_Assembler");
            DungeonModuleSceneAssembler assembler = host.AddComponent<DungeonModuleSceneAssembler>();
            assembler.Configure(layout, catalog, enableOfflineModules);
            EditorUtility.SetDirty(assembler);
        }

        // ---------------------------------------------------------------- player

        private static PlayerRig BuildPlayer(DungeonLayoutData layout, GameConfig config)
        {
            // Loaded here rather than passed in: creating a new scene unloads assets no scene
            // references yet, which would leave a passed-in reference destroyed.
            InputActionAsset inputActions = EmberdelveProjectSetup.EnsureInputAsset();

            GameObject rigGo = new GameObject("Player_Rig");
            int playerLayer = LayerMask.NameToLayer(GameConfig.PlayerLayerName);
            if (playerLayer >= 0) rigGo.layer = playerLayer;
            rigGo.transform.position = layout.CellCenterWorld(layout.playerStartCell) + Vector3.up * 0.15f;

            CharacterController controller = rigGo.AddComponent<CharacterController>();
            controller.height = 1.8f;
            controller.radius = 0.34f;
            controller.center = new Vector3(0f, 0.9f, 0f);
            controller.skinWidth = 0.03f;
            controller.slopeLimit = 52f;
            controller.stepOffset = 0.32f;

            EmberdelveInput input = rigGo.AddComponent<EmberdelveInput>();
            PlayerMovement movement = rigGo.AddComponent<PlayerMovement>();
            PlayerLook look = rigGo.AddComponent<PlayerLook>();
            HealthComponent health = rigGo.AddComponent<HealthComponent>();
            Inventory inventory = rigGo.AddComponent<Inventory>();
            ClassLoadout loadout = rigGo.AddComponent<ClassLoadout>();

            // PlayerRig before the components that RequireComponent(typeof(PlayerRig)), otherwise
            // Unity adds PlayerRig implicitly and the explicit AddComponent fails.
            PlayerRig rig = rigGo.AddComponent<PlayerRig>();

            MeleeCombat melee = rigGo.AddComponent<MeleeCombat>();
            RangedCombat ranged = rigGo.AddComponent<RangedCombat>();
            Interactor interactor = rigGo.AddComponent<Interactor>();
            HotbarController hotbar = rigGo.AddComponent<HotbarController>();

            // Vitals and kit state the rig owns. PlayerRig.WireRig (its Awake) Ensures exactly this set, so
            // the builder Ensures the same way instead of adding its own copies: whether or not Awake already
            // ran on this GameObject, each one exists once and builder, rig and caster end up holding the same
            // pool, status set and caster. In an edit-mode build Awake has not run, so Ensure is also what
            // puts all four on the GameObject for the wiring below to serialize.
            ManaComponent mana = ManaComponent.Ensure(rigGo);
            StatusComponent status = StatusComponent.Ensure(rigGo);
            Equipment equipment = Equipment.Ensure(rigGo);
            SkillCaster skills = rigGo.GetComponent<SkillCaster>();
            if (skills == null) skills = rigGo.AddComponent<SkillCaster>();
            mana.name = "Mana";
            status.name = "Status";
            skills.name = "SkillCaster";
            equipment.name = "Equipment";

            GameObject pivot = new GameObject("Look_Pivot");
            pivot.transform.SetParent(rigGo.transform, false);
            pivot.transform.localPosition = new Vector3(0f, 1.66f, 0f);

            GameObject cameraGo = new GameObject("MainCamera");
            cameraGo.transform.SetParent(pivot.transform, false);
            cameraGo.tag = "MainCamera";

            Camera camera = cameraGo.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.015f, 0.015f, 0.02f, 1f);
            camera.nearClipPlane = 0.05f;
            camera.farClipPlane = 240f;
            camera.fieldOfView = config.fieldOfView;
            EmberdelveVisualSetup.ConfigureCamera(camera);

            GameObject torchGo = new GameObject("Player_Torch");
            torchGo.transform.SetParent(cameraGo.transform, false);
            torchGo.transform.localPosition = new Vector3(0.08f, 0.10f, 0.05f);

            Light torchLight = torchGo.AddComponent<Light>();
            torchLight.type = LightType.Point;
            torchLight.color = new Color(1f, 0.7f, 0.42f, 1f);
            torchLight.range = 9.5f;
            torchLight.intensity = 9f;
            torchLight.shadows = LightShadows.None;
            torchGo.AddComponent<TorchFlicker>();

            GameObject weaponAnchor = new GameObject("Weapon_Anchor");
            weaponAnchor.transform.SetParent(pivot.transform, false);
            weaponAnchor.transform.localPosition = Vector3.zero;

            SetReference(input, "actions", inputActions);
            SetReference(movement, "input", input);
            SetReference(movement, "controller", controller);
            SetReference(movement, "groundMask", LayerMask.GetMask(GameConfig.LevelLayerName));
            SetReference(look, "input", input);
            SetReference(look, "movement", movement);
            SetReference(look, "lookPivot", pivot.transform);
            SetReference(look, "cameraTransform", cameraGo.transform);
            SetReference(rig, "input", input);
            SetReference(rig, "movement", movement);
            SetReference(rig, "look", look);
            SetReference(rig, "health", health);
            SetReference(rig, "mana", mana);
            SetReference(rig, "status", status);
            SetReference(rig, "skills", skills);
            SetReference(rig, "inventory", inventory);
            SetReference(rig, "equipment", equipment);
            SetReference(rig, "loadout", loadout);
            SetReference(loadout, "weaponAnchor", weaponAnchor.transform);
            SetReference(loadout, "meleeCombat", melee);
            SetReference(loadout, "rangedCombat", ranged);
            SetReference(loadout, "targetCamera", camera);
            SetReference(melee, "rig", rig);
            SetReference(melee, "config", config);
            SetReference(ranged, "rig", rig);
            SetReference(ranged, "config", config);
            SetReference(interactor, "rig", rig);
            SetReference(interactor, "config", config);
            SetReference(hotbar, "rig", rig);
            SetReference(hotbar, "inventory", inventory);

            return rig;
        }

        // ---------------------------------------------------------------- content

        private static void BuildLoot(DungeonLayoutData layout, RunDirector director)
        {
            GameObject root = new GameObject("Loot");
            int lootLayer = LayerMask.NameToLayer(GameConfig.LootLayerName);

            for (int i = 0; i < layout.lootSpawns.Count; i++)
            {
                LootSpawnSpec spec = layout.lootSpawns[i];
                if (spec.item == null) continue;

                Vector3 position = layout.CellCenterWorld(spec.cell) + Vector3.up * 0.45f;

                GameObject box = GameObject.CreatePrimitive(PrimitiveType.Cube);
                box.name = "Loot_" + spec.item.displayName + "_" + i;
                box.transform.SetParent(root.transform, false);
                box.transform.position = position;
                box.transform.localScale = spec.item.worldSize;
                if (lootLayer >= 0) box.layer = lootLayer;

                Renderer renderer = box.GetComponent<Renderer>();
                if (renderer != null && spec.item.worldMaterial != null) renderer.sharedMaterial = spec.item.worldMaterial;

                LootPickup pickup = box.AddComponent<LootPickup>();
                pickup.Configure(spec.item, Mathf.Max(1, spec.amount), director);
            }
        }

        private static void BuildEnemies(DungeonLayoutData layout, GameConfig config, RunDirector director)
        {
            GameObject root = new GameObject("Enemies");
            int enemyLayer = LayerMask.NameToLayer(GameConfig.EnemyLayerName);

            for (int i = 0; i < layout.enemySpawns.Count; i++)
            {
                EnemySpawnSpec spec = layout.enemySpawns[i];
                if (spec.enemy == null) continue;

                Vector3 position = layout.CellCenterWorld(spec.cell);

                GameObject enemy = new GameObject("Enemy_" + spec.enemy.displayName + "_" + i);
                enemy.transform.SetParent(root.transform, false);
                enemy.transform.position = position;
                if (enemyLayer >= 0) enemy.layer = enemyLayer;

                HealthComponent health = enemy.AddComponent<HealthComponent>();
                EnemyAgent agent = enemy.AddComponent<EnemyAgent>();

                float bodyScale = spec.enemy.isElite ? 1.25f : 1f;

                GameObject body = GameObject.CreatePrimitive(PrimitiveType.Cube);
                body.name = "Body";
                body.transform.SetParent(enemy.transform, false);
                body.transform.localPosition = new Vector3(0f, 0.95f * bodyScale, 0f);
                body.transform.localScale = new Vector3(0.78f * bodyScale, 1.45f * bodyScale, 0.58f * bodyScale);
                if (enemyLayer >= 0) body.layer = enemyLayer;

                Renderer bodyRenderer = body.GetComponent<Renderer>();
                if (bodyRenderer != null && spec.enemy.bodyMaterial != null) bodyRenderer.sharedMaterial = spec.enemy.bodyMaterial;

                GameObject head = GameObject.CreatePrimitive(PrimitiveType.Cube);
                head.name = "Head";
                head.transform.SetParent(enemy.transform, false);
                head.transform.localPosition = new Vector3(0f, 1.85f * bodyScale, 0f);
                head.transform.localScale = new Vector3(0.44f * bodyScale, 0.44f * bodyScale, 0.44f * bodyScale);
                if (enemyLayer >= 0) head.layer = enemyLayer;

                Renderer headRenderer = head.GetComponent<Renderer>();
                if (headRenderer != null) headRenderer.sharedMaterial = EmberdelveMaterials.EnemyHead;

                SetReference(agent, "data", spec.enemy);
                SetReference(agent, "navGrid", EmberdelveAssetLibrary.NavGrid);
                SetReference(agent, "config", config);
                SetReference(agent, "lootHost", director);
                SetReference(agent, "headCollider", head.GetComponent<Collider>());
                SetReference(agent, "bodyTransform", body.transform);
                SetReference(agent, "groundHeight", 0f);

                // V2: skeleton model as the child "Visual"; the Body/Head cubes (and their colliders) stay, only their renderers go off.
                EmberdelveEnemyVisualFactory.Attach(enemy, spec.enemy, i);
            }
        }

        private static void BuildExtractionGate(DungeonLayoutData layout, GameConfig config, RunDirector director)
        {
            Vector3 center = layout.CellCenterWorld(layout.extractionCell);

            GameObject gate = new GameObject("Ember_Gate");
            gate.transform.position = center;
            int interactLayer = LayerMask.NameToLayer(GameConfig.InteractLayerName);
            if (interactLayer >= 0) gate.layer = interactLayer;

            float width = 3.2f;
            float height = 3.4f;

            CreateBox(gate.transform, "Gate_Pillar_Left",
                center + new Vector3(-width * 0.5f, 0f, -0.35f),
                center + new Vector3(-width * 0.5f + 0.45f, height, 0.35f),
                EmberdelveMaterials.StoneWall, interactLayer, 1.5f, 1.5f);

            CreateBox(gate.transform, "Gate_Pillar_Right",
                center + new Vector3(width * 0.5f - 0.45f, 0f, -0.35f),
                center + new Vector3(width * 0.5f, height, 0.35f),
                EmberdelveMaterials.StoneWall, interactLayer, 1.5f, 1.5f);

            CreateBox(gate.transform, "Gate_Lintel",
                center + new Vector3(-width * 0.5f, height, -0.35f),
                center + new Vector3(width * 0.5f, height + 0.5f, 0.35f),
                EmberdelveMaterials.StoneWall, interactLayer, 1.5f, 1.5f);

            GameObject portal = GameObject.CreatePrimitive(PrimitiveType.Cube);
            portal.name = "Gate_Portal";
            Collider portalCollider = portal.GetComponent<Collider>();
            if (portalCollider != null) UnityEngine.Object.DestroyImmediate(portalCollider);
            portal.transform.SetParent(gate.transform, false);
            portal.transform.localPosition = new Vector3(0f, 1.7f, 0f);
            portal.transform.localScale = new Vector3(2.4f, 3.1f, 1f);
            Renderer portalRenderer = portal.GetComponent<Renderer>();
            if (portalRenderer != null) portalRenderer.sharedMaterial = EmberdelveMaterials.PortalMat;

            GameObject trigger = new GameObject("Gate_Trigger");
            trigger.transform.SetParent(gate.transform, false);
            trigger.transform.localPosition = new Vector3(0f, 1.4f, 0f);
            BoxCollider triggerCollider = trigger.AddComponent<BoxCollider>();
            triggerCollider.isTrigger = true;
            triggerCollider.size = new Vector3(3f, 2.8f, 2.4f);
            if (interactLayer >= 0) trigger.layer = interactLayer;

            GameObject glowGo = new GameObject("Gate_Glow");
            glowGo.transform.SetParent(gate.transform, false);
            glowGo.transform.localPosition = new Vector3(0f, 1.8f, 0f);
            Light glow = glowGo.AddComponent<Light>();
            glow.type = LightType.Point;
            glow.color = new Color(1f, 0.58f, 0.22f, 1f);
            glow.range = 11f;
            glow.intensity = 4f;
            glow.shadows = LightShadows.None;

            ExtractionPoint point = gate.AddComponent<ExtractionPoint>();
            SetReference(point, "config", config);
            SetReference(point, "sinkBehaviour", director);
            SetReference(point, "glowLight", glow);
            SetReference(point, "portalRenderer", portalRenderer);
        }

        // ---------------------------------------------------------------- helpers

        private static void SetReference(Component target, string propertyName, Object value)
        {
            if (target == null)
            {
                Debug.LogWarning($"[Emberdelve] Cannot wire '{propertyName}': target component is null.");
                return;
            }

            SerializedObject serialized = new SerializedObject(target);
            SerializedProperty property = serialized.FindProperty(propertyName);

            if (property == null)
            {
                Debug.LogWarning($"[Emberdelve] {target.GetType().Name} has no serialized field '{propertyName}'.");
                return;
            }

            property.objectReferenceValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void SetReference(Component target, string propertyName, float value)
        {
            if (target == null) return;

            SerializedObject serialized = new SerializedObject(target);
            SerializedProperty property = serialized.FindProperty(propertyName);

            if (property == null)
            {
                Debug.LogWarning($"[Emberdelve] {target.GetType().Name} has no serialized field '{propertyName}'.");
                return;
            }

            if (property.propertyType == SerializedPropertyType.Float) property.floatValue = value;
            else if (property.propertyType == SerializedPropertyType.Integer) property.intValue = Mathf.RoundToInt(value);
        }

        private static void SetReference(Component target, string propertyName, int layerMaskValue)
        {
            if (target == null) return;

            SerializedObject serialized = new SerializedObject(target);
            SerializedProperty property = serialized.FindProperty(propertyName);

            if (property == null)
            {
                Debug.LogWarning($"[Emberdelve] {target.GetType().Name} has no serialized field '{propertyName}'.");
                return;
            }

            if (property.propertyType == SerializedPropertyType.LayerMask) property.intValue = layerMaskValue;
            else if (property.propertyType == SerializedPropertyType.Integer) property.intValue = layerMaskValue;
        }
    }
}
