using System.Collections.Generic;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Flow;
using Emberdelve.Items;
using Emberdelve.Player;
using Emberdelve.UI;
using UnityEngine;

namespace Emberdelve.Net
{
    /// <summary>Owner-only corpse list. Buttons send item intents; only server snapshots change displayed contents.</summary>
    [DisallowMultipleComponent]
    public sealed class CorpseContainerScreenController : MonoBehaviour
    {
        private const int RowsPerPage = 6;
        private const double StateTimeoutSeconds = 6.0;
        private const double TransferTimeoutSeconds = 8.0;
        private static readonly Color PanelColor = new Color(0.055f, 0.05f, 0.045f, 0.98f);
        private static readonly Color TextColor = new Color(0.91f, 0.87f, 0.79f, 1f);

        private readonly Rules.CorpseContainerUiSession _session = new Rules.CorpseContainerUiSession();
        private readonly List<Row> _rows = new List<Row>(RowsPerPage);
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
            public UnityEngine.UI.Text Label;
            public UnityEngine.UI.Button TakeOne;
            public UnityEngine.UI.Button TakeAll;
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
            if (_client != null && _client.IsOpen && !_presenter.IsContainerUiOpen)
                GetComponent<NetInventoryClient>()?.CloseSatchelForCorpse();
            else if (_rig.GetComponent<InventoryScreenController>() is InventoryScreenController satchel && satchel.IsOpen)
                satchel.Close();
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
            _lastFailure = null;
            _page = 0;
            _stateStartedAt = 0.0;
            _transferStartedAt = 0.0;
            _refreshAt = 0.0;
            if (_root != null) _root.gameObject.SetActive(false);
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
            int entryIndex = _page * RowsPerPage + rowIndex;
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
            UiFactory.Stretch(_root);
            _root.gameObject.SetActive(false);
            UiFactory.CreateImage(_root, "Backdrop", new Color(0.015f, 0.012f, 0.01f, 0.72f),
                new Vector2(2400f, 1400f), Vector2.zero, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));

            _panel = UiFactory.CreateGroup(_root, "CorpsePanel");
            UiFactory.SetRect(_panel, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(760f, 550f), Vector2.zero);
            UnityEngine.UI.Image panelImage = _panel.gameObject.AddComponent<UnityEngine.UI.Image>();
            panelImage.color = PanelColor;

            _header = UiFactory.CreateText(_panel, "Header", "CORPSE", 25,
                new Color(0.95f, 0.78f, 0.4f, 1f), TextAnchor.MiddleCenter);
            UiFactory.SetRect(_header.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                new Vector2(680f, 38f), new Vector2(0f, -32f));
            _status = UiFactory.CreateText(_panel, "Status", "", 16, TextColor, TextAnchor.MiddleCenter);
            UiFactory.SetRect(_status.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                new Vector2(680f, 28f), new Vector2(0f, -72f));

            for (int i = 0; i < RowsPerPage; i++)
            {
                int rowIndex = i;
                RectTransform row = UiFactory.CreateGroup(_panel, "ItemRow_" + i);
                UiFactory.SetRect(row, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                    new Vector2(700f, 54f), new Vector2(0f, 123f - i * 61f));
                UnityEngine.UI.Image rowImage = row.gameObject.AddComponent<UnityEngine.UI.Image>();
                rowImage.color = new Color(0.11f, 0.095f, 0.08f, 1f);
                var widget = new Row
                {
                    Label = UiFactory.CreateText(row, "ItemLabel", "", 17, TextColor, TextAnchor.MiddleLeft),
                    TakeOne = UiFactory.CreateButton(row, "TakeOne", "Take 1", () => Take(rowIndex, false),
                        new Vector2(94f, 40f), new Color(0.22f, 0.18f, 0.12f, 1f), TextColor, 15),
                    TakeAll = UiFactory.CreateButton(row, "TakeAll", "Take all", () => Take(rowIndex, true),
                        new Vector2(104f, 40f), new Color(0.31f, 0.22f, 0.1f, 1f), TextColor, 15)
                };
                UiFactory.SetRect(widget.Label.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                    new Vector2(435f, 42f), new Vector2(14f, 0f), new Vector2(0f, 0.5f));
                UiFactory.SetRect(((UnityEngine.UI.Image)widget.TakeOne.targetGraphic).rectTransform,
                    new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(94f, 40f), new Vector2(-160f, 0f));
                UiFactory.SetRect(((UnityEngine.UI.Image)widget.TakeAll.targetGraphic).rectTransform,
                    new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(104f, 40f), new Vector2(-54f, 0f));
                _rows.Add(widget);
            }

