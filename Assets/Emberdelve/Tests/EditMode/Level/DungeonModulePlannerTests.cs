using System;
using System.Collections.Generic;
using System.Reflection;
using Emberdelve.Flow;
using Emberdelve.Level;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Emberdelve.Level.Tests
{
    public sealed class DungeonModulePlannerTests
    {
        private readonly List<GameObject> _objects = new List<GameObject>();
        private readonly List<ScriptableObject> _assets = new List<ScriptableObject>();
        private List<DungeonModuleDefinition> _modules;

        [SetUp]
        public void SetUp()
        {
            _modules = new List<DungeonModuleDefinition>
            {
                CreateModule("entry", DungeonModuleKind.Entry, DungeonModuleSockets.All),
                CreateModule("hall", DungeonModuleKind.Hall, DungeonModuleSockets.All),
                CreateModule("ossuary", DungeonModuleKind.Ossuary, DungeonModuleSockets.All),
                CreateModule("bridge", DungeonModuleKind.Bridge, DungeonModuleSockets.All),
                CreateModule("extraction", DungeonModuleKind.Extraction, DungeonModuleSockets.All)
            };
        }

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject go in _objects) if (go != null) UnityEngine.Object.DestroyImmediate(go);
            _objects.Clear();
            foreach (ScriptableObject asset in _assets) if (asset != null) UnityEngine.Object.DestroyImmediate(asset);
            _assets.Clear();
        }

        [Test]
        public void SpawnSearchAvoidsAuthoredObstacleDeterministicallyAndFailsWhenBlocked()
        {
            DungeonLayoutData layout = ScriptableObject.CreateInstance<DungeonLayoutData>();
            _assets.Add(layout);
            layout.origin = new Vector3(1000f, 0f, 1000f);
            layout.cellSize = 2;
            GameObject root = new GameObject("spawn-test-module");
            _objects.Add(root);
            GameObject floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            floor.transform.SetParent(root.transform);
            floor.transform.position = layout.CellCenterWorld(Vector2Int.zero) + new Vector3(1f, -0.1f, 0f);
            floor.transform.localScale = new Vector3(8f, 0.2f, 8f);
            GameObject obstacle = GameObject.CreatePrimitive(PrimitiveType.Cube);
            obstacle.transform.SetParent(root.transform);
            obstacle.transform.position = layout.CellCenterWorld(Vector2Int.zero) + Vector3.up;
            obstacle.transform.localScale = new Vector3(1f, 2f, 1f);
            Physics.SyncTransforms();

            Assert.That(DungeonModuleSceneAssembler.TryFindClearSpawnCell(layout, root.transform, Vector2Int.zero,
                Vector2Int.zero, Vector2Int.right, out Vector2Int first), Is.True);
            Assert.That(first, Is.EqualTo(Vector2Int.right));
            Assert.That(DungeonModuleSceneAssembler.TryFindClearSpawnCell(layout, root.transform, Vector2Int.zero,
                Vector2Int.zero, Vector2Int.right, out Vector2Int second), Is.True);
            Assert.That(second, Is.EqualTo(first));
            obstacle.transform.localScale = new Vector3(8f, 2f, 8f);
            Physics.SyncTransforms();
            Assert.That(DungeonModuleSceneAssembler.TryFindClearSpawnCell(layout, root.transform, Vector2Int.zero,
                Vector2Int.zero, Vector2Int.right, out _), Is.False);
            obstacle.SetActive(false);
            floor.SetActive(false);
            Physics.SyncTransforms();
            Assert.That(DungeonModuleSceneAssembler.TryFindClearSpawnCell(layout, root.transform, Vector2Int.zero,
                Vector2Int.zero, Vector2Int.right, out _), Is.False, "a clear void is not a safe spawn");
        }

        [Test]
        public void ContentPlacementAvoidsSpawnsAndObstaclesAndFailsWithoutSafeCell()
        {
            DungeonLayoutData layout = ScriptableObject.CreateInstance<DungeonLayoutData>();
            _assets.Add(layout);
            layout.origin = new Vector3(1000f, 0f, 1000f);
            layout.cellSize = 2;
            layout.playerSpawns.Clear();
            layout.playerSpawns.Add(Vector2Int.zero);
            GameObject root = new GameObject("content-test-module");
            _objects.Add(root);
            GameObject floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            floor.transform.SetParent(root.transform);
            floor.transform.position = layout.CellCenterWorld(Vector2Int.zero) + new Vector3(1f, -.1f, 0f);
            floor.transform.localScale = new Vector3(8f, .2f, 8f);
            Physics.SyncTransforms();
            Vector3 pickupSize = Vector3.one * .3f;
            Assert.That(DungeonModuleSceneAssembler.TryFindContentCell(layout, root.transform, Vector2Int.zero,
                Vector2Int.zero, Vector2Int.right, pickupSize, out Vector2Int first), Is.True);
            Assert.That(first, Is.EqualTo(Vector2Int.right), "pickup must not occupy the player capsule");
            Assert.That(DungeonModuleSceneAssembler.TryFindContentCell(layout, root.transform, Vector2Int.zero,
                Vector2Int.zero, Vector2Int.right, pickupSize, out Vector2Int repeat), Is.True);
            Assert.That(repeat, Is.EqualTo(first), "server and clients choose deterministically");
            GameObject obstacle = GameObject.CreatePrimitive(PrimitiveType.Cube);
            obstacle.transform.SetParent(root.transform);
            obstacle.transform.position = layout.CellCenterWorld(Vector2Int.right) + Vector3.up;
            obstacle.transform.localScale = new Vector3(1f, 2f, 1f);
            Physics.SyncTransforms();
            Assert.That(DungeonModuleSceneAssembler.TryFindContentCell(layout, root.transform, Vector2Int.zero,
                Vector2Int.zero, Vector2Int.right, pickupSize, out _), Is.False, "must fail closed when all cells are unsafe");
            obstacle.SetActive(false);
            floor.SetActive(false);
            Physics.SyncTransforms();
            Assert.That(DungeonModuleSceneAssembler.TryFindContentCell(layout, root.transform, Vector2Int.zero,
                Vector2Int.zero, Vector2Int.right, pickupSize, out _), Is.False, "must not place content over a void");
        }

        [Test]
        public void SameSeedProducesSamePlanAndHash()
        {
            Assert.That(DungeonModulePlanner.TryCreate(74123, _modules, out DungeonModulePlan first, out string reason), Is.True, reason);
            Assert.That(DungeonModulePlanner.TryCreate(74123, _modules, out DungeonModulePlan second, out reason), Is.True, reason);
            Assert.That(second.LayoutHash, Is.EqualTo(first.LayoutHash));
            CollectionAssert.AreEqual(first.Placements, second.Placements);
        }

        [Test]
        public void PlannerRejectsCatalogWithoutDedicatedBridgeModule()
        {
            List<DungeonModuleDefinition> withoutBridge = new List<DungeonModuleDefinition>(_modules);
            withoutBridge.RemoveAll(module => module.Kind == DungeonModuleKind.Bridge);
            Assert.That(DungeonModulePlanner.TryCreate(74123, withoutBridge, out DungeonModulePlan plan, out string reason), Is.False);
            Assert.That(plan, Is.Null);
            Assert.That(reason, Does.Contain("bridge"));
        }

        [Test]
        public void DifferentSeedsVaryLayoutAndEveryPlanIsConnectedAndInBounds()
        {
            Assert.That(DungeonModulePlanner.TryCreate(11, _modules, out DungeonModulePlan baseline, out string reason), Is.True, reason);
            bool foundVariation = false;
            for (int seed = 12; seed < 80; seed++)
            {
                Assert.That(DungeonModulePlanner.TryCreate(seed, _modules, out DungeonModulePlan plan, out reason), Is.True, reason);
                Assert.That(plan.Validate(out reason), Is.True, reason);
                Assert.That(_modules[plan.At(1, 1).ModuleIndex].Kind, Is.EqualTo(DungeonModuleKind.Bridge),
                    "Every layout must reserve the center cell for the bridge module.");
                AssertBridgeEndsOpen(plan);
                Assert.That(plan.Placements, Has.Length.EqualTo(DungeonModulePlan.Width * DungeonModulePlan.Height));
                for (int i = 0; i < plan.Placements.Length; i++)
                {
                    DungeonModulePlacement p = plan.Placements[i];
                    Assert.That(p.X, Is.InRange(0, DungeonModulePlan.Width - 1));
                    Assert.That(p.Y, Is.InRange(0, DungeonModulePlan.Height - 1));
                    Assert.That(p.X, Is.EqualTo(i % DungeonModulePlan.Width));
                    Assert.That(p.Y, Is.EqualTo(i / DungeonModulePlan.Width));
                }
                if (plan.LayoutHash != baseline.LayoutHash) foundVariation = true;
            }
            Assert.That(foundVariation, Is.True, "The sampled seeds should produce more than one layout.");
        }

        [TestCase(2043687868)] // Live v21: the bridge deck ended in a closed west wall.
        [TestCase(11)]
        [TestCase(-1)]
        public void BridgeWalkableEndsAlwaysHaveOpenReciprocalPassages(int seed)
        {
            Assert.That(DungeonModulePlanner.TryCreate(seed, _modules, out DungeonModulePlan plan, out string reason), Is.True, reason);
            AssertBridgeEndsOpen(plan);
            Assert.That(plan.Validate(out reason), Is.True, reason);
        }

        private static void AssertBridgeEndsOpen(DungeonModulePlan plan)
        {
            DungeonModulePlacement bridge = plan.At(1, 1);
            bool northSouth = bridge.QuarterTurnsClockwise % 2 == 0;
            DungeonModuleSockets ends = northSouth
                ? DungeonModuleSockets.North | DungeonModuleSockets.South
                : DungeonModuleSockets.East | DungeonModuleSockets.West;
            Assert.That(bridge.OpenSockets & ends, Is.EqualTo(ends), "Both actual deck ends must be passages, not closure walls.");
            if (northSouth)
            {
                Assert.That(plan.At(1, 0).OpenSockets & DungeonModuleSockets.North, Is.EqualTo(DungeonModuleSockets.North));
                Assert.That(plan.At(1, 2).OpenSockets & DungeonModuleSockets.South, Is.EqualTo(DungeonModuleSockets.South));
            }
            else
            {
                Assert.That(plan.At(0, 1).OpenSockets & DungeonModuleSockets.East, Is.EqualTo(DungeonModuleSockets.East));
                Assert.That(plan.At(2, 1).OpenSockets & DungeonModuleSockets.West, Is.EqualTo(DungeonModuleSockets.West));
            }
        }

        [Test]
        public void DirectionalSocketRotationSurvivesCodecRoundTrip()
        {
            SerializedObject entry = new SerializedObject(_modules[0]);
            entry.FindProperty("availableSockets").intValue = (int)DungeonModuleSockets.North;
            entry.ApplyModifiedPropertiesWithoutUndo();
            DungeonModulePlan rotated = null;
            for (int seed = 0; seed < 200 && rotated == null; seed++)
            {
                if (DungeonModulePlanner.TryCreate(seed, _modules, out DungeonModulePlan candidate, out _) &&
                    candidate.At(0, 0).QuarterTurnsClockwise != 0) rotated = candidate;
            }
            Assert.That(rotated, Is.Not.Null, "A directional entry socket should require rotation for some generated tree.");
            byte[] payload = DungeonModulePlanCodec.Encode(rotated);
            Assert.That(DungeonModulePlanCodec.TryDecode(payload, _modules, out DungeonModulePlan decoded, out string reason), Is.True, reason);
            Assert.That(decoded.At(0, 0).QuarterTurnsClockwise, Is.EqualTo(rotated.At(0, 0).QuarterTurnsClockwise));
            Assert.That(decoded.LayoutHash, Is.EqualTo(rotated.LayoutHash));
        }

        [Test]
        public void PhysicalSocketAlignmentUsesWorldFacingDirectionsForAllQuarterTurns()
        {
            DungeonModuleDefinition west = CreateSocketTestClone();
            DungeonModuleDefinition east = CreateSocketTestClone();
            DungeonModuleDefinition south = CreateSocketTestClone();
            west.transform.position = Vector3.zero;
            east.transform.position = new Vector3(DungeonModulePlan.ModuleWidthCells * 2f, 0f, 0f);
            south.transform.position = new Vector3(0f, 0f, DungeonModulePlan.ModuleHeightCells * 2f);
            PlaceSocketChildren(west);
            PlaceSocketChildren(east);
            PlaceSocketChildren(south);

            const float tolerance = 0.001f;
            for (byte westTurn = 0; westTurn < 4; westTurn++)
            for (byte eastTurn = 0; eastTurn < 4; eastTurn++)
            for (byte northTurn = 0; northTurn < 4; northTurn++)
            {
                west.transform.rotation = Quaternion.Euler(0f, westTurn * 90f, 0f);
                east.transform.rotation = Quaternion.Euler(0f, eastTurn * 90f, 0f);
                south.transform.rotation = Quaternion.Euler(0f, northTurn * 90f, 0f);

                Transform eastSocket = west.Socket(DungeonModulePlanner.ToLocalSocketDirection(DungeonModuleSockets.East, westTurn));
                Transform westSocket = east.Socket(DungeonModulePlanner.ToLocalSocketDirection(DungeonModuleSockets.West, eastTurn));
                Transform northSocket = west.Socket(DungeonModulePlanner.ToLocalSocketDirection(DungeonModuleSockets.North, westTurn));
                Transform southSocket = south.Socket(DungeonModulePlanner.ToLocalSocketDirection(DungeonModuleSockets.South, northTurn));

                Assert.That(Vector3.Distance(eastSocket.position, westSocket.position), Is.LessThanOrEqualTo(tolerance),
                    "East/west sockets must meet for rotations " + westTurn + "/" + eastTurn);
                Assert.That(Vector3.Distance(northSocket.position, southSocket.position), Is.LessThanOrEqualTo(tolerance),
                    "North/south sockets must meet for rotations " + westTurn + "/" + northTurn);
            }
        }

        [Test]
        public void ModularRunStartStaysDeferredUntilAssemblerCommitsLayout()
        {
            GameObject host = new GameObject("modular-run-start-gate-test");
            _objects.Add(host);
            RunDirector director = host.AddComponent<RunDirector>();
            director.SetServerMode(true);
            Assert.That(director.RequireRuntimeLayoutBeforeStart(), Is.True);
            InvokeRunDirectorStart(director);
            Assert.That(director.IsStarted, Is.False, "The scene's Start callback must not start gameplay while waiting for a plan.");

            DungeonLayoutData runtimeLayout = ScriptableObject.CreateInstance<DungeonLayoutData>();
            runtimeLayout.name = "RuntimeLayout_Test";
            _assets.Add(runtimeLayout);
            Assert.That(director.SetRuntimeLayout(runtimeLayout), Is.True);
            Assert.That(director.IsStarted, Is.False, "Assigning layout alone must not release the gate before scene validation completes.");
            Assert.That(director.CompleteRuntimeLayoutInstallation(), Is.True);
            Assert.That(director.RuntimeLayoutInstallationComplete, Is.True);
            Assert.That(director.IsStarted, Is.True, "The deferred server run starts after the assembler commits the validated layout.");
        }

        [Test]
        public void LegacyRunDirectorStartsNormallyWithoutModularOptIn()
        {
            GameObject host = new GameObject("legacy-run-start-test");
            _objects.Add(host);
            host.SetActive(false);
            RunDirector director = host.AddComponent<RunDirector>();
            director.SetServerMode(true);
            DungeonModuleSceneAssembler assembler = host.AddComponent<DungeonModuleSceneAssembler>();

            MethodInfo awake = typeof(DungeonModuleSceneAssembler).GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(awake, Is.Not.Null, "The assembler must retain its Unity Awake callback.");
            awake.Invoke(assembler, null);

            InvokeRunDirectorStart(director);

            Assert.That(director.IsStarted, Is.True, "Legacy scenes do not arm the modular layout gate.");
            Assert.That(director.RuntimeLayoutInstallationComplete, Is.False);
        }

        [TestCase(Emberdelve.Rules.RoyalePartyMode.Solo)]
        [TestCase(Emberdelve.Rules.RoyalePartyMode.Duo)]
        [TestCase(Emberdelve.Rules.RoyalePartyMode.Trio)]
        public void RoyaleModeRoundTripsAndBindsMapAcknowledgement(Emberdelve.Rules.RoyalePartyMode mode)
        {
            Assert.True(DungeonModulePlanner.TryCreate(11, _modules, out var legacy, out var reason), reason);
            var plan = legacy.WithRoyaleMode(mode);
            Assert.IsNull(legacy.RoyaleMode);
            Assert.AreNotSame(legacy.Placements, plan.Placements);
            Assert.That(plan.LayoutHash, Is.Not.EqualTo(legacy.LayoutHash));
            foreach (var other in new[] { Emberdelve.Rules.RoyalePartyMode.Solo, Emberdelve.Rules.RoyalePartyMode.Duo, Emberdelve.Rules.RoyalePartyMode.Trio })
                if (other != mode) Assert.That(plan.LayoutHash, Is.Not.EqualTo(legacy.WithRoyaleMode(other).LayoutHash));
            byte[] payload = DungeonModulePlanCodec.Encode(plan);
            Assert.That(payload.Length, Is.EqualTo(DungeonModulePlanCodec.PayloadBytes));
            Assert.True(DungeonModulePlanCodec.TryDecode(payload, _modules, out var decoded, out reason), reason);
            Assert.That(decoded.RoyaleMode, Is.EqualTo(mode));
            Assert.That(decoded.LayoutHash, Is.EqualTo(plan.LayoutHash));
            Assert.That(decoded.Placements, Is.EqualTo(plan.Placements));
            payload[1] = (byte)(mode == Emberdelve.Rules.RoyalePartyMode.Solo ? 2 : 1);
            Assert.False(DungeonModulePlanCodec.TryDecode(payload, _modules, out _, out reason));
            Assert.That(reason, Does.Contain("hash"));
            payload[1] = 255;
            AssertDecodeRejected(payload);
            Assert.Throws<ArgumentOutOfRangeException>(() => legacy.WithRoyaleMode((Emberdelve.Rules.RoyalePartyMode)4));
        }

        [Test]
        public void CodecRejectsTruncatedWrongVersionAndTamperedSnapshots()
        {
            Assert.That(DungeonModulePlanner.TryCreate(5, _modules, out DungeonModulePlan plan, out string reason), Is.True, reason);
            byte[] valid = DungeonModulePlanCodec.Encode(plan);
            AssertDecodeRejected(Array.Empty<byte>());
            byte[] truncated = new byte[valid.Length - 1];
            Array.Copy(valid, truncated, truncated.Length);
            AssertDecodeRejected(truncated);
            byte[] wrongVersion = (byte[])valid.Clone();
            wrongVersion[0]++;
            AssertDecodeRejected(wrongVersion);
            byte[] tampered = (byte[])valid.Clone();
            tampered[tampered.Length - 1] ^= 0x40;
            AssertDecodeRejected(tampered);
        }

        [Test]
        public void PlanRejectsOutOfBoundsLookupAndCodecRejectsNullPlan()
        {
            Assert.That(DungeonModulePlanner.TryCreate(9, _modules, out DungeonModulePlan plan, out string reason), Is.True, reason);
            Assert.Throws<ArgumentOutOfRangeException>(() => plan.At(-1, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => plan.At(3, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => plan.At(0, 3));
            Assert.Throws<ArgumentNullException>(() => DungeonModulePlanCodec.Encode(null));
        }

        [Test]
        public void AuthoredResourcesCatalogContainsTenValidUniqueModulesAndCenterBridge()
        {
            const string catalogPath = "Assets/Emberdelve/Resources/Emberdelve/DungeonModuleCatalog.asset";
            DungeonModuleCatalog authoredCatalog = AssetDatabase.LoadAssetAtPath<DungeonModuleCatalog>(catalogPath);
            Assert.That(authoredCatalog, Is.Not.Null, "The builder must create the authored Resources catalog asset.");
            DungeonModuleCatalog catalog = Resources.Load<DungeonModuleCatalog>("Emberdelve/DungeonModuleCatalog");
            Assert.That(catalog, Is.Not.Null,
                "Runtime Resources.Load must resolve the authored catalog. AssetDatabase path=" +
                AssetDatabase.GetAssetPath(authoredCatalog) + ", GUID=" + AssetDatabase.AssetPathToGUID(catalogPath) +
                ", object type=" + authoredCatalog.GetType().FullName);
            Assert.That(catalog.Modules, Has.Length.EqualTo(10));
            HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
            int entries = 0, extractions = 0;
            foreach (DungeonModuleDefinition module in catalog.Modules)
            {
                Assert.That(module, Is.Not.Null);
                Assert.That(module.HasValidSockets(out string socketReason), Is.True, module.ModuleId + ": " + socketReason);
                Assert.That(module.WidthCells, Is.EqualTo(DungeonModulePlan.ModuleWidthCells));
                Assert.That(module.HeightCells, Is.EqualTo(DungeonModulePlan.ModuleHeightCells));
                Assert.That(ids.Add(module.ModuleId), Is.True, "Duplicate module id: " + module.ModuleId);
                if (module.Kind == DungeonModuleKind.Entry) entries++;
                if (module.Kind == DungeonModuleKind.Extraction) extractions++;
            }
            Assert.That(entries, Is.EqualTo(1));
            Assert.That(extractions, Is.EqualTo(1));
            Assert.That(DungeonModulePlanner.TryCreate(2026, catalog.Modules, out DungeonModulePlan plan, out string reason), Is.True, reason);
            Assert.That(plan.Validate(out reason), Is.True, reason);
            Assert.That(catalog.Modules[plan.At(1, 1).ModuleIndex].Kind, Is.EqualTo(DungeonModuleKind.Bridge));
        }

        private void AssertDecodeRejected(byte[] payload)
        {
            Assert.That(DungeonModulePlanCodec.TryDecode(payload, _modules, out DungeonModulePlan decoded, out string reason), Is.False);
            Assert.That(decoded, Is.Null);
            Assert.That(reason, Is.Not.Empty);
        }

        private DungeonModuleDefinition CreateModule(string id, DungeonModuleKind kind, DungeonModuleSockets sockets)
        {
            GameObject root = new GameObject(id);
            _objects.Add(root);
            DungeonModuleDefinition definition = root.AddComponent<DungeonModuleDefinition>();
            SerializedObject serialized = new SerializedObject(definition);
            serialized.FindProperty("moduleId").stringValue = id;
            serialized.FindProperty("kind").enumValueIndex = (int)kind;
            serialized.FindProperty("availableSockets").intValue = (int)sockets;
            serialized.FindProperty("widthCells").intValue = DungeonModulePlan.ModuleWidthCells;
            serialized.FindProperty("heightCells").intValue = DungeonModulePlan.ModuleHeightCells;
            SetChild(serialized, "playerSpawnAnchor", root.transform, "spawn");
            SetSocket(serialized, "northSocket", root.transform, "north", sockets, DungeonModuleSockets.North);
            SetSocket(serialized, "eastSocket", root.transform, "east", sockets, DungeonModuleSockets.East);
            SetSocket(serialized, "southSocket", root.transform, "south", sockets, DungeonModuleSockets.South);
            SetSocket(serialized, "westSocket", root.transform, "west", sockets, DungeonModuleSockets.West);
            AddNamedChild(root.transform, "Closure_North");
            AddNamedChild(root.transform, "Closure_East");
            AddNamedChild(root.transform, "Closure_South");
            AddNamedChild(root.transform, "Closure_West");
            serialized.ApplyModifiedPropertiesWithoutUndo();
            return definition;
        }

        private DungeonModuleDefinition CreateSocketTestClone()
        {
            GameObject clone = UnityEngine.Object.Instantiate(_modules[1].gameObject);
            clone.name += "_socket-test";
            _objects.Add(clone);
            return clone.GetComponent<DungeonModuleDefinition>();
        }

        private static void InvokeRunDirectorStart(RunDirector director)
        {
            MethodInfo start = typeof(RunDirector).GetMethod("Start", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(start, Is.Not.Null, "RunDirector must retain its Unity Start callback.");
            start.Invoke(director, null);
        }

        private static void PlaceSocketChildren(DungeonModuleDefinition definition)
        {
            definition.Socket(DungeonModuleSockets.North).localPosition = new Vector3(0f, 0f, 12f);
            definition.Socket(DungeonModuleSockets.East).localPosition = new Vector3(12f, 0f, 0f);
            definition.Socket(DungeonModuleSockets.South).localPosition = new Vector3(0f, 0f, -12f);
            definition.Socket(DungeonModuleSockets.West).localPosition = new Vector3(-12f, 0f, 0f);
        }

        private void SetSocket(SerializedObject serialized, string property, Transform root, string name,
            DungeonModuleSockets available, DungeonModuleSockets direction)
        {
            if ((available & direction) == 0) return;
            SetChild(serialized, property, root, name);
        }

        private void SetChild(SerializedObject serialized, string property, Transform root, string name)
        {
            Transform child = AddNamedChild(root, name);
            serialized.FindProperty(property).objectReferenceValue = child;
        }

        private Transform AddNamedChild(Transform root, string name)
        {
            Transform child = new GameObject(name).transform;
            child.SetParent(root, false);
            return child;
        }
    }
}
