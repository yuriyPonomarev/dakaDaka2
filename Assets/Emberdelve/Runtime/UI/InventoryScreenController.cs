using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Flow;
using Emberdelve.Items;
using Emberdelve.Player;
using Emberdelve.Stats;

namespace Emberdelve.UI
{
    /// <summary>
    /// The satchel and the worn kit, opened with I or Tab.
    ///
    /// The bag is a real grid: an item occupies its footprint cells, a stack occupies one footprint, and a
    /// placement is only accepted when every cell it needs is free.
    ///
    /// The cursor holds a SELECTION, not an item. Clicking a stack records its stable runtime identity; the
    /// stack stays in the bag, at its own cells, with its own count, until an operation actually commits.
    /// Nothing is lifted out of the bag to be "carried", so there is no removed stack, no stale index to
    /// trust afterwards, and nothing to drop, duplicate or credit when the screen is closed, cancelled or
    /// disabled. Every operation resolves that identity against the live bag first, because a container, a
    /// hotkey or a death can move the bag underneath the cursor between two clicks.
    ///
    /// Clicks are measured against the rectangles that are actually rendered, through RectTransformUtility and
    /// the owning canvas's camera, so a scaled or offset canvas still routes to the cell the player sees, and
    /// the gaps between cells and the area outside the panel are not cells at all.
    ///
    /// Everything drawn here is read from the inventory and equipment state. The screen never stores item
    /// data of its own, so a future server-authoritative inventory renders unchanged.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class InventoryScreenController : MonoBehaviour
    {
        private const float CellSize = 46f;
        private const float CellGap = 2f;
        // Shared paper-doll geometry keeps own and looted equipment in identical places.
        private const float PanelWidth = LootPanelStyle.Width;
        private const float PanelHeight = LootPanelStyle.Height;
        private const float PanelCenterX = 190f;
        private const float PanelCenterY = 0f;
        private const float PanelInset = 16f;
        private const float ColumnGap = 10f;
        private const float HeaderHeight = 26f;
        private const float DetailHeight = 30f;
        // The instruction band needs three 13pt lines (the selected-item hint); the detail line only ever wraps to two.
        private const float CarryHeight = 62f;

        // The satchel screen has two tabs in the header band: the bag, which is everything above, and the
        // character pane, which reuses the bag's own column (grid + detail + instructions) as its drawing
        // area. Nothing moves, so the accepted grid, the six worn slots and their labels land exactly where
        // the pointer fixtures already click them.
        private const float TooltipPanelWidth = 250f;
        private const float TooltipLineHeight = 16f;
        private const float TooltipPadding = 8f;
        private const float TooltipPointerOffset = 14f;
        private const float TooltipEdgeMargin = 4f;

        private const float TabButtonWidth = 92f;
        private const float TabButtonHeight = 26f;
        private const float TabGap = 10f;
        private const float TabHeaderGap = 10f;
        private const float HeaderTextWidth = 260f;

        // The instruction band is split rather than lengthened: the tooltip line takes the top of the band
        // the instructions already own, so the column height - and with it the grid - does not move.
        private const float TooltipHeight = 20f;
        private const float CarryTooltipGap = 4f;
        private const float InstructionHeight = CarryHeight - TooltipHeight - CarryTooltipGap;

        private const float PanePadding = 10f;

        private static readonly Color PanelColor = new Color(0.055f, 0.05f, 0.045f, 1f);
        private static readonly Color CellColor = new Color(0.13f, 0.12f, 0.11f, 1f);
        private static readonly Color CellBlockedColor = new Color(0.2f, 0.13f, 0.1f, 1f);
        private static readonly Color SlotColor = new Color(0.1f, 0.11f, 0.13f, 1f);
        private static readonly Color TextColor = new Color(0.88f, 0.84f, 0.78f, 1f);
        private static readonly Color DimColor = new Color(0.6f, 0.56f, 0.5f, 1f);
        private static readonly Color SelectedColor = new Color(0.95f, 0.83f, 0.42f, 1f);
        private static readonly Color PaneColor = new Color(0.075f, 0.07f, 0.062f, 1f);
        private static readonly Color TabIdleColor = new Color(0.13f, 0.12f, 0.11f, 1f);
        private static readonly Color TabActiveColor = new Color(0.3f, 0.23f, 0.12f, 1f);

        // R6d: the non-view half (binding, selection, commands, hotbar assignment) lives in the model; the
        // names below are read-only views of its binding so the drawing code reads as before.
        private readonly InventoryScreenModel _model = new InventoryScreenModel();
        private RunDirector _director => _model.Director;
        private PlayerRig _rig => _model.Rig;
        private Inventory _inventory => _model.Inventory;
        private Equipment _equipment => _model.Equipment;

        private Canvas _canvas;
        private ulong _splitStackId, _splitRevision;
        private int _splitAmount;
        private bool _splitChoosingCell;
        private RectTransform _splitDialog;
        private UnityEngine.UI.Text _splitAmountText;
        private UnityEngine.UI.Image _splitGhost;
        public bool IsSplitting => _splitStackId != 0;

        private RectTransform _root;
        private RectTransform _panel;
        private Image _backdrop;
        private RectTransform _gridGroup;
        private RectTransform _equipGroup;
        private Text _headerLabel;
        private Text _detailLabel;
        private Text _carryLabel;
        private Image _carryGhost;

        // The character pane and the item tooltip. Both are built once, like every other widget here, and
        // afterwards only re-labelled: a reopen must not produce a second copy of either.
        private RectTransform _statsGroup;
        private Image _statsPane;
        private CharacterStatsView _statsView;
        private CharacterSidebar _sidebar;
        private Text _tooltipLabel;
        private Button _bagTab;
        private Button _characterTab;
        private Image _bagTabGraphic;
        private Image _characterTabGraphic;
        private Text _bagTabLabel;
        private Text _characterTabLabel;
        // Tooltips are keyed on the ItemInstance under the pointer (uid, rarity, rolls), not on its ItemData, so
        // two drops of one base item show their own affixes. A carried stack with no identity falls back to
        // its definition.
        private ItemInstance _hoverInstance;
        private ItemData _hoverDefinition;
        private ItemTooltipModel _currentTooltip;
        private Image _tipPanel;
        private Text _tipBody;
        private Vector2 _tipAnchor;
        private bool _tipFromSlot;
        private float _equipColumnLeftRoot;
        private bool _showCharacter;

        private readonly List<Image> _cellImages = new List<Image>(64);
        private readonly List<Image> _itemImages = new List<Image>(24);
        private readonly List<Image> _equipImages = new List<Image>(6);
        private readonly List<Text> _equipLabels = new List<Text>(6);

        // The hit-test surfaces are the rectangles on screen, paired with the cell or slot they stand for.
        // A click is measured against these instead of against private layout arithmetic.
        private readonly List<RectTransform> _cellRects = new List<RectTransform>(64);
        private readonly List<Vector2Int> _cellCoords = new List<Vector2Int>(64);
        private readonly List<RectTransform> _equipRects = new List<RectTransform>(6);
        private readonly List<EquipmentSlot> _equipSlots = new List<EquipmentSlot>(6);

        public bool IsOpen { get; private set; }
        public static int LastCloseFrame => InventoryInputFocus.LastCloseFrame;
        public PlayerRig BoundRig { get { EnsureLiveRig(); return _rig; } }
        public bool IsLootCompanion { get; private set; }
        private void LateUpdate()
        {
            if (_root != null && IsOpen) _root.localScale = Vector3.one * LootPanelStyle.InventoryScale(IsLootCompanion);
        }

        private bool RunEnded => _director != null && (_director.State == RunState.Dead || _director.State == RunState.Extracted
            || _director.LocalPlayerState == RunState.Dead || _director.LocalPlayerState == RunState.Extracted);

        /// <summary>R7: the bag screen shows the bag of the player it is bound to; an event about another player never rebuilds it.</summary>
        private bool IsMine(int playerId) => NetRole.IsClientOnly && _rig != null
            ? playerId == _rig.PlayerId : PlayerView.Shows(playerId);

        private int ToastOwner => _model.ToastOwner;

        /// <summary>The stable identity currently selected in the bag; zero means no live selection.</summary>
        public ulong SelectedStackId => TryResolveSelection(out _) ? _model.SelectedStackId : 0;

        /// <summary>The stack the cursor is pointing at. It is still carried in the bag.</summary>
        public ItemData SelectedItem => TryResolveSelection(out _) ? _model.SelectedItem : null;
        public Vector2Int SelectedOrigin => TryResolveSelection(out _) ? _model.SelectedOrigin : new Vector2Int(-1, -1);
        public int SelectedAmount => TryResolveSelection(out _) ? _model.SelectedAmount : 0;
        public bool HasSelection => TryResolveSelection(out _);

        /// <summary>Old cursor API, kept as a read-only view of the selection: nothing is held outside the bag.</summary>
        public ItemData CarriedItem => SelectedItem;
        public int CarriedAmount => SelectedAmount;

        private Vector2 _gridOrigin;

        public void Configure(RunDirector director, PlayerRig rig)
        {
            // Re-pointing the screen at another rig is not a moment at which items may move anywhere:
            // the model drops the selection as it binds.
            _model.Bind(director, rig);
            ClearSelection();
            Build();
        }

        private void OnEnable()
        {
            GameEvents.InventoryChanged += OnInventoryChanged;
            GameEvents.EquipmentChanged += OnEquipmentChanged;
            GameEvents.RunStateChanged += OnRunState;
            GameEvents.PlayerRunStateChanged += OnPlayerRunState;
        }

        private void OnDisable()
        {
            Close(false);
            GameEvents.InventoryChanged -= OnInventoryChanged;
            GameEvents.EquipmentChanged -= OnEquipmentChanged;
            GameEvents.RunStateChanged -= OnRunState;
            GameEvents.PlayerRunStateChanged -= OnPlayerRunState;

            // Dying, pausing out of the run or the HUD being torn down must not leave a selection behind
            // that someone later tries to put somewhere. The stack was never removed, so clearing is free.
            ClearSelection();
        }

        private void OnDestroy()
        {
            InventoryInputFocus.Close(this);
            if (_root != null) Destroy(_root.gameObject);
        }

        public void SetLootCompanion(bool enabled)
        {
            IsLootCompanion = enabled;
            if (_panel == null) Build();
            _panel.anchoredPosition = new Vector2(enabled ? -80f : 190f, 0f);
            if (_backdrop != null) _backdrop.gameObject.SetActive(!enabled);
            if (_director != null && _rig != null && _rig.IsAlive && !_rig.IsExtracted && _director.State == RunState.Running)
                _director.SetInventoryOpen(enabled);
            if (enabled) { _root.SetAsLastSibling(); SelectBagTab(); Open(); }
            Rebuild();
        }

        public void Open()
        {
            if (IsOpen || RunEnded) return;
            IsOpen = true;
            InventoryInputFocus.Open(this);

            if (_root != null) _root.gameObject.SetActive(true);
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            Rebuild();
        }

        public void Close()
        {
            Close(true);
        }

        /// <summary>
        /// Closes the screen. relockCursor is false when the run has ended: the result screen needs a free
        /// pointer, so the cursor is left exactly as the run layer set it.
        /// </summary>
        public void Close(bool relockCursor)
        {
            if (!IsOpen) return;
            IsOpen = false;
            CancelSplit();
            InventoryInputFocus.Close(this);

            // Closing drops the selection, not an item: the selected stack never left the bag, so there is
            // nothing to return, nothing to spawn at the player's feet and nothing to credit as loot.
            ClearSelection();

            // The floating tooltip belongs to the screen: it goes with it and is rebuilt on the next hover.
            _hoverInstance = null;
            _hoverDefinition = null;
            _currentTooltip = null;
            if (_tipPanel != null) _tipPanel.gameObject.SetActive(false);

            if (_root != null) _root.gameObject.SetActive(false);
            if (relockCursor)
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }
        }

