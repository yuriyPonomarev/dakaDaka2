using System;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Level;
using Emberdelve.Net.Rules;

namespace Emberdelve.UI
{
    /// <summary>
    /// U3A5: the lobby's Online panel: an address field, Host and Join buttons, and one status line. Hosting always starts
    /// the same authored modular crypt scene; a joining client follows the server's scene. The class is the
    /// lobby's current selection. It only talks to <see cref="NetLobbyBridge"/>; the network layer does the rest. It exists only when
    /// the bridge has a handler (the Net assembly is in the build), and it never touches the offline "Descend" flow.
    ///
    /// The text field is hand-made on purpose: the legacy InputField reads the old Input class, and this project runs the Input System
    /// package only. Typing comes from Keyboard.onTextInput; Backspace, Enter, Escape and Ctrl+V are read from the keyboard device.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class LobbyOnlinePanel : MonoBehaviour
    {
        public const string DefaultAddressText = "127.0.0.1:7777";
        private const int MaxChars = 32;
        private const string LogPrefix = "[Emberdelve][Lobby] ";

        private Func<CharacterClassData> _classProvider;
        private string _address = DefaultAddressText;
        private bool _built;
        private bool _focused;
        private Text _addressText;
        private Text _statusText;
        private Image _fieldImage;
        private Button _join;
        private Button _host;
        private Button _cancel;
        private Keyboard _subscribedKeyboard;
        private float _autoJoinAt = -1f;
        private float _backspaceNext = -1f;

        public bool IsBuilt => _built;

        /// <summary>Builds the panel into <paramref name="canvas"/>. A no-op (nothing is created) when networking is not in this build.</summary>
        public void Build(Canvas canvas, Func<CharacterClassData> classProvider)
        {
            if (_built || canvas == null || !NetLobbyBridge.IsAvailable) return;
            _built = true;
            _classProvider = classProvider;

            RectTransform root = UiFactory.CreateGroup(canvas.transform.Find("LobbyRoot") ?? canvas.transform, "OnlinePanel");
            UiFactory.SetRect(root, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(340f, 225f), new Vector2(40f, -140f), new Vector2(0f, 1f));

            var background = UiFactory.CreateImage(root, "Bg", new Color(0.05f, 0.04f, 0.03f, 0.9f), new Vector2(340f, 225f),
                new Vector2(170f, -112.5f), new Vector2(0f, 1f), new Vector2(0f, 1f));
            DungeonUiStyle.Surface(background);

            Text header = UiFactory.CreateText(root, "Header", "PLAY ONLINE", 17, new Color(0.95f, 0.78f, 0.4f, 1f), TextAnchor.MiddleLeft);
            UiFactory.SetRect(header.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(320f, 24f), new Vector2(170f, -20f));

            _fieldImage = UiFactory.CreateImage(root, "AddressField", new Color(0.13f, 0.11f, 0.09f, 1f), new Vector2(320f, 34f),
                new Vector2(170f, -56f), new Vector2(0f, 1f), new Vector2(0f, 1f));
            Button fieldButton = _fieldImage.gameObject.AddComponent<Button>();
            fieldButton.targetGraphic = _fieldImage;
            fieldButton.onClick.AddListener(() => SetFocus(true));

            _addressText = UiFactory.CreateText(_fieldImage.transform, "Text", _address, 18, new Color(0.96f, 0.9f, 0.8f, 1f), TextAnchor.MiddleLeft);
            UiFactory.SetRect(_addressText.rectTransform, Vector2.zero, Vector2.one, new Vector2(-16f, 0f), Vector2.zero);

            _join = UiFactory.CreateButton(root, "JoinButton", "JOIN", () => StartSession(false),
                new Vector2(100f, 38f), new Color(0.2f, 0.36f, 0.22f, 1f), new Color(0.95f, 0.98f, 0.9f, 1f), 20);
            PlaceTopLeft(_join, new Vector2(100f, 38f), new Vector2(60f, -112f));

            _host = UiFactory.CreateButton(root, "HostButton", "HOST", () => StartSession(true),
                new Vector2(100f, 38f), new Color(0.3f, 0.24f, 0.12f, 1f), new Color(1f, 0.95f, 0.85f, 1f), 20);
            PlaceTopLeft(_host, new Vector2(100f, 38f), new Vector2(170f, -112f));

            _cancel = UiFactory.CreateButton(root, "CancelButton", "CANCEL", () => { SetFocus(false); NetLobbyBridge.Cancel(); },
                new Vector2(210f, 38f), new Color(0.45f, 0.18f, 0.14f, 1f), new Color(1f, 0.92f, 0.88f, 1f), 20);
            PlaceTopLeft(_cancel, new Vector2(210f, 38f), new Vector2(115f, -112f));

            _statusText = UiFactory.CreateText(root, "Status", "", 15, new Color(0.8f, 0.76f, 0.68f, 1f), TextAnchor.UpperLeft);
            _statusText.horizontalOverflow = HorizontalWrapMode.Wrap;
            _statusText.verticalOverflow = VerticalWrapMode.Truncate;
            UiFactory.SetRect(_statusText.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(320f, 46f), new Vector2(170f, -174f));

            NetLobbyBridge.Changed += Refresh;
            Refresh();

#if DEVELOPMENT_BUILD || UNITY_EDITOR
            // Proof hook (development builds only): join a server without a mouse. The gate starts the lobby, this arms a join one second later.
            string auto = ReadArg("-ember-lobby-autojoin");
            if (!string.IsNullOrEmpty(auto))
            {
                _address = auto.Trim().Trim('"');
                RefreshAddress();
                _autoJoinAt = Time.unscaledTime + 1f;
                Debug.Log(LogPrefix + "autojoin armed address=" + _address);
            }
#endif
        }

        private static void PlaceTopLeft(Button button, Vector2 size, Vector2 center)
        {
            UiFactory.SetRect(((Image)button.targetGraphic).rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), size, center);
        }

