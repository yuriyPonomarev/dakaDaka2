using System;
using System.Linq;
using System.Reflection;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Items;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Emberdelve.Combat.Tests
{
    public sealed class StackSplitTests
    {
        GameObject _root;
        Inventory _bag;
        InventorySlot _source;
        NetRoleKind _previousRole;
        ItemCatalog _catalog;
        [SetUp] public void SetUp()
        {
            _previousRole = NetRole.Current; NetRole.ResetToOffline();
            _root = new GameObject("StackSplit"); _root.SetActive(false);
            _bag = _root.AddComponent<Inventory>();
            _catalog = AssetDatabase.LoadAssetAtPath<ItemCatalog>("Assets/Emberdelve/Data/EmberdelveItemCatalog.asset");
            _bag.SetCatalog(_catalog);
            var item = _catalog.Items.First(x => x != null && !x.IsEquippable && x.EffectiveMaxStack >= 3 && x.EffectiveFootprint == Vector2Int.one);
            Assert.That(_bag.TryAddAt(item, 3, new Vector2Int(2,1), out var reason), Is.True, reason);
            Assert.That(_bag.TryAddAt(item, 1, new Vector2Int(4,2), out reason), Is.True, reason);
            _source = _bag.Slots[0];
        }
        [TearDown] public void TearDown()
        {
            if (_root != null) UnityEngine.Object.DestroyImmediate(_root);
            NetRole.Set(_previousRole);
        }
        [TestCase(1)] [TestCase(2)]
        public void SplitRetainsSourceIdentityCreatesDistinctDestinationAndConservesAllCounters(int amount)
        {
            ulong revision = _bag.Revision;
            int units = _bag.TotalUnits, value = _bag.CarriedValue, collected = _bag.LifetimeCollected, recovered = _bag.RecoveredValue;
            int notifications = 0;
            Action<int> observe = _ => { notifications++; Assert.That(_bag.Slots.Sum(x => x.Count), Is.EqualTo(units)); };
            GameEvents.InventoryChanged += observe;
            try
            {
                Assert.That(_bag.TrySplitStack(_source.StackId, amount, new Vector2Int(8,3), out var reason), Is.True, reason);
                Assert.That(_bag.TryGetStack(_source.StackId, out var remaining), Is.True);
                Assert.That(remaining.Count, Is.EqualTo(3-amount)); Assert.That(remaining.Origin, Is.EqualTo(_source.Origin));
                var split = _bag.Slots.Single(x => x.Origin == new Vector2Int(8,3));
                Assert.That(split.StackId, Is.Not.Zero.And.Not.EqualTo(_source.StackId));
                Assert.That(split.Item, Is.SameAs(_source.Item)); Assert.That(split.Instance, Is.Null);
                Assert.That(split.Count, Is.EqualTo(amount)); Assert.That(_bag.Revision, Is.EqualTo(revision+1));
                Assert.That(_bag.TotalUnits, Is.EqualTo(units)); Assert.That(_bag.CarriedValue, Is.EqualTo(value));
                Assert.That(_bag.LifetimeCollected, Is.EqualTo(collected)); Assert.That(_bag.RecoveredValue, Is.EqualTo(recovered));
                Assert.That(notifications, Is.EqualTo(1));
                Assert.That(_bag.TryMergeStacks(split.Index, remaining.Index, out reason), Is.True, reason);
                Assert.That(_bag.TryGetStack(split.StackId, out _), Is.False);
                Assert.That(_bag.TryGetStack(_source.StackId, out var merged), Is.True);
                Assert.That(merged.Count, Is.EqualTo(3)); Assert.That(_bag.TotalUnits, Is.EqualTo(units));
            }
            finally { GameEvents.InventoryChanged -= observe; }
        }
        [TestCase(0,8,3)] [TestCase(-1,8,3)] [TestCase(3,8,3)] [TestCase(4,8,3)]
        [TestCase(1,2,1)] [TestCase(1,4,2)] [TestCase(1,-1,0)] [TestCase(1,10,0)] [TestCase(1,0,5)] [TestCase(1,int.MaxValue,0)]
        public void InvalidAmountOrDestinationLeavesCanonicalSnapshotAndNextIdentityUntouched(int amount, int x, int y)
        {
            string before = JsonUtility.ToJson(_bag.ToContainerSnapshot());
            var field = typeof(Inventory).GetField("_nextStackId", BindingFlags.Instance|BindingFlags.NonPublic);
            object next = field.GetValue(_bag);
            Assert.That(_bag.TrySplitStack(_source.StackId, amount, new Vector2Int(x,y), out _), Is.False);
            Assert.That(JsonUtility.ToJson(_bag.ToContainerSnapshot()), Is.EqualTo(before));
            Assert.That(field.GetValue(_bag), Is.EqualTo(next));
        }
        [Test] public void DropReservationAndExhaustedIdentitiesRefuseSplitWithoutMutation()
        {
            string before = JsonUtility.ToJson(_bag.ToContainerSnapshot());
            Assert.That(_bag.TryBeginDropMutation(out var lease, out var reason), Is.True, reason);
            using(lease) Assert.That(_bag.TrySplitStack(_source.StackId, 1, new Vector2Int(8,3), out _), Is.False);
            typeof(Inventory).GetField("_stackIdsExhausted", BindingFlags.Instance|BindingFlags.NonPublic).SetValue(_bag,true);
            Assert.That(_bag.TrySplitStack(_source.StackId, 1, new Vector2Int(8,3), out _), Is.False);
            Assert.That(JsonUtility.ToJson(_bag.ToContainerSnapshot()), Is.EqualTo(before));
        }
        [Test] public void GearAndUnknownStackCannotBeSplit()
        {
            _bag.SetIssuer(ItemIdentityIssuer.CreateLocalTrusted());
            var gear = _catalog.Items.First(x => x != null && x.IsEquippable);
            Assert.That(_catalog.TryMintCommon(_bag.Issuer, gear, out var instance, out var reason), Is.True, reason);
            Assert.That(_bag.TryAddInstance(instance, out reason), Is.True, reason);
            var slot = _bag.Slots.Single(x => x.Instance == instance);
            string before = JsonUtility.ToJson(_bag.ToContainerSnapshot());
            Assert.That(_bag.TrySplitStack(slot.StackId, 1, new Vector2Int(8,3), out _), Is.False);
            Assert.That(_bag.TrySplitStack(ulong.MaxValue, 1, new Vector2Int(8,3), out _), Is.False);
            Assert.That(JsonUtility.ToJson(_bag.ToContainerSnapshot()), Is.EqualTo(before));
        }
    }
}
