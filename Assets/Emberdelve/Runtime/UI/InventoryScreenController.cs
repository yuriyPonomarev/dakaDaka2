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
    /// The cursor holds a SELECTION, not an item. Clicking a stack names the stack it is pointing at; the
    /// stack stays in the bag, at its own cells, with its own count, until an operation actually commits.
    /// Nothing is lifted out of the bag to be "carried", so there is no removed stack, no stale index to
    /// trust afterwards, and nothing to drop, duplicate or credit when the screen is closed, cancelled or
    /// disabled. Every operation re-resolves the selection from the grid first, because a container, a
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
        // U2B11: six worn slots (Back joined the original five). 6 x (54 + 6) - 6 = 354 px, inside the panel's
        // 398 px inner height (the five-slot column was 5 x (64 + 8) - 8 = 352), so the column stays contained
        // at 720p and 1080p without growing the panel.
        private const float EquipSlotSize = 54f;
        private const float EquipSlotGap = 6f;

        // The satchel is laid out from the panel's own inner bounds rather than from a handful of offsets
        // that happened to look right at one window size. Everything below is measured from the panel, so the
        // grid, the six worn slots, their labels and the instructions all land inside the panel that is
        // already inside the window.
        private const float PanelWidth = 760f;
        private const float PanelHeight = 430f;
        private const float PanelCenterX = -40f;
        // 66 (was 20): lifts the panel so its bottom edge (y 509 from the top at 720p) clears the bottom-right skill cards
        // (top edge y 514) instead of hiding the Dash card under its corner.
        private const float PanelCenterY = 66f;
        private const float PanelInset = 16f;
        private const float ColumnGap = 10f;
        private const float EquipLabelGap = 10f;
        private const float EquipLabelWidth = 118f;
        private const float HeaderHeight = 26f;
        private const float DetailHeight = 56f;
        // The instruction band needs three 13pt lines (the selected-item hint); the detail line only ever wraps to two.
        private const float CarryHeight = 74f;

        // The satchel screen has two tabs in the header band: the bag, which is everything above, and the
        // character pane, which reuses the bag's own column (grid + detail + instructions) as its drawing
        // area. Nothing moves, so the accepted grid, the six worn slots and their labels land exactly where
        // the pointer fixtures already click them.
        private const float TooltipPanelWidth = 250f;
        private const float TooltipLineHeight = 16f;
        private const float TooltipPadding = 8f;
        private const float TooltipPointerOffset = 14f;
        private const float TooltipEdgeMargin = 4f;

        private const float TabButtonWidth = 94f;
        private const float TabButtonHeight = 26f;
        private const float TabGap = 10f;
        private const float TabHeaderGap = 10f;
        private const float HeaderTextWidth = 271f;

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

        private RunDirector _director;
        private PlayerRig _rig;
        private Inventory _inventory;
        private Equipment _equipment;

        private Canvas _canvas;
        private RectTransform _root;
        private RectTransform _panel;
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

        // The selection names one stack by the cell its footprint starts at. Until an operation commits the
        // stack is still in the bag, so no index is remembered and no unit is removed here.
        private ItemData _selectedItem;
        private Vector2Int _selectedOrigin = new Vector2Int(-1, -1);
        private int _selectedAmount;

        public bool IsOpen { get; private set; }

        private bool RunEnded => _director != null && (_director.State == RunState.Dead || _director.State == RunState.Extracted
            || _director.LocalPlayerState == RunState.Dead || _director.LocalPlayerState == RunState.Extracted);

        /// <summary>R7: the bag screen shows the bag of the player it is bound to; an event about another player never rebuilds it.</summary>
        private bool IsMine(int playerId) => PlayerView.Shows(playerId);

        private int ToastOwner => _rig != null ? _rig.PlayerId : GameEvents.NoPlayer;

        /// <summary>The stack the cursor is pointing at. It is still carried in the bag.</summary>
        public ItemData SelectedItem => _selectedItem;
        public Vector2Int SelectedOrigin => _selectedOrigin;
        public int SelectedAmount => _selectedAmount;
        public bool HasSelection => _selectedItem != null;

        /// <summary>Old cursor API, kept as a read-only view of the selection: nothing is held outside the bag.</summary>
        public ItemData CarriedItem => _selectedItem;
        public int CarriedAmount => _selectedAmount;

        private Vector2 _gridOrigin;
        private Vector2 _equipOrigin;
        private Vector2 _panelCenter;

        public void Configure(RunDirector director, PlayerRig rig)
        {
            _director = director;
            _rig = rig;
            _inventory = rig != null ? rig.Inventory : null;
            _equipment = rig != null ? rig.Equipment : null;

            if (_inventory == null && director != null) _inventory = director.Inventory;

            // Re-pointing the screen at another rig is not a moment at which items may move anywhere.
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
            GameEvents.InventoryChanged -= OnInventoryChanged;
            GameEvents.EquipmentChanged -= OnEquipmentChanged;
            GameEvents.RunStateChanged -= OnRunState;
            GameEvents.PlayerRunStateChanged -= OnPlayerRunState;

            // Dying, pausing out of the run or the HUD being torn down must not leave a selection behind
            // that someone later tries to put somewhere. The stack was never removed, so clearing is free.
            ClearSelection();
        }

        public void Open()
        {
            if (IsOpen || RunEnded) return;
            IsOpen = true;

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
            if (_director == null) GameServices.TryGet<RunDirector>(out _director);

            PlayerRig live = _director != null ? _director.Player : null;
            if (live == null) return false;

            Inventory bag = live.Inventory;
            Equipment worn = live.Equipment;

            if (ReferenceEquals(_rig, live) && _inventory == bag && _equipment == worn) return false;

            bool bagChanged = _inventory != null && bag != null && !ReferenceEquals(_inventory, bag);

            _rig = live;
            if (bag != null) _inventory = bag;
            if (worn != null) _equipment = worn;

            // The pane reads the rig through the view, so the view is re-pointed at the same object the
            // screen just bound to before any line of it is labelled or refreshed.
            if (_statsView != null) _statsView.Configure(_rig);

            if (bagChanged) ClearSelection();

            return true;
        }

        /// <summary>
        /// Reads mouse clicks while the screen is open. Called by the HUD every frame so this screen does not
        /// need its own Input Action map: the bag is presentation and input routing stays in one place.
        /// </summary>
        public void HandlePointer()
        {
            if (!IsOpen) return;

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

            Vector2 screen = mouse.position.ReadValue();

            UpdateGhost(screen);
            ShowTooltipAt(screen);

            // A frame boundary is a legal moment for the bag to change underneath the cursor: a container, a
            // consumable hotkey, an equipment change or a scripted transfer can all move a stack. The
            // selection is re-resolved before anything is done with it.
            ValidateSelection();

            if (mouse.leftButton.wasPressedThisFrame) HandleClick(screen, false);
            else if (mouse.rightButton.wasPressedThisFrame) HandleClick(screen, true);
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
            if (_director != null && (_director.State != RunState.Running || _director.LocalPlayerState != RunState.Running))
            {
                if (_selectedItem != null) CancelSelection();
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
            bool carrying = _selectedItem != null && !_showCharacter;
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

            if (_selectedItem != null)
            {
                if (!TryResolveSelection(out int sourceIndex))
                {
                    CancelSelection();
                    return;
                }

                if (_selectedItem.equipSlot != slot)
                {
                    GameEvents.RaiseToast(ToastOwner, $"{_selectedItem.displayName} does not fit the {slot} slot");
                    return;
                }

                string reason;
                if (_equipment.TryEquipFromInventory(sourceIndex, out reason))
                {
                    // Equipment commits the bag half and the slot half as one transfer. The cursor only
                    // lets go after that call says it happened.
                    ClearSelection();
                    Rebuild();
                }
                else
                {
                    GameEvents.RaiseToast(ToastOwner, reason);
                }
                return;
            }

            ItemData worn = _equipment.Equipped(slot);
            if (worn == null) return;

            // Taking worn gear off goes through the equipment API, which decides where it lands in the bag.
            // Snapshotting the cells this definition already occupied is how the returned stack is told apart
            // from another copy of the same definition while copies share one definition and have no uid.
            List<Vector2Int> cellsBefore = OriginsOfItem(worn);

            string unequipReason;
            if (_equipment.TryUnequip(slot, out unequipReason))
            {
                SelectReturnedStack(worn, cellsBefore);
                Rebuild();
            }
            else
            {
                GameEvents.RaiseToast(ToastOwner, unequipReason);
            }
        }

        private void ClickCell(Vector2Int cell)
        {
            if (_inventory == null) return;

            int targetIndex = _inventory.IndexAtCell(cell);

            if (_selectedItem == null)
            {
                if (targetIndex >= 0) SelectStack(targetIndex);
                return;
            }

            if (!TryResolveSelection(out int sourceIndex))
            {
                CancelSelection();
                return;
            }

            if (targetIndex == sourceIndex)
            {
                // Clicking the stack that is already selected is the grid's cancel gesture. Same cell,
                // same stack: nothing is moved, swapped, merged or credited.
                CancelSelection();
                return;
            }

            string reason;

            if (targetIndex < 0)
            {
                // An empty cell is a move of the stack that is still owned by the bag. TryMove ignores the
                // source's own occupancy, so a shape may also be shifted onto its own footprint.
                if (_inventory.TryMove(sourceIndex, cell, out reason))
                {
                    ClearSelection();
                    Rebuild();
                }
                else
                {
                    GameEvents.RaiseToast(ToastOwner, reason);
                }
                return;
            }

            InventorySlot target = _inventory.Slots[targetIndex];

            if (target.Item == _selectedItem && _selectedItem.CanStack)
            {
                if (_inventory.TryMergeStacks(sourceIndex, targetIndex, out reason))
                {
                    ClearSelection();
                    Rebuild();
                }
                else
                {
                    GameEvents.RaiseToast(ToastOwner, reason);
                }
                return;
            }

            // Both shapes have to fit in each other's holes, which TrySwap checks. A refusal leaves the
            // selection and the whole bag exactly as they were.
            if (_inventory.TrySwap(sourceIndex, targetIndex, out reason))
            {
                ClearSelection();
                Rebuild();
            }
            else
            {
                GameEvents.RaiseToast(ToastOwner, reason);
            }
        }

        private void HandleRightClick(Vector2 screen)
        {
            // Right click is cancel first. The selected stack never left the bag, so cancelling has nothing
            // to put back and cannot drop, duplicate, collect or credit anything.
            // ...except over an EQUIPPABLE bag item: a right click on gear is "wear this one" whatever is
            // selected (P4c: a leftover selection used to swallow the click, so the cloak was never worn).
            // The selection is only a cursor mark, so dropping it here moves, spends and credits nothing.
            if (_selectedItem != null)
            {
                bool overEquippable = false;
                if (TryCellAt(screen, out Vector2Int overCell) && !TryEquipSlotAt(screen, out _) && _inventory != null)
                {
                    int overIndex = _inventory.IndexAtCell(overCell);
                    overEquippable = overIndex >= 0 && _inventory.Slots[overIndex].Item != null && _inventory.Slots[overIndex].Item.IsEquippable;
                }
                bool overBagItem = overEquippable;
                if (!overBagItem)
                {
                    CancelSelection();
                    return;
                }

                ClearSelection();
            }

            if (TryEquipSlotAt(screen, out EquipmentSlot slot))
            {
                ItemData worn = _equipment != null ? _equipment.Equipped(slot) : null;
                if (worn != null)
                {
                    List<Vector2Int> cellsBefore = OriginsOfItem(worn);

                    string reason;
                    if (_equipment.TryUnequip(slot, out reason))
                    {
                        SelectReturnedStack(worn, cellsBefore);
                        Rebuild();
                    }
                    else
                    {
                        GameEvents.RaiseToast(ToastOwner, reason);
                    }
                }
                return;
            }

            if (TryCellAt(screen, out Vector2Int cell))
            {
                int index = _inventory != null ? _inventory.IndexAtCell(cell) : -1;
                if (index < 0) return;

                InventorySlot slotData = _inventory.Slots[index];
                ItemData item = slotData.Item;
                if (item == null) return;

                if (item.IsEquippable)
                {
                    string equipReason = _equipment == null ? "nothing to wear the item" : null;
                    bool worn = _equipment != null && _equipment.TryEquipFromInventory(index, out equipReason);
                    if (worn) Rebuild();
                    else GameEvents.RaiseToast(ToastOwner, equipReason);
                    return;
                }

                if (_inventory.TryUseSlot(index)) Rebuild();
            }
        }

        // ------------------------------------------------------------ tabs and tooltip

        /// <summary>Reached through the button's own onClick, i.e. the canvas raycaster and the InputSystem module.</summary>
        private void SelectBagTab()
        {
            if (!_showCharacter) return;
            _showCharacter = false;
            _hoverInstance = null; // the tooltip was hidden with the tab; forget it so the next frame re-shows it without a pointer move
            _hoverDefinition = null;
            ApplyTabVisibility();
            Rebuild();
        }

        private void SelectCharacterTab()
        {
            if (_showCharacter) return;
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

            if (_selectedItem == null || !TryResolveSelection(out int index)) return;

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
                _tooltipLabel.text = model != null ? model.Headline : string.Empty;
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

            if (model.UidShort.Length > 0)
            {
                builder.Append("<color=#").Append(ColorUtility.ToHtmlStringRGB(DimColor)).Append(">uid ")
                       .Append(model.UidShort).Append("</color>");
                lines++;
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
            float left = PanelCenterX - PanelWidth * 0.5f + TooltipEdgeMargin;
            float right = PanelCenterX + PanelWidth * 0.5f - TooltipEdgeMargin;
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

        private void SelectStack(int index)
        {
            if (_inventory == null || index < 0 || index >= _inventory.Slots.Count) return;

            InventorySlot slot = _inventory.Slots[index];
            if (slot.Item == null || slot.Count <= 0) return;

            _selectedItem = slot.Item;
            _selectedOrigin = slot.Origin;
            _selectedAmount = slot.Count;

            Rebuild();
        }

        /// <summary>
        /// Selects the stack that unequipping just put back in the bag. The cells the same definition already
        /// occupied are excluded, so a duplicate of the worn item cannot be mistaken for the returned one and
        /// the selection never names a stack the bag does not hold.
        /// </summary>
        private void SelectReturnedStack(ItemData item, List<Vector2Int> cellsBefore)
        {
            if (_inventory == null || item == null) return;

            for (int i = 0; i < _inventory.Slots.Count; i++)
            {
                InventorySlot slot = _inventory.Slots[i];
                if (slot.Item != item || slot.Count <= 0) continue;
                if (cellsBefore != null && cellsBefore.Contains(slot.Origin)) continue;

                _selectedItem = slot.Item;
                _selectedOrigin = slot.Origin;
                _selectedAmount = slot.Count;
                return;
            }
        }

        /// <summary>
        /// Turns the selection into the index it names right now, or refuses. The selection is identified by
        /// the cell its footprint starts at and re-read from the grid, so a stack that moved, shrank, was
        /// consumed or was removed cannot be reached through an index remembered from an earlier click.
        /// </summary>
        private bool TryResolveSelection(out int index)
        {
            index = -1;

            if (_selectedItem == null || _inventory == null) return false;
            if (_selectedOrigin.x < 0 || _selectedOrigin.y < 0) return false;

            int resolved = _inventory.IndexAtCell(_selectedOrigin);
            if (resolved < 0 || resolved >= _inventory.Slots.Count) return false;

            InventorySlot slot = _inventory.Slots[resolved];
            if (slot.Item != _selectedItem || slot.Origin != _selectedOrigin || slot.Count <= 0) return false;

            index = resolved;
            _selectedAmount = slot.Count;
            return true;
        }

        private void ValidateSelection()
        {
            if (_selectedItem == null) return;
            if (TryResolveSelection(out _)) return;

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
            _selectedItem = null;
            _selectedOrigin = new Vector2Int(-1, -1);
            _selectedAmount = 0;

            if (_carryGhost != null)
            {
                _carryGhost.enabled = false;
                _carryGhost.gameObject.SetActive(false);
            }
        }

        private List<Vector2Int> OriginsOfItem(ItemData item)
        {
            List<Vector2Int> origins = new List<Vector2Int>(4);
            if (_inventory == null || item == null) return origins;

            for (int i = 0; i < _inventory.Slots.Count; i++)
            {
                if (_inventory.Slots[i].Item == item) origins.Add(_inventory.Slots[i].Origin);
            }

            return origins;
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
        private bool TryCellAt(Vector2 screen, out Vector2Int cell)
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

            _panel = UiFactory.CreatePanel(_root, "SatchelPanel", PanelColor);
            _panel.sizeDelta = new Vector2(PanelWidth, PanelHeight);
            _panel.anchoredPosition = new Vector2(PanelCenterX, PanelCenterY);

            _gridGroup = UiFactory.CreateGroup(_root, "GridGroup");
            _equipGroup = UiFactory.CreateGroup(_root, "EquipGroup");

            // InventoryRoot is a zero-size group, so every anchor point inside it coincides with its centre:
            // the pivot is what makes an offset describe the corner it is named for. Pivot (0,1) turns the
            // offsets below into the widget's top-left corner inside the panel, which is what a left-aligned
            // label needs. The previous centre pivot put the label's centre at the panel inset and half of
            // the label outside the panel.
            _headerLabel = UiFactory.CreateText(_root, "Header", "SATCHEL", 22, TextColor, TextAnchor.UpperLeft);
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
            float innerLeft = -PanelWidth * 0.5f + PanelInset;
            float innerRight = PanelWidth * 0.5f - PanelInset;
            float equipColumnWidth = EquipSlotSize + EquipLabelGap + EquipLabelWidth;
            return innerRight - equipColumnWidth - ColumnGap - innerLeft;
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
        public void Rebuild()
        {
            if (_root == null) return;

            // Every line below reads the bag, the worn kit and the rig through this screen's own binding,
            // so the binding is resolved from the live director before anything is drawn or re-labelled.
            EnsureLiveRig();

            if (_inventory == null) return;

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
            float equipStep = EquipSlotSize + EquipSlotGap;
            float equipColumnWidth = EquipSlotSize + EquipLabelGap + EquipLabelWidth;
            float equipColumnHeight = order.Length * equipStep - EquipSlotGap;

            // The worn-kit column is measured by its slots plus the label extents that sit beside them, so a
            // slot label is never allowed to hang past the panel edge. The bag grid keeps its full width and
            // whatever is left between the two columns is the gutter.
            float equipColumnLeft = innerRight - equipColumnWidth;
            float leftColumnWidth = equipColumnLeft - ColumnGap - innerLeft;

            float leftColumnHeight = HeaderHeight + ColumnGap + gridHeight + ColumnGap + DetailHeight +
                                     ColumnGap + CarryHeight;
            float leftColumnTop = innerTop - (PanelHeight - 2f * PanelInset - leftColumnHeight) * 0.5f;

            float headerTop = leftColumnTop;
            float gridTop = headerTop - HeaderHeight - ColumnGap;
            float detailTop = gridTop - gridHeight - ColumnGap;
            float carryTop = detailTop - DetailHeight - ColumnGap;

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

            // Root-local centre of the selected stack, kept outside the slot loop so the ghost block below
            // can park the no-mouse ghost over the footprint it actually names.
            Vector2 selectedCenter = Vector2.zero;
            bool hasSelectedCenter = false;

            for (int i = 0; i < _inventory.Slots.Count; i++)
            {
                InventorySlot slot = _inventory.Slots[i];
                if (slot.Item == null) continue;

                Vector2Int size = slot.Size;
                Vector2 center = _gridOrigin + new Vector2(
                    slot.Origin.x * step + (size.x * CellSize + (size.x - 1) * CellGap) * 0.5f,
                    gridHeight - (slot.Origin.y * step + (size.y * CellSize + (size.y - 1) * CellGap) * 0.5f));

                Vector2 blockSize = new Vector2(size.x * CellSize + (size.x - 1) * CellGap,
                    size.y * CellSize + (size.y - 1) * CellGap);

                Color color = slot.Item.iconColor;
                color.a = 1f;

                // The selected stack is marked where it still stands in the bag. It is not lifted out and it
                // is not drawn twice as two normal owned copies: the bag copy is dimmed inside a frame of
                // selection colour, and only the ghost at the pointer is translucent.
                bool isSelected = _selectedItem != null && slot.Item == _selectedItem && slot.Origin == _selectedOrigin;
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

                _itemImages.Add(block);

                if (slot.Count > 1)
                {
                    Text count = UiFactory.CreateText(block.transform, "Count", slot.Count.ToString(), 15, Color.black,
                        TextAnchor.LowerRight, FontStyle.Bold);
                    count.rectTransform.anchorMin = Vector2.zero;
                    count.rectTransform.anchorMax = Vector2.one;
                    count.rectTransform.offsetMin = new Vector2(3f, 1f);
                    count.rectTransform.offsetMax = new Vector2(-3f, -1f);
                }
            }

            // Equipment column. The origin is the top-left corner of the column, and each row descends from
            // it once: the previous origin was already a full half-column below the centre and the loop then
            // descended again, which pushed the last two slots below the window.
            Vector2 equipCentrePanel = new Vector2(equipColumnLeft + EquipSlotSize * 0.5f, 0f);
            Vector2 equipCenter = PanelToRoot(equipCentrePanel);
            _equipOrigin = equipCenter + new Vector2(-EquipSlotSize * 0.5f, equipColumnHeight * 0.5f);
            _equipGroup.anchoredPosition = equipCenter;
            _equipColumnLeftRoot = _equipOrigin.x;

            for (int i = 0; i < order.Length; i++)
            {
                EquipmentSlot kind = order[i];
                Vector2 center = _equipOrigin + new Vector2(EquipSlotSize * 0.5f, -(i * equipStep) - EquipSlotSize * 0.5f);

                ItemData worn = _equipment != null ? _equipment.Equipped(kind) : null;

                Color color = worn != null ? worn.iconColor : SlotColor;
                color.a = worn != null ? 1f : 1f;

                Image slotImage = UiFactory.CreateImage(_equipGroup.transform, "Equip_" + kind, color,
                    new Vector2(EquipSlotSize, EquipSlotSize), center - equipCenter,
                    new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));

                _equipImages.Add(slotImage);
                _equipRects.Add(slotImage.rectTransform);
                _equipSlots.Add(kind);

                // The worn-item label sits beside the slot on the dark SatchelPanel, not inside the coloured
                // slot block, so its foreground cannot follow the item icon colour (Footpad Boots' near-black
                // icon colour rendered the name invisible). Use the panel's readable light text colour; empty
                // slots keep DimColor.
                // P4c: the slot name is always shown (so a worn Back item cannot hide which slot it is); the
                // worn item is a second line under it, in its rarity colour.
                string slotName = ItemTooltip.SlotLabel(kind);
                string labelText = slotName;
                if (worn != null)
                {
                    ItemInstance wornInstance = _equipment.EquippedInstance(kind);
                    Color nameColor = wornInstance != null ? ItemTooltip.RarityColor(wornInstance.Rarity) : TextColor;
                    labelText = "<color=#" + ColorUtility.ToHtmlStringRGB(DimColor) + "><size=11>" + slotName + "</size></color>\n" +
                                "<color=#" + ColorUtility.ToHtmlStringRGB(nameColor) + ">" + worn.displayName + "</color>";
                }
                Text label = UiFactory.CreateText(_equipGroup.transform, "EquipLabel_" + kind,
                    labelText, 13, worn != null ? TextColor : DimColor, TextAnchor.MiddleLeft, FontStyle.Bold);
                label.horizontalOverflow = HorizontalWrapMode.Wrap;
                label.verticalOverflow = VerticalWrapMode.Truncate;
                label.supportRichText = true;
                label.raycastTarget = false;
                label.rectTransform.anchorMin = new Vector2(0.5f, 0.5f);
                label.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
                label.rectTransform.pivot = new Vector2(0f, 0.5f);
                label.rectTransform.sizeDelta = new Vector2(EquipLabelWidth, EquipSlotSize);
                label.rectTransform.anchoredPosition = center - equipCenter +
                    new Vector2(EquipSlotSize * 0.5f + EquipLabelGap, 0f);

                _equipLabels.Add(label);
            }

            if (_headerLabel != null)
            {
                int worn = _equipment != null ? _equipment.FilledCount : 0;
                _headerLabel.text = _showCharacter
                    ? "CHARACTER"
                    : $"SATCHEL  {_inventory.UsedCells}/{_inventory.Capacity}  worn {worn}";
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
                _carryLabel.text = _selectedItem != null
                    ? $"Selected {_selectedItem.displayName} x{_selectedAmount} ({SelectedSummary()})   " +
                      "left click: move, swap, combine or wear   right click: let go"
                    : "left click: select   right click a bag item: wear or use it   right click a worn item: take it off";
                _carryLabel.rectTransform.sizeDelta = new Vector2(leftColumnWidth, InstructionHeight);
                _carryLabel.rectTransform.anchoredPosition = PanelToRoot(new Vector2(innerLeft, instructionTop));
            }

            // The character pane covers exactly the bag column below the header: the grid, the detail line,
            // the tooltip line and the instructions. Those widgets stay where they are and are simply drawn
            // under the pane while the character tab is up, so switching back puts them back untouched.
            float paneTop = gridTop;
            // Sized from the content as well as from the column: the rows start at the group origin and need
            // PreferredHeight; the extra padding keeps the last row's bottom inside the pane background (U2B11 review M1).
            float paneHeight = Mathf.Max(paneTop - (carryTop - CarryHeight), CharacterStatsView.PreferredHeight + PanePadding);
            float paneWidth = leftColumnWidth;

            if (_statsGroup != null)
                _statsGroup.anchoredPosition = PanelToRoot(new Vector2(innerLeft + PanePadding, paneTop - PanePadding));

            if (_statsPane != null)
            {
                _statsPane.rectTransform.sizeDelta = new Vector2(paneWidth, paneHeight);
                _statsPane.rectTransform.anchoredPosition = new Vector2(paneWidth * 0.5f, -paneHeight * 0.5f);
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
                if (_selectedItem != null && !_showCharacter)
                {
                    Vector2Int shape = _selectedItem.EffectiveFootprint;
                    Color ghostColor = _selectedItem.iconColor;
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
            ItemTooltipModel model = ItemTooltip.ForDefinition(_selectedItem);
            return model != null && model.FixedLines.Count > 0 ? string.Join(", ", model.FixedLines) : _selectedItem.kind.ToString().ToLower();
        }

        private string BuildDetailText()
        {
            if (_inventory == null) return string.Empty;

            System.Text.StringBuilder builder = new System.Text.StringBuilder();
            builder.Append("grid ").Append(_inventory.GridWidth).Append('x').Append(_inventory.GridHeight)
                   .Append("   free ").Append(_inventory.FreeCells).Append(" cells")
                   .Append("   value ").Append(_inventory.CarriedValue);

            if (_equipment != null && _equipment.FilledCount > 0)
            {
                builder.Append("   worn: ").Append(_equipment.Describe());
            }

            // The six scores and the derived numbers are no longer dumped onto this line: the character tab
            // shows them as a readable pane, and a raw "strength=16(+3) ... hp=120" run-in here was the
            // thing the pane was asked to replace.
            return builder.ToString();
        }
    }
}
