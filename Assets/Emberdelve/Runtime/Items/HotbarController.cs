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
    /// <summary>
    /// R6c2: the six quick-wheel assignments of one bag. A slot holds a runtime StackId (0 = empty), never a list
    /// index and never a definition, so list compaction, a partial use, or a lookalike stack cannot retarget a key.
    ///
    /// Rules:
    ///  - Default assignment: when a raid loadout is (re)established (<see cref="Inventory.GiveStartingItems"/>, a
    ///    bag restore) the first six holdable stacks in bag order are bound to the two wheels (<see cref="SeedDefaults"/>).
    ///    Holdable utility definitions include healing items, ammo packs and torches.
    ///  - No auto-refill: nothing is bound mid-raid except by <see cref="TryAssign"/>. A stack that appears later
    ///    (a pickup, an unequip, a split) never takes an empty slot on its own, because it is a different identity
    ///    from the one the player last saw on that key. The one exception is a one-shot first-frame seed in
    ///    <see cref="HotbarController"/> for a bag whose loadout hook never ran.
    ///  - A bound stack that retires (consumed, removed, moved out, Clear, restore) reads as empty; the key does
    ///    nothing. A partial use keeps the same StackId and therefore the binding.
    ///
    /// D3c: this is the utility belt. A binding still names a live StackId (so a lookalike stack never inherits it), but a
    /// restore no longer throws the belt away: before the bag is replaced every live binding is read as an ITEM IDENTITY
    /// (<see cref="BeltIdentity"/>: the instance uid for a uid-backed stack, otherwise the item definition) and after the new
    /// stacks exist each slot is bound again to the stack that carries that identity - by identity, never by list index or old
    /// StackId, so reordering the bag cannot move a key. An identity that no longer resolves leaves its key empty; nothing is
    /// auto-filled. A restore of a belt that held nothing keeps the old behaviour (the first usable stacks are bound by default).
    /// The belt also travels in the v5 loadout snapshot (<see cref="LoadoutSnapshot"/>).
    ///
    /// Reads validate against the live bag, so a retired binding is empty even before it is pruned. Changes made
    /// by <see cref="TryAssign"/> raise <see cref="GameEvents.HotbarChanged"/>; seeding and retiring always accompany an
    /// InventoryChanged notification, which already refreshes the HUD.
    /// </summary>
    /// <summary>
    /// D3c: what a belt key points at, independent of any runtime StackId or list index: the instance uid when the stack is
    /// uid-backed, otherwise the item definition (carried stacks are fungible, so the definition is their identity).
    /// </summary>
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

    public sealed class HotbarBindings
    {
        public const int SlotCount = 6;

        private readonly Inventory _inventory;
        private readonly ulong[] _ids = new ulong[SlotCount];
        private bool _seeded;

        internal HotbarBindings(Inventory inventory)
        {
            _inventory = inventory;
        }

        /// <summary>True once a loadout hook, a clear or a restore has run for this bag (so no lazy seed is needed).</summary>
        public bool HasSeeded => _seeded;

        /// <summary>Whether a definition can be put on the hotbar: the same test the Use authority applies to the item (heals or grants ammo).</summary>
        public static bool IsUsableDefinition(ItemData item)
        {
            return item != null && (item.healAmount > 0 || item.ammoGrant > 0 || (item.grantedWeapon != null && item.grantedWeapon.weaponId == HandheldTorchVisual.WeaponId));
        }

        /// <summary>The live StackId bound to a slot, or 0 when the slot is empty or its stack has retired.</summary>
        public ulong StackIdAt(int slot)
        {
            if (slot < 0 || slot >= SlotCount || _inventory == null) return 0;
            ulong id = _ids[slot];
            return id != 0 && _inventory.IndexOfStack(id) >= 0 ? id : 0;
        }

        /// <summary>Reads the live stack bound to a slot (current count, definition, origin). False for an empty or retired slot.</summary>
        public bool TryGetStack(int slot, out InventorySlot stack)
        {
            ulong id = StackIdAt(slot);
            if (id == 0 || !_inventory.TryGetStack(id, out stack))
            {
                stack = default;
                return false;
            }
            return true;
        }

        /// <summary>The hotbar slot (0-5) a live stack is bound to, or -1.</summary>
        public int SlotOf(ulong stackId)
        {
            if (stackId == 0) return -1;
            for (int i = 0; i < SlotCount; i++)
                if (StackIdAt(i) == stackId) return i;
            return -1;
        }

        /// <summary>
        /// Binds a live, usable stack to a slot, replacing whatever the slot held and removing the stack from any
        /// other slot so one stack is never on two keys. A refusal changes nothing.
        /// </summary>
        public bool TryAssign(int slot, ulong stackId, out string reason)
        {
            if (slot < 0 || slot >= SlotCount) { reason = "there is no such hotbar slot"; return false; }
            if (_inventory == null || stackId == 0 || !_inventory.TryGetStack(stackId, out InventorySlot stack) ||
                stack.Item == null)
            {
                reason = "that item is no longer in the bag";
                return false;
            }
            if (!IsUsableDefinition(stack.Item))
            {
                reason = stack.Item.displayName + " cannot be used from the hotbar";
                return false;
            }

            PruneRetired();
            bool changed = _ids[slot] != stackId;
            for (int i = 0; i < SlotCount; i++)
            {
                if (i != slot && _ids[i] == stackId) { _ids[i] = 0; changed = true; }
            }
            _ids[slot] = stackId;
            _seeded = true;

            if (changed) GameEvents.RaiseHotbarChanged(_inventory.OwnerPlayerId);
            reason = null;
            return true;
        }

        /// <summary>
        /// Uses exactly the stack bound to the slot, through the authority Use command. An empty or retired slot
        /// does nothing and never falls through to whatever sits at an index or shares a definition.
        /// </summary>
        public bool TryUse(int slot, out string reason)
        {
            ulong id = StackIdAt(slot);
            if (id == 0)
            {
                if (slot >= 0 && slot < SlotCount) _ids[slot] = 0;
                reason = "that hotbar slot is empty";
                return false;
            }

            if (_inventory.TryUseStack(id))
            {
                reason = null;
                return true;
            }

            reason = "the bound item could not be used";
            return false;
        }

        /// <summary>D3c: the identity behind a slot's live binding, or an empty identity when the slot is empty or its stack retired.</summary>
        public BeltIdentity IdentityAt(int slot)
        {
            if (!TryGetStack(slot, out InventorySlot stack)) return default;
            return new BeltIdentity(stack.Item, stack.Instance != null ? stack.Instance.Uid : Guid.Empty);
        }

        /// <summary>D3c: the six identities as they are right now (a retired or empty binding reads as empty). Read-only.</summary>
        public BeltIdentity[] CaptureIdentities()
        {
            BeltIdentity[] identities = new BeltIdentity[SlotCount];
            for (int i = 0; i < SlotCount; i++) identities[i] = IdentityAt(i);
            return identities;
        }

        /// <summary>
        /// D3c: binds every slot again to the live stack that carries its saved identity, in bag order for fungible stacks (two
        /// slots holding the same definition take two different stacks). Replaces all bindings. Raises no event (a restore's
        /// InventoryChanged refreshes the HUD). Returns false, changing nothing, when every identity is empty - the caller then
        /// seeds the defaults instead.
        /// </summary>
        public bool RebindFromIdentities(IReadOnlyList<BeltIdentity> identities, bool allowEmpty = false)
        {
            if (identities == null) return false;

            bool any = false;
            for (int i = 0; i < SlotCount && i < identities.Count; i++) any |= !identities[i].IsEmpty;
            if ((!any && !allowEmpty) || _inventory == null) return false;

            RetireAll();
            HashSet<ulong> taken = new HashSet<ulong>();
            for (int i = 0; i < SlotCount && i < identities.Count; i++)
            {
                BeltIdentity wanted = identities[i];
                if (wanted.IsEmpty) continue;

                for (int s = 0; s < _inventory.Slots.Count; s++)
                {
                    InventorySlot stack = _inventory.Slots[s];
                    if (stack.StackId == 0 || taken.Contains(stack.StackId) || !IsUsableDefinition(stack.Item)) continue;

                    bool matches = wanted.Uid != Guid.Empty
                        ? stack.Instance != null && stack.Instance.Uid == wanted.Uid
                        : stack.Instance == null && stack.Item == wanted.Definition;
                    if (!matches) continue;

                    _ids[i] = stack.StackId;
                    taken.Add(stack.StackId);
                    break;
                }
            }

            return true;
        }

        /// <summary>Drops every binding. Called by Inventory.Clear.</summary>
        internal void RetireAll()
        {
            for (int i = 0; i < SlotCount; i++) _ids[i] = 0;
            _seeded = true;
        }

        /// <summary>Retires every binding, then binds the first usable stacks in bag order to slots 0..5. Raises no event.</summary>
        internal void SeedDefaults()
        {
            RetireAll();
            if (_inventory == null) return;

            int next = 0;
            for (int i = 0; i < _inventory.Slots.Count && next < SlotCount; i++)
            {
                InventorySlot stack = _inventory.Slots[i];
                if (stack.StackId == 0 || !IsUsableDefinition(stack.Item)) continue;
                _ids[next++] = stack.StackId;
            }
        }

        private void PruneRetired()
        {
            for (int i = 0; i < SlotCount; i++)
                if (_ids[i] != 0 && _inventory.IndexOfStack(_ids[i]) < 0) _ids[i] = 0;
        }
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
                    : inventory.RequestHoldStack(inventory.Hotbar.StackIdAt(slot), out reason);
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