        /// <summary>
        /// A finished run (death, extraction) or a return to the lobby shuts the satchel and its tooltip at
        /// once, so nothing under the result screen is left open or clickable. The cursor stays free.
        /// </summary>
        private void OnPlayerRunState(int playerId, RunState state)
        {
            if (playerId == GameEvents.NoPlayer || !IsMine(playerId)) return;
            OnRunState(state);
        }

        private void OnRunState(RunState state)
        {
            if (state == RunState.Dead || state == RunState.Extracted || state == RunState.Lobby)
                Close(false);
        }

        public void Toggle()
        {
            if (IsOpen) Close();
            else Open();
        }

        private void OnInventoryChanged(int playerId)
        {
            if (IsOpen && IsMine(playerId)) Rebuild();
        }

        private void OnEquipmentChanged(int playerId, ItemData item, bool equipped)
        {
            if (IsOpen && IsMine(playerId)) Rebuild();
        }

        /// <summary>
        /// Re-points the screen at the rig the run is playing right now, and reports whether anything
        /// actually changed. The HUD builds this screen in its own Awake, which is not a moment at which
        /// the run layer has necessarily registered the director, wired the rig or applied the class, so a
        /// binding taken there can be missing the rig, or hold a rig whose bag and kit are not resolved
        /// yet. Reading the live director again is a read only: no item, pool, score or roll is touched,
        /// the widgets are not rebuilt here, and the selection is dropped only when the bag it names is a
        /// different bag - never when a half-bound rig is merely completed.
        /// </summary>
        private bool EnsureLiveRig()
        {
            if (!_model.EnsureLiveRig(out bool bagChanged)) return false;

            // The pane reads the rig through the view, so the view is re-pointed at the same object the
            // screen just bound to before any line of it is labelled or refreshed.
            if (_statsView != null) _statsView.Configure(_rig);

            // The model already dropped the selection; this hides the ghost that drew it.
            if (bagChanged) ClearSelection();

            return true;
        }

        /// <summary>
        /// Reads mouse clicks while the screen is open. Called by the HUD every frame so this screen does not
        /// need its own Input Action map: the bag is presentation and input routing stays in one place.
        /// </summary>
        private bool _dragArmed;
        private Vector2 _dragStart;
        public void CancelCarry() => CancelSelection();

