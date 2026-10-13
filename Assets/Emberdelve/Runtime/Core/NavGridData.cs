using UnityEngine;

namespace Emberdelve.Core
{
    /// <summary>
    /// Hand-authored navigation data for the blockout level: a walkable/blocked grid that the enemy
    /// pathfinder uses. It exists so the prototype needs no baked NavMesh (which cannot be produced
    /// without opening the editor). Swap it for com.unity.ai.navigation NavMeshSurface later without
    /// touching enemy logic: only INavigationProvider has to be re-implemented.
    /// </summary>
    [CreateAssetMenu(menuName = "Emberdelve/05 Nav Grid", fileName = "EmberdelveNavGrid")]
    public class NavGridData : ScriptableObject
    {
        public int width;
        public int height;
        public float cellSize = 2f;
        public Vector3 origin;
        public float navPlaneHeight = 0f;
        [Tooltip("One character per cell. '.' walkable, '#' blocked. Rows are concatenated.")]
        public string mask = string.Empty;

        public bool IsInside(int x, int y) => x >= 0 && y >= 0 && x < width && y < height;

        public bool IsWalkable(int x, int y)
        {
            if (!IsInside(x, y)) return false;
            int index = y * width + x;
            if (index >= mask.Length) return false;
            return mask[index] != '#';
        }

        public Vector3 CellCenter(int x, int y) =>
            origin + new Vector3((x + 0.5f) * cellSize, navPlaneHeight, (y + 0.5f) * cellSize);

        public VectorInt CellFromWorld(Vector3 world) => new VectorInt(
            Mathf.FloorToInt((world.x - origin.x) / cellSize),
            Mathf.FloorToInt((world.z - origin.z) / cellSize));

        public Vector3 WorldFromCellCenterSafe(VectorInt cell) => CellCenter(Mathf.Clamp(cell.x, 0, width - 1), Mathf.Clamp(cell.y, 0, height - 1));
    }

    public readonly struct VectorInt
    {
        public readonly int x;
        public readonly int y;
        public VectorInt(int xValue, int yValue) { x = xValue; y = yValue; }
        public override int GetHashCode() => x * 73856093 ^ y * 19349663;
        public override bool Equals(object obj) => obj is VectorInt other && other.x == x && other.y == y;
        public override string ToString() => $"({x},{y})";
    }
}
