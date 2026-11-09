using Emberdelve.Interaction;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Level;
using NUnit.Framework;
using UnityEngine;

namespace Emberdelve.Tests.NetInventory
{
    public sealed class ChestNetworkIdTests
    {
        private GameObject _go;
        private GameObject _presentationRoot;
        private GameObject _secondPresentationRoot;
        private GameObject _authoredChestObject;
        private GameObject _serverSpawnerObject;
        private DungeonLayoutData _layout;
        private DungeonLayoutData _secondLayout;
        private LootTable _lootTable;
        private LootTable _secondLootTable;

        [TearDown]
        public void TearDown()
        {
            if (_go != null) Object.DestroyImmediate(_go);
            if (_presentationRoot != null) Object.DestroyImmediate(_presentationRoot);
            if (_secondPresentationRoot != null) Object.DestroyImmediate(_secondPresentationRoot);
            if (_authoredChestObject != null) Object.DestroyImmediate(_authoredChestObject);
            if (_serverSpawnerObject != null) Object.DestroyImmediate(_serverSpawnerObject);
            if (_layout != null) Object.DestroyImmediate(_layout);
            if (_secondLayout != null) Object.DestroyImmediate(_secondLayout);
            if (_lootTable != null) Object.DestroyImmediate(_lootTable);
            if (_secondLootTable != null) Object.DestroyImmediate(_secondLootTable);
        }

        [TestCase("Chest_Supply_4_3_0")]
        [TestCase("Chest-Ember-12-7-2")]
        public void StableAuthoredIds_AreAccepted(string chestId)
        {
            Assert.IsTrue(ChestInteractable.IsValidNetworkChestId(chestId));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("Chest Supply 4")]
        [TestCase("../Chest")]
        [TestCase("Chest_4/3")]
        [TestCase("Chest_é")]
        public void InvalidIds_AreRejected(string chestId)
        {
            Assert.IsFalse(ChestInteractable.IsValidNetworkChestId(chestId));
        }

        [Test]
        public void Ids_OverFixedStringCapacity_AreRejected()
        {
            Assert.IsFalse(ChestInteractable.IsValidNetworkChestId(new string('a', 65)));
        }

        [Test]
        public void ClientPresentation_UsesOnlyMatchingServerState()
        {
            _go = new GameObject("Chest_Supply_4_3_0");
            ChestInteractable chest = _go.AddComponent<ChestInteractable>();
            NetRoleKind previous = NetRole.Current;
            try
            {
                NetRole.Set(NetRoleKind.Client);
                Assert.IsFalse(chest.ApplyReplicatedState("Chest_Supply_4_3_1", true, true, 0));
                Assert.IsFalse(chest.ApplyReplicatedState(chest.ChestId, true, false, -1));
                Assert.IsTrue(chest.ApplyReplicatedState(chest.ChestId, false, false, 0));
                Assert.AreEqual("Open chest", chest.PromptText);
                Assert.IsTrue(chest.ApplyReplicatedState(chest.ChestId, true, true, 2));
                Assert.IsTrue(chest.Opened);
                Assert.AreEqual(2, chest.RemainingUnits);
                Assert.AreEqual("Open chest", chest.PromptText);
                Assert.IsTrue(chest.ApplyReplicatedState(chest.ChestId, true, true, 0));
                Assert.AreEqual("Chest (empty)", chest.PromptText);
            }
            finally { NetRole.Set(previous); }
        }

        [Test]
        public void NetworkInteraction_IsEnabledOnlyForOwner()
        {
            Assert.IsTrue(Emberdelve.Net.NetPlayerRig.ShouldEnableOwnerInteractor(Emberdelve.Net.NetPlayerRole.Owner));
            Assert.IsFalse(Emberdelve.Net.NetPlayerRig.ShouldEnableOwnerInteractor(Emberdelve.Net.NetPlayerRole.Observer));
        }

