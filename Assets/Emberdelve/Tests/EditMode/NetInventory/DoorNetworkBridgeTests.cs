using Emberdelve.Net;
using NUnit.Framework;

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
    }
}