        public void HandlePointer()
        {
            if (!IsOpen) return;
            if (_rig != null && _rig.Input != null && _rig.Input.QuickWheelCaptured) return;
            if (!IsLootCompanion && Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
            {
                if (IsSplitting) { CancelSplit(); Rebuild(); return; }
                Close();
                _director?.SetInventoryOpen(false);
                return;
            }

            // Belt and braces for the run-state event: a satchel still open after the run ended closes here.
            if (RunEnded) { Close(false); return; }

            // The run layer owns the pointer while the satchel is closed and hands it over in Open(). A
            // resume from pause re-locks it as part of unpausing, and it does that after the pause
            // notification the HUD is listening to, so a satchel that stayed open through the pause would
            // be left with a hidden, re-centred cursor and every click aimed at a widget landing on the
            // crosshair instead. This screen is the one that holds the pointer while it is open, so it
            // re-asserts that on the next frame. Closed, this method is not reached and the cursor stays
            // locked by the run layer.
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            // The rig the run is actually playing is not necessarily the rig this screen was pointed at:
            // the HUD builds the screen in its own Awake, which is not a moment at which the run layer has
            // finished wiring the rig or applied the class. Re-reading it here is what lets the pane show
            // the live scores and pools instead of the ones the binding was missing when it was built.
            if (EnsureLiveRig()) Rebuild();

            // The character pane is a live read-out: pools regenerate, a cast spends mana, a gear change
            // re-derives the scores, and all of it happens while the panel is open. This is the frame the
            // HUD already hands the screen, so the pane needs no input action or Update of its own.
            if (_showCharacter && _statsView != null) _statsView.Refresh();

            Mouse mouse = Mouse.current;
            if (mouse == null) return;

            if (_rig != null && _rig.GetComponent<HotbarController>() is HotbarController activeWheel && activeWheel.WheelOpen) return;
            Vector2 screen = mouse.position.ReadValue();
            if (IsSplitting)
            {
                HandleSplitPointer(screen, mouse);
                return;
            }
            if (!_showCharacter && mouse.leftButton.wasPressedThisFrame && Keyboard.current != null &&
                Keyboard.current.ctrlKey.isPressed && TryCellAt(screen, out var splitCell))
            {
                int splitIndex = _inventory.IndexAtCell(splitCell);
                if (splitIndex >= 0) BeginSplit(_inventory.Slots[splitIndex]);
                return;
            }


            UpdateGhost(screen);
            ShowTooltipAt(screen);

            // A frame boundary is a legal moment for the bag to change underneath the cursor: a container, a
            // consumable hotkey, an equipment change or a scripted transfer can all move a stack. The
            // selection is re-resolved before anything is done with it.
            ValidateSelection();

            if (mouse.leftButton.wasPressedThisFrame)
            {
                bool wasCarrying = HasSelection;
                HandleClick(screen, false);
                _dragArmed = !wasCarrying && HasSelection;
                _dragStart = screen;
            }
            else if (mouse.rightButton.wasPressedThisFrame)
            {
                _dragArmed = false;
                if (Keyboard.current != null && Keyboard.current.shiftKey.isPressed) TryDropAt(screen);
                else HandleClick(screen, true);
            }
            if (mouse.leftButton.wasReleasedThisFrame && _dragArmed)
            {
                _dragArmed = false;
                if ((screen - _dragStart).sqrMagnitude > 36f && HasSelection) HandleClick(screen, false);
            }

            HandleHotbarKeys(screen);
            HandleDropKey(screen);
        }

        /// <summary>U5A: while the satchel is open, Q lays the item under the pointer (a worn slot or a bag cell) on the ground. It is the same sequenced Drop command a client would send; the authority decides.</summary>
        private void HandleDropKey(Vector2 screen)
        {
            if (!IsOpen || _showCharacter) return;
            Keyboard keyboard = Keyboard.current;
            if (keyboard != null && keyboard.qKey.wasPressedThisFrame) TryDropAt(screen);
        }

        /// <summary>
        /// U5A: drops the gear under <paramref name="screenPosition"/> - a worn equipment slot (the main hand, the off-hand shield, clothing) or a bag
        /// cell - through the Drop inventory command. A refusal (the authority's reason) is shown as a toast and changes nothing.
        /// </summary>
        public bool TryDropAt(Vector2 screenPosition)
        {
            if (!IsOpen || _inventory == null || !_model.CommandsAllowed) return false;
            ValidateSelection();

            string reason = null;
            bool accepted = false;
            if (TryEquipSlotAt(screenPosition, out EquipmentSlot slot))
            {
                ItemInstance worn = _equipment != null ? _equipment.EquippedInstance(slot) : null;
                if (worn == null) return false;
                accepted = SubmitInventoryCommand(InventoryCommandKind.Drop, default, default, Vector2Int.zero, slot, worn.Uid, Guid.Empty, out reason);
            }
            else if (TryCellAt(screenPosition, out Vector2Int cell))
            {
                int index = _inventory.IndexAtCell(cell);
                if (index < 0) return false;
                InventorySlot source = _inventory.Slots[index];
                InventoryCommandKind dropKind = source.Instance != null ? InventoryCommandKind.Drop
                    : (Keyboard.current != null && Keyboard.current.ctrlKey.isPressed
                        ? InventoryCommandKind.DropOne : InventoryCommandKind.DropStack);
                accepted = SubmitInventoryCommand(dropKind, source, default, Vector2Int.zero, EquipmentSlot.None, Guid.Empty, Guid.Empty, out reason);
            }
            else return false;

            if (!accepted) GameEvents.RaiseToast(ToastOwner, reason);
            RefreshAfterRefusal();
            return accepted;
        }

        /// <summary>
        /// R6c2: while the satchel is open, holding 3/4 opens a wheel to assign the stack under the pointer (or, with the pointer on no
        /// stack, the selected stack) to that hotbar slot. The run layer switches the player action map off while
        /// the satchel is open, so the same keys cannot also use an item; this reads the keyboard device directly,
        /// as HandlePointer reads the mouse, because that action map is not live.
        /// </summary>
        private void HandleHotbarKeys(Vector2 screen)
        {
            if (!IsOpen || _showCharacter || _rig == null || !_model.CommandsAllowed) return;
            var input = _rig.Input;
            var controller = _rig.GetComponent<HotbarController>();
            if (input == null || controller == null) return;
            ulong stack = TryCellAt(screen, out Vector2Int cell) ? _model.StackIdAtCell(cell) : 0;
            if (stack == 0) stack = _model.SelectedStackId;
            for (int group = 0; group < 2; group++)
                if (input.QuickWheelPressed(group) && !controller.OpenAssignmentWheel(group, stack))
                    GameEvents.RaiseToast(ToastOwner, "Point at a potion, ammo pack or torch, then hold 3 or 4");
        }

        /// <summary>
        /// The assignment adapter used by focused UI checks. Binds the stack
        /// under <paramref name="screenPosition"/> - falling back to the selected stack when the pointer is over no
        /// stack - to hotbar slot <paramref name="hotbarSlot"/> (0-5) by StackId, moving it off any other slot.
        /// An unusable or vanished stack is refused with a toast and nothing changes.
        /// </summary>
        public bool TryAssignHotbarSlot(int hotbarSlot, Vector2 screenPosition)
        {
            if (!IsOpen || _inventory == null) return false;
            if (!_model.CommandsAllowed) return false;

            ValidateSelection();

            ulong hovered = TryCellAt(screenPosition, out Vector2Int cell) ? _model.StackIdAtCell(cell) : 0;
            return _model.TryAssignHotbar(hotbarSlot, hovered);
        }

        /// <summary>
        /// The one click entry point. The HUD's mouse reading and the focused UI checks both arrive here, so
        /// both travel the same path: hit-test the rendered rectangles with the owning canvas camera, then run
        /// the same move / swap / merge / equip / unequip / cancel operations.
        /// </summary>
        public void HandleClick(Vector2 screenPosition, bool rightClick)
        {
            if (!IsOpen) return;

            // A finished run is not a moment at which to rearrange a bag: dying, extracting or a result
            // screen handing control elsewhere must not leave a live cursor that can still shuffle gear.
            // Letting go is always safe, because the selected stack never left the bag.
            if (!_model.CommandsAllowed)
            {
                if (HasSelection) CancelSelection();
                return;
            }

            ValidateSelection();

            if (rightClick) HandleRightClick(screenPosition);
            else HandleLeftClick(screenPosition);
        }

        private void UpdateGhost(Vector2 screen)
        {
            if (_carryGhost == null) return;

            // The ghost names a stack in the grid. The character tab switches the grid - and with it the
            // ghost - off, so a selection that survived the tab switch must not bring it back over the pane.
            bool carrying = HasSelection && !_showCharacter;
            _carryGhost.gameObject.SetActive(carrying);
            if (!carrying) return;

            Vector2 local;
            if (_root != null && RectTransformUtility.ScreenPointToLocalPointInRectangle(_root, screen, CanvasCamera, out local))
                _carryGhost.rectTransform.anchoredPosition = local;
        }

        // ------------------------------------------------------------ clicks

        private void HandleLeftClick(Vector2 screen)
        {
            if (TryEquipSlotAt(screen, out EquipmentSlot slot))
            {
                ClickEquipSlot(slot);
                return;
            }

            if (TryCellAt(screen, out Vector2Int cell))
            {
                ClickCell(cell);
                return;
            }

            // A click on the panel, on a gap between cells or outside the screen is a no-op. It does not
            // cancel a selection and it does not move anything.
        }

        private void ClickEquipSlot(EquipmentSlot slot)
        {
            if (_equipment == null)
            {
                GameEvents.RaiseToast(ToastOwner, "Nothing is wearing anything");
                return;
            }

            if (HasSelection)
            {
                if (!TryResolveSelection(out int sourceIndex))
                {
                    CancelSelection();
                    return;
                }

                InventorySlot source = _inventory.Slots[sourceIndex];
                if (source.Item == null || !EquipmentSlots.Accepts(source.Item.equipSlot, slot))
                {
                    GameEvents.RaiseToast(ToastOwner, $"{source.Item?.displayName ?? "Item"} does not fit the {slot} slot");
                    return;
                }

                ItemInstance worn = _equipment.EquippedInstance(slot);
                Guid targetUid = worn != null ? worn.Uid : Guid.Empty;
                if (SubmitInventoryCommand(InventoryCommandKind.Equip, source, default, Vector2Int.zero,
                        slot, Guid.Empty, targetUid, out string reason))
                {
                    // The authority commits the bag/slot transfer before its change notifications. The
                    // selected stack ID retires as that exact gear instance leaves the bag.
                    ClearSelection();
                    Rebuild();
                }
                else
                {
                    GameEvents.RaiseToast(ToastOwner, reason);
                    RefreshAfterRefusal();
                }
                return;
            }

            ItemData wornDefinition = _equipment.Equipped(slot);
            if (wornDefinition == null) return;

            ItemInstance wornInstance = _equipment.EquippedInstance(slot);
            if (wornInstance == null)
            {
                GameEvents.RaiseToast(ToastOwner, "The worn item has no stable identity");
                return;
            }

            Guid returnedUid = wornInstance.Uid;
            if (SubmitInventoryCommand(InventoryCommandKind.Unequip, default, default, Vector2Int.zero,
                    slot, returnedUid, Guid.Empty, out string unequipReason))
            {
                SelectReturnedStack(returnedUid);
                Rebuild();
            }
            else
            {
                GameEvents.RaiseToast(ToastOwner, unequipReason);
                RefreshAfterRefusal();
            }
        }

        private void ClickCell(Vector2Int cell)
        {
            if (_inventory == null) return;

            int targetIndex = _inventory.IndexAtCell(cell);
            if (!HasSelection)
            {
                if (targetIndex >= 0) SelectStack(targetIndex);
                return;
            }

            if (!TryResolveSelection(out int sourceIndex))
            {
                CancelSelection();
                return;
            }

            InventorySlot source = _inventory.Slots[sourceIndex];
            if (targetIndex >= 0)
            {
                InventorySlot target = _inventory.Slots[targetIndex];
                if (target.StackId == _model.SelectedStackId)
                {
                    // Clicking the selected stack again is the grid's cancel gesture.
                    CancelSelection();
                    return;
                }

                InventoryCommandKind kind = target.Item == source.Item && source.Item != null && source.Item.CanStack
                    ? InventoryCommandKind.Merge : InventoryCommandKind.Swap;
                if (SubmitInventoryCommand(kind, source, target, Vector2Int.zero, EquipmentSlot.None,
                        Guid.Empty, Guid.Empty, out string reason))
                {
                    ClearSelection();
                    Rebuild();
                }
                else
                {
                    GameEvents.RaiseToast(ToastOwner, reason);
                    RefreshAfterRefusal();
                }
                return;
            }

            // Empty-cell moves resolve the selected ID at command time, so a prior move, resize or index
            // compaction cannot redirect this click to a lookalike definition.
            if (SubmitInventoryCommand(InventoryCommandKind.Move, source, default, cell, EquipmentSlot.None,
                    Guid.Empty, Guid.Empty, out string moveReason))
            {
                ClearSelection();
                Rebuild();
            }
            else
            {
                GameEvents.RaiseToast(ToastOwner, moveReason);
                RefreshAfterRefusal();
            }
        }

        private void QuickTransferUtility(InventorySlot source)
        {
            string reason = null;
            bool accepted = false;
            if (_inventory.IsBeltCell(source.Origin))
            {
                Vector2Int free = _inventory.FindFreeOrigin(source.Item.EffectiveFootprint);
                if (free.x < 0) reason = "No room in the bag; the item stays in its belt slot";
                else accepted = SubmitInventoryCommand(InventoryCommandKind.Move, source, default, free,
                    EquipmentSlot.None, Guid.Empty, Guid.Empty, out reason);
            }
            else
            {
                int destination = -1;
                for (int i = 0; i < QuickBeltRules.SlotCount; i++)
                    if (_inventory.TryGetBeltStack(i, out var other) && other.Item == source.Item &&
                        other.Item.CanStack && other.Count < QuickBeltRules.StackLimit(other.Item)) { destination = i; break; }
                if (destination < 0)
                    for (int i = 0; i < QuickBeltRules.SlotCount; i++)
                        if (!_inventory.TryGetBeltStack(i, out _)) { destination = i; break; }
                if (destination < 0) reason = "The utility belt is full; drag onto a slot to swap";
                else accepted = _inventory.Hotbar.TryAssign(destination, source.StackId, out reason);
            }
            if (!accepted) GameEvents.RaiseToast(ToastOwner, reason);
            ClearSelection(); Rebuild();
        }

        private void HandleRightClick(Vector2 screen)
        {
            // The established gesture is cancel-first: while a selection exists, only a right-click on
            // equippable bag gear performs another action. Keep that selection if the authority refuses.
            if (HasSelection)
            {
                bool overEquippable = false;
                if (TryCellAt(screen, out Vector2Int selectedTabCell)
                    && !TryEquipSlotAt(screen, out _) && _inventory != null)
                {
                    int selectedTargetIndex = _inventory.IndexAtCell(selectedTabCell);
                    overEquippable = selectedTargetIndex >= 0
                        && _inventory.Slots[selectedTargetIndex].Item != null
                        && _inventory.Slots[selectedTargetIndex].Item.IsEquippable;
                }
                if (!overEquippable)
                {
                    CancelSelection();
                    return;
                }
            }

            if (TryEquipSlotAt(screen, out EquipmentSlot slot))
            {
                ItemData wornDefinition = _equipment != null ? _equipment.Equipped(slot) : null;
                if (wornDefinition == null) return;

                ItemInstance worn = _equipment.EquippedInstance(slot);
                if (worn == null)
                {
                    GameEvents.RaiseToast(ToastOwner, "The worn item has no stable identity");
                    return;
                }

                Guid returnedUid = worn.Uid;
                if (SubmitInventoryCommand(InventoryCommandKind.Unequip, default, default, Vector2Int.zero,
                        slot, returnedUid, Guid.Empty, out string reason))
                {
                    SelectReturnedStack(returnedUid);
                    Rebuild();
                }
                else
                {
                    GameEvents.RaiseToast(ToastOwner, reason);
                    RefreshAfterRefusal();
                }
                return;
            }

            if (TryCellAt(screen, out Vector2Int cell))
            {
                int index = _inventory != null ? _inventory.IndexAtCell(cell) : -1;
                if (index < 0) return;

                InventorySlot source = _inventory.Slots[index];
                if (source.Item == null) return;

                if (QuickBeltRules.CanStore(source.Item))
                {
                    QuickTransferUtility(source);
                    return;
                }

                if (HotbarBindings.IsUsableDefinition(source.Item))
                {
                    if (_inventory.RequestQuickStack(source.StackId, out string holdReason))
                    {
                        Close();
                        _director?.SetInventoryOpen(false);
                    }
                    else GameEvents.RaiseToast(ToastOwner, holdReason);
                    return;
                }

                if (source.Item.IsEquippable)
                {
                    if (_equipment == null)
                    {
                        GameEvents.RaiseToast(ToastOwner, "nothing to wear the item");
                        return;
                    }

                    // D3b: a ring goes to the first empty ring slot; with both full the player must click the ring slot to
                    // replace (an explicit target), so nothing is replaced by a right click on the bag.
                    if (!_equipment.TryChooseSlot(source.Item, EquipmentSlot.None, out EquipmentSlot equipTarget, out string chooseReason))
                    {
                        GameEvents.RaiseToast(ToastOwner, chooseReason);
                        RefreshAfterRefusal();
                        return;
                    }

                    ItemInstance worn = _equipment.EquippedInstance(equipTarget);
                    Guid targetUid = worn != null ? worn.Uid : Guid.Empty;
                    if (SubmitInventoryCommand(InventoryCommandKind.Equip, source, default, Vector2Int.zero,
                            equipTarget, Guid.Empty, targetUid, out string equipReason))
                    {
                        ClearSelection();
                        Rebuild();
                    }
                    else
                    {
                        GameEvents.RaiseToast(ToastOwner, equipReason);
                        RefreshAfterRefusal();
                    }
                    return;
                }

                GameEvents.RaiseToast(ToastOwner, "This item cannot be held or used");
            }
        }

        // ------------------------------------------------------------ tabs and tooltip

        /// <summary>Reached through the button's own onClick, i.e. the canvas raycaster and the InputSystem module.</summary>
        private void SelectBagTab()
        {
            if (IsSplitting || !_showCharacter) return;
            _showCharacter = false;
            _hoverInstance = null; // the tooltip was hidden with the tab; forget it so the next frame re-shows it without a pointer move
            _hoverDefinition = null;
            ApplyTabVisibility();
            Rebuild();
        }

        private void SelectCharacterTab()
        {
            if (IsSplitting || _showCharacter) return;
            _showCharacter = true;
            ApplyTabVisibility();
            Rebuild();
        }

        /// <summary>
        /// Switching tabs only switches which group is on screen. The bag keeps drawing its cells even while
        /// the pane is up, so the pointer fixtures, the six worn slots and every bag operation are exactly
        /// where they were; the hit-test already ignores a rectangle that is not active.
        ///
        /// The pane covers the whole bag column below the header, which is the column the detail line, the
        /// tooltip line and the instructions are drawn in. Leaving them active put a glyph or two of bag
        /// text in the 10px inset between the panel edge and the pane, so they are switched with the grid
        /// and switched back with it. Their rectangles, their text and the hovered item they were naming are
        /// untouched, and the header, the two tabs and all six worn-slot rows stay exactly where they are
        /// on both tabs.
        /// </summary>
        private void ApplyTabVisibility()
        {
            if (_gridGroup != null) _gridGroup.gameObject.SetActive(!_showCharacter);
            if (_equipGroup != null) _equipGroup.gameObject.SetActive(!_showCharacter);
            if (_statsGroup != null) _statsGroup.gameObject.SetActive(_showCharacter);

            if (_detailLabel != null) _detailLabel.gameObject.SetActive(!_showCharacter);
            if (_tooltipLabel != null) _tooltipLabel.gameObject.SetActive(!_showCharacter);
            if (_tipPanel != null && _showCharacter) _tipPanel.gameObject.SetActive(false);
            if (_carryLabel != null) _carryLabel.gameObject.SetActive(!_showCharacter);

            // The ghost names a stack in the grid. With the grid off screen it would float over the pane,
            // and Rebuild puts it back over the footprint it names when the bag tab comes back.
            if (_carryGhost != null && _showCharacter) _carryGhost.gameObject.SetActive(false);

            Color idle = TabIdleColor;
            Color active = TabActiveColor;

            if (_bagTabGraphic != null) _bagTabGraphic.color = _showCharacter ? idle : active;
            if (_characterTabGraphic != null) _characterTabGraphic.color = _showCharacter ? active : idle;
            if (_bagTabLabel != null) _bagTabLabel.color = _showCharacter ? DimColor : TextColor;
            if (_characterTabLabel != null) _characterTabLabel.color = _showCharacter ? TextColor : DimColor;
        }

        private void PositionTabs(float headerTop)
        {
            float innerLeft = -PanelWidth * 0.5f + PanelInset;
            float centreY = headerTop - TabButtonHeight * 0.5f;

            if (_bagTabGraphic != null)
            {
                _bagTabGraphic.rectTransform.anchoredPosition =
                    PanelToRoot(new Vector2(innerLeft + TabButtonWidth * 0.5f, centreY));
            }

            if (_characterTabGraphic != null)
            {
                _characterTabGraphic.rectTransform.anchoredPosition =
                    PanelToRoot(new Vector2(innerLeft + TabButtonWidth + TabGap + TabButtonWidth * 0.5f, centreY));
            }
        }

        /// <summary>
        /// The tooltip follows the pointer through the same hit-test the clicks already use: the cell or the
        /// worn slot under the cursor names the item INSTANCE, and the tooltip reports that instance's rarity,
        /// its fixed properties and its own rolled affixes (U2B11). Returns the model now on screen (null when
        /// nothing is hovered). Nothing here selects, moves, equips or re-rolls anything.
        /// </summary>
        public ItemTooltipModel ShowTooltipAt(Vector2 screen)
        {
            ItemInstance instance = null;
            ItemData definition = null;
            bool overSlot = false;

            if (TryEquipSlotAt(screen, out EquipmentSlot slot))
            {
                overSlot = true;
                if (_equipment != null)
                {
                    instance = _equipment.EquippedInstance(slot);
                    definition = _equipment.Equipped(slot);
                }
            }
            else if (TryCellAt(screen, out Vector2Int cell))
            {
                int index = _inventory != null ? _inventory.IndexAtCell(cell) : -1;
                if (index >= 0 && index < _inventory.Slots.Count)
                {
                    instance = _inventory.Slots[index].Instance;
                    definition = _inventory.Slots[index].Item;
                }
            }

            if (instance == null && definition == null) ResolveSelectedItem(out instance, out definition);
            _tipFromSlot = overSlot;

            if (_root != null && RectTransformUtility.ScreenPointToLocalPointInRectangle(_root, screen, CanvasCamera, out Vector2 local))
                _tipAnchor = local;

            if (!ReferenceEquals(instance, _hoverInstance) || definition != _hoverDefinition)
            {
                _hoverInstance = instance;
                _hoverDefinition = definition;
                ApplyTooltip(instance, definition);
            }
            else
            {
                PositionTooltipPanel();
            }

            return _currentTooltip;
        }

        /// <summary>The instance (or, for a stack with no identity, the definition) the current selection names, if it still resolves.</summary>
        private void ResolveSelectedItem(out ItemInstance instance, out ItemData definition)
        {
            instance = null;
            definition = null;

            if (_model.SelectedStackId == 0 || !TryResolveSelection(out int index)) return;

            instance = _inventory.Slots[index].Instance;
            definition = _inventory.Slots[index].Item;
        }

        /// <summary>The tooltip on screen right now, for the focused UI checks: null when none is shown.</summary>
        public ItemTooltipModel CurrentTooltip => _currentTooltip;

        /// <summary>True when the bag or a worn slot still holds this exact instance (by uid), or, for a stack with no identity, this definition.</summary>
        private bool IsCarriedOrWorn(ItemInstance instance, ItemData definition)
        {
            if (instance != null)
            {
                if (_equipment != null && _equipment.HoldsUid(instance.Uid)) return true;
                return _inventory != null && _inventory.HoldsInstanceUid(instance.Uid);
            }

            if (definition == null) return false;
            if (_equipment != null && _equipment.IsEquipped(definition)) return true;
            return _inventory != null && _inventory.IndexOfItem(definition) >= 0;
        }

        private void ApplyTooltip(ItemInstance instance, ItemData definition)
        {
            ItemTooltipModel model = instance != null ? ItemTooltip.ForInstance(instance) : ItemTooltip.ForDefinition(definition);
            _currentTooltip = model;

            if (_tooltipLabel != null)
            {
                _tooltipLabel.text = model == null ? string.Empty : (model.HasRarity ? model.RarityName + "  " : "") + model.Title;
                _tooltipLabel.color = model != null && model.HasRarity ? model.RarityColor : TextColor;
            }

            if (_tipPanel == null || _tipBody == null) return;

            bool show = model != null && !_showCharacter;
            _tipPanel.gameObject.SetActive(show);
            if (!show) return;

            string sectionColor = ColorUtility.ToHtmlStringRGB(new Color(0.84f, 0.72f, 0.5f, 1f));
            string rarityColor = ColorUtility.ToHtmlStringRGB(model.RarityColor);

            System.Text.StringBuilder builder = new System.Text.StringBuilder(160);
            int lines = 0;

            builder.Append("<color=#").Append(rarityColor).Append("><b>").Append(model.Title).Append("</b></color>\n");
            lines++;
            if (model.HasRarity)
            {
                builder.Append("<color=#").Append(rarityColor).Append('>').Append(model.RarityName).Append("</color>");
                if (model.SlotName.Length > 0) builder.Append("   ").Append(model.SlotName);
                builder.Append('\n');
                lines++;
            }
            else if (model.SlotName.Length > 0)
            {
                builder.Append(model.SlotName).Append('\n');
                lines++;
            }

            builder.Append("<color=#").Append(sectionColor).Append('>').Append(ItemTooltipModel.FixedHeader).Append("</color>\n");
            lines++;
            for (int i = 0; i < model.FixedLines.Count; i++)
            {
                builder.Append("  ").Append(model.FixedLines[i]).Append('\n');
                lines++;
            }

            if (model.HasRolledSection)
            {
                builder.Append("<color=#").Append(sectionColor).Append('>').Append(ItemTooltipModel.RolledHeader).Append("</color>\n");
                lines++;
                for (int i = 0; i < model.RolledDisplayLines.Count; i++)
                {
                    builder.Append("  <color=#").Append(rarityColor).Append('>').Append(model.RolledDisplayLines[i]).Append("</color>\n");
                    lines++;
                }
            }

            _tipBody.text = builder.ToString().TrimEnd('\n');
            _tipPanel.rectTransform.sizeDelta = new Vector2(TooltipPanelWidth, lines * TooltipLineHeight + 2f * TooltipPadding);
            PositionTooltipPanel();
        }

        /// <summary>
        /// Parks the tooltip panel next to the pointer and clamps the whole rectangle inside the satchel panel,
        /// so it can never hang past the panel (and with it the window) at any resolution.
        /// </summary>
        private void PositionTooltipPanel()
        {
            if (_tipPanel == null || !_tipPanel.gameObject.activeSelf) return;

            Vector2 size = _tipPanel.rectTransform.sizeDelta;
            float left = _panel.anchoredPosition.x - PanelWidth * 0.5f + TooltipEdgeMargin;
            float right = _panel.anchoredPosition.x + PanelWidth * 0.5f - TooltipEdgeMargin;
            float top = PanelCenterY + PanelHeight * 0.5f - TooltipEdgeMargin;
            float bottom = PanelCenterY - PanelHeight * 0.5f + TooltipEdgeMargin;

            // P4c: keep clear of the worn-kit column. Over a worn slot the tooltip sits beside the column (left of
            // it); over the grid it is clamped so its right edge stops at the column.
            if (_equipColumnLeftRoot > left + size.x) right = Mathf.Min(right, _equipColumnLeftRoot - TooltipEdgeMargin * 2f);
            float wantedX = _tipFromSlot ? right - size.x : _tipAnchor.x + TooltipPointerOffset;
            float x = Mathf.Clamp(wantedX, left, Mathf.Max(left, right - size.x));
            float y = Mathf.Clamp(_tipAnchor.y - TooltipPointerOffset, Mathf.Min(top, bottom + size.y), top);

            _tipPanel.rectTransform.anchoredPosition = new Vector2(x, y);
        }

        // ------------------------------------------------------------ selection

        private void BeginSplit(InventorySlot source)
        {
            if (!_model.CommandsAllowed || source.Instance != null || source.Item == null || source.Count < 2) return;
            ClearSelection();
            _dragArmed = false;
            _splitStackId = source.StackId;
            _splitRevision = _inventory.Revision;
            _splitAmount = 1;
            _splitChoosingCell = false;
            if (_tipPanel != null) _tipPanel.gameObject.SetActive(false);
            if (_splitDialog == null)
            {
                _splitDialog = UiFactory.CreatePanel(_root, "SplitAmount", new Color(.08f,.07f,.055f,.98f));
                _splitDialog.anchorMin = _splitDialog.anchorMax = new Vector2(.5f,.5f);
                _splitDialog.pivot = new Vector2(.5f,.5f);
                _splitDialog.sizeDelta = new Vector2(360,220);
                _splitDialog.anchoredPosition = Vector2.zero;
                var title = UiFactory.CreateText(_splitDialog,"Title","SPLIT STACK",21,new Color(.9f,.8f,.55f));
                title.rectTransform.sizeDelta = new Vector2(330,32); title.rectTransform.anchoredPosition = new Vector2(0,80);
                _splitAmountText = UiFactory.CreateText(_splitDialog,"Amount","",18,Color.white);
                _splitAmountText.rectTransform.sizeDelta = new Vector2(230,56);
                _splitAmountText.rectTransform.anchoredPosition = new Vector2(0,24);
                SplitButton("Less","-",new Vector2(-140,20),new Vector2(42,40),()=>ChangeSplitAmount(-1));
                SplitButton("More","+",new Vector2(140,20),new Vector2(42,40),()=>ChangeSplitAmount(1));
                SplitButton("Place","CHOOSE CELL",new Vector2(78,-62),new Vector2(166,40),()=>
                {
                    if (!ValidateSplitSource(out _)) { CancelSplit(); return; }
                    _splitChoosingCell = true; _splitDialog.gameObject.SetActive(false);
                });
                SplitButton("Cancel","CANCEL",new Vector2(-100,-62),new Vector2(130,40),()=> { CancelSplit(); Rebuild(); });
            }
            _splitDialog.gameObject.SetActive(true);
            _splitDialog.SetAsLastSibling();
            ChangeSplitAmount(0);
        }

        private void SplitButton(string name, string label, Vector2 position, Vector2 size, UnityEngine.Events.UnityAction click)
        {
            var button = UiFactory.CreateButton(_splitDialog,name,label,click,size,new Color(.25f,.21f,.13f),Color.white,15);
            ((RectTransform)button.transform).anchoredPosition = position;
        }

        private bool ValidateSplitSource(out InventorySlot source)
        {
            source = default;
            return IsOpen && _model.CommandsAllowed && _inventory != null && _inventory.Revision == _splitRevision &&
                _inventory.TryGetStack(_splitStackId,out source) && source.Instance == null && source.Item != null &&
                source.Count > _splitAmount && _splitAmount > 0;
        }

        private void ChangeSplitAmount(int delta)
        {
            if (!ValidateSplitSource(out var source)) { CancelSplit(); return; }
            _splitAmount = Mathf.Clamp(_splitAmount+delta,1,source.Count-1);
            _splitAmountText.text = source.Item.displayName+"\n"+_splitAmount+" / "+source.Count;
        }

        private void CancelSplit()
        {
            _splitStackId = 0; _splitChoosingCell = false;
            if (_splitDialog != null) _splitDialog.gameObject.SetActive(false);
            if (_splitGhost != null) { Destroy(_splitGhost.gameObject); _splitGhost = null; }
        }

        private void HandleSplitPointer(Vector2 screen, Mouse mouse)
        {
            if (!ValidateSplitSource(out var source))
            { CancelSplit(); Rebuild(); GameEvents.RaiseToast(ToastOwner,"Inventory changed; select the stack again"); return; }
            if (mouse.rightButton.wasPressedThisFrame) { CancelSplit(); Rebuild(); return; }
            if (!_splitChoosingCell) return;
            if (_splitGhost == null)
            {
                _splitGhost = UiFactory.CreateImage(_root,"SplitCarry",Color.white,
                    new Vector2(source.Size.x*CellSize,source.Size.y*CellSize),Vector2.zero,new Vector2(.5f,.5f),new Vector2(.5f,.5f));
                LootPanelStyle.Decorate(_splitGhost,source.Item,null);
                foreach(var graphic in _splitGhost.GetComponentsInChildren<UnityEngine.UI.Graphic>()) graphic.raycastTarget=false;
            }
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(_root,screen,CanvasCamera,out var point))
                _splitGhost.rectTransform.anchoredPosition=point;
            bool hasCell = TryCellAt(screen,out var cell);
            _splitGhost.color=hasCell && _inventory.CanPlaceItem(source.Item,_splitAmount,cell) ? new Color(.15f,.45f,.25f,.85f) : new Color(.55f,.15f,.12f,.85f);
            _splitGhost.transform.SetAsLastSibling();
            if (_carryLabel != null) _carryLabel.text="Place "+source.Item.displayName+" x"+_splitAmount+" in a free cell. RMB / Esc: cancel";
            if (!mouse.leftButton.wasPressedThisFrame || !hasCell) return;
            bool accepted = _model.Submit(InventoryCommandKind.Split,source,default,cell,EquipmentSlot.None,
                Guid.Empty,Guid.Empty,out var reason,_splitAmount);
            if (accepted) { CancelSplit(); Rebuild(); }
            else GameEvents.RaiseToast(ToastOwner,reason);
        }

