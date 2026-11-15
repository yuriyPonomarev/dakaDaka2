using System.Linq;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Items;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Emberdelve.Combat.Tests
{
    public sealed class PhysicalQuickBeltTests
    {
        private GameObject _host;
        private Inventory _bag;
        private ItemData _potion;
        private ItemCatalog _catalog;
        private NetRoleKind _previousRole;
        [SetUp] public void Setup()
        {
            _previousRole = NetRole.Current; NetRole.ResetToOffline();
            _host = new GameObject("PhysicalQuickBeltTests");
            _bag = _host.AddComponent<Inventory>();
            _catalog = AssetDatabase.LoadAssetAtPath<ItemCatalog>("Assets/Emberdelve/Data/EmberdelveItemCatalog.asset");
            _bag.SetCatalog(_catalog); _bag.ConfigureGrid(2, 1);
            Assert.That(_catalog.TryResolveItem("blood_draught", out _potion, out var reason), Is.True, reason);
        }
        [TearDown] public void Cleanup() { Object.DestroyImmediate(_host); NetRole.Set(_previousRole); }
        [Test] public void BeltFreesBagAndFullBagReturnRefusesWithoutMutation()
        {
            Assert.That(_bag.TryAddAt(_potion, 5, Vector2Int.zero, out var reason), Is.True, reason);
            ulong id = _bag.Slots[0].StackId;
            Assert.That(_bag.TryMove(0, _bag.BeltCell(5), out reason), Is.True, reason);
            Assert.That(_bag.UsedCells, Is.Zero);
            Assert.That(_bag.Hotbar.StackIdAt(5), Is.EqualTo(id));
            Assert.That(_bag.TryAddAt(_potion, 5, Vector2Int.zero, out reason), Is.True, reason);
            Assert.That(_bag.TryAddAt(_potion, 5, Vector2Int.right, out reason), Is.True, reason);
            string before = _bag.ToContainerSnapshot().ToJson(); ulong revision = _bag.Revision;
            Assert.That(_bag.TryMove(_bag.IndexOfStack(id), Vector2Int.zero, out _), Is.False);
            Assert.That(_bag.ToContainerSnapshot().ToJson(), Is.EqualTo(before));
            Assert.That(_bag.Revision, Is.EqualTo(revision));
            Assert.That(_bag.TotalUnits, Is.EqualTo(15));
        }
        [Test] public void BeltRoundTripPreservesPositionAndQuantityWithoutAutofill()
        {
            Assert.That(_bag.TryAddAt(_potion, 5, _bag.BeltCell(4), out var reason), Is.True, reason);
            Assert.That(_bag.TryAddAt(_potion, 2, Vector2Int.zero, out reason), Is.True, reason);
            var saved = _bag.ToContainerSnapshot();
            Assert.That(ContainerSnapshot.TryFromJson(saved.ToJson(), out var decoded, out reason), Is.True, reason);
            Assert.That(_bag.TryRestore(decoded, out reason), Is.True, reason);
            Assert.That(_bag.TryGetBeltStack(4, out var utility), Is.True);
            Assert.That(utility.Count, Is.EqualTo(5));
            Assert.That(_bag.Hotbar.StackIdAt(0), Is.Zero);
            Assert.That(_bag.TotalUnits, Is.EqualTo(7));
            Assert.That(_bag.UsedCells, Is.EqualTo(1));
            Assert.That(_bag.ToContainerSnapshot().ToJson(), Is.EqualTo(saved.ToJson()));
        }
        [Test] public void InvalidBeltItemAndOversizeStackAreAtomicRefusals()
        {
            string before = _bag.ToContainerSnapshot().ToJson();
            Assert.That(_bag.TryAddAt(_potion, 6, _bag.BeltCell(0), out _), Is.False);
            var armor = _catalog.Items.First(i => i != null && i.IsEquippable && !QuickBeltRules.CanStore(i));
            Assert.That(_bag.TryAddAt(armor, 1, _bag.BeltCell(0), out _), Is.False);
            Assert.That(_bag.ToContainerSnapshot().ToJson(), Is.EqualTo(before));
            var forged = new ContainerSnapshot { kind = ContainerKinds.Bag, containerId = "bag", width = 2, height = 1,
                entries = new[] { new ContainerEntry { x = 2, y = 0, item = new ItemSnapshot { itemId = _potion.itemId, quantity = 5 } } } };
            forged.Seal();
            forged.entries = new[] { forged.entries[0], forged.entries[0].Clone() }; forged.Seal();
            Assert.That(_bag.TryRestore(forged, out _), Is.False);
            Assert.That(_bag.ToContainerSnapshot().ToJson(), Is.EqualTo(before));
        }
        [Test] public void ExactBeltPlacementDoesNotFillAnotherBagStack()
        {
            Assert.That(_bag.TryAddAt(_potion, 2, Vector2Int.zero, out var reason), Is.True, reason);
            ulong bagId = _bag.Slots[0].StackId;
            Assert.That(_bag.TryAddAt(_potion, 3, _bag.BeltCell(2), out reason), Is.True, reason);
            Assert.That(_bag.TryGetStack(bagId, out var bagStack), Is.True);
            Assert.That(bagStack.Count, Is.EqualTo(2));
            Assert.That(_bag.TryGetBeltStack(2, out var belt), Is.True);
            Assert.That(belt.Count, Is.EqualTo(3));
        }
        [Test] public void MergeIntoBeltCapsAtFiveAndPreservesRemainder()
        {
            Assert.That(_bag.TryAddAt(_potion, 4, _bag.BeltCell(0), out var reason), Is.True, reason);
            Assert.That(_bag.TryAddAt(_potion, 3, Vector2Int.zero, out reason), Is.True, reason);
            ulong source = _bag.Slots.First(s => s.Origin == Vector2Int.zero).StackId;
            ulong target = _bag.Hotbar.StackIdAt(0);
            Assert.That(_bag.TryMergeStacks(_bag.IndexOfStack(source), _bag.IndexOfStack(target), out reason), Is.True, reason);
            Assert.That(_bag.TryGetBeltStack(0, out var belt), Is.True);
            Assert.That(belt.Count, Is.EqualTo(5));
            Assert.That(_bag.TryGetStack(source, out var left), Is.True);
            Assert.That(left.Count, Is.EqualTo(2));
            Assert.That(_bag.TotalUnits, Is.EqualTo(7));
        }
        [Test] public void LegacySchemaCannotSmugglePhysicalBeltCoordinates()
        {
            var old = new ContainerSnapshot { schemaVersion = 5, kind = ContainerKinds.Bag, containerId = "bag", width = 2, height = 1,
                entries = new[] { new ContainerEntry { x = 2, y = 0, item = new ItemSnapshot { itemId = _potion.itemId, quantity = 1 } } } };
            old.Seal();
            Assert.That(ContainerSnapshot.TryFromJson(old.ToJson(), out _, out _), Is.False);
            old.entries[0].x = 0; old.Seal();
            Assert.That(ContainerSnapshot.TryFromJson(old.ToJson(), out var current, out var reason), Is.True, reason);
            Assert.That(_bag.TryRestore(current, out reason), Is.True, reason);
            Assert.That(_bag.TryGetBeltStack(0, out _), Is.False);
            Assert.That(_bag.TotalUnits, Is.EqualTo(1));
        }
    }
}
