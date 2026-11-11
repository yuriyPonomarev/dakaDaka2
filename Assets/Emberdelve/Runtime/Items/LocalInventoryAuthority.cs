using System;
using Emberdelve.Combat;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Flow;
using Emberdelve.Player;
using UnityEngine;

namespace Emberdelve.Items
{
    /// <summary>
    /// Bound local gameplay authority for one exact rig and its bag/equipment pair. It validates client intents
    /// against the live model; this is not a network transport or a remote-player trust boundary.
    /// </summary>
    public sealed class LocalInventoryAuthority : IInventoryAuthority
    {
        private readonly PlayerRig _rig;
        private readonly Inventory _inventory;
        private readonly Equipment _equipment;
        private ulong _lastSequence;
        private ulong _nextLocalSequence = 1;
        private bool _busy;

        internal LocalInventoryAuthority(PlayerRig rig, Inventory inventory, Equipment equipment)
        {
            _rig = rig;
            _inventory = inventory;
            _equipment = equipment;
        }

        public bool TryCreateLocalCommand(InventoryCommandKind kind, ulong sourceStackId, ulong targetStackId,
            Vector2Int destinationCell, EquipmentSlot destinationSlot, Guid expectedSourceGearUid,
            Guid expectedTargetGearUid, out InventoryCommand command, out string reason)
        {
            command = default;
            if (_rig == null || _inventory == null)
            {
                reason = "inventory authority has no bound player rig and bag";
                return false;
            }

            if (_nextLocalSequence == 0 || _lastSequence == ulong.MaxValue)
            {
                reason = "inventory command sequence space is exhausted";
                return false;
            }

            ulong sequence = _nextLocalSequence;
            if (sequence <= _lastSequence) sequence = _lastSequence + 1;
            command = new InventoryCommand(kind, _rig.PlayerId, sequence, _inventory.Revision,
                sourceStackId, targetStackId, destinationCell, destinationSlot,
                expectedSourceGearUid, expectedTargetGearUid);
            if (!command.IsStructurallyValid(out reason))
            {
                command = default;
                return false;
            }

            _nextLocalSequence = sequence == ulong.MaxValue ? 0 : sequence + 1;
            reason = null;
            return true;
        }

        public InventoryCommandResult Submit(PlayerRig requester, InventoryCommand command)
        {
            if (NetRole.IsClientOnly) return Refused("inventory changes require the server");
            if (_busy) return Refused("inventory authority is already processing a command");
            if (requester == null || _rig == null || !ReferenceEquals(requester, _rig))
                return Refused("requester is not the player bound to this inventory authority");
            if (command.OwnerPlayerId != _rig.PlayerId)
                return Refused("inventory command owner does not match the bound player");
            if (!command.IsStructurallyValid(out string structuralReason)) return Refused(structuralReason);
            if (command.Sequence <= _lastSequence)
                return Refused("inventory command sequence was replayed or arrived out of order");

            // A structurally valid new sequence is consumed before stale-revision and business checks.
            _lastSequence = command.Sequence;
            _nextLocalSequence = command.Sequence == ulong.MaxValue ? 0 :
                Math.Max(_nextLocalSequence, command.Sequence + 1);
            _busy = true;
            ulong before = _inventory != null ? _inventory.Revision : 0;
            try
            {
                if (!ValidateLiveOwner(out string reason)) return Refused(reason);
                if (command.ExpectedRevision != _inventory.Revision)
                    return Refused("inventory revision is stale");

                bool accepted = Execute(command, out reason);
                ulong after = _inventory.Revision;
                return new InventoryCommandResult(accepted, accepted && after != before, after, reason);
            }
            finally
            {
                _busy = false;
            }
        }

