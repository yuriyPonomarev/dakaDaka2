using System;
using System.Collections.Generic;
using UnityEngine;
using Emberdelve.Combat;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Player;

namespace Emberdelve.Items
{
    /// <summary>
    /// D3c: the timed half of using a consumable, owned by the inventory command authority (R6b2: consumable effects are
    /// authority-owned). <see cref="LocalInventoryAuthority"/> is the only caller of <see cref="TryBegin"/> and
    /// <see cref="StartHeal"/>; the channel then runs on its own clock.
    ///
    /// Rules (decision recorded in Docs/Tasks/D3c.md):
    ///  - A potion with <see cref="ItemData.useChannelSeconds"/> is DRUNK first: the stack is not touched while the player
    ///    channels. Moving faster than <see cref="ConsumableRules.MoveCancelRatio"/> of walking speed, being hit, attacking (a
    ///    swing, a draw, a reload or an attack press), swapping weapon sets, dying or running out of the game cancel the drink,
    ///    and a cancelled drink KEEPS the potion (nothing is consumed, nothing is healed).
    ///  - A completed channel consumes exactly one unit of the same stack (one revision, one inventory notification) and then
    ///    delivers <see cref="ItemData.healAmount"/> over <see cref="ItemData.healOverTimeSeconds"/> (or at once when that is 0).
    ///    The delivered total is exactly healAmount; the heal is committed once consumed, so a hit AFTER the channel does not
    ///    stop it. A stack that vanished during the channel, or a player already at full health, ends the drink without consuming.
    ///  - One drink at a time; any number of heals over time may overlap.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ConsumableChannel : MonoBehaviour
    {
        private sealed class ActiveHeal
        {
            public ItemData Item;
            public float Total;
            public float Duration;
            public float Elapsed;
            public float Delivered;
        }

        private PlayerRig _rig;
        private HealthComponent _subscribedHealth;
        private readonly List<ActiveHeal> _heals = new List<ActiveHeal>(2);

        private ulong _stackId;
        private ItemData _item;
        private float _elapsed;
        private float _duration;

        /// <summary>Test seam (same idea as the input overrides): replaces the real speed ratio while set. Never set in game code.</summary>
        internal Func<float> SpeedRatioOverride { get; set; }

        public bool IsChanneling => _item != null;
        public ItemData ChannelItem => _item;
        public float ChannelProgress => _item != null ? ConsumableRules.Progress(_elapsed, _duration) : 0f;
        public int ActiveHealCount => _heals.Count;
        public int CompletedDrinks { get; private set; }
        public int CancelledDrinks { get; private set; }
        public string LastCancelReason { get; private set; }

        /// <summary>Heal still to be delivered by the heals over time in progress.</summary>
        public float PendingHeal
        {
            get
            {
                float pending = 0f;
                for (int i = 0; i < _heals.Count; i++) pending += Mathf.Max(0f, _heals[i].Total - _heals[i].Delivered);
                return pending;
            }
        }

        public static ConsumableChannel Ensure(GameObject target)
        {
            if (target == null) return null;
            ConsumableChannel existing = target.GetComponent<ConsumableChannel>();
            return existing != null ? existing : target.AddComponent<ConsumableChannel>();
        }

        private PlayerRig Rig
        {
            get
            {
                if (_rig == null) _rig = GetComponent<PlayerRig>();
                return _rig;
            }
        }

        private int OwnerId => Rig != null ? Rig.PlayerId : GameEvents.NoPlayer;

        private void Update()
        {
            Tick(Time.deltaTime);
        }

        private void OnDestroy()
        {
            Unsubscribe();
        }

        /// <summary>
        /// Starts the drink for one live stack. The caller (the authority) has already checked that the item heals and that the
        /// player can be healed. Nothing is consumed here. Refused (no state change) when a drink is already running.
        /// </summary>
        internal bool TryBegin(InventorySlot slot, out string reason)
        {
            ItemData item = slot.Item;
            if (item == null || slot.StackId == 0)
            {
                reason = "the requested stack has no item definition";
                return false;
            }

            if (!(item.useChannelSeconds > 0f))
            {
                reason = item.displayName + " has no use channel";
                return false;
            }

            if (Rig == null || Rig.Health == null || Rig.IsDead)
            {
                reason = "nobody to drink it";
                return false;
            }

            if (Rig.Inventory == null || Rig.Inventory.HeldStackId != slot.StackId)
            { reason = "take that item in hand before using it"; return false; }

            if (IsChanneling)
            {
                reason = "already drinking " + _item.displayName;
                return false;
            }

            _stackId = slot.StackId;
            _item = item;
            _duration = item.useChannelSeconds;
            _elapsed = 0f;

            Unsubscribe();
            _subscribedHealth = Rig.Health;
            _subscribedHealth.Damaged += OnDamaged;

            Notify(() => GameEvents.RaiseToast(OwnerId, "Drinking " + item.displayName + "..."));
            reason = null;
            return true;
        }

        /// <summary>Starts delivering a consumed item's heal: over time when it says so, otherwise all at once. The item has already been consumed.</summary>
        internal void StartHeal(ItemData consumed)
        {
            if (consumed == null || consumed.healAmount <= 0 || Rig == null || Rig.Health == null) return;

            if (!(consumed.healOverTimeSeconds > 0f))
            {
                Rig.Health.Heal(consumed.healAmount);
                return;
            }

            _heals.Add(new ActiveHeal
            {
                Item = consumed,
                Total = consumed.healAmount,
                Duration = consumed.healOverTimeSeconds,
                Elapsed = 0f,
                Delivered = 0f
            });
        }

        /// <summary>Ends a drink without consuming anything. False when nothing was being drunk.</summary>
        public bool Cancel(string reason)
        {
            if (!IsChanneling) return false;

            ItemData item = _item;
            EndChannel();
            CancelledDrinks++;
            LastCancelReason = reason;
            Notify(() => GameEvents.RaiseToast(OwnerId, "Interrupted drinking " + item.displayName + (string.IsNullOrEmpty(reason) ? "" : " (" + reason + ")")));
            return true;
        }

        /// <summary>One step of this component's clock (Update calls it with the frame time; a gate calls it with exact steps).</summary>
        internal void Tick(float deltaTime)
        {
            if (float.IsNaN(deltaTime) || deltaTime <= 0f) return;

            TickHeals(deltaTime);
            TickChannel(deltaTime);
        }

        private void TickHeals(float deltaTime)
        {
            if (_heals.Count == 0) return;

            HealthComponent health = Rig != null ? Rig.Health : null;
            if (health == null || health.IsDead)
            {
                _heals.Clear();
                return;
            }

            for (int i = _heals.Count - 1; i >= 0; i--)
            {
                ActiveHeal heal = _heals[i];
                heal.Elapsed += deltaTime;
                float target = ConsumableRules.HealDelivered(heal.Total, heal.Duration, heal.Elapsed);
                float step = target - heal.Delivered;
                // Settle the payout before Health.Changed can throw or reenter; this unit of healing is paid only once.
                heal.Delivered = target;
                if (heal.Elapsed >= heal.Duration) _heals.RemoveAt(i);
                if (step > 0f)
                {
                    try { health.Heal(step); }
                    catch (Exception exception) { Debug.LogException(exception); }
                }
            }
        }

        private void TickChannel(float deltaTime)
        {
            if (!IsChanneling) return;

            PlayerRig rig = Rig;
            if (rig == null || rig.IsDead || !rig.IsInPlay)
            {
                Cancel("no longer able to drink");
                return;
            }

            if (rig.Loadout != null && rig.Loadout.IsActionLocked)
            {
                Cancel("attacking");
                return;
            }

            if (rig.Inventory == null || rig.Inventory.HeldStackId != _stackId)
            {
                Cancel("attacking");
                return;
            }

            if (ConsumableRules.IsMovingTooFast(CurrentSpeedRatio()))
            {
                Cancel("moving");
                return;
            }

            _elapsed += deltaTime;
            if (_elapsed >= _duration) Complete();
        }

        private float CurrentSpeedRatio()
        {
            if (SpeedRatioOverride != null) return SpeedRatioOverride();
            return Rig != null && Rig.Movement != null ? Rig.Movement.SpeedMultiplier : 0f;
        }

        private void Complete()
        {
            ItemData item = _item;
            ulong stackId = _stackId;
            EndChannel();

            PlayerRig rig = Rig;
            Inventory inventory = rig != null ? rig.Inventory : null;
            HealthComponent health = rig != null ? rig.Health : null;
            if (inventory == null || health == null || health.IsDead || inventory.HeldStackId != stackId) return;

            if (health.CurrentHealth >= health.MaxHealth)
            {
                GameEvents.RaiseToast(OwnerId, "Already at full health");
                return;
            }

            if (!inventory.CanCommitRevision(out string reason) ||
                !inventory.TryConsumeStackSilently(stackId, 1, item, out ItemData consumed, out reason))
            {
                GameEvents.RaiseToast(OwnerId, reason ?? (item.displayName + " is gone"));
                return;
            }

            CompletedDrinks++;
            try { StartHeal(consumed); }
            catch (Exception e) { Debug.LogException(e); }

            Notify(() => GameEvents.RaiseToast(OwnerId, "Used " + consumed.displayName + " (+" + consumed.healAmount + " HP" +
                                           (consumed.healOverTimeSeconds > 0f ? " over " + consumed.healOverTimeSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " s" : "") + ")"));
            try { inventory.NotifyAuthorityMutation(); }
            catch (Exception e) { Debug.LogException(e); }
        }

        private static void Notify(Action callback)
        {
            try { callback(); }
            catch (Exception exception) { Debug.LogException(exception); }
        }

        private void OnDamaged(DamageInfo info)
        {
            if (info.Amount > 0f) Cancel("hit");
        }

        private void EndChannel()
        {
            _item = null;
            _stackId = 0;
            _elapsed = 0f;
            _duration = 0f;
            Unsubscribe();
        }

        private void Unsubscribe()
        {
            if (_subscribedHealth != null) _subscribedHealth.Damaged -= OnDamaged;
            _subscribedHealth = null;
        }
    }
}
