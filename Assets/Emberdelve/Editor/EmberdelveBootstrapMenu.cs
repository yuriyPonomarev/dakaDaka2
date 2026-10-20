using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using Emberdelve.Core;
using Emberdelve.InputLayer;
using Emberdelve.Level;

namespace Emberdelve.EditorTools
{
    /// <summary>
    /// One-click generation of the vertical slice. Everything it creates lives under Assets/Emberdelve
    /// and can be re-run safely: existing data assets are reused, scenes are regenerated.
    /// </summary>
    public static class EmberdelveBootstrapMenu
    {
        private const string Menu = "Emberdelve/";

        [MenuItem(Menu + "Build Vertical Slice (all)")]
        public static void BuildEverything()
        {
            EmberdelvePaths.EnsureFolders();

            int layers = EmberdelveProjectSetup.EnsureLayers();
            if (layers > 0) Debug.Log($"[Emberdelve] Added {layers} layer(s) to TagManager.");

            AssetDatabase.Refresh();

            EmberdelveAssetLibrary.BuildAll();

            EmberdelveLobbyBuilder.Build();
            EmberdelveLevelBuilder.Build();

            EmberdelveProjectSetup.EnsureBuildScenes();

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            EditorSceneManager.OpenScene(EmberdelvePaths.LobbyScenePath, OpenSceneMode.Single);

            Debug.Log("[Emberdelve] Vertical slice built. Open " +
                      EmberdelvePaths.LobbyScenePath + " and press Play, then DESCEND.");
        }

