using System;
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
    /// <summary>Two real rigs share one authority. Only ground staging is a test double;
    /// item commands, bag acceptance, equipment, ammunition and corpse transfers use runtime rules.</summary>
    public sealed class AmmoOwnershipRuntimeTests
    {
        private sealed class Ground : IGroundItemSink
        {
            public ItemInstance Item;
            public bool FailPublication;
            public bool Cancelled;
            private sealed class Pending : IPreparedGroundDrop
            {
                private readonly Ground _ground;
                private readonly ItemInstance _item;
                private bool _published;
                public Pending(Ground ground, ItemInstance item) { _ground = ground; _item = item; }
                public void Publish()
                {
                    if (_ground.FailPublication) throw new InvalidOperationException("injected publication failure");
                    _ground.Item = _item; _published = true;
                }
                public void Cancel() { if (ReferenceEquals(_ground.Item, _item)) _ground.Item = null; _ground.Cancelled = true; _published = false; }
                public void Dispose() { if (!_published) Cancel(); }
            }
            public bool TryPrepareDropped(ItemInstance instance, Vector3 position, out IPreparedGroundDrop prepared, out string reason)
            {
                prepared = null;
                if (Item != null) { reason = "test ground already occupied"; return false; }
                Cancelled = false; prepared = new Pending(this, instance); reason = null; return true;
            }
            public ItemInstance TakeInto(Inventory inventory)
            {
                Assert.That(Item, Is.Not.Null, "only a published drop may be picked up");
                var item = Item;
                Assert.That(inventory.TryAddInstance(item, out var why), Is.True, why);
                Item = null;
                return item;
            }
        }
        private const BindingFlags InstancePrivate = BindingFlags.Instance | BindingFlags.NonPublic;
        private GameObject _authorityObject;
        private PlayerRig _a, _b;
        private LocalCombatAuthority _authority;
        private ICombatAuthority _previousAuthority;
        private IGroundItemSink _previousGround;
        private NetRoleKind _previousRole;
        private Ground _ground;
        private CharacterClassData _character;
        private GameConfig _config;
        private float _now;
        private static void Lifecycle(object component, string method) => component.GetType().GetMethod(method, InstancePrivate).Invoke(component, null);
        [SetUp] public void Setup()
        {
            Assert.That(OfflineProfileSession.HasRaid, Is.False, "this fixture must not consume another fixture's departure");
            _previousRole = NetRole.Current; NetRole.ResetToOffline();
            GameServices.TryGet(out _previousAuthority);
            _previousGround = LocalInventoryAuthority.GroundSinkOverride;
            _ground = new Ground(); LocalInventoryAuthority.GroundSinkOverride = _ground;
            _character = AssetDatabase.LoadAssetAtPath<CharacterClassData>("Assets/Emberdelve/Data/Class_Warrior.asset");
            _config = AssetDatabase.LoadAssetAtPath<GameConfig>("Assets/Emberdelve/Data/EmberdelveGameConfig.asset");
            _authorityObject = new GameObject("AmmoOwnershipAuthority");
            _authority = _authorityObject.AddComponent<LocalCombatAuthority>();
            GameServices.Register<ICombatAuthority>(_authority);
            _now = 10f; _authority.SetClock(() => _now);
            _a = Actor("AmmoOwnerA"); _b = Actor("AmmoOwnerB");
            Assert.That(_a.Equipment.MainHandInstance(1).Uid, Is.Not.EqualTo(_b.Equipment.MainHandInstance(1).Uid));
            Assert.That(_authority.KitCount, Is.EqualTo(2));
            Lifecycle(_authority, "TickKits");
        }
        private PlayerRig Actor(string name)
        {
            var actor = new GameObject(name);
            actor.AddComponent<Inventory>(); actor.AddComponent<Equipment>();
            var loadout = actor.AddComponent<ClassLoadout>();
            var rig = actor.AddComponent<PlayerRig>(); actor.AddComponent<RangedCombat>();
            Lifecycle(actor.GetComponent<Equipment>(), "Awake");
            Lifecycle(loadout, "Awake"); Lifecycle(actor.GetComponent<PlayerMovement>(), "Awake");
            rig.ApplyClass(_character, _config);
            Assert.That(rig.Loadout.SwitchToSlot(1), Is.True);
            Assert.That(View(rig).Magazine, Is.EqualTo(_character.secondaryWeapon.magazineSize));
            return rig;
        }
        [TearDown] public void Cleanup()
        {
            LocalInventoryAuthority.GroundSinkOverride = _previousGround;
            if (_a != null) DestroyActor(_a);
            if (_b != null) DestroyActor(_b);
            if (_authority != null) GameServices.Unregister<ICombatAuthority>(_authority);
            if (_authorityObject != null) UnityEngine.Object.DestroyImmediate(_authorityObject);
            if (_previousAuthority != null) GameServices.Register(_previousAuthority);
            NetRole.Set(_previousRole);
        }
        private static void DestroyActor(PlayerRig rig)
        {
            // EditMode does not drive the ordinary MonoBehaviour destruction lifecycle.
            Lifecycle(rig, "OnDestroy");
            UnityEngine.Object.DestroyImmediate(rig.gameObject);
        }
        private KitView View(PlayerRig rig)
        {
            Assert.That(_authority.TryGetKit(rig.CombatId, out var view), Is.True);
            return view;
        }
        private static OfflineAmmunition Capture(PlayerRig rig) => rig.Loadout.CapturePreparedAmmo(LoadoutSnapshot.Capture(rig.Inventory, rig.Equipment));
        private InventoryCommandResult Drop(PlayerRig rig, ItemInstance item, bool fromBag)
        {
            ulong stackId = fromBag ? rig.Inventory.Slots.Single(s => s.UidText == item.UidText).StackId : 0;
            var authority = rig.Inventory.CommandAuthority;
            Assert.That(authority.TryCreateLocalCommand(InventoryCommandKind.Drop, stackId, 0, Vector2Int.zero,
                fromBag ? EquipmentSlot.None : EquipmentSlot.AltWeapon, item.Uid, Guid.Empty,
                out var command, out var why), Is.True, why);
            return authority.Submit(rig, command);
        }
        private void Equip(PlayerRig rig, ItemInstance item)
        {
            int index = rig.Inventory.Slots.Single(s => s.UidText == item.UidText).Index;
            Assert.That(rig.Equipment.TryEquipFromInventory(index, EquipmentSlot.AltWeapon, out var why), Is.True, why);
            if (rig.Loadout.ActiveSlot != 1) Assert.That(rig.Loadout.SwitchToSlot(1), Is.True);
            Assert.That(View(rig).AmmoPoolId, Is.EqualTo(item.UidText));
            Assert.That(rig.GetComponent<RangedCombat>().Magazine, Is.EqualTo(View(rig).Magazine),
                "same-frame re-equip must display the current authority magazine, not this actor's older cached value");
            Assert.That(rig.GetComponent<RangedCombat>().Reserve, Is.EqualTo(View(rig).Reserve));
        }
        private void LoseRound(PlayerRig rig)
        {
            int before = View(rig).Magazine;
            Assert.That(before, Is.GreaterThan(0));
            Assert.That(_authority.TryForfeitRound(rig.CombatSender, rig.CombatId), Is.True);
            Assert.That(View(rig).Magazine, Is.EqualTo(before - 1));
        }
        private void Advance(float seconds)
        {
            while (seconds > 0f)
            {
                float step = Mathf.Min(0.125f, seconds); seconds -= step; _now += step;
                Lifecycle(_authority, "TickKits");
            }
        }
        [TestCase(false)] [TestCase(true)]
        public void SameActorRepickKeepsExactLoadedUidWithoutMovingLooseReserve(bool fromBag)
        {
            var item = _a.Equipment.MainHandInstance(1);
            int reserve = View(_a).Reserve;
            if (fromBag) Assert.That(_a.Equipment.TryUnequip(EquipmentSlot.AltWeapon, out var why), Is.True, why);
            Assert.That(Drop(_a, item, fromBag).Accepted, Is.True);
            Assert.That(_ground.Item, Is.SameAs(item));
            Assert.That(Capture(_a).LoadedCount(item.UidText), Is.Zero, "an unowned dropped UID must not be exported");
            Assert.That(_ground.TakeInto(_a.Inventory), Is.SameAs(item));
            Equip(_a, item);
            Assert.That(View(_a).Magazine, Is.EqualTo(1));
            Assert.That(View(_a).Reserve, Is.EqualTo(reserve));
        }
        [Test] public void DifferentActorSpendReloadAndReturnCannotRevivePreviousHolderMagazine()
        {
            var item = _a.Equipment.MainHandInstance(1);
            var other = _b.Equipment.MainHandInstance(1);
            int aReserve = View(_a).Reserve, bReserve = View(_b).Reserve;
            Assert.That(Drop(_a, item, false).Accepted, Is.True);
            _ground.TakeInto(_b.Inventory); Equip(_b, item);
            Assert.That(View(_b).Magazine, Is.EqualTo(1));
            Assert.That(View(_b).Reserve, Is.EqualTo(bReserve), "loose reserve belongs to the receiving actor");
            LoseRound(_b);
            Assert.That(_authority.TryStartReload(_b.CombatSender, _b.CombatId, out var why), Is.True, why);
            float duration = View(_b).ReloadDuration;
            Advance(duration * 0.5f);
            Assert.That(Capture(_b).LoadedCount(item.UidText), Is.Zero);
            Advance(duration * 0.5f + 0.02f);
            Assert.That(View(_b).Magazine, Is.EqualTo(1));
            Assert.That(View(_b).Reserve, Is.EqualTo(bReserve - 1));
            LoseRound(_b);
            Assert.That(Drop(_b, item, false).Accepted, Is.True);
            _ground.TakeInto(_a.Inventory); Equip(_a, item);
            Assert.That(View(_a).Magazine, Is.Zero, "A's pre-drop cached one round must not override B's latest zero");
            Assert.That(View(_a).Reserve, Is.EqualTo(aReserve));
            Assert.That(Capture(_b).LoadedCount(item.UidText), Is.Zero);
            Assert.That(Capture(_b).LoadedCount(other.UidText), Is.EqualTo(1), "B's displaced same-definition weapon keeps its own magazine");
        }
        [Test] public void RecipientCanCaptureLoadedBagOnlyPickupWithoutEverEquippingIt()
        {
            var item = _a.Equipment.MainHandInstance(1);
            string bActive = View(_b).AmmoPoolId;
            Assert.That(Drop(_a, item, false).Accepted, Is.True);
            _ground.TakeInto(_b.Inventory);
            Assert.That(View(_b).AmmoPoolId, Is.EqualTo(bActive));
            Assert.That(_b.CaptureKitSeed(KitSeedReason.Weapon).Pools.Any(p => p.PoolId == item.UidText), Is.False,
                "the receiver has never seeded a local cache for this bag-only UID");
            Assert.That(Capture(_b).LoadedCount(item.UidText), Is.EqualTo(1));
            Assert.That(Capture(_a).LoadedCount(item.UidText), Is.Zero);
            // Bag drop uses the same ownership transaction and retains the same global magazine.
            Assert.That(Drop(_b, item, true).Accepted, Is.True);
            Assert.That(Capture(_b).LoadedCount(item.UidText), Is.Zero);
            _ground.TakeInto(_a.Inventory); Equip(_a, item);
            Assert.That(View(_a).Magazine, Is.EqualTo(1));
        }
        [Test] public void CorpseSnapshotTransferKeepsLoadedUidAfterSourceRigIsGone()
        {
            var item = _a.Equipment.MainHandInstance(1);
            Assert.That(CorpseLootManifest.TryCapture(_a.PlayerId, Vector3.zero, _a.Inventory, _a.Equipment,
                out var manifest, out var why), Is.True, why);
            Assert.That(CorpseContainerModel.TryCreate(manifest, out var corpse, out why), Is.True, why);
            var entry = corpse.CopyEntries().Single(e => e.Uid == item.UidText);
            // This fixture exercises the committed corpse model, not RunDirector's death UI.
            // Remove the original owner before delivery; retaining it would fabricate duplicate ownership.
            DestroyActor(_a); _a = null;
            Advance(0.125f);
            var registry = new ItemUidRegistry();
            var recipient = LoadoutSnapshot.Capture(_b.Inventory, _b.Equipment);
            Assert.That(registry.TryReplaceOwner(Inventory.SnapshotContainerId,
                recipient.bag.entries.Where(e => !string.IsNullOrEmpty(e.item.uid)).Select(e => Guid.Parse(e.item.uid)).ToArray(), out why), Is.True, why);
            Assert.That(registry.TryReplaceOwner(Equipment.SnapshotContainerId,
                recipient.equipment.entries.Where(e => !string.IsNullOrEmpty(e.item.uid)).Select(e => Guid.Parse(e.item.uid)).ToArray(), out why), Is.True, why);
            Assert.That(corpse.TryTransferToBag(entry.EntryId, 1, corpse.Revision, _b.Inventory, _b.Inventory.Revision,
                registry, out why), Is.True, why);
            Assert.That(Capture(_b).LoadedCount(item.UidText), Is.EqualTo(1));
            Assert.That(corpse.TryTransferToBag(entry.EntryId, 1, corpse.Revision, _b.Inventory, _b.Inventory.Revision,
                registry, out _), Is.False, "the corpse can deliver its UID only once");
            Equip(_b, item);
            Assert.That(View(_b).Magazine, Is.EqualTo(1));
        }
        [Test] public void NewAuthorityAndActorsDoNotInheritThePreviousRunUidRegistry()
        {
            string oldUid = _a.Equipment.MainHandInstance(1).UidText;
            Assert.That(_authority.TryGetLoadedRounds(oldUid, _character.secondaryWeapon.weaponId, out var oldLoaded), Is.True);
            Assert.That(oldLoaded, Is.EqualTo(1));
            DestroyActor(_a); _a = null;
            DestroyActor(_b); _b = null;
            UnityEngine.Object.DestroyImmediate(_authorityObject);
            _authorityObject = new GameObject("NextRunAmmoAuthority");
            _authority = _authorityObject.AddComponent<LocalCombatAuthority>();
            GameServices.Register<ICombatAuthority>(_authority); _authority.SetClock(() => _now);
            Assert.That(_authority.TryGetLoadedRounds(oldUid, _character.secondaryWeapon.weaponId, out _), Is.False);
            _a = Actor("NextRunAmmoOwner");
            Assert.That(_a.Equipment.MainHandInstance(1).UidText, Is.Not.EqualTo(oldUid));
            Assert.That(_authority.TryGetLoadedRounds(oldUid, _character.secondaryWeapon.weaponId, out _), Is.False);
            Assert.That(Capture(_a).LoadedCount(oldUid), Is.Zero);
        }
        [TestCase(false)] [TestCase(true)]
        public void FailedPublicationRollsBackExactItemAndLoadedMagazine(bool fromBag)
        {
            var item = _a.Equipment.MainHandInstance(1);
            if (fromBag) Assert.That(_a.Equipment.TryUnequip(EquipmentSlot.AltWeapon, out var why), Is.True, why);
            string before = LoadoutSnapshot.Capture(_a.Inventory, _a.Equipment).ToJson();
            string ammo = Capture(_a).ToJson();
            ulong revision = _a.Inventory.Revision;
            _ground.FailPublication = true;
            var result = Drop(_a, item, fromBag);
            Assert.That(result.Accepted, Is.False); Assert.That(result.Changed, Is.False);
            Assert.That(_ground.Cancelled, Is.True); Assert.That(_ground.Item, Is.Null);
            Assert.That(_a.Inventory.Revision, Is.EqualTo(revision));
            Assert.That(LoadoutSnapshot.Capture(_a.Inventory, _a.Equipment).ToJson(), Is.EqualTo(before));
            Assert.That(Capture(_a).ToJson(), Is.EqualTo(ammo));
            Assert.That(Capture(_a).LoadedCount(item.UidText), Is.EqualTo(1));
            if (fromBag) Equip(_a, item);
            Assert.That(View(_a).Magazine, Is.EqualTo(1));
        }
    }
}

