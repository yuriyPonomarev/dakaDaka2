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
            // U3A5: a process started as a network role from the command line (dedicated server, client, host) takes the scene the
            // server loads; this screen is the offline / pre-connection entry only. Offline never takes this branch.
            if (NetRole.IsNetworked)
            {
                Debug.Log("[Emberdelve][Lobby] skipped: process role is " + NetRole.Current);
                return;
            }

            Time.timeScale = 1f;

            Canvas canvas = UiFactory.EnsureCanvas("Emberdelve_Lobby", 20);
            _view.Build(canvas, classes, HandleSelect, HandleStart, RunContext.LastResult, RunContext.HasResult);

            if (classes != null && classes.Length > 0) HandleSelect(classes[0]);

            // U3A5: the minimal Online panel (Host / Join + address). It builds nothing when networking is not in the build.
            // Normally the lobby scene carries the component (EmberdelveLobbyBuilder); an older saved scene gets one here.
            LobbyOnlinePanel online = GetComponent<LobbyOnlinePanel>();
            if (online == null) online = gameObject.AddComponent<LobbyOnlinePanel>();
            online.Build(canvas, () => _selected);

            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            GameEvents.RaiseRunState(RunState.Lobby);
            Debug.Log("[Emberdelve][Lobby] ready classes=" + (classes != null ? classes.Length : 0) + " online=" + NetLobbyBridge.IsAvailable);
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

            // U3A5: while an online connection is starting or open, "Descend" would orphan it. Never true offline.
            if (NetLobbyBridge.Phase == NetLobbyPhase.Connecting || NetLobbyBridge.Phase == NetLobbyPhase.Connected)
            {
                GameEvents.RaiseToast("Cancel the online connection first");
                return;
            }

            RunContext.SelectClass(_selected);
            SceneManager.LoadScene(config != null ? config.dungeonSceneName : Emberdelve.Core.GameConfig.DefaultDungeonSceneName);
        }
    }
}
