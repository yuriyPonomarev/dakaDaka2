using UnityEngine;
using UnityEngine.UI;
using Emberdelve.Core;
using Emberdelve.Flow;
using Emberdelve.Level;
using Emberdelve.Player;
using Emberdelve.Rules;

namespace Emberdelve.UI
{
    /// <summary>
    /// End-of-run overlay. Listens for RunEnded only: it never decides the outcome itself.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ResultScreenController : MonoBehaviour
    {
        private Canvas _canvas;
        private RectTransform _root;
        private Text _titleLabel;
        private Text _detailLabel;
        private RunDirector _director;
        private PlayerRig _fallenPlayer;
        private PortalSpectatorCamera _spectator;
        private RectTransform _spectatorRoot;
        private Text _spectatorLabel;
        private Button _watchTeamButton;
        private float _nextAllyCheck;
        private bool _navigationRequested;
        public bool IsWatchingTeam => _spectatorRoot != null && _spectatorRoot.gameObject.activeSelf;

        private void OnEnable() => GameEvents.RunEnded += OnRunEnded;
        private void OnDisable()
        {
            GameEvents.RunEnded -= OnRunEnded;
            if (_spectator != null) _spectator.StopWatchingAfterDeath();
            if (_spectatorRoot != null) _spectatorRoot.gameObject.SetActive(false);
            if (_root != null) _root.gameObject.SetActive(false);
        }

        private void OnRunEnded(int playerId, RunResult result)
        {
            // R7: the overlay shows the LOCAL player's own result, when that player's run ends (not when the whole run does).
            if (!PlayerView.Shows(playerId)) return;
            BuildIfNeeded();
            if (_root == null) return;

            UiFactory.EnsureEventSystem();
            _navigationRequested = false;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            bool escaped = result.Outcome == RunState.Extracted;

            _titleLabel.text = escaped ? "YOU ESCAPED THE DELVE" : "YOU DIED IN THE DELVE";
            Color titleColor = escaped ? new Color(0.55f, 0.86f, 0.55f, 1f) : new Color(0.9f, 0.3f, 0.25f, 1f);
            _titleLabel.color = titleColor;

            _detailLabel.text =
                $"Class: {result.ClassName}\n" +
                $"Loot value: {Mathf.RoundToInt(result.LootValue)}   Items: {result.ItemsCollected}\n" +
                $"Slain: {result.EnemiesSlain}   Time: {FormatTime(result.DurationSeconds)}";

            if (_director == null) GameServices.TryGet<RunDirector>(out _director);
            bool team = NetRole.IsClientOnly
                ? DungeonModulePlanSession.Current?.RoyaleMode is RoyalePartyMode.Duo or RoyalePartyMode.Trio
                : _director != null && _director.HasTeamDescent;
            _fallenPlayer = result.Outcome == RunState.Dead && team
                ? (NetRole.IsClientOnly ? GetComponent<PlayerRig>() : _director.Player) : null;
            if (!TryWatchTeam()) ShowResult();
        }

        private bool TryWatchTeam()
        {
            if (!PortalSpectatorCamera.HasLivingAlly(_fallenPlayer)) return false;
            _spectator = _fallenPlayer.GetComponent<PortalSpectatorCamera>();
            if (_spectator == null) _spectator = _fallenPlayer.gameObject.AddComponent<PortalSpectatorCamera>();
            if (!_spectator.WatchAfterDeath(_fallenPlayer)) return false;
            BuildSpectatorIfNeeded();
            _root.gameObject.SetActive(false);
            _spectatorRoot.gameObject.SetActive(true);
            _spectatorLabel.text = HudController.DescribeSpectator(_spectator);
            return true;
        }

        private void Update()
        {
            if (IsWatchingTeam)
            {
                if (_spectator == null || !_spectator.HasLivingAllies) ShowResult();
                else _spectatorLabel.text = HudController.DescribeSpectator(_spectator);
            }
            else if (_root != null && _root.gameObject.activeSelf && _fallenPlayer != null && Time.unscaledTime >= _nextAllyCheck)
                RefreshWatchButton();
        }

        private void ShowResult()
        {
            if (_spectator != null) _spectator.StopWatchingAfterDeath();
            if (_spectatorRoot != null) _spectatorRoot.gameObject.SetActive(false);
            _root.gameObject.SetActive(true);
            RefreshWatchButton();
        }

        private void RefreshWatchButton()
        {
            _nextAllyCheck = Time.unscaledTime + .25f;
            _watchTeamButton.gameObject.SetActive(PortalSpectatorCamera.HasLivingAlly(_fallenPlayer));
        }

        private void BuildSpectatorIfNeeded()
        {
            if (_spectatorRoot != null) return;
            _spectatorRoot = UiFactory.CreateGroup(_canvas.transform, "DeathSpectatorRoot");
            UiFactory.Stretch(_spectatorRoot);
            var bottom = new Vector2(.5f, 0f);
            _spectatorLabel = UiFactory.CreateText(_spectatorRoot, "SpectatorAlly", "", 20,
                Color.white, TextAnchor.MiddleCenter, FontStyle.Bold);
            UiFactory.SetRect(_spectatorLabel.rectTransform, bottom, bottom, new Vector2(600f, 32f), new Vector2(0f, 112f));
            _spectatorLabel.raycastTarget = false;
            var results = UiFactory.CreateButton(_spectatorRoot, "SpectatorResultsButton", "Results", ShowResult,
                new Vector2(152f, 40f), new Color(.16f, .2f, .26f, .94f), new Color(.9f, .92f, .96f), 18);
            UiFactory.SetRect((RectTransform)results.transform, bottom, bottom, new Vector2(152f, 40f), new Vector2(0f, 64f));
        }

        private void BuildIfNeeded()
        {
            if (_canvas != null) return;

            _canvas = UiFactory.EnsureCanvas("Emberdelve_Result", 30);

            _root = UiFactory.CreateGroup(_canvas.transform, "ResultRoot");
            UiFactory.Stretch(_root);
            _root.gameObject.SetActive(false);

            UiFactory.CreateImage(_root, "Backdrop", new Color(0.02f, 0.015f, 0.01f, 0.88f),
                new Vector2(2400f, 1400f), Vector2.zero, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));

            _titleLabel = UiFactory.CreateText(_root, "Title", "", 44, Color.white, TextAnchor.MiddleCenter);

            DungeonUiStyle.Heading(_titleLabel);
            UiFactory.SetRect(_titleLabel.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(1100f, 70f), new Vector2(0f, 150f));

            _detailLabel = UiFactory.CreateText(_root, "Details", "", 22, new Color(0.9f, 0.86f, 0.78f, 1f), TextAnchor.MiddleCenter);
            UiFactory.SetRect(_detailLabel.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(900f, 120f), new Vector2(0f, 40f));

            Button again = UiFactory.CreateButton(_root, "RunAgainButton", "Descend again", OnRestart,
                new Vector2(300f, 54f), new Color(0.32f, 0.24f, 0.12f, 1f), new Color(0.96f, 0.9f, 0.8f, 1f), 22);
            UiFactory.SetRect(((Image)again.targetGraphic).rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(300f, 54f), new Vector2(-160f, -90f));

            Button lobby = UiFactory.CreateButton(_root, "LobbyButton", "Return to lobby", OnLobby,
                new Vector2(300f, 54f), new Color(0.16f, 0.2f, 0.26f, 1f), new Color(0.9f, 0.92f, 0.96f, 1f), 22);
            UiFactory.SetRect(((Image)lobby.targetGraphic).rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(300f, 54f), new Vector2(160f, -90f));

            var center = new Vector2(.5f, .5f);
            _watchTeamButton = UiFactory.CreateButton(_root, "SpectateTeamButton", "Spectate team", () => TryWatchTeam(),
                new Vector2(300f, 48f), new Color(.16f, .2f, .26f, 1f), new Color(.9f, .92f, .96f), 21);
            UiFactory.SetRect((RectTransform)_watchTeamButton.transform, center, center, new Vector2(300f, 48f), new Vector2(0f, -151f));
        }

