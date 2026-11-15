using System;
using System.Collections.Generic;
using UnityEngine;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Flow;
using Emberdelve.InputLayer;
using Emberdelve.Player;

namespace Emberdelve.Items
{
    /// <summary>Legacy snapshot metadata for the item occupying a physical belt slot.</summary>
    public readonly struct BeltIdentity
    {
        public readonly ItemData Definition;
        public readonly Guid Uid;

        public BeltIdentity(ItemData definition, Guid uid)
        {
            Definition = definition;
            Uid = uid;
        }

        public bool IsEmpty => Definition == null && Uid == Guid.Empty;

        /// <summary>The snapshot spelling: "" (empty), "i:" + itemId, or "u:" + the 32-digit lowercase uid.</summary>
        public string ToSnapshotText()
        {
            if (Uid != Guid.Empty) return "u:" + Uid.ToString("N");
            return Definition != null ? "i:" + Definition.itemId : "";
        }
    }

    /// <summary>Six physical utility slots. Item positions, not local bindings, are authoritative.</summary>
    public sealed class HotbarBindings
    {
        public const int SlotCount = QuickBeltRules.SlotCount;
        private readonly Inventory _inventory;
        private bool _seeded;
        internal HotbarBindings(Inventory inventory) { _inventory = inventory; }
        public bool HasSeeded => _seeded;
        public static bool IsAmmoSupply(ItemData item) => item != null && item.ammoGrant > 0 && item.healAmount <= 0;
        public static bool IsUsableDefinition(ItemData item) => item != null && (item.healAmount > 0 || item.ammoGrant > 0 || QuickBeltRules.CanStore(item));
        public ulong StackIdAt(int slot) => TryGetStack(slot, out InventorySlot stack) ? stack.StackId : 0;
        public bool TryGetStack(int slot, out InventorySlot stack)
        {
            stack = default;
            return _inventory != null && _inventory.TryGetBeltStack(slot, out stack);
        }
        public int SlotOf(ulong stackId)
        {
            if (stackId != 0) for (int i = 0; i < SlotCount; i++) if (StackIdAt(i) == stackId) return i;
            return -1;
        }
        public bool TryAssign(int slot, ulong stackId, out string reason)
        {
            reason = "inventory unavailable";
            if (_inventory == null) return false;
            bool accepted = _inventory.RequestBeltPlacement(slot, stackId, out reason);
            if (accepted) _seeded = true;
            return accepted;
        }
        public bool TryUse(int slot, out string reason)
        {
            ulong id = StackIdAt(slot);
            reason = id == 0 ? "that belt slot is empty" : "the belt item could not be used";
            if (id == 0 || !_inventory.TryUseStack(id)) return false;
            reason = null;
            return true;
        }
        public BeltIdentity IdentityAt(int slot)
        {
            if (!TryGetStack(slot, out InventorySlot stack)) return default;
            return new BeltIdentity(stack.Item, stack.Instance != null ? stack.Instance.Uid : Guid.Empty);
        }
        public BeltIdentity[] CaptureIdentities()
        {
            var result = new BeltIdentity[SlotCount];
            for (int i = 0; i < SlotCount; i++) result[i] = IdentityAt(i);
            return result;
        }
        // Legacy identity metadata must never move physical items, especially after a server snapshot.
        public bool RebindFromIdentities(IReadOnlyList<BeltIdentity> identities, bool allowEmpty = false)
        {
            _seeded = true;
            return identities != null;
        }
        internal void RetireAll() { _seeded = true; }
        internal void SeedDefaults() { _seeded = true; _inventory?.SeedBeltDefaults(); }
    }

