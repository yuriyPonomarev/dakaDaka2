#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using Emberdelve.Player;
using UnityEngine;

namespace Emberdelve.Level
{
    /// <summary>Physical measurements for development-only network movement proofs.</summary>
    public static class MovementProofGeometry
    {
        // Match the standing player controller; the small lift keeps ground support from counting as a wall.
        public const float ControllerRadius = 0.34f;
        private const float Lift = 0.04f;
        private const float SupportStep = 0.5f;

        /// <summary>Measures traversable supported distance for the entire controller capsule, bounded by maxDistance.</summary>
        public static float MeasureLane(Vector3 start, Vector3 direction, float height, float maxDistance)
        {
            if (height < 2f * ControllerRadius || maxDistance <= 0f || !Finite(start) || !Finite(direction) ||
                float.IsNaN(height) || float.IsInfinity(height) || float.IsNaN(maxDistance) || float.IsInfinity(maxDistance)) return 0f;
            direction.y = 0f;
            if (direction.sqrMagnitude < 0.99f) return 0f;
            direction.Normalize();
            Vector3 bottom = start + Vector3.up * (ControllerRadius + Lift);
            Vector3 top = start + Vector3.up * (height - ControllerRadius);
            Collider[] overlaps = Physics.OverlapCapsule(bottom, top, ControllerRadius, Physics.AllLayers, QueryTriggerInteraction.Ignore);
            foreach (Collider overlap in overlaps) if (IsObstacle(overlap)) return 0f;
            float clear = maxDistance;
            RaycastHit[] hits = Physics.CapsuleCastAll(bottom, top, ControllerRadius, direction, maxDistance,
                Physics.AllLayers, QueryTriggerInteraction.Ignore);
            foreach (RaycastHit hit in hits)
                if (IsObstacle(hit.collider)) clear = Mathf.Min(clear, Mathf.Max(0f, hit.distance));
            for (float distance = 0f; distance <= clear; distance += SupportStep)
                if (!HasSupport(start + direction * distance)) return Mathf.Max(0f, distance - SupportStep);
            if (!HasSupport(start + direction * clear)) return Mathf.Max(0f, clear - SupportStep);
            return clear;
        }

        /// <summary>Finds a supported +Z lane clear of stationary actors on the actual physics scene.</summary>
        public static bool TryFindWalkLane(DungeonLayoutData layout, Vector3[] stationaryPlayers, float height,
            float distance, out Vector3 start)
        {
            start = default;
            if (layout == null || distance <= 0f || float.IsNaN(distance) || float.IsInfinity(distance)) return false;
            bool[,] grid = layout.BuildWalkableGrid();
            for (int x = 0; x < grid.GetLength(0); x++)
            for (int z = 0; z < grid.GetLength(1); z++)
            {
                if (!grid[x, z]) continue;
                Vector3 candidate = layout.CellCenterWorld(new Vector2Int(x, z)) + Vector3.up * 0.15f;
                if (!ClearOfPlayers(candidate, Vector3.forward, distance, stationaryPlayers) ||
                    MeasureLane(candidate, Vector3.forward, height, distance) < distance - 0.01f) continue;
                start = candidate;
                return true;
            }
            return false;
        }

