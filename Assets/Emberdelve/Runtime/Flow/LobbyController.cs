using UnityEngine;
using UnityEngine.SceneManagement;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.UI;

namespace Emberdelve.Flow
{
    /// <summary>
    /// Safe-room flow: pick a class, record it in RunContext, load the dungeon.
    /// In a networked build this is where the session service would hand the client an agreed
    /// RunContext instead of the player choosing locally.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class LobbyController : MonoBehaviour
    {
        [Header("Configuration")]
        [SerializeField] private GameConfig config;
        [SerializeField] private CharacterClassData[] classes = new CharacterClassData[0];

        private readonly LobbyScreenView _view = new LobbyScreenView();
        private CharacterClassData _selected;

        public CharacterClassData Selected => _selected;

        private void Awake()
        {
            if (config != null) config.EnsureValid();
            GameServices.Register<LobbyController>(this);
        }

        private void OnDestroy() => GameServices.Unregister<LobbyController>(this);

        private void Start()
        {
            Time.timeScale = 1f;

            Canvas canvas = UiFactory.EnsureCanvas("Emberdelve_Lobby", 20);
            _view.Build(canvas, classes, HandleSelect, HandleStart, RunContext.LastResult, RunContext.HasResult);

            if (classes != null && classes.Length > 0) HandleSelect(classes[0]);

            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            GameEvents.RaiseRunState(RunState.Lobby);
        }

        private void HandleSelect(CharacterClassData classData)
        {
            if (classData == null) return;

            _selected = classData;
            RunContext.SelectClass(classData);
            _view.SetSelected(classData);
            GameEvents.RaiseToast($"{classData.displayName} selected");
        }

        private void HandleStart()
        {
            if (_selected == null)
            {
                GameEvents.RaiseToast("Choose a class first");
                return;
            }

            RunContext.SelectClass(_selected);
            SceneManager.LoadScene(config != null ? config.dungeonSceneName : Emberdelve.Core.GameConfig.DefaultDungeonSceneName);
        }
    }
}