        private bool ValidateLiveOwner(out string reason)
        {
            if (_rig == null || _inventory == null || _equipment == null ||
                !ReferenceEquals(_rig.Inventory, _inventory) || !ReferenceEquals(_rig.Equipment, _equipment))
            {
                reason = "the bound player loadout is no longer available";
                return false;
            }

            if (_rig.IsDead || !_rig.IsInPlay)
            {
                reason = _rig.IsDead ? "dead players cannot change inventory" : "extracted players cannot change inventory";
                return false;
            }

            if (GameServices.TryGet<RunDirector>(out RunDirector director) &&
                !director.IsInteractionActorAdmitted(_rig, out reason))
            {
                if (string.IsNullOrEmpty(reason)) reason = "player is not admitted to a running session";
                return false;
            }

            reason = null;
            return true;
        }

        private bool Execute(InventoryCommand command, out string reason)
        {
            reason = null;
            if (_rig.GetComponent<ConsumableChannel>() is ConsumableChannel drink && drink.IsChanneling)
            { reason = "finish drinking before changing inventory"; return false; }
            switch (command.Kind)
            {
                case InventoryCommandKind.HoldQuickItem:
                    if (!ResolveBagStack(command.SourceStackId, command.ExpectedSourceGearUid, out var quick, out _, out reason)) return false;
                    if (!HotbarBindings.IsUsableDefinition(quick.Item)) { reason = "item cannot be held in quick access"; return false; }
                    if (_rig.Loadout != null && _rig.Loadout.IsActionLocked) { reason = "cannot change held item during an attack"; return false; }
                    if (!_inventory.CanCommitRevision(out reason)) return false;
                    _inventory.SetHeldStack(command.SourceStackId);
                    return true;
                case InventoryCommandKind.SelectWeaponSet:
                    if (_rig.Loadout != null && _rig.Loadout.IsActionLocked) { reason = "cannot change held item during an attack"; return false; }
                    if (!_inventory.CanCommitRevision(out reason)) return false;
                    using (GameEventTransaction.Defer())
                    {
                        bool changedSet = _equipment.ActiveWeaponSet != command.DestinationCell.x;
                        if (changedSet && !_equipment.TrySwapWeaponSet(out reason)) return false;
                        _inventory.SetHeldStack(0, !changedSet);
                    }
                    return true;
                case InventoryCommandKind.Move:
                case InventoryCommandKind.Use:
                case InventoryCommandKind.Equip:
                    if (!ResolveBagStack(command.SourceStackId, command.ExpectedSourceGearUid,
                            out InventorySlot source, out int sourceIndex, out reason)) return false;
                    if (command.Kind == InventoryCommandKind.Move)
                        return _inventory.TryMove(sourceIndex, command.DestinationCell, out reason);
                    if (command.Kind == InventoryCommandKind.Equip)
                    {
                        if (HotbarBindings.IsUsableDefinition(source.Item)) { reason = "quick items belong in quick access, not weapon slots"; return false; }
                        if (source.Item == null || !EquipmentSlots.Accepts(source.Item.equipSlot, command.DestinationSlot))
                        {
                            reason = "the source item does not fit the requested equipment slot";
                            return false;
                        }
                        ItemInstance current = _equipment.EquippedInstance(command.DestinationSlot);
                        Guid currentUid = current != null ? current.Uid : Guid.Empty;
                        if (currentUid != command.ExpectedTargetGearUid)
                        {
                            reason = "the expected target gear identity no longer matches that slot";
                            return false;
                        }
                        return _equipment.TryEquipFromInventory(sourceIndex, command.DestinationSlot, out reason);
                    }
                    return UseStack(source, command.SourceStackId, out reason);

                case InventoryCommandKind.Swap:
                case InventoryCommandKind.Merge:
                    if (!ResolveBagStack(command.SourceStackId, command.ExpectedSourceGearUid,
                            out _, out int firstIndex, out reason)) return false;
                    if (!ResolveBagStack(command.TargetStackId, command.ExpectedTargetGearUid,
                            out _, out int secondIndex, out reason)) return false;
                    return command.Kind == InventoryCommandKind.Swap
                        ? _inventory.TrySwap(firstIndex, secondIndex, out reason)
                        : _inventory.TryMergeStacks(firstIndex, secondIndex, out reason);

                case InventoryCommandKind.Unequip:
                    ItemInstance worn = _equipment.EquippedInstance(command.DestinationSlot);
                    if (worn == null || worn.Uid != command.ExpectedSourceGearUid)
                    {
                        reason = "the requested gear identity is no longer worn in that slot";
                        return false;
                    }
                    return _equipment.TryUnequip(command.DestinationSlot, out reason);

                case InventoryCommandKind.SwapEquipment:
                {
                    // D3b: the two ring slots exchange their contents. Both identities are checked against the live slots
                    // before anything moves, and Equipment performs the exchange as one step (or refuses and changes nothing).
                    EquipmentSlot ringTo = command.DestinationSlot;
                    EquipmentSlot ringFrom = EquipmentSlots.OtherRing(ringTo);
                    ItemInstance ringFromItem = _equipment.EquippedInstance(ringFrom);
                    ItemInstance ringToItem = _equipment.EquippedInstance(ringTo);
                    Guid ringFromUid = ringFromItem != null ? ringFromItem.Uid : Guid.Empty;
                    Guid ringToUid = ringToItem != null ? ringToItem.Uid : Guid.Empty;
                    if (ringFromUid != command.ExpectedSourceGearUid)
                    {
                        reason = "the expected gear identity no longer matches the source ring slot";
                        return false;
                    }
                    if (ringToUid != command.ExpectedTargetGearUid)
                    {
                        reason = "the expected gear identity no longer matches the target ring slot";
                        return false;
                    }
                    return _equipment.TrySwapRingSlots(ringFrom, ringTo, out reason);
                }

                case InventoryCommandKind.SwapWeaponSet:
                {
                    // D3c: both identities are checked against the live main hands before anything moves; Equipment decides
                    // the rest (dead, mid-attack, no weapon in the other set, no revision room) and changes nothing on a refusal.
                    ItemInstance activeHand = _equipment.MainHandInstance(_equipment.ActiveWeaponSet);
                    ItemInstance otherHand = _equipment.MainHandInstance(1 - _equipment.ActiveWeaponSet);
                    Guid activeUid = activeHand != null ? activeHand.Uid : Guid.Empty;
                    Guid otherUid = otherHand != null ? otherHand.Uid : Guid.Empty;
                    if (activeUid != command.ExpectedSourceGearUid)
                    {
                        reason = "the expected gear identity no longer matches the weapon in hand";
                        return false;
                    }
                    if (otherUid != command.ExpectedTargetGearUid)
                    {
                        reason = "the expected gear identity no longer matches the other weapon set";
                        return false;
                    }
                    using (GameEventTransaction.Defer())
                    {
                        if (!_equipment.TrySwapWeaponSet(out reason)) return false;
                        _inventory.SetHeldStack(0, false);
                        return true;
                    }
                }

                case InventoryCommandKind.Drop:
                    return ExecuteDrop(command, out reason);

                default:
                    reason = "unknown inventory command";
                    return false;
            }
        }

