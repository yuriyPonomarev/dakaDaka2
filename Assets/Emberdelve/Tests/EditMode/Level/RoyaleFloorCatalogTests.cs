using Emberdelve.Rules;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Emberdelve.Level.Tests
{
    public sealed class RoyaleFloorCatalogTests
    {
        [Test]
        public void AuthoredDestinationsKeepEveryPartySupportedConnectedAndCovered(
            [Values(2, 3)] int floor,
            [Values(RoyalePartyMode.Solo, RoyalePartyMode.Duo, RoyalePartyMode.Trio)] RoyalePartyMode mode,
            [Values(11, 2043687868)] int seed)
        {
            var source = AssetDatabase.LoadAssetAtPath<DungeonLayoutData>(
                "Assets/Emberdelve/Data/DungeonLayout_AshenHalls.asset");
            Assert.IsNotNull(source);
            var originalSpawns = new List<Vector2Int>(source.playerSpawns);
            Assert.True(RoyaleFloorCatalogs.TryLoad(floor, out var catalog, out var reason), reason);
            Assert.True(DungeonModulePlanner.TryCreate(seed, catalog.Modules, out var plan, out reason), reason);
            plan = plan.WithRoyaleMode(mode);
            DungeonModuleSceneAssembler stage = null;
            try
            {
                Assert.True(DungeonModuleSceneAssembler.TryStageFloor(source, catalog, plan,
                    new Vector3(floor * 1000, 0, 1000), out stage, out reason), reason);
                Assert.True(stage.RuntimeMapValidationComplete);
                Assert.False(stage.gameObject.activeSelf);
                stage.gameObject.SetActive(true);
                Physics.SyncTransforms();
                // The established same-plan validator rechecks closure state and baked navigation
                // connectivity, including every spawn, extraction, chest, loot and enemy cell.
                Assert.True(stage.ApplyDungeonModulePlan(plan, out reason), reason);
                Assert.True(stage.BindStagedFloorIdentity(floor, out reason), reason);
                foreach (var door in stage.GetComponentsInChildren<Emberdelve.Interaction.DungeonDoorInteractable>())
                    Assert.That(door.DoorId, Does.StartWith("f" + floor + "_"));
                var layout = stage.RuntimeLayout;
                var spawns = layout.playerSpawns;
                int count = mode == RoyalePartyMode.Trio ? 15 : 16;
                Assert.That(spawns.Count, Is.EqualTo(count));
                Assert.That(new HashSet<Vector2Int>(spawns).Count, Is.EqualTo(count));
                Assert.That(source.playerSpawns, Is.EqualTo(originalSpawns));
                Assert.That(stage.AppliedLayoutHash, Is.EqualTo(plan.LayoutHash));
                for (int i = 0; i < count; i++)
                {
                    Vector3 point = layout.CellCenterWorld(spawns[i]);
                    Assert.True(DungeonModuleSceneAssembler.TryFindClearSpawnCell(layout, stage.transform,
                        spawns[i], spawns[i], spawns[i], out _), "Unsupported or obstructed spawn " + i);
                    int leader = i / (int)mode * (int)mode;
                    Assert.That(Vector3.Distance(point, layout.CellCenterWorld(spawns[leader])), Is.LessThanOrEqualTo(6.01f));
                    for (int j = 0; j < i; j++)
                    {
                        if (j / (int)mode == i / (int)mode) continue;
                        Vector3 other = layout.CellCenterWorld(spawns[j]);
                        float distance = Vector3.Distance(point, other);
                        Assert.That(distance, Is.GreaterThanOrEqualTo(8f));
                        if (distance > 24f) continue;
                        foreach (float height in new[] { 1.2f, 1.7f })
                            Assert.True(Physics.Linecast(point + Vector3.up * height, other + Vector3.up * height,
                                out var cover, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)
                                && cover.transform.IsChildOf(stage.transform),
                                "Nearby opposing spawns need map cover: " + i + "/" + j + " height=" + height);
                    }
                }
            }
            finally { if (stage != null) stage.DiscardStagedFloor(); }
        }

        [TestCase(2, RoyalePartyMode.Solo)] [TestCase(2, RoyalePartyMode.Duo)] [TestCase(2, RoyalePartyMode.Trio)]
        [TestCase(3, RoyalePartyMode.Solo)] [TestCase(3, RoyalePartyMode.Duo)] [TestCase(3, RoyalePartyMode.Trio)]
        public void DestinationEnvelopeResolvesItsOwnCatalogAndRejectsOtherThemes(int floor, RoyalePartyMode mode)
        {
            Assert.True(RoyaleFloorCatalogs.TryLoad(floor, out var catalog, out var reason), reason);
            foreach (int seed in new[] { 11, 2043687868 })
            {
                Assert.True(DungeonModulePlanner.TryCreate(seed, catalog.Modules, out var plan, out reason), reason);
                plan = plan.WithRoyaleMode(mode);
                Assert.True(RoyaleMapSnapshot.TryCreate(floor, "12345678", Vector3.right * 1000, plan, out var snapshot));
                Assert.True(RoyaleMapSnapshot.TryDecode(snapshot.Encode(), out var decoded, out reason), reason);
                Assert.That(decoded.Hash, Is.EqualTo(snapshot.Hash));
                Assert.That(decoded.CopyPlan().CatalogHash, Is.EqualTo(plan.CatalogHash));
                for (int other = 1; other <= 3; other++)
                {
                    if (other == floor) continue;
                    Assert.True(RoyaleFloorCatalogs.TryLoad(other, out var foreign, out reason), reason);
                    Assert.False(RoyaleMapSnapshot.TryDecode(snapshot.Encode(), foreign.Modules, out _, out _));
                }
                var tampered = snapshot.Encode(); tampered[1] = (byte)(floor == 2 ? 3 : 2);
                Assert.False(RoyaleMapSnapshot.TryDecode(tampered, out _, out _), "A valid plan cannot be relabeled as another floor");
            }
        }

        [TestCase(-1)] [TestCase(0)] [TestCase(4)]
        public void InvalidFloorNeverFallsBackToRuins(int floor)
        {
            Assert.False(RoyaleFloorCatalogs.TryLoad(floor, out var catalog, out var reason));
            Assert.IsNull(catalog); Assert.IsNotEmpty(reason);
        }
    }
}
