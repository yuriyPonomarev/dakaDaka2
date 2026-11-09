using System;
using System.Reflection;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Items;
using Emberdelve.Player;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Emberdelve.Tests.NetInventory
{
    public sealed class InventoryDropRollbackTests
    {
        private sealed class RefusingGround : IGroundItemSink
        {
            public ItemInstance Seen;

            public bool TryPrepareDropped(ItemInstance instance, Vector3 position, out IPreparedGroundDrop prepared, out string reason)
            {
                Seen = instance;
                prepared = null;
                reason = "test refusal";
                return false;
            }
        }

        private sealed class PreparedDrop : IPreparedGroundDrop
        {
            public bool ThrowOnPublish;
            public bool Published;
            public bool Canceled;
            public void Publish()
            {
                if (ThrowOnPublish) throw new InvalidOperationException("injected publish failure");
                Published = true;
            }
            public void Cancel() { Canceled = true; Published = false; }
            public void Dispose() { if (!Published) Cancel(); }
        }

        private sealed class StagingGround : IGroundItemSink
        {
            public ItemInstance Seen;
            public Action DuringPrepare;
            public PreparedDrop Drop;
            public bool RefusePrepare;
            public bool ThrowOnPublish;
            public bool TryPrepareDropped(ItemInstance instance, Vector3 position, out IPreparedGroundDrop prepared, out string reason)
            {
                Seen = instance;
                DuringPrepare?.Invoke();
                if (RefusePrepare)
                {
                    prepared = null;
                    reason = "test preparation refusal";
                    return false;
                }
                Drop = new PreparedDrop { ThrowOnPublish = ThrowOnPublish };
                prepared = Drop;
                reason = null;
                return true;
            }
        }

        [Test]
        public void RefusedBagDrop_RestoresExactStackWithoutRevisionOrObserverGap()
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
            FieldInfo sinkField = typeof(LocalInventoryAuthority).GetField("GroundSinkOverride", flags);
            Assert.That(sinkField, Is.Not.Null);

            NetRoleKind previousRole = NetRole.Current;
            object previousSink = sinkField.GetValue(null);
            GameObject owner = null;
            Action<int> observer = null;
            var sink = new RefusingGround();
            try
            {
                NetRole.ResetToOffline();
                owner = new GameObject("InventoryDropRollback_Test");
                owner.SetActive(false);
                Inventory inventory = owner.AddComponent<Inventory>();
                Equipment equipment = owner.AddComponent<Equipment>();
                PlayerRig rig = owner.AddComponent<PlayerRig>();
                owner.SetActive(true);
                rig.WireRig();
                inventory.Initialize(rig);
                equipment.Initialize(rig);

                ItemCatalog catalog = AssetDatabase.LoadAssetAtPath<ItemCatalog>(
                    "Assets/Emberdelve/Data/EmberdelveItemCatalog.asset");
                Assert.That(catalog, Is.Not.Null);
                ItemData definition = null;
                foreach (ItemData candidate in catalog.Items)
                    if (candidate != null && candidate.IsEquippable) { definition = candidate; break; }
                Assert.That(definition, Is.Not.Null, "the production catalog supplies an equippable item");

                inventory.SetCatalog(catalog);
                inventory.SetIssuer(ItemIdentityIssuer.CreateLocalTrusted());
                Assert.That(catalog.TryMintCommon(inventory.Issuer, definition, out ItemInstance instance, out string mintReason),
                    Is.True, mintReason);
                Assert.That(inventory.TryAddInstance(instance, out string addReason), Is.True, addReason);
                InventorySlot before = inventory.Slots[0];
                ulong revision = inventory.Revision;
                int recovered = inventory.RecoveredValue;
                int collected = inventory.LifetimeCollected;
                int total = inventory.TotalUnits;
                int carriedValue = inventory.CarriedValue;

                int notifications = 0;
                bool observerSawExactItem = false;
                observer = playerId =>
                {
                    notifications++;
                    observerSawExactItem = inventory.HoldsInstanceUid(instance.Uid) &&
                        inventory.TryGetStack(before.StackId, out InventorySlot observed) &&
                        ReferenceEquals(observed.Instance, instance);
                };
                GameEvents.InventoryChanged += observer;
                sinkField.SetValue(null, sink);

                IInventoryAuthority authority = inventory.CommandAuthority;
                Assert.That(authority.TryCreateLocalCommand(InventoryCommandKind.Drop, before.StackId, 0,
                    Vector2Int.zero, EquipmentSlot.None, instance.Uid, Guid.Empty,
                    out InventoryCommand command, out string commandReason), Is.True, commandReason);
                InventoryCommandResult result = authority.Submit(rig, command);

                Assert.That(result.Accepted, Is.False, "the ground refusal rejects the intent");
                Assert.That(result.Changed, Is.False);
                Assert.That(sink.Seen, Is.SameAs(instance), "the sink received the exact instance, not a reroll or copy");
                Assert.That(inventory.Revision, Is.EqualTo(revision));
                Assert.That(inventory.RecoveredValue, Is.EqualTo(recovered));
                Assert.That(inventory.LifetimeCollected, Is.EqualTo(collected));
                Assert.That(inventory.TotalUnits, Is.EqualTo(total));
                Assert.That(inventory.CarriedValue, Is.EqualTo(carriedValue));
                Assert.That(inventory.Slots, Has.Count.EqualTo(1));
                Assert.That(inventory.Slots[0].StackId, Is.EqualTo(before.StackId));
                Assert.That(inventory.Slots[0].Origin, Is.EqualTo(before.Origin));
                Assert.That(inventory.Slots[0].Instance, Is.SameAs(instance));
                Assert.That(notifications, Is.Zero, "a failed transfer publishes no temporary removal or rollback snapshot");
                Assert.That(observerSawExactItem, Is.False);
            }
            finally
            {
                if (observer != null) GameEvents.InventoryChanged -= observer;
                sinkField.SetValue(null, previousSink);
                NetRole.Set(previousRole);
                if (owner != null) UnityEngine.Object.DestroyImmediate(owner);
            }
        }

        [Test]
        public void EquipmentDrop_PreparationRefusalKeepsExactWornUidAndRevision()
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
            FieldInfo sinkField = typeof(LocalInventoryAuthority).GetField("GroundSinkOverride", flags);
            Assert.That(sinkField, Is.Not.Null);
            NetRoleKind previousRole = NetRole.Current;
            object previousSink = sinkField.GetValue(null);
            GameObject owner = null;
            Action<int> inventoryObserver = null;
            Action<int, ItemData, bool> equipmentObserver = null;
            var sink = new StagingGround { RefusePrepare = true };
            try
            {
                NetRole.ResetToOffline();
                owner = new GameObject("EquipmentDropPrepareRefusal_Test");
                owner.SetActive(false);
                Inventory inventory = owner.AddComponent<Inventory>();
                Equipment equipment = owner.AddComponent<Equipment>();
                PlayerRig rig = owner.AddComponent<PlayerRig>();
                owner.SetActive(true);
                rig.WireRig(); inventory.Initialize(rig); equipment.Initialize(rig);
                ItemCatalog catalog = AssetDatabase.LoadAssetAtPath<ItemCatalog>("Assets/Emberdelve/Data/EmberdelveItemCatalog.asset");
                Assert.That(catalog, Is.Not.Null);
                ItemData definition = null;
                foreach (ItemData candidate in catalog.Items)
                    if (candidate != null && candidate.IsEquippable && candidate.equipSlot != EquipmentSlot.None) { definition = candidate; break; }
                Assert.That(definition, Is.Not.Null);
                inventory.SetCatalog(catalog); inventory.SetIssuer(ItemIdentityIssuer.CreateLocalTrusted());
                Assert.That(catalog.TryMintCommon(inventory.Issuer, definition, out ItemInstance instance, out string mintReason), Is.True, mintReason);
                Assert.That(inventory.TryAddInstance(instance, out string addReason), Is.True, addReason);
                InventorySlot bag = inventory.Slots[0];
                IInventoryAuthority authority = inventory.CommandAuthority;
                Assert.That(authority.TryCreateLocalCommand(InventoryCommandKind.Equip, bag.StackId, 0, Vector2Int.zero,
                    definition.equipSlot, instance.Uid, Guid.Empty, out InventoryCommand equipCommand, out string commandReason), Is.True, commandReason);
                Assert.That(authority.Submit(rig, equipCommand).Accepted, Is.True);
                ulong revision = inventory.Revision;
                int inventoryEvents = 0, equipmentEvents = 0;
                inventoryObserver = _ => inventoryEvents++;
                equipmentObserver = (_, __, ___) => equipmentEvents++;
                GameEvents.InventoryChanged += inventoryObserver;
                GameEvents.EquipmentChanged += equipmentObserver;
                sinkField.SetValue(null, sink);

                Assert.That(authority.TryCreateLocalCommand(InventoryCommandKind.Drop, 0, 0, Vector2Int.zero,
                    definition.equipSlot, instance.Uid, Guid.Empty, out InventoryCommand dropCommand, out commandReason), Is.True, commandReason);
                InventoryCommandResult result = authority.Submit(rig, dropCommand);

                Assert.That(result.Accepted, Is.False);
                Assert.That(result.Changed, Is.False);
                Assert.That(sink.Seen, Is.SameAs(instance));
                Assert.That(equipment.EquippedInstance(definition.equipSlot), Is.SameAs(instance));
                Assert.That(inventory.HoldsInstanceUid(instance.Uid), Is.False);
                Assert.That(inventory.Revision, Is.EqualTo(revision));
                Assert.That(inventoryEvents, Is.Zero);
                Assert.That(equipmentEvents, Is.Zero);
            }
            finally
            {
                if (inventoryObserver != null) GameEvents.InventoryChanged -= inventoryObserver;
                if (equipmentObserver != null) GameEvents.EquipmentChanged -= equipmentObserver;
                sinkField.SetValue(null, previousSink); NetRole.Set(previousRole);
                if (owner != null) UnityEngine.Object.DestroyImmediate(owner);
            }
        }

        [TestCase(true, false, false)]
        [TestCase(false, false, false)]
        [TestCase(false, true, false)]
        [TestCase(true, false, true)]
        public void EquipmentDrop_FinalOutcomesAreAtomicAndPrepareReentryIsBlocked(bool throwOnPublish, bool throwOnFinalize, bool throwOnRollback)
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
            FieldInfo sinkField = typeof(LocalInventoryAuthority).GetField("GroundSinkOverride", flags);
            FieldInfo finalizeFaultField = typeof(Equipment).GetField("DropFinalizeFaultForTest", flags);
            FieldInfo rollbackFaultField = typeof(Equipment).GetField("DropRollbackFaultForTest", flags);
            NetRoleKind previousRole = NetRole.Current;
            object previousSink = sinkField.GetValue(null);
            object previousFinalizeFault = finalizeFaultField.GetValue(null);
            object previousRollbackFault = rollbackFaultField.GetValue(null);
            GameObject owner = null;
            Action<int> inventoryObserver = null;
            Action<int, ItemData, bool> equipmentObserver = null;
            bool observerSawFinalState = false;
            try
            {
                NetRole.ResetToOffline();
                owner = new GameObject("EquipmentDropPublishRollback_Test"); owner.SetActive(false);
                Inventory inventory = owner.AddComponent<Inventory>();
                Equipment equipment = owner.AddComponent<Equipment>();
                PlayerRig rig = owner.AddComponent<PlayerRig>(); owner.SetActive(true);
                rig.WireRig(); inventory.Initialize(rig); equipment.Initialize(rig);
                ItemCatalog catalog = AssetDatabase.LoadAssetAtPath<ItemCatalog>("Assets/Emberdelve/Data/EmberdelveItemCatalog.asset");
                ItemData definition = null;
                foreach (ItemData candidate in catalog.Items)
                    if (candidate != null && candidate.IsEquippable && candidate.equipSlot != EquipmentSlot.None) { definition = candidate; break; }
                Assert.That(definition, Is.Not.Null);
                inventory.SetCatalog(catalog); inventory.SetIssuer(ItemIdentityIssuer.CreateLocalTrusted());
                Assert.That(catalog.TryMintCommon(inventory.Issuer, definition, out ItemInstance instance, out string mintReason), Is.True, mintReason);
                Assert.That(inventory.TryAddInstance(instance, out string addReason), Is.True, addReason);
                InventorySlot bag = inventory.Slots[0];
                IInventoryAuthority authority = inventory.CommandAuthority;
                Assert.That(authority.TryCreateLocalCommand(InventoryCommandKind.Equip, bag.StackId, 0, Vector2Int.zero,
                    definition.equipSlot, instance.Uid, Guid.Empty, out InventoryCommand equipCommand, out string commandReason), Is.True, commandReason);
                Assert.That(authority.Submit(rig, equipCommand).Accepted, Is.True);
                ulong revision = inventory.Revision;
                WeaponData activeBefore = rig.Loadout != null ? rig.Loadout.ActiveWeapon : null;
                WeaponData offhandBefore = rig.OffhandWeapon;
                int weaponChangesBefore = rig.Loadout != null ? rig.Loadout.WeaponChangeCount : 0;
                var sink = new StagingGround();
                ItemInstance unrelated = null;
                Assert.That(catalog.TryMintCommon(inventory.Issuer, definition, out unrelated, out mintReason), Is.True, mintReason);
                MethodInfo beginKit = typeof(Equipment).GetMethod("BeginKit", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.That(beginKit, Is.Not.Null);
                bool reentryAccepted = true;
                bool equipmentReentryAccepted = true;
                bool kitReentryBlocked = false;
                bool equipmentPairPreserved = false;
                sink.DuringPrepare = () =>
                {
                    reentryAccepted = inventory.TryAddInstance(unrelated, out _);
                    equipmentReentryAccepted = equipment.TryUnequip(definition.equipSlot, out _);
                    beginKit.Invoke(equipment, null);
                    kitReentryBlocked = !equipment.KitIsItems;
                    equipment.Initialize(null);
                    equipmentPairPreserved = equipment.IsPairedWith(inventory);
                };
                sink.ThrowOnPublish = throwOnPublish;
                int inventoryEvents = 0, equipmentEvents = 0;
                inventoryObserver = _ =>
                {
                    inventoryEvents++;
                    observerSawFinalState = equipment.EquippedInstance(definition.equipSlot) == null &&
                        !inventory.HoldsInstanceUid(instance.Uid) && sink.Drop != null && sink.Drop.Published &&
                        inventory.Revision == revision + 1;
                };
                equipmentObserver = (_, __, ___) => equipmentEvents++;
                GameEvents.InventoryChanged += inventoryObserver;
                GameEvents.EquipmentChanged += equipmentObserver;
                finalizeFaultField.SetValue(null, throwOnFinalize
                    ? (Action)(() => throw new InvalidOperationException("injected finalization failure"))
                    : null);
                rollbackFaultField.SetValue(null, throwOnRollback
                    ? (Action)(() => throw new InvalidOperationException("injected rollback after restoration"))
                    : null);
                sinkField.SetValue(null, sink);

                Assert.That(authority.TryCreateLocalCommand(InventoryCommandKind.Drop, 0, 0, Vector2Int.zero,
                    definition.equipSlot, instance.Uid, Guid.Empty, out InventoryCommand dropCommand, out commandReason), Is.True, commandReason);
                InventoryCommandResult result = authority.Submit(rig, dropCommand);

                Assert.That(reentryAccepted, Is.False, "the shared revision lease rejects direct mutations during sink preparation");
                Assert.That(equipmentReentryAccepted, Is.False, "equipment mutation APIs share the same revision lease");
                Assert.That(kitReentryBlocked, Is.True, "kit seeding cannot bypass the equipment lease");
                Assert.That(equipmentPairPreserved, Is.True, "a reentrant Initialize cannot change the reserved equipment owner");
                bool success = !throwOnPublish && !throwOnFinalize && !throwOnRollback;
                Assert.That(result.Accepted, Is.EqualTo(success));
                Assert.That(result.Changed, Is.EqualTo(success));
                Assert.That(sink.Drop.Canceled, Is.EqualTo(!success));
                Assert.That(sink.Drop.Published, Is.EqualTo(success));
                Assert.That(equipment.EquippedInstance(definition.equipSlot), Is.EqualTo(success ? null : instance));
                Assert.That(inventory.Revision, Is.EqualTo(revision + (success ? 1UL : 0UL)));
                Assert.That(inventory.HoldsInstanceUid(instance.Uid), Is.False);
                Assert.That(inventory.HoldsInstanceUid(unrelated.Uid), Is.False);
                Assert.That(inventoryEvents, Is.EqualTo(success ? 1 : 0));
                Assert.That(equipmentEvents, Is.EqualTo(success ? 1 : 0));
                Assert.That(observerSawFinalState, Is.EqualTo(success), "observers run only after the ground and source state have settled");
                if (throwOnRollback)
                    Assert.That(result.Reason, Does.Contain("injected rollback after restoration"));
                if (!success)
                {
                    Assert.That(rig.Loadout != null ? rig.Loadout.WeaponChangeCount : 0, Is.EqualTo(weaponChangesBefore));
                    Assert.That(rig.Loadout != null ? rig.Loadout.ActiveWeapon : null, Is.SameAs(activeBefore));
                    Assert.That(rig.OffhandWeapon, Is.SameAs(offhandBefore));
                }
            }
            finally
            {
                if (inventoryObserver != null) GameEvents.InventoryChanged -= inventoryObserver;
                if (equipmentObserver != null) GameEvents.EquipmentChanged -= equipmentObserver;
                finalizeFaultField.SetValue(null, previousFinalizeFault);
                rollbackFaultField.SetValue(null, previousRollbackFault);
                sinkField.SetValue(null, previousSink); NetRole.Set(previousRole);
                if (owner != null) UnityEngine.Object.DestroyImmediate(owner);
            }
        }

        [TestCase(EquipmentSlot.Weapon, false)]
        [TestCase(EquipmentSlot.OffHand, false)]
        [TestCase(EquipmentSlot.Weapon, true)]
        [TestCase(EquipmentSlot.OffHand, true)]
        public void EquipmentDrop_FinalizationFailureRestoresExactMainOffhandAndKitState(EquipmentSlot slot, bool itemKit)
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
            FieldInfo sinkField = typeof(LocalInventoryAuthority).GetField("GroundSinkOverride", flags);
            FieldInfo finalizeFaultField = typeof(Equipment).GetField("DropFinalizeFaultForTest", flags);
            FieldInfo rollbackFaultField = typeof(Equipment).GetField("DropRollbackFaultForTest", flags);
            FieldInfo kitField = typeof(Equipment).GetField("_kitIsItems", BindingFlags.Instance | BindingFlags.NonPublic);
            NetRoleKind previousRole = NetRole.Current;
            object previousSink = sinkField.GetValue(null);
            object previousFinalizeFault = finalizeFaultField.GetValue(null);
            object previousRollbackFault = rollbackFaultField.GetValue(null);
            GameObject owner = null;
            try
            {
                NetRole.ResetToOffline();
                owner = new GameObject("EquipmentDropHandRollback_Test"); owner.SetActive(false);
                Inventory inventory = owner.AddComponent<Inventory>();
                Equipment equipment = owner.AddComponent<Equipment>();
                PlayerRig rig = owner.AddComponent<PlayerRig>(); owner.SetActive(true);
                rig.WireRig(); inventory.Initialize(rig); equipment.Initialize(rig);
                ItemCatalog catalog = AssetDatabase.LoadAssetAtPath<ItemCatalog>("Assets/Emberdelve/Data/EmberdelveItemCatalog.asset");
                Assert.That(catalog, Is.Not.Null);
                ItemData definition = null;
                foreach (ItemData candidate in catalog.Items)
                    if (candidate != null && candidate.IsEquippable && candidate.equipSlot == slot && candidate.GrantsWeapon)
                    { definition = candidate; break; }
                Assert.That(definition, Is.Not.Null, "catalog contains a concrete weapon or shield for the requested hand");
                inventory.SetCatalog(catalog); inventory.SetIssuer(ItemIdentityIssuer.CreateLocalTrusted());
                Assert.That(catalog.TryMintCommon(inventory.Issuer, definition, out ItemInstance instance, out string mintReason), Is.True, mintReason);
                Assert.That(inventory.TryAddInstance(instance, out string addReason), Is.True, addReason);
                InventorySlot bag = inventory.Slots[0];
                IInventoryAuthority authority = inventory.CommandAuthority;
                Assert.That(authority.TryCreateLocalCommand(InventoryCommandKind.Equip, bag.StackId, 0, Vector2Int.zero,
                    slot, instance.Uid, Guid.Empty, out InventoryCommand equipCommand, out string commandReason), Is.True, commandReason);
                Assert.That(authority.Submit(rig, equipCommand).Accepted, Is.True);
                ulong revision = inventory.Revision;
                WeaponData activeBefore = rig.Loadout != null ? rig.Loadout.ActiveWeapon : null;
                WeaponData offhandBefore = rig.OffhandWeapon;
                int weaponChangesBefore = rig.Loadout != null ? rig.Loadout.WeaponChangeCount : 0;
                kitField.SetValue(equipment, itemKit);
                finalizeFaultField.SetValue(null, (Action)(() => throw new InvalidOperationException("injected post-hand finalization failure")));
                var sink = new StagingGround(); sinkField.SetValue(null, sink);

                Assert.That(authority.TryCreateLocalCommand(InventoryCommandKind.Drop, 0, 0, Vector2Int.zero,
                    slot, instance.Uid, Guid.Empty, out InventoryCommand dropCommand, out commandReason), Is.True, commandReason);
                InventoryCommandResult result = authority.Submit(rig, dropCommand);

                Assert.That(result.Accepted, Is.False);
                Assert.That(result.Changed, Is.False);
                Assert.That(equipment.EquippedInstance(slot), Is.SameAs(instance));
                Assert.That(inventory.Revision, Is.EqualTo(revision));
                Assert.That(rig.Loadout != null ? rig.Loadout.ActiveWeapon : null, Is.SameAs(activeBefore));
                Assert.That(rig.OffhandWeapon, Is.SameAs(offhandBefore));
                Assert.That(rig.Loadout != null ? rig.Loadout.WeaponChangeCount : 0, Is.EqualTo(weaponChangesBefore));
                Assert.That(sink.Drop.Canceled, Is.True);
                Assert.That(sink.Drop.Published, Is.False);
            }
            finally
            {
                finalizeFaultField.SetValue(null, previousFinalizeFault);
                rollbackFaultField.SetValue(null, previousRollbackFault);
                sinkField.SetValue(null, previousSink); NetRole.Set(previousRole);
                if (owner != null) UnityEngine.Object.DestroyImmediate(owner);
            }
        }

    }
}
