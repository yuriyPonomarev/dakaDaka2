using System.Collections.Generic;
using UnityEngine;
using Emberdelve.Core;

namespace Emberdelve.AI
{
    /// <summary>
    /// Everything enemy movement needs from navigation. Today NavGridPathfinder implements it from a
    /// hand-authored grid; a NavMeshSurface from com.unity.ai.navigation can implement the same
    /// interface later without touching EnemyBrain or EnemyAgent.
    /// </summary>
    public interface INavigationProvider
    {
        bool TryFindPath(Vector3 from, Vector3 to, List<Vector3> path);
        bool IsWalkableWorld(Vector3 world);
    }

    /// <summary>Breadth-first search over NavGridData cells, with corner-cutting disabled.</summary>
    public sealed class NavGridPathfinder : INavigationProvider
    {
        private static readonly VectorInt[] Steps =
        {
            new VectorInt(1, 0), new VectorInt(-1, 0), new VectorInt(0, 1), new VectorInt(0, -1),
            new VectorInt(1, 1), new VectorInt(1, -1), new VectorInt(-1, 1), new VectorInt(-1, -1)
        };

        private readonly NavGridData _grid;
        private readonly Queue<VectorInt> _frontier = new Queue<VectorInt>();
        private readonly Dictionary<VectorInt, VectorInt> _cameFrom = new Dictionary<VectorInt, VectorInt>();
        private readonly List<VectorInt> _cells = new List<VectorInt>(256);

        public NavGridPathfinder(NavGridData grid)
        {
            _grid = grid;
        }

        public bool HasGrid => _grid != null;

        public bool IsWalkableWorld(Vector3 world)
        {
            if (_grid == null) return true;
            VectorInt cell = _grid.CellFromWorld(world);
            return _grid.IsWalkable(cell.x, cell.y);
        }

        public bool TryFindPath(Vector3 from, Vector3 to, List<Vector3> path)
        {
            path.Clear();
            if (_grid == null || _grid.width <= 0 || _grid.height <= 0) return false;

            VectorInt start = ResolveWalkable(_grid.CellFromWorld(from));
            VectorInt goal = ResolveWalkable(_grid.CellFromWorld(to));

            if (start.x < 0 || goal.x < 0) return false;
            if (start.Equals(goal))
            {
                path.Add(_grid.CellCenter(goal.x, goal.y));
                return true;
            }

            _frontier.Clear();
            _cameFrom.Clear();
            _cells.Clear();

            _frontier.Enqueue(start);
            _cameFrom[start] = start;

            bool found = false;

            while (_frontier.Count > 0)
            {
                VectorInt current = _frontier.Dequeue();
                _cells.Add(current);

                if (current.Equals(goal))
                {
                    found = true;
                    break;
                }

                for (int i = 0; i < Steps.Length; i++)
                {
                    VectorInt next = new VectorInt(current.x + Steps[i].x, current.y + Steps[i].y);
                    if (!_grid.IsWalkable(next.x, next.y)) continue;

                    if (Steps[i].x != 0 && Steps[i].y != 0)
                    {
                        if (!_grid.IsWalkable(current.x + Steps[i].x, current.y)) continue;
                        if (!_grid.IsWalkable(current.x, current.y + Steps[i].y)) continue;
                    }

                    if (_cameFrom.ContainsKey(next)) continue;

                    _cameFrom[next] = current;
                    _frontier.Enqueue(next);
                }
            }

            if (!found) return false;

            List<VectorInt> cells = BuildCellPath(goal);
            Simplify(cells);

            for (int i = 0; i < cells.Count; i++)
            {
                path.Add(_grid.CellCenter(cells[i].x, cells[i].y));
            }

            return path.Count > 0;
        }

        private List<VectorInt> BuildCellPath(VectorInt goal)
        {
            List<VectorInt> reversed = new List<VectorInt>(32);
            VectorInt current = goal;
            int guard = _grid.width * _grid.height + 8;

            while (guard-- > 0)
            {
                reversed.Add(current);
                VectorInt parent = _cameFrom[current];
                if (parent.Equals(current)) break;
                current = parent;
            }

            reversed.Reverse();
            return reversed;
        }

        /// <summary>Removes waypoints that lie on a straight line between their neighbours.</summary>
        private static void Simplify(List<VectorInt> cells)
        {
            for (int i = cells.Count - 2; i > 0; i--)
            {
                VectorInt a = cells[i - 1];
                VectorInt b = cells[i];
                VectorInt c = cells[i + 1];

                int d1x = b.x - a.x, d1y = b.y - a.y;
                int d2x = c.x - b.x, d2y = c.y - b.y;

                if (d1x * d2y - d1y * d2x == 0) cells.RemoveAt(i);
            }
        }

        private VectorInt ResolveWalkable(VectorInt cell)
        {
            if (_grid.IsWalkable(cell.x, cell.y)) return cell;

            for (int radius = 1; radius <= 3; radius++)
            {
                for (int dy = -radius; dy <= radius; dy++)
                {
                    for (int dx = -radius; dx <= radius; dx++)
                    {
                        if (Mathf.Abs(dx) != radius && Mathf.Abs(dy) != radius) continue;
                        VectorInt candidate = new VectorInt(cell.x + dx, cell.y + dy);
                        if (_grid.IsWalkable(candidate.x, candidate.y)) return candidate;
                    }
                }
            }

            return new VectorInt(-1, -1);
        }
    }
}
