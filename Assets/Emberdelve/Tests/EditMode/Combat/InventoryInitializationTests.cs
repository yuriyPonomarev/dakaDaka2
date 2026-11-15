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
    public sealed class InventoryInitializationTests
    {
        private GameObject _owner;
        private Inventory _inventory;
        private NetRoleKind _priorRole;

        [SetUp]
        public void SetUp()
        {
            _priorRole = NetRole.Current;
            NetRole.ResetToOffline();
        }

        [TearDown]
        public void TearDown()
        {
            NetRole.Set(_priorRole);
            if (_owner != null) UnityEngine.Object.DestroyImmediate(_owner);
        }

        [Test]
        public void FreshOfflineInventoryPlacesConfiguredStartingItemsAtDefaultSize()
        {
            ItemCatalog catalog = AssetDatabase.LoadAssetAtPath<ItemCatalog>(
                "Assets/Emberdelve/Data/EmberdelveItemCatalog.asset");
            GameConfig config = AssetDatabase.LoadAssetAtPath<GameConfig>(
                "Assets/Emberdelve/Data/EmberdelveGameConfig.asset");
            Assert.That(catalog, Is.Not.Null);
            Assert.That(config, Is.Not.Null);
            Assert.That(config.itemCatalog, Is.SameAs(catalog), "the fixture uses the production run catalog");

            CharacterClassData startingClass = AssetDatabase.LoadAssetAtPath<CharacterClassData>(
                "Assets/Emberdelve/Data/Class_Archer.asset");
            Assert.That(startingClass, Is.Not.Null);
            Assert.That(startingClass.startingItems, Is.Not.Null.And.Not.Empty,
                "the shipped class has a starting inventory kit");
            foreach (ItemData item in startingClass.startingItems)
                Assert.That(catalog.Contains(item), Is.True, item != null ? item.name : "starting item is null");

            _owner = new GameObject("InventoryInitialization_OfflineRig");
            _owner.SetActive(false); // model a component queried before Unity's Awake lifecycle callback
            _inventory = _owner.AddComponent<Inventory>();
            _inventory.SetCatalog(catalog);

            // The shipped 10x4 configuration matches Inventory's serialized defaults, so this is
            // the exact offline ClassLoadout sizing path that previously returned before Recompute.
            Assert.That(_inventory.GridWidth, Is.EqualTo(config.inventoryGridWidth));
            Assert.That(_inventory.GridHeight, Is.EqualTo(config.inventoryGridHeight));
            _inventory.ConfigureGrid(config.inventoryGridWidth, config.inventoryGridHeight);
            Assert.That(_inventory.GridWidth, Is.EqualTo(config.inventoryGridWidth));
            Assert.That(_inventory.GridHeight, Is.EqualTo(config.inventoryGridHeight));
            Assert.That(NetRole.Current, Is.EqualTo(NetRoleKind.Offline));
            Assert.That(_inventory.FindFreeOrigin(Vector2Int.one), Is.EqualTo(Vector2Int.zero),
                "a fresh default-sized bag has an actually free first cell before starting items are added");

            _inventory.GiveStartingItems(startingClass.startingItems);

            int expectedSlots = startingClass.startingItems.Distinct().Count();
            int expectedCells = startingClass.startingItems.Where(item => item != null && !QuickBeltRules.CanStore(item))
                .GroupBy(item => item).Sum(group => group.Key.EffectiveFootprint.x * group.Key.EffectiveFootprint.y);
            Assert.That(_inventory.UsedSlots, Is.EqualTo(expectedSlots),
                "every distinct shipped starting item is represented by a placed stack");
            Assert.That(_inventory.UsedCells, Is.EqualTo(expectedCells));
            Assert.That(_inventory.FreeCells, Is.EqualTo(_inventory.Capacity - expectedCells));
            foreach (ItemData item in startingClass.startingItems)
                Assert.That(_inventory.Slots.Any(slot => slot.Item == item), Is.True, item.itemId);
            foreach (InventorySlot slot in _inventory.Slots)
            {
                if (QuickBeltRules.CanStore(slot.Item))
                {
                    Assert.That(QuickBeltRules.TryGetSlot(slot.Origin, _inventory.GridWidth, out int pocket), Is.True, "starting utility must free the bag");
                    Assert.That(_inventory.Hotbar.StackIdAt(pocket), Is.EqualTo(slot.StackId));
                    Assert.That(slot.Count, Is.LessThanOrEqualTo(QuickBeltRules.StackLimit(slot.Item)));
                    continue;
                }
                Assert.That(slot.Origin.x, Is.InRange(0, _inventory.GridWidth - slot.Size.x));
                Assert.That(slot.Origin.y, Is.InRange(0, _inventory.GridHeight - slot.Size.y));
            }

            string placedState = _inventory.ToContainerSnapshot().ToJson();
            FieldInfo initialized = typeof(Inventory).GetField("_gridMapInitialized",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(initialized, Is.Not.Null);
            initialized.SetValue(_inventory, false);
            Assert.That(_inventory.FindFreeOrigin(Vector2Int.one).x, Is.GreaterThanOrEqualTo(0),
                "lazy map rebuild accounts for occupied cells and finds another hole");
            Assert.That(_inventory.ToContainerSnapshot().ToJson(), Is.EqualTo(placedState),
                "rebuilding occupancy preserves every existing stack");
        }
    }
}
