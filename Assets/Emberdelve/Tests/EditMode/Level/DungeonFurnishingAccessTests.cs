using System.Collections.Generic;
using System.Linq;
using Emberdelve.AI;
using Emberdelve.Core;
using Emberdelve.Interaction;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Emberdelve.Level.Tests
{
    public sealed class DungeonFurnishingAccessTests
    {
        private static readonly string[] Names = { "Crypt_Entry", "Crypt_Hall_01", "Crypt_Hall_02", "Crypt_Hall_03",
            "Crypt_Ossuary_01", "Crypt_Ossuary_02", "Crypt_Shrine", "Crypt_Vault", "Crypt_Extraction", "Crypt_Skybridge" };
        private static readonly Vector3 Origin = new Vector3(16000, 0, 16000);
        private static int LevelMask => LayerMask.GetMask(GameConfig.LevelLayerName);

        [TestCase("")] [TestCase("Crypt")] [TestCase("Infernal")]
        public void FurnishedSideChambersRemainReachableBeyondTheirDoorways(string theme)
        {
            for (int index = 0; index < Names.Length; index++)
            {
                GameObject room = LoadRoom(theme, Names[index], (index % 4) * 90f);
                NavGridData grid = null;
                try
                {
                    var chambers = room.GetComponentsInChildren<Transform>()
                        .Where(t => t.name == "Side_Chamber").ToArray();
                    if (chambers.Length == 0) continue;
                    grid = Bake(room);
                    var finder = new NavGridPathfinder(grid);
                    Vector3 start = room.transform.TransformPoint(Vector3.zero);
                    foreach (Transform chamber in chambers)
                    {
                        // Find the south doorway from the saved wall collision, not a duplicated room depth table.
                        float front = float.PositiveInfinity;
                        foreach (var wall in chamber.GetComponentsInChildren<BoxCollider>())
                        {
                            for (int corner = 0; corner < 8; corner++)
                            {
                                Vector3 sign = new Vector3((corner & 1) == 0 ? -1 : 1,
                                    (corner & 2) == 0 ? -1 : 1, (corner & 4) == 0 ? -1 : 1);
                                Vector3 point = wall.transform.TransformPoint(wall.center + Vector3.Scale(wall.size * .5f, sign));
                                front = Mathf.Min(front, chamber.InverseTransformPoint(point).z);
                            }
                        }
                        Assert.That(float.IsInfinity(front), Is.False, "Saved chamber needs physical walls");
                        Vector3 threshold = chamber.TransformPoint(new Vector3(0, 0, front + 1.5f));
                        RequireAccess(room, grid, finder, start, threshold, "inside chamber doorway");
                        // Ossuary02's original east-west tomb occupies the chamber centre.
                        // Inspect its north-east viewing aisle at an exact navigation cell instead.
                        Vector3 interior = Names[index] == "Crypt_Ossuary_02"
                            ? room.transform.TransformPoint(new Vector3(7, 0, -7)) : chamber.position;
                        RequireAccess(room, grid, finder, threshold, interior, "chamber interior");
                    }
                }
                finally
                {
                    if (grid != null) Object.DestroyImmediate(grid);
                    Object.DestroyImmediate(room);
                }
            }
        }

        public static IEnumerable<TestCaseData> SecretRooms()
        {
            foreach (string theme in new[] { "", "Crypt", "Infernal" })
                foreach (string name in new[] { "Crypt_Hall_02", "Crypt_Ossuary_01", "Crypt_Ossuary_02", "Crypt_Vault" })
                    yield return new TestCaseData(theme, name)
                        .SetName("FurnishedSecretAccess_" + (theme.Length == 0 ? "Ruins" : theme) + "_" + name);
        }

        [TestCaseSource(nameof(SecretRooms))]
        public void FurnishingPreservesLeverApproachAndOpenedCacheAccess(string theme, string name)
        {
            GameObject room = LoadRoom(theme, name, 90);
            NavGridData grid = null;
            try
            {
                DungeonDoorInteractable secret = room.GetComponentsInChildren<DungeonDoorInteractable>()
                    .Single(d => d.IsSecret);
                Assert.That(secret.ValidateSecret(out string reason), Is.True, reason);
                Assert.That(secret.IsOpen, Is.False);
                Assert.That(secret.BlockerEnabled, Is.True);
                // Approach the actual lever from its exposed side, within normal interaction reach.
                Vector3 approach = secret.transform.position - room.transform.forward * 1.5f;
                approach.y = room.transform.position.y;
                grid = Bake(room);
                RequireAccess(room, grid, new NavGridPathfinder(grid), room.transform.position, approach, "closed cache lever");
                Object.DestroyImmediate(grid);
                grid = null;

                // Exercise the authored lift/disabled blocker rather than deleting a wall to make the test pass.
                secret.ToggleAuthoritatively();
                Assert.That(secret.IsOpen, Is.True);
                Assert.That(secret.BlockerEnabled, Is.False);
                Physics.SyncTransforms();
                Transform chest = secret.transform.Find("SecretChestAnchor");
                Assert.That(chest, Is.Not.Null, "The original cache anchor must survive furnishing");
                grid = Bake(room);
                RequireAccess(room, grid, new NavGridPathfinder(grid), approach, chest.position, "opened cache chest");
            }
            finally
            {
                if (grid != null) Object.DestroyImmediate(grid);
                Object.DestroyImmediate(room);
            }
        }

        private static void RequireAccess(GameObject room, NavGridData grid, NavGridPathfinder finder,
            Vector3 from, Vector3 target, string label)
        {
            string context = room.name + " " + label + " at " + room.transform.InverseTransformPoint(target);
            Assert.That(Physics.CheckCapsule(target + Vector3.up * .55f, target + Vector3.up * 1.5f,
                .34f, LevelMask, QueryTriggerInteraction.Ignore), Is.False, context + " has no standing clearance");
            VectorInt cell = grid.CellFromWorld(target);
            Assert.That(grid.IsWalkable(cell.x, cell.y), Is.True, context + " cell is blocked");
            Assert.That(grid.CellCenter(cell.x, cell.y).y - room.transform.position.y, Is.EqualTo(0).Within(.035f),
                context + " must remain supported by its original floor");
            var path = new List<Vector3>();
            Assert.That(finder.TryFindPath(from, target, path), Is.True, context + " is unreachable");
            Assert.That((path[path.Count - 1] - grid.CellCenter(cell.x, cell.y)).sqrMagnitude, Is.LessThan(.0001f),
                context + " must reach the requested cell without nearest-cell fallback");
            Assert.That(finder.TryFindPath(target, from, path), Is.True, context + " needs a return route");
        }

        private static GameObject LoadRoom(string theme, string name, float yaw)
        {
            string path = "Assets/Emberdelve/Prefabs/DungeonModules/" +
                (theme.Length == 0 ? name : theme + "/" + theme + "_" + name) + ".prefab";
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            Assert.That(prefab, Is.Not.Null, path);
            GameObject room = Object.Instantiate(prefab);
            room.transform.SetPositionAndRotation(Origin, Quaternion.Euler(0, yaw, 0));
            var definition = room.GetComponent<DungeonModuleDefinition>();
            foreach (var socket in new[] { DungeonModuleSockets.North, DungeonModuleSockets.East,
                DungeonModuleSockets.South, DungeonModuleSockets.West })
                definition.Closure(socket).gameObject.SetActive(false);
            Physics.SyncTransforms();
            return room;
        }

        private static NavGridData Bake(GameObject room)
        {
            Physics.SyncTransforms();
            var grid = ScriptableObject.CreateInstance<NavGridData>();
            grid.origin = room.transform.position - new Vector3(18, 0, 18);
            grid.width = grid.height = 18;
            grid.cellSize = 2;
            grid.mask = new string('.', 324);
            NavGridSurface.Bake(grid, room.transform, LevelMask);
            return grid;
        }
    }
}