        [MenuItem(Menu + "Build Data Assets Only")]
        public static void BuildDataOnly()
        {
            EmberdelvePaths.EnsureFolders();
            EmberdelveProjectSetup.EnsureLayers();
            EmberdelveAssetLibrary.BuildAll();
            EmberdelveProjectSetup.EnsureInputAsset();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        /// <summary>
        /// Rewrites the authored .inputactions asset from EmberdelveInputFallback, which is the single
        /// source of truth for bindings. Callable headless so a binding change can be applied without an
        /// open Editor; the .meta is kept, so scene references to the asset survive.
        /// </summary>
        [MenuItem(Menu + "Regenerate Input Actions Asset")]
        public static void RegenerateInputAsset()
        {
            EmberdelveProjectSetup.RebuildInputAsset();
            InputActionAsset asset = AssetDatabase.LoadAssetAtPath<InputActionAsset>(EmberdelvePaths.InputAssetPath);
            if (asset == null)
            {
                Debug.LogError("[Emberdelve] Input actions asset missing after regeneration.");
                return;
            }

            InputActionMap map = asset.FindActionMap(EmberdelveInputFallback.PlayerMapName);
            if (map == null)
            {
                Debug.LogError("[Emberdelve] Regenerated input asset has no Player map.");
                return;
            }

            string[] required = { "Move", "Look", "PrimaryAttack", "SecondaryAction", "Reload", "SwapWeapon", "Skill1", "Skill2" };
            int missing = 0;
            for (int i = 0; i < required.Length; i++)
            {
                InputAction action = map.FindAction(required[i]);
                if (action == null)
                {
                    missing++;
                    Debug.LogError($"[Emberdelve] Input asset is missing action '{required[i]}'.");
                    continue;
                }

                Debug.Log($"[Emberdelve] input action {required[i]} -> {action.GetBindingDisplayString(0)}");
            }

            if (missing > 0) Debug.LogError($"[Emberdelve] {missing} required input action(s) missing.");
        }

        [MenuItem(Menu + "Build Dungeon Scene Only")]
        public static void BuildDungeonOnly()
        {
            EmberdelveAssetLibrary.BuildAll();
            EmberdelveLevelBuilder.Build();
            EmberdelveProjectSetup.EnsureBuildScenes();
            AssetDatabase.SaveAssets();
            EditorSceneManager.OpenScene(EmberdelvePaths.DungeonScenePath, OpenSceneMode.Single);
        }

        [MenuItem(Menu + "Build Lobby Scene Only")]
        public static void BuildLobbyOnly()
        {
            EmberdelveAssetLibrary.BuildAll();
            EmberdelveLobbyBuilder.Build();
            EmberdelveProjectSetup.EnsureBuildScenes();
            AssetDatabase.SaveAssets();
            EditorSceneManager.OpenScene(EmberdelvePaths.LobbyScenePath, OpenSceneMode.Single);
        }

        [MenuItem(Menu + "Open Lobby Scene")]
        public static void OpenLobby()
        {
            if (!System.IO.File.Exists(EmberdelvePaths.LobbyScenePath))
            {
                Debug.LogWarning("[Emberdelve] Lobby scene not found - run 'Build Vertical Slice (all)' first.");
                return;
            }

            EditorSceneManager.OpenScene(EmberdelvePaths.LobbyScenePath, OpenSceneMode.Single);
        }

        [MenuItem(Menu + "Open Dungeon Scene")]
        public static void OpenDungeon()
        {
            if (!System.IO.File.Exists(EmberdelvePaths.DungeonScenePath))
            {
                Debug.LogWarning("[Emberdelve] Dungeon scene not found - run 'Build Vertical Slice (all)' first.");
                return;
            }

            EditorSceneManager.OpenScene(EmberdelvePaths.DungeonScenePath, OpenSceneMode.Single);
        }

        /// <summary>
        /// Headless verification of the built slice: level connectivity through the navigation grid,
        /// and wiring of every gameplay component in the dungeon scene.
        /// </summary>
        [MenuItem(Menu + "Validate Slice")]
        public static void ValidateSlice()
        {
            EmberdelveAssetLibrary.BuildAll();

            Emberdelve.Level.DungeonLayoutData layout = EmberdelveAssetLibrary.Layout;
            Emberdelve.Core.NavGridData grid = EmberdelveAssetLibrary.NavGrid;

            List<string> failures = new List<string>();
            if (layout == null) failures.Add("missing DungeonLayoutData asset");
            if (grid == null) failures.Add("missing NavGridData asset");
            if (failures.Count > 0)
            {
                for (int i = 0; i < failures.Count; i++) Debug.LogError("[Emberdelve][Validate] " + failures[i]);
                return;
            }

            foreach (string problem in layout.Validate()) failures.Add("layout: " + problem);

            // ---- 1. Navigation grid agrees with the layout ----
            bool[,] walkable = layout.BuildWalkableGrid();
            int layoutCells = 0;
            int mismatch = 0;
            for (int y = 0; y < layout.gridHeight; y++)
            {
                for (int x = 0; x < layout.gridWidth; x++)
                {
                    if (walkable[x, y]) layoutCells++;
                    if (walkable[x, y] != grid.IsWalkable(x, y)) mismatch++;
                }
            }
            Debug.Log($"[Emberdelve][Validate] walkable cells: layout={layoutCells} navgrid={grid.width}x{grid.height} mismatch={mismatch}");
            if (mismatch > 0) failures.Add($"nav grid mask disagrees with the layout on {mismatch} cells");

            // ---- 2. Breadth-first reachability from the player start ----
            HashSet<Vector2Int> visited = new HashSet<Vector2Int>();
            Queue<Vector2Int> frontier = new Queue<Vector2Int>();
            Vector2Int start = layout.playerStartCell;
            if (!grid.IsWalkable(start.x, start.y)) failures.Add("player start cell is not walkable on the nav grid");
            visited.Add(start);
            frontier.Enqueue(start);
            while (frontier.Count > 0)
            {
                Vector2Int cell = frontier.Dequeue();
                Vector2Int[] neighbours =
                {
                    new Vector2Int(cell.x + 1, cell.y), new Vector2Int(cell.x - 1, cell.y),
                    new Vector2Int(cell.x, cell.y + 1), new Vector2Int(cell.x, cell.y - 1)
                };
                for (int i = 0; i < neighbours.Length; i++)
                {
                    Vector2Int n = neighbours[i];
                    if (visited.Contains(n)) continue;
                    if (!grid.IsWalkable(n.x, n.y)) continue;
                    visited.Add(n);
                    frontier.Enqueue(n);
                }
            }
            Debug.Log($"[Emberdelve][Validate] cells reachable from the player start: {visited.Count}");

            Vector2Int extraction = layout.extractionCell;
            if (!visited.Contains(extraction)) failures.Add($"extraction cell {extraction} is NOT reachable from the player start");

            for (int i = 0; i < layout.lootSpawns.Count; i++)
                if (!visited.Contains(layout.lootSpawns[i].cell)) failures.Add($"loot spawn {i} at {layout.lootSpawns[i].cell} is not reachable");
            for (int i = 0; i < layout.enemySpawns.Count; i++)
                if (!visited.Contains(layout.enemySpawns[i].cell)) failures.Add($"enemy spawn {i} at {layout.enemySpawns[i].cell} is not reachable");

            // A* through the runtime pathfinder used by enemies.
            Emberdelve.AI.NavGridPathfinder pathfinder = new Emberdelve.AI.NavGridPathfinder(grid);
            List<Vector3> route = new List<Vector3>();
            bool routed = pathfinder.TryFindPath(
                layout.CellCenterWorld(start) + Vector3.up * 0.25f,
                layout.CellCenterWorld(extraction) + Vector3.up * 0.25f,
                route);
            Debug.Log(routed
                ? $"[Emberdelve][Validate] pathfinder route start->extraction: {route.Count} waypoints"
                : "[Emberdelve][Validate] pathfinder returned no route");
            if (!routed || route.Count < 2) failures.Add("runtime pathfinder could not route start -> extraction");

            // ---- 3. Scene wiring ----
            EditorSceneManager.OpenScene(EmberdelvePaths.DungeonScenePath, OpenSceneMode.Single);

            Emberdelve.Player.PlayerRig rig = Object.FindFirstObjectByType<Emberdelve.Player.PlayerRig>();
            if (rig == null) failures.Add("no PlayerRig in the dungeon scene");
            else
            {
                if (rig.GetComponent<Emberdelve.Movement.PlayerMovement>() == null) failures.Add("PlayerRig: missing PlayerMovement");
                if (rig.GetComponent<Emberdelve.Movement.PlayerLook>() == null) failures.Add("PlayerRig: missing PlayerLook");
                if (rig.GetComponent<Emberdelve.Combat.HealthComponent>() == null) failures.Add("PlayerRig: missing HealthComponent");
                if (rig.GetComponent<Emberdelve.Combat.MeleeCombat>() == null) failures.Add("PlayerRig: missing MeleeCombat");
                if (rig.GetComponent<Emberdelve.Combat.RangedCombat>() == null) failures.Add("PlayerRig: missing RangedCombat");
                if (rig.GetComponent<Emberdelve.Items.Inventory>() == null) failures.Add("PlayerRig: missing Inventory");
                if (rig.GetComponent<Emberdelve.Interaction.Interactor>() == null) failures.Add("PlayerRig: missing Interactor");
                if (rig.GetComponent<Emberdelve.InputLayer.EmberdelveInput>() == null) failures.Add("PlayerRig: missing EmberdelveInput");
                if (rig.GetComponentInChildren<Camera>() == null) failures.Add("PlayerRig: missing Camera");
            }

            Emberdelve.InputLayer.EmberdelveInput input =
                Object.FindFirstObjectByType<Emberdelve.InputLayer.EmberdelveInput>();
            if (input != null)
            {
                SerializedObject serialized = new SerializedObject(input);
                SerializedProperty actions = serialized.FindProperty("actions");
                if (actions != null && actions.objectReferenceValue == null)
                    failures.Add("EmberdelveInput.actions is not assigned (runtime fallback will be used)");
            }

            Emberdelve.AI.EnemyAgent[] enemies = Object.FindObjectsByType<Emberdelve.AI.EnemyAgent>(FindObjectsSortMode.None);
            for (int i = 0; i < enemies.Length; i++)
            {
                SerializedObject serialized = new SerializedObject(enemies[i]);
                if (enemies[i].Data == null) failures.Add($"enemy {i}: no EnemyData");
                if (serialized.FindProperty("navGrid").objectReferenceValue == null) failures.Add($"enemy {i}: no NavGridData");
                if (serialized.FindProperty("config").objectReferenceValue == null) failures.Add($"enemy {i}: no GameConfig");
                if (enemies[i].GetComponent<Emberdelve.Combat.HealthComponent>() == null) failures.Add($"enemy {i}: no HealthComponent");
                if (enemies[i].GetComponentInChildren<Collider>() == null) failures.Add($"enemy {i}: no Collider");
            }

            Emberdelve.Items.LootPickup[] loot = Object.FindObjectsByType<Emberdelve.Items.LootPickup>(FindObjectsSortMode.None);
            for (int i = 0; i < loot.Length; i++)
                if (loot[i].Item == null) failures.Add($"loot {i}: no ItemData");

            if (Object.FindFirstObjectByType<Emberdelve.Interaction.ExtractionPoint>() == null)
                failures.Add("no ExtractionPoint in the dungeon scene");
            if (Object.FindFirstObjectByType<Emberdelve.Flow.RunDirector>() == null)
                failures.Add("no RunDirector in the dungeon scene");
            if (Object.FindFirstObjectByType<Emberdelve.UI.HudController>() == null)
                failures.Add("no HudController in the dungeon scene");
            Emberdelve.Combat.LocalCombatAuthority authority =
                Object.FindFirstObjectByType<Emberdelve.Combat.LocalCombatAuthority>();
            if (authority == null) failures.Add("no LocalCombatAuthority in the dungeon scene");
            else if (!(authority is Emberdelve.Combat.ICombatAuthority))
                failures.Add("LocalCombatAuthority does not implement ICombatAuthority");

            int renderersWithoutMaterial = 0;
            foreach (Renderer renderer in Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None))
                if (renderer.sharedMaterials.Length == 0 || renderer.sharedMaterials[0] == null) renderersWithoutMaterial++;
            if (renderersWithoutMaterial > 0) failures.Add($"{renderersWithoutMaterial} renderer(s) without a material");

            Debug.Log($"[Emberdelve][Validate] scene objects: enemies={enemies.Length} loot={loot.Length} " +
                      $"renderers={Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None).Length}");

            if (failures.Count == 0)
            {
                Debug.Log("[Emberdelve][Validate] PASS - level is connected and every gameplay reference resolves.");
            }
            else
            {
                Debug.LogError($"[Emberdelve][Validate] FAIL ({failures.Count}):");
                for (int i = 0; i < failures.Count; i++) Debug.LogError("  - " + failures[i]);
            }
        }

