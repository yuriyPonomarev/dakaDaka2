using System.Linq;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Items;
using Emberdelve.Player;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Emberdelve.Tests.Combat
{
    public sealed class NetworkLoadoutStateTests
    {
        private GameObject _sourceObject, _targetObject;
        private Inventory _source, _target;
        private Equipment _sourceGear, _targetGear;
        private NetRoleKind _priorRole;

        [SetUp]
        public void SetUp()
        {
            _priorRole = NetRole.Current;
            NetRole.ResetToOffline();
            var catalog = AssetDatabase.LoadAssetAtPath<ItemCatalog>("Assets/Emberdelve/Data/EmberdelveItemCatalog.asset");
            Assert.IsNotNull(catalog);
            // Usable also includes ammunition, which cannot occupy a physical utility pocket.
            // Require a heal explicitly so belt tests do not depend on catalog ordering.
            var utility = catalog.Items.FirstOrDefault(i => i != null && i.healAmount > 0 && QuickBeltRules.CanStore(i));
            Assert.IsNotNull(utility, "the fixture requires a physical-belt healing item");
            var bagItem = catalog.Items.FirstOrDefault(i => i != null && !i.IsEquippable && i != utility);
            Assert.IsNotNull(bagItem, "the fixture requires a distinct ordinary bag item");
            var items = new[] { utility, bagItem };
            _sourceObject = new GameObject("source");
            _source = _sourceObject.AddComponent<Inventory>();
            _sourceGear = _sourceObject.AddComponent<Equipment>();
            _source.SetCatalog(catalog);
            _source.ConfigureGrid(9, 4);
            Assert.IsTrue(_source.TryAdd(items[0], 1, out var reason), reason);
            Assert.IsTrue(_source.TryAdd(items[1], 1, out reason), reason);
            _targetObject = new GameObject("target");
            _target = _targetObject.AddComponent<Inventory>();
            _targetGear = _targetObject.AddComponent<Equipment>();
            _target.SetCatalog(catalog);
            _target.ConfigureGrid(9, 4);
        }

        [TearDown]
        public void TearDown()
        {
            NetRole.Set(_priorRole);
            Object.DestroyImmediate(_sourceObject);
            Object.DestroyImmediate(_targetObject);
        }

        [Test]
        public void ReplicatedHandsBecomeEmptyWithoutRestoringClassShieldOrMintingKit()
        {
            var config = AssetDatabase.LoadAssetAtPath<GameConfig>("Assets/Emberdelve/Data/EmberdelveGameConfig.asset");
            var warrior = AssetDatabase.LoadAssetAtPath<CharacterClassData>("Assets/Emberdelve/Data/Class_Warrior.asset");
            Assert.IsNotNull(warrior);
            var shield = _source.Catalog.Items.First(i => i != null && i.grantedWeapon == warrior.offhandShield);
            Assert.IsTrue(_source.TryMintKitInstance(shield, out var instance, out string reason), reason);
            var state = NetworkLoadoutState.Capture(_source, _sourceGear);
            state.Loadout.equipment.entries = new[] { new ContainerEntry { slot = "OffHand", item = instance.ToSnapshot() } };
            state.Loadout.Seal();

            NetRole.Set(NetRoleKind.Client);
            var loadout = _targetObject.AddComponent<ClassLoadout>();
            var rig = _targetObject.AddComponent<PlayerRig>();
            rig.InitializeReplicatedClass(warrior, config);
            Assert.AreEqual(0, _target.TotalUnits, "presentation initialization must not mint a kit");
            Assert.AreEqual(0, _targetGear.FilledCount);
            Assert.IsTrue(_targetGear.KitIsItems);
            Assert.IsTrue(state.TryApply(_target, _targetGear, out reason), reason);
            Assert.AreSame(warrior.offhandShield, rig.OffhandWeapon);

            state.Loadout.equipment.entries = new ContainerEntry[0];
            state.Loadout.Seal();
            state.Revision++;
            Assert.IsTrue(state.TryApply(_target, _targetGear, out reason), reason);
            Assert.IsNull(rig.OffhandWeapon, "empty server slot must clear the presented shield");
            Assert.AreSame(config.unarmedWeapon, loadout.ActiveWeapon);
        }

        [Test]
        public void AppliesServerIdsAndRevisionBeforeNotifying_RejectsStale()
        {
            var state = NetworkLoadoutState.Capture(_source, _sourceGear);
            NetRole.Set(NetRoleKind.Client);
            bool observedFinal = false;
            System.Action<int> onChanged = _ => observedFinal = _target.Revision == state.Revision &&
                _target.Slots.All(s => state.Stacks.Any(x => x.X == s.Origin.x && x.Y == s.Origin.y && x.Id == s.StackId));
            GameEvents.InventoryChanged += onChanged;
            try { Assert.IsTrue(state.TryApply(_target, _targetGear, out string reason), reason); }
            finally { GameEvents.InventoryChanged -= onChanged; }
            Assert.IsTrue(observedFinal);
            Assert.AreEqual(state.Revision, _target.Revision);
            Assert.AreEqual(state.Stacks.Length, _target.Slots.Count);
            Assert.IsFalse(state.TryApply(_target, _targetGear, out _));
        }

        [TestCase(105f, false)]
        [TestCase(0f, true)]
        public void LoadoutRefreshPreservesServerVitalsAndDeath(float currentHealth, bool dead)
        {
            var config = AssetDatabase.LoadAssetAtPath<GameConfig>("Assets/Emberdelve/Data/EmberdelveGameConfig.asset");
            var warrior = AssetDatabase.LoadAssetAtPath<CharacterClassData>("Assets/Emberdelve/Data/Class_Warrior.asset");
            var state = NetworkLoadoutState.Capture(_source, _sourceGear);
            NetRole.Set(NetRoleKind.Client);
            _targetObject.AddComponent<ClassLoadout>();
            var rig = _targetObject.AddComponent<PlayerRig>();
            rig.InitializeReplicatedClass(warrior, config);
            Assert.IsTrue(rig.Health.ApplyReplicatedState(currentHealth, 120f, dead));

            Assert.IsTrue(state.TryApply(_target, _targetGear, out string reason), reason);
            Assert.AreEqual(120f, rig.Health.MaxHealth, "loadout must not override server limits");
            Assert.AreEqual(currentHealth, rig.Health.CurrentHealth, "loadout must neither clamp damage nor heal");
            Assert.AreEqual(dead, rig.Health.IsDead);
        }

        [Test]
        public void InvalidIdentityMapLeavesExistingLoadoutUntouched()
        {
            var state = NetworkLoadoutState.Capture(_source, _sourceGear);
            Assert.GreaterOrEqual(state.Stacks.Length, 2);
            state.Stacks[1].Id = state.Stacks[0].Id;
            string before = LoadoutSnapshot.Capture(_target, _targetGear).ToJson();
            ulong revision = _target.Revision;
            NetRole.Set(NetRoleKind.Client);
            Assert.IsFalse(state.TryApply(_target, _targetGear, out _));
            Assert.AreEqual(before, LoadoutSnapshot.Capture(_target, _targetGear).ToJson());
            Assert.AreEqual(revision, _target.Revision);
        }

        [Test]
        public void OfflineCannotApplyNetworkState()
        {
            var state = NetworkLoadoutState.Capture(_source, _sourceGear);
            Assert.IsFalse(state.TryApply(_target, _targetGear, out _));
        }

        [Test]
        public void CorruptedLoadoutCannotPartiallyApply()
        {
            var state = NetworkLoadoutState.Capture(_source, _sourceGear);
            state.Loadout.bag.entries[0].item.quantity += 1;
            string before = LoadoutSnapshot.Capture(_target, _targetGear).ToJson();
            ulong revision = _target.Revision;
            NetRole.Set(NetRoleKind.Client);
            Assert.IsFalse(state.TryApply(_target, _targetGear, out _));
            Assert.AreEqual(before, LoadoutSnapshot.Capture(_target, _targetGear).ToJson());
            Assert.AreEqual(revision, _target.Revision);
        }

        [Test]
        public void NewSessionAcceptsLowerServerRevision()
        {
            var state = NetworkLoadoutState.Capture(_source, _sourceGear);
            NetRole.Set(NetRoleKind.Client);
            Assert.IsTrue(state.TryApply(_target, _targetGear, out string reason), reason);
            state.Revision = 0;
            Assert.IsFalse(state.TryApply(_target, _targetGear, out _));
            _target.BeginNetworkSession();
            Assert.IsTrue(state.TryApply(_target, _targetGear, out reason), reason);
            Assert.AreEqual(0, _target.Revision);
        }

        [Test]
        public void HeldSnapshotUsesServerBeltAndRejectsMissingHeldIdentity()
        {
            var usable = _source.Slots.First(s => QuickBeltRules.CanStore(s.Item));
            Assert.IsTrue(_source.TryMove(usable.Index, _source.BeltCell(5), out string reason), reason);
            var state = NetworkLoadoutState.Capture(_source, _sourceGear);
            state.HeldStackId = usable.StackId;
            NetRole.Set(NetRoleKind.Client);
            Assert.IsTrue(state.TryApply(_target, _targetGear, out reason), reason);
            Assert.AreEqual(usable.StackId, _target.HeldStackId);
            Assert.AreSame(usable.Item, _target.HeldItem);
            Assert.AreEqual(usable.StackId, _target.Hotbar.StackIdAt(5));
            Assert.AreEqual(_source.UsedCells, _target.UsedCells);
            state.Revision++;
            state.HeldStackId = ulong.MaxValue;
            Assert.IsFalse(state.TryApply(_target, _targetGear, out _));
            Assert.AreEqual(usable.StackId, _target.HeldStackId);
            Assert.AreEqual(usable.StackId, _target.Hotbar.StackIdAt(5));
        }

        [Test]
        public void LaterServerSnapshotMovesBeltItemBackToBagWithoutLocalRebinding()
        {
            var usable = _source.Slots.First(s => QuickBeltRules.CanStore(s.Item));
            var oldCell = usable.Origin;
            Assert.IsTrue(_source.TryMove(usable.Index, _source.BeltCell(2), out string reason), reason);
            var state = NetworkLoadoutState.Capture(_source, _sourceGear);
            NetRole.Set(NetRoleKind.Client);
            Assert.IsTrue(state.TryApply(_target, _targetGear, out reason), reason);
            Assert.AreEqual(usable.StackId, _target.Hotbar.StackIdAt(2));
            NetRole.ResetToOffline();
            Assert.IsTrue(_source.TryMove(_source.IndexOfStack(usable.StackId), oldCell, out reason), reason);
            state = NetworkLoadoutState.Capture(_source, _sourceGear);
            NetRole.Set(NetRoleKind.Client);
            Assert.IsTrue(state.TryApply(_target, _targetGear, out reason), reason);
            Assert.AreEqual(0, _target.Hotbar.StackIdAt(2));
            Assert.AreEqual(oldCell, _target.Slots.Single(s => s.StackId == usable.StackId).Origin);
            Assert.AreEqual(_source.TotalUnits, _target.TotalUnits);
        }

        [Test]
        public void HeldUtilityOutsidePhysicalBeltCannotMutateClientLoadout()
        {
            var usable = _source.Slots.First(s => QuickBeltRules.CanStore(s.Item));
            var state = NetworkLoadoutState.Capture(_source, _sourceGear);
            state.HeldStackId = usable.StackId;
            string before = LoadoutSnapshot.Capture(_target, _targetGear).ToJson();
            NetRole.Set(NetRoleKind.Client);
            Assert.IsFalse(state.TryApply(_target, _targetGear, out _));
            Assert.AreEqual(before, LoadoutSnapshot.Capture(_target, _targetGear).ToJson());
            Assert.AreEqual(0, _target.HeldStackId);
        }

        [Test]
        public void HotbarRebindsToServerStackId()
        {
            var usable = _source.Slots.First(s => QuickBeltRules.CanStore(s.Item));
            Assert.IsTrue(_source.TryMove(usable.Index, _source.BeltCell(0), out string reason), reason);
            var state = NetworkLoadoutState.Capture(_source, _sourceGear);
            for (int i = 0; i < state.Stacks.Length; i++) state.Stacks[i].Id += 1000;
            NetRole.Set(NetRoleKind.Client);
            Assert.IsTrue(state.TryApply(_target, _targetGear, out reason), reason);
            Assert.AreEqual(usable.StackId + 1000, _target.Hotbar.StackIdAt(0));
        }
    }
}
