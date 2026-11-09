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
    public sealed class CorpseLootManifestTests
    {
        private GameObject _sourceObject;
        private GameObject _otherObject;
        private GameObject _reentrantObject;
        private Inventory _bag;
        private Equipment _equipment;
        private Inventory _otherBag;
        private Equipment _otherEquipment;
        private Inventory _reentrantBag;
        private Equipment _reentrantEquipment;
        private ItemCatalog _catalog;
        private ItemData _stackable;
        private ItemInstance _gearInstance;
        private Emberdelve.Core.NetRoleKind _priorRole;

        [SetUp]
        public void SetUp()
        {
            _priorRole = Emberdelve.Core.NetRole.Current;
            Emberdelve.Core.NetRole.ResetToOffline();
            _catalog = AssetDatabase.LoadAssetAtPath<ItemCatalog>("Assets/Emberdelve/Data/EmberdelveItemCatalog.asset");
            Assert.That(_catalog, Is.Not.Null);
            _stackable = _catalog.Items.FirstOrDefault(item => item != null && !item.IsEquippable);
            ItemData gear = _catalog.Items.FirstOrDefault(item => item != null && item.IsEquippable);
            Assert.That(_stackable, Is.Not.Null, "the catalog supplies carried stack data");
            Assert.That(gear, Is.Not.Null, "the catalog supplies equippable gear data");

            _sourceObject = new GameObject("CorpseLootManifestSource");
            _bag = _sourceObject.AddComponent<Inventory>();
            _equipment = _sourceObject.AddComponent<Equipment>();
            _bag.SetCatalog(_catalog);
            _bag.ConfigureGrid(10, 6);
            Assert.That(_bag.TryAdd(_stackable, 3, out string reason), Is.True, reason);
            Assert.That(_bag.TryMintKitInstance(gear, out _gearInstance, out reason), Is.True, reason);
            Assert.That(_bag.TryAddInstance(_gearInstance, out reason), Is.True, reason);

            _otherObject = new GameObject("CorpseLootManifestOther");
            _otherBag = _otherObject.AddComponent<Inventory>();
            _otherEquipment = _otherObject.AddComponent<Equipment>();
            _otherBag.SetCatalog(_catalog);
            _otherBag.ConfigureGrid(10, 6);

            _reentrantObject = new GameObject("CorpseLootManifestReentrant");
            _reentrantBag = _reentrantObject.AddComponent<Inventory>();
            _reentrantEquipment = _reentrantObject.AddComponent<Equipment>();
            _reentrantBag.SetCatalog(_catalog);
            _reentrantBag.ConfigureGrid(10, 6);
        }

        [TearDown]
        public void TearDown()
        {
            Emberdelve.Core.NetRole.Set(_priorRole);
            if (_sourceObject != null) UnityEngine.Object.DestroyImmediate(_sourceObject);
            if (_otherObject != null) UnityEngine.Object.DestroyImmediate(_otherObject);
            if (_reentrantObject != null) UnityEngine.Object.DestroyImmediate(_reentrantObject);
        }

        [Test]
        public void CapturePreservesCarriedStacksAndExactGearIdentityWithoutChangingSource()
        {
            string before = LoadoutSnapshot.Capture(_bag, _equipment).ToJson();
            ulong revision = _bag.Revision;

            Assert.That(CorpseLootManifest.TryCapture(42, new Vector3(1.25f, 2f, -3f), _bag, _equipment,
                out CorpseLootManifest manifest, out string reason), Is.True, reason);

            LoadoutSnapshot captured = manifest.CopyLoadout();
            Assert.That(captured.ToJson(), Is.EqualTo(before));
            Assert.That(captured.bag.entries.Any(entry => entry.item.itemId == _stackable.itemId && entry.item.quantity == 3), Is.True);
            Assert.That(manifest.CopyGearUids(), Does.Contain(_gearInstance.Uid));
            Assert.That(manifest.GearUidCount, Is.EqualTo(1));
            Assert.That(manifest.SourcePlayerId, Is.EqualTo(42));
            Assert.That(manifest.WorldPosition, Is.EqualTo(new Vector3(1.25f, 2f, -3f)));
            Assert.That(manifest.SourceRevision, Is.EqualTo(revision));
            Assert.That(manifest.StillMatches(_bag, _equipment), Is.True);

            Assert.That(LoadoutSnapshot.Capture(_bag, _equipment).ToJson(), Is.EqualTo(before));
            Assert.That(_bag.Revision, Is.EqualTo(revision));
            Assert.That(_bag.HoldsInstanceUid(_gearInstance.Uid), Is.True);
        }

        [Test]
        public void ReturnedSnapshotAndUidCopiesCannotMutateTheManifest()
        {
            Assert.That(CorpseLootManifest.TryCapture(7, Vector3.zero, _bag, _equipment,
                out CorpseLootManifest manifest, out string reason), Is.True, reason);
            string sealedJson = manifest.CopyLoadout().ToJson();
            Guid[] originalUids = manifest.CopyGearUids();

            LoadoutSnapshot exposed = manifest.CopyLoadout();
            exposed.bag.entries[0].item.quantity++;
            Guid[] exposedUids = manifest.CopyGearUids();
            exposedUids[0] = Guid.Empty;

            Assert.That(exposed.ChecksumMatches(), Is.False, "the changed caller copy is detectably invalid");
            Assert.That(manifest.CopyLoadout().ToJson(), Is.EqualTo(sealedJson));
            Assert.That(manifest.CopyGearUids(), Is.EqualTo(originalUids));
            Assert.That(manifest.StillMatches(_bag, _equipment), Is.True);
        }

        [Test]
        public void SourceMutationMakesTheCapturedManifestStale()
        {
            Assert.That(CorpseLootManifest.TryCapture(9, Vector3.zero, _bag, _equipment,
                out CorpseLootManifest manifest, out string reason), Is.True, reason);
            Assert.That(manifest.StillMatches(_bag, _equipment), Is.True);

            Assert.That(_bag.TryAdd(_stackable, 1, out reason), Is.True, reason);

            Assert.That(manifest.StillMatches(_bag, _equipment), Is.False);
        }

        [Test]
        public void InvalidIdentityPositionAndContainerPairingAreRefused()
        {
            Assert.That(CorpseLootManifest.TryCapture(-1, Vector3.zero, _bag, _equipment, out _, out _), Is.False);
            Assert.That(CorpseLootManifest.TryCapture(1, new Vector3(float.NaN, 0f, 0f), _bag, _equipment,
                out _, out _), Is.False);
            Assert.That(CorpseLootManifest.TryCapture(1, Vector3.zero, null, _equipment, out _, out _), Is.False);

            Assert.That(CorpseLootManifest.TryCapture(1, Vector3.zero, _bag, _otherEquipment, out _, out _), Is.False);
        }

        [Test]
        public void DeathMovesExactLoadoutIntoRegistryAndWholeLootTransferPreservesUid()
        {
            CorpseLootRegistry registry = new CorpseLootRegistry();
            string original = LoadoutSnapshot.Capture(_bag, _equipment).ToJson();
            Assert.That(registry.TryCommitDeath(42, new Vector3(2f, 0f, 4f), _bag, _equipment,
                out ulong corpseId, out string reason), Is.True, reason);
            Assert.That(corpseId, Is.GreaterThan(0));
            Assert.That(registry.Count, Is.EqualTo(1));
            Assert.That(_bag.ToContainerSnapshot().entries, Is.Empty);
            Assert.That(_equipment.ToContainerSnapshot().entries, Is.Empty);
            Assert.That(registry.TryGet(corpseId, out CorpseLootManifest manifest), Is.True);
            Assert.That(manifest.CopyLoadout().ToJson(), Is.EqualTo(original));
            Assert.That(manifest.CopyGearUids(), Does.Contain(_gearInstance.Uid));

            ItemUidRegistry recipientUidRegistry = new ItemUidRegistry();
            Assert.That(registry.TryLootAll(corpseId, _otherBag, _otherEquipment, recipientUidRegistry, out reason), Is.True, reason);
            Assert.That(LoadoutSnapshot.Capture(_otherBag, _otherEquipment).ToJson(), Is.EqualTo(original));
            Assert.That(_otherBag.HoldsInstanceUid(_gearInstance.Uid), Is.True);
            Assert.That(recipientUidRegistry.TryGetOwner(_gearInstance.Uid, out string owner), Is.True);
            Assert.That(owner, Is.EqualTo(Inventory.SnapshotContainerId));
            Assert.That(registry.Count, Is.Zero);
            Assert.That(registry.TryLootAll(corpseId, _otherBag, _otherEquipment, recipientUidRegistry, out _), Is.False,
                "a second request cannot transfer a consumed corpse");
        }

        [Test]
        public void FailedSourceClearRollsBackStagedCorpseAndLeavesLoadoutUntouched()
        {
            CorpseLootRegistry registry = new CorpseLootRegistry();
            string original = LoadoutSnapshot.Capture(_bag, _equipment).ToJson();
            _bag.SetCatalog(null);

            Assert.That(registry.TryCommitDeath(9, Vector3.zero, _bag, _equipment, out ulong corpseId, out string reason), Is.False);
            Assert.That(reason, Does.Contain("catalog"));
            Assert.That(corpseId, Is.Zero);
            Assert.That(registry.Count, Is.Zero);
            Assert.That(LoadoutSnapshot.Capture(_bag, _equipment).ToJson(), Is.EqualTo(original));
            Assert.That(_bag.HoldsInstanceUid(_gearInstance.Uid), Is.True);
        }

        [Test]
        public void ConflictingProfileUidRefusesLootWithoutConsumingCorpse()
        {
            CorpseLootRegistry registry = new CorpseLootRegistry();
            Assert.That(registry.TryCommitDeath(7, Vector3.zero, _bag, _equipment, out ulong corpseId, out string reason), Is.True, reason);
            var profile = new ItemUidRegistry();
            Assert.That(profile.TryReplaceOwner("stash", new[] { _gearInstance.Uid }, out reason), Is.True, reason);

            Assert.That(registry.TryLootAll(corpseId, _otherBag, _otherEquipment, profile, out reason), Is.False);
            Assert.That(reason, Does.Contain("already held"));
            Assert.That(registry.Count, Is.EqualTo(1));
            Assert.That(_otherBag.ToContainerSnapshot().entries, Is.Empty);

            profile.ReleaseOwner("stash");
            Assert.That(registry.TryLootAll(corpseId, _otherBag, _otherEquipment, profile, out reason), Is.True, reason);
            Assert.That(_otherBag.HoldsInstanceUid(_gearInstance.Uid), Is.True);
        }

        [Test]
        public void ReentrantLootRequestCannotDuplicateTheStagedLoadout()
        {
            CorpseLootRegistry registry = new CorpseLootRegistry();
            Assert.That(registry.TryCommitDeath(12, Vector3.zero, _bag, _equipment, out ulong corpseId, out string reason), Is.True, reason);
            var profile = new ItemUidRegistry();
            bool reentrantAccepted = true;
            string reentrantReason = null;
            System.Action<int> reenter = _ =>
                reentrantAccepted = registry.TryLootAll(corpseId, _reentrantBag, _reentrantEquipment,
                    new ItemUidRegistry(), out reentrantReason);
            GameEvents.InventoryChanged += reenter;
            bool accepted;
            try
            {
                accepted = registry.TryLootAll(corpseId, _otherBag, _otherEquipment, profile, out reason);
            }
            finally
            {
                GameEvents.InventoryChanged -= reenter;
            }

            Assert.That(accepted, Is.True, reason);
            Assert.That(reentrantAccepted, Is.False);
            Assert.That(reentrantReason, Does.Contain("already being transferred"));
            Assert.That(_otherBag.HoldsInstanceUid(_gearInstance.Uid), Is.True);
            Assert.That(_reentrantBag.ToContainerSnapshot().entries, Is.Empty);
            Assert.That(registry.Count, Is.Zero);
        }
    }
}
