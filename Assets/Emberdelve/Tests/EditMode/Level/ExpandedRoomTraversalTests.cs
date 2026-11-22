using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Emberdelve.Level.Tests
{
    public sealed class ExpandedRoomTraversalTests
    {
        [TestCase("", false, -1)] [TestCase("Crypt/", false, -1)] [TestCase("Infernal/", false, -1)]
        [TestCase("", false, 1)] [TestCase("Crypt/", false, 1)] [TestCase("Infernal/", false, 1)]
        [TestCase("", true, -1)] [TestCase("Crypt/", true, -1)] [TestCase("Infernal/", true, -1)]
        public void StandingControllerClimbsAndDescendsGalleryAndUndercroft(string theme, bool below, int side)
        {
            string prefix = theme.Length == 0 ? "" : theme.TrimEnd('/') + "_";
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Emberdelve/Prefabs/DungeonModules/" + theme + prefix + "Crypt_Hall_02.prefab");
            Assert.NotNull(prefab);
            var room = Object.Instantiate(prefab);
            var player = new GameObject("TraversalProbe");
            try
            {
                room.transform.position = new Vector3(12000, 0, 12000);
                // Gallery uses a side lane around columns/table. The undercroft retains a clear middle lane.
                float x = below ? 13.5f : -13f;
                float start = below ? -16 : side * 17;
                float finish = below ? -4 : side * 4;
                player.transform.position = room.transform.position + new Vector3(x, .04f, start);
                var cc = player.AddComponent<CharacterController>();
                cc.height = 1.8f; cc.radius = .34f; cc.center = Vector3.up * .9f;
                cc.stepOffset = .3f; cc.slopeLimit = 50; cc.skinWidth = .02f;
                Physics.SyncTransforms();
                float lowest = 0, highest = 0;
                void WalkTo(float targetZ)
                {
                    for (int frame = 0; frame < 1400; frame++)
                    {
                        float distance = room.transform.position.z + targetZ - player.transform.position.z;
                        if (Mathf.Abs(distance) < .04f) break;
                        cc.Move(new Vector3(0, -.09f, Mathf.Sign(distance) * .035f));
                        float y = player.transform.position.y;
                        lowest = Mathf.Min(lowest, y); highest = Mathf.Max(highest, y);
                    }
                    Assert.That(player.transform.position.z - room.transform.position.z, Is.EqualTo(targetZ).Within(.06f),
                        "Route must be traversable without jumping");
                }
                WalkTo(finish);
                if (!below) WalkTo(start);
                Assert.That(player.transform.position.y, Is.InRange(-.05f, .12f), "Both ends return to the socket floor");
                if (below) Assert.That(lowest, Is.LessThan(-1.35f), "A flat hidden collider must not bridge the undercroft");
                else Assert.That(highest, Is.GreaterThan(3.8f), "Upper gallery must be reachable");
            }
            finally { Object.DestroyImmediate(player); Object.DestroyImmediate(room); }
        }

        [TestCase("")] [TestCase("Crypt/")] [TestCase("Infernal/")]
        public void GalleryUnderpassPreservesFullDoorwayClearanceForLargeEnemies(string theme)
        {
            string prefix = theme.Length == 0 ? "" : theme.TrimEnd('/') + "_";
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Emberdelve/Prefabs/DungeonModules/" + theme + prefix + "Crypt_Hall_02.prefab");
            var room = Object.Instantiate(prefab);
            try
            {
                room.transform.position = new Vector3(12000, 0, 12000);
                Physics.SyncTransforms();
                var bottom = room.transform.position + new Vector3(-16.8f, .57f, 0);
                var top = room.transform.position + new Vector3(-16.8f, 2.65f, 0);
                Assert.False(Physics.CheckCapsule(bottom, top, .5f, Physics.AllLayers, QueryTriggerInteraction.Ignore),
                    "A 3.15m-tall enemy must fit beneath the gallery");
                Assert.False(Physics.CapsuleCast(bottom, top, .5f, Vector3.right, 5.3f, Physics.AllLayers, QueryTriggerInteraction.Ignore),
                    "Large enemies must cross the west doorway axis without hitting the gallery");
            }
            finally { Object.DestroyImmediate(room); }
        }

        [TestCase("", -1)] [TestCase("Crypt/", -1)] [TestCase("Infernal/", -1)]
        [TestCase("", 1)] [TestCase("Crypt/", 1)] [TestCase("Infernal/", 1)]
        public void EnemiesCanPathUpEachGalleryStair(string theme, int side)
        {
            string prefix = theme.Length == 0 ? "" : theme.TrimEnd('/') + "_";
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Emberdelve/Prefabs/DungeonModules/" + theme + prefix + "Crypt_Hall_02.prefab");
            var room = Object.Instantiate(prefab);
            var grid = ScriptableObject.CreateInstance<Emberdelve.Core.NavGridData>();
            try
            {
                room.transform.position = new Vector3(12000, 0, 12000);
                grid.origin = room.transform.position - new Vector3(18, 0, 18);
                grid.width = grid.height = 18; grid.cellSize = 2; grid.mask = new string('.', 324);
                Physics.SyncTransforms();
                Emberdelve.AI.NavGridSurface.Bake(grid, room.transform, LayerMask.GetMask(Emberdelve.Core.GameConfig.LevelLayerName));
                int targetZ = side < 0 ? 6 : 11;
                Assert.That(grid.CellCenter(2, targetZ).y, Is.EqualTo(3.9f).Within(.02f));
                var path = new System.Collections.Generic.List<Vector3>();
                var routeDetails = new System.Text.StringBuilder();
                for (int z = 0; z < 18; z++)
                    routeDetails.Append($" z{z}={grid.CellCenter(2,z).y:F2}/{grid.IsWalkable(2,z)}/{grid.surfaceLinks[z*18+2]}");
                Assert.True(new Emberdelve.AI.NavGridPathfinder(grid).TryFindPath(
                    room.transform.position + new Vector3(-13, 0, side * 17), grid.CellCenter(2, targetZ), path),
                    "Each upper wing must have a real stair route for enemies:" + routeDetails);
            }
            finally { Object.DestroyImmediate(grid); Object.DestroyImmediate(room); }
        }
    }
}
