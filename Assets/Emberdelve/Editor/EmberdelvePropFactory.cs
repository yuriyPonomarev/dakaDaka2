using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Emberdelve.Interaction;
using Emberdelve.Level;

namespace Emberdelve.EditorTools
{
    /// <summary>
    /// V2: the KayKit dungeon props. Two jobs, both visual only:
    ///   1. the chest model set (Resources/Emberdelve/ChestVisualSet.asset) the runtime ChestSpawner dresses its cubes with;
    ///   2. a deterministic scatter of decorative props (barrels, crates, pillars, banners, candles, shelves, rubble) along the
    ///      walls of the rooms, under a scene root "Props". Props have NO colliders (they cannot block movement, projectiles,
    ///      line of sight or the nav grid; the player walks through them, like the torch sconces), and they are placed only in
    ///      wall-adjacent cells of rooms, pushed against the wall, at least one cell away from every doorway and from every
    ///      spawn, loot, chest, enemy, start and extraction cell. The layout is the only input: same layout, same props.
    /// </summary>
    internal static class EmberdelvePropFactory
    {
        public const string ChestSetPath = "Assets/Emberdelve/Resources/Emberdelve/ChestVisualSet.asset";
        public const string PropsRootName = "Props";

        /// <summary>One placed prop (kept for the gate).</summary>
        internal struct Placement
        {
            public string Model;
            public Vector2Int Cell;
            public string Room;
        }

        internal static readonly List<Placement> LastPlacements = new List<Placement>();

        private const int Density = 42;       // percent of the eligible, non-adjacent cells that get a prop
        private const int MaxPerRoom = 5;
        private const float WallGap = 0.03f;  // metres between the prop's back and the wall face
        private const float MaxDepth = 1.5f;  // metres a prop may reach into the room

        // ---------------------------------------------------------------- chest set

        /// <summary>Writes the chest model set. Supply = wooden chest, Ember = gold chest, Sealed = gold chest tinted cold violet.</summary>
        public static ChestVisualSet EnsureChestVisualSet()
        {
            EmberdelveVisualSetup.EnsureFolder("Assets/Emberdelve/Resources/Emberdelve");

            GameObject wood = EmberdelveVendorImport.LoadDungeonModel("chest");
            GameObject gold = EmberdelveVendorImport.LoadDungeonModel("chest_gold");

            ChestVisualSet set = AssetDatabase.LoadAssetAtPath<ChestVisualSet>(ChestSetPath);
            bool created = set == null;
            if (created) set = ScriptableObject.CreateInstance<ChestVisualSet>();

            set.entries = new[]
            {
                NewEntry(ChestVariant.Supply, wood, Color.white),
                NewEntry(ChestVariant.Ember, gold, new Color(1f, 0.82f, 0.62f, 1f)),
                NewEntry(ChestVariant.Sealed, gold, new Color(0.66f, 0.7f, 1f, 1f))
            };

            if (created) AssetDatabase.CreateAsset(set, ChestSetPath);
            else EditorUtility.SetDirty(set);
            AssetDatabase.SaveAssets();

            if (wood == null || gold == null) Debug.LogWarning("[Emberdelve][Props] chest models are not imported; the chests keep their blockout cubes.");
            return set;
        }

        private static ChestVisualSet.Entry NewEntry(ChestVariant variant, GameObject model, Color tint)
        {
            return new ChestVisualSet.Entry { variant = variant, model = model, tint = tint, lidNameContains = "lid", frontSign = 1f, openAngle = 105f };
        }

        // ---------------------------------------------------------------- scatter

        private static string[] ThemeFor(string roomName)
        {
            string n = (roomName ?? string.Empty).ToLowerInvariant();
            if (n.Contains("entry")) return new[] { "barrel_small_stack", "crates_stacked", "barrel_large", "box_stacked", "banner_red" };
            if (n.Contains("library")) return new[] { "shelf_small_candles", "table_long_broken", "candle_triple", "banner_patternA_red", "box_stacked" };
            if (n.Contains("central")) return new[] { "pillar_decorated", "banner_red", "candle_triple", "rubble_half", "pillar" };
            if (n.Contains("armory")) return new[] { "crates_stacked", "sword_shield_broken", "barrel_large", "box_stacked", "trunk_medium_A" };
            if (n.Contains("chapel")) return new[] { "candle_triple", "candle_lit", "banner_patternA_red", "pillar", "bottle_A_green" };
            if (n.Contains("crypt")) return new[] { "rubble_large", "rubble_half", "column", "keyring_hanging", "coin_stack_small", "bottle_A_green" };
            if (n.Contains("gate")) return new[] { "column", "pillar_decorated", "banner_red", "rubble_half" };
            return new[] { "barrel_large", "crates_stacked", "rubble_half" };
        }

