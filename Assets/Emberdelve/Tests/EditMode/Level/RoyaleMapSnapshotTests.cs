using System;
using System.Reflection;
using System.Collections.Generic;
using Emberdelve.Interaction;
using Emberdelve.Core;
using Emberdelve.Flow;
using Emberdelve.Level;
using Emberdelve.Rules;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Emberdelve.Level.Tests
{
    public sealed class RoyaleMapSnapshotTests
    {
        private static DungeonModuleCatalog Catalog => AssetDatabase.LoadAssetAtPath<DungeonModuleCatalog>(
            "Assets/Emberdelve/Resources/Emberdelve/DungeonModuleCatalog.asset");
        private static DungeonModulePlan Plan(int seed, RoyalePartyMode mode, int floor = 1)
        {
            Assert.True(RoyaleFloorCatalogs.TryLoad(floor, out var catalog, out var reason), reason);
            Assert.True(DungeonModulePlanner.TryCreate(seed, catalog.Modules, out var plan, out reason), reason);
            return plan.WithRoyaleMode(mode);
        }
        [TestCase(RoyalePartyMode.Solo)] [TestCase(RoyalePartyMode.Duo)] [TestCase(RoyalePartyMode.Trio)]
        public void RoundTripBindsFloorOriginModeAndInitialMap(RoyalePartyMode mode)
        {
            var initial = Plan(11, mode); var plan = Plan(123, mode);
            Assert.True(RoyaleMapSnapshot.TryCreate(2, initial.LayoutHash, new Vector3(1000, 0, 2000), plan, out var sent));
            Assert.True(RoyaleMapSnapshot.TryDecode(sent.Encode(), Catalog.Modules, out var decoded, out var reason), reason);
            Assert.That(decoded.Hash, Is.EqualTo(sent.Hash)); Assert.That(decoded.Origin, Is.EqualTo(sent.Origin));
            Assert.That(decoded.CopyPlan().LayoutHash, Is.EqualTo(plan.LayoutHash));
            Assert.True(sent.Accepts(decoded.Acknowledge(true))); Assert.False(sent.Accepts(decoded.Acknowledge(false)));
            Assert.True(RoyaleMapSnapshot.TryCreate(3, initial.LayoutHash, sent.Origin, plan, out var other));
            Assert.False(sent.Accepts(other.Acknowledge(true)));
            Assert.True(RoyaleMapSnapshot.TryCreate(2, initial.LayoutHash, sent.Origin + Vector3.right, plan, out other));
            Assert.False(sent.Accepts(other.Acknowledge(true)));
            Assert.True(RoyaleMapSnapshot.TryCreate(2, "01234567", sent.Origin, plan, out other));
            Assert.False(sent.Accepts(other.Acknowledge(true)));
            var bytes = sent.Encode(); bytes[0] = 0; Assert.That(sent.Encode()[0], Is.EqualTo(1));
        }
        [Test] public void MalformedEnvelopesAndAcknowledgementsAreRejected()
        {
            var plan = Plan(11, RoyalePartyMode.Trio);
            Assert.True(RoyaleMapSnapshot.TryCreate(2, plan.LayoutHash, Vector3.right * 1000, plan, out var sent));
            foreach (int length in new[] { 0, RoyaleMapSnapshot.PayloadBytes - 1, RoyaleMapSnapshot.PayloadBytes + 1 })
                Assert.False(RoyaleMapSnapshot.TryDecode(new byte[length], Catalog.Modules, out _, out _));
            foreach (int index in new[] { 0, 1, 18 })
            { var bytes = sent.Encode(); bytes[index] = 255; Assert.False(RoyaleMapSnapshot.TryDecode(bytes, Catalog.Modules, out _, out _)); }
            var invalid = sent.Encode(); Array.Copy(BitConverter.GetBytes(float.NaN), 0, invalid, 6, 4);
            Assert.False(RoyaleMapSnapshot.TryDecode(invalid, Catalog.Modules, out _, out _));
            for (int i = 0; i < RoyaleMapSnapshot.AckBytes; i++)
            { var ack = sent.Acknowledge(true); ack[i] ^= 128; Assert.False(sent.Accepts(ack)); }
            Assert.False(sent.Accepts(new byte[RoyaleMapSnapshot.AckBytes + 1]));
        }
        [Test] public void InvalidAuthorityDescriptorsCannotBeCreated()
        {
            var plan = Plan(11, RoyalePartyMode.Solo);
            foreach (int floor in new[] { -1, 0, 1, 4 }) Assert.False(RoyaleMapSnapshot.TryCreate(floor, plan.LayoutHash, Vector3.zero, plan, out _));
            Assert.False(RoyaleMapSnapshot.TryCreate(2, "invalid", Vector3.zero, plan, out _));
            Assert.False(RoyaleMapSnapshot.TryCreate(2, plan.LayoutHash, Vector3.right * 100001, plan, out _));
            Assert.False(RoyaleMapSnapshot.TryCreate(2, plan.LayoutHash, Vector3.zero, plan.WithRoyaleMode(null), out _));
        }
        [TestCase(RoyalePartyMode.Solo, 11)] [TestCase(RoyalePartyMode.Duo, 11)]
        [TestCase(RoyalePartyMode.Trio, 11)] [TestCase(RoyalePartyMode.Trio, 2043687868)]
        public void ClientStagesBothMapsWithoutReplacingInitialWorld(RoyalePartyMode mode, int seed)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var setup = EditorSceneManager.GetSceneManagerSetup(); var role = NetRole.Current;
            RoyaleClientMaps maps = null; DungeonModuleSceneAssembler initial = null;
            try
            {
                NetRole.ResetToOffline(); RunContext.ResetHandoff(); DungeonModulePlanSession.Reset();
                RunContext.SelectRoyaleMode(mode); RunContext.SetNextRaidSeed((ulong)seed);
                EditorSceneManager.OpenScene("Assets/Emberdelve/Scenes/Emberdelve_ModularCrypts.unity", OpenSceneMode.Single);
                initial = Object.FindFirstObjectByType<DungeonModuleSceneAssembler>(FindObjectsInactive.Include);
                typeof(DungeonModuleSceneAssembler).GetMethod("Awake", flags).Invoke(initial, null);
                Assert.True(initial.RuntimeMapValidationComplete);
                var originalLayout = initial.RuntimeLayout; string originalHash = initial.AppliedLayoutHash;
                maps = new RoyaleClientMaps(initial, Catalog);
                Assert.True(RoyaleMapSnapshot.TryCreate(2, originalHash, new Vector3(1000, 0, 1000), Plan(unchecked(seed + 15838), mode, 2), out var second));
                Assert.False(maps.TryApply(second, out _), "Authority cannot run client geometry installer");
                NetRole.Set(NetRoleKind.Client);
                Assert.True(RoyaleMapSnapshot.TryCreate(3, originalHash, new Vector3(2000, 0, 1000), Plan(unchecked(seed + 23757), mode, 3), out var third));
                Assert.False(maps.TryApply(third, out _), "Do not skip floor2");
                Assert.True(RoyaleMapSnapshot.TryCreate(2, originalHash, originalLayout.origin, second.CopyPlan(), out var overlap));
                Assert.False(maps.TryApply(overlap, out _));
                Assert.True(RoyaleMapSnapshot.TryCreate(2, "01234567", second.Origin, second.CopyPlan(), out var foreign));
                Assert.False(maps.TryApply(foreign, out _));
                Assert.True(maps.TryApply(second, out var reason), reason); Assert.True(maps.TryApply(second, out reason), reason);
                Assert.That(maps.Count, Is.EqualTo(1));
                Assert.True(RoyaleMapSnapshot.TryCreate(2, originalHash, second.Origin + Vector3.right * 100, second.CopyPlan(), out var replacement));
                Assert.False(maps.TryApply(replacement, out _));
                Assert.False(maps.TryRetire(1, second.InitialHash ^ 1u, second.Hash, out _));
                Assert.False(maps.TryRetire(1, second.InitialHash, second.Hash ^ 1u, out _));
                Assert.False(maps.TryRetire(2, third.InitialHash, third.Hash, out _), "Next destination must exist first");
                Assert.True(maps.TryRetire(1, second.InitialHash, second.Hash, out reason), reason);
                Assert.True(initial.RuntimeContentRetired);
                Assert.True(initial.gameObject.activeSelf, "Retiring geometry must not disable the director host");
                Assert.True(maps.TryApply(third, out reason), reason); Assert.That(maps.Count, Is.EqualTo(2));
                Assert.False(maps.PublicStateReady(2, _ => false, _ => true));
                Assert.False(maps.PublicStateReady(2, _ => true, _ => false));
                Assert.True(maps.PublicStateReady(2, _ => true, _ => true));
                Assert.True(maps.PublicStateReady(3, _ => true, _ => true));
                Assert.True(maps.TryRetire(2, third.InitialHash, third.Hash, out reason), reason);
                Assert.True(maps.TryRetire(2, third.InitialHash, third.Hash, out reason), "Retirement is idempotent");
                Assert.True(maps.IsRetired(1)); Assert.True(maps.IsRetired(2));
                Assert.False(maps.TryApply(second, out _), "Stale plans cannot reactivate retired geometry");
                Assert.True(maps.TryApply(third, out reason), reason);
                var chestIds = new HashSet<string>();
                foreach (var chest in Object.FindObjectsByType<ChestInteractable>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                { Assert.True(chestIds.Add(chest.ChestId)); Assert.False(chest.IsRolled, "Client presentation never rolls loot"); }
                Assert.Greater(chestIds.Count, 3);
                Assert.That(initial.RuntimeLayout, Is.SameAs(originalLayout)); Assert.That(initial.AppliedLayoutHash, Is.EqualTo(originalHash));
                foreach (var map in Object.FindObjectsByType<DungeonModuleSceneAssembler>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                {
                    Assert.True(map.RuntimeMapValidationComplete);
                    Assert.That(map.gameObject.activeInHierarchy, Is.EqualTo(map.AcceptsSessionPlan || !map.RuntimeContentRetired));
                    Assert.That(map.RuntimeLayout.playerSpawns.Count, Is.EqualTo(mode == RoyalePartyMode.Trio ? 15 : 16));
                }
                var doorIds = new HashSet<string>(); int doorCount = 0;
                foreach (var door in Object.FindObjectsByType<DungeonDoorInteractable>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                {
                    Assert.True(doorIds.Add(door.DoorId), "No shared door identities across staged floors"); doorCount++;
                    if (door.DoorId.StartsWith("f2_"))
                    {
                        string id = door.DoorId; Assert.True(door.BindStagedFloor(2)); Assert.False(door.BindStagedFloor(3));
                        Assert.That(door.DoorId, Is.EqualTo(id), "Idempotent binding cannot reassign live door identity");
                    }
                }
                Assert.Greater(doorCount, 3);
                var barrelIds = new HashSet<ulong>();
                var barrels = Object.FindObjectsByType<BreakableBarrel>(FindObjectsInactive.Include, FindObjectsSortMode.None);
                Assert.Greater(barrels.Length, 3);
                foreach (var barrel in barrels) Assert.True(barrelIds.Add(barrel.StableId), "Barrel RPC identity must be unique across all floors");

                maps.Dispose(); Assert.That(maps.Count, Is.Zero);
                Assert.True(initial != null && initial.gameObject.activeInHierarchy);
            }
            finally
            {
                maps?.Dispose();
                if (initial != null) typeof(DungeonModuleSceneAssembler).GetMethod("ClearGeneratedModules", flags).Invoke(initial, null);
                DungeonModulePlanSession.Reset(); RunContext.ResetHandoff(); NetRole.Set(role);
                if (Array.Exists(setup, scene => scene.isActive && scene.isLoaded)) EditorSceneManager.RestoreSceneManagerSetup(setup);
                else EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
        }
    }
}