            _previous = UiFactory.CreateButton(_panel, "PreviousPage", "Previous", () => ChangePage(-1),
                new Vector2(120f, 38f), new Color(0.13f, 0.12f, 0.11f, 1f), TextColor, 15);
            UiFactory.SetRect(((UnityEngine.UI.Image)_previous.targetGraphic).rectTransform,
                new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(120f, 38f), new Vector2(-235f, 24f));
            _next = UiFactory.CreateButton(_panel, "NextPage", "Next", () => ChangePage(1),
                new Vector2(120f, 38f), new Color(0.13f, 0.12f, 0.11f, 1f), TextColor, 15);
            UiFactory.SetRect(((UnityEngine.UI.Image)_next.targetGraphic).rectTransform,
                new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(120f, 38f), new Vector2(235f, 24f));
            _pageLabel = UiFactory.CreateText(_panel, "Page", "", 15, TextColor, TextAnchor.MiddleCenter);
            UiFactory.SetRect(_pageLabel.rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
                new Vector2(130f, 36f), new Vector2(0f, 24f));
            UnityEngine.UI.Button refresh = UiFactory.CreateButton(_panel, "Refresh", "Refresh", Refresh,
                new Vector2(115f, 38f), new Color(0.15f, 0.19f, 0.19f, 1f), TextColor, 15);
            UiFactory.SetRect(((UnityEngine.UI.Image)refresh.targetGraphic).rectTransform,
                new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(115f, 38f), new Vector2(-95f, -25f));
            UnityEngine.UI.Button close = UiFactory.CreateButton(_panel, "Close", "Close", () => Close(),
                new Vector2(115f, 38f), new Color(0.22f, 0.12f, 0.1f, 1f), TextColor, 15);
            UiFactory.SetRect(((UnityEngine.UI.Image)close.targetGraphic).rectTransform,
                new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(115f, 38f), new Vector2(95f, -25f));
        }

        private void Refresh()
        {
            if (!IsOpen || _session.IsTransferPending || _session.StateRequestSequence != 0) return;
            _lastFailure = null;
            RequestState();
        }

        private void ChangePage(int delta)
        {
            int count = _session.Document != null ? _session.Document.entries.Length : 0;
            int pages = Mathf.Max(1, Mathf.CeilToInt(count / (float)RowsPerPage));
            _page = Mathf.Clamp(_page + delta, 0, pages - 1);
            Render();
        }

        private void ClampPage()
        {
            int count = _session.Document != null ? _session.Document.entries.Length : 0;
            int pages = Mathf.Max(1, Mathf.CeilToInt(count / (float)RowsPerPage));
            _page = Mathf.Clamp(_page, 0, pages - 1);
        }

        private void Render()
        {
            if (_header != null) _header.text = "CORPSE " + _session.ActiveCorpseId;
            if (_status != null) _status.text = !string.IsNullOrEmpty(_lastFailure) ? _lastFailure : (_session.Status ?? string.Empty);
            RenderRows();
        }

        private void RenderRows()
        {
            if (_rows.Count == 0) return;
            CorpseContainerDocument document = _session.Document;
            int count = document != null && document.entries != null ? document.entries.Length : 0;
            int pages = Mathf.Max(1, Mathf.CeilToInt(count / (float)RowsPerPage));
            bool bagReady = _client == null || _client.IsReady;
            for (int i = 0; i < _rows.Count; i++)
            {
                int index = _page * RowsPerPage + i;
                bool visible = index < count;
                Row row = _rows[i];
                row.Label.text = visible ? BuildItemLabel(document.entries[index]) : string.Empty;
                row.Label.transform.parent.gameObject.SetActive(visible);
                int quantity = visible && document.entries[index]?.item != null ? document.entries[index].item.quantity : 0;
                bool canTransfer = visible && bagReady && !_session.IsTransferPending && _session.StateRequestSequence == 0;
                row.TakeOne.gameObject.SetActive(visible && quantity > 0);
                row.TakeOne.interactable = canTransfer;
                row.TakeAll.gameObject.SetActive(visible && quantity > 1);
                row.TakeAll.interactable = canTransfer;
            }
            if (_pageLabel != null) _pageLabel.text = "Page " + (_page + 1) + " / " + pages;
            if (_previous != null) _previous.interactable = _page > 0;
            if (_next != null) _next.interactable = _page + 1 < pages;
            if (_status != null) _status.text = !string.IsNullOrEmpty(_lastFailure) ? _lastFailure :
                (!bagReady && count > 0 ? "Syncing your bag…" : (_session.Status ?? string.Empty));
        }

        private string BuildItemLabel(CorpseContainerEntryDocument entry)
        {
            if (entry?.item == null) return "Invalid item";
            string label = entry.item.itemId ?? "Unknown item";
            if (_bag != null && _bag.Catalog != null)
            {
                IReadOnlyList<ItemData> items = _bag.Catalog.Items;
                for (int i = 0; i < items.Count; i++)
                {
                    if (items[i] == null || items[i].itemId != entry.item.itemId) continue;
                    label = items[i].displayName;
                    break;
                }
            }
            if (!string.IsNullOrEmpty(entry.item.uid)) label += "  • gear";
            else if (entry.item.quantity > 1) label += "  × " + entry.item.quantity;
            return label;
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
        }
    }
}