        /// <summary>
        /// Diagnostic / repair: reports what the asset database returns for the generated input asset
        /// and re-wires EmberdelveInput.actions in the dungeon scene.
        /// </summary>
        public static void WireInputAssetInDungeon()
        {
            InputActionAsset asset = EmberdelveProjectSetup.EnsureInputAsset();
            Debug.Log($"[Emberdelve][Diag] EnsureInputAsset => {(asset == null ? "NULL" : asset.name + " / " + AssetDatabase.GetAssetPath(asset))}");

            Object[] all = AssetDatabase.LoadAllAssetsAtPath(EmberdelvePaths.InputAssetPath);
            System.Text.StringBuilder builder = new System.Text.StringBuilder();
            for (int i = 0; i < all.Length; i++) builder.Append(all[i].GetType().Name).Append('!').Append(all[i].name).Append(", ");
            Debug.Log($"[Emberdelve][Diag] LoadAllAssetsAtPath returned {all.Length}: {builder}");

            if (asset == null && all.Length > 0) asset = all[0] as InputActionAsset;
            if (asset == null)
            {
                Debug.LogWarning("[Emberdelve][Diag] Input asset unavailable; runtime fallback will be used.");
                return;
            }

            EditorSceneManager.OpenScene(EmberdelvePaths.DungeonScenePath, OpenSceneMode.Single);

            // Re-load after the scene opened: opening a scene unloads assets it does not reference.
            asset = EmberdelveProjectSetup.EnsureInputAsset();
            if (asset == null)
            {
                Debug.LogWarning("[Emberdelve][Diag] Input asset unavailable after opening the scene.");
                return;
            }

            Emberdelve.InputLayer.EmberdelveInput input =
                Object.FindFirstObjectByType<Emberdelve.InputLayer.EmberdelveInput>();

            if (input == null)
            {
                Debug.LogWarning("[Emberdelve][Diag] No EmberdelveInput in the dungeon scene.");
                return;
            }

            SerializedObject serialized = new SerializedObject(input);
            SerializedProperty property = serialized.FindProperty("actions");

            if (property == null)
            {
                Debug.LogWarning("[Emberdelve][Diag] EmberdelveInput has no 'actions' serialized property.");
                return;
            }

            Debug.Log($"[Emberdelve][Diag] property type='{property.type}' propertyType={property.propertyType} before={(property.objectReferenceValue == null ? "null" : property.objectReferenceValue.GetType().Name)}");
            Debug.Log($"[Emberdelve][Diag] asset hideFlags={asset.hideFlags} instanceId={asset.GetInstanceID()} type={asset.GetType().FullName}");

            property.objectReferenceValue = asset;
            serialized.Update();
            Debug.Log($"[Emberdelve][Diag] after set (before apply) => {(property.objectReferenceValue == null ? "null" : property.objectReferenceValue.name)}");

            bool applied = serialized.ApplyModifiedPropertiesWithoutUndo();
            Debug.Log($"[Emberdelve][Diag] ApplyModifiedPropertiesWithoutUndo => {applied}");

            serialized = new SerializedObject(input);
            Debug.Log($"[Emberdelve][Diag] re-read => {(serialized.FindProperty("actions").objectReferenceValue == null ? "null" : "set")}");

            Scene scene = SceneManager.GetActiveScene();
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log("[Emberdelve][Diag] Wired actions on " + input.name);
        }

