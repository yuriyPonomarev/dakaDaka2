using System.Collections.Generic;
using Emberdelve.AI;
using Emberdelve.Combat;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Interaction;
using Emberdelve.Level;
using NUnit.Framework;
using UnityEngine;

namespace Emberdelve.Level.Tests
{
    public sealed class DungeonDoorInteractableTests
    {
        private sealed class StubPlayerTarget : ICombatEntity
        {
            public EntityKind Kind => EntityKind.RemotePlayer;
            public int CombatId => 42;
            public int TeamId => 1;
            public Vector3 CombatCenter { get; set; }
            public Collider HeadCollider => null;
            public bool IsDead => false;
            public float CurrentHealth => 100f;
            public float MaxHealth => 100f;
            public void ApplyDamage(DamageInfo damage) { }
        }

        [Test]
        public void EnemyBrainPursuesHeardTargetBehindOccludingDoorUsingFreshSearchPoint()
        {
            EnemyData data = ScriptableObject.CreateInstance<EnemyData>();
            GameObject occluder = GameObject.CreatePrimitive(PrimitiveType.Cube);
            try
            {
                data.sightRange = 15f;
                data.sightCone = 120f;
                data.hearingRange = 9f;
                data.memoryDuration = 7f;
                data.attackRange = 2f;
                data.chaseSpeed = 2.7f;
                occluder.transform.position = new Vector3(0f, 1.2f, 3f);
                occluder.transform.localScale = new Vector3(2f, 3f, 0.3f);
                Physics.SyncTransforms();
                var brain = new EnemyBrain(data, null, Vector3.zero, Physics.DefaultRaycastLayers);
                var target = new StubPlayerTarget { CombatCenter = new Vector3(0f, 1.15f, 6f) };
                Assert.IsFalse(HitScanner.HasLineOfSight(Vector3.up * 1.35f, target.CombatCenter,
                    Physics.DefaultRaycastLayers, 0.3f), "The fixture door panel must block sight.");

                BrainOrder order = brain.Tick(0.1f, 1f, Vector3.zero, Vector3.forward, target);

                Assert.AreEqual(EnemyState.Chase, brain.State, "A heard target behind the enemy should start a chase even without line of sight.");
                Assert.AreEqual(target.CombatCenter, brain.LastKnownPosition, "Hearing should record the actual search point.");
                Assert.AreEqual(target.CombatCenter, order.MoveTo, "The enemy should advance toward the heard target instead of its home point.");
                Assert.AreEqual(data.chaseSpeed, order.Speed, 0.001f);

                target.CombatCenter = new Vector3(0f, 1.15f, 7f);
                BrainOrder secondOrder = brain.Tick(0.1f, 2f, Vector3.zero, Vector3.forward, target);
                Assert.AreEqual(new Vector3(0f, 1.15f, 6f), brain.LastKnownPosition,
                    "Continuous hearing must not reveal the exact hidden target movement each frame.");
                Assert.AreEqual(new Vector3(0f, 1.15f, 6f), secondOrder.MoveTo,
                    "The enemy continues to the bounded last-heard point.");

                brain.Tick(0.1f, 9f, Vector3.zero, Vector3.forward, target);
                Assert.AreEqual(EnemyState.Investigate, brain.State, "A hidden target must leave chase after sound memory expires.");
                brain.Tick(0.1f, 13f, Vector3.zero, Vector3.forward, target);
                Assert.AreEqual(EnemyState.Patrol, brain.State, "Investigation must end instead of reacquiring the continuously hidden target.");
                Assert.AreEqual(new Vector3(0f, 1.15f, 6f), brain.LastKnownPosition,
                    "The expired sound point must not be refreshed to the hidden target's current position.");
            }
            finally { Object.DestroyImmediate(occluder); Object.DestroyImmediate(data); }
        }

