using System;
using System.Linq;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Items;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Emberdelve.Combat.Tests
{
    public sealed class CorpseContainerModelTests
    {
        private GameObject _sourceObject;
        private GameObject _targetObject;
        private GameObject _reentrantObject;
        private Inventory _sourceBag;
        private Equipment _sourceEquipment;
        private Inventory _targetBag;
        private Equipment _targetEquipment;
        private Inventory _reentrantBag;
        private Equipment _reentrantEquipment;
        private ItemCatalog _catalog;
        private ItemData _stackable;
        private int _sourceStackQuantity;
        private ItemInstance _gear;
        private CorpseContainerModel _model;
        private ItemUidRegistry _profile;
        private NetRoleKind _priorRole;

        [SetUp]
        public void SetUp()
        {
            _priorRole = NetRole.Current;
            NetRole.ResetToOffline();
            _catalog = AssetDatabase.LoadAssetAtPath<ItemCatalog>("Assets/Emberdelve/Data/EmberdelveItemCatalog.asset");
            Assert.That(_catalog, Is.Not.Null);
            _stackable = _catalog.Items.FirstOrDefault(item => item != null && item.CanStack);
            ItemData gearDefinition = _catalog.Items.FirstOrDefault(item => item != null && item.IsEquippable);
            Assert.That(_stackable, Is.Not.Null);
            Assert.That(gearDefinition, Is.Not.Null);
            _sourceStackQuantity = Math.Max(2, Math.Min(4, _stackable.StackLimitForFootprint));
            Assert.That(_stackable.StackLimitForFootprint, Is.GreaterThanOrEqualTo(2), "the catalog supplies a stackable corpse item");

            _sourceObject = new GameObject("CorpseContainerSource");
            _sourceBag = _sourceObject.AddComponent<Inventory>();
            _sourceEquipment = _sourceObject.AddComponent<Equipment>();
            _sourceBag.SetCatalog(_catalog);
            _sourceBag.ConfigureGrid(10, 6);
            Assert.That(_sourceBag.TryAdd(_stackable, _sourceStackQuantity, out string reason), Is.True, reason);
            Assert.That(_sourceBag.TryMintKitInstance(gearDefinition, out _gear, out reason), Is.True, reason);
            Assert.That(_sourceBag.TryAddInstance(_gear, out reason), Is.True, reason);
            Assert.That(CorpseLootManifest.TryCapture(37, new Vector3(3f, 0f, -2f), _sourceBag, _sourceEquipment,
                out CorpseLootManifest manifest, out reason), Is.True, reason);
            Assert.That(CorpseContainerModel.TryCreate(manifest, out _model, out reason), Is.True, reason);

            _targetObject = new GameObject("CorpseContainerTarget");
            _targetBag = _targetObject.AddComponent<Inventory>();
            _targetEquipment = _targetObject.AddComponent<Equipment>();
            _targetBag.SetCatalog(_catalog);
            _targetBag.ConfigureGrid(10, 6);

            _reentrantObject = new GameObject("CorpseContainerReentrantTarget");
            _reentrantBag = _reentrantObject.AddComponent<Inventory>();
            _reentrantEquipment = _reentrantObject.AddComponent<Equipment>();
            _reentrantBag.SetCatalog(_catalog);
            _reentrantBag.ConfigureGrid(10, 6);

            _profile = new ItemUidRegistry();
            Assert.That(_profile.TryReplaceOwner(Inventory.SnapshotContainerId, Array.Empty<Guid>(), out reason), Is.True, reason);
            Assert.That(_profile.TryReplaceOwner(Equipment.SnapshotContainerId, Array.Empty<Guid>(), out reason), Is.True, reason);
        }

        [Test]
        public void LootRollSplitsStacksAndTransfersExactGearOnce()
        {
            int quantity = _stackable.StackLimitForFootprint + 2;
            var drops = new[] { new LootDrop(_stackable, quantity), new LootDrop(_gear.Definition, 1, _gear) };
            Assert.That(CorpseContainerModel.TryCreateLoot("enemy", "Skeleton", Vector3.zero, drops, _catalog, out var loot, out string reason), Is.True, reason);
            Assert.That(loot.Kind, Is.EqualTo("enemy"));
            Assert.That(loot.CopyEntries().Where(e => !e.IsGear).Sum(e => e.Quantity), Is.EqualTo(quantity));
            var gear = loot.CopyEntries().Single(e => e.IsGear);
            Assert.That(loot.TryTransferToBag(gear.EntryId, 1, loot.Revision, _targetBag, _targetBag.Revision, _profile, out reason), Is.True, reason);
            Assert.That(loot.TryTransferToBag(gear.EntryId, 1, loot.Revision, _targetBag, _targetBag.Revision, _profile, out _), Is.False);
            Assert.That(_targetBag.Slots.Single(e => e.Instance != null).Instance.ToSnapshot().ToJson(), Is.EqualTo(_gear.ToSnapshot().ToJson()));
        }

        [Test]
        public void PlayerCorpseRetainsEquipmentSlotsAndBagOriginsAfterTransfer()
        {
            var equipped = LoadoutSnapshot.Capture(_sourceBag, _sourceEquipment);
            var gearEntry = equipped.bag.entries.Single(e => e.item.uid == _gear.Uid.ToString("N"));
            equipped.bag.entries = equipped.bag.entries.Where(e => e != gearEntry).ToArray();
            gearEntry.slot = _gear.Definition.equipSlot.ToString(); gearEntry.x = 0; gearEntry.y = 0;
            equipped.equipment.entries = new[] { gearEntry }; equipped.Seal();
            Assert.That(LoadoutSnapshot.TryRestore(equipped, _sourceBag, _sourceEquipment, null, out string reason), Is.True, reason);
            Assert.That(CorpseLootManifest.TryCapture(37, Vector3.zero, _sourceBag, _sourceEquipment, out var manifest, out reason), Is.True, reason);
            Assert.That(CorpseContainerModel.TryCreate(manifest, out var corpse, out reason), Is.True, reason);
            Assert.That(corpse.Width, Is.EqualTo(10)); Assert.That(corpse.Height, Is.EqualTo(6));
            var worn = corpse.CopyEntries().Single(e => e.IsGear);
            Assert.That(worn.Slot, Is.EqualTo(_gear.Definition.equipSlot.ToString()));
            var stack = corpse.CopyEntries().Single(e => !e.IsGear);
            Assert.That(corpse.TryTransferToBag(stack.EntryId, 1, corpse.Revision, _targetBag, _targetBag.Revision, _profile, out reason), Is.True, reason);
            Assert.That(corpse.TryGetEntry(worn.EntryId, out var after), Is.True);
            Assert.That(after.Slot, Is.EqualTo(worn.Slot)); Assert.That(after.Uid, Is.EqualTo(worn.Uid));
        }

        [TearDown]
        public void TearDown()
        {
            NetRole.Set(_priorRole);
            if (_sourceObject != null) Object.DestroyImmediate(_sourceObject);
            if (_targetObject != null) Object.DestroyImmediate(_targetObject);
            if (_reentrantObject != null) Object.DestroyImmediate(_reentrantObject);
        }

        [Test]
        public void PartialStackTransferPreservesExactRemainderAndRejectsStaleRevisions()
        {
            int targetStackQuantity = _stackable.StackLimitForFootprint - 1;
            Assert.That(_targetBag.TryAdd(_stackable, targetStackQuantity, out string addReason), Is.True, addReason);
            CorpseItemView stack = FindEntry(_stackable.itemId);
            ulong corpseRevision = _model.Revision;
            ulong bagRevision = _targetBag.Revision;
            int recoveredBefore = _targetBag.RecoveredValue;
            int lifetimeBefore = _targetBag.LifetimeCollected;

            Assert.That(_model.TryTransferToBag(stack.EntryId, 1, corpseRevision, _targetBag, bagRevision, _profile, out string reason), Is.True, reason);

            Assert.That(_model.Revision, Is.EqualTo(corpseRevision + 1));
            Assert.That(_model.TryGetEntry(stack.EntryId, out CorpseItemView remainder), Is.True);
            Assert.That(remainder.Quantity, Is.EqualTo(_sourceStackQuantity - 1));
            Assert.That(_targetBag.ToContainerSnapshot().entries.Sum(entry => entry.item.quantity), Is.EqualTo(_stackable.StackLimitForFootprint));
            Assert.That(_targetBag.ToContainerSnapshot().entries.Length, Is.EqualTo(1),
                "matching ordinary stacks merge before requiring a free cell");
            Assert.That(_targetBag.Revision, Is.EqualTo(bagRevision + 1));
            Assert.That(_targetBag.RecoveredValue, Is.EqualTo(recoveredBefore + _stackable.value));
            Assert.That(_targetBag.LifetimeCollected, Is.EqualTo(lifetimeBefore + 1));

            string sourceAfterCommit = remainder.CopySnapshot().ToJson();
            string bagAfterCommit = _targetBag.ToContainerSnapshot().ToJson();
            Assert.That(_model.TryTransferToBag(stack.EntryId, 1, corpseRevision, _targetBag,
                _targetBag.Revision, _profile, out _), Is.False, "stale corpse revision must refuse a replay");
            Assert.That(_model.TryTransferToBag(stack.EntryId, 1, _model.Revision, _targetBag,
                bagRevision, _profile, out _), Is.False, "stale bag revision must refuse a replay");
            Assert.That(_model.TryGetEntry(stack.EntryId, out remainder), Is.True);
            Assert.That(remainder.CopySnapshot().ToJson(), Is.EqualTo(sourceAfterCommit));
            Assert.That(_targetBag.ToContainerSnapshot().ToJson(), Is.EqualTo(bagAfterCommit));
        }

        [Test]
        public void GearTransferPreservesUidAndRegistersItUnderRecipientBag()
        {
            CorpseItemView gear = FindEntry(_gear.Definition.itemId);
            Assert.That(gear.IsGear, Is.True);
            Guid uid = _gear.Uid;
            int recoveredBefore = _targetBag.RecoveredValue;
            int lifetimeBefore = _targetBag.LifetimeCollected;

            Assert.That(_model.TryTransferToBag(gear.EntryId, 1, _model.Revision, _targetBag,
                _targetBag.Revision, _profile, out string reason), Is.True, reason);

            Assert.That(_targetBag.HoldsInstanceUid(uid), Is.True);
            Assert.That(_model.TryGetEntry(gear.EntryId, out _), Is.False);
            Assert.That(_profile.TryGetOwner(uid, out string owner), Is.True);
            Assert.That(owner, Is.EqualTo(Inventory.SnapshotContainerId));
            Assert.That(_profile.Count, Is.EqualTo(1));
            Assert.That(_targetBag.RecoveredValue, Is.EqualTo(recoveredBefore + _gear.Definition.value));
            Assert.That(_targetBag.LifetimeCollected, Is.EqualTo(lifetimeBefore + 1));
        }

        [Test]
        public void FullBagRefusesTransferWithoutChangingEitherSideOrUidLedger()
        {
            Vector2Int fillerFootprint = _stackable.EffectiveFootprint;
            _targetBag.ConfigureGrid(fillerFootprint.x, fillerFootprint.y);
            Assert.That(_targetBag.TryAdd(_stackable, 1, out string reason), Is.True, reason);
            Assert.That(_profile.TryReplaceOwner(Inventory.SnapshotContainerId, Array.Empty<Guid>(), out reason), Is.True, reason);
            CorpseItemView gear = FindEntry(_gear.Definition.itemId);
            string bagBefore = _targetBag.ToContainerSnapshot().ToJson();
            string sourceBefore = gear.CopySnapshot().ToJson();
            ulong corpseRevision = _model.Revision;
            ulong bagRevision = _targetBag.Revision;
            int ledgerCount = _profile.Count;

            Assert.That(_model.TryTransferToBag(gear.EntryId, 1, corpseRevision, _targetBag,
                bagRevision, _profile, out reason), Is.False);
            Assert.That(reason, Does.Contain("space"));
            Assert.That(_targetBag.ToContainerSnapshot().ToJson(), Is.EqualTo(bagBefore));
            Assert.That(_targetBag.Revision, Is.EqualTo(bagRevision));
            Assert.That(_model.Revision, Is.EqualTo(corpseRevision));
            Assert.That(_model.TryGetEntry(gear.EntryId, out CorpseItemView stillThere), Is.True);
            Assert.That(stillThere.CopySnapshot().ToJson(), Is.EqualTo(sourceBefore));
            Assert.That(_profile.Count, Is.EqualTo(ledgerCount));
        }

        [Test]
        public void ProfileUidConflictRefusesTransferAndLeavesCorpseAndBagUntouched()
        {
            CorpseItemView gear = FindEntry(_gear.Definition.itemId);
            Guid uid = _gear.Uid;
            Assert.That(_profile.TryReplaceOwner("stash", new[] { uid }, out string reason), Is.True, reason);
            string bagBefore = _targetBag.ToContainerSnapshot().ToJson();
            string sourceBefore = gear.CopySnapshot().ToJson();
            ulong corpseRevision = _model.Revision;
            ulong bagRevision = _targetBag.Revision;

            Assert.That(_model.TryTransferToBag(gear.EntryId, 1, corpseRevision, _targetBag,
                bagRevision, _profile, out reason), Is.False);
            Assert.That(reason, Does.Contain("already held"));
            Assert.That(_targetBag.ToContainerSnapshot().ToJson(), Is.EqualTo(bagBefore));
            Assert.That(_targetBag.Revision, Is.EqualTo(bagRevision));
            Assert.That(_model.Revision, Is.EqualTo(corpseRevision));
            Assert.That(_model.TryGetEntry(gear.EntryId, out CorpseItemView stillThere), Is.True);
            Assert.That(stillThere.CopySnapshot().ToJson(), Is.EqualTo(sourceBefore));
            Assert.That(_profile.TryGetOwner(uid, out string owner), Is.True);
            Assert.That(owner, Is.EqualTo("stash"));
        }

        [Test]
        public void ReentrantTransferDuringInventoryNotificationCannotDuplicateEntry()
        {
            CorpseItemView stack = FindEntry(_stackable.itemId);
            ulong expectedCorpseRevision = _model.Revision;
            bool reentrantAccepted = true;
            string reentrantReason = null;
            System.Action<int> reenter = _ => reentrantAccepted = _model.TryTransferToBag(stack.EntryId, 1,
                _model.Revision, _reentrantBag, _reentrantBag.Revision, new ItemUidRegistry(), out reentrantReason);
            GameEvents.InventoryChanged += reenter;

            bool accepted;
            try
            {
                accepted = _model.TryTransferToBag(stack.EntryId, 1, expectedCorpseRevision,
                    _targetBag, _targetBag.Revision, _profile, out string reason);
                Assert.That(accepted, Is.True, reason);
            }
            finally
            {
                GameEvents.InventoryChanged -= reenter;
            }

            Assert.That(reentrantAccepted, Is.False);
            Assert.That(reentrantReason, Does.Contain("already being transferred"));
            Assert.That(_targetBag.ToContainerSnapshot().entries.Sum(entry => entry.item.quantity), Is.EqualTo(1));
            Assert.That(_reentrantBag.ToContainerSnapshot().entries, Is.Empty);
            Assert.That(_model.TryGetEntry(stack.EntryId, out CorpseItemView remainder), Is.True);
            Assert.That(remainder.Quantity, Is.EqualTo(_sourceStackQuantity - 1));
        }

        private CorpseItemView FindEntry(string itemId)
        {
            foreach (CorpseItemView entry in _model.CopyEntries())
                if (entry.ItemId == itemId) return entry;
            Assert.Fail("corpse entry was not found: " + itemId);
            return default;
        }
    }
}
