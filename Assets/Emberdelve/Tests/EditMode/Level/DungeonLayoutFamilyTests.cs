using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Emberdelve.AI;
using Emberdelve.Core;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Emberdelve.Level.Tests
{
    public sealed class DungeonLayoutFamilyTests
    {
        private static readonly string[] Names = { "Crypt_Entry", "Crypt_Hall_01", "Crypt_Hall_02", "Crypt_Hall_03",
            "Crypt_Ossuary_01", "Crypt_Ossuary_02", "Crypt_Shrine", "Crypt_Vault", "Crypt_Extraction", "Crypt_Skybridge" };
        private static readonly string[] Themes = { "", "Crypt", "Infernal" };
        private static readonly Vector3 Origin = new Vector3(14000, 0, 14000);
        private static readonly DungeonModuleSockets[] Ports = { DungeonModuleSockets.North, DungeonModuleSockets.East,
            DungeonModuleSockets.South, DungeonModuleSockets.West };
        private static readonly Vector3[] PortPoints = { new Vector3(0, 0, 17), new Vector3(17, 0, 0),
            new Vector3(0, 0, -17), new Vector3(-17, 0, 0) };
        private static int LevelMask => LayerMask.GetMask(GameConfig.LevelLayerName);

        public static IEnumerable<TestCaseData> EveryRoom()
        {
            foreach (string theme in Themes)
                for (int i = 0; i < Names.Length; i++)
                    yield return new TestCaseData(theme, Names[i], (i % 4) * 90f)
                        .SetName("FamilyPorts_" + (theme.Length == 0 ? "Ruins" : theme) + "_" + Names[i]);
        }

        [TestCaseSource(nameof(EveryRoom))]
        public void AllOpenedPortsConnectToCentreThroughActualRoomGeometry(string theme, string name, float yaw)
        {
            GameObject room = LoadRoom(theme, name, yaw); NavGridData grid = null;
            try
            {
                grid = Bake(room);
                Vector3 centre = room.transform.TransformPoint(Vector3.zero);
                RequireCell(grid, centre, 0, "room centre");
                var finder = new NavGridPathfinder(grid); var path = new List<Vector3>();
                for (int i = 0; i < PortPoints.Length; i++)
                {
                    Vector3 target = room.transform.TransformPoint(PortPoints[i]);
                    VectorInt cell = RequireCell(grid, target, 0, Ports[i] + " approach");
                    Assert.That(finder.TryFindPath(centre, target, path), Is.True,
                        name + " " + Ports[i] + " must connect to centre using baked surface links");
                    Assert.That((path[path.Count - 1] - grid.CellCenter(cell.x, cell.y)).sqrMagnitude, Is.LessThan(.0001f),
                        "Pathfinder must reach the requested port cell, not a substituted nearby cell");
                    Assert.That(finder.TryFindPath(target, centre, path), Is.True,
                        "An opened socket must also provide a return route");
                    if (room.transform.Find("Roof") != null)
                    {
                        Vector3 outward = room.transform.TransformDirection(PortPoints[i].normalized);
                        Assert.That(Physics.Raycast(target + Vector3.up * 6, outward, 2f,
                            LevelMask, QueryTriggerInteraction.Ignore), Is.True,
                            "Opening " + Ports[i] + " must retain solid upper masonry to the roof");
                    }
                    Assert.That(Physics.CheckCapsule(target + Vector3.up * .55f, target + Vector3.up * 1.5f,
                        .34f, LevelMask, QueryTriggerInteraction.Ignore), Is.False,
                        name + " " + Ports[i] + " must admit the standing controller");
                }
            }
            finally { if (grid != null) Object.DestroyImmediate(grid); Object.DestroyImmediate(room); }
        }

        [TestCase("")] [TestCase("Crypt")] [TestCase("Infernal")]
        public void TenFamiliesHaveDifferentPhysicalNavigationLayouts(string theme)
        {
            var signatures = new Dictionary<string, string>();
            foreach (string name in Names)
            {
                GameObject room = LoadRoom(theme, name, 0); NavGridData grid = null;
                try
                {
                    grid = Bake(room);
                    // Occupancy, measured heights and actual links come from colliders, never hierarchy labels.
                    var signature = new StringBuilder(grid.mask);
                    for (int i = 0; i < grid.surfaceHeights.Length; i++)
                    {
                        signature.Append('|').Append(Mathf.RoundToInt(grid.surfaceHeights[i] * 100f).ToString(CultureInfo.InvariantCulture));
                        signature.Append(':').Append(grid.surfaceLinks[i].ToString(CultureInfo.InvariantCulture));
                    }
                    string key = signature.ToString();
                    Assert.That(signatures.TryGetValue(key, out string duplicate), Is.False,
                        name + " repeats the physical layout of " + duplicate);
                    signatures.Add(key, name);
                }
                finally { if (grid != null) Object.DestroyImmediate(grid); Object.DestroyImmediate(room); }
            }
            Assert.That(signatures.Count, Is.EqualTo(10));
        }

        public static IEnumerable<TestCaseData> HeightRoutes()
        {
            foreach (string theme in Themes)
                foreach (string name in new[] { "Crypt_Entry", "Crypt_Shrine", "Crypt_Vault", "Crypt_Skybridge", "Crypt_Ossuary_02" })
                    yield return new TestCaseData(theme, name)
                        .SetName("FamilyHeightRoute_" + (theme.Length == 0 ? "Ruins" : theme) + "_" + name);
        }

        [TestCaseSource(nameof(HeightRoutes))]
        public void ControllerAndNavigationReachRaisedOrSunkenRouteAndReturn(string theme, string name)
        {
            Vector3[] route = Route(name);
            GameObject room = LoadRoom(theme, name, name == "Crypt_Skybridge" ? 90 : 0);
            var player = new GameObject("FamilyTraversalProbe"); NavGridData grid = null;
            try
            {
                // Bake before creating the controller so the probe cannot become its own obstacle.
                grid = Bake(room);
                var finder = new NavGridPathfinder(grid); var path = new List<Vector3>();
                for (int i = 0; i < route.Length; i++)
                    RequireCell(grid, room.transform.TransformPoint(route[i]), route[i].y, "route waypoint " + i);
                for (int i = 1; i < route.Length; i++)
                {
                    Vector3 a = room.transform.TransformPoint(route[i - 1]), b = room.transform.TransformPoint(route[i]);
                    Assert.That(finder.TryFindPath(a, b, path), Is.True, "Real stairs must connect route waypoint " + i);
                    Assert.That(finder.TryFindPath(b, a, path), Is.True, "Real stairs must connect in both directions");
                }
                player.transform.position = room.transform.TransformPoint(route[0]) + Vector3.up * .04f;
                var controller = player.AddComponent<CharacterController>();
                controller.height = 1.8f; controller.radius = .34f; controller.center = Vector3.up * .9f;
                controller.stepOffset = .3f; controller.slopeLimit = 50; controller.skinWidth = .02f;
                Physics.SyncTransforms();
                float lowest = 0, highest = 0;
                for (int i = 1; i < route.Length; i++) Walk(route[i]);
                for (int i = route.Length - 2; i >= 0; i--) Walk(route[i]);
                Assert.That(player.transform.position.y - room.transform.position.y, Is.EqualTo(0).Within(.12f),
                    "The route must return to the original floor without jumping or teleporting");
                float featureHeight = route[1].y;
                if (featureHeight < 0)
                    Assert.That(lowest, Is.LessThan(featureHeight + .12f), "No hidden floor may cover the sunken route");
                else Assert.That(highest, Is.GreaterThan(featureHeight - .12f), "The raised route must actually be climbed");

                void Walk(Vector3 local)
                {
                    Vector3 target = room.transform.TransformPoint(local);
                    for (int frame = 0; frame < 1600; frame++)
                    {
                        Vector3 delta = target - player.transform.position; delta.y = 0;
                        if (delta.magnitude < .035f) break;
                        controller.Move(Vector3.ClampMagnitude(delta, .035f) + Vector3.down * .09f);
                        float height = player.transform.position.y - room.transform.position.y;
                        lowest = Mathf.Min(lowest, height); highest = Mathf.Max(highest, height);
                    }
                    // Settle on actual collision; passing above a void must not count as arrival.
                    for (int frame = 0; frame < 18; frame++) controller.Move(Vector3.down * .09f);
                    Vector3 position = room.transform.InverseTransformPoint(player.transform.position);
                    Assert.That(new Vector2(position.x - local.x, position.z - local.z).magnitude, Is.LessThan(.07f),
                        name + " blocked before waypoint " + local);
                    Assert.That(position.y, Is.EqualTo(local.y).Within(.12f),
                        name + " wrong support height at " + local + "; a hidden cover, hole or inaccessible platform is not a route");
                    lowest = Mathf.Min(lowest, position.y); highest = Mathf.Max(highest, position.y);
                }
            }
            finally
            {
                Object.DestroyImmediate(player);
                if (grid != null) Object.DestroyImmediate(grid);
                Object.DestroyImmediate(room);
            }
        }

        private static Vector3[] Route(string name)
        {
            switch (name)
            {
                case "Crypt_Entry": return new[] { new Vector3(0, 0, 6), new Vector3(0, .9f, 10) };
                case "Crypt_Shrine": return new[] { new Vector3(0, 0, 2), new Vector3(0, 1.8f, 9) };
                case "Crypt_Vault": return new[] { new Vector3(-11, 0, -3), new Vector3(-11, 1.8f, 5) };
                case "Crypt_Skybridge": return new[] { new Vector3(-12, 0, 6), new Vector3(0, 1.8f, 6), new Vector3(12, 0, 6) };
                case "Crypt_Ossuary_02": return new[] { new Vector3(-12, 0, -16), new Vector3(-12, -1.5f, -9), new Vector3(-12, 0, 4) };
                default: throw new ArgumentException("Unknown physical height route", nameof(name));
            }
        }

        private static GameObject LoadRoom(string theme, string name, float yaw)
        {
            string path = "Assets/Emberdelve/Prefabs/DungeonModules/" +
                (theme.Length == 0 ? name : theme + "/" + theme + "_" + name) + ".prefab";
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            Assert.That(prefab, Is.Not.Null, path);
            GameObject room = Object.Instantiate(prefab);
            room.transform.SetPositionAndRotation(Origin, Quaternion.Euler(0, yaw, 0));
            var definition = room.GetComponent<DungeonModuleDefinition>();
            Assert.That(definition, Is.Not.Null);
            foreach (DungeonModuleSockets port in Ports)
            {
                Transform closure = definition.Closure(port);
                Assert.That(closure, Is.Not.Null, "Authored closure is required for " + port);
                closure.gameObject.SetActive(false);
            }
            Physics.SyncTransforms(); return room;
        }

        private static NavGridData Bake(GameObject room)
        {
            var grid = ScriptableObject.CreateInstance<NavGridData>();
            grid.origin = room.transform.position - new Vector3(18, 0, 18);
            grid.width = grid.height = 18; grid.cellSize = 2; grid.mask = new string('.', 324);
            NavGridSurface.Bake(grid, room.transform, LevelMask); return grid;
        }

        private static VectorInt RequireCell(NavGridData grid, Vector3 point, float height, string context)
        {
            VectorInt cell = grid.CellFromWorld(point);
            Assert.That(grid.IsWalkable(cell.x, cell.y), Is.True,
                context + " is blocked at " + cell + "; do not let nearest-cell fallback hide a missing route");
            Assert.That(grid.CellCenter(cell.x, cell.y).y - grid.origin.y, Is.EqualTo(height).Within(.035f),
                context + " must have its declared physical support height");
            return cell;
        }
    }
}