        private void OnDestroy()
        {
            NetLobbyBridge.Changed -= Refresh;
            UnsubscribeKeyboard();
        }

        // ---- input ---------------------------------------------------------------------------------------------

        private void Update()
        {
            if (!_built) return;
            NetLobbyBridge.StartPendingRetry();

            if (_autoJoinAt > 0f && Time.unscaledTime >= _autoJoinAt)
            {
                _autoJoinAt = -1f;
                StartSession(false);
            }

            Keyboard keyboard = Keyboard.current;
            if (keyboard != _subscribedKeyboard)
            {
                UnsubscribeKeyboard();
                if (keyboard != null)
                {
                    keyboard.onTextInput += OnTextInput;
                    _subscribedKeyboard = keyboard;
                }
            }

            if (keyboard == null || !_focused) return;

            if (keyboard.escapeKey.wasPressedThisFrame) { SetFocus(false); return; }
            if (keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame) { StartSession(false); return; }

            // Backspace with key repeat (the text event stream does not carry it reliably).
            if (keyboard.backspaceKey.wasPressedThisFrame)
            {
                Backspace();
                _backspaceNext = Time.unscaledTime + 0.4f;
            }
            else if (keyboard.backspaceKey.isPressed && _backspaceNext > 0f && Time.unscaledTime >= _backspaceNext)
            {
                Backspace();
                _backspaceNext = Time.unscaledTime + 0.04f;
            }
            else if (!keyboard.backspaceKey.isPressed)
            {
                _backspaceNext = -1f;
            }

            if (keyboard.vKey.wasPressedThisFrame && (keyboard.ctrlKey.isPressed || keyboard.leftCtrlKey.isPressed))
            {
                string pasted = GUIUtility.systemCopyBuffer;
                if (!string.IsNullOrEmpty(pasted))
                    for (int i = 0; i < pasted.Length; i++) Append(pasted[i]);
            }

            // Caret blink: redraw the label twice a second.
            RefreshAddress();
        }

        private void UnsubscribeKeyboard()
        {
            if (_subscribedKeyboard != null) _subscribedKeyboard.onTextInput -= OnTextInput;
            _subscribedKeyboard = null;
        }