        [Test]
        public void ClientPresentation_BuildsStableAuthoredIdsOnceAndAppliesLateStateWithoutLoot()
        {
            _layout = ScriptableObject.CreateInstance<DungeonLayoutData>();
            _lootTable = ScriptableObject.CreateInstance<LootTable>();
            _layout.levelName = "network presentation test";
            _layout.chestSpawns.Add(new ChestSpawnSpec { cell = new Vector2Int(4, 3), variant = ChestVariant.Supply, seed = 17, lootTable = _lootTable });
            _layout.chestSpawns.Add(new ChestSpawnSpec { cell = new Vector2Int(8, 5), variant = ChestVariant.Ember, seed = 21, lootTable = _lootTable });

            NetRoleKind previous = NetRole.Current;
            try
            {
                NetRole.Set(NetRoleKind.Client);
                ChestSpawner first = ChestSpawner.EnsureClientPresentation(_layout);
                _presentationRoot = first.gameObject;
                Assert.AreEqual(2, first.Count);
                Assert.AreEqual("Chest_Supply_4_3_0", first.Chests[0].ChestId);
                Assert.AreEqual("Chest_Ember_8_5_1", first.Chests[1].ChestId);
                Assert.IsTrue(first.Chests[0].IsPresentationOnly);
                Assert.IsTrue(first.Chests[1].IsPresentationOnly);

                ChestSpawner repeated = ChestSpawner.EnsureClientPresentation(_layout);
                Assert.AreSame(first, repeated);
                Assert.AreEqual(2, Object.FindObjectsByType<ChestInteractable>(FindObjectsSortMode.None).Length);

                // The authoritative snapshot can arrive after the view has been built; applying it updates the
                // client prompt/state only. A presentation chest has no local loot objects to grant.
                ChestInteractable lateReplica = first.Chests[1];
                Assert.IsTrue(lateReplica.ApplyReplicatedState(lateReplica.ChestId, true, true, 3));
                Assert.IsTrue(lateReplica.IsRolled);
                Assert.IsTrue(lateReplica.Opened);
                Assert.AreEqual(3, lateReplica.RemainingUnits);
                Assert.AreEqual(0, lateReplica.Remaining.Count);

                Assert.IsFalse(lateReplica.Open(null));
                Assert.AreEqual(0, lateReplica.Remaining.Count, "client presentation never rolls or grants local loot");
            }
            finally { NetRole.Set(previous); }
        }

        [Test]
        public void ClientPresentation_LayoutChangeRemovesOnlyStalePresentationRoot()
        {
            _layout = ScriptableObject.CreateInstance<DungeonLayoutData>();
            _secondLayout = ScriptableObject.CreateInstance<DungeonLayoutData>();
            _lootTable = ScriptableObject.CreateInstance<LootTable>();
            _secondLootTable = ScriptableObject.CreateInstance<LootTable>();
            _layout.levelName = "first network layout";
            _secondLayout.levelName = "second network layout";
            _layout.chestSpawns.Add(new ChestSpawnSpec { cell = new Vector2Int(4, 3), variant = ChestVariant.Supply, lootTable = _lootTable });
            _secondLayout.chestSpawns.Add(new ChestSpawnSpec { cell = new Vector2Int(9, 6), variant = ChestVariant.Ember, lootTable = _secondLootTable });

            _authoredChestObject = new GameObject("AuthoredChest");
            ChestInteractable authoredChest = _authoredChestObject.AddComponent<ChestInteractable>();
            _serverSpawnerObject = new GameObject("ServerChestSpawner");
            ChestSpawner serverSpawner = _serverSpawnerObject.AddComponent<ChestSpawner>();

            NetRoleKind previous = NetRole.Current;
            try
            {
                NetRole.Set(NetRoleKind.Client);
                ChestSpawner first = ChestSpawner.EnsureClientPresentation(_layout);
                _presentationRoot = first.gameObject;
                Assert.AreEqual(1, first.Count);
                Assert.AreEqual("Chest_Supply_4_3_0", first.Chests[0].ChestId);

                ChestSpawner second = ChestSpawner.EnsureClientPresentation(_secondLayout);
                _secondPresentationRoot = second.gameObject;

                Assert.AreNotSame(first, second);
                Assert.IsTrue(first == null || !first.gameObject.activeInHierarchy,
                    "the stale presentation root is removed or disabled before the next chest snapshot is applied");
                Assert.AreEqual(1, second.Count);
                Assert.AreEqual("Chest_Ember_9_6_0", second.Chests[0].ChestId);
                Assert.IsNotNull(authoredChest);
                Assert.IsTrue(_authoredChestObject.activeInHierarchy, "authored chest objects are not cleaned up");
                Assert.IsNotNull(serverSpawner);
                Assert.IsTrue(_serverSpawnerObject.activeInHierarchy, "server spawners are not cleaned up");

                ChestInteractable[] sceneChests = Object.FindObjectsByType<ChestInteractable>(FindObjectsSortMode.None);
                Assert.AreEqual(2, sceneChests.Length, "only the new presentation chest and authored chest remain");
            }
            finally { NetRole.Set(previous); }
        }

