using Emberdelve.Level;
using Emberdelve.Rules;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Emberdelve.Level.Tests
{
    public sealed class StagedDungeonFloorTests
    {
        private DungeonModuleCatalog Catalog => AssetDatabase.LoadAssetAtPath<DungeonModuleCatalog>(
            "Assets/Emberdelve/Resources/Emberdelve/DungeonModuleCatalog.asset");
        private DungeonLayoutData Layout => AssetDatabase.LoadAssetAtPath<DungeonLayoutData>(
            "Assets/Emberdelve/Data/DungeonLayout_AshenHalls.asset");

        [TestCase(-107985638)] [TestCase(-1673065418)]
        public void ObservedEarlyDeathLayoutsKeepOpposingSpawnsCovered(int seed) =>
            RoyaleStageHasFullSeparatedPartiesOnSupportedGeometry(seed, RoyalePartyMode.Solo, 16);

        [TestCase(11, RoyalePartyMode.Solo, 16)]
        [TestCase(11, RoyalePartyMode.Duo, 16)]
        [TestCase(11, RoyalePartyMode.Trio, 15)]
        [TestCase(2043687868, RoyalePartyMode.Solo, 16)]
        [TestCase(2043687868, RoyalePartyMode.Duo, 16)]
        [TestCase(2043687868, RoyalePartyMode.Trio, 15)]
        [TestCase(-1606305943, RoyalePartyMode.Solo, 16)]
        [TestCase(-1606305943, RoyalePartyMode.Duo, 16)]
        [TestCase(-1606305943, RoyalePartyMode.Trio, 15)]
        [TestCase(1327740745, RoyalePartyMode.Solo, 16)]
        [TestCase(1327740745, RoyalePartyMode.Duo, 16)]
        [TestCase(1327740745, RoyalePartyMode.Trio, 15)]
        [TestCase(-113720714, RoyalePartyMode.Solo, 16)]
        [TestCase(-113720714, RoyalePartyMode.Duo, 16)]
        [TestCase(-113720714, RoyalePartyMode.Trio, 15)]
        [TestCase(1847760105, RoyalePartyMode.Solo, 16)]
        [TestCase(1847760105, RoyalePartyMode.Duo, 16)]
        [TestCase(1847760105, RoyalePartyMode.Trio, 15)]
        public void RoyaleStageHasFullSeparatedPartiesOnSupportedGeometry(int seed, RoyalePartyMode mode, int count)
        {
            Assert.True(DungeonModulePlanner.TryCreate(seed, Catalog.Modules, out var plan, out var reason), reason);
            var original = new List<Vector2Int>(Layout.playerSpawns);
            DungeonModuleSceneAssembler stage = null;
            try
            {
                Assert.False(DungeonModuleSceneAssembler.TryStageFloor(Layout, Catalog, plan,
                    new Vector3(1000, 0, 1000), out _, out _, mode), "Staging must not silently rewrite the caller's plan identity");
                var authorityPlan = plan.WithRoyaleMode(mode);
                Assert.True(DungeonModulePlanCodec.TryDecode(DungeonModulePlanCodec.Encode(authorityPlan), Catalog.Modules, out var receivedPlan, out reason), reason);
                Assert.True(DungeonModuleSceneAssembler.TryStageFloor(Layout, Catalog, receivedPlan,
                    new Vector3(1000, 0, 1000), out stage, out reason), reason);
                Assert.That(stage.AppliedLayoutHash, Is.EqualTo(authorityPlan.LayoutHash));
                var stale = authorityPlan.WithRoyaleMode(mode);
                var first = stale.Placements[0]; var second = stale.Placements[1];
                byte slot = first.PlayerSpawnSlot; first.PlayerSpawnSlot = second.PlayerSpawnSlot; second.PlayerSpawnSlot = slot;
                stale.Placements[0] = first; stale.Placements[1] = second;
                Assert.True(stale.Validate(out _));
                Assert.False(stage.ApplyDungeonModulePlan(stale, out reason));
                Assert.That(reason, Does.Contain("stale layout hash"));
                Assert.True(stage.ApplyDungeonModulePlan(receivedPlan, out reason), reason);
                Assert.False(stage.ApplyDungeonModulePlan(plan, out _), "A legacy-mode plan must not reuse this mode-specific world");
                Assert.False(stage.gameObject.activeSelf);
                var spawns = stage.RuntimeLayout.playerSpawns;
                Assert.That(spawns.Count, Is.EqualTo(count));
                Assert.That(new HashSet<Vector2Int>(spawns).Count, Is.EqualTo(count));
                Assert.That(Layout.playerSpawns, Is.EqualTo(original));
                stage.gameObject.SetActive(true);
                Physics.SyncTransforms();
                for (int i = 0; i < count; i++)
                {
                    Vector3 point = stage.RuntimeLayout.CellCenterWorld(spawns[i]);
                    Assert.True(DungeonModuleSceneAssembler.TryFindClearSpawnCell(stage.RuntimeLayout, stage.transform,
                        spawns[i], spawns[i], spawns[i], out _), "Unsupported or obstructed spawn " + i);
                    int leader = i / (int)mode * (int)mode;
                    Assert.That(Vector3.Distance(point, stage.RuntimeLayout.CellCenterWorld(spawns[leader])), Is.LessThanOrEqualTo(6.01f));
                    for (int j = 0; j < i; j++)
                        if (j / (int)mode != i / (int)mode)
                        {
                            Assert.That(Vector3.Distance(point, stage.RuntimeLayout.CellCenterWorld(spawns[j])), Is.GreaterThanOrEqualTo(8f));
                            Vector3 other = stage.RuntimeLayout.CellCenterWorld(spawns[j]);
                            if (Vector3.Distance(point, other) <= 24f)
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

        [TestCase(11)]
        [TestCase(2043687868)]
        [TestCase(-1606305943)]
        [TestCase(1327740745)]
        [TestCase(-113720714)]
        [TestCase(1847760105)]
        public void AuthoredFloorStagesWithoutDirectorOrMutatingCurrentMap(int seed)
        {
            Assert.True(DungeonModulePlanner.TryCreate(seed, Catalog.Modules, out var plan, out var reason), reason);
            var liveGeometry = new GameObject("Props");
            liveGeometry.transform.position = new Vector3(3, 4, 5);
            var priorPlan = DungeonModulePlanSession.Current;
            DungeonModuleSceneAssembler stage = null;
            try
            {
                Assert.True(DungeonModuleSceneAssembler.TryStageFloor(Layout, Catalog, plan,
                    new Vector3(1000, 0, 1000), out stage, out reason), reason);
                Assert.False(stage.gameObject.activeSelf);
                Assert.False(stage.AcceptsSessionPlan);
                Assert.True(stage.RuntimeMapValidationComplete);
                Assert.That(stage.RuntimeLayout.playerSpawns.Count, Is.EqualTo(9));
                Assert.That(stage.RuntimeLayout.origin, Is.EqualTo(new Vector3(1000, 0, 1000)));
                Assert.That(stage.AppliedLayoutHash, Is.EqualTo(plan.LayoutHash));
                string originalMask = stage.RuntimeNavGrid.mask;
                var spawnCell = stage.RuntimeLayout.playerSpawns[0];
                char[] blocked = originalMask.ToCharArray();
                blocked[spawnCell.y * stage.RuntimeNavGrid.width + spawnCell.x] = '#';
                stage.RuntimeNavGrid.mask = new string(blocked);
                object[] validation = { stage.RuntimeLayout, null };
                Assert.False((bool)typeof(DungeonModuleSceneAssembler).GetMethod("ValidateNavConnectivity",
                    BindingFlags.Instance | BindingFlags.NonPublic).Invoke(stage, validation), "Validation must inspect baked obstacles");
                stage.RuntimeNavGrid.mask = originalMask;
                Assert.That(DungeonModulePlanSession.Current, Is.SameAs(priorPlan));
                Assert.True(liveGeometry.activeSelf);
                Assert.That(liveGeometry.transform.position, Is.EqualTo(new Vector3(3, 4, 5)));
                Assert.True(DungeonModulePlanner.TryCreate(seed + 1, Catalog.Modules, out var other, out reason), reason);
                var stagedLayout = stage.RuntimeLayout;
                Assert.False(stage.ApplyDungeonModulePlan(other, out reason));
                Assert.That(stage.RuntimeLayout, Is.SameAs(stagedLayout));
                Assert.That(stage.AppliedLayoutHash, Is.EqualTo(plan.LayoutHash));
            }
            finally
            {
                if (stage != null)
                {
                    var ownedLayout = stage.RuntimeLayout;
                    var ownedNav = stage.RuntimeNavGrid;
                    stage.DiscardStagedFloor();
                    Assert.True(ownedLayout == null); Assert.True(ownedNav == null);
                }
                Object.DestroyImmediate(liveGeometry);
            }
        }

        [Test]
        public void RepeatingContentPreferencesNeverOverlapOtherContent()
        {
            var source = Object.Instantiate(Layout);
            DungeonModuleSceneAssembler stage = null;
            try
            {
                var item = source.lootSpawns[0].item;
                source.lootSpawns.Clear();
                for (int i = 0; i < 28; i++) source.lootSpawns.Add(new LootSpawnSpec { item = item, amount = 1 });
                Assert.True(DungeonModulePlanner.TryCreate(11, Catalog.Modules, out var plan, out var reason), reason);
                Assert.True(DungeonModuleSceneAssembler.TryStageFloor(source, Catalog, plan.WithRoyaleMode(RoyalePartyMode.Trio),
                    new Vector3(1000, 0, 1000), out stage, out reason), reason);
                var cells = new HashSet<Vector2Int>();
                foreach (var spec in stage.RuntimeLayout.chestSpawns) Assert.True(cells.Add(spec.cell));
                foreach (var spec in stage.RuntimeLayout.lootSpawns) Assert.True(cells.Add(spec.cell));
                foreach (var spec in stage.RuntimeLayout.enemySpawns) Assert.True(cells.Add(spec.cell));
            }
            finally
            {
                if (stage != null) stage.DiscardStagedFloor();
                Object.DestroyImmediate(source);
            }
        }

        [Test]
        public void InvalidStagingRequestLeavesNoCandidateAndKeepsExistingGeometry()
        {
            var liveGeometry = new GameObject("Props");
            try
            {
                Assert.False(DungeonModuleSceneAssembler.TryStageFloor(Layout, Catalog, null,
                    Vector3.zero, out var stage, out _));
                Assert.That(stage, Is.Null);
                Assert.False(DungeonModuleSceneAssembler.TryStageFloor(Layout, Catalog, null,
                    new Vector3(float.NaN, 0, 0), out stage, out _));
                Assert.That(stage, Is.Null);
                Assert.True(liveGeometry.activeSelf);
                Assert.That(Object.FindObjectsByType<DungeonModuleSceneAssembler>(FindObjectsInactive.Include,
                    FindObjectsSortMode.None), Is.Empty);
            }
            finally { Object.DestroyImmediate(liveGeometry); }
        }
    }
}
