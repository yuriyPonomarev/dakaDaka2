using UnityEngine;
using UnityEngine.SceneManagement;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Combat;
using Emberdelve.Interaction;
using Emberdelve.InputLayer;
using Emberdelve.Items;
using Emberdelve.Player;

namespace Emberdelve.Flow
{
    /// <summary>
    /// Owns one dungeon run: applies the chosen class to the player rig, tracks the run's facts,
    /// decides whether extraction is allowed, and turns death or a completed hold into a RunResult.
    /// It is also the loot host, so pickups and enemy drops go through one place.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class RunDirector : MonoBehaviour, IExtractionSink, ILootHost
    {
        [Header("Configuration")]
        [SerializeField] private GameConfig config;
        [SerializeField] private CharacterClassData fallbackClass;

        [Header("Scene wiring")]
        [SerializeField] private PlayerRig playerRig;

        private RunState _state = RunState.Briefing;
        private Inventory _inventory;
        private float _startedAt;
        private int _kills;
        private bool _paused;

        public RunState State => _state;
        public GameConfig Config => config;
        public Inventory Inventory => _inventory;
        public PlayerRig Player => playerRig;
        public int Kills => _kills;
        public int ItemsCollected => _inventory != null ? _inventory.LifetimeCollected : 0;
        public int LootValue => _inventory != null ? _inventory.RecoveredValue : 0;
        public float Elapsed => Mathf.Max(0f, Time.unscaledTime - _startedAt);

        private void Awake()
        {
            if (config != null) config.EnsureValid();
            GameServices.Register<RunDirector>(this);
        }

        private void Start()
        {
            if (playerRig == null)
            {
                Debug.LogError("[RunDirector] No PlayerRig assigned; the run cannot start.", this);
                return;
            }

            CharacterClassData classData = RunContext.GetSelectedClassOrDefault(fallbackClass);
            playerRig.ApplyClass(classData, config);

            _inventory = playerRig.Inventory;
            if (_inventory != null) _inventory.Initialize(playerRig);

            if (playerRig.Health != null) playerRig.Health.Died += HandlePlayerDeath;
            GameEvents.DamageDealt += HandleDamageDealt;

            _startedAt = Time.unscaledTime;
            SetState(RunState.Running);

            GameEvents.RaiseObjective(config != null ? config.objectiveText : "Survive and extract");
            GameEvents.RaiseStatusLine(classData != null ? classData.displayName : "Class");

            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        private void OnDestroy()
        {
            GameServices.Unregister<RunDirector>(this);
            GameEvents.DamageDealt -= HandleDamageDealt;

            if (playerRig != null && playerRig.Health != null) playerRig.Health.Died -= HandlePlayerDeath;

            Time.timeScale = 1f;
        }

        private void Update()
        {
            if (playerRig == null) return;

            EmberdelveInput input = playerRig.Input;
            if (input == null) return;

            if (input.PausePressed) TogglePause();
        }

        private void TogglePause()
        {
            if (_state != RunState.Running && _state != RunState.Paused) return;

            _paused = !_paused;
            Time.timeScale = _paused ? 0f : 1f;
            playerRig.SetInputEnabled(!_paused);
            SetState(_paused ? RunState.Paused : RunState.Running);
            GameEvents.RaisePause(_paused);

            Cursor.lockState = _paused ? CursorLockMode.None : CursorLockMode.Locked;
            Cursor.visible = _paused;
        }

        private void HandleDamageDealt(HitFeedback feedback)
        {
            if (feedback.TargetKind != EntityKind.Enemy || !feedback.TargetKilled) return;
            _kills++;
            GameEvents.RaiseStatusLine($"Slain: {_kills}");
        }

        private void HandlePlayerDeath()
        {
            Complete(RunState.Dead);
        }

        public bool ExtractionAllowed(out string reason)
        {
            if (_state != RunState.Running)
            {
                reason = "Run already finished";
                return false;
            }

            if (config != null && config.requireLootToExtract && LootValue <= 0)
            {
                reason = "Recover at least one relic first";
                return false;
            }

            reason = null;
            return true;
        }

        public void BeginExtraction()
        {
            Complete(RunState.Extracted);
        }

        private void Complete(RunState outcome)
        {
            if (_state != RunState.Running) return;

            SetState(outcome);
            _paused = false;
            Time.timeScale = 1f;

            if (playerRig != null) playerRig.SetInputEnabled(false);

            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            CharacterClassData classData = RunContext.SelectedClass;

            RunContext.CompleteRun(new RunResult(
                outcome,
                classData != null ? classData.displayName : "Class",
                LootValue,
                ItemsCollected,
                _kills,
                Elapsed));

            GameEvents.RaiseRunEnded(RunContext.LastResult);
        }

        private void SetState(RunState state)
        {
            _state = state;
            GameEvents.RaiseRunState(state);
        }

        public bool CollectLoot(ItemData item, int amount)
        {
            if (_inventory == null) return false;
            return _inventory.TryAdd(item, Mathf.Max(1, amount));
        }

        public void SpawnLoot(ItemData item, int amount, Vector3 worldPosition)
        {
            if (item == null) return;

            GameObject root = GameObject.CreatePrimitive(PrimitiveType.Cube);
            root.name = "Loot_" + item.displayName;
            root.transform.position = worldPosition;
            root.transform.localScale = new Vector3(
                Mathf.Max(0.12f, item.worldSize.x),
                Mathf.Max(0.12f, item.worldSize.y),
                Mathf.Max(0.12f, item.worldSize.z));

            int lootLayer = LayerMask.NameToLayer(GameConfig.LootLayerName);
            if (lootLayer >= 0) root.layer = lootLayer;

            BoxCollider box = root.GetComponent<BoxCollider>();
            if (box != null)
            {
                box.size = new Vector3(1.9f, 1.9f, 1.9f);
            }

            Renderer renderer = root.GetComponent<Renderer>();
            if (renderer != null && item.worldMaterial != null) renderer.material = item.worldMaterial;

            LootPickup pickup = root.AddComponent<LootPickup>();
            pickup.Configure(item, Mathf.Max(1, amount), this);
        }

        public void RestartRun()
        {
            Time.timeScale = 1f;
            SceneManager.LoadScene(config != null ? config.dungeonSceneName : SceneManager.GetActiveScene().name);
        }

        public void ReturnToLobby()
        {
            Time.timeScale = 1f;
            SceneManager.LoadScene(config != null ? config.lobbySceneName : "Lobby");
        }

        private void OnDrawGizmosSelected()
        {
            if (playerRig == null) return;
            Gizmos.color = Color.cyan;
            Gizmos.DrawWireSphere(playerRig.transform.position, 0.6f);
        }
    }
}