        /// <summary>
        /// Repair / bootstrap: gives the dungeon scene's RunDirector the layout asset so containers are built
        /// from data at run start. This is a reference assignment on an existing scene, not a rebuild, so the
        /// scene a previous milestone produced keeps working.
        /// </summary>
        [MenuItem(Menu + "Wire Layout Into Dungeon Scene")]
        public static void WireLayoutInDungeon()
        {
            BuildDataOnly();

            DungeonLayoutData layout = AssetDatabase.LoadAssetAtPath<DungeonLayoutData>(
                EmberdelvePaths.Data + "/DungeonLayout_AshenHalls.asset");

            if (layout == null)
            {
                Debug.LogWarning("[Emberdelve][Wire] No layout asset; chests cannot be spawned.");
                return;
            }

            EditorSceneManager.OpenScene(EmberdelvePaths.DungeonScenePath, OpenSceneMode.Single);

            layout = AssetDatabase.LoadAssetAtPath<DungeonLayoutData>(EmberdelvePaths.Data + "/DungeonLayout_AshenHalls.asset");

            Emberdelve.Flow.RunDirector director = Object.FindFirstObjectByType<Emberdelve.Flow.RunDirector>();
            if (director == null)
            {
                Debug.LogWarning("[Emberdelve][Wire] No RunDirector in the dungeon scene.");
                return;
            }

            SerializedObject serialized = new SerializedObject(director);
            SerializedProperty property = serialized.FindProperty("layout");

            if (property == null)
            {
                Debug.LogWarning("[Emberdelve][Wire] RunDirector has no 'layout' serialized property.");
                return;
            }

            property.objectReferenceValue = layout;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            Scene scene = SceneManager.GetActiveScene();
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);

            Debug.Log($"[Emberdelve][Wire] RunDirector.layout = {layout.levelName} ({layout.chestSpawns.Count} chest specs) on {director.name}");
        }
    }
}
