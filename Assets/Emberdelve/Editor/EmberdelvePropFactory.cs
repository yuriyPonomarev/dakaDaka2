using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Emberdelve.Interaction;
using Emberdelve.Level;

namespace Emberdelve.EditorTools
{
    /// <summary>
    /// V3a (was V2 KayKit): the realistic CC0 Poly Haven props (see <see cref="EmberdelvePolyHavenImport"/>). Two jobs, both visual only:
    ///   1. the chest model set (Resources/Emberdelve/ChestVisualSet.asset) the runtime ChestSpawner dresses its cubes with
    ///      (Poly Haven treasure_chest; Ember and Sealed are the same model tinted warm / cold);
    ///   2. a deterministic scatter of decorative props (barrels, crates, shelves, racks, tables, stools, cabinets, mossy rocks, root
    ///      clusters, standing candelabra) along the walls of the rooms, under a scene root "Props", plus a few fixed pieces in the lobby
    ///      ("Lobby_Props"). Props have NO colliders (they cannot block movement, projectiles, line of sight or the nav grid; the player
    ///      walks through them, like the wall lanterns), and in the dungeon they are placed only in wall-adjacent cells of rooms, pushed
    ///      against the wall, at least one cell away from every doorway and from every spawn, loot, chest, enemy, start and extraction
    ///      cell. Corner cells prefer roots and moss rocks. The layout is the only input: same layout, same props.
    /// A model key is "id" or "id#part" (part = the Poly Haven set piece that is kept, e.g. "rock_moss_set_01#rock02").
    /// </summary>
    internal static class EmberdelvePropFactory
    {
        public const string ChestSetPath = "Assets/Emberdelve/Resources/Emberdelve/ChestVisualSet.asset";
        public const string PropsRootName = "Props";
        public const string LobbyPropsRootName = "Lobby_Props";

        /// <summary>One placed prop (kept for the gate).</summary>
        internal struct Placement
        {
            public string Model;
            public Vector2Int Cell;
            public string Room;
        }

        internal static readonly List<Placement> LastPlacements = new List<Placement>();

        private const int Density = 60;       // percent of the eligible, non-adjacent cells that get a prop
        private const int MaxPerRoom = 7;
        private const float WallGap = 0.03f;  // metres between the prop's back and the wall face
        private const float MaxDepth = 1.5f;  // metres a prop may reach into the room

        private static readonly string[] CornerPool =
        {
            "root_cluster_01", "rock_moss_set_01#rock01", "rock_moss_set_01#rock03", "rock_moss_set_01#rock05", "root_cluster_01", "rock_moss_set_01#rock02"
        };

        // ---------------------------------------------------------------- chest set

        /// <summary>Writes the chest model set. Supply = plain chest, Ember = warm tint, Sealed = cold dark violet tint.</summary>
        public static ChestVisualSet EnsureChestVisualSet()
        {
            EmberdelveVisualSetup.EnsureFolder("Assets/Emberdelve/Resources/Emberdelve");

            GameObject chest = EmberdelvePolyHavenImport.LoadModel("treasure_chest");

            ChestVisualSet set = AssetDatabase.LoadAssetAtPath<ChestVisualSet>(ChestSetPath);
            bool created = set == null;
            if (created) set = ScriptableObject.CreateInstance<ChestVisualSet>();

            var supply = MeshyMapDressing.Load("IronboundChest");
            var ember = MeshyMapDressing.Load("TreasureChest");
            var sealedChest = MeshyMapDressing.Load("AncientChest");
            var emberEntry = NewEntry(ChestVariant.Ember, ember ?? chest, ember != null ? Color.white : new Color(1f, 0.78f, 0.5f, 1f));
            if (ember != null) emberEntry.alternateModel = MeshyMapDressing.Load("HiddenChest");
            set.entries = new[]
            {
                NewEntry(ChestVariant.Supply, supply ?? chest, Color.white),
                emberEntry,
                NewEntry(ChestVariant.Sealed, sealedChest ?? chest, sealedChest != null ? Color.white : new Color(0.55f, 0.6f, 0.9f, 1f))
            };

            if (created) AssetDatabase.CreateAsset(set, ChestSetPath);
            else EditorUtility.SetDirty(set);
            AssetDatabase.SaveAssets();

            if (ArrayHasMissingModel(set.entries))
                Debug.LogWarning("[Emberdelve][Props] Some chest variants have no visual asset and will use their fallback cubes.");
            return set;
        }

