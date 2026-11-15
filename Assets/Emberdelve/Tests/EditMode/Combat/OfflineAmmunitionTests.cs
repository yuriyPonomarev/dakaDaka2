using System;
using System.IO;
using System.Linq;
using Emberdelve.Data;
using Emberdelve.Items;
using NUnit.Framework;
using UnityEditor;

namespace Emberdelve.Combat.Tests
{
    public sealed class OfflineAmmunitionTests
    {
        private string _directory, _path;
        private ItemCatalog _catalog;
        private CharacterClassData _character;
        private OfflineProfile _profile;
        private string WeaponId => _character.secondaryWeapon.weaponId;
        private int WeaponIndex => Array.FindIndex(_profile.Snapshot.Ready.equipment.entries,
            e => _catalog.Items.Any(i => i.itemId == e.item.itemId && i.grantedWeapon == _character.secondaryWeapon));
        private string WeaponUid => _profile.Snapshot.Ready.equipment.entries[WeaponIndex].item.uid;
        private ItemData Pack => _catalog.Items.First(i => i.kind == ItemKind.AmmoPack && i.ammoGrant > 1 && i.MatchesAmmo(_character.secondaryWeapon));

        [SetUp] public void Setup()
        {
            _catalog = AssetDatabase.LoadAssetAtPath<ItemCatalog>("Assets/Emberdelve/Data/EmberdelveItemCatalog.asset");
            _character = AssetDatabase.LoadAssetAtPath<CharacterClassData>("Assets/Emberdelve/Data/Class_Warrior.asset");
            _directory = Path.Combine(Path.GetTempPath(), "EmberdelveAmmoTests", Guid.NewGuid().ToString("N"));
            _path = Path.Combine(_directory, "warrior.json");
            Assert.That(OfflineProfile.TryOpen(_path, _catalog, _character, out _profile, out var why), Is.True, why);
        }
        [TearDown] public void Cleanup()
        {
            _profile?.Dispose();
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }
        private void UnpackStored()
        {
            Assert.That(_profile.Buy(Pack.itemId, _profile.Revision, out var why), Is.True, why);
            int index = Array.FindIndex(_profile.Snapshot.Stash.entries, e => e.item.itemId == Pack.itemId);
            Assert.That(_profile.UnpackAmmo(PreparationArea.Stash, index, WeaponId, _profile.Revision, out why), Is.True, why);
        }
        private void PrepareLoaded()
        {
            UnpackStored();
            Assert.That(_profile.TransferAmmo(WeaponId, 3, false, _profile.Revision, out var why), Is.True, why);
            Assert.That(_profile.LoadAmmo(PreparationArea.Equipment, WeaponIndex, _profile.Revision, out why), Is.True, why);
        }
        private void Reopen()
        {
            _profile.Dispose();
            Assert.That(OfflineProfile.TryOpen(_path, _catalog, _character, out _profile, out var why), Is.True, why);
        }
        private void Unchanged(PreparationState before)
        {
            Assert.That(_profile.Revision, Is.EqualTo(before.Revision));
            Assert.That(_profile.Snapshot.Ammunition.ToJson(), Is.EqualTo(before.Ammunition.ToJson()));
            Assert.That(_profile.Snapshot.Ready.ToJson(), Is.EqualTo(before.Ready.ToJson()));
            Assert.That(_profile.Snapshot.Stash.ToJson(), Is.EqualTo(before.Stash.ToJson()));
            Assert.That(_profile.Snapshot.Gold, Is.EqualTo(before.Gold));
        }
        [Test] public void NewProfileAndReopenKeepPacksButNeverInventLooseAmmunition()
        {
            var ready = _profile.Snapshot.Ready.ToJson();
            Assert.That(_profile.Snapshot.Ready.bag.entries.Sum(e => e.item.quantity), Is.EqualTo(_character.startingItems.Count(i => i != null)));
            Assert.That(_profile.Snapshot.Ammunition.loaded, Is.Empty);
            Assert.That(_profile.Snapshot.Ammunition.carried, Is.Empty);
            Assert.That(_profile.Snapshot.Ammunition.stored, Is.Empty);
            Reopen();
            Assert.That(_profile.Snapshot.Ready.ToJson(), Is.EqualTo(ready));
            Assert.That(_profile.Snapshot.Ammunition.LoadedCount(WeaponUid), Is.Zero);
        }
        [Test] public void ExtractionAndRestartPreserveExactSpentAmmunitionAndStash()
        {
            PrepareLoaded(); string uid = WeaponUid;
            Assert.That(_profile.BeginRaid(out var raid, out var kit, out var ammo, out var why), Is.True, why);
            Assert.That(ammo.LoadedCount(uid), Is.EqualTo(1));
            Assert.That(ammo.CarriedCount(WeaponId), Is.EqualTo(2));
            Assert.That(ammo.stored, Is.Empty);
            Assert.That(_profile.Snapshot.Ammunition.StoredCount(WeaponId), Is.EqualTo(Pack.ammoGrant - 3));
            Assert.That(_profile.Snapshot.Ammunition.loaded, Is.Empty);
            Assert.That(_profile.Snapshot.Ammunition.carried, Is.Empty);
            ammo.SetLoaded(uid, WeaponId, 0); // The authoritative capture records one spent bolt.
            Assert.That(_profile.FinishRaid(raid, kit, ammo, out why), Is.True, why);
            Assert.That(_profile.FinishRaid(raid, kit, ammo, out _), Is.False);
            Reopen();
            Assert.That(_profile.Snapshot.Ammunition.LoadedCount(uid), Is.Zero);
            Assert.That(_profile.Snapshot.Ammunition.CarriedCount(WeaponId), Is.EqualTo(2));
            Assert.That(_profile.Snapshot.Ammunition.StoredCount(WeaponId), Is.EqualTo(Pack.ammoGrant - 3));
            Assert.That(_profile.Snapshot.Ready.ToJson(), Is.EqualTo(kit.ToJson()));
        }
        [Test] public void SameDefinitionWeaponsKeepIndependentUidMagazinesThroughMoves()
        {
            PrepareLoaded(); string first = WeaponUid;
            var weaponItem = _catalog.Items.First(i => i.grantedWeapon == _character.secondaryWeapon);
            Assert.That(_profile.Buy(weaponItem.itemId, _profile.Revision, out var why), Is.True, why);
            int secondIndex = Array.FindIndex(_profile.Snapshot.Stash.entries, e => e.item.itemId == weaponItem.itemId);
            string second = _profile.Snapshot.Stash.entries[secondIndex].item.uid;
            Assert.That(second, Is.Not.EqualTo(first));
            Assert.That(_profile.Snapshot.Ammunition.LoadedCount(second), Is.Zero);
            Assert.That(_profile.LoadAmmo(PreparationArea.Stash, secondIndex, _profile.Revision, out why), Is.True, why);
            Assert.That(_profile.UnloadAmmo(PreparationArea.Equipment, WeaponIndex, _profile.Revision, out why), Is.True, why);
            Assert.That(_profile.Snapshot.Ammunition.LoadedCount(first), Is.Zero);
            Assert.That(_profile.Snapshot.Ammunition.LoadedCount(second), Is.EqualTo(1));
            Assert.That(_profile.Move(PreparationArea.Stash, secondIndex, PreparationArea.Bag, EquipmentSlot.None, null, _profile.Revision, out why), Is.True, why);
            Assert.That(_profile.StoreBag(_profile.Revision, out why), Is.True, why);
            Reopen();
            Assert.That(_profile.Snapshot.Ammunition.LoadedCount(second), Is.EqualTo(1));
        }
        [TestCase(false)] [TestCase(true)]
        public void DeathOrCrashLosesOutgoingButKeepsStashLoadedAndStored(bool crash)
        {
            PrepareLoaded(); string uid = WeaponUid;
            Assert.That(_profile.Move(PreparationArea.Equipment, WeaponIndex, PreparationArea.Stash, EquipmentSlot.None, null, _profile.Revision, out var why), Is.True, why);
            string stash = _profile.Snapshot.Stash.ToJson();
            int stored = _profile.Snapshot.Ammunition.StoredCount(WeaponId);
            Assert.That(_profile.BeginRaid(out var raid, out _, out var ammo, out why), Is.True, why);
            Assert.That(ammo.loaded, Is.Empty);
            if (!crash) Assert.That(_profile.FinishRaid(raid, null, new OfflineAmmunition(), out why), Is.True, why);
            Reopen();
            Assert.That(_profile.Snapshot.Stash.ToJson(), Is.EqualTo(stash));
            Assert.That(_profile.Snapshot.Ammunition.LoadedCount(uid), Is.EqualTo(1));
            Assert.That(_profile.Snapshot.Ammunition.StoredCount(WeaponId), Is.EqualTo(stored));
            Assert.That(_profile.Snapshot.Ammunition.carried, Is.Empty);
            Assert.That(_profile.Snapshot.Ready.equipment.entries, Is.Empty);
        }
        [Test] public void CancellationRestoresExactOutgoingAndRejectsAlteredOrRepeatedSettlement()
        {
            PrepareLoaded(); var before = _profile.Snapshot;
            Assert.That(_profile.BeginRaid(out var raid, out var kit, out var ammo, out var why), Is.True, why);
            var malformed = ammo.Clone(); malformed.loaded = null;
            Assert.That(_profile.FinishRaid(raid, kit, malformed, out _, true), Is.False,
                "malformed cancellation is refused before canonical serialization, without throwing");
            Assert.That(_profile.ActiveRaid, Is.EqualTo(raid));
            var changed = ammo.Clone(); changed.SetReserve(WeaponId, ammo.CarriedCount(WeaponId) + 1, false);
            Assert.That(_profile.FinishRaid(raid, kit, changed, out _, true), Is.False);
            Assert.That(_profile.ActiveRaid, Is.EqualTo(raid));
            Assert.That(_profile.FinishRaid(raid, kit, ammo, out why, true), Is.True, why);
            Assert.That(_profile.Snapshot.Ready.ToJson(), Is.EqualTo(before.Ready.ToJson()));
            Assert.That(_profile.Snapshot.Ammunition.ToJson(), Is.EqualTo(before.Ammunition.ToJson()));
            Assert.That(_profile.FinishRaid(raid, kit, ammo, out _, true), Is.False);
        }
        [Test] public void FailedWriteAndStaleRevisionLeaveItemsAndAmmoUntouched()
        {
            PrepareLoaded(); var before = _profile.Snapshot;
            Assert.That(_profile.TransferAmmo(WeaponId, 1, true, before.Revision - 1, out _), Is.False);
            Unchanged(before);
            using (var lease = new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                Assert.That(_profile.UnloadAmmo(PreparationArea.Equipment, WeaponIndex, _profile.Revision, out _), Is.False);
            Unchanged(before);
            Assert.That(_profile.UnloadAmmo(PreparationArea.Equipment, WeaponIndex, _profile.Revision, out var why), Is.True, why);
            Assert.That(_profile.Snapshot.Ammunition.CarriedCount(WeaponId), Is.EqualTo(3));
        }
        [Test] public void RaidReservationAndSettlementDiskFailureAreRetryableWithoutDuplication()
        {
            PrepareLoaded(); var before = _profile.Snapshot;
            using (var lease = new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                Assert.That(_profile.BeginRaid(out _, out _, out _, out _), Is.False);
            Unchanged(before);
            Assert.That(_profile.BeginRaid(out var raid, out var kit, out var ammo, out var why), Is.True, why);
            var reserved = _profile.Snapshot;
            using (var lease = new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                Assert.That(_profile.FinishRaid(raid, kit, ammo, out _), Is.False);
            Unchanged(reserved);
            Assert.That(_profile.FinishRaid(raid, kit, ammo, out why), Is.True, why);
            Assert.That(_profile.Snapshot.Ammunition.ToJson(), Is.EqualTo(before.Ammunition.ToJson()));
        }
        [Test] public void VersionOneDigestMigrationPreservesEveryItemAndStartsWithZeroAmmo()
        {
            var before = _profile.Snapshot;
            _profile.Dispose();
            var envelope = new OfflineProfileEnvelope { version = 1, classId = _character.classId, revision = before.Revision,
                gold = before.Gold, activeRaid = before.ActiveRaid, supplied = before.Supplied,
                stash = before.Stash.ToJson(), loadout = before.Ready.ToJson() };
            // Independent legacy expression: adding v2 fields must not alter the v1 checksum contract.
            envelope.checksum = ContainerSnapshot.ChecksumOf("1|" + envelope.classId + "|" + envelope.revision + "|" + envelope.gold + "|" +
                envelope.activeRaid + "|" + envelope.stash + "|" + envelope.loadout + (string.IsNullOrEmpty(envelope.supplied) ? "" : "|" + envelope.supplied));
            Assert.That(envelope.Digest(), Is.EqualTo(envelope.checksum));
            File.WriteAllText(_path, UnityEngine.JsonUtility.ToJson(envelope));
            string legacy = File.ReadAllText(_path);
            Reopen();
            Assert.That(_profile.Snapshot.Ready.ToJson(), Is.EqualTo(before.Ready.ToJson()));
            Assert.That(_profile.Snapshot.Stash.ToJson(), Is.EqualTo(before.Stash.ToJson()));
            Assert.That(_profile.Snapshot.Ammunition.loaded, Is.Empty);
            Assert.That(_profile.Snapshot.Ammunition.carried, Is.Empty);
            Assert.That(File.ReadAllText(_path), Is.EqualTo(legacy), "opening v1 must not rewrite it");
            using (var lease = new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                Assert.That(_profile.StoreBag(_profile.Revision, out _), Is.False);
            Assert.That(File.ReadAllText(_path), Is.EqualTo(legacy));
            Assert.That(_profile.StoreBag(_profile.Revision, out var why), Is.True, why);
            var migrated = UnityEngine.JsonUtility.FromJson<OfflineProfileEnvelope>(File.ReadAllText(_path));
            Assert.That(migrated.version, Is.EqualTo(2));
            Assert.That(migrated.ammunitionChecksum, Is.EqualTo(ContainerSnapshot.ChecksumOf(migrated.ammunition)));
            Reopen(); Assert.That(_profile.Snapshot.Ammunition.carried, Is.Empty);
        }
        [Test] public void LoadedWeaponCannotBeSoldUntilUnloadedAndSquireDoesNotRefill()
        {
            PrepareLoaded(); var before = _profile.Snapshot;
            Assert.That(_profile.Sell(PreparationArea.Equipment, WeaponIndex, _profile.Revision, out _), Is.False);
            Unchanged(before);
            Assert.That(_profile.UnloadAmmo(PreparationArea.Equipment, WeaponIndex, _profile.Revision, out var why), Is.True, why);
            Assert.That(_profile.Sell(PreparationArea.Equipment, WeaponIndex, _profile.Revision, out why), Is.True, why);
            Assert.That(_profile.EquipBaseWeapons(_profile.Revision, out why), Is.True, why);
            Assert.That(_profile.Snapshot.Ammunition.LoadedCount(WeaponUid), Is.Zero);
            Assert.That(_profile.Snapshot.Ammunition.CarriedCount(WeaponId), Is.EqualTo(3));
            var ammo = _profile.Snapshot.Ammunition.ToJson();
            Assert.That(_profile.Move(PreparationArea.Equipment, WeaponIndex, PreparationArea.Stash, EquipmentSlot.None, null, _profile.Revision, out _), Is.False);
            Reopen();
            Assert.That(_profile.Snapshot.Ammunition.ToJson(), Is.EqualTo(ammo));
        }
        [Test] public void UnpackAndTransferRefuseOverflowWithoutLosingAnyPackUnits()
        {
            UnpackStored(); UnpackStored(); UnpackStored();
            int capacity = AmmoCarryRules.Capacity(_character, WeaponId);
            Assert.That(_profile.TransferAmmo(WeaponId, capacity, false, _profile.Revision, out var why), Is.True, why);
            Assert.That(_profile.Buy(Pack.itemId, _profile.Revision, out why), Is.True, why);
            int index = Array.FindIndex(_profile.Snapshot.Stash.entries, e => e.item.itemId == Pack.itemId);
            Assert.That(_profile.Move(PreparationArea.Stash, index, PreparationArea.Bag, EquipmentSlot.None, null, _profile.Revision, out why), Is.True, why);
            index = Array.FindIndex(_profile.Snapshot.Ready.bag.entries, e => e.item.itemId == Pack.itemId);
            var before = _profile.Snapshot;
            Assert.That(_profile.UnpackAmmo(PreparationArea.Bag, index, WeaponId, _profile.Revision, out _), Is.False);
            Assert.That(_profile.TransferAmmo(WeaponId, 1, false, _profile.Revision, out _), Is.False);
            Assert.That(_profile.TransferAmmo(WeaponId, int.MaxValue, true, _profile.Revision, out _), Is.False);
            Unchanged(before);
        }
        [Test] public void InvalidOwnershipCountsAndStoredRaidReservesCannotSettle()
        {
            Assert.That(_profile.BeginRaid(out var raid, out var kit, out var ammo, out var why), Is.True, why);
            var bad = ammo.Clone(); bad.SetLoaded(Guid.NewGuid().ToString("N"), WeaponId, 1);
            Assert.That(_profile.FinishRaid(raid, kit, bad, out _), Is.False);
            var uid = kit.equipment.entries.First(e => e.slot == EquipmentSlot.AltWeapon.ToString()).item.uid;
            bad = ammo.Clone(); bad.SetLoaded(uid, WeaponId, 100);
            Assert.That(_profile.FinishRaid(raid, kit, bad, out _), Is.False);
            bad = ammo.Clone(); bad.SetReserve(WeaponId, 1, true);
            Assert.That(_profile.FinishRaid(raid, kit, bad, out _), Is.False);
            bad = ammo.Clone(); bad.SetReserve(WeaponId, AmmoCarryRules.Capacity(_character, WeaponId) + 1, false);
            Assert.That(_profile.FinishRaid(raid, kit, bad, out _), Is.False);
            Assert.That(_profile.ActiveRaid, Is.EqualTo(raid));
            Assert.That(_profile.FinishRaid(raid, kit, ammo, out why), Is.True, why);
        }
        [Test] public void ModelRejectsDuplicateNegativeUnknownAndMismatchedRecords()
        {
            var owned = _profile.Snapshot.Ready.equipment.entries.Select(e => e.item).ToArray();
            var ammo = new OfflineAmmunition(); ammo.SetLoaded(WeaponUid, WeaponId, 1);
            Assert.That(ammo.TryValidate(owned, _catalog, _character, out var why), Is.True, why);
            var copy = ammo.Clone(); copy.loaded[0].magazine = 0;
            Assert.That(ammo.LoadedCount(WeaponUid), Is.EqualTo(1), "clone must own its records");
            ammo.loaded = new[] { ammo.loaded[0], ammo.loaded[0].Clone() };
            Assert.That(OfflineAmmunition.TryFromJson(ammo.ToJson(), out _, out _), Is.False);
            ammo.loaded = new[] { ammo.loaded[0] }; ammo.loaded[0].magazine = -1;
            Assert.That(ammo.TryValidate(owned, _catalog, _character, out _), Is.False);
            ammo.loaded[0].magazine = 1; ammo.loaded[0].weaponId = "unknown";
            Assert.That(ammo.TryValidate(owned, _catalog, _character, out _), Is.False);
            ammo = new OfflineAmmunition(); ammo.SetReserve("unknown", 1, false);
            Assert.That(ammo.TryValidate(owned, _catalog, _character, out _), Is.False);
            ammo = new OfflineAmmunition(); ammo.SetReserve(WeaponId, OfflineAmmunition.StoredCapacity + 1, true);
            Assert.That(ammo.TryValidate(owned, _catalog, _character, out _), Is.False);
            ammo = new OfflineAmmunition(); ammo.SetReserve(WeaponId, 1, false);
            ammo.carried = new[] { ammo.carried[0], ammo.carried[0].Clone() };
            Assert.That(OfflineAmmunition.TryFromJson(ammo.ToJson(), out _, out _), Is.False);
            Assert.Throws<ArgumentException>(() => new OfflineAmmunition().SetLoaded(WeaponUid, WeaponId, -1));
        }
        [Test] public void ReserveCapacityUsesMatchingSlotAndUnknownFallbackWithoutNegativeCapacity()
        {
            var character = UnityEngine.ScriptableObject.CreateInstance<CharacterClassData>();
            try
            {
                character.primaryWeapon = _character.primaryWeapon;
                character.secondaryWeapon = _character.secondaryWeapon;
                character.maxReserveAmmo = 7; character.secondaryMaxReserveAmmo = 11;
                Assert.That(AmmoCarryRules.Capacity(character, character.primaryWeapon.weaponId), Is.EqualTo(7));
                Assert.That(AmmoCarryRules.Capacity(character, WeaponId), Is.EqualTo(11));
                Assert.That(AmmoCarryRules.Capacity(character, "unknown"), Is.EqualTo(11));
                character.maxReserveAmmo = -4; character.secondaryMaxReserveAmmo = -8;
                Assert.That(AmmoCarryRules.Capacity(character, "unknown"), Is.Zero);
                Assert.That(AmmoCarryRules.Capacity(character, WeaponId), Is.Zero);
            }
            finally { UnityEngine.Object.DestroyImmediate(character); }
        }
        [Test] public void FullReserveRefusesUnloadAndWrongPackWithoutChangingOwnership()
        {
            PrepareLoaded(); UnpackStored(); UnpackStored();
            int need = AmmoCarryRules.Capacity(_character, WeaponId) - _profile.Snapshot.Ammunition.CarriedCount(WeaponId);
            Assert.That(_profile.TransferAmmo(WeaponId, need, false, _profile.Revision, out var why), Is.True, why);
            var before = _profile.Snapshot;
            Assert.That(_profile.UnloadAmmo(PreparationArea.Equipment, WeaponIndex, _profile.Revision, out _), Is.False);
            Unchanged(before);
            var wrongPack = _catalog.Items.First(i => i.kind == ItemKind.AmmoPack && !i.MatchesAmmo(_character.secondaryWeapon));
            Assert.That(_profile.Buy(wrongPack.itemId, _profile.Revision, out why), Is.True, why);
            int index = Array.FindIndex(_profile.Snapshot.Stash.entries, e => e.item.itemId == wrongPack.itemId);
            before = _profile.Snapshot;
            Assert.That(_profile.UnpackAmmo(PreparationArea.Stash, index, WeaponId, _profile.Revision, out _), Is.False);
            Unchanged(before);
        }
        [Test] public void AmmunitionTamperIsRejectedEvenWithUnchangedItemSnapshots()
        {
            PrepareLoaded(); _profile.Dispose();
            var envelope = UnityEngine.JsonUtility.FromJson<OfflineProfileEnvelope>(File.ReadAllText(_path));
            Assert.That(OfflineAmmunition.TryFromJson(envelope.ammunition, out var ammo, out var why), Is.True, why);
            ammo.SetReserve(WeaponId, 0, false); envelope.ammunition = ammo.ToJson();
            string altered = UnityEngine.JsonUtility.ToJson(envelope);
            File.WriteAllText(_path, altered);
            Assert.That(OfflineProfile.TryOpen(_path, _catalog, _character, out _, out _), Is.False);
            Assert.That(File.ReadAllText(_path), Is.EqualTo(altered));
        }
    }
}
