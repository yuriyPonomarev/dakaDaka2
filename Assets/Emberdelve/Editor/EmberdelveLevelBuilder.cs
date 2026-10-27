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
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // Re-load every data asset and material now: creating a new scene unloads assets that
            // no loaded scene references yet, which can leave earlier references destroyed.
            EmberdelveAssetLibrary.BuildAll();

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
            BuildExtractionGate(layout, config, director);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, EmberdelvePaths.DungeonScenePath);

            return scene;
        }

        // ---------------------------------------------------------------- environment

        private static void SetupEnvironment()
        {
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.045f, 0.05f, 0.07f, 1f);
            RenderSettings.ambientIntensity = 1f;
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Exponential;
            RenderSettings.fogColor = new Color(0.04f, 0.035f, 0.03f, 1f);
            RenderSettings.fogDensity = 0.035f;

            GameObject lightGo = new GameObject("Moon_Slit_Directional");
            lightGo.transform.rotation = Quaternion.Euler(58f, 32f, 0f);
            Light light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.color = new Color(0.42f, 0.52f, 0.78f, 1f);
            light.intensity = 0.55f;
            light.shadows = LightShadows.Soft;
            light.shadowStrength = 0.55f;
            RenderSettings.sun = light;
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
            int levelLayer = LayerMask.NameToLayer(GameConfig.LevelLayerName);

            List<RectInt> patches = MergeRectangles(walkable);

            for (int i = 0; i < patches.Count; i++)
            {
                RectInt patch = patches[i];
                Vector3 min = origin + new Vector3(patch.xMin * cell, -layout.floorThickness, patch.yMin * cell);
                Vector3 max = origin + new Vector3(patch.xMax * cell, 0f, patch.yMax * cell);
                CreateBox(levelRoot.transform, "Floor_" + i, min, max, EmberdelveMaterials.StoneFloor, levelLayer);

                Vector3 ceilingMin = origin + new Vector3(patch.xMin * cell, layout.wallHeight, patch.yMin * cell);
                Vector3 ceilingMax = origin + new Vector3(patch.xMax * cell, layout.wallHeight + layout.ceilingThickness, patch.yMax * cell);
                CreateBox(levelRoot.transform, "Ceiling_" + i, ceilingMin, ceilingMax, EmberdelveMaterials.StoneCeiling, levelLayer);
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
                    CreateBox(levelRoot.transform, "Wall_Z" + edge + "_" + start, min, max, EmberdelveMaterials.StoneWall, levelLayer);
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
                    CreateBox(levelRoot.transform, "Wall_X" + edge + "_" + start, min, max, EmberdelveMaterials.StoneWall, levelLayer);
                }
            }

            BuildRoomMarkers(levelRoot.transform, layout);
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

        private static void CreateBox(Transform parent, string name, Vector3 min, Vector3 max, Material material, int layer)
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

                GameObject sconce = GameObject.CreatePrimitive(PrimitiveType.Cube);
                sconce.name = "Sconce";
                Collider collider = sconce.GetComponent<Collider>();
                if (collider != null) UnityEngine.Object.DestroyImmediate(collider);
                sconce.transform.SetParent(torch.transform, false);
                sconce.transform.localPosition = Vector3.zero;
                sconce.transform.localScale = new Vector3(0.18f, 0.42f, 0.18f);
                Renderer renderer = sconce.GetComponent<Renderer>();
                if (renderer != null) renderer.sharedMaterial = EmberdelveMaterials.TorchMat;

                Light light = torch.AddComponent<Light>();
                light.type = LightType.Point;
                light.color = new Color(1f, 0.66f, 0.34f, 1f);
                light.range = spec.range;
                light.intensity = spec.intensity;
                light.shadows = LightShadows.None;

                torch.AddComponent<TorchFlicker>();
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
            return director;
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

            GameObject torchGo = new GameObject("Player_Torch");
            torchGo.transform.SetParent(cameraGo.transform, false);
            torchGo.transform.localPosition = new Vector3(0.3f, -0.18f, 0.25f);

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
                EmberdelveMaterials.StoneWall, interactLayer);

            CreateBox(gate.transform, "Gate_Pillar_Right",
                center + new Vector3(width * 0.5f - 0.45f, 0f, -0.35f),
                center + new Vector3(width * 0.5f, height, 0.35f),
                EmberdelveMaterials.StoneWall, interactLayer);

            CreateBox(gate.transform, "Gate_Lintel",
                center + new Vector3(-width * 0.5f, height, -0.35f),
                center + new Vector3(width * 0.5f, height + 0.5f, 0.35f),
                EmberdelveMaterials.StoneWall, interactLayer);

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
