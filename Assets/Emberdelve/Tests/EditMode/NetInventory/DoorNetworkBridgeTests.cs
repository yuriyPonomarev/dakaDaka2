using Emberdelve.Net;
using NUnit.Framework;
using Emberdelve.Interaction;
using UnityEngine;

namespace Emberdelve.NetInventory.Tests
{
    public sealed class DoorNetworkBridgeTests
    {
        [Test]
        public void HostUsesInProcessServerAuthorityWhileRemoteOwnerUsesRpcBridge()
        {
            Assert.IsTrue(NetPlayerRig.ShouldUseHostDoorBridge(NetPlayerRole.HostPlayer));
            Assert.IsFalse(NetPlayerRig.ShouldUseHostDoorBridge(NetPlayerRole.Owner));
            Assert.IsFalse(NetPlayerRig.ShouldUseHostDoorBridge(NetPlayerRole.Observer));
            Assert.IsFalse(NetPlayerRig.ShouldUseHostDoorBridge(NetPlayerRole.Server));
        }

        [Test]
        public void DoorSnapshotRequestRetriesOnlyUntilReadyOrBoundedLimit()
        {
            Assert.IsTrue(NetInventoryClient.ShouldRequestDoorSnapshot(false, 0));
            Assert.IsTrue(NetInventoryClient.ShouldRequestDoorSnapshot(false, 5));
            Assert.IsFalse(NetInventoryClient.ShouldRequestDoorSnapshot(false, 6));
            Assert.IsFalse(NetInventoryClient.ShouldRequestDoorSnapshot(true, 0));
        }

        [Test]
        public void SecretWallUsesCompletedDoorSnapshotAndRejectsStaleUpdates()
        {
            var root = new GameObject("secret-door-snapshot-test");
            try
            {
                var panel = new GameObject("panel");
                panel.transform.SetParent(root.transform, false);
                panel.transform.localPosition = Vector3.right * 10f;
                var wall = panel.AddComponent<BoxCollider>();
                var probe = new GameObject("lever-probe");
                probe.transform.SetParent(root.transform, false);
                var trigger = probe.AddComponent<BoxCollider>();
                trigger.isTrigger = true;
                var secret = root.AddComponent<DungeonDoorInteractable>();
                secret.ConfigureSecret("cache", panel.transform, wall, trigger, Vector3.up * 3.4f);
                var room = new GameObject("SecretRoomVolume");
                room.transform.SetParent(root.transform, false);
                var volume = room.AddComponent<BoxCollider>();
                volume.isTrigger = true;
                secret.ConfigureSecretRoom(volume);
                Assert.True(secret.BindModuleCell(2, 1));
                Assert.True(secret.BindStagedFloor(3));
                var client = root.AddComponent<NetInventoryClient>();
                client.OnDoorState(string.Empty, false, 7, true, false);
                client.OnDoorState(secret.DoorId, true, 7, false, false);
                Assert.False(secret.IsOpen, "Partial snapshots cannot reveal a cache.");
                client.OnDoorState(string.Empty, false, 7, false, true);
                Assert.True(client.DoorStateReady);
                Assert.True(secret.IsOpen);
                Assert.False(wall.enabled);
                Assert.AreEqual(new Vector3(10f, 3.4f, 0f), panel.transform.localPosition);
                client.OnDoorState(secret.DoorId, false, 6, false, false);
                Assert.True(secret.IsOpen, "An older packet cannot restore the blocking panel.");
                client.OnDoorState("f2_secret_2_1_cache", false, 8, false, false);
                Assert.True(secret.IsOpen, "An identically placed cache on another floor has a distinct identity.");
                client.OnDoorState(secret.DoorId, false, 9, false, false);
                Assert.False(secret.IsOpen);
                Assert.True(wall.enabled);
            }
            finally { Object.DestroyImmediate(root); }
        }
    }
}
