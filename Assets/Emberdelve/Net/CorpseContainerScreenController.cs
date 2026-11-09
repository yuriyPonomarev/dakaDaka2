using System.Collections.Generic;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Flow;
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
            _owner = rig != null ? rig.GetComponent<NetPlayerRig>() : null;
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
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            GetComponent<NetInputSender>()?.ClearPendingMovement();
            if (_hostOwner != null)
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
            _lastFailure = string.IsNullOrEmpty(reason) ? "Could not read corpse contents" : reason;
            Render();
            _stateStartedAt = 0.0;
            _refreshAt = Time.realtimeSinceStartupAsDouble + 0.35;
        }

        public void OnResult(ulong corpseId, uint sequence, bool accepted, ulong corpseRevision, string reason)
        {
            if (!_session.ApplyResult(corpseId, sequence, accepted, corpseRevision, reason)) return;
            _lastFailure = accepted ? null : (string.IsNullOrEmpty(reason) ? "Server refused this corpse request" : reason);
            if (_session.TransferSequence == 0) _transferStartedAt = 0.0;
            if (_session.StateRequestSequence == 0) _stateStartedAt = 0.0;
            if (_session.NeedsRefresh) _refreshAt = Time.realtimeSinceStartupAsDouble + 0.35;
            Render();
        }

        public void Close(bool relockCursor = true)
        {
            if (!IsOpen) return;
            _session.Close();
            if (_inventoryScreen != null) { _inventoryScreen.Close(false); _inventoryScreen.SetLootCompanion(false); }
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

            _root.localScale = Vector3.one * Mathf.Min(1f, Screen.width / 1100f, Screen.height / 700f);
            if (Keyboard.current != null && (Keyboard.current.escapeKey.wasPressedThisFrame || Keyboard.current.iKey.wasPressedThisFrame)) { Close(); return; }
            _inventoryScreen?.HandlePointer();
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

        private void Take(int rowIndex, bool all)
        {
            if (!IsOpen || _bag == null || _presenter == null || _session.Document == null || _session.IsTransferPending) return;
            int entryIndex = rowIndex;
            if (entryIndex < 0 || entryIndex >= _session.Document.entries.Length) return;
            CorpseContainerEntryDocument entry = _session.Document.entries[entryIndex];
            if (entry == null || entry.item == null) return;
            int amount = all ? entry.item.quantity : 1;
            if (!_session.TryBeginTransfer(entry.entryId, amount, _bag.Revision,
                    out Rules.CorpseTransferUiRequest request, out string reason))
            {
                _lastFailure = reason;
                Render();
                return;
            }

            _lastFailure = null;
            bool sent = _presenter.RequestTransfer(request, _session.AttachTransferSequence, out _);
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
            _panel.anchoredPosition = new Vector2(270, 0);
            var background = _panel.gameObject.AddComponent<UnityEngine.UI.Image>();
            LootPanelStyle.Panel(background);
            _header = Label(_panel, "Header", "LOOT", 22, new Vector2(0, 297), new Vector2(440, 32));
            _header.color = new Color(.89f, .77f, .51f);
            _status = Label(_panel, "Status", "", 14, new Vector2(0, -233), new Vector2(475, 58));
            _status.horizontalOverflow = UnityEngine.HorizontalWrapMode.Wrap;
            Label(_panel, "Instructions", "Click: take stack   •   Shift + click: take one", 13, new Vector2(0, -278), new Vector2(475, 26));
            _pageLabel = Label(_panel, "Page", "", 13, new Vector2(0, -185), new Vector2(180, 26));
            _previous = Button("PreviousPage", "<", () => ChangePage(-1), new Vector2(-110,-185), new Vector2(40,26));
            _next = Button("NextPage", ">", () => ChangePage(1), new Vector2(110,-185), new Vector2(40,26));
            Button("Refresh", "Refresh", Refresh, new Vector2(-66,-308), new Vector2(110,26));
            Button("Close", "Close [I / Esc]", () => Close(), new Vector2(66,-308), new Vector2(130,26));
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

        private void HandleLootPointer()
        {
            if (Mouse.current == null || _tip == null) return;
            Vector2 pointer = Mouse.current.position.ReadValue();
            _tip.gameObject.SetActive(false);
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
                if (Mouse.current.leftButton.wasPressedThisFrame || Mouse.current.rightButton.wasPressedThisFrame)
                    Take(row.Index, !(Keyboard.current != null && Keyboard.current.shiftKey.isPressed));
                return;
            }
        }

        private void Refresh()
        {
            if (!IsOpen || _session.IsTransferPending || _session.StateRequestSequence != 0 || _session.Document?.entries.Length == 0) return;
            _lastFailure = null;
            RequestState();
        }

        private int Pages => Mathf.Max(1, Mathf.CeilToInt((_session.Document?.height ?? 4) / 4f));
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
            for (int y = 0; y < 4; y++) for (int x = 0; x < width; x++)
                UiFactory.CreateImage(_contents,"Cell_"+x+"_"+y,new Color(.13f,.12f,.11f),new Vector2(cell,cell),new Vector2(-width*step*.5f+x*step+cell*.5f,top-y*step-cell*.5f),new Vector2(.5f,.5f),new Vector2(.5f,.5f));
            if (doc != null && _bag?.Catalog != null)
                for (int i = 0; i < doc.entries.Length; i++)
                {
                    var entry = doc.entries[i];
                    if (!_bag.Catalog.TryResolveItem(entry.item.itemId,out ItemData item,out _)) continue;
                    bool equipped = !string.IsNullOrEmpty(entry.slot) && System.Enum.TryParse(entry.slot,out EquipmentSlot _);
                    if (!equipped && (entry.y / 4 != _page)) continue;
                    ItemInstance instance = null;
                    if (!string.IsNullOrEmpty(entry.item.uid)) ItemInstance.TryRestore(entry.item,_bag.Catalog.Authority,out instance,out _);
                    Vector2 size, at;
                    if (equipped) { var slot = (EquipmentSlot)System.Enum.Parse(typeof(EquipmentSlot), entry.slot); size = LootPanelStyle.SlotSize(slot); at = LootPanelStyle.SlotPosition(slot); }
                    else { Vector2Int shape = item.EffectiveFootprint; size = new Vector2(shape.x*step-2,shape.y*step-2); at = new Vector2(-width*step*.5f+entry.x*step+size.x*.5f,top-(entry.y%4)*step-size.y*.5f); }
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
        }
    }
}
