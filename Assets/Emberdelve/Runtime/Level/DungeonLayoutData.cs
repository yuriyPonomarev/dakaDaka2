using System;
using System.Collections.Generic;
using UnityEngine;
using Emberdelve.Data;

namespace Emberdelve.Level
{
    [Serializable]
    public class RoomSpec
    {
        public string roomName = "Room";
        [Tooltip("Inclusive lower cell corner.")]
        public Vector2Int minCell;
        [Tooltip("Inclusive upper cell corner.")]
        public Vector2Int maxCell;
    }

    [Serializable]
    public class CorridorSpec
    {
        public string corridorName = "Corridor";
        public Vector2Int fromCell;
        public Vector2Int toCell;
        [Range(1, 4)] public int width = 2;
    }

    [Serializable]
    public class LootSpawnSpec
    {
        public Vector2Int cell;
        public ItemData item;
        public int amount = 1;
    }

    [Serializable]
    public class EnemySpawnSpec
    {
        public Vector2Int cell;
        public EnemyData enemy;
    }

    [Serializable]
    public class TorchSpec
    {
        public Vector2Int cell;
        public float height = 2.5f;
        public float range = 9f;
        public float intensity = 14f;
    }

    /// <summary>
    /// Data description of the blockout dungeon. Cell based, so the same grid drives geometry,
    /// navigation and spawn placement. Replacing the blockout with real art means keeping this asset
    /// (or swapping the geometry builder), not rewriting gameplay.
    /// </summary>
    [CreateAssetMenu(menuName = "Emberdelve/07 Dungeon Layout", fileName = "DungeonLayout")]
    public class DungeonLayoutData : ScriptableObject
    {
        [Header("Identity")]
        public string levelName = "The Ashen Halls";

        [Header("Grid")]
        public int gridWidth = 26;
        public int gridHeight = 22;
        public int cellSize = 2;
        public float wallHeight = 3.6f;
        public float wallThickness = 0.45f;
        public Vector3 origin = Vector3.zero;
        public float floorThickness = 0.5f;
        public float ceilingThickness = 0.4f;

        [Header("Shape")]
        public List<RoomSpec> rooms = new List<RoomSpec>();
        public List<CorridorSpec> corridors = new List<CorridorSpec>();

        [Header("Markers")]
        public Vector2Int playerStartCell;
        public Vector2Int extractionCell;

        [Header("Content")]
        public List<LootSpawnSpec> lootSpawns = new List<LootSpawnSpec>();
        public List<EnemySpawnSpec> enemySpawns = new List<EnemySpawnSpec>();
        public List<TorchSpec> torches = new List<TorchSpec>();

        public int CellSize => Mathf.Max(1, cellSize);

        public Vector3 CellCenterWorld(Vector2Int cell) =>
            origin + new Vector3((cell.x + 0.5f) * CellSize, 0f, (cell.y + 0.5f) * CellSize);

        public Vector2Int CellFromWorld(Vector3 world) => new Vector2Int(
            Mathf.FloorToInt((world.x - origin.x) / CellSize),
            Mathf.FloorToInt((world.z - origin.z) / CellSize));

        public bool[,] BuildWalkableGrid()
        {
            bool[,] walkable = new bool[Mathf.Max(1, gridWidth), Mathf.Max(1, gridHeight)];

            for (int i = 0; i < rooms.Count; i++)
            {
                RoomSpec room = rooms[i];
                CarveRectangle(walkable, room.minCell, room.maxCell);
            }

            for (int i = 0; i < corridors.Count; i++)
            {
                CorridorSpec corridor = corridors[i];
                int width = Mathf.Max(1, corridor.width);
                int lowOffset = -(width / 2);
                int highOffset = (width - 1) / 2;

                Vector2Int a = corridor.fromCell;
                Vector2Int b = corridor.toCell;

                if (a.x == b.x)
                {
                    int lo = Mathf.Min(a.y, b.y);
                    int hi = Mathf.Max(a.y, b.y);
                    CarveRectangle(walkable, new Vector2Int(a.x + lowOffset, lo), new Vector2Int(a.x + highOffset, hi));
                }
                else if (a.y == b.y)
                {
                    int lo = Mathf.Min(a.x, b.x);
                    int hi = Mathf.Max(a.x, b.x);
                    CarveRectangle(walkable, new Vector2Int(lo, a.y + lowOffset), new Vector2Int(hi, a.y + highOffset));
                }
                else
                {
                    Debug.LogError($"[DungeonLayout] Corridor '{corridor.corridorName}' is diagonal; corridors must be axis aligned.", this);
                }
            }

            return walkable;
        }

        private static void CarveRectangle(bool[,] walkable, Vector2Int min, Vector2Int max)
        {
            int x0 = Mathf.Clamp(Mathf.Min(min.x, max.x), 0, walkable.GetLength(0) - 1);
            int x1 = Mathf.Clamp(Mathf.Max(min.x, max.x), 0, walkable.GetLength(0) - 1);
            int y0 = Mathf.Clamp(Mathf.Min(min.y, max.y), 0, walkable.GetLength(1) - 1);
            int y1 = Mathf.Clamp(Mathf.Max(min.y, max.y), 0, walkable.GetLength(1) - 1);

            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++) walkable[x, y] = true;
            }
        }

        /// <summary>Mask string consumed by NavGridData ('.' walkable, '#' blocked).</summary>
        public string BuildNavMask()
        {
            bool[,] walkable = BuildWalkableGrid();
            char[] buffer = new char[walkable.GetLength(0) * walkable.GetLength(1)];

            for (int y = 0; y < walkable.GetLength(1); y++)
            {
                for (int x = 0; x < walkable.GetLength(0); x++)
                {
                    buffer[y * walkable.GetLength(0) + x] = walkable[x, y] ? '.' : '#';
                }
            }

            return new string(buffer);
        }

        public bool IsWalkableCell(Vector2Int cell)
        {
            if (cell.x < 0 || cell.y < 0 || cell.x >= gridWidth || cell.y >= gridHeight) return false;
            return BuildWalkableGrid()[cell.x, cell.y];
        }

        public List<string> Validate()
        {
            List<string> problems = new List<string>();

            if (gridWidth < 4 || gridHeight < 4) problems.Add("Grid is smaller than 4x4 cells.");
            if (rooms.Count == 0) problems.Add("No rooms defined.");
            if (!IsWalkableCell(playerStartCell)) problems.Add($"Player start cell {playerStartCell} is not walkable.");
            if (!IsWalkableCell(extractionCell)) problems.Add($"Extraction cell {extractionCell} is not walkable.");

            for (int i = 0; i < corridors.Count; i++)
            {
                CorridorSpec corridor = corridors[i];
                if (corridor.fromCell.x != corridor.toCell.x && corridor.fromCell.y != corridor.toCell.y)
                {
                    problems.Add($"Corridor '{corridor.corridorName}' is diagonal.");
                }
            }

            for (int i = 0; i < enemySpawns.Count; i++)
            {
                if (!IsWalkableCell(enemySpawns[i].cell)) problems.Add($"Enemy spawn {enemySpawns[i].cell} is not walkable.");
            }

            for (int i = 0; i < lootSpawns.Count; i++)
            {
                if (!IsWalkableCell(lootSpawns[i].cell)) problems.Add($"Loot spawn {lootSpawns[i].cell} is not walkable.");
            }

            return problems;
        }
    }
}
