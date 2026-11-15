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
    public sealed class HeldTorchCombatTests
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
                    if (_ground.FailPublication) throw new System.InvalidOperationException("injected torch ground publication failure");
                    _ground.Item = _item;
                    _published = true;
                }
                public void Cancel()
                {
                    if (ReferenceEquals(_ground.Item, _item)) _ground.Item = null;
                    _ground.Cancelled = true;
                    _published = false;
                }
                public void Dispose() { if (!_published) Cancel(); }
            }
            public bool TryPrepareDropped(ItemInstance item, Vector3 position, out IPreparedGroundDrop prepared, out string reason)
            {
                prepared = new Pending(this, item);
                reason = null;
                return true;
            }
        }
        private GameObject _authorityObject;
        private LocalCombatAuthority _authority;
        private ICombatAuthority _previousAuthority;
        private NetRoleKind _previousRole;
        private PlayerRig _rig;
        private ItemData _torch;
        private CharacterClassData _character;
        private float _now;
        private IGroundItemSink _previousGround;
        private Ground _ground;
        private static void Lifecycle(object component, string method) => component.GetType()
            .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(component, null);

        [SetUp] public void Setup()
        {
            Assert.That(OfflineProfileSession.HasRaid, Is.False, "do not consume another fixture's prepared raid");
            _previousRole = NetRole.Current;
            NetRole.ResetToOffline();
            GameServices.TryGet(out _previousAuthority);
            _previousGround = LocalInventoryAuthority.GroundSinkOverride;
            _ground = new Ground();
            LocalInventoryAuthority.GroundSinkOverride = _ground;
            _authorityObject = new GameObject("HeldTorchAuthority");
            _authority = _authorityObject.AddComponent<LocalCombatAuthority>();
            GameServices.Register<ICombatAuthority>(_authority);
            _now = 10f;
            _authority.SetClock(() => _now);
            _character = AssetDatabase.LoadAssetAtPath<CharacterClassData>("Assets/Emberdelve/Data/Class_Warrior.asset");
            var config = AssetDatabase.LoadAssetAtPath<GameConfig>("Assets/Emberdelve/Data/EmberdelveGameConfig.asset");
            _torch = AssetDatabase.LoadAssetAtPath<ItemData>("Assets/Emberdelve/Data/Item_PitchTorch.asset");
            Assert.That(_torch, Is.Not.Null);
            var actor = new GameObject("HeldTorchActor");
            actor.AddComponent<Inventory>();
            actor.AddComponent<Equipment>();
            var loadout = actor.AddComponent<ClassLoadout>();
            _rig = actor.AddComponent<PlayerRig>();
            var melee = actor.AddComponent<MeleeCombat>();
            actor.AddComponent<RangedCombat>();
            Lifecycle(actor.GetComponent<Equipment>(), "Awake");
            Lifecycle(melee, "Awake");
            Lifecycle(loadout, "Awake");
            Lifecycle(actor.GetComponent<PlayerMovement>(), "Awake");
            _rig.ApplyClass(_character, config);
            Assert.That(_authority.KitCount, Is.EqualTo(1));
            Lifecycle(_authority, "TickKits");
        }

        [TearDown] public void Cleanup()
        {
            LocalInventoryAuthority.GroundSinkOverride = _previousGround;
            if (_rig != null)
            {
                Lifecycle(_rig, "OnDestroy");
                Object.DestroyImmediate(_rig.gameObject);
            }
            if (_authority != null) GameServices.Unregister<ICombatAuthority>(_authority);
            if (_authorityObject != null) Object.DestroyImmediate(_authorityObject);
            if (_previousAuthority != null) GameServices.Register(_previousAuthority);
            NetRole.Set(_previousRole);
        }

        private ulong AddStack(ItemData item)
        {
            var before = _rig.Inventory.Slots.Select(s => s.StackId).ToArray();
            Assert.That(_rig.Inventory.TryAdd(item, 1, out var why), Is.True, why);
            return _rig.Inventory.Slots.First(s => s.Item == item && !before.Contains(s.StackId)).StackId;
        }

        private ulong TorchStack() => _rig.Inventory.Slots.Any(s => s.Item == _torch)
            ? _rig.Inventory.Slots.First(s => s.Item == _torch).StackId : AddStack(_torch);

        private void Hold(ulong stack)
        {
            if (_rig.Inventory.Hotbar.SlotOf(stack) < 0)
            {
                int free = Enumerable.Range(0, QuickBeltRules.SlotCount).First(i => _rig.Inventory.Hotbar.StackIdAt(i) == 0);
                Assert.That(_rig.Inventory.Hotbar.TryAssign(free, stack, out var placement), Is.True, placement);
            }
            Assert.That(_rig.Inventory.RequestHoldStack(stack, out var why), Is.True, why);
            Assert.That(_rig.Inventory.HeldStackId, Is.EqualTo(stack));
        }

        private void Stow(int set)
        {
            Assert.That(_rig.Inventory.RequestWeaponSet(set, out var why), Is.True, why);
            Assert.That(_rig.Inventory.HeldStackId, Is.Zero);
        }

        private KitView Kit()
        {
            Assert.That(_authority.TryGetKit(_rig.CombatId, out var view), Is.True);
            return view;
        }

        [Test] public void HeldTorchUsesItsOwnMeleeWithoutMovingBagOrEquipment()
        {
            ulong stack = TorchStack();
            string before = LoadoutSnapshot.Capture(_rig.Inventory, _rig.Equipment).ToJson();
            var main = _rig.Equipment.MainHandInstance(0);
            int epoch = _rig.WeaponChangeCount;
            Hold(stack);
            Assert.That(_rig.ActiveWeapon, Is.SameAs(_torch.grantedWeapon));
            Assert.That(_rig.HeldMeleeWeapon, Is.SameAs(_torch.grantedWeapon));
            Assert.That(_rig.GetComponent<MeleeCombat>().Weapon, Is.SameAs(_torch.grantedWeapon));
            Assert.That(_rig.GetComponent<MeleeCombat>().enabled, Is.True);
            Assert.That(_rig.GetComponent<RangedCombat>().enabled, Is.False);
            Assert.That(_rig.WeaponInputSuppressed, Is.False);
            Assert.That(_rig.CanGuard, Is.False);
            _rig.SetGuardHeld(true);
            Assert.That(_rig.IsGuarding, Is.False);
            Assert.That(_rig.WeaponChangeCount, Is.GreaterThan(epoch));
            Assert.That(_rig.Equipment.MainHandInstance(0), Is.SameAs(main));
            Assert.That(LoadoutSnapshot.Capture(_rig.Inventory, _rig.Equipment).ToJson(), Is.EqualTo(before));
            Stow(0);
            Assert.That(_rig.ActiveWeapon, Is.SameAs(_character.primaryWeapon));
            Assert.That(_rig.HeldMeleeWeapon, Is.Null);
            Assert.That(LoadoutSnapshot.Capture(_rig.Inventory, _rig.Equipment).ToJson(), Is.EqualTo(before));
        }

        [Test] public void DifferentTorchIdentityChangesEpochButRepeatedSelectionDoesNot()
        {
            ulong first = TorchStack(), second = AddStack(_torch);
            Assert.That(second, Is.Not.EqualTo(first));
            var firstUid = _rig.Inventory.Slots.Single(s => s.StackId == first).UidText;
            var secondUid = _rig.Inventory.Slots.Single(s => s.StackId == second).UidText;
            Assert.That(secondUid, Is.Not.EqualTo(firstUid));
            Hold(first);
            int epoch = _rig.WeaponChangeCount;
            Hold(first);
            Assert.That(_rig.WeaponChangeCount, Is.EqualTo(epoch));
            Hold(second);
            Assert.That(_rig.WeaponChangeCount, Is.GreaterThan(epoch));
            Assert.That(_rig.ActiveWeapon, Is.SameAs(_torch.grantedWeapon));
            Assert.That(_rig.Inventory.Slots.Single(s => s.StackId == first).UidText, Is.EqualTo(firstUid));
            Assert.That(_rig.Inventory.Slots.Single(s => s.StackId == second).UidText, Is.EqualTo(secondUid));
        }

        [TestCase(false)] [TestCase(true)]
        public void ConsumablesNeverAcquireTorchMelee(bool ammunition)
        {
            var item = _rig.Inventory.Catalog.Items.First(i => i != null &&
                (ammunition ? i.ammoGrant > 0 : i.healAmount > 0));
            ulong stack = _rig.Inventory.Slots.Any(s => s.Item == item)
                ? _rig.Inventory.Slots.First(s => s.Item == item).StackId : AddStack(item);
            ulong torch = TorchStack();
            Hold(torch);
            if (ammunition)
            {
                string before = _rig.Inventory.ToContainerSnapshot().ToJson();
                Assert.That(_rig.Inventory.RequestHoldStack(stack, out _), Is.False, "ammunition is bag supply, not a held utility");
                Assert.That(_rig.Inventory.Hotbar.TryAssign(5, stack, out _), Is.False);
                Assert.That(_rig.Inventory.HeldStackId, Is.EqualTo(torch), "refused supply selection does not change the held torch");
                Assert.That(_rig.Inventory.ToContainerSnapshot().ToJson(), Is.EqualTo(before));
                Stow(1);
                Assert.That(_rig.HeldMeleeWeapon, Is.Null);
                Assert.That(_rig.ActiveWeapon, Is.SameAs(_character.secondaryWeapon));
                return;
            }
            Hold(stack);
            Assert.That(_rig.HeldMeleeWeapon, Is.Null);
            Assert.That(_rig.ActiveWeapon, Is.Null);
            Assert.That(_rig.WeaponInputSuppressed, Is.True);
            Assert.That(_rig.GetComponent<MeleeCombat>().TryBeginSwing(), Is.False);
            Assert.That(_rig.CanGuard, Is.False);
        }

        [Test] public void TorchRoundTripPreservesSpentMagazineAndLooseReserve()
        {
            Stow(1);
            var weapon = _rig.Equipment.MainHandInstance(1);
            int reserve = Kit().Reserve;
            Assert.That(Kit().Magazine, Is.GreaterThan(0));
            Assert.That(_authority.TryForfeitRound(_rig.CombatSender, _rig.CombatId), Is.True);
            int magazine = Kit().Magazine;
            ulong torch = TorchStack();
            for (int repeat = 0; repeat < 2; repeat++)
            {
                Hold(torch);
                Assert.That(_rig.ActiveWeapon, Is.SameAs(_torch.grantedWeapon));
                Assert.That(_rig.Equipment.MainHandInstance(1), Is.SameAs(weapon));
                Stow(1);
                Assert.That(_rig.ActiveWeapon, Is.SameAs(_character.secondaryWeapon));
                Assert.That(Kit().Magazine, Is.EqualTo(magazine));
                Assert.That(Kit().Reserve, Is.EqualTo(reserve));
                Assert.That(_rig.GetComponent<RangedCombat>().Magazine, Is.EqualTo(magazine));
                Assert.That(_rig.GetComponent<RangedCombat>().Reserve, Is.EqualTo(reserve));
            }
        }

        [Test] public void BagAmmoAfterTorchRequiresDrawnRangedWeaponAndPreservesMagazine()
        {
            Stow(1);
            var selected = _rig.ActiveWeapon;
            var pack = _rig.Inventory.Catalog.Items.First(i => i != null && i.ammoGrant > 0 && i.MatchesAmmo(selected));
            ulong stack = _rig.Inventory.Slots.Any(s => s.Item == pack)
                ? _rig.Inventory.Slots.First(s => s.Item == pack).StackId : AddStack(pack);
            var ranged = _rig.GetComponent<RangedCombat>();
            int reserve = Kit().Reserve, magazine = Kit().Magazine;
            Assert.That((long)reserve + pack.ammoGrant, Is.LessThanOrEqualTo((long)ranged.MaxReserve),
                "authored starter reserve leaves room for one whole compatible pack");
            int packs = _rig.Inventory.Slots.Where(s => s.Item == pack).Sum(s => s.Count);
            ulong torch = TorchStack(); Hold(torch);
            ulong revision = _rig.Inventory.Revision;
            Assert.That(_rig.Inventory.RequestHoldStack(stack, out _), Is.False);
            Assert.That(_rig.Inventory.TryUseStack(stack), Is.False, "a torch is not the matching drawn ranged weapon");
            Assert.That(_rig.Inventory.Revision, Is.EqualTo(revision));
            Assert.That(_rig.Inventory.HeldStackId, Is.EqualTo(torch));
            Assert.That(_rig.Inventory.Slots.Where(s => s.Item == pack).Sum(s => s.Count), Is.EqualTo(packs));
            Assert.That(Kit().Reserve, Is.EqualTo(reserve));
            Stow(1);
            Assert.That(_rig.ActiveWeapon, Is.SameAs(selected));
            Assert.That(ranged.Weapon, Is.SameAs(selected));
            Assert.That(_rig.Inventory.TryUseStack(stack), Is.True);
            Assert.That(_rig.Inventory.Slots.Where(s => s.Item == pack).Sum(s => s.Count), Is.EqualTo(packs - 1));
            Assert.That(Kit().Reserve, Is.EqualTo(reserve + pack.ammoGrant));
            Assert.That(Kit().Magazine, Is.EqualTo(magazine));
            Stow(1);
            Assert.That(ranged.Reserve, Is.EqualTo(reserve + pack.ammoGrant));
            Assert.That(ranged.Magazine, Is.EqualTo(magazine));
        }

        [TestCase(false)] [TestCase(true)]
        public void HeldTorchGroundDropCommitsCombatTransitionOnlyAfterPublication(bool failPublication)
        {
            ulong stack = TorchStack();
            Hold(stack);
            var item = _rig.Inventory.Slots.Single(s => s.StackId == stack).Instance;
            var melee = _rig.GetComponent<MeleeCombat>();
            Assert.That(melee.TryBeginSwing(), Is.True);
            int swing = melee.LastSwingId, epoch = _rig.WeaponChangeCount;
            ulong revision = _rig.Inventory.Revision;
            string before = LoadoutSnapshot.Capture(_rig.Inventory, _rig.Equipment).ToJson();
            var underlying = _rig.Equipment.MainHandInstance(0);
            _ground.FailPublication = failPublication;
            var authority = _rig.Inventory.CommandAuthority;
            Assert.That(authority.TryCreateLocalCommand(InventoryCommandKind.Drop, stack, 0, Vector2Int.zero,
                EquipmentSlot.None, item.Uid, System.Guid.Empty, out var command, out var why), Is.True, why);
            var result = authority.Submit(_rig, command);
            Assert.That(result.Accepted, Is.EqualTo(!failPublication), result.Reason);
            Assert.That(_rig.Equipment.MainHandInstance(0), Is.SameAs(underlying));
            if (failPublication)
            {
                Assert.That(result.Changed, Is.False);
                Assert.That(_ground.Cancelled, Is.True);
                Assert.That(_ground.Item, Is.Null);
                Assert.That(_rig.Inventory.HeldStackId, Is.EqualTo(stack));
                Assert.That(_rig.Inventory.Slots.Single(s => s.StackId == stack).Instance, Is.SameAs(item));
                Assert.That(_rig.Inventory.Revision, Is.EqualTo(revision));
                Assert.That(LoadoutSnapshot.Capture(_rig.Inventory, _rig.Equipment).ToJson(), Is.EqualTo(before));
                Assert.That(_rig.WeaponChangeCount, Is.EqualTo(epoch));
                Assert.That(_rig.ActiveWeapon, Is.SameAs(_torch.grantedWeapon));
                Assert.That(melee.Weapon, Is.SameAs(_torch.grantedWeapon));
                Assert.That(melee.LastSwingId, Is.EqualTo(swing), "failed publication must not reinitialize combat");
            }
            else
            {
                Assert.That(result.Changed, Is.True);
                Assert.That(_ground.Item, Is.SameAs(item));
                Assert.That(_rig.Inventory.HeldStackId, Is.Zero);
                Assert.That(_rig.Inventory.Slots.Any(s => s.UidText == item.UidText), Is.False);
                Assert.That(_rig.WeaponChangeCount, Is.GreaterThan(epoch), "drop commits the epoch before returning");
                Assert.That(_rig.ActiveWeapon, Is.SameAs(_character.primaryWeapon));
                Assert.That(melee.Weapon, Is.SameAs(_character.primaryWeapon), "no Update or extra combat intent is needed to restore the hand");
            }
            _now += .01f;
            _authority.StepSwings();
            Assert.That(_authority.TryGetMeleeSwingStatus(swing, out var status), Is.True);
            Assert.That(status.Finished, Is.EqualTo(!failPublication));
            Assert.That(status.EndReason, Is.EqualTo(failPublication ? SwingEndReason.None : SwingEndReason.WeaponChanged));
        }

        [Test] public void ReturningHeldTorchToBagStowsItUntilExplicitlyDrawnAgain()
        {
            ulong torch = TorchStack(); Hold(torch);
            int pocket = _rig.Inventory.Hotbar.SlotOf(torch);
            Assert.That(_rig.Inventory.TryGetStack(torch, out var utility), Is.True);
            var free = _rig.Inventory.FindFreeOrigin(utility.Item.EffectiveFootprint);
            Assert.That(_rig.Inventory.TryMove(utility.Index, free, out var reason), Is.True, reason);
            Assert.That(_rig.Inventory.HeldStackId, Is.Zero);
            Assert.That(_rig.Inventory.Hotbar.TryAssign(pocket, torch, out reason), Is.True, reason);
            Assert.That(_rig.Inventory.HeldStackId, Is.Zero, "returning to belt must not silently draw it again");
        }
        [Test] public void OrdinaryAuthorityAcceptsTorchSwingAndLocksIdentityUntilAttackEnds()
        {
            ulong first = TorchStack(), second = AddStack(_torch);
            Hold(second); // Both identities are physically eligible; the refusal below proves the attack lock.
            Hold(first);
            var melee = _rig.GetComponent<MeleeCombat>();
            Assert.That(melee.TryBeginSwing(), Is.True, "the real authority accepts the held torch's authored attack");
            Assert.That(melee.ActivePattern, Is.SameAs(_torch.grantedWeapon.swingPatterns[0]));
            Assert.That(_authority.TryGetMeleeSwingStatus(melee.LastSwingId, out var status), Is.True);
            Assert.That(status.Finished, Is.False);
            int epoch = _rig.WeaponChangeCount;
            Assert.That(_rig.Inventory.RequestHoldStack(second, out _), Is.False, "public identity changes remain attack-locked");
            Assert.That(_rig.Inventory.RequestWeaponSet(0, out _), Is.False, "stowing must not bypass an accepted swing lock");
            Assert.That(_rig.Inventory.HeldStackId, Is.EqualTo(first));
            Assert.That(_rig.WeaponChangeCount, Is.EqualTo(epoch));
        }
    }
}