        private void OnTextInput(char c)
        {
            if (_focused) Append(c);
        }

        private static bool Allowed(char c)
        {
            return (c >= '0' && c <= '9') || c == '.' || c == ':' || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
        }

        private void Append(char c)
        {
            if (!Allowed(c) || _address.Length >= MaxChars) return;
            _address += c;
            RefreshAddress();
        }

        private void Backspace()
        {
            if (_address.Length == 0) return;
            _address = _address.Substring(0, _address.Length - 1);
            RefreshAddress();
        }

        private void SetFocus(bool focused)
        {
            _focused = focused;
            _backspaceNext = -1f;
            RefreshAddress();
        }

        // ---- actions -------------------------------------------------------------------------------------------

        private void StartSession(bool host)
        {
            if (!_built) return;
            SetFocus(false);

            if (NetLobbyBridge.Phase == NetLobbyPhase.Connecting || NetLobbyBridge.Phase == NetLobbyPhase.Connected) return;

            string modularScenePath = "Assets/Emberdelve/Scenes/" + NetTuning.ModularDungeonSceneName + ".unity";
            if (SceneUtility.GetBuildIndexByScenePath(modularScenePath) < 0 ||
                Resources.Load<DungeonModuleCatalog>("Emberdelve/DungeonModuleCatalog") == null)
            {
                NetLobbyBridge.Report(NetLobbyPhase.Failed, "required Modular Crypts scene/catalog is missing from this build");
                GameEvents.RaiseToast("This build is missing the required modular crypt scene or catalog.");
                return;
            }

            CharacterClassData selected = _classProvider != null ? _classProvider() : null;
            if (selected == null)
            {
                NetLobbyBridge.Report(NetLobbyPhase.Failed, "choose a class first");
                return;
            }

            if (!NetEndpoint.TryParse(_address, out string address, out int port, out string error))
            {
                NetLobbyBridge.Report(NetLobbyPhase.Failed, "invalid address: " + error);
                return;
            }

            Debug.Log(LogPrefix + "online " + (host ? "host" : "join") + " map=" + (host ? "modular-3x3" : "server-selected") +
                " address=" + address + " port=" + port + " class=" + selected.classId);
            NetLobbyBridge.TryStart(host, address, port, selected.classId, out _);
        }

        // ---- view ----------------------------------------------------------------------------------------------

        private void Refresh()
        {
            if (!_built || _statusText == null) return;

            NetLobbyPhase phase = NetLobbyBridge.Phase;
            string status = NetLobbyBridge.Status;
            _statusText.text = status.Length == 0
                ? "Host uses the selected team size. Join follows the server. Both start with base gear."
                : status;

            switch (phase)
            {
                case NetLobbyPhase.Connecting: _statusText.color = new Color(0.95f, 0.82f, 0.45f, 1f); break;
                case NetLobbyPhase.Connected: _statusText.color = new Color(0.55f, 0.9f, 0.55f, 1f); break;
                case NetLobbyPhase.Refused:
                case NetLobbyPhase.Failed: _statusText.color = new Color(0.95f, 0.5f, 0.42f, 1f); break;
                default: _statusText.color = new Color(0.8f, 0.76f, 0.68f, 1f); break;
            }

            bool busy = phase == NetLobbyPhase.Connecting || phase == NetLobbyPhase.Connected;
            _join.gameObject.SetActive(!busy);
            _host.gameObject.SetActive(!busy);
            _cancel.gameObject.SetActive(busy);
            RefreshAddress();
        }

        private void RefreshAddress()
        {
            if (_addressText == null) return;
            bool caret = _focused && (int)(Time.unscaledTime * 2f) % 2 == 0;
            _addressText.text = _address + (caret ? "|" : "");
            if (_fieldImage != null)
                _fieldImage.color = _focused ? new Color(0.2f, 0.17f, 0.12f, 1f) : new Color(0.13f, 0.11f, 0.09f, 1f);
        }

#if DEVELOPMENT_BUILD || UNITY_EDITOR
        private static string ReadArg(string name)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return null;
        }
#endif
    }
}