        [Test]
        public void DelayedChestRefusal_CannotCancelNewerHoldLease()
        {
            Assert.IsFalse(Emberdelve.Net.NetInventoryClient.ShouldCancelForChestResult("Chest-B", 8, "Chest-A", 7));
            Assert.IsFalse(Emberdelve.Net.NetInventoryClient.ShouldCancelForChestResult("Chest-A", 8, "Chest-A", 7));
            Assert.IsTrue(Emberdelve.Net.NetInventoryClient.ShouldCancelForChestResult("Chest-A", 8, "Chest-A", 8));
            Assert.IsFalse(Emberdelve.Net.NetInventoryClient.ShouldCancelForChestResult("Chest-A", 0, "Chest-A", 0));
            Assert.IsTrue(Emberdelve.Net.NetInventoryClient.MatchesPendingChestCompletion("Chest-A", 7, "Chest-A", 7));
            Assert.IsFalse(Emberdelve.Net.NetInventoryClient.ShouldCancelForChestResult("Chest-B", 8, "Chest-A", 7),
                "a completion response for the old lease is acknowledged without canceling the newer active lease");
            Assert.IsFalse(Emberdelve.Net.NetInventoryClient.MatchesPendingChestCompletion("Chest-B", 8, "Chest-A", 7),
                "a late old result is not mistaken for the newer pending completion");
        }

        [Test]
        public void LostCompletionResponse_TimesOutAndAllowsANewerLease()
        {
            Assert.IsFalse(Emberdelve.Net.NetInventoryClient.IsChestCompletionTimedOut(20.0, 27.99,
                Emberdelve.Net.NetInventoryClient.ChestCompletionTimeoutSeconds));
            Assert.IsTrue(Emberdelve.Net.NetInventoryClient.IsChestCompletionTimedOut(20.0, 28.0,
                Emberdelve.Net.NetInventoryClient.ChestCompletionTimeoutSeconds));
            Assert.IsFalse(Emberdelve.Net.NetInventoryClient.IsChestCompletionTimedOut(20.0, 19.0,
                Emberdelve.Net.NetInventoryClient.ChestCompletionTimeoutSeconds), "clock rollback cannot cause an immediate timeout");
            Assert.IsFalse(Emberdelve.Net.NetInventoryClient.MatchesPendingChestCompletion("Chest-A", 12, "Chest-A", 11),
                "after timeout, an old ack cannot clear a new lease's pending token");
            Assert.IsTrue(Emberdelve.Net.NetInventoryClient.ShouldCancelForChestResult("Chest-B", 13, "Chest-B", 13),
                "the new lease remains cancellable by its own correlated rejection");
        }

        [Test]
        public void ServerHoldLease_UsesServerDurationAndChestRevision()
        {
            Assert.IsTrue(Emberdelve.Net.Rules.ChestHoldWindow.TryStart("Chest-Ember-12-7-2", 10.0, 1.4f, 3, out var lease));
            Assert.AreEqual(Emberdelve.Net.Rules.ChestHoldVerdict.TooEarly, lease.Evaluate("Chest-Ember-12-7-2", 3, 11.39));
            Assert.AreEqual(Emberdelve.Net.Rules.ChestHoldVerdict.Valid, lease.Evaluate("Chest-Ember-12-7-2", 3, 11.4));
            Assert.AreEqual(Emberdelve.Net.Rules.ChestHoldVerdict.WrongChest, lease.Evaluate("Chest-Supply-12-7-2", 3, 11.4));
            Assert.AreEqual(Emberdelve.Net.Rules.ChestHoldVerdict.ChestChanged, lease.Evaluate("Chest-Ember-12-7-2", 4, 11.4));
            Assert.AreEqual(Emberdelve.Net.Rules.ChestHoldVerdict.Expired, lease.Evaluate("Chest-Ember-12-7-2", 3, 13.41));
            Assert.IsFalse(Emberdelve.Net.Rules.ChestHoldWindow.TryStart("Chest", 0, 0, 0, out _));
            Assert.AreEqual(Emberdelve.Net.Rules.ChestHoldVerdict.TooEarly, lease.Evaluate("Chest-Ember-12-7-2", 3, 11.2),
                "an early Complete remains pending until the authoritative clock reaches the server duration");
            Assert.IsTrue(Emberdelve.Net.Rules.ChestHoldWindow.IsHeartbeatFresh(10.0, 10.8, 1.25),
                "reasonable RPC delay keeps the lease alive");
            Assert.IsFalse(Emberdelve.Net.Rules.ChestHoldWindow.IsHeartbeatFresh(10.0, 11.26, 1.25),
                "a stopped client cannot complete after the heartbeat lease expires");
        }
    }
}