        private static bool ArrayHasMissingModel(ChestVisualSet.Entry[] entries)
        {
            foreach (var entry in entries)
                if (entry.model == null) return true;
            return false;
        }

        private static ChestVisualSet.Entry NewEntry(ChestVariant variant, GameObject model, Color tint)
        {
            return new ChestVisualSet.Entry { variant = variant, model = model, tint = tint, lidNameContains = "lid", frontSign = 1f, openAngle = 105f };
        }

        // ---------------------------------------------------------------- scatter

        private static string[] ThemeFor(string roomName)
        {
            string n = (roomName ?? string.Empty).ToLowerInvariant();
            if (n.Contains("entry")) return new[] { "Barrel_01", "wooden_crate_01", "wine_barrel_01", "wooden_crate_02", "wooden_bucket_01", "wooden_barrels_01#barrel01" };
            if (n.Contains("library")) return new[] { "wooden_bookshelf_worn", "WoodenTable_01", "wooden_bookshelf_worn", "wooden_stool_01", "brass_candleholders#candleholder_03", "wooden_barrels_01#barrel01" };
            if (n.Contains("central")) return new[] { "rock_moss_set_01#rock04", "wooden_barrels_01#barrel02", "brass_candleholders#candleholder_03", "root_cluster_01", "wooden_crate_02" };
            if (n.Contains("armory")) return new[] { "worn_metal_rack", "wooden_crate_02", "Barrel_01", "worn_metal_rack", "wooden_crate_01", "wooden_stool_01" };
            if (n.Contains("chapel")) return new[] { "brass_candleholders#candleholder_03", "wooden_stool_01", "wooden_bookshelf_worn", "root_cluster_01", "wooden_bucket_01" };
            if (n.Contains("crypt")) return new[] { "rock_moss_set_01#rock06", "root_cluster_01", "wooden_bucket_01", "rock_moss_set_01#rock02", "wine_barrel_01" };
            if (n.Contains("gate")) return new[] { "rock_moss_set_01#rock01", "wooden_barrels_01#barrel01", "root_cluster_01", "wooden_crate_01" };
            return new[] { "Barrel_01", "wooden_crate_01", "rock_moss_set_01#rock05" };
        }

        /// <summary>Largest horizontal extent (metres) a model is scaled to; 0 = keep Poly Haven's real-world size. The rock and root sets are huge.</summary>
        internal static float TargetMax(string key)
        {
            if (key.StartsWith("rock_moss_set_01")) return 0.9f;
            if (key.StartsWith("root_cluster_01")) return 1.3f;
            return 0f;
        }

        /// <summary>Barrels, rocks and roots have no meaningful front: they get a random turn.</summary>
        internal static bool HasFront(string key)
        {
            return !(key.StartsWith("rock_moss_set_01") || key.StartsWith("root_cluster_01") || key.StartsWith("Barrel_01") || key.StartsWith("wine_barrel_01")
                     || key.StartsWith("wooden_barrels_01") || key.StartsWith("wooden_bucket_01") || key.StartsWith("brass_candleholders"));
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
            float wallFace = layout.CellSize * 0.5f - Mathf.Max(0.05f, layout.wallThickness) * 0.5f;
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

                        List<Vector3> normals = WallNormals(walkable, x, y);
                        string model = theme[(int)((h >> 8) % (uint)theme.Length)];
                        if (normals.Count >= 2 && ((h >> 4) % 100u) < 65u) model = CornerPool[(int)((h >> 12) % (uint)CornerPool.Length)]; // corners: roots and moss rocks

                        GameObject probe = EmberdelvePolyHavenImport.LoadModel(SplitKey(model).id);
                        if (probe == null) { missingModels++; continue; }

                        Vector3 normal = normals[(int)((h >> 16) % (uint)normals.Count)];
                        float jitter = (((h >> 20) & 0xFFu) / 255f - 0.5f) * 0.6f;

                        if (!Place(root.transform, model, layout, new Vector2Int(x, y), normal, wallFace, jitter, h)) { missingModels++; continue; }
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

        private static (string id, string part) SplitKey(string key)
        {
            int hash = key.IndexOf('#');
            return hash < 0 ? (key, null) : (key.Substring(0, hash), key.Substring(hash + 1));
        }

        /// <summary>Instantiates a model key under the parent: the whole FBX, or only one set piece (the instance is then unpacked).</summary>
        private static GameObject Instantiate(string key, Transform parent)
        {
            (string id, string part) = SplitKey(key);
            GameObject asset = EmberdelvePolyHavenImport.LoadModel(id);
            if (asset == null) return null;

            // A holder carries the placement (position / yaw / scale): the FBX instance keeps its own import transform (several Poly Haven
            // files carry a -90 deg X turn and a x100 scale on their root), which a plain SetPositionAndRotation on the instance would destroy.
            GameObject holder = new GameObject("Prop");
            holder.transform.SetParent(parent, false);
            GameObject model = (GameObject)PrefabUtility.InstantiatePrefab(asset, holder.transform);
            GameObject prop = holder;
            if (part != null)
            {
                PrefabUtility.UnpackPrefabInstance(model, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
                Renderer[] all = prop.GetComponentsInChildren<Renderer>(true);
                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i] != null && !all[i].name.EndsWith("_" + part)) Object.DestroyImmediate(all[i].gameObject);
                }
            }

