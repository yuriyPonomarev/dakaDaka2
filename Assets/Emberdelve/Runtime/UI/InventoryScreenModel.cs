using System;
using UnityEngine;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Flow;
using Emberdelve.Items;
using Emberdelve.Player;
using Emberdelve.Stats;

namespace Emberdelve.UI
{
    /// <summary>
    /// R6d: the non-view half of the satchel screen. It owns which director / rig / bag / worn kit the screen
    /// targets, the StackId selection, the command submission through the bag's authority and the R6c2 hotbar
    /// assignment. It holds no widgets and never rebuilds anything: <see cref="InventoryScreenController"/>
    /// remains the facade that draws, hit-tests and calls in here, and rebuilds after the calls that need it.
    ///
    /// A selection is bound to the bag it was made against. StackIds are minted per bag, so two bags can both
    /// have a stack 1; a selection from bag A therefore never resolves against bag B, whatever ids it holds.
    /// </summary>
    internal sealed class InventoryScreenModel
    {
        private RunDirector _director;
        private PlayerRig _rig;
        private Inventory _inventory;
        private Equipment _equipment;

        // Identity is the live stack, not its definition, current list index, or current grid position.
        // The remaining fields are display values refreshed whenever this ID resolves.
        private ulong _selectedStackId;
        private ItemData _selectedItem;
        private Vector2Int _selectedOrigin = new Vector2Int(-1, -1);
        private int _selectedAmount;
        private Inventory _selectionBag;

        public RunDirector Director => _director;
        public PlayerRig Rig => _rig;
        public Inventory Inventory => _inventory;
        public Equipment Equipment => _equipment;

        /// <summary>The raw selected id, not re-validated against the bag. Zero means none.</summary>
        public ulong SelectedStackId => _selectedStackId;
        public ItemData SelectedItem => _selectedItem;
        public int SelectedAmount => _selectedAmount;
        public Vector2Int SelectedOrigin => _selectedOrigin;

        public int ToastOwner => _rig != null ? _rig.PlayerId : GameEvents.NoPlayer;

        /// <summary>True while the director (when there is one) says this player is still running the raid.</summary>
        public bool CommandsAllowed =>
            _director == null || (_director.State == RunState.Running && _director.LocalPlayerState == RunState.Running);

        // ------------------------------------------------------------ binding

        /// <summary>Binds to a director and rig, and drops any selection: re-pointing is not a moment at which items may move.</summary>
        public void Bind(RunDirector director, PlayerRig rig)
        {
            _director = director;
            _rig = rig;
            _inventory = rig != null ? rig.Inventory : null;
            _equipment = rig != null ? rig.Equipment : null;

            if (_inventory == null && director != null) _inventory = director.Inventory;

            Clear();
        }

        /// <summary>
        /// Re-reads the live director's rig. Returns true when the binding changed. bagChanged is true only when
        /// a different bag replaced an existing one (a half-bound rig being completed keeps its selection); the
        /// caller then clears its selection widgets. The selection is dropped here in that case.
        /// </summary>
        public bool EnsureLiveRig(out bool bagChanged)
        {
            bagChanged = false;
            // A client screen belongs to its replicated owner, never the disabled scene rig.
            if (NetRole.IsClientOnly && _rig != null) return false;
            if (_director == null) GameServices.TryGet<RunDirector>(out _director);

            PlayerRig live = _director != null ? _director.Player : null;
            if (live == null) return false;

            Inventory bag = live.Inventory;
            Equipment worn = live.Equipment;

            if (ReferenceEquals(_rig, live) && _inventory == bag && _equipment == worn) return false;

            bagChanged = _inventory != null && bag != null && !ReferenceEquals(_inventory, bag);

            _rig = live;
            if (bag != null) _inventory = bag;
            if (worn != null) _equipment = worn;

            if (bagChanged) Clear();
            return true;
        }

        /// <summary>Points the screen at another bag. Returns true (and drops the selection) when the bag actually changed.</summary>
        public bool RebindInventory(Inventory bag)
        {
            if (ReferenceEquals(_inventory, bag)) return false;
            _inventory = bag;
            Clear();
            return true;
        }

        // ------------------------------------------------------------ selection

        public void SetSelection(InventorySlot slot)
        {
            _selectedStackId = slot.StackId;
            _selectedItem = slot.Item;
            _selectedOrigin = slot.Origin;
            _selectedAmount = slot.Count;
            _selectionBag = _inventory;
        }