        private void SelectStack(int index)
        {
            if (!_model.TrySelectIndex(index)) return;
            Rebuild();
        }

        /// <summary>Selects the exact gear instance returned by an accepted unequip command.</summary>
        private void SelectReturnedStack(Guid uid)
        {
            _model.SelectReturnedStack(uid);
        }

        private bool TryResolveSelection(out int index)
        {
            return _model.TryResolve(out index);
        }

        private bool SubmitInventoryCommand(InventoryCommandKind kind, InventorySlot source, InventorySlot target,
            Vector2Int destinationCell, EquipmentSlot destinationSlot, Guid expectedSourceGearUid,
            Guid expectedTargetGearUid, out string reason)
        {
            return _model.Submit(kind, source, target, destinationCell, destinationSlot,
                expectedSourceGearUid, expectedTargetGearUid, out reason);
        }

        private void RefreshAfterRefusal()
        {
            ValidateSelection();
            Rebuild();
        }

        private void ValidateSelection()
        {
            if (!_model.ValidateSelection()) return;

            ClearSelection();
            Rebuild();
        }

        private void CancelSelection()
        {
            ClearSelection();
            Rebuild();
        }

        private void ClearSelection()
        {
            CancelSplit();
            _model.Clear();

            if (_carryGhost != null)
            {
                _carryGhost.enabled = false;
                _carryGhost.gameObject.SetActive(false);
            }
        }