        /// <summary>U5A test seam: the ground that receives dropped items when no RunDirector is in the scene.</summary>
        internal static IGroundItemSink GroundSinkOverride;

        private bool ExecuteDrop(InventoryCommand command, out string reason)
        {
            using (GameEventTransaction.Defer()) return ExecuteDropTransaction(command, out reason);
        }

        private bool ExecuteDropTransaction(InventoryCommand command, out string reason)
        {
            IGroundItemSink sink = GroundSinkOverride;
            if (sink == null && GameServices.TryGet<RunDirector>(out RunDirector director)) sink = director;
            if (sink == null)
            {
                reason = "there is no ground to drop onto";
                return false;
            }

            if (!_inventory.TryBeginDropMutation(out Inventory.DropMutationLease lease, out reason)) return false;
            using (lease)
            {
            IPreparedGroundDrop prepared = null;
            ItemInstance instance;
            EquipmentSlot fromSlot = EquipmentSlot.None;
            Equipment.DropStateSnapshot equipmentSnapshot = null;
            Vector2Int fromOrigin = Vector2Int.zero;
            ulong fromStackId = 0;
            ulong originalRevision = _inventory.Revision;
            Vector3 at = _rig.transform.position + _rig.transform.forward * 1.1f + Vector3.up * 0.5f;
            if (command.SourceStackId != 0)
            {
                if (!_inventory.TryGetStack(command.SourceStackId, out InventorySlot source) || source.Instance == null ||
                    source.Instance.Uid != command.ExpectedSourceGearUid)
                {
                    reason = "source stack identity is unknown or no longer holds the expected gear";
                    return false;
                }
                instance = source.Instance;
            }
            else
            {
                instance = _equipment.EquippedInstance(command.DestinationSlot);
                if (instance == null || instance.Uid != command.ExpectedSourceGearUid)
                {
                    reason = "the requested gear identity is no longer worn in that slot";
                    return false;
                }
            }
            try
            {
                if (!sink.TryPrepareDropped(instance, at, out prepared, out reason))
                {
                    SafeCancelPrepared(prepared);
                    return false;
                }
            }
            catch (Exception exception)
            {
                SafeCancelPrepared(prepared);
                reason = "the ground could not prepare the drop: " + exception.Message;
                return false;
            }

            if (prepared == null)
            {
                if (string.IsNullOrEmpty(reason)) reason = "the ground refused to prepare the item";
                return false;
            }

            try
            {
                if (command.SourceStackId != 0)
                {
                    if (!_inventory.TryDetachInstanceForDrop(lease, command.SourceStackId, command.ExpectedSourceGearUid,
                            out ItemInstance detached, out fromOrigin, out originalRevision, out reason))
                    {
                        SafeCancelPrepared(prepared);
                        return false;
                    }
                    instance = detached;
                    fromStackId = command.SourceStackId;
                }
                else
                {
                    fromSlot = command.DestinationSlot;
                    if (!_equipment.TryDetachSlotForDrop(lease, fromSlot, command.ExpectedSourceGearUid,
                            out ItemInstance detached, out equipmentSnapshot, out reason))
                    {
                        SafeCancelPrepared(prepared);
                        return false;
                    }
                    instance = detached;
                }

                // Publication is the only operation that can still refuse. It occurs while the source is provisional
                // and before revision/events commit, so a throwing or reentrant sink can be compensated exactly.
                try { prepared.Publish(); }
                catch (Exception exception)
                {
                    SafeCancelPrepared(prepared);
                    string rollbackReason;
                    bool restored = fromSlot == EquipmentSlot.None
                        ? _inventory.TryRestoreInstanceAfterDropRefusal(lease, instance, fromStackId, fromOrigin,
                            originalRevision, out rollbackReason)
                        : _equipment.TryRestoreSlotAfterDropFailure(lease, fromSlot, instance, equipmentSnapshot, out rollbackReason);
                    reason = "the ground could not publish the drop: " + exception.Message;
                    if (!restored) reason += "; rollback failed: " + rollbackReason;
                    return false;
                }

                if (fromSlot != EquipmentSlot.None && !_equipment.FinalizeDropDetach(lease, fromSlot, instance))
                {
                    SafeCancelPrepared(prepared);
                    string rollbackReason;
                    bool restored = _equipment.TryRestoreSlotAfterDropFailure(lease, fromSlot, instance, equipmentSnapshot, out rollbackReason);
                    reason = "equipment drop finalization was refused";
                    if (!restored) reason += "; rollback failed: " + rollbackReason;
                    return false;
                }

                if (!_inventory.CommitDroppedInstance(lease, originalRevision, out reason))
                {
                    SafeCancelPrepared(prepared);
                    string rollbackReason;
                    bool restored = fromSlot == EquipmentSlot.None
                        ? _inventory.TryRestoreInstanceAfterDropRefusal(lease, instance, fromStackId, fromOrigin,
                            originalRevision, out rollbackReason)
                        : _equipment.TryRestoreSlotAfterDropFailure(lease, fromSlot, instance, equipmentSnapshot, out rollbackReason);
                    if (!restored) reason = (reason ?? "drop commit refused") + "; rollback failed: " + rollbackReason;
                    return false;
                }

                if (fromSlot != EquipmentSlot.None)
                {
                    GameEvents.RaiseEquipmentChanged(_rig.PlayerId, instance.Definition, false);
                    if (instance.Definition.GrantsWeapon && _rig.Loadout != null)
                        GameEvents.RaiseActiveWeapon(_rig.PlayerId, _rig.Loadout.ActiveWeapon);
                }

                AttemptNotification(() => GameEvents.RaiseToast(_rig.PlayerId, $"Dropped {instance.Definition.displayName}"));
                reason = null;
                return true;
            }
            catch (Exception exception)
            {
                SafeCancelPrepared(prepared);
                bool stillAtSource = fromSlot == EquipmentSlot.None
                    ? (_inventory.TryGetStack(fromStackId != 0 ? fromStackId : command.SourceStackId, out InventorySlot live) &&
                       ReferenceEquals(live.Instance, instance))
                    : ReferenceEquals(_equipment.EquippedInstance(fromSlot), instance);
                if (!stillAtSource && _inventory.Revision == originalRevision)
                {
                    string rollbackReason;
                    bool restored = fromSlot == EquipmentSlot.None
                        ? _inventory.TryRestoreInstanceAfterDropRefusal(lease, instance,
                            fromStackId != 0 ? fromStackId : command.SourceStackId, fromOrigin, originalRevision, out rollbackReason)
                        : _equipment.TryRestoreSlotAfterDropFailure(lease, fromSlot, instance, equipmentSnapshot, out rollbackReason);
                    if (!restored) reason = "; rollback failed: " + rollbackReason;
                }
                reason = "the drop transaction failed: " + exception.Message + (reason ?? string.Empty);
                return false;
            }
            finally { SafeDisposePrepared(prepared); }
            }
        }