            // Pure visuals: whatever the import produced, nothing on a prop may collide or cast the 20 px streaks of a shadow-heavy cluster.
            Collider[] colliders = prop.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < colliders.Length; i++) Object.DestroyImmediate(colliders[i]);
            return prop;
        }

        private static bool Place(Transform parent, string key, DungeonLayoutData layout, Vector2Int cell, Vector3 normal, float wallFace, float jitter, uint hash)
        {
            Vector3 centre = layout.CellCenterWorld(cell);
            centre.y = layout.origin.y;

            GameObject prop = Instantiate(key, parent);
            if (prop == null) return false;
            prop.name = "Prop_" + key + "_" + cell.x + "_" + cell.y;

            // Front (model +Z) faces into the room; models without a front get a random quarter-ish turn.
            Quaternion look = Quaternion.LookRotation(normal, Vector3.up);
            if (!HasFront(key)) look *= Quaternion.Euler(0f, ((hash >> 24) & 0xFFu) / 255f * 360f, 0f);
            prop.transform.SetPositionAndRotation(centre, look);

            Renderer[] renderers = prop.GetComponentsInChildren<Renderer>(true);
            if (!TryGetBounds(renderers, out Bounds bounds)) { Object.DestroyImmediate(prop); return false; }

            float target = TargetMax(key);
            if (target > 0f)
            {
                float extent = Mathf.Max(bounds.size.x, bounds.size.z);
                prop.transform.localScale = prop.transform.localScale * (target / Mathf.Max(0.01f, extent));
                TryGetBounds(renderers, out bounds);
            }

            float depth = AlongAxis(bounds.size, normal);
            if (depth > MaxDepth)
            {
                float scale = Mathf.Max(0.6f, MaxDepth / depth);
                prop.transform.localScale = prop.transform.localScale * scale;
                TryGetBounds(renderers, out bounds);
            }

            Vector3 tangent = new Vector3(-normal.z, 0f, normal.x);
            float halfDepth = AlongAxis(bounds.size, normal) * 0.5f;
            float backNow = Vector3.Dot(bounds.center - centre, -normal) + halfDepth;     // distance of the back face from the cell centre, towards the wall
            float shiftBack = (wallFace - WallGap) - backNow;
            float lateralNow = Vector3.Dot(bounds.center - centre, tangent);
            float shiftLateral = jitter - lateralNow;
            float shiftUp = layout.origin.y - bounds.min.y;

            prop.transform.position += -normal * shiftBack + tangent * shiftLateral + Vector3.up * shiftUp;
            MakeStatic(prop);
            return true;
        }

        private static void MakeStatic(GameObject prop)
        {
            Transform[] all = prop.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++) GameObjectUtility.SetStaticEditorFlags(all[i].gameObject, StaticEditorFlags.BatchingStatic);
        }

        // ---------------------------------------------------------------- lobby

        private struct LobbyPiece
        {
            public string Model;
            public Vector3 WallPoint;   // point on the wall face the piece leans against (floor level)
            public Vector3 Normal;      // into the room
            public float Lateral;       // metres along the wall from WallPoint
            public float Yaw;           // extra yaw for pieces without a front
        }

        /// <summary>The safe room: a few shelves, barrels, crates, a table with a stool and corner growth along the walls. Visual only, no colliders.</summary>
        public static GameObject BuildLobbyProps()
        {
            GameObject root = new GameObject(LobbyPropsRootName);
            Vector3 north = Vector3.back, west = Vector3.right, east = Vector3.left;
            LobbyPiece[] pieces =
            {
                new LobbyPiece { Model = "wooden_bookshelf_worn", WallPoint = new Vector3(-7.4f, 0f, 8f), Normal = north },
                new LobbyPiece { Model = "GothicCabinet_01", WallPoint = new Vector3(7.4f, 0f, 8f), Normal = north },
                new LobbyPiece { Model = "Barrel_01", WallPoint = new Vector3(-4.2f, 0f, 8f), Normal = north, Yaw = 20f },
                new LobbyPiece { Model = "wine_barrel_01", WallPoint = new Vector3(-3.3f, 0f, 8f), Normal = north, Yaw = 140f },
                new LobbyPiece { Model = "wooden_crate_02", WallPoint = new Vector3(4.4f, 0f, 8f), Normal = north },
                new LobbyPiece { Model = "root_cluster_01", WallPoint = new Vector3(-10.1f, 0f, 8f), Normal = north, Yaw = 30f },
                new LobbyPiece { Model = "rock_moss_set_01#rock03", WallPoint = new Vector3(10.2f, 0f, 8f), Normal = north, Yaw = 75f },
                new LobbyPiece { Model = "WoodenTable_01", WallPoint = new Vector3(-11f, 0f, 2f), Normal = west },
                new LobbyPiece { Model = "wooden_stool_01", WallPoint = new Vector3(-11f, 0f, 2f), Normal = west, Lateral = 0.4f, Yaw = 0f },
                new LobbyPiece { Model = "worn_metal_rack", WallPoint = new Vector3(11f, 0f, -3f), Normal = east },
                new LobbyPiece { Model = "wooden_barrels_01#barrel01", WallPoint = new Vector3(11f, 0f, 2.6f), Normal = east, Yaw = 200f },
                new LobbyPiece { Model = "brass_candleholders#candleholder_03", WallPoint = new Vector3(-11f, 0f, -3.5f), Normal = west },
                new LobbyPiece { Model = "wooden_bucket_01", WallPoint = new Vector3(11f, 0f, 4.6f), Normal = east, Yaw = 60f }
            };

            for (int i = 0; i < pieces.Length; i++)
            {
                LobbyPiece piece = pieces[i];
                GameObject prop = Instantiate(piece.Model, root.transform);
                if (prop == null) continue;
                prop.name = "LobbyProp_" + piece.Model + "_" + i;

                Quaternion look = Quaternion.LookRotation(piece.Normal, Vector3.up);
                if (!HasFront(piece.Model)) look *= Quaternion.Euler(0f, piece.Yaw, 0f);
                prop.transform.SetPositionAndRotation(piece.WallPoint, look);
                Renderer[] renderers = prop.GetComponentsInChildren<Renderer>(true);
                if (!TryGetBounds(renderers, out Bounds bounds)) { Object.DestroyImmediate(prop); continue; }

                float target = TargetMax(piece.Model);
                if (target > 0f)
                {
                    prop.transform.localScale = prop.transform.localScale * (target / Mathf.Max(0.01f, Mathf.Max(bounds.size.x, bounds.size.z)));
                    TryGetBounds(renderers, out bounds);
                }

                Vector3 tangent = new Vector3(-piece.Normal.z, 0f, piece.Normal.x);
                float halfDepth = AlongAxis(bounds.size, piece.Normal) * 0.5f;
                Vector3 desired = piece.WallPoint + piece.Normal * (halfDepth + WallGap) + tangent * piece.Lateral;
                Vector3 shift = new Vector3(desired.x - bounds.center.x, -bounds.min.y, desired.z - bounds.center.z);
                prop.transform.position += shift;
                MakeStatic(prop);
            }

            return root;
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
