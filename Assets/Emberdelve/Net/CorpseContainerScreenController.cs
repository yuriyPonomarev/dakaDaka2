using System.Collections.Generic;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Flow;
using Emberdelve.Feedback;
using Emberdelve.Items;
using Emberdelve.Player;
using Emberdelve.UI;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Emberdelve.Net
{
    /// <summary>Owner-only corpse list. Buttons send item intents; only server snapshots change displayed contents.</summary>
    [DisallowMultipleComponent]
    public sealed class CorpseContainerScreenController : MonoBehaviour
    {
        private const double StateTimeoutSeconds = 6.0;
        private const double TransferTimeoutSeconds = 8.0;
        private static readonly Color PanelColor = new Color(0.055f, 0.05f, 0.045f, 0.98f);
        private static readonly Color TextColor = new Color(0.91f, 0.87f, 0.79f, 1f);

        private readonly Rules.CorpseContainerUiSession _session = new Rules.CorpseContainerUiSession();
        private readonly List<Row> _rows = new List<Row>();
        private RectTransform _contents;
        private InventoryScreenController _inventoryScreen;
        private UnityEngine.UI.Image _tip;
        private UnityEngine.UI.Text _tipText;
        private CorpseLootWorldPresenter _presenter;
        private NetInventoryClient _client;
        private NetPlayerRig _hostOwner;
        private NetPlayerRig _owner;
        private PlayerRig _rig;
        private Inventory _bag;
        private RectTransform _root;
        private RectTransform _panel;
        private UnityEngine.UI.Text _header;
        private UnityEngine.UI.Text _status;
        private UnityEngine.UI.Text _pageLabel;
        private UnityEngine.UI.Button _previous;
        private UnityEngine.UI.Button _next;
        private int _page;
        private double _stateStartedAt;
        private double _transferStartedAt;
        private double _refreshAt;
        private string _lastFailure;
        private bool _hostInputPaused;
        private bool _audioOpen;
        private ulong _carriedEntry;
        private Vector2 _carryStart;
        private UnityEngine.UI.Image _carry;
        private ItemData _carriedItem;

        private sealed class Row
        {
            public RectTransform Rect;
            public int Index;
            public ItemData Item;
            public ItemInstance Instance;
        }

        public bool IsOpen => _session.IsOpen;
        public bool IsOpenFor(ulong corpseId) => IsOpen && _session.ActiveCorpseId == corpseId;

        public void Configure(CorpseLootWorldPresenter presenter, NetInventoryClient client,
            NetPlayerRig hostOwner, PlayerRig rig)
        {
            if (_client != null)
            {
                _client.CorpseContainerReceived -= OnState;
                _client.CorpseTransferResultReceived -= OnClientResult;
            }
            _presenter = presenter;
            _client = client;
            _hostOwner = hostOwner;
            _owner = rig != null && !NetRole.IsOffline ? rig.GetComponent<NetPlayerRig>() : null;
            _rig = rig;
            _bag = rig != null ? rig.Inventory : null;
            if (_client != null)
            {
                _client.CorpseContainerReceived += OnState;
                _client.CorpseTransferResultReceived += OnClientResult;
            }
        }

        public bool OpenPending(ulong corpseId)
        {
            if (corpseId == 0 || _presenter == null || _rig == null || !_rig.IsAlive || _rig.IsExtracted ||
                (_client != null && !_client.IsOwnerReady) ||
                (_hostOwner != null && (!_hostOwner.IsServer || !_hostOwner.IsOwner || !_hostOwner.IsSpawned))) return false;
            if (!_session.Open(corpseId)) return false;
            _lastFailure = null;

            BuildIfNeeded();
            if (_root == null)
            {
                _session.Close();
                return false;
            }
            _page = 0;
            _refreshAt = 0.0;
            _root.gameObject.SetActive(true);
            foreach (var screen in Object.FindObjectsByType<InventoryScreenController>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (screen.BoundRig == _rig) { _inventoryScreen = screen; break; }
            _inventoryScreen?.SetLootCompanion(true);
            if (_inventoryScreen != null) { _inventoryScreen.LootPanelRect = _panel; _inventoryScreen.LootPutHandler = PutIntoContainer; }
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            GetComponent<NetInputSender>()?.ClearPendingMovement();
            if (_client == null) // host or offline: this process drives the rig, so it pauses its own input
            {
                _rig.SetInputEnabled(false);
                _hostInputPaused = true;
            }
            Render();
            if (!RequestState())
            {
                Close();
                return false;
            }
            if (!_audioOpen)
            {
                _audioOpen = true;
                WorldAudio.PlayLocal(WorldSound.UiOpen);
            }
            return true;
        }

        public void OnState(CorpseContainerDocument document)
        {
            if (!_session.ApplyState(document)) return;
            _stateStartedAt = 0.0;
            _refreshAt = 0.0;
            ClampPage();
            Render();
        }

        public void OnEmptied(ulong revision)
        {
            if (!IsOpen || _session.Document == null) return;
            var doc = _session.Document;
            doc.revision = revision;
            doc.entries = System.Array.Empty<CorpseContainerEntryDocument>();
            _session.ApplyState(doc);
            _stateStartedAt = _transferStartedAt = _refreshAt = 0;
            _lastFailure = null;
            Render();
        }

        public void OnInvalidState(string reason)
        {
            if (!IsOpen) return;
            _lastFailure = string.IsNullOrEmpty(reason) ? "Could not read container contents" : reason;
            Render();
            _stateStartedAt = 0.0;
            _refreshAt = Time.realtimeSinceStartupAsDouble + 0.35;
        }

        public void OnResult(ulong corpseId, uint sequence, bool accepted, ulong corpseRevision, string reason)
        {
            bool isTransfer = sequence != 0 && sequence == _session.TransferSequence;
            if (!_session.ApplyResult(corpseId, sequence, accepted, corpseRevision, reason)) return;
            // Authority acquisition already raises ItemCollected; snapshots on a remote client do not.
            if (accepted && isTransfer && NetRole.IsClientOnly && _rig != null)
                WorldAudio.PlayAttached(WorldSound.Pickup, _rig.transform, Vector3.up * 1.2f);
            _lastFailure = accepted ? null : (string.IsNullOrEmpty(reason) ? "Server refused this container request" : reason);
            if (_session.TransferSequence == 0) _transferStartedAt = 0.0;
            if (_session.StateRequestSequence == 0) _stateStartedAt = 0.0;
            if (_session.NeedsRefresh) _refreshAt = Time.realtimeSinceStartupAsDouble + 0.35;
            Render();
            if (_pendingEquipUid != System.Guid.Empty && _session.TransferSequence == 0)
            {
                // The item was dragged from this window onto a worn slot: now that it is in the bag, wear it.
                System.Guid uid = _pendingEquipUid;
                EquipmentSlot slot = _pendingEquipSlot;
                _pendingEquipUid = System.Guid.Empty;
                if (accepted) _inventoryScreen?.TryEquipBagInstance(uid, slot);
            }
        }

        private System.Guid _pendingEquipUid;
        private EquipmentSlot _pendingEquipSlot;

        public void Close(bool relockCursor = true)
        {
            if (!IsOpen) return;
            if (_audioOpen) WorldAudio.PlayLocal(WorldSound.UiClose);
            _audioOpen = false;
            ClearCarry();
            _session.Close();
            if (_inventoryScreen != null)
            {
                _inventoryScreen.LootPanelRect = null;
                _inventoryScreen.LootPutHandler = null;
                _inventoryScreen.Close(false);
                _inventoryScreen.SetLootCompanion(false);
            }
            _lastFailure = null;
            _page = 0;
            _stateStartedAt = 0.0;
            _transferStartedAt = 0.0;
            _refreshAt = 0.0;
            if (_root != null) _root.gameObject.SetActive(false);
            if (_tip != null) _tip.gameObject.SetActive(false);
            if (_hostInputPaused)
            {
                _hostInputPaused = false;
                if (relockCursor && _rig != null && _rig.IsAlive && !_rig.IsExtracted) _rig.SetInputEnabled(true);
            }
            if (relockCursor)
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }
        }

        private void Update()
        {
            if (!IsOpen) return;
            if (_rig == null || _rig.IsDead || _rig.IsExtracted ||
                (_owner != null && !_owner.IsSpawned) ||
                (_hostOwner != null && !_hostOwner.IsSpawned))
            {
                Close(false);
                return;
            }

            _root.localScale = Vector3.one * LootPanelStyle.InventoryScale(true);
            if (Keyboard.current != null && (Keyboard.current.escapeKey.wasPressedThisFrame || Keyboard.current.iKey.wasPressedThisFrame || Keyboard.current.tabKey.wasPressedThisFrame)) { Close(); return; }
            if (_carriedEntry == 0) _inventoryScreen?.HandlePointer();
            HandleLootPointer();
            double now = Time.realtimeSinceStartupAsDouble;
            if (_session.StateRequestSequence != 0 && now - _stateStartedAt >= StateTimeoutSeconds)
            {
                _session.CancelStateRequest(_session.StateRequestSequence, "Corpse request timed out");
                _stateStartedAt = 0.0;
                _refreshAt = now + 0.35;
                Render();
            }
            if (_session.TransferSequence != 0 && now - _transferStartedAt >= TransferTimeoutSeconds)
            {
                _session.CancelTransfer("Transfer timed out; checking server contents…");
                _transferStartedAt = 0.0;
                _refreshAt = now + 0.35;
                Render();
            }
            if (_refreshAt > 0.0 && now >= _refreshAt && !_session.IsTransferPending && _session.StateRequestSequence == 0)
            {
                _refreshAt = 0.0;
                RequestState();
            }
        }

        /// <summary>Called by the bag screen when a carried bag item is released over this window. Returns null on success, otherwise the reason.</summary>
        private string PutIntoContainer(InventorySlot slot)
        {
            if (!IsOpen || _session.Document == null || _presenter == null || slot.Item == null)
                return "The loot window is not open";
            if (_session.IsTransferPending || _session.StateRequestSequence != 0) return "Wait for the window to finish updating";
            if (_session.Document.kind == "player") return "You cannot put items into a fallen adventurer's gear";
            _lastFailure = null;
            if (!_presenter.RequestPut(_session.ActiveCorpseId, slot.Origin, slot.Item.itemId, out string reason))
                return reason ?? "Cannot put that here";
            WorldAudio.PlayLocal(WorldSound.ItemMove);
            return null;
        }

        private bool RequestState()
        {
            _stateStartedAt = Time.realtimeSinceStartupAsDouble;
            bool sent = _presenter.RequestState(_session.ActiveCorpseId,
                sequence => _session.BeginStateRequest(sequence), out _);
            if (!sent)
            {
                _stateStartedAt = 0.0;
                _status.text = "Could not request current contents";
                RenderRows();
                return false;
            }
            RenderRows();
            return true;
        }

        private void OnClientResult(ulong corpseId, uint sequence, bool accepted,
            ulong corpseRevision, ulong bagRevision, string reason)
        {
            OnResult(corpseId, sequence, accepted, corpseRevision, reason);
        }

        private void Take(int rowIndex, bool all, Vector2Int? destinationCell = null, EquipmentSlot? equipAfter = null)
        {
            _pendingEquipUid = System.Guid.Empty;
            if (!IsOpen || _bag == null || _presenter == null || _session.Document == null || _session.IsTransferPending) return;
            int entryIndex = rowIndex;
            if (entryIndex < 0 || entryIndex >= _session.Document.entries.Length) return;
            CorpseContainerEntryDocument entry = _session.Document.entries[entryIndex];
            if (entry == null || entry.item == null) return;
            int amount = all ? entry.item.quantity : 1;
            if (!_session.TryBeginTransfer(entry.entryId, amount, _bag.Revision,
                    out Rules.CorpseTransferUiRequest request, out string reason, destinationCell))
            {
                _lastFailure = reason;
                Render();
                return;
            }

            _lastFailure = null;
            if (equipAfter.HasValue && !string.IsNullOrEmpty(entry.item.uid) && System.Guid.TryParseExact(entry.item.uid, "N", out System.Guid gearUid))
            { _pendingEquipUid = gearUid; _pendingEquipSlot = equipAfter.Value; }
            bool sent = _presenter.RequestTransfer(request, _session.AttachTransferSequence, out _);
            if (!sent) _pendingEquipUid = System.Guid.Empty;
            if (!sent)
            {
                _session.CancelTransfer("Could not send item transfer");
                _lastFailure = "Could not send item transfer";
                Render();
                return;
            }
            if (_session.TransferSequence != 0) _transferStartedAt = Time.realtimeSinceStartupAsDouble;
            Render();
        }

        private void BuildIfNeeded()
        {
            if (_root != null) return;
            Canvas canvas = UiFactory.EnsureCanvas("Emberdelve_Hud", 10);
            _root = UiFactory.CreateGroup(canvas.transform, "CorpseContainerRoot");
            _root.gameObject.SetActive(false);
            UiFactory.CreateImage(_root,"LootBackdrop",new Color(0,0,0,.62f),new Vector2(6000,4000),Vector2.zero,new Vector2(.5f,.5f),new Vector2(.5f,.5f));
            _panel = UiFactory.CreateGroup(_root, "CorpsePanel");
            _panel.sizeDelta = new Vector2(LootPanelStyle.Width, LootPanelStyle.Height);
            _panel.anchoredPosition = new Vector2(460, 0);
            var background = _panel.gameObject.AddComponent<UnityEngine.UI.Image>();
            LootPanelStyle.Panel(background);
            _header = Label(_panel, "Header", "LOOT", 22, new Vector2(0, 297), new Vector2(440, 32));
            _header.color = new Color(.89f, .77f, .51f);
            _status = Label(_panel, "Status", "", 14, new Vector2(0, -268), new Vector2(475, 20));
            _status.horizontalOverflow = UnityEngine.HorizontalWrapMode.Wrap;
            Label(_panel, "Instructions", "LMB: drag to bag   |   RMB: take stack", 13, new Vector2(0, -289), new Vector2(475, 20));
            _pageLabel = Label(_panel, "Page", "", 13, new Vector2(0, -245), new Vector2(180, 26));
            _previous = Button("PreviousPage", "<", () => ChangePage(-1), new Vector2(-110,-245), new Vector2(40,26));
            _next = Button("NextPage", ">", () => ChangePage(1), new Vector2(110,-245), new Vector2(40,26));
            Button("Refresh", "Refresh", Refresh, new Vector2(-66,-308), new Vector2(110,26));
            Button("Close", "Close [Tab / Esc]", () => Close(), new Vector2(66,-308), new Vector2(130,26));
            _tip = UiFactory.CreateImage(canvas.transform, "LootTooltip", LootPanelStyle.Ink, new Vector2(290,200), Vector2.zero, new Vector2(.5f,.5f),new Vector2(.5f,.5f));
            _tip.raycastTarget = false; LootPanelStyle.Frame(_tip, LootPanelStyle.Gold);
            // A canvas-level popup avoids being occluded by the companion inventory root.
            _tipText = Label(_tip.rectTransform,"TooltipText","",14,Vector2.zero,new Vector2(264,180));
            _tipText.alignment = TextAnchor.UpperLeft; _tipText.supportRichText = true;
            _tipText.horizontalOverflow = HorizontalWrapMode.Wrap;
            _tip.gameObject.SetActive(false);
        }

        private static UnityEngine.UI.Text Label(Transform parent, string name, string text, int font, Vector2 at, Vector2 size)
        {
            var label = UiFactory.CreateText(parent, name, text, font, TextColor, TextAnchor.MiddleCenter);
            label.rectTransform.anchoredPosition = at; label.rectTransform.sizeDelta = size; label.raycastTarget = false;
            return label;
        }

        private UnityEngine.UI.Button Button(string name, string text, UnityEngine.Events.UnityAction action, Vector2 at, Vector2 size)
        {
            var button = UiFactory.CreateButton(_panel, name, text, action, size, new Color(.18f,.16f,.12f), TextColor, 13);
            ((RectTransform)button.transform).anchoredPosition = at;
            return button;
        }

        private void ClearCarry()
        {
            _carriedEntry = 0;
            _carriedItem = null;
            if (_carry != null) { Destroy(_carry.gameObject); _carry = null; }
        }

        private void HandleLootPointer()
        {
            if (_inventoryScreen != null && _inventoryScreen.IsSplitting) { ClearCarry(); return; }
            if (Mouse.current == null || _tip == null) return;
            Vector2 pointer = Mouse.current.position.ReadValue();
            _tip.gameObject.SetActive(false);
            if (_carriedEntry != 0)
            {
                int index = System.Array.FindIndex(_session.Document.entries, e => e != null && e.entryId == _carriedEntry);
                if (index < 0 || Mouse.current.rightButton.wasPressedThisFrame) { ClearCarry(); return; }
                RectTransformUtility.ScreenPointToLocalPointInRectangle(_root, pointer, null, out Vector2 local);
                if (_carry != null)
                {
                    _carry.rectTransform.localScale = _root.localScale;
                    _carry.rectTransform.anchoredPosition = local * _root.localScale.x;
                    _carry.transform.SetAsLastSibling();
                    bool fits = _inventoryScreen != null && _inventoryScreen.TryCellAt(pointer, out Vector2Int hovered) && _bag.CanPlace(hovered, _carriedItem.EffectiveFootprint);
                    _carry.color = fits ? new Color(.15f,.45f,.25f,.8f) : new Color(.55f,.15f,.12f,.8f);
                }
                bool releasedDrag = Mouse.current.leftButton.wasReleasedThisFrame && (pointer - _carryStart).sqrMagnitude > 36f;
                if (releasedDrag || Mouse.current.leftButton.wasPressedThisFrame)
                {
                    if (_inventoryScreen != null && _inventoryScreen.TryEquipSlotAt(pointer, out EquipmentSlot wornSlot))
                    {
                        Take(index, true, null, wornSlot);   // taken into the bag, then worn
                        ClearCarry();
                    }
                    else if (_inventoryScreen != null && _inventoryScreen.TryCellAt(pointer, out Vector2Int cell))
                    {
                        Take(index, true, cell);
                        ClearCarry();
                    }
                    else if (releasedDrag) ClearCarry();
                }
                return;
            }
            foreach (Row row in _rows)
            {
                if (!RectTransformUtility.RectangleContainsScreenPoint(row.Rect, pointer)) continue;
                ItemTooltipModel tooltip = row.Instance != null ? ItemTooltip.ForInstance(row.Instance) : ItemTooltip.ForDefinition(row.Item);
                if (tooltip != null)
                {
                    string text = "<b><color=#" + ColorUtility.ToHtmlStringRGB(tooltip.RarityColor) + ">" + tooltip.Title + "</color></b>\n";
                    if (tooltip.HasRarity) text += tooltip.RarityName + " • " + tooltip.SlotName + "\n";
                    text += "\n" + string.Join("\n", tooltip.FixedLines);
                    if (tooltip.RolledDisplayLines.Count > 0) text += "\n\n<color=#74B9E8>" + string.Join("\n", tooltip.RolledDisplayLines) + "</color>";
                    _tipText.text = text;
                    float height = Mathf.Min(590f, Mathf.Max(100, _tipText.preferredHeight + 28));
                    _tip.rectTransform.sizeDelta = new Vector2(290,height);
                    _tipText.rectTransform.sizeDelta = new Vector2(264,height-28);
                    RectTransformUtility.ScreenPointToLocalPointInRectangle(_root, pointer, null, out Vector2 at);
                    _tip.rectTransform.localScale = _root.localScale;
                    _tip.rectTransform.anchoredPosition = new Vector2(Mathf.Clamp(at.x-160,-380,380), Mathf.Clamp(at.y-height*.5f,-325+height*.5f,325-height*.5f)) * _root.localScale.x;
                    _tip.gameObject.SetActive(true);
                    _tip.transform.SetAsLastSibling();
                }
                if (_session.IsTransferPending || _session.StateRequestSequence != 0) return;
                if (_inventoryScreen != null && _inventoryScreen.HasSelection) return; // a bag item is being carried: it must not also pick up a loot row
                if (Mouse.current.rightButton.wasPressedThisFrame) Take(row.Index, true);
                else if (Mouse.current.leftButton.wasPressedThisFrame)
                {
                    _inventoryScreen?.CancelCarry();
                    _carriedEntry = _session.Document.entries[row.Index].entryId;
                    _carriedItem = row.Item;
                    _carryStart = pointer;
                    _carry = UiFactory.CreateImage(_root.parent, "CarriedLoot", LootPanelStyle.Ink, row.Rect.sizeDelta, Vector2.zero, Vector2.one*.5f, Vector2.one*.5f);
                    LootPanelStyle.Decorate(_carry, row.Item, row.Instance);
                    _carry.raycastTarget = false;
                    _carry.rectTransform.localScale = _root.localScale;
                    RectTransformUtility.ScreenPointToLocalPointInRectangle((RectTransform)_root.parent, pointer, null, out Vector2 carryAt);
                    _carry.rectTransform.anchoredPosition = carryAt;
                    _tip.gameObject.SetActive(false);
                }
                return;
            }
        }

        private void Refresh()
        {
            if (!IsOpen || _session.IsTransferPending || _session.StateRequestSequence != 0 || _session.Document?.entries.Length == 0) return;
            _lastFailure = null;
            RequestState();
        }

        private int Pages => Mathf.Max(1, Mathf.CeilToInt((_session.Document?.height ?? 4) / 5f));
        private void ChangePage(int delta) { _page = Mathf.Clamp(_page + delta, 0, Pages - 1); Render(); }
        private void ClampPage() { _page = Mathf.Clamp(_page, 0, Pages - 1); }
        private void Render()
        {
            if (_header != null) _header.text = _session.Document?.title ?? "SEARCHING…";
            RenderRows();
        }

        private void RenderRows()
        {
            if (_panel == null) return;
            if (_contents != null) { _contents.gameObject.SetActive(false); Destroy(_contents.gameObject); }
            _contents = UiFactory.CreateGroup(_panel, "Contents");
            _rows.Clear();
            CorpseContainerDocument doc = _session.Document;
            bool player = doc != null && doc.kind == "player";
            int width = doc?.width ?? 10;
            float step = Mathf.Min(LootPanelStyle.Step, 480f / width), cell = step - 2f;
            float top = player ? 8f : 238f;
            if (player)
                for (int i = 1; i < Equipment.SlotCount; i++)
                {
                    var slot = (EquipmentSlot)i;
                    var frame = UiFactory.CreateImage(_contents, "Equip_"+slot, LootPanelStyle.Ink, LootPanelStyle.SlotSize(slot), LootPanelStyle.SlotPosition(slot),new Vector2(.5f,.5f),new Vector2(.5f,.5f));
                    LootPanelStyle.Decorate(frame,null);
                    LootPanelStyle.EmptySlot(frame,slot);
                }
            for (int y = 0; y < 5; y++) for (int x = 0; x < width; x++)
                UiFactory.CreateImage(_contents,"Cell_"+x+"_"+y,new Color(.13f,.12f,.11f),new Vector2(cell,cell),new Vector2(-width*step*.5f+x*step+cell*.5f,top-y*step-cell*.5f),new Vector2(.5f,.5f),new Vector2(.5f,.5f));
            if (doc != null && _bag?.Catalog != null)
                for (int i = 0; i < doc.entries.Length; i++)
                {
                    var entry = doc.entries[i];
                    if (!_bag.Catalog.TryResolveItem(entry.item.itemId,out ItemData item,out _)) continue;
                    bool equipped = !string.IsNullOrEmpty(entry.slot) && System.Enum.TryParse(entry.slot,out EquipmentSlot _);
                    if (!equipped && (entry.y / 5 != _page)) continue;
                    ItemInstance instance = null;
                    if (!string.IsNullOrEmpty(entry.item.uid)) ItemInstance.TryRestore(entry.item,_bag.Catalog.Authority,out instance,out _);
                    Vector2 size, at;
                    if (equipped) { var slot = (EquipmentSlot)System.Enum.Parse(typeof(EquipmentSlot), entry.slot); size = LootPanelStyle.SlotSize(slot); at = LootPanelStyle.SlotPosition(slot); }
                    else { Vector2Int shape = item.EffectiveFootprint; size = new Vector2(shape.x*step-2,shape.y*step-2); at = new Vector2(-width*step*.5f+entry.x*step+size.x*.5f,top-(entry.y%5)*step-size.y*.5f); }
                    var image = UiFactory.CreateImage(_contents,"Loot_"+entry.entryId,LootPanelStyle.Ink,size,at,new Vector2(.5f,.5f),new Vector2(.5f,.5f));
                    LootPanelStyle.Decorate(image,item,instance,!equipped);
                    if(entry.item.quantity > 1) Label(image.transform,"Count",entry.item.quantity.ToString(),13,new Vector2(size.x*.5f-12,size.y*.5f-10),new Vector2(24,18));
                    _rows.Add(new Row { Rect=image.rectTransform, Index=i, Item=item, Instance=instance });
                }
            _previous.gameObject.SetActive(Pages>1); _next.gameObject.SetActive(Pages>1);
            _previous.interactable = _page>0; _next.interactable = _page+1<Pages;
            _pageLabel.text = Pages>1 ? (_page+1)+" / "+Pages : "";
            _status.text = !string.IsNullOrEmpty(_lastFailure) ? _lastFailure : (_session.Status ?? "");
            if (doc != null && doc.entries.Length == 0) _status.text = "Empty";
            _tip?.gameObject.SetActive(false);
        }

        private void OnPlayerRunState(int playerId, RunState state)
        {
            if (_rig == null || playerId != _rig.PlayerId) return;
            if (state == RunState.Dead || state == RunState.Extracted || state == RunState.Lobby) Close(false);
        }

        private void OnEnable() => GameEvents.PlayerRunStateChanged += OnPlayerRunState;
        private void OnDisable()
        {
            GameEvents.PlayerRunStateChanged -= OnPlayerRunState;
            Close();
        }

        private void OnDestroy()
        {
            Close(false);
            if (_client != null)
            {
                _client.CorpseContainerReceived -= OnState;
                _client.CorpseTransferResultReceived -= OnClientResult;
            }
            if (_root != null) Destroy(_root.gameObject);
            if (_tip != null) Destroy(_tip.gameObject);
            ClearCarry();
        }
    }
}
