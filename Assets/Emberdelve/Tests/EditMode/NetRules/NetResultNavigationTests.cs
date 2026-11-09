using Emberdelve.Core;
using NUnit.Framework;

namespace Emberdelve.NetRules.Tests
{
    public sealed class NetResultNavigationTests
    {
        private NetRoleKind _priorRole;

        [SetUp]
        public void SetUp()
        {
            _priorRole = NetRole.Current;
            NetRole.ResetToOffline();
            NetLobbyBridge.ResetForTests();
        }

        [TearDown]
        public void TearDown()
        {
            NetLobbyBridge.Register(null, null);
            NetLobbyBridge.ResetForTests();
            NetRole.Set(_priorRole);
        }

        [Test]
        public void RetryWaitsForShutdownAndPreservesDestinationExactlyOnce()
        {
            int starts = 0;
            NetLobbyBridge.Register((host, address, port, classId, slot) =>
            {
                starts++;
                Assert.IsFalse(host);
                Assert.AreEqual("127.0.0.1", address);
                Assert.AreEqual(7778, port);
                Assert.AreEqual("ranger", classId);
                Assert.AreEqual(NetLobbyBridge.SessionSlot, slot);
                return "";
            }, null);
            NetRole.Set(NetRoleKind.Client);
            NetLobbyBridge.QueueRetry(false, "127.0.0.1", 7778, "ranger");
            NetLobbyBridge.StartPendingRetry();
            Assert.AreEqual(0, starts);
            NetRole.ResetToOffline();
            NetLobbyBridge.StartPendingRetry();
            NetLobbyBridge.StartPendingRetry();
            Assert.AreEqual(1, starts);
        }

        [Test]
        public void FailedRetryReportsFailureWithoutAnAutomaticRetryLoop()
        {
            int starts = 0;
            NetLobbyBridge.Register((host, address, port, classId, slot) => { starts++; return "server unavailable"; }, null);
            NetLobbyBridge.QueueRetry(false, "127.0.0.1", 7777, "warrior");
            NetLobbyBridge.StartPendingRetry();
            NetLobbyBridge.StartPendingRetry();
            Assert.AreEqual(1, starts);
            Assert.AreEqual(NetLobbyPhase.Failed, NetLobbyBridge.Phase);
            Assert.AreEqual("server unavailable", NetLobbyBridge.Status);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ResultNavigationPassesTheRequestedActionToSessionOwner(bool retry)
        {
            bool? received = null;
            NetLobbyBridge.Register(null, null, request => { received = request; return true; });
            Assert.IsTrue(NetLobbyBridge.NavigateResult(retry));
            Assert.AreEqual(retry, received);
        }
    }
}