        [Test]
        public void DoorwaySpawnerBuildsExactlyOneAlignedDoorForEveryRotatedSocketPair()
        {
            DungeonModuleCatalog catalog = Resources.Load<DungeonModuleCatalog>("Emberdelve/DungeonModuleCatalog");
            Assert.IsNotNull(catalog);
            Assert.IsTrue(DungeonModulePlanner.TryCreate(2026, catalog.Modules, out DungeonModulePlan plan, out string reason), reason);
            GameObject generated = new GameObject("doorway-spawner-test");
            try
            {
                var roots = new Transform[plan.Placements.Length];
                bool hasRotation = false;
                for (int i = 0; i < plan.Placements.Length; i++)
                {
                    DungeonModulePlacement placement = plan.Placements[i];
                    hasRotation |= placement.QuarterTurnsClockwise != 0;
                    Vector3 position = new Vector3((placement.X * DungeonModulePlan.ModuleWidthCells + 6) * 2f,
                        0f, (placement.Y * DungeonModulePlan.ModuleHeightCells + 6) * 2f);
                    DungeonModuleDefinition instance = Object.Instantiate(catalog.Modules[placement.ModuleIndex], position,
                        Quaternion.Euler(0f, placement.QuarterTurnsClockwise * 90f, 0f), generated.transform);
                    roots[i] = instance.transform;
                }
                Assert.IsTrue(hasRotation, "The fixture plan must exercise rotated modules.");

                Assert.IsTrue(DungeonDoorwaySpawner.TryBuild(plan, roots, out List<DungeonDoorInteractable> doors, out reason), reason);
                Assert.IsTrue(DungeonDoorwaySpawner.ValidateBuilt(plan, roots, out reason), reason);
                int expected = 0;
                for (int i = 0; i < plan.Placements.Length; i++)
                {
                    DungeonModulePlacement p = plan.Placements[i];
                    if (p.X + 1 < DungeonModulePlan.Width && (p.OpenSockets & DungeonModuleSockets.East) != 0) expected++;
                    if (p.Y + 1 < DungeonModulePlan.Height && (p.OpenSockets & DungeonModuleSockets.North) != 0) expected++;
                }
                Assert.AreEqual(expected, doors.Count);
                var ids = new HashSet<string>();
                foreach (DungeonDoorInteractable door in doors) Assert.IsTrue(ids.Add(door.DoorId), "Door IDs must be unique");
            }
            finally { Object.DestroyImmediate(generated); }
        }

