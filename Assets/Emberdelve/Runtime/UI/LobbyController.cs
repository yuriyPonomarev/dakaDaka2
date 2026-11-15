using UnityEngine;
using UnityEngine.SceneManagement;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Net.Rules;
using Emberdelve.Level;
using Emberdelve.UI;
using Emberdelve.Rules;

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
            RunContext.SelectRoyaleMode(RunContext.SelectedRoyaleMode ?? RoyalePartyMode.Solo);
            _view.Build(canvas, classes, HandleSelect, HandleStart, RunContext.LastResult, RunContext.HasResult, HandleMode);

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

        private void HandleMode(RoyalePartyMode mode)
        {
            if (NetLobbyBridge.Phase == NetLobbyPhase.Connecting || NetLobbyBridge.Phase == NetLobbyPhase.Connected) return;
            RunContext.SelectRoyaleMode(mode); _view.SetMode(mode);
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
            RaidPreparationScreen preparation = GetComponent<RaidPreparationScreen>();
            if (preparation == null) preparation = gameObject.AddComponent<RaidPreparationScreen>();
            preparation.Open(config, _selected, StartOfflineRun);
        }

        private void StartOfflineRun()
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

            const string modularScenePath = "Assets/Emberdelve/Scenes/Emberdelve_ModularCrypts.unity";
            if (SceneUtility.GetBuildIndexByScenePath(modularScenePath) < 0)
            {
                GameEvents.RaiseToast("The crypt map is unavailable in this build. Rebuild with Emberdelve_ModularCrypts included.");
                Debug.LogError("[Emberdelve][Lobby] cannot start raid: required modular scene is missing from Build Settings");
                return;
            }

            DungeonModuleCatalog catalog = Resources.Load<DungeonModuleCatalog>("Emberdelve/DungeonModuleCatalog");
            if (catalog == null || catalog.Modules == null || catalog.Modules.Length == 0)
            {
                GameEvents.RaiseToast("The required modular crypt catalog is missing. No raid was started.");
                Debug.LogError("[Emberdelve][Lobby] cannot start raid: required Resources/Emberdelve/DungeonModuleCatalog is missing or empty");
                return;
            }

            RunContext.SelectClass(_selected);
            RunContext.SelectRoyaleMode(RunContext.SelectedRoyaleMode ?? RoyalePartyMode.Solo);
            RunContext.ClearNextRaidSeed();
            if (!Emberdelve.Items.OfflineProfileSession.Depart(out string reason))
            {
                GameEvents.RaiseToast(reason);
                GetComponent<RaidPreparationScreen>()?.ShowFailure(reason);
                return;
            }
            Debug.Log("[Emberdelve][Lobby] starting Squire Royale offline mode=" + RunContext.SelectedRoyaleMode);
            try { SceneManager.LoadScene(NetTuning.ModularDungeonSceneName); }
            catch (System.Exception)
            {
                Emberdelve.Items.OfflineProfileSession.CancelDeparture(out _);
                throw;
            }
        }
    }
}