        // ------------------------------------------------------------ geometry

        /// <summary>
        /// The camera a screen-space-camera or world-space canvas needs for screen-to-rectangle maths. An
        /// overlay canvas needs none, and feeding it the scene camera is what puts a click in the wrong cell.
        /// </summary>
        private Camera CanvasCamera
        {
            get
            {
                if (_canvas == null) return null;
                if (_canvas.renderMode == RenderMode.ScreenSpaceOverlay) return null;
                if (_canvas.worldCamera != null) return _canvas.worldCamera;
                return Camera.main;
            }
        }

        /// <summary>
        /// Hit-tests the cell rectangles that are actually rendered. Screen pixels are converted by
        /// RectTransformUtility, which accounts for the canvas scale factor, the offset and size of the group
        /// the cells live in, any parent rotation or scale, and the canvas render mode. A click in a gap, on
        /// the panel border or outside the grid belongs to no cell, so it stays a no-op.
        /// </summary>
        public bool TryCellAt(Vector2 screen, out Vector2Int cell)
        {
            cell = Vector2Int.zero;

            Camera camera = CanvasCamera;

            for (int i = 0; i < _cellRects.Count; i++)
            {
                RectTransform rect = _cellRects[i];
                if (rect == null || !rect.gameObject.activeInHierarchy) continue;
                if (!RectTransformUtility.RectangleContainsScreenPoint(rect, screen, camera)) continue;

                cell = _cellCoords[i];
                return true;
            }

            return false;
        }

