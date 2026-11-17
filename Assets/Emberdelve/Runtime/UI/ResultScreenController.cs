using UnityEngine;
using UnityEngine.UI;
using Emberdelve.Core;
using Emberdelve.Flow;

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
        private bool _navigationRequested;

        private void OnEnable() => GameEvents.RunEnded += OnRunEnded;
        private void OnDisable() => GameEvents.RunEnded -= OnRunEnded;

        private void OnRunEnded(int playerId, RunResult result)
        {
            // R7: the overlay shows the LOCAL player's own result, when that player's run ends (not when the whole run does).
            if (!PlayerView.Shows(playerId)) return;
            BuildIfNeeded();
            if (_root == null) return;

            _root.gameObject.SetActive(true);
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
