using System;
using System.Collections.Generic;
using System.Reflection;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Interaction;
using Emberdelve.Items;
using NUnit.Framework;
using UnityEngine;

namespace Emberdelve.Tests.Combat
{
    public sealed class LootReentrancyTests
    {
        private sealed class ExpectedObserverExceptionHandler : ILogHandler
        {
            private readonly ILogHandler _inner;
            public int SuppressedCount { get; private set; }
            public ExpectedObserverExceptionHandler(ILogHandler inner) { _inner = inner; }
            public void LogException(Exception exception, UnityEngine.Object context)
            {
                if (exception is InvalidOperationException && exception.Message == "observer failure")
                {
                    SuppressedCount++;
                    return;
                }
                _inner.LogException(exception, context);
            }
            public void LogFormat(LogType logType, UnityEngine.Object context, string format, params object[] args) =>
                _inner.LogFormat(logType, context, format, args);
        }

        private sealed class Actor : IInteractionActor
        {
            public int PlayerId => 1;
            public int PartyId => 0;
            public bool IsAlive => true;
            public Vector3 InteractionOrigin => Vector3.zero;
            public Inventory Inventory => null;
        }

        public sealed class Host : MonoBehaviour, ICountedLootHost
        {
            public Action Reenter;
            public int Calls;
            public bool Throw;
            public bool Notify;
            public int Accepted;
            public bool CollectLoot(IInteractionActor actor, ItemData item, int amount) => false;
            public void SpawnLoot(ItemData item, int amount, Vector3 position) { }
            public int CollectLootCounted(IInteractionActor actor, ItemData item, int amount)
            {
                Calls++;
                Reenter?.Invoke();
                if (Throw) throw new InvalidOperationException("before commit");
                if (Notify) GameEvents.RaiseInventoryChanged(actor.PlayerId);
                return Accepted;
            }
        }

        private GameObject _go;
        private ItemData _item;
        private LootTable _table;
        private Host _host;
        private readonly Actor _actor = new Actor();

        [SetUp]
        public void SetUp()
        {
            _go = new GameObject("test loot");
            _host = _go.AddComponent<Host>();
            _item = ScriptableObject.CreateInstance<ItemData>();
            _item.itemId = "reentrancy_test";
            _table = ScriptableObject.CreateInstance<LootTable>();
        }

        [TearDown]
        public void TearDown()
        {
            UnityEngine.Object.DestroyImmediate(_go);
            UnityEngine.Object.DestroyImmediate(_item);
            UnityEngine.Object.DestroyImmediate(_table);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Chest_BlocksReentry_AndRetainsUncommittedUnits(bool throws)
        {
            var chest = _go.AddComponent<ChestInteractable>();
            chest.Configure(ChestVariant.Supply, _table, 1, _host);
            typeof(ChestInteractable).GetField("_rolled", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(chest, true);
            var drops = (List<LootDrop>)typeof(ChestInteractable).GetField("_remaining", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(chest);
            drops.Add(new LootDrop(_item, 2));
            bool nested = true;
            _host.Reenter = () => nested = chest.Open(_actor);
            _host.Accepted = 0;
            _host.Throw = throws;
            if (throws) Assert.Throws<InvalidOperationException>(() => chest.Open(_actor));
            else Assert.IsTrue(chest.Open(_actor));
            Assert.IsFalse(nested);
            Assert.AreEqual(1, _host.Calls);
            Assert.AreEqual(2, chest.RemainingUnits);
            _host.Throw = false;
            _host.Reenter = null;
            _host.Accepted = 1;
            Assert.IsTrue(chest.Open(_actor));
            Assert.AreEqual(1, chest.RemainingUnits);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Pickup_BlocksReentry_AndRetainsUncommittedUnits(bool throws)
        {
            var pickup = _go.AddComponent<LootPickup>();
            pickup.Configure(_item, 2, _host);
            _host.Reenter = () => pickup.OnInteractInstant(_actor);
            _host.Throw = throws;
            if (throws) Assert.Throws<InvalidOperationException>(() => pickup.OnInteractInstant(_actor));
            else pickup.OnInteractInstant(_actor);
            Assert.AreEqual(1, _host.Calls);
            Assert.AreEqual(2, pickup.Amount);
            _host.Throw = false;
            _host.Reenter = null;
            _host.Accepted = 1;
            pickup.OnInteractInstant(_actor);
            Assert.AreEqual(1, pickup.Amount);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ObserverException_DoesNotAbortRecordedTransfer(bool chestSource)
        {
            var chest = chestSource ? _go.AddComponent<ChestInteractable>() : null;
            var pickup = chestSource ? null : _go.AddComponent<LootPickup>();
            if (chest != null)
            {
                chest.Configure(ChestVariant.Supply, _table, 1, _host);
                typeof(ChestInteractable).GetField("_rolled", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(chest, true);
                var drops = (List<LootDrop>)typeof(ChestInteractable).GetField("_remaining", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(chest);
                drops.Add(new LootDrop(_item, 2));
            }
            else pickup.Configure(_item, 2, _host);
            _host.Accepted = 1;
            _host.Notify = true;
            int observed = -1;
            Action<int> listener = _ =>
            {
                observed = chest != null ? chest.RemainingUnits : pickup.Amount;
                throw new InvalidOperationException("observer failure");
            };
            int afterThrow = 0;
            Action<int> secondListener = _ => afterThrow++;
            GameEvents.InventoryChanged += listener;
            GameEvents.InventoryChanged += secondListener;
            ILogHandler originalHandler = Debug.unityLogger.logHandler;
            var expectedHandler = new ExpectedObserverExceptionHandler(originalHandler);
            try
            {
                // Suppress only this observer's expected exception; other logs still reach Unity's handler.
                Debug.unityLogger.logHandler = expectedHandler;
                if (chest != null) Assert.IsTrue(chest.Open(_actor));
                else pickup.OnInteractInstant(_actor);
                Assert.AreEqual(1, observed, "observer sees source after accepted quantity was removed");
                Assert.AreEqual(1, chest != null ? chest.RemainingUnits : pickup.Amount);
                Assert.AreEqual(1, _host.Calls);
                Assert.AreEqual(1, afterThrow);
                Assert.AreEqual(1, expectedHandler.SuppressedCount, "the dispatcher logged exactly the expected observer exception");
            }
            finally
            {
                Debug.unityLogger.logHandler = originalHandler;
                GameEvents.InventoryChanged -= listener;
                GameEvents.InventoryChanged -= secondListener;
            }
        }

        [Test]
        public void ClientOnly_CannotSubmitToLocalInventoryAuthority()
        {
            var ctor = typeof(LocalInventoryAuthority).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic,
                null, new[] { typeof(Emberdelve.Player.PlayerRig), typeof(Inventory), typeof(Equipment) }, null);
            var authority = (LocalInventoryAuthority)ctor.Invoke(new object[] { null, null, null });
            NetRoleKind previous = NetRole.Current;
            try
            {
                NetRole.Set(NetRoleKind.Client);
                var result = authority.Submit(null, default);
                Assert.IsFalse(result.Accepted);
                Assert.IsFalse(result.Changed);
                StringAssert.Contains("server", result.Reason);
            }
            finally { NetRole.Set(previous); }
        }
    }
}