        private bool TryEquipSlotAt(Vector2 screen, out EquipmentSlot slot)
        {
            slot = EquipmentSlot.None;

            if (_equipment == null) return false;

            Camera camera = CanvasCamera;

            for (int i = 0; i < _equipRects.Count; i++)
            {
                RectTransform rect = _equipRects[i];
                if (rect == null || !rect.gameObject.activeInHierarchy) continue;
                if (!RectTransformUtility.RectangleContainsScreenPoint(rect, screen, camera)) continue;

                slot = _equipSlots[i];
                return true;
            }

            return false;
        }

        /// <summary>
        /// Every real slot in enum order (Weapon, OffHand, Head, Chest, Legs, Back), derived from the enum so a
        /// slot appended to <see cref="EquipmentSlot"/> gets a row without another hard-coded list (U2B11).
        /// </summary>
        private static EquipmentSlot[] SlotOrder()
        {
            List<EquipmentSlot> order = new List<EquipmentSlot>(Equipment.SlotCount);
            for (int i = 1; i < Equipment.SlotCount; i++) order.Add((EquipmentSlot)i);
            return order.ToArray();
        }

        // ------------------------------------------------------------ build

        private void Build()
        {
            if (_root != null) return;

            Canvas canvas = UiFactory.EnsureCanvas("Emberdelve_Hud", 10);
            _canvas = canvas;

            _root = UiFactory.CreateGroup(canvas.transform, "InventoryRoot");
            _root.gameObject.SetActive(false);

            _backdrop = UiFactory.CreateImage(_root,"InventoryBackdrop",new Color(0,0,0,.92f),new Vector2(6000,4000),Vector2.zero,new Vector2(.5f,.5f),new Vector2(.5f,.5f));
            _panel = UiFactory.CreatePanel(_root, "SatchelPanel", PanelColor);
            _panel.sizeDelta = new Vector2(PanelWidth, PanelHeight);
            _panel.anchoredPosition = new Vector2(PanelCenterX, PanelCenterY);
            LootPanelStyle.Panel(_panel.GetComponent<Image>());

            var sideRoot = UiFactory.CreateGroup(_root, "CharacterSidebar");
            _sidebar = sideRoot.gameObject.AddComponent<CharacterSidebar>();
            _sidebar.Build();
            _gridGroup = UiFactory.CreateGroup(_root, "GridGroup");
            _equipGroup = UiFactory.CreateGroup(_root, "EquipGroup");

            // InventoryRoot is a zero-size group, so every anchor point inside it coincides with its centre:
            // the pivot is what makes an offset describe the corner it is named for. Pivot (0,1) turns the
            // offsets below into the widget's top-left corner inside the panel, which is what a left-aligned
            // label needs. The previous centre pivot put the label's centre at the panel inset and half of
            // the label outside the panel.
            _headerLabel = UiFactory.CreateText(_root, "Header", "INVENTORY", 16, TextColor, TextAnchor.UpperLeft);
            _headerLabel.rectTransform.anchorMin = new Vector2(0.5f, 0.5f);
            _headerLabel.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            _headerLabel.rectTransform.pivot = new Vector2(0f, 1f);
            _headerLabel.rectTransform.sizeDelta = new Vector2(420f, HeaderHeight);

            // The detail and instruction lines are wrapped into their column: a single unwrapped line of
            // "grid 10x4 ... worn: ... modifiers: ..." is far wider than the panel and ran off the window.
            _detailLabel = UiFactory.CreateText(_root, "Detail", "", 16, DimColor, TextAnchor.UpperLeft);
            _detailLabel.rectTransform.anchorMin = new Vector2(0.5f, 0.5f);
            _detailLabel.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            _detailLabel.rectTransform.pivot = new Vector2(0f, 1f);
            _detailLabel.rectTransform.sizeDelta = new Vector2(700f, DetailHeight);
            _detailLabel.horizontalOverflow = HorizontalWrapMode.Wrap;
            _detailLabel.verticalOverflow = VerticalWrapMode.Truncate;

            _carryLabel = UiFactory.CreateText(_root, "Carry", "", 13, TextColor, TextAnchor.UpperLeft);
            _carryLabel.rectTransform.anchorMin = new Vector2(0.5f, 0.5f);
            _carryLabel.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            _carryLabel.rectTransform.pivot = new Vector2(0f, 1f);
            _carryLabel.rectTransform.sizeDelta = new Vector2(420f, CarryHeight);
            _carryLabel.horizontalOverflow = HorizontalWrapMode.Wrap;
            _carryLabel.verticalOverflow = VerticalWrapMode.Truncate;

            _carryGhost = UiFactory.CreateImage(_root, "CarryGhost", Color.white,
                new Vector2(CellSize, CellSize), Vector2.zero, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));
            _carryGhost.enabled = false;
            _carryGhost.raycastTarget = false;
            _carryGhost.gameObject.SetActive(false);

            // The tooltip line lives in the same column as the instructions it sits above, with the same
            // top-left pivot, so it cannot run past the panel edge any more than they can.
            _tooltipLabel = UiFactory.CreateText(_root, "Tooltip", "", 14, TextColor, TextAnchor.UpperLeft);
            _tooltipLabel.rectTransform.anchorMin = new Vector2(0.5f, 0.5f);
            _tooltipLabel.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            _tooltipLabel.rectTransform.pivot = new Vector2(0f, 1f);
            _tooltipLabel.rectTransform.sizeDelta = new Vector2(420f, TooltipHeight);
            _tooltipLabel.horizontalOverflow = HorizontalWrapMode.Wrap;
            _tooltipLabel.verticalOverflow = VerticalWrapMode.Truncate;
            _tooltipLabel.raycastTarget = false;

            // The character pane is one contained rectangle over the bag's own column, with its rows parented
            // inside it. It is created once here and re-labelled afterwards, and it is switched as a unit.
            _statsGroup = UiFactory.CreateGroup(_root, "StatsGroup");
            _statsGroup.gameObject.SetActive(false);