    /// <summary>
    /// Routes weapon keys 1/2, quick wheels 3/4 and held-item primary use (see <see cref="HotbarBindings"/>). Kept
    /// separate from Inventory so the bag has no dependency on devices, and separate from the world Interactor so
    /// picking up and using never mix.
    /// </summary>
    [DefaultExecutionOrder(-90)]
    [DisallowMultipleComponent]
    public sealed class HotbarController : MonoBehaviour
    {
        [SerializeField] private PlayerRig rig;
        [SerializeField] private Inventory inventory;
        public static HotbarController Local { get; private set; }
        public bool WheelOpen { get; private set; }
        public int WheelGroup { get; private set; }
        public int WheelSelection { get; private set; }
        public bool Assigning => _assignmentTarget != 0;
        public Inventory Bag => inventory;
        private ulong _assignmentTarget;
        private readonly int[] _lastSelection = new int[2];
        private CursorLockMode _oldLock;
        private bool _oldVisible;
        private int _closedFrame = -1;
        private void Awake()
        {
            if (rig == null) rig = GetComponent<PlayerRig>();
            if (inventory == null) inventory = GetComponent<Inventory>();
        }
        private void OnDisable() { CloseWheel(false); if (Local == this) Local = null; }
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetLocal() { Local = null; }
        public bool OpenAssignmentWheel(int group, ulong stack)
        {
            if (rig == null || !rig.IsLocallyControlled || inventory == null || group < 0 || group > 1 ||
                !inventory.TryGetStack(stack, out var item) || !HotbarBindings.IsUsableDefinition(item.Item)) return false;
            OpenWheel(group, stack); return true;
        }
        private void OpenWheel(int group, ulong assignment)
        {
            if (WheelOpen) return;
            WheelGroup = group; _assignmentTarget = assignment;
            WheelSelection = _lastSelection[group];
            _oldLock = Cursor.lockState; _oldVisible = Cursor.visible;
            WheelOpen = true; rig.Input.QuickWheelCaptured = true;
            InventoryInputFocus.Open(this);
            Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
            UnityEngine.InputSystem.Mouse.current?.WarpCursorPosition(new Vector2(Screen.width * .5f, Screen.height * .5f));
        }
        private void CloseWheel(bool select)
        {
            if (!WheelOpen) return;
            int slot = WheelGroup * 3 + WheelSelection;
            _lastSelection[WheelGroup] = WheelSelection;
            WheelOpen = false; _closedFrame = Time.frameCount;
            if (rig != null && rig.Input != null) rig.Input.QuickWheelCaptured = false;
            InventoryInputFocus.Close(this);
            Cursor.lockState = _oldLock; Cursor.visible = _oldVisible;
            if (select && inventory != null)
            {
                rig?.SuppressWeaponInputForFrame();
                string reason;
                bool accepted = _assignmentTarget != 0
                    ? inventory.Hotbar.TryAssign(slot, _assignmentTarget, out reason)
                    : inventory.RequestQuickStack(inventory.Hotbar.StackIdAt(slot), out reason);
                if (!accepted && rig != null) GameEvents.RaiseToast(rig.PlayerId, reason);
            }
            _assignmentTarget = 0;
        }
        public static int SectorAt(Vector2 direction)
        {
            // Clockwise, starting at the top. Dead centre keeps the last selected sector.
            float angle = Mathf.Repeat(Mathf.Atan2(direction.x, direction.y) * Mathf.Rad2Deg + 60f, 360f);
            return Mathf.Clamp((int)(angle / 120f), 0, 2);
        }
        private void Update()
        {
            if (rig == null || inventory == null || !rig.IsLocallyControlled) return;
            Local = this;
            var input = rig.Input;
            if (input == null) return;
            if (!inventory.Hotbar.HasSeeded) inventory.Hotbar.SeedDefaults();
            if (rig.IsDead || rig.IsExtracted) { CloseWheel(false); return; }
            if (WheelOpen)
            {
                if (input.PausePressed || input.InventoryPressed) { CloseWheel(false); return; }
                Vector2 direction = input.PointerPosition - new Vector2(Screen.width * .5f, Screen.height * .5f);
                if (direction.sqrMagnitude > 24f * 24f) WheelSelection = SectorAt(direction);
                if (!input.QuickWheelHeld(WheelGroup)) CloseWheel(true);
                return;
            }
            if (_closedFrame == Time.frameCount || InventoryInputFocus.OwnsEscape || !input.GameplayInputEnabled) return;
            for (int group = 0; group < 2; group++)
                if (input.QuickWheelPressed(group)) { OpenWheel(group, 0); return; }
            for (int set = 0; set < 2; set++)
                if (input.SlotPressed(set)) { if (!inventory.RequestWeaponSet(set, out string reason)) GameEvents.RaiseToast(rig.PlayerId, reason); return; }
            if (input.PrimaryPressed && inventory.HeldItem != null &&
                (inventory.HeldItem.healAmount > 0 || inventory.HeldItem.ammoGrant > 0))
            {
                rig.SuppressWeaponInputForFrame();
                inventory.TryUseStack(inventory.HeldStackId);
            }
        }
    }
}