        /// <summary>Height of the model's lowest point above the floor: wall hangings float, everything else stands.</summary>
        private static float ElevationOf(string model)
        {
            if (model.StartsWith("banner")) return 0.9f;
            if (model.StartsWith("keyring")) return 1.5f;
            return 0f;
        }

        private static uint Hash(int a, int b, int c)
        {
            unchecked
            {
                uint h = 2166136261u;
                h = (h ^ (uint)a) * 16777619u;
                h = (h ^ (uint)b) * 16777619u;
                h = (h ^ (uint)c) * 16777619u;
                h ^= h >> 15;
                h *= 2246822519u;
                h ^= h >> 13;
                return h;
            }
        }

        private static void Reserve(HashSet<Vector2Int> set, Vector2Int cell, int radius)
        {
            for (int dy = -radius; dy <= radius; dy++)
                for (int dx = -radius; dx <= radius; dx++)
                    set.Add(new Vector2Int(cell.x + dx, cell.y + dy));
        }

        private static bool Walkable(bool[,] walkable, int x, int y)
        {
            if (x < 0 || y < 0 || x >= walkable.GetLength(0) || y >= walkable.GetLength(1)) return false;
            return walkable[x, y];
        }

        private static bool InRoom(RoomSpec room, int x, int y)
        {
            return x >= Mathf.Min(room.minCell.x, room.maxCell.x) && x <= Mathf.Max(room.minCell.x, room.maxCell.x)
                && y >= Mathf.Min(room.minCell.y, room.maxCell.y) && y <= Mathf.Max(room.minCell.y, room.maxCell.y);
        }

        /// <summary>Cells that must stay free of props (spawns, loot, chests, enemies, start, extraction and a margin around them).</summary>
        internal static HashSet<Vector2Int> ReservedCells(DungeonLayoutData layout)
        {
            HashSet<Vector2Int> reserved = new HashSet<Vector2Int>();
            Reserve(reserved, layout.playerStartCell, 1);
            Reserve(reserved, layout.extractionCell, 2);
            for (int i = 0; i < layout.playerSpawns.Count; i++) Reserve(reserved, layout.playerSpawns[i], 1);
            for (int i = 0; i < layout.lootSpawns.Count; i++) Reserve(reserved, layout.lootSpawns[i].cell, 0);
            for (int i = 0; i < layout.enemySpawns.Count; i++) Reserve(reserved, layout.enemySpawns[i].cell, 1);
            for (int i = 0; i < layout.chestSpawns.Count; i++) if (layout.chestSpawns[i] != null) Reserve(reserved, layout.chestSpawns[i].cell, 1);
            return reserved;
        }

        /// <summary>Wall sides of a cell as unit vectors pointing INTO the room (away from the wall).</summary>
        internal static List<Vector3> WallNormals(bool[,] walkable, int x, int y)
        {
            List<Vector3> normals = new List<Vector3>(4);
            if (!Walkable(walkable, x, y + 1)) normals.Add(Vector3.back);     // wall to +Z, the prop faces -Z
            if (!Walkable(walkable, x, y - 1)) normals.Add(Vector3.forward);
            if (!Walkable(walkable, x + 1, y)) normals.Add(Vector3.left);
            if (!Walkable(walkable, x - 1, y)) normals.Add(Vector3.right);
            return normals;
        }

        /// <summary>A cell is a prop candidate when it lies in the room, touches a wall, and all its walkable neighbours (8-neighbourhood) are in the same room.</summary>
        internal static bool IsEligible(DungeonLayoutData layout, bool[,] walkable, RoomSpec room, int x, int y, HashSet<Vector2Int> reserved)
        {
            if (!InRoom(room, x, y) || !Walkable(walkable, x, y)) return false;
            if (reserved.Contains(new Vector2Int(x, y))) return false;
            if (WallNormals(walkable, x, y).Count == 0) return false;

            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    if (Walkable(walkable, x + dx, y + dy) && !InRoom(room, x + dx, y + dy)) return false; // next to a doorway
                }
            }

