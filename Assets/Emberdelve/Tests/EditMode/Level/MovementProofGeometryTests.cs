#if UNITY_EDITOR
using Emberdelve.Level;
using NUnit.Framework;
using UnityEngine;

namespace Emberdelve.Tests.Level
{
    public sealed class MovementProofGeometryTests
    {
        [Test]
        public void LaneFinder_FindsSeparateSupportedWalkAndWallLanes()
        {
            Vector3 origin = new Vector3(11000f, 0f, 11000f);
            DungeonLayoutData layout = ScriptableObject.CreateInstance<DungeonLayoutData>();
            GameObject floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            GameObject wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            try
            {
                layout.origin = origin;
                layout.cellSize = 2;
                layout.gridWidth = layout.gridHeight = 15;
                layout.rooms.Add(new RoomSpec { minCell = Vector2Int.zero, maxCell = new Vector2Int(14, 14) });
                floor.transform.position = origin + new Vector3(15f, -0.5f, 15f);
                floor.transform.localScale = new Vector3(30f, 1f, 30f);
                wall.transform.position = origin + new Vector3(15f, 1f, 26f);
                wall.transform.localScale = new Vector3(30f, 2f, 0.5f);
                Physics.SyncTransforms();
                Assert.That(MovementProofGeometry.TryFindLanes(layout, null, 1.8f, 19.4f, 15f, 15f,
                    out Vector3 walk, out Vector3 wallStart, out float wallDistance), Is.True);
                Assert.That(MovementProofGeometry.MeasureLane(walk, Vector3.forward, 1.8f, 19.4f), Is.GreaterThanOrEqualTo(19.39f));
                Assert.That(wallDistance, Is.InRange(3f, 9f));
                Assert.That(Vector3.Distance(walk, wallStart), Is.GreaterThan(2f));
                Assert.That(MovementProofGeometry.TryFindWalkLane(layout, null, 1.8f, 19.4f, out Vector3 solo), Is.True);
                Vector3 stationary = solo + Vector3.forward * 8f;
                Assert.That(MovementProofGeometry.TryFindWalkLane(layout, new[] { stationary }, 1.8f, 19.4f,
                    out Vector3 avoidingPlayer), Is.True);
                Assert.That(Mathf.Abs(avoidingPlayer.x - stationary.x), Is.GreaterThanOrEqualTo(1.1f),
                    "the staged walk must not pass through the stationary observer");
                floor.SetActive(false);
                Physics.SyncTransforms();
                Assert.That(MovementProofGeometry.TryFindWalkLane(layout, null, 1.8f, 19.4f, out _), Is.False);
                Assert.That(MovementProofGeometry.TryFindLanes(layout, null, 1.8f, 19.4f, 15f, 15f, out _, out _, out _), Is.False);
            }
            finally
            {
                Object.DestroyImmediate(wall);
                Object.DestroyImmediate(floor);
                Object.DestroyImmediate(layout);
                Physics.SyncTransforms();
            }
        }

        [Test]
        public void LaneFinder_RejectsWallWhoseStoppingPointFallsOutsideWalkableGrid()
        {
            Vector3 origin = new Vector3(12000f, 0f, 12000f);
            DungeonLayoutData layout = ScriptableObject.CreateInstance<DungeonLayoutData>();
            GameObject floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            GameObject wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            try
            {
                layout.origin = origin;
                layout.cellSize = 2;
                layout.gridWidth = layout.gridHeight = 15;
                // Geometry continues beyond the logical room, as at modular boundary walls.
                layout.rooms.Add(new RoomSpec { minCell = Vector2Int.zero, maxCell = new Vector2Int(14, 11) });
                floor.transform.position = origin + new Vector3(15f, -0.5f, 15f);
                floor.transform.localScale = new Vector3(30f, 1f, 30f);
                wall.transform.position = origin + new Vector3(15f, 1f, 26f);
                wall.transform.localScale = new Vector3(30f, 2f, 0.5f);
                Physics.SyncTransforms();
                Assert.That(MovementProofGeometry.TryFindLanes(layout, null, 1.8f, 19.4f, 15f, 15f,
                    out _, out _, out _), Is.False,
                    "a physically reachable wall beyond the logical room is unsuitable for the walkability proof");

                layout.rooms[0].maxCell = new Vector2Int(14, 12);
                Assert.That(MovementProofGeometry.TryFindLanes(layout, null, 1.8f, 19.4f, 15f, 15f,
                    out _, out Vector3 wallStart, out float wallDistance), Is.True);
                Vector2Int endCell = layout.CellFromWorld(wallStart + Vector3.forward * (wallDistance - 0.02f));
                Assert.That(layout.BuildWalkableGrid()[endCell.x, endCell.y], Is.True);
            }
            finally
            {
                Object.DestroyImmediate(wall);
                Object.DestroyImmediate(floor);
                Object.DestroyImmediate(layout);
                Physics.SyncTransforms();
            }
        }

        [Test]
        public void Lane_UsesCapsuleObstaclesAndRejectsUnsupportedGround()
        {
            Vector3 origin = new Vector3(10000f, 0f, 10000f);
            GameObject floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            GameObject wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            try
            {
                floor.transform.position = origin + new Vector3(0f, -0.5f, 4f);
                floor.transform.localScale = new Vector3(4f, 1f, 8f);
                wall.transform.position = origin + new Vector3(0f, 1f, 3f);
                wall.transform.localScale = new Vector3(2f, 2f, 0.5f);
                Physics.SyncTransforms();
                Vector3 start = origin + Vector3.up * 0.15f;
                float blocked = MovementProofGeometry.MeasureLane(start, Vector3.forward, 1.8f, 6f);
                Assert.That(blocked, Is.InRange(2.3f, 2.5f), "the full capsule must stop before the wall");
                wall.SetActive(false);
                Physics.SyncTransforms();
                Assert.That(MovementProofGeometry.MeasureLane(start, Vector3.forward, 1.8f, 6f), Is.EqualTo(6f).Within(0.01f));
                Assert.That(MovementProofGeometry.MeasureLane(start, Vector3.forward, 1.8f, 12f), Is.LessThan(8.1f), "the lane must stop at unsupported ground");
                Assert.That(MovementProofGeometry.MeasureLane(start, Vector3.zero, 1.8f, 6f), Is.Zero);
            }
            finally
            {
                Object.DestroyImmediate(wall);
                Object.DestroyImmediate(floor);
                Physics.SyncTransforms();
            }
        }
    }
}
#endif
