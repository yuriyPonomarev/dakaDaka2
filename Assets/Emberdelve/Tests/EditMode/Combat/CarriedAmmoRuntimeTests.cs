using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Items;
using Emberdelve.Movement;
using Emberdelve.Player;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Emberdelve.Combat.Tests
{
    public sealed class CarriedAmmoKitStateTests
    {
        private const string Weapon = "crossbow";
        private const string First = "11111111111111111111111111111111";
        private const string Second = "22222222222222222222222222222222";
        private static KitAmmoSeed Pool(string uid, int loaded, int reserve, int size = 1) => new KitAmmoSeed {
            WeaponId = Weapon, PoolId = uid, ReserveId = Weapon, MagazineSize = size,
            Magazine = loaded, Reserve = reserve, MaxReserve = 16, ReloadTime = 2f
        };
        private static KitSeed Seed(string active, KitSeedReason reason, params KitAmmoSeed[] pools) => new KitSeed {
            Reason = reason, MaxMana = 10, ActiveWeaponId = Weapon, ActiveAmmoPoolId = active, Pools = pools
        };
        [Test] public void IdenticalDefinitionsHaveIndependentMagazinesAndOneSharedReserve()
        {
            var state = new CombatKitState();
            state.ApplySeed(Seed(First, KitSeedReason.ClassApply, Pool(First, 1, 5), Pool(Second, 0, 5)), true);
            Assert.That(state.ToView().AmmoPoolId, Is.EqualTo(First));
            Assert.That(state.TryConsumeAmmo(1), Is.True);
            Assert.That(state.TryConsumeAmmo(1), Is.False);
            state.ApplySeed(Seed(Second, KitSeedReason.Weapon, Pool(First, 1, 16), Pool(Second, 1, 16)), false);
            Assert.That(state.Active.Magazine, Is.Zero, "same-definition swap must not copy the first magazine or accept a stale refill");
            Assert.That(state.TryStartReload(2f), Is.True);
            state.Tick(1.99f);
            Assert.That(state.Active.Magazine, Is.Zero);
            Assert.That(state.Active.Reserve, Is.EqualTo(5));
            state.Tick(0.02f);
            Assert.That(state.Active.Magazine, Is.EqualTo(1));
            Assert.That(state.Active.Reserve, Is.EqualTo(4));
            state.ApplySeed(Seed(First, KitSeedReason.Weapon, Pool(First, 1, 16), Pool(Second, 1, 16)), false);
            Assert.That(state.Active.Magazine, Is.Zero);
            Assert.That(state.Active.Reserve, Is.EqualTo(4));
            Assert.That(state.Pools[Second].Magazine, Is.EqualTo(1));
            Assert.That(state.Pools[First].ReserveState, Is.SameAs(state.Pools[Second].ReserveState));
        }
        [Test] public void AddingAnEmptyNewUidDoesNotCloneOrRefillTheKnownReserve()
        {
            var state = new CombatKitState();
            state.ApplySeed(Seed(First, KitSeedReason.ClassApply, Pool(First, 0, 2)), true);
            Assert.That(state.TryStartReload(1f), Is.True); state.Tick(1f);
            state.ApplySeed(Seed(Second, KitSeedReason.Weapon, Pool(Second, 0, 16)), false);
            Assert.That(state.Active.Magazine, Is.Zero);
            Assert.That(state.Active.Reserve, Is.EqualTo(1));
            Assert.That(state.TryConsumeAmmo(1), Is.False);
            state.ApplySeed(Seed("missing-uid", KitSeedReason.Weapon), false);
            Assert.That(state.ToView().HasActiveAmmo, Is.False);
            Assert.That(state.TryConsumeAmmo(1), Is.False);
        }
        [Test] public void InterruptedReloadNeverCreditsEitherMagazineOrDebitsReserve()
        {
            var state = new CombatKitState();
            state.ApplySeed(Seed(First, KitSeedReason.ClassApply, Pool(First, 0, 4), Pool(Second, 0, 4)), true);
            Assert.That(state.TryStartReload(2f), Is.True); state.Tick(1f);
            state.ApplySeed(Seed(Second, KitSeedReason.Weapon), false);
            Assert.That(state.Reloading, Is.False); state.Tick(10f);
            Assert.That(state.Pools[First].Magazine, Is.Zero);
            Assert.That(state.Pools[Second].Magazine, Is.Zero);
            Assert.That(state.Active.Reserve, Is.EqualTo(4));
            Assert.That(state.TryStartReload(2f), Is.True); state.Tick(2f);
            Assert.That(state.Active.Magazine, Is.EqualTo(1));
            Assert.That(state.Active.Reserve, Is.EqualTo(3));
        }
        [Test] public void WholePackAcceptanceRequiresPositiveAmountAndEntireCapacity()
        {
            var state = new CombatKitState();
            state.ApplySeed(Seed(First, KitSeedReason.ClassApply, Pool(First, 0, 8)), true);
            Assert.That(state.CanAcceptReserve(8), Is.True, "exact capacity is accepted");
            Assert.That(state.CanAcceptReserve(9), Is.False);
            Assert.That(state.CanAcceptReserve(0), Is.False);
            Assert.That(state.CanAcceptReserve(-1), Is.False);
            Assert.That(state.CanAcceptReserve(int.MaxValue), Is.False, "overflow must not turn a huge pack into a valid grant");
            state.GrantReserve(7);
            Assert.That(state.Active.Reserve, Is.EqualTo(15));
            Assert.That(state.CanAcceptReserve(8), Is.False, "one spare slot cannot consume an eight-round pack");
            Assert.That(state.CanAcceptReserve(1), Is.True);
            state.GrantReserve(1);
            Assert.That(state.Active.Reserve, Is.EqualTo(16));
            Assert.That(state.CanAcceptReserve(1), Is.False);
        }
        [TestCase(0, 0)] [TestCase(2, 3)]
        public void ClassRestoreReplacesEarlierSeedWithExactZeroOrPartialCounts(int loaded, int reserve)
        {
            var state = new CombatKitState();
            state.ApplySeed(Seed(First, KitSeedReason.ClassApply, Pool(First, 5, 16, 5)), true);
            state.ApplySeed(Seed(Second, KitSeedReason.ClassApply, Pool(Second, loaded, reserve, 5)), false);
            Assert.That(state.Pools.ContainsKey(First), Is.False);
            Assert.That(state.ToView().AmmoPoolId, Is.EqualTo(Second));
            Assert.That(state.Active.Magazine, Is.EqualTo(loaded));
            Assert.That(state.Active.Reserve, Is.EqualTo(reserve));
        }
    }

    /// <summary>Real preparation, item ownership, final class seed, authority reload and settlement.
    /// Reflection only drives existing lifecycle/clock methods and installs the isolated profile session.
    /// No magazine fields or HostEditKit fixture values are written.</summary>
    public sealed class CarriedAmmoRuntimeTests
    {
        private const BindingFlags StaticPrivate = BindingFlags.Static | BindingFlags.NonPublic;
        private const BindingFlags InstancePrivate = BindingFlags.Instance | BindingFlags.NonPublic;
        private string _directory, _path;
        private OfflineProfile _profile;
        private CharacterClassData _character;
        private ItemCatalog _catalog;
        private GameConfig _config;
        private GameObject _actor, _authorityObject;
        private PlayerRig _rig;
        private LocalCombatAuthority _authority;
        private ICombatAuthority _previousAuthority;
        private NetRoleKind _previousRole;
        private float _now;
        private string WeaponId => _character.secondaryWeapon.weaponId;
        private int PreparedWeaponIndex => Array.FindIndex(_profile.Snapshot.Ready.equipment.entries,
            e => e.slot == EquipmentSlot.AltWeapon.ToString());
        private string PreparedUid => _profile.Snapshot.Ready.equipment.entries[PreparedWeaponIndex].item.uid;
        private static void ResetSession() => typeof(OfflineProfileSession).GetMethod("Reset", StaticPrivate).Invoke(null, null);
        private static void Lifecycle(object target, string name) => target.GetType().GetMethod(name, InstancePrivate).Invoke(target, null);
        [SetUp] public void Setup()
        {
            _previousRole = NetRole.Current;
            GameServices.TryGet(out _previousAuthority);
            NetRole.ResetToOffline(); ResetSession();
            _catalog = AssetDatabase.LoadAssetAtPath<ItemCatalog>("Assets/Emberdelve/Data/EmberdelveItemCatalog.asset");
            _character = AssetDatabase.LoadAssetAtPath<CharacterClassData>("Assets/Emberdelve/Data/Class_Warrior.asset");
            _config = AssetDatabase.LoadAssetAtPath<GameConfig>("Assets/Emberdelve/Data/EmberdelveGameConfig.asset");
            _directory = Path.Combine(Path.GetTempPath(), "EmberdelveCarriedAmmoTests", Guid.NewGuid().ToString("N"));
            _path = Path.Combine(_directory, "warrior.json");
            Assert.That(OfflineProfile.TryOpen(_path, _catalog, _character, out _profile, out var why), Is.True, why);
            typeof(OfflineProfileSession).GetField("<Current>k__BackingField", StaticPrivate).SetValue(null, _profile);
        }
        [TearDown] public void Cleanup()
        {
            if (_actor != null) UnityEngine.Object.DestroyImmediate(_actor);
            if (_authority != null) GameServices.Unregister<ICombatAuthority>(_authority);
            if (_authorityObject != null) UnityEngine.Object.DestroyImmediate(_authorityObject);
            if (_previousAuthority != null) GameServices.Register(_previousAuthority);
            ResetSession(); _profile?.Dispose(); NetRole.Set(_previousRole);
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }
        private void PrepareLoaded()
        {
            var pack = _catalog.Items.First(i => i.kind == ItemKind.AmmoPack && i.ammoGrant > 1 && i.MatchesAmmo(_character.secondaryWeapon));
            Assert.That(_profile.Buy(pack.itemId, _profile.Revision, out var why), Is.True, why);
            int index = Array.FindIndex(_profile.Snapshot.Stash.entries, e => e.item.itemId == pack.itemId);
            Assert.That(_profile.UnpackAmmo(PreparationArea.Stash, index, WeaponId, _profile.Revision, out why), Is.True, why);
            Assert.That(_profile.TransferAmmo(WeaponId, 3, false, _profile.Revision, out why), Is.True, why);
            Assert.That(_profile.LoadAmmo(PreparationArea.Equipment, PreparedWeaponIndex, _profile.Revision, out why), Is.True, why);
        }
        private void DepartAndCreateRig(bool switchToCrossbow = true)
        {
            Assert.That(OfflineProfileSession.Depart(out var why), Is.True, why);
            _authorityObject = new GameObject("CarriedAmmoAuthority");
            _authority = _authorityObject.AddComponent<LocalCombatAuthority>();
            GameServices.Register<ICombatAuthority>(_authority);
            _now = 10f; _authority.SetClock(() => _now);
            _actor = new GameObject("CarriedAmmoPlayer");
            _actor.AddComponent<Inventory>(); _actor.AddComponent<Equipment>();
            var loadout = _actor.AddComponent<ClassLoadout>();
            _rig = _actor.AddComponent<PlayerRig>();
            _actor.AddComponent<RangedCombat>();
            Lifecycle(_actor.GetComponent<Equipment>(), "Awake");
            Lifecycle(loadout, "Awake");
            Lifecycle(_actor.GetComponent<PlayerMovement>(), "Awake");
            _rig.ApplyClass(_character, _config);
            Assert.That(OfflineProfileSession.LastError, Is.Null);
            Assert.That(_authority.KitCount, Is.EqualTo(1), "the final class application must publish an authority record");
            if (switchToCrossbow) Assert.That(_rig.Loadout.SwitchToSlot(1), Is.True);
            Lifecycle(_authority, "TickKits"); // establish the injected authority clock's first sample
        }
        private KitView View()
        {
            Assert.That(_authority.TryGetKit(_rig.CombatId, out var view), Is.True);
            return view;
        }
        private OfflineAmmunition Capture() => _rig.Loadout.CapturePreparedAmmo(LoadoutSnapshot.Capture(_rig.Inventory, _rig.Equipment));
        private void Advance(float duration)
        {
            while (duration > 0f)
            {
                float step = Mathf.Min(0.125f, duration);
                _now += step; duration -= step; Lifecycle(_authority, "TickKits");
            }
        }
        [Test] public void PreparedUidAndAmmoReplaceClassSeedAndAuthorityReloadExtractConservesThem()
        {
            PrepareLoaded(); string uid = PreparedUid;
            int stored = _profile.Snapshot.Ammunition.StoredCount(WeaponId);
            DepartAndCreateRig();
            Assert.That(_rig.Equipment.MainHandInstance(1).UidText, Is.EqualTo(uid));
            Assert.That(View().AmmoPoolId, Is.EqualTo(uid));
            Assert.That(View().Magazine, Is.EqualTo(1));
            Assert.That(View().Reserve, Is.EqualTo(2), "class starting reserve must not override preparation");
            Assert.That(_actor.GetComponent<RangedCombat>().Magazine, Is.EqualTo(1));
            // Public sender-authorized round loss, not a fabricated write to the rig or authority record.
            Assert.That(_authority.TryForfeitRound(_rig.CombatSender, _rig.CombatId), Is.True);
            Assert.That(View().Magazine, Is.Zero);
            Assert.That(Capture().LoadedCount(uid), Is.Zero);
            Assert.That(_authority.TryStartReload(_rig.CombatSender, _rig.CombatId, out var why), Is.True, why);
            float duration = View().ReloadDuration;
            Advance(duration * 0.5f);
            Assert.That(View().Magazine, Is.Zero);
            Assert.That(View().Reserve, Is.EqualTo(2));
            Assert.That(Capture().LoadedCount(uid), Is.Zero);
            Advance(duration * 0.5f + 0.02f);
            Assert.That(View().Magazine, Is.EqualTo(1));
            Assert.That(View().Reserve, Is.EqualTo(1));
            Assert.That(OfflineProfileSession.Complete(true, _rig, out why), Is.True, why);
            Assert.That(OfflineProfile.TryOpen(_path, _catalog, _character, out var reopened, out why), Is.True, why);
            using (reopened)
            {
                Assert.That(reopened.Snapshot.Ammunition.LoadedCount(uid), Is.EqualTo(1));
                Assert.That(reopened.Snapshot.Ammunition.CarriedCount(WeaponId), Is.EqualTo(1));
                Assert.That(reopened.Snapshot.Ammunition.StoredCount(WeaponId), Is.EqualTo(stored));
                Assert.That(reopened.Snapshot.Ready.equipment.entries.Any(e => e.item.uid == uid), Is.True);
            }
        }
        [Test] public void EmptyPreparedWeaponHasZeroMagazineAndReserveInAuthorityAndCapture()
        {
            string uid = PreparedUid;
            DepartAndCreateRig();
            Assert.That(View().Magazine, Is.Zero);
            Assert.That(View().Reserve, Is.Zero);
            Assert.That(View().AmmoPoolId, Is.EqualTo(uid));
            Assert.That(_authority.TryStartReload(_rig.CombatSender, _rig.CombatId, out _), Is.False);
            Assert.That(Capture().loaded, Is.Empty);
            Assert.That(Capture().carried, Is.Empty);
        }
        [Test] public void FullyStoredPreparedKitDoesNotRetainGhostClassAmmunition()
        {
            Assert.That(_profile.StoreBag(_profile.Revision, out var why), Is.True, why);
            while (_profile.Snapshot.Ready.equipment.entries.Length > 0)
                Assert.That(_profile.Move(PreparationArea.Equipment, 0, PreparationArea.Stash, EquipmentSlot.None, null, _profile.Revision, out why), Is.True, why);
            DepartAndCreateRig(false);
            Assert.That(_rig.Inventory.TotalUnits, Is.Zero);
            Assert.That(View().HasActiveAmmo, Is.False);
            Assert.That(_rig.CaptureKitSeed(KitSeedReason.Weapon).Pools, Is.Empty);
            Assert.That(Capture().loaded, Is.Empty);
            Assert.That(Capture().carried, Is.Empty);
        }
        [Test] public void RealInventoryUseRefusesOversizedPackWithoutConsumingStackOrChangingRevision()
        {
            var pack = _catalog.Items.First(i => i.kind == ItemKind.AmmoPack && i.ammoGrant == 8 && i.MatchesAmmo(_character.secondaryWeapon));
            for (int i = 0; i < 2; i++)
            {
                Assert.That(_profile.Buy(pack.itemId, _profile.Revision, out var buyReason), Is.True, buyReason);
                int packIndex = Array.FindIndex(_profile.Snapshot.Stash.entries, e => e.item.itemId == pack.itemId);
                Assert.That(_profile.UnpackAmmo(PreparationArea.Stash, packIndex, WeaponId, _profile.Revision, out var unpackReason), Is.True, unpackReason);
            }
            int capacity = AmmoCarryRules.Capacity(_character, WeaponId);
            Assert.That(_profile.TransferAmmo(WeaponId, capacity - 1, false, _profile.Revision, out var why), Is.True, why);
            Assert.That(_profile.Buy(pack.itemId, _profile.Revision, out why), Is.True, why);
            int index = Array.FindIndex(_profile.Snapshot.Stash.entries, e => e.item.itemId == pack.itemId);
            Assert.That(_profile.Move(PreparationArea.Stash, index, PreparationArea.Bag, EquipmentSlot.None, null, _profile.Revision, out why), Is.True, why);
            DepartAndCreateRig();
            Assert.That(View().Reserve, Is.EqualTo(capacity - 1));
            var before = LoadoutSnapshot.Capture(_rig.Inventory, _rig.Equipment).ToJson();
            var ammunition = Capture().ToJson();
            ulong revision = _rig.Inventory.Revision;
            var slot = _rig.Inventory.Slots.First(s => s.Item == pack);
            Assert.That(_rig.Inventory.TryUseStack(slot.StackId), Is.False);
            Assert.That(_rig.Inventory.Revision, Is.EqualTo(revision));
            Assert.That(LoadoutSnapshot.Capture(_rig.Inventory, _rig.Equipment).ToJson(), Is.EqualTo(before));
            Assert.That(Capture().ToJson(), Is.EqualTo(ammunition));
            Assert.That(_rig.Inventory.TryGetStack(slot.StackId, out var retained), Is.True);
            Assert.That(retained.Count, Is.EqualTo(slot.Count));
            Assert.That(retained.Item, Is.SameAs(pack));
            Assert.That(retained.UidText, Is.EqualTo(slot.UidText));
        }
        [Test] public void LoadedMagazineFollowsUidThroughBagAndIdenticalEmptyReplacement()
        {
            PrepareLoaded(); string loadedUid = PreparedUid;
            var definition = _catalog.Items.First(i => i.grantedWeapon == _character.secondaryWeapon);
            Assert.That(_profile.Buy(definition.itemId, _profile.Revision, out var why), Is.True, why);
            int index = Array.FindIndex(_profile.Snapshot.Stash.entries, e => e.item.itemId == definition.itemId);
            string emptyUid = _profile.Snapshot.Stash.entries[index].item.uid;
            Assert.That(_profile.Move(PreparationArea.Stash, index, PreparationArea.Bag, EquipmentSlot.None, null, _profile.Revision, out why), Is.True, why);
            DepartAndCreateRig();
            Assert.That(_rig.Equipment.TryUnequip(EquipmentSlot.AltWeapon, out why), Is.True, why);
            Assert.That(Capture().LoadedCount(loadedUid), Is.EqualTo(1), "putting a loaded weapon in the bag preserves its magazine");
            index = _rig.Inventory.Slots.First(s => s.UidText == emptyUid).Index;
            Assert.That(_rig.Equipment.TryEquipFromInventory(index, EquipmentSlot.AltWeapon, out why), Is.True, why);
            if (_rig.Loadout.ActiveSlot != 1) Assert.That(_rig.Loadout.SwitchToSlot(1), Is.True);
            Assert.That(View().AmmoPoolId, Is.EqualTo(emptyUid));
            Assert.That(View().Magazine, Is.Zero, "an identical definition with another UID is not the loaded item");
            Assert.That(View().Reserve, Is.EqualTo(2));
            Assert.That(Capture().LoadedCount(loadedUid), Is.EqualTo(1));
            Assert.That(_rig.Equipment.TryUnequip(EquipmentSlot.AltWeapon, out why), Is.True, why);
            index = _rig.Inventory.Slots.First(s => s.UidText == loadedUid).Index;
            Assert.That(_rig.Equipment.TryEquipFromInventory(index, EquipmentSlot.AltWeapon, out why), Is.True, why);
            if (_rig.Loadout.ActiveSlot != 1) Assert.That(_rig.Loadout.SwitchToSlot(1), Is.True);
            Assert.That(View().AmmoPoolId, Is.EqualTo(loadedUid));
            Assert.That(View().Magazine, Is.EqualTo(1));
            Assert.That(View().Reserve, Is.EqualTo(2));
            Assert.That(Capture().LoadedCount(emptyUid), Is.Zero);
            Assert.That(OfflineProfileSession.Complete(true, _rig, out why), Is.True, why);
            Assert.That(_profile.Snapshot.Ammunition.LoadedCount(loadedUid), Is.EqualTo(1));
            Assert.That(_profile.Snapshot.Ammunition.LoadedCount(emptyUid), Is.Zero);
        }
    }
}