        private void OnRestart()
        {
            if (NetRole.IsNetworked)
            {
                if (!_navigationRequested) _navigationRequested = NetLobbyBridge.NavigateResult(true);
                return;
            }
            if (_director == null) GameServices.TryGet<RunDirector>(out _director);
            if (_director != null) _director.RestartRun();
        }

        private void OnLobby()
        {
            if (NetRole.IsNetworked)
            {
                if (!_navigationRequested) _navigationRequested = NetLobbyBridge.NavigateResult(false);
                return;
            }
            if (_director == null) GameServices.TryGet<RunDirector>(out _director);
            if (_director != null) _director.ReturnToLobby();
            else if (GameServices.TryGet<LobbyReturnTarget>(out LobbyReturnTarget target)) target.ReturnToLobby();
        }

        private static string FormatTime(float seconds)
        {
            int total = Mathf.Max(0, Mathf.RoundToInt(seconds));
            return $"{total / 60:00}:{total % 60:00}";
        }
    }

    /// <summary>Fallback so a result screen can always offer a way back, even without a RunDirector.</summary>
    public sealed class LobbyReturnTarget : MonoBehaviour
    {
        [SerializeField] private string lobbySceneName = "Lobby";

        public void ReturnToLobby()
        {
            Time.timeScale = 1f;
            UnityEngine.SceneManagement.SceneManager.LoadScene(lobbySceneName);
        }
    }
}