            return true;
        }

        public static GameObject BuildProps(DungeonLayoutData layout)
        {
            LastPlacements.Clear();
            GameObject root = new GameObject(PropsRootName);
            if (layout == null) return root;

            bool[,] walkable = layout.BuildWalkableGrid();
            HashSet<Vector2Int> reserved = ReservedCells(layout);
            HashSet<Vector2Int> taken = new HashSet<Vector2Int>();
            float cell = layout.CellSize;
            float wallFace = cell * 0.5f - Mathf.Max(0.05f, layout.wallThickness) * 0.5f;
            int missingModels = 0;

            for (int r = 0; r < layout.rooms.Count; r++)
            {
                RoomSpec room = layout.rooms[r];
                string[] theme = ThemeFor(room.roomName);
                int placedInRoom = 0;
                int x0 = Mathf.Min(room.minCell.x, room.maxCell.x), x1 = Mathf.Max(room.minCell.x, room.maxCell.x);
                int y0 = Mathf.Min(room.minCell.y, room.maxCell.y), y1 = Mathf.Max(room.minCell.y, room.maxCell.y);

                for (int y = y0; y <= y1 && placedInRoom < MaxPerRoom; y++)
                {
                    for (int x = x0; x <= x1 && placedInRoom < MaxPerRoom; x++)
                    {
                        if (!IsEligible(layout, walkable, room, x, y, reserved)) continue;

                        uint h = Hash(x, y, r + 17);
                        if (h % 100u >= (uint)Density) continue;
                        if (HasNeighbour(taken, x, y)) continue;

                        string model = theme[(int)((h >> 8) % (uint)theme.Length)];
                        GameObject asset = EmberdelveVendorImport.LoadDungeonModel(model);
                        if (asset == null) { missingModels++; continue; }

                        List<Vector3> normals = WallNormals(walkable, x, y);
                        Vector3 normal = normals[(int)((h >> 16) % (uint)normals.Count)];
                        float jitter = (((h >> 20) & 0xFFu) / 255f - 0.5f) * 0.6f;

                        Place(root.transform, asset, model, layout, new Vector2Int(x, y), normal, wallFace, jitter);
                        taken.Add(new Vector2Int(x, y));
                        placedInRoom++;
                        LastPlacements.Add(new Placement { Model = model, Cell = new Vector2Int(x, y), Room = room.roomName });
                    }
                }
            }

            if (missingModels > 0) Debug.LogWarning("[Emberdelve][Props] " + missingModels + " prop model(s) are not imported and were skipped.");
            Debug.Log("[Emberdelve][Props] placed " + LastPlacements.Count + " decorative props (no colliders).");
            return root;
        }

        private static bool HasNeighbour(HashSet<Vector2Int> taken, int x, int y)
        {
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                    if (taken.Contains(new Vector2Int(x + dx, y + dy))) return true;
            return false;
        }

        private static void Place(Transform parent, GameObject asset, string model, DungeonLayoutData layout, Vector2Int cell, Vector3 normal, float wallFace, float jitter)
        {
            Vector3 centre = layout.CellCenterWorld(cell);
            centre.y = layout.origin.y;

            GameObject prop = (GameObject)PrefabUtility.InstantiatePrefab(asset, parent);
            prop.name = "Prop_" + model + "_" + cell.x + "_" + cell.y;

            // Pure visuals: whatever the import produced, nothing on a prop may collide.
            Collider[] colliders = prop.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < colliders.Length; i++) Object.DestroyImmediate(colliders[i]);

            // Front (model +Z) faces into the room.
            prop.transform.SetPositionAndRotation(centre, Quaternion.LookRotation(normal, Vector3.up));
            Renderer[] renderers = prop.GetComponentsInChildren<Renderer>(true);
            if (!TryGetBounds(renderers, out Bounds bounds)) return;

            float depth = AlongAxis(bounds.size, normal);
            if (depth > MaxDepth)
            {
                float scale = Mathf.Max(0.6f, MaxDepth / depth);
                prop.transform.localScale = Vector3.one * scale;
                TryGetBounds(renderers, out bounds);
            }

            Vector3 tangent = new Vector3(-normal.z, 0f, normal.x);
            float halfDepth = AlongAxis(bounds.size, normal) * 0.5f;
            float backNow = Vector3.Dot(bounds.center - centre, -normal) + halfDepth;     // distance of the back face from the cell centre, towards the wall
            float shiftBack = (wallFace - WallGap) - backNow;
            float lateralNow = Vector3.Dot(bounds.center - centre, tangent);
            float shiftLateral = jitter - lateralNow;
            float shiftUp = (layout.origin.y + ElevationOf(model)) - bounds.min.y;

            prop.transform.position += -normal * shiftBack + tangent * shiftLateral + Vector3.up * shiftUp;

            Transform[] all = prop.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++) GameObjectUtility.SetStaticEditorFlags(all[i].gameObject, StaticEditorFlags.BatchingStatic);
        }

        private static float AlongAxis(Vector3 size, Vector3 axis)
        {
            return Mathf.Abs(axis.x) * size.x + Mathf.Abs(axis.z) * size.z;
        }

        private static bool TryGetBounds(Renderer[] renderers, out Bounds bounds)
        {
            bounds = new Bounds();
            bool any = false;
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] == null) continue;
                if (!any) { bounds = renderers[i].bounds; any = true; }
                else bounds.Encapsulate(renderers[i].bounds);
            }

            return any;
        }
    }
}
