using System.Linq;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Items;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Emberdelve.Combat.Tests
{
    public sealed class HealingStackCapacityTests
    {
        private GameObject _root;
        private Inventory _bag;
        private ItemCatalog _catalog;
        private NetRoleKind _previousRole;

        [SetUp] public void SetUp()
        {
            _previousRole = NetRole.Current;
            NetRole.ResetToOffline();
            _root = new GameObject("HealingStackCapacity");
            _root.SetActive(false);
            _bag = _root.AddComponent<Inventory>();
            _catalog = AssetDatabase.LoadAssetAtPath<ItemCatalog>("Assets/Emberdelve/Data/EmberdelveItemCatalog.asset");
            _bag.SetCatalog(_catalog);
        }

        [TearDown] public void TearDown()
        {
            if (_root != null) Object.DestroyImmediate(_root);
            NetRole.Set(_previousRole);
        }

        [TestCase("blood_draught")]
        [TestCase("linen_bandage")]
        [TestCase("smoldering_tonic")]
        public void SixthHealingItemUsesAnotherCellAndCannotMergePastFive(string itemId)
        {
            var item = _catalog.Items.Single(x => x != null && x.itemId == itemId);
            Assert.That(_bag.TryAdd(item, 6, out var reason), Is.True, reason);
            Assert.That(_bag.Slots.Select(x => x.Count), Is.EqualTo(new[] { 5, 1 }));
            Assert.That(_bag.Slots[0].Origin, Is.Not.EqualTo(_bag.Slots[1].Origin));
            string before = _bag.ToContainerSnapshot().ToJson();
            Assert.That(_bag.TryMergeStacks(1, 0, out reason), Is.False);
            Assert.That(_bag.ToContainerSnapshot().ToJson(), Is.EqualTo(before), "a full healing stack must retain the sixth unit in its original cell");
            Assert.That(_bag.TotalUnits, Is.EqualTo(6));
        }

        [TestCase("blood_draught")]
        [TestCase("linen_bandage")]
        [TestCase("smoldering_tonic")]
        public void FiveCanBeSavedButOvercapSnapshotCannotReplaceInventory(string itemId)
        {
            var item = _catalog.Items.Single(x => x != null && x.itemId == itemId);
            Assert.That(_bag.TryAddAt(item, 5, new Vector2Int(2, 1), out var reason), Is.True, reason);
            var valid = _bag.ToContainerSnapshot();
            Assert.That(_bag.TryRestore(valid, out reason), Is.True, reason);
            Assert.That(_bag.Slots.Single().Count, Is.EqualTo(5));
            Assert.That(_bag.Slots.Single().Origin, Is.EqualTo(new Vector2Int(2, 1)));
            string before = _bag.ToContainerSnapshot().ToJson();
            var overcap = _bag.ToContainerSnapshot();
            overcap.entries[0].item.quantity = 6;
            overcap.Seal();
            Assert.That(_bag.TryRestore(overcap, out reason), Is.False);
            Assert.That(_bag.ToContainerSnapshot().ToJson(), Is.EqualTo(before), "a valid checksum does not authorize excess stack capacity or partial restore");
        }
    }
}