        private static void SafeCancelPrepared(IPreparedGroundDrop prepared)
        {
            if (prepared == null) return;
            try { prepared.Cancel(); }
            catch (Exception exception) { Debug.LogException(exception); }
        }

        private static void SafeDisposePrepared(IPreparedGroundDrop prepared)
        {
            if (prepared == null) return;
            try { prepared.Dispose(); }
            catch (Exception exception) { Debug.LogException(exception); }
        }

        private bool ResolveBagStack(ulong stackId, Guid expectedUid, out InventorySlot slot,
            out int index, out string reason)
        {
            index = _inventory.IndexOfStack(stackId);
            if (index < 0 || !_inventory.TryGetStack(stackId, out slot))
            {
                reason = "source or target stack identity is unknown or retired";
                slot = default;
                return false;
            }

            Guid liveUid = slot.Instance != null ? slot.Instance.Uid : Guid.Empty;
            if (liveUid != expectedUid)
            {
                reason = "the expected gear identity no longer matches that stack";
                return false;
            }

            reason = null;
            return true;
        }

        private bool UseStack(InventorySlot slot, ulong stackId, out string reason)
        {
            ItemData item = slot.Item;
            HealthComponent health = _rig.Health;
            RangedCombat ranged = _rig.GetComponent<RangedCombat>();
            bool drawnAmmoSupply = HotbarBindings.IsAmmoSupply(item) && _inventory.HeldStackId == 0 &&
                ranged != null && ranged.enabled && (_rig.Loadout == null || _rig.ActiveWeapon == ranged.Weapon);
            if (_inventory.HeldStackId != stackId && !drawnAmmoSupply)
            {
                reason = HotbarBindings.IsAmmoSupply(item) ? "draw the matching ranged weapon to replenish ammunition" : "take that item in hand before using it";
                return false;
            }
            bool canHeal = item != null && item.healAmount > 0 && health != null && !health.IsDead &&
                           health.CurrentHealth < health.MaxHealth;
            bool canGrantAmmo = item != null && item.ammoGrant > 0 && item.MatchesAmmo(ranged != null ? ranged.Weapon : null) && CanGrantAmmo(ranged, item.ammoGrant);
            if (!canHeal && !canGrantAmmo)
            {
                reason = item == null ? "the requested stack has no item definition" :
                    (item.healAmount > 0 && health != null && !health.IsDead && health.CurrentHealth >= health.MaxHealth
                        ? "already at full health"
                        : (item.ammoGrant > 0 ? "ammunition is full or no ranged weapon can receive it" :
                            item.displayName + " cannot be used"));
                if (item != null)
                {
                    string toastReason = reason;
                    AttemptNotification(() => GameEvents.RaiseToast(_rig.PlayerId, toastReason));
                }
                return false;
            }

            // D3c: a potion with a drink channel and/or a heal over time. Consumable effects stay authority-owned: the channel and
            // the heal run on ConsumableChannel, which only this authority starts.
            if (item.HasTimedUse && canHeal && !canGrantAmmo)
            {
                ConsumableChannel timed = ConsumableChannel.Ensure(_rig.gameObject);
                if (timed == null)
                {
                    reason = "no consumable channel for this player";
                    return false;
                }

                if (item.useChannelSeconds > 0f)
                {
                    // The drink: nothing is consumed (and no revision committed) until it completes; an interrupted drink keeps the item.
                    if (!timed.TryBegin(slot, out reason))
                    {
                        string beginReason = reason;
                        AttemptNotification(() => GameEvents.RaiseToast(_rig.PlayerId, beginReason));
                        return false;
                    }
                    return true;
                }

                // Heal over time with no channel: consumed now, delivered over healOverTimeSeconds.
                if (!_inventory.CanCommitRevision(out reason)) return false;
                if (!_inventory.TryConsumeStackSilently(stackId, 1, item, out ItemData timedConsumed, out reason)) return false;
                AttemptNotification(() => timed.StartHeal(timedConsumed));
                AttemptNotification(() => GameEvents.RaiseToast(_rig.PlayerId,
                    $"Used {timedConsumed.displayName} (+{timedConsumed.healAmount} HP over {timedConsumed.healOverTimeSeconds:0.#} s)"));
                AttemptNotification(_inventory.NotifyAuthorityMutation);
                reason = null;
                return true;
            }

            if (!_inventory.CanCommitRevision(out reason)) return false;
            if (!_inventory.TryConsumeStackSilently(stackId, 1, item, out ItemData consumed, out reason)) return false;

            // Removal and revision commit happen before health/ammo callbacks can inspect the bag. A bad
            // observer cannot turn this committed command into a reported refusal or suppress final bag notice.
            if (canHeal)
            {
                AttemptNotification(() => health.Heal(consumed.healAmount));
                AttemptNotification(() => GameEvents.RaiseToast(_rig.PlayerId,
                    $"Used {consumed.displayName} (+{consumed.healAmount} HP)"));
            }
            if (canGrantAmmo)
            {
                AttemptNotification(() => ranged.AddAmmo(consumed.ammoGrant));
                AttemptNotification(() => GameEvents.RaiseToast(_rig.PlayerId,
                    $"Used {consumed.displayName} (+{consumed.ammoGrant} ammo)"));
            }

            AttemptNotification(_inventory.NotifyAuthorityMutation);
            reason = null;
            return true;
        }

        private static void AttemptNotification(Action notification)
        {
            try { notification(); }
            catch (Exception exception) { Debug.LogException(exception); }
        }

        private static bool CanGrantAmmo(RangedCombat ranged, int amount) =>
            ranged != null && ranged.CanAcceptAmmo(amount);

        private InventoryCommandResult Refused(string reason) =>
            new InventoryCommandResult(false, false, _inventory != null ? _inventory.Revision : 0, reason);
    }
}