            _statsPane = UiFactory.CreateImage(_statsGroup.transform, "StatsPane", PaneColor, Vector2.zero,
                Vector2.zero, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));
            _statsPane.raycastTarget = false;

            _statsView = new CharacterStatsView(_statsGroup.transform, MeasureLeftColumnWidth() - 2f * PanePadding);

            // Ordinary uGUI buttons on the accepted canvas: the GraphicRaycaster and the InputSystemUIInputModule
            // UiFactory already ensures drive them, so a tab is a click like any other button in the game.
            // UiFactory's default colour block is a lobby-style translucent highlight; on a tab that would
            // make the chip go see-through while the pointer is over it, so the multipliers are set to keep
            // each tab's own colour and let the tab state be carried by the colour the screen applies.
            _bagTab = UiFactory.CreateButton(_root, "TabBag", "BAG", SelectBagTab,
                new Vector2(TabButtonWidth, TabButtonHeight), TabActiveColor, TextColor, 15);
            _characterTab = UiFactory.CreateButton(_root, "TabCharacter", "CHARACTER", SelectCharacterTab,
                new Vector2(TabButtonWidth, TabButtonHeight), TabIdleColor, TextColor, 15);
            _bagTabLabel = _bagTab != null ? _bagTab.GetComponentInChildren<Text>() : null;
            _characterTabLabel = _characterTab != null ? _characterTab.GetComponentInChildren<Text>() : null;
            _bagTabGraphic = _bagTab != null ? _bagTab.targetGraphic as Image : null;
            _characterTabGraphic = _characterTab != null ? _characterTab.targetGraphic as Image : null;
            ConfigureTabButton(_bagTab);
            ConfigureTabButton(_characterTab);

            // The floating tooltip panel is created last so it draws above every other widget. It never receives
            // pointer events, and it is clamped inside the satchel panel when it is positioned.
            _tipPanel = UiFactory.CreateImage(_root, "TooltipPanel", new Color(0.045f, 0.04f, 0.036f, 1f),
                new Vector2(TooltipPanelWidth, 80f), Vector2.zero, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));
            _tipPanel.rectTransform.pivot = new Vector2(0f, 1f);
            DungeonUiStyle.Surface(_tipPanel);
            _tipPanel.raycastTarget = false;
            _tipBody = UiFactory.CreateText(_tipPanel.transform, "TooltipBody", string.Empty, 13, TextColor, TextAnchor.UpperLeft);
            _tipBody.rectTransform.anchorMin = Vector2.zero;
            _tipBody.rectTransform.anchorMax = Vector2.one;
            _tipBody.rectTransform.offsetMin = new Vector2(TooltipPadding, TooltipPadding);
            _tipBody.rectTransform.offsetMax = new Vector2(-TooltipPadding, -TooltipPadding);
            _tipBody.horizontalOverflow = HorizontalWrapMode.Wrap;
            _tipBody.verticalOverflow = VerticalWrapMode.Overflow;
            _tipBody.supportRichText = true;
            _tipBody.raycastTarget = false;
            _tipPanel.gameObject.SetActive(false);

            ApplyTabVisibility();
            Rebuild();
        }

        /// <summary>
        /// A tab is a state, not a lobby call to action: its chip keeps its own colour and the highlight only
        /// brightens it a little, instead of the shared button block multiplying the chip's alpha down to a
        /// ghost of the panel behind it.
        /// </summary>
        private static void ConfigureTabButton(Button button)
        {
            if (button == null) return;

            ColorBlock block = button.colors;
            block.normalColor = Color.white;
            block.highlightedColor = new Color(1.15f, 1.15f, 1.15f, 1f);
            block.pressedColor = new Color(0.9f, 0.9f, 0.9f, 1f);
            block.selectedColor = Color.white;
            button.colors = block;
        }

        /// <summary>
        /// The width the bag's own column has between the panel's left inset and the worn-kit column, measured
        /// by the same numbers the layout uses. The character pane and its rows are sized from it so the two
        /// tabs cannot disagree about how wide their shared column is.
        /// </summary>
        private static float MeasureLeftColumnWidth()
        {
            return PanelWidth - 2f * PanelInset;
        }

        /// <summary>
        /// Panel-local placement (origin at the panel centre, y up) converted to the root-local space these
        /// widgets are actually parented in. The panel is a child of the zero-size InventoryRoot, so its
        /// anchoredPosition is its centre in that space.
        /// </summary>
        private Vector2 PanelToRoot(Vector2 panelLocal)
        {
            Vector2 panelCentre = _panel != null ? _panel.anchoredPosition : Vector2.zero;
            return panelLocal + panelCentre;
        }

        /// <summary>
        /// Redraws the satchel from the live bag and worn kit. Public because it is the screen's own redraw
        /// entry point (the same reason it was public before the U1C2C layout work); making it private was
        /// unrelated to containing the panel.
        /// </summary>
        private static Vector2 BeltPosition(int slot)
        {
            return new Vector2((slot < 3 ? -184f : 80f) + (slot % 3) * 52f, 18f);
        }

        public void Rebuild()
        {
            if (_root == null) return;

            // Every line below reads the bag, the worn kit and the rig through this screen's own binding,
            // so the binding is resolved from the live director before anything is drawn or re-labelled.
            EnsureLiveRig();

            if (_inventory == null) return;

            // InventoryChanged and EquipmentChanged can arrive synchronously inside a command. Retain a
            // surviving StackId at its new location; clear only when that exact identity has disappeared.
            if (_model.SelectedStackId != 0 && !TryResolveSelection(out _)) ClearSelection();

            foreach (Image image in _cellImages) if (image != null) Destroy(image.gameObject);
            foreach (Image image in _itemImages) if (image != null) Destroy(image.gameObject);
            foreach (Image image in _equipImages) if (image != null) Destroy(image.gameObject);
            foreach (Text text in _equipLabels) if (text != null) Destroy(text.gameObject);

            _cellImages.Clear();
            _itemImages.Clear();
            _equipImages.Clear();
            _equipLabels.Clear();
            _cellRects.Clear();
            _cellCoords.Clear();
            _equipRects.Clear();
            _equipSlots.Clear();

            int width = _inventory.GridWidth;
            int height = _inventory.GridHeight;
            float step = CellSize + CellGap;

            float gridWidth = width * step - CellGap;
            float gridHeight = height * step - CellGap;

            // ---- panel-local layout, measured from the panel's inner bounds
            float innerLeft = -PanelWidth * 0.5f + PanelInset;
            float innerRight = PanelWidth * 0.5f - PanelInset;
            float innerTop = PanelHeight * 0.5f - PanelInset;

            EquipmentSlot[] order = SlotOrder();
            float leftColumnWidth = PanelWidth - PanelInset * 2f;
            float headerTop = innerTop;
            float gridTop = -18f;
            float detailTop = gridTop - gridHeight - ColumnGap;
            float carryTop = detailTop - DetailHeight - ColumnGap;
            if (_panel != null) _panel.sizeDelta = new Vector2(PanelWidth, Mathf.Max(PanelHeight, 2f * (gridHeight + 136f)));

            Vector2 gridCentrePanel = new Vector2(innerLeft + gridWidth * 0.5f, gridTop - gridHeight * 0.5f);
            Vector2 gridCenter = PanelToRoot(gridCentrePanel);

            _gridOrigin = gridCenter - new Vector2(gridWidth * 0.5f, gridHeight * 0.5f);

            _gridGroup.anchoredPosition = gridCenter;

            for (int row = 0; row < height; row++)
            {
                for (int col = 0; col < width; col++)
                {
                    Vector2 center = _gridOrigin + new Vector2(col * step + CellSize * 0.5f,
                        gridHeight - (row * step + CellSize * 0.5f));

                    bool occupied = _inventory.IsCellOccupied(new Vector2Int(col, row));

                    Image cell = UiFactory.CreateImage(_gridGroup.transform, $"Cell_{col}_{row}",
                        occupied ? CellBlockedColor : CellColor,
                        new Vector2(CellSize, CellSize), center - gridCenter,
                        new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));

                    _cellImages.Add(cell);
                    _cellRects.Add(cell.rectTransform);
                    _cellCoords.Add(new Vector2Int(col, row));
                }
            }

            // Six physical utility slots share authority with the bag but occupy no bag cells.
            for (int belt = 0; belt < HotbarBindings.SlotCount; belt++)
            {
                Vector2 center = PanelToRoot(BeltPosition(belt));
                Vector2Int coordinate = _inventory.BeltCell(belt);
                bool occupied = _inventory.TryGetBeltStack(belt, out _);
                Image cell = UiFactory.CreateImage(_gridGroup, "BeltCell_" + belt,
                    occupied ? CellBlockedColor : CellColor, Vector2.one * CellSize,
                    center - gridCenter, Vector2.one * .5f, Vector2.one * .5f);
                LootPanelStyle.Frame(cell, LootPanelStyle.Gold);
                _cellImages.Add(cell); _cellRects.Add(cell.rectTransform); _cellCoords.Add(coordinate);
                if (!occupied)
                {
                    Text hint = UiFactory.CreateText(cell.transform, "EmptyBeltSlot", (belt % 3 + 1).ToString(), 12, DimColor);
                    hint.raycastTarget = false;
                }
            }
            for (int group = 0; group < 2; group++)
            {
                Text label = UiFactory.CreateText(_gridGroup, "BeltGroup_" + group,
                    (group + 3) + "  QUICK SLOTS", 13, TextColor, TextAnchor.MiddleCenter, FontStyle.Bold);
                label.raycastTarget = false;
                UiFactory.SetRect(label.rectTransform, Vector2.one * .5f, Vector2.one * .5f,
                    new Vector2(156, 16), PanelToRoot(new Vector2(group == 0 ? -132 : 132, 49)) - gridCenter);
                _equipLabels.Add(label);
            }

            // Root-local centre of the selected stack, kept outside the slot loop so the ghost block below
            // can park the no-mouse ghost over the footprint it actually names.
            Vector2 selectedCenter = Vector2.zero;
            bool hasSelectedCenter = false;

            for (int i = 0; i < _inventory.Slots.Count; i++)
            {
                InventorySlot slot = _inventory.Slots[i];
                if (slot.Item == null) continue;

                bool inBelt = _inventory.IsBeltCell(slot.Origin);
                Vector2Int size = inBelt ? Vector2Int.one : slot.Size;
                Vector2 center = inBelt ? PanelToRoot(BeltPosition(slot.Origin.x - width)) : _gridOrigin + new Vector2(
                    slot.Origin.x * step + (size.x * CellSize + (size.x - 1) * CellGap) * 0.5f,
                    gridHeight - (slot.Origin.y * step + (size.y * CellSize + (size.y - 1) * CellGap) * 0.5f));

                Vector2 blockSize = new Vector2(size.x * CellSize + (size.x - 1) * CellGap,
                    size.y * CellSize + (size.y - 1) * CellGap);

                Color color = slot.Item.iconColor;
                color.a = 1f;

                // The selected stack is marked where it still stands in the bag. It is not lifted out and it
                // is not drawn twice as two normal owned copies: the bag copy is dimmed inside a frame of
                // selection colour, and only the ghost at the pointer is translucent.
                bool isSelected = _model.SelectedStackId != 0 && slot.StackId == _model.SelectedStackId;
                if (isSelected) color.a = 0.58f;

                if (isSelected)
                {
                    Image frame = UiFactory.CreateImage(_gridGroup.transform, "Selected_" + slot.Item.displayName,
                        SelectedColor, blockSize + new Vector2(6f, 6f), center - gridCenter,
                        new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));
                    frame.raycastTarget = false;
                    // Tracked with the other transient item visuals, which Rebuild destroys at its start, so
                    // repeated rebuild/cancel cannot stack orphan selection frames in the grid group.
                    _itemImages.Add(frame);

                    // 'center' is already in _root local space: _gridGroup has centre anchors/pivot at
                    // gridCenter with zero size, and the block sits at center - gridCenter inside it.
                    // The ghost is a _root child with centre anchors, so this is the same space its
                    // anchoredPosition (and UpdateGhost's pointer reading) uses.
                    selectedCenter = center;
                    hasSelectedCenter = true;
                }

                Image block = UiFactory.CreateImage(_gridGroup.transform, "Item_" + slot.Item.displayName, color,
                    blockSize, center - gridCenter, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));
                block.raycastTarget = false;

                LootPanelStyle.Decorate(block, slot.Item, slot.Instance);
                _itemImages.Add(block);

                if (slot.Count > 1 || inBelt)
                {
                    string quantity = inBelt && slot.Item.healAmount > 0 ? slot.Count + "/5" : slot.Count.ToString();
                    Text count = UiFactory.CreateText(block.transform, "Count", quantity, 13, Color.white,
                        TextAnchor.LowerRight, FontStyle.Bold);
                    count.rectTransform.anchorMin = Vector2.zero;
                    count.rectTransform.anchorMax = Vector2.one;
                    count.rectTransform.offsetMin = new Vector2(3f, 1f);
                    count.rectTransform.offsetMax = new Vector2(-3f, -1f);
                }
            }

            // Equipment mirrors the looted player paper doll.
            Vector2 equipCenter = PanelToRoot(new Vector2(0, 40));
            if (_sidebar != null) { ((RectTransform)_sidebar.transform).anchoredPosition = PanelToRoot(Vector2.zero) + new Vector2(-450,0); _sidebar.Bind(_rig); }
            _equipGroup.anchoredPosition = equipCenter;
            _equipGroup.localScale = Vector3.one * .85f;
            _equipColumnLeftRoot = 0f;

            for (int i = 0; i < order.Length; i++)
            {
                EquipmentSlot kind = order[i];
                Vector2 center = equipCenter + LootPanelStyle.SlotPosition(kind);

                ItemData worn = _equipment != null ? _equipment.Equipped(kind) : null;

                Color color = worn != null ? worn.iconColor : SlotColor;
                color.a = worn != null ? 1f : 1f;

                Image slotImage = UiFactory.CreateImage(_equipGroup.transform, "Equip_" + kind, color,
                    LootPanelStyle.SlotSize(kind), center - equipCenter,
                    new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));

                LootPanelStyle.Decorate(slotImage, worn, _equipment != null ? _equipment.EquippedInstance(kind) : null, false);
                if (worn == null) LootPanelStyle.EmptySlot(slotImage, kind);
                _equipImages.Add(slotImage);
                _equipRects.Add(slotImage.rectTransform);
                _equipSlots.Add(kind);


            }

            if (_headerLabel != null)
            {
                int worn = _equipment != null ? _equipment.FilledCount : 0;
                _headerLabel.text = _showCharacter
                    ? "CHARACTER"
                    : $"INVENTORY   {_inventory.UsedCells}/{_inventory.Capacity}";
                _headerLabel.rectTransform.sizeDelta = new Vector2(HeaderTextWidth, HeaderHeight);
                _headerLabel.rectTransform.anchoredPosition = PanelToRoot(new Vector2(innerLeft + TabButtonWidth * 2f + TabGap + TabHeaderGap, headerTop));
            }

            if (_detailLabel != null)
            {
                _detailLabel.text = BuildDetailText();
                _detailLabel.rectTransform.sizeDelta = new Vector2(leftColumnWidth, DetailHeight);
                _detailLabel.rectTransform.anchoredPosition = PanelToRoot(new Vector2(innerLeft, detailTop));
            }

            // The instruction band is split into the tooltip line and the instructions, inside the band the
            // instructions already owned: tooltip on top, instructions below it, both left-aligned at the
            // same inset, neither allowed past the column the panel measured.
            float tooltipTop = carryTop;
            float instructionTop = carryTop - TooltipHeight - CarryTooltipGap;

            if (_tooltipLabel != null)
            {
                _tooltipLabel.rectTransform.sizeDelta = new Vector2(leftColumnWidth, TooltipHeight);
                _tooltipLabel.rectTransform.anchoredPosition = PanelToRoot(new Vector2(innerLeft, tooltipTop));

                // A tooltip may only name something the run still has. A container, a hotkey or an equip can
                // take the hovered definition out of the bag between two frames, and a stale line about an
                // item the player no longer holds is a wrong number on screen just as much as a wrong stat.
                ItemInstance sourceInstance = _hoverInstance;
                ItemData sourceDefinition = _hoverDefinition;
                if (!IsCarriedOrWorn(sourceInstance, sourceDefinition)) ResolveSelectedItem(out sourceInstance, out sourceDefinition);
                if (!IsCarriedOrWorn(sourceInstance, sourceDefinition))
                {
                    sourceInstance = null;
                    sourceDefinition = null;
                }
                _hoverInstance = sourceInstance;
                _hoverDefinition = sourceDefinition;
                ApplyTooltip(sourceInstance, sourceDefinition);
            }

            if (_carryLabel != null)
            {
                _carryLabel.text = HasSelection
                    ? $"Selected {_model.SelectedItem.displayName} x{_model.SelectedAmount}\n" +
                      "Drag / click: place   RMB: cancel   hold 3/4: move to belt"
                    : "LMB: move   Ctrl+LMB: split   RMB: equip / belt\nShift+RMB / Q: drop (Ctrl: one)   Hold 3/4: belt";
                _carryLabel.rectTransform.sizeDelta = new Vector2(leftColumnWidth, InstructionHeight);
                _carryLabel.rectTransform.anchoredPosition = PanelToRoot(new Vector2(innerLeft, instructionTop));
            }

            // The character pane covers exactly the bag column below the header: the grid, the detail line,
            // the tooltip line and the instructions. Those widgets stay where they are and are simply drawn
            // under the pane while the character tab is up, so switching back puts them back untouched.
            float paneTop = innerTop - 40f;
            // Sized from the content as well as from the column: the rows start at the group origin and need
            // PreferredHeight; the extra padding keeps the last row's bottom inside the pane background (U2B11 review M1).
            float paneHeight = Mathf.Max(paneTop - (carryTop - CarryHeight), CharacterStatsView.PreferredHeight + PanePadding);
            float paneWidth = leftColumnWidth;

            if (_statsGroup != null)
                _statsGroup.anchoredPosition = PanelToRoot(new Vector2(innerLeft + PanePadding, paneTop - PanePadding));

            if (_statsPane != null)
            {
                _statsPane.rectTransform.sizeDelta = new Vector2(paneWidth, paneHeight);
                _statsPane.rectTransform.anchoredPosition = new Vector2(paneWidth * 0.5f - PanePadding, -paneHeight * 0.5f + PanePadding);
            }

            if (_statsView != null)
            {
                // Re-pointing at the current rig is a read, not a rebuild: the rows are built exactly once,
                // no matter how many times the satchel is opened, closed or refilled underneath.
                _statsView.Configure(_rig);
                if (_statsView.RowCount == 0) _statsView.Build();
                else _statsView.Refresh();
            }

            PositionTabs(headerTop);

            if (_carryGhost != null)
            {
                // ApplyTabVisibility switched the ghost off with the grid; this is the other place that
                // would put it back, so the same tab test applies here and the bag tab gets it back intact.
                if (HasSelection && !_showCharacter)
                {
                    Vector2Int shape = _model.SelectedItem.EffectiveFootprint;
                    Color ghostColor = _model.SelectedItem.iconColor;
                    ghostColor.a = 0.62f;

                    _carryGhost.gameObject.SetActive(true);
                    _carryGhost.enabled = true;
                    _carryGhost.color = ghostColor;
                    _carryGhost.rectTransform.sizeDelta = new Vector2(
                        shape.x * CellSize + (shape.x - 1) * CellGap,
                        shape.y * CellSize + (shape.y - 1) * CellGap);

                    // With a mouse the ghost follows the pointer every frame. With no pointer device there is
                    // nothing to follow, so it sits over the stack it names instead of at a stale position.
                    if (Mouse.current == null && hasSelectedCenter)
                        _carryGhost.rectTransform.anchoredPosition = selectedCenter;
                }
                else
                {
                    _carryGhost.enabled = false;
                    _carryGhost.gameObject.SetActive(false);
                }
            }
        }

        /// <summary>Fixed property lines of the selected item, in the same wording as the tooltip (no raw modifier text).</summary>
        private string SelectedSummary()
        {
            ItemTooltipModel model = ItemTooltip.ForDefinition(_model.SelectedItem);
            return model != null && model.FixedLines.Count > 0 ? string.Join(", ", model.FixedLines) : _model.SelectedItem.kind.ToString().ToLower();
        }

        private string BuildDetailText()
        {
            if (_inventory == null) return string.Empty;
            return $"Carried value  {_inventory.CarriedValue}     вЂў     Free space  {_inventory.FreeCells}/{_inventory.Capacity}";
        }
    }
}
