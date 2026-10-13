using System.Collections.Generic;
using UnityEngine;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Combat;
using Emberdelve.Player;

namespace Emberdelve.Items
{
    public readonly struct InventorySlot
    {
        public readonly ItemData Item;
        public readonly int Count;
        public InventorySlot(ItemData item, int count) { Item = item; Count = count; }
    }

    /// <summary>
    /// Stack-based carrying bag for the local player. It stores what the player holds; it never
    /// decides what a pickup is worth or whether a run succeeds (RunDirector does that from the
    /// facts raised through GameEvents). Consumables apply their effect here because that is the
    /// only place that knows both the item data and the owner's combat components.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class Inventory : MonoBehaviour
    {
        [SerializeField] private int capacity = 8;

        private readonly List<InventorySlot> _slots = new List<InventorySlot>();
        private PlayerRig _rig;
        private RangedCombat _rangedCombat;

        public int Capacity => capacity;
        public int UsedSlots => _slots.Count;
        public IReadOnlyList<InventorySlot> Slots => _slots;
        public int TotalUnits { get; private set; }
        public int CarriedValue { get; private set; }

        /// <summary>Value recovered from the level this run. Starting kit is excluded so the extraction
        /// objective cannot be satisfied by gear the class already carries in.</summary>
        public int RecoveredValue { get; private set; }
        public int LifetimeCollected { get; private set; }

        private bool _givingStartingKit;

        private void Awake()
        {
            _rig = GetComponent<PlayerRig>();
            _rangedCombat = GetComponent<RangedCombat>();
        }

        public void Initialize(PlayerRig rig)
        {
            _rig = rig;
            _rangedCombat = GetComponent<RangedCombat>();
        }

        public void Configure(int newCapacity)
        {
            capacity = Mathf.Max(1, newCapacity);
        }

        public void GiveStartingItems(ItemData[] items)
        {
            if (items == null) return;

            _givingStartingKit = true;
            try
            {
                for (int i = 0; i < items.Length; i++)
                {
                    if (items[i] != null) TryAdd(items[i], 1);
                }
            }
            finally
            {
                _givingStartingKit = false;
                GameEvents.RaiseInventoryChanged();
            }
        }

        public bool TryAdd(ItemData item, int amount = 1)
        {
            if (item == null || amount <= 0) return false;

            int remaining = amount;
            int stackLimit = Mathf.Max(1, item.maxStack);

            for (int i = 0; i < _slots.Count && remaining > 0; i++)
            {
                if (_slots[i].Item != item) continue;
                int space = stackLimit - _slots[i].Count;
                if (space <= 0) continue;

                int move = Mathf.Min(space, remaining);
                _slots[i] = new InventorySlot(item, _slots[i].Count + move);
                remaining -= move;
            }

            while (remaining > 0 && _slots.Count < capacity)
            {
                int move = Mathf.Min(stackLimit, remaining);
                _slots.Add(new InventorySlot(item, move));
                remaining -= move;
            }

            int accepted = amount - remaining;
            if (accepted <= 0)
            {
                GameEvents.RaiseToast("Bag is full");
                return false;
            }

            TotalUnits += accepted;
            CarriedValue += item.value * accepted;

            if (!_givingStartingKit)
            {
                LifetimeCollected += accepted;
                RecoveredValue += item.value * accepted;
                GameEvents.RaiseItemCollected(item, LifetimeCollected);
            }

            GameEvents.RaiseInventoryChanged();

            if (remaining > 0) GameEvents.RaiseToast($"Bag full, left {item.displayName} behind");
            return remaining <= 0;
        }

        public bool TryUseSlot(int index)
        {
            if (index < 0 || index >= _slots.Count) return false;

            InventorySlot slot = _slots[index];
            ItemData item = slot.Item;
            if (item == null) return false;

            bool applied = false;

            if (item.healAmount > 0 && _rig != null && _rig.Health != null)
            {
                if (_rig.Health.IsDead) return false;
                if (_rig.Health.CurrentHealth >= _rig.Health.MaxHealth)
                {
                    GameEvents.RaiseToast("Already at full health");
                    return false;
                }
                _rig.Health.Heal(item.healAmount);
                GameEvents.RaiseToast($"Used {item.displayName} (+{item.healAmount} HP)");
                applied = true;
            }

            if (item.ammoGrant > 0 && _rangedCombat != null)
            {
                _rangedCombat.AddAmmo(item.ammoGrant);
                GameEvents.RaiseToast($"Used {item.displayName} (+{item.ammoGrant} arrows)");
                applied = true;
            }

            if (!applied)
            {
                GameEvents.RaiseToast($"{item.displayName} cannot be used");
                return false;
            }

            int count = slot.Count - 1;
            if (count <= 0) _slots.RemoveAt(index);
            else _slots[index] = new InventorySlot(item, count);

            TotalUnits = Mathf.Max(0, TotalUnits - 1);
            CarriedValue = Mathf.Max(0, CarriedValue - item.value);

            GameEvents.RaiseInventoryChanged();
            return true;
        }

        public void Clear()
        {
            _slots.Clear();
            TotalUnits = 0;
            CarriedValue = 0;
            GameEvents.RaiseInventoryChanged();
        }
    }
}