        /// <summary>Finds two non-overlapping supported lanes, both in +Z, with an actual wall for the second.</summary>
        public static bool TryFindLanes(DungeonLayoutData layout, Vector3[] stationaryPlayers, float height, float walkDistance, float returnStart, float returnDistance,
            out Vector3 walkStart, out Vector3 wallStart, out float wallDistance)
        {
            walkStart = wallStart = default;
            wallDistance = 0f;
            if (layout == null || walkDistance <= 0f || returnStart < 0f || returnStart > walkDistance || returnDistance <= 0f ||
                float.IsNaN(walkDistance) || float.IsInfinity(walkDistance) || float.IsNaN(returnStart) || float.IsInfinity(returnStart) ||
                float.IsNaN(returnDistance) || float.IsInfinity(returnDistance)) return false;
            bool[,] grid = layout.BuildWalkableGrid();
            if (grid == null) return false;
            Vector3 direction = Vector3.forward;
            for (int x = 0; x < layout.gridWidth; x++)
            for (int z = 0; z < layout.gridHeight; z++)
            {
                if (!grid[x, z]) continue;
                Vector3 candidate = layout.CellCenterWorld(new Vector2Int(x, z)) + Vector3.up * 0.15f;
                float behind = Mathf.Max(0f, returnDistance - returnStart);
                Vector3 pathStart = candidate - direction * behind;
                if (!ClearOfPlayers(pathStart, direction, walkDistance + behind, stationaryPlayers)) continue;
                if (MeasureLane(candidate, direction, height, walkDistance) < walkDistance - 0.01f ||
                    MeasureLane(candidate + direction * returnStart, -direction, height, returnDistance) < returnDistance - 0.01f) continue;
                for (int wx = 0; wx < layout.gridWidth; wx++)
                for (int wz = 0; wz < layout.gridHeight; wz++)
                {
                    if (!grid[wx, wz]) continue;
                    Vector3 other = layout.CellCenterWorld(new Vector2Int(wx, wz)) + Vector3.up * 0.15f;
                    if (!ClearOfPlayers(other, direction, 9f, stationaryPlayers) ||
                        DistanceToSegment(other, pathStart, candidate + direction * walkDistance) < 2f ||
                        DistanceToSegment(pathStart, other, other + direction * 9f) < 2f ||
                        DistanceToSegment(candidate + direction * walkDistance, other, other + direction * 9f) < 2f) continue;
                    float obstacle = MeasureObstacle(other, direction, height, 9f);
                    if (obstacle < 3f || obstacle > 9f) continue;
                    float supported = MeasureLane(other, direction, height, obstacle);
                    if (supported < obstacle - 0.05f) continue;
                    // The wall test retains the logical walkability assertion as well as the physical collision.
                    // A controller can stop just inside a wall whose centre belongs to a blocked boundary cell;
                    // that wall is unsuitable for this proof, so search for a different one.
                    Vector2Int endCell = layout.CellFromWorld(other + direction * Mathf.Max(0f, obstacle - 0.02f));
                    if (endCell.x < 0 || endCell.y < 0 || endCell.x >= grid.GetLength(0) || endCell.y >= grid.GetLength(1) ||
                        !grid[endCell.x, endCell.y]) continue;
                    walkStart = candidate; wallStart = other; wallDistance = obstacle;
                    return true;
                }
            }
            return false;
        }

        private static float MeasureObstacle(Vector3 start, Vector3 direction, float height, float maxDistance)
        {
            Vector3 bottom = start + Vector3.up * (ControllerRadius + Lift);
            Vector3 top = start + Vector3.up * (height - ControllerRadius);
            float nearest = maxDistance + 1f;
            foreach (RaycastHit hit in Physics.CapsuleCastAll(bottom, top, ControllerRadius, direction, maxDistance,
                         Physics.AllLayers, QueryTriggerInteraction.Ignore))
                if (IsObstacle(hit.collider)) nearest = Mathf.Min(nearest, hit.distance);
            return nearest;
        }

        private static bool ClearOfPlayers(Vector3 start, Vector3 direction, float distance, Vector3[] players)
        {
            if (players == null) return true;
            foreach (Vector3 player in players)
                if (DistanceToSegment(player, start, start + direction * distance) < 1.1f) return false;
            return true;
        }

        private static float DistanceToSegment(Vector3 point, Vector3 start, Vector3 end)
        {
            point.y = start.y = end.y = 0f;
            Vector3 delta = end - start;
            float t = delta.sqrMagnitude > 0f ? Mathf.Clamp01(Vector3.Dot(point - start, delta) / delta.sqrMagnitude) : 0f;
            return Vector3.Distance(point, start + delta * t);
        }

        private static bool HasSupport(Vector3 point)
        {
            RaycastHit[] hits = Physics.RaycastAll(point + Vector3.up * 0.35f, Vector3.down, 0.65f,
                Physics.AllLayers, QueryTriggerInteraction.Ignore);
            foreach (RaycastHit hit in hits)
                if (IsObstacle(hit.collider) && hit.normal.y >= 0.7f) return true;
            return false;
        }

        private static bool IsObstacle(Collider collider)
        {
            return collider != null && collider.enabled && collider.GetComponentInParent<PlayerRig>() == null;
        }

        private static bool Finite(Vector3 value)
        {
            return !float.IsNaN(value.x) && !float.IsNaN(value.y) && !float.IsNaN(value.z) &&
                   !float.IsInfinity(value.x) && !float.IsInfinity(value.y) && !float.IsInfinity(value.z);
        }
    }
}
#endif