        [Test]
        public void ReplicatedStateControlsBlockerAndRejectsOtherDoorIds()
        {
            GameObject root = new GameObject("door-test");
            GameObject hinge = new GameObject("hinge");
            GameObject panel = new GameObject("panel");
            try
            {
                hinge.transform.SetParent(root.transform);
                panel.transform.SetParent(root.transform);
                BoxCollider blocker = panel.AddComponent<BoxCollider>();
                DungeonDoorInteractable door = root.AddComponent<DungeonDoorInteractable>();
                door.Configure("door_0_0_E", hinge.transform, blocker, 90f);

                Assert.IsFalse(door.IsOpen);
                Assert.IsTrue(blocker.enabled);
                Assert.IsFalse(door.ApplyReplicatedState("door_1_0_E", true));
                Assert.IsFalse(door.IsOpen);
                Assert.IsTrue(door.ApplyReplicatedState("door_0_0_E", true));
                Assert.IsTrue(door.IsOpen);
                Assert.IsFalse(blocker.enabled);
                Assert.AreEqual(90f, hinge.transform.localEulerAngles.y, 0.01f);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void EnemyDoorOpeningRequiresAChaseTargetProximityAndExpiredCooldown()
        {
            Assert.IsTrue(DoorwayMovementBlocker.ShouldEnemyOpenDoor(true, 1.0f, 0f));
            Assert.IsFalse(DoorwayMovementBlocker.ShouldEnemyOpenDoor(false, 0.5f, 0f), "Idle enemies must leave doors alone.");
            Assert.IsFalse(DoorwayMovementBlocker.ShouldEnemyOpenDoor(true, DoorwayMovementBlocker.EnemyDoorOpenRange + 0.01f, 0f),
                "An enemy must be beside the panel before opening it.");
            Assert.IsFalse(DoorwayMovementBlocker.ShouldEnemyOpenDoor(true, 1.0f, 0.01f), "The cooldown must prevent repeated attempts.");
        }

        [Test]
        public void EnemyOpenOperationIsIdempotentAndDisablesTheBlocker()
        {
            GameObject root = new GameObject("enemy-open-door-test");
            GameObject hinge = new GameObject("hinge");
            GameObject panel = new GameObject("panel");
            try
            {
                hinge.transform.SetParent(root.transform);
                panel.transform.SetParent(root.transform);
                BoxCollider blocker = panel.AddComponent<BoxCollider>();
                DungeonDoorInteractable door = root.AddComponent<DungeonDoorInteractable>();
                door.Configure("door_enemy_test", hinge.transform, blocker, 90f);

                Assert.IsTrue(door.OpenAuthoritatively());
                Assert.IsTrue(door.IsOpen);
                Assert.IsFalse(blocker.enabled);
                Assert.IsFalse(door.OpenAuthoritatively(), "A second AI request must not toggle the door closed.");
                Assert.IsTrue(door.IsOpen);
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void EnemyMovementStopsAtClosedDoorAndPassesWhenDoorOpens()
        {
            GameObject root = new GameObject("doorway-movement-test");
            GameObject panel = new GameObject("panel");
            GameObject hinge = new GameObject("hinge");
            try
            {
                root.transform.rotation = Quaternion.Euler(0f, 90f, 0f);
                panel.transform.SetParent(root.transform, false);
                panel.transform.localPosition = new Vector3(0f, 1.5f, 0f);
                panel.transform.localScale = new Vector3(3f, 3f, 0.2f);
                hinge.transform.SetParent(root.transform, false);
                BoxCollider blocker = panel.AddComponent<BoxCollider>();
                DungeonDoorInteractable door = root.AddComponent<DungeonDoorInteractable>();
                door.Configure("door_test", hinge.transform, blocker, 90f);
                Physics.SyncTransforms();

                Vector3 start = new Vector3(-2f, 0f, 0f);
                Vector3 requested = Vector3.right * 4f;
                Vector3 stopped = DoorwayMovementBlocker.ClampDelta(start, requested, 1.5f, 0.22f,
                    Physics.DefaultRaycastLayers, new RaycastHit[16], new Collider[8], out DungeonDoorInteractable blockingDoor);
                Assert.AreSame(door, blockingDoor, "The movement clamp should identify the doorway that stopped the enemy.");
                Vector3 stoppedPosition = start + stopped;

                Assert.Less(stoppedPosition.x, 0f, "An enemy must not step through the closed blocker.");
                Assert.Greater(stoppedPosition.x, -0.6f, "The enemy should stop close to the panel, not at its start point.");

                Vector3 overlappingStart = new Vector3(-0.15f, 0f, 0f);
                Vector3 intoClosedDoor = DoorwayMovementBlocker.ClampDelta(overlappingStart, Vector3.right * 0.1f,
                    1.5f, 0.22f, Physics.DefaultRaycastLayers, new RaycastHit[16], new Collider[8]);
                Assert.AreEqual(Vector3.zero, intoClosedDoor, "An actor already overlapping the panel cannot move farther through it.");
                Vector3 retreatFromOverlap = DoorwayMovementBlocker.ClampDelta(overlappingStart, Vector3.left * 0.1f,
                    1.5f, 0.22f, Physics.DefaultRaycastLayers, new RaycastHit[16], new Collider[8]);
                Assert.AreEqual(Vector3.left * 0.1f, retreatFromOverlap, "An actor overlapping the door can retreat out of it.");

                Vector3 centeredRecovery = DoorwayMovementBlocker.ClampDelta(Vector3.zero, Vector3.right,
                    1.5f, 0.22f, Physics.DefaultRaycastLayers, new RaycastHit[16], new Collider[8]);
                Assert.Greater(centeredRecovery.x, 0f, "An actor centered in a newly closed panel can resolve toward one side.");
                Assert.Less(centeredRecovery.x, 0.4f, "Overlap recovery must stop after clearing the panel, without traversing the doorway.");

                Assert.IsTrue(door.ApplyReplicatedState("door_test", true));
                Vector3 throughOpenDoor = DoorwayMovementBlocker.ClampDelta(start, requested, 1.5f, 0.22f,
                    Physics.DefaultRaycastLayers, new RaycastHit[16], new Collider[8]);
                Assert.AreEqual(requested, throughOpenDoor, "The same movement should proceed through an open doorway.");
            }
            finally { Object.DestroyImmediate(root); }
        }

        [TestCase("door_0_0_E", true)]
        [TestCase("door_2_1_N", true)]
        [TestCase("", false)]
        [TestCase("door/0/0/E", false)]
        [TestCase("door_0_0_E;bad", false)]
        public void DoorIdsAreBoundedSafeTokens(string id, bool expected)
        {
            Assert.AreEqual(expected, DungeonDoorInteractable.IsValidDoorId(id));
        }
    }
}