        /// <summary>Selects an in-bag stack by index when it is a real, countable, identified stack.</summary>
        public bool TrySelectIndex(int index)
        {
            if (_inventory == null || index < 0 || index >= _inventory.Slots.Count) return false;

            InventorySlot slot = _inventory.Slots[index];
            if (slot.Item == null || slot.Count <= 0 || slot.StackId == 0) return false;

            SetSelection(slot);
            return true;
        }

        /// <summary>Selects the exact gear instance returned by an accepted unequip command.</summary>
        public void SelectReturnedStack(Guid uid)
        {
            if (_inventory == null || uid == Guid.Empty) return;

            for (int i = 0; i < _inventory.Slots.Count; i++)
            {
                InventorySlot slot = _inventory.Slots[i];
                if (slot.Instance == null || slot.Instance.Uid != uid) continue;
                SetSelection(slot);
                return;
            }
        }

        public bool TryResolve(out int index)
        {
            index = -1;
            if (_selectedStackId == 0 || _inventory == null) return false;
            if (!ReferenceEquals(_selectionBag, _inventory)) return false;

            int resolved = _inventory.IndexOfStack(_selectedStackId);
            if (resolved < 0 || !_inventory.TryGetStack(_selectedStackId, out InventorySlot slot)
                || slot.StackId != _selectedStackId || slot.Item == null || slot.Count <= 0)
                return false;

            index = resolved;
            _selectedItem = slot.Item;
            _selectedOrigin = slot.Origin;
            _selectedAmount = slot.Count;
            return true;
        }

        /// <summary>True when a selection existed but no longer resolves; it is cleared here and the caller redraws.</summary>
        public bool ValidateSelection()
        {
            if (_selectedStackId == 0) return false;
            if (TryResolve(out _)) return false;

            Clear();
            return true;
        }

        public void Clear()
        {
            _selectedStackId = 0;
            _selectedItem = null;
            _selectedOrigin = new Vector2Int(-1, -1);
            _selectedAmount = 0;
            _selectionBag = null;
        }

        // ------------------------------------------------------------ commands

        public bool Submit(InventoryCommandKind kind, InventorySlot source, InventorySlot target,
            Vector2Int destinationCell, EquipmentSlot destinationSlot, Guid expectedSourceGearUid,
            Guid expectedTargetGearUid, out string reason)
        {
            reason = null;
            IInventoryAuthority authority = _inventory != null ? _inventory.CommandAuthority : null;
            if (authority == null || _rig == null)
            {
                reason = "the inventory command authority is unavailable";
                return false;
            }

            Guid sourceUid = source.Instance != null ? source.Instance.Uid : expectedSourceGearUid;
            Guid targetUid = target.Instance != null ? target.Instance.Uid : Guid.Empty;
            if (!authority.TryCreateLocalCommand(kind, source.StackId, target.StackId, destinationCell,
                    destinationSlot, sourceUid, expectedTargetGearUid != Guid.Empty ? expectedTargetGearUid : targetUid,
                    out InventoryCommand command, out reason))
                return false;

            InventoryCommandResult result = authority.Submit(_rig, command);
            if (!result.Accepted)
            {
                reason = result.Reason;
                return false;
            }

            reason = null;
            return true;
        }

        // ------------------------------------------------------------ hotbar (R6c2)

        public ulong StackIdAtCell(Vector2Int cell)
        {
            if (_inventory == null) return 0;
            int index = _inventory.IndexAtCell(cell);
            return index >= 0 && index < _inventory.Slots.Count ? _inventory.Slots[index].StackId : 0;
        }

        /// <summary>
        /// Binds <paramref name="hoveredStackId"/> - or the live selection when it is zero - to the hotbar slot,
        /// moving it off any other slot. The caller has already validated the selection and checked the screen is open.
        /// </summary>
        public bool TryAssignHotbar(int hotbarSlot, ulong hoveredStackId)
        {
            ulong target = hoveredStackId;
            if (target == 0 && TryResolve(out _)) target = _selectedStackId;

            if (target == 0 || !_inventory.TryGetStack(target, out InventorySlot stack))
            {
                GameEvents.RaiseToast(ToastOwner, "Point at or select a usable item to bind it to the hotbar");
                return false;
            }

            if (!_inventory.Hotbar.TryAssign(hotbarSlot, target, out string reason))
            {
                GameEvents.RaiseToast(ToastOwner, reason);
                return false;
            }

            GameEvents.RaiseToast(ToastOwner, $"{stack.Item.displayName} bound to hotbar {hotbarSlot + 1}");
            return true;
        }
    }
}
