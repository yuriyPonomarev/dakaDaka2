using System;
using System.Linq;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Items;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Emberdelve.Combat.Tests
{
    public sealed class StackDropTransactionTests
    {
        GameObject _root;
        Inventory _bag;
        ItemData _item;
        NetRoleKind _priorRole;
        InventorySlot _source;

        [SetUp]
        public void SetUp()
        {
            _priorRole = NetRole.Current;
            NetRole.ResetToOffline();
            _root = new GameObject("StackDropTransaction");
            _root.SetActive(false);
            _bag = _root.AddComponent<Inventory>();
            var catalog = AssetDatabase.LoadAssetAtPath<ItemCatalog>("Assets/Emberdelve/Data/EmberdelveItemCatalog.asset");
            _bag.SetCatalog(catalog);
            _item = catalog.Items.First(x => x != null && !x.IsEquippable && x.EffectiveMaxStack >= 3 && x.EffectiveFootprint == Vector2Int.one);
            Assert.That(_bag.TryAddAt(_item, 1, new Vector2Int(0,0), out var reason), Is.True, reason);
            Assert.That(_bag.TryAddAt(_item, 3, new Vector2Int(3,0), out reason), Is.True, reason);
            Assert.That(_bag.TryAddAt(_item, 1, new Vector2Int(6,0), out reason), Is.True, reason);
            _source = _bag.Slots[1];
        }

        [TearDown]
        public void TearDown()
        {
            if (_root != null) UnityEngine.Object.DestroyImmediate(_root);
            NetRole.Set(_priorRole);
        }

        [TestCase(1)]
        [TestCase(3)]
        public void RefusalRestoresExactStackOrderCountersAndRevisionWithoutNotifications(int amount)
        {
            string before = JsonUtility.ToJson(_bag.ToContainerSnapshot());
            int units = _bag.TotalUnits, value = _bag.CarriedValue, collected = _bag.LifetimeCollected, recovered = _bag.RecoveredValue;
            int notifications = 0;
            Action<int> observe = _ => notifications++;
            GameEvents.InventoryChanged += observe;
            try
            {
                Assert.That(_bag.TryBeginDropMutation(out var lease, out var reason), Is.True, reason);
                using (lease)
                {
                    Assert.That(_bag.TryDetachStackForDrop(lease, _source.StackId, amount, out var state, out reason), Is.True, reason);
                    Assert.That(_bag.TotalUnits, Is.EqualTo(units - amount));
                    Assert.That(_bag.TryAdd(_item, 1), Is.False, "reentrant mutation cannot fill the reserved source cell");
                    Assert.That(_bag.TryBeginDropMutation(out _, out _), Is.False);
                    Assert.That(_bag.TryRestoreStackAfterDropRefusal(lease, state, out reason), Is.True, reason);
                    Assert.That(_bag.TryRestoreStackAfterDropRefusal(lease, state, out _), Is.False, "rollback cannot duplicate units");
                }
                Assert.That(JsonUtility.ToJson(_bag.ToContainerSnapshot()), Is.EqualTo(before));
                Assert.That(_bag.Slots[1].StackId, Is.EqualTo(_source.StackId));
                Assert.That(_bag.TotalUnits, Is.EqualTo(units));
                Assert.That(_bag.CarriedValue, Is.EqualTo(value));
                Assert.That(_bag.LifetimeCollected, Is.EqualTo(collected));
                Assert.That(_bag.RecoveredValue, Is.EqualTo(recovered));
                Assert.That(notifications, Is.Zero);
            }
            finally { GameEvents.InventoryChanged -= observe; }
        }

        [TestCase(1)]
        [TestCase(3)]
        public void SuccessfulDropCommitsOnceAndCannotBeRolledBackAfterPublication(int amount)
        {
            ulong revision = _bag.Revision;
            Assert.That(_bag.TryBeginDropMutation(out var lease, out var reason), Is.True, reason);
            using (lease)
            {
                Assert.That(_bag.TryDetachStackForDrop(lease, _source.StackId, amount, out var state, out reason), Is.True, reason);
                Assert.That(_bag.CommitDroppedInstance(lease, revision, out reason), Is.True, reason);
                Assert.That(_bag.CommitDroppedInstance(lease, revision, out _), Is.False);
                Assert.That(_bag.TryRestoreStackAfterDropRefusal(lease, state, out _), Is.False);
            }
            Assert.That(_bag.Revision, Is.EqualTo(revision + 1));
            Assert.That(_bag.TotalUnits, Is.EqualTo(5 - amount));
            Assert.That(_bag.TryGetStack(_source.StackId, out var remaining), Is.EqualTo(amount < 3));
            if (amount < 3) { Assert.That(remaining.Count, Is.EqualTo(3 - amount)); Assert.That(remaining.Origin, Is.EqualTo(_source.Origin)); }
        }

        [TestCase(0)]
        [TestCase(-1)]
        [TestCase(4)]
        public void InvalidAmountLeavesInventoryUntouched(int amount)
        {
            string before = JsonUtility.ToJson(_bag.ToContainerSnapshot());
            Assert.That(_bag.TryBeginDropMutation(out var lease, out var reason), Is.True, reason);
            using (lease) Assert.That(_bag.TryDetachStackForDrop(lease, _source.StackId, amount, out _, out _), Is.False);
            Assert.That(JsonUtility.ToJson(_bag.ToContainerSnapshot()), Is.EqualTo(before));
        }
    }
}
