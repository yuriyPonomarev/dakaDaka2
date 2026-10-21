using UnityEngine;
using UnityEngine.SceneManagement;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Combat;
using Emberdelve.Interaction;
using Emberdelve.InputLayer;
using Emberdelve.Items;
using Emberdelve.Level;
using Emberdelve.Player;

namespace Emberdelve.Flow
{
    /// <summary>
    /// Owns one dungeon run: applies the chosen class to the player rig, tracks the run's facts,
    /// decides whether extraction is allowed, and turns death or a completed hold into a RunResult.
    /// It is also the loot host, so pickups and enemy drops go through one place - and since U2B9 it is the
    /// only host that can receive a chest's already-rolled gear item, as that exact item.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class RunDirector : MonoBehaviour, IExtractionSink, ILootHost, ICountedLootHost, IInstanceLootHost
    {
        [Header("Configuration")]
        [SerializeField] private GameConfig config;
        [SerializeField] private CharacterClassData fallbackClass;
        [SerializeField] private DungeonLayoutData layout;

        [Header("Scene wiring")]
        [SerializeField] private PlayerRig playerRig;

        private RunState _state = RunState.Briefing;
        private Inventory _inventory;
        private float _startedAt;
        private int _kills;
        private bool _paused;

        public RunState State => _state;
        public GameConfig Config => config;
        public DungeonLayoutData Layout => layout;
        public Inventory Inventory => _inventory;
        public PlayerRig Player => playerRig;
        public int Kills => _kills;
        public int ItemsCollected => _inventory != null ? _inventory.LifetimeCollected : 0;
        public int LootValue => _inventory != null ? _inventory.RecoveredValue : 0;
        public float Elapsed => Mathf.Max(0f, Time.unscaledTime - _startedAt);

        /// <summary>Containers built from the layout asset, created once per run scene.</summary>
        public ChestSpawner Chests { get; private set; }

        /// <summary>True while the satchel screen has the pointer.</summary>
        public bool InventoryOpen { get; private set; }

        private void Awake()
        {
            if (config != null)
            {
                config.EnsureValid();

                // R3: the run's one item catalog is validated once, up front. An invalid catalog is a data error
                // that makes every gear mint refuse, so it is reported here rather than item by item.
                if (config.itemCatalog == null)
                    Debug.LogError("[RunDirector] GameConfig has no itemCatalog; gear cannot be minted.", this);
                else if (!config.itemCatalog.IsValid(out string catalogReason))
                    Debug.LogError("[RunDirector] Item catalog is invalid: " + catalogReason, this);
                else
                    ReportCatalogCoverage(config.itemCatalog);
            }

            GameServices.Register<RunDirector>(this);
        }

        /// <summary>
        /// R3: every item this run can hand out (chest tables and their affixes, floor loot, enemy drops, the
        /// class starting kits) must be an entry of the item catalog, otherwise a chest silently refuses to roll or
        /// a pickup is refused at the bag. One aggregated error names everything missing; nothing is changed.
        /// </summary>
        private void ReportCatalogCoverage(ItemCatalog catalog)
        {
            System.Collections.Generic.List<string> missing = new System.Collections.Generic.List<string>();

            void CheckItem(ItemData item, string where)
            {
                if (item != null && !catalog.Contains(item)) missing.Add($"{item.name} ({item.itemId}) in {where}");
            }

            void CheckClass(CharacterClassData data)
            {
                if (data == null || data.startingItems == null) return;
                for (int i = 0; i < data.startingItems.Length; i++) CheckItem(data.startingItems[i], "starting kit of " + data.name);
            }

            if (layout != null)
            {
                if (layout.chestSpawns != null)
                {
                    for (int i = 0; i < layout.chestSpawns.Count; i++)
                    {
                        LootTable table = layout.chestSpawns[i] != null ? layout.chestSpawns[i].lootTable : null;
                        if (table == null) continue;
                        if (table.entries != null)
                        {
                            for (int k = 0; k < table.entries.Length; k++)
                                if (table.entries[k] != null) CheckItem(table.entries[k].item, "loot table " + table.name);
                        }
                        if (table.affixCatalog != null)
                        {
                            for (int k = 0; k < table.affixCatalog.Length; k++)
                            {
                                AffixDefinition a = table.affixCatalog[k];
                                if (a != null && (!catalog.TryResolveAffix(a.id, out AffixDefinition owned, out _) || owned != a))
                                    missing.Add($"affix {a.id} in loot table {table.name}");
                            }
                        }
                    }
                }

                if (layout.lootSpawns != null)
                {
                    for (int i = 0; i < layout.lootSpawns.Count; i++)
                        if (layout.lootSpawns[i] != null) CheckItem(layout.lootSpawns[i].item, "floor loot");
                }

                if (layout.enemySpawns != null)
                {
                    for (int i = 0; i < layout.enemySpawns.Count; i++)
                    {
                        EnemyData enemy = layout.enemySpawns[i] != null ? layout.enemySpawns[i].enemy : null;
                        if (enemy == null || enemy.lootTable == null) continue;
                        for (int k = 0; k < enemy.lootTable.Length; k++) CheckItem(enemy.lootTable[k], "drops of " + enemy.name);
                    }
                }
            }

            CheckClass(fallbackClass);
            CheckClass(RunContext.SelectedClass);

            if (missing.Count > 0)
                Debug.LogError("[RunDirector] Items not in the item catalog (rebuild the data assets): " + string.Join("; ", missing), this);
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

            // Containers come from the layout asset through the run layer, because a chest hands its loot to the
            // run host and the run host is what decides whether the player can actually carry it.
            Chests = ChestSpawner.Ensure(layout, config, this);

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

        /// <summary>
        /// The satchel screen takes the pointer, so while it is open the player keeps their position and stops
        /// acting: no movement, no attacks, no skills. The world carries on, which is the cost of stopping to
        /// sort a bag in a hostile level. Pausing and the bag are separate states and neither cancels the other.
        /// </summary>
        public void SetInventoryOpen(bool open)
        {
            if (playerRig == null) return;

            InventoryOpen = open && _state == RunState.Running;

            bool controlAllowed = !InventoryOpen && !_paused;
            playerRig.SetInputEnabled(controlAllowed);

            if (!InventoryOpen && !_paused)
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }
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

        /// <summary>
        /// The authority for the extraction rule: re-validates here, whatever the caller already checked,
        /// and refuses with a reason instead of completing the run.
        /// </summary>
        public bool BeginExtraction(out string reason)
        {
            if (!ExtractionAllowed(out reason))
            {
                GameEvents.RaiseToast(reason);
                return false;
            }

            Complete(RunState.Extracted);
            return true;
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

        /// <summary>
        /// The yes/no answer a loot source used before counts existed: true only when the bag took every unit.
        /// It is now derived from the counted path, so the two can never disagree.
        /// </summary>
        public bool CollectLoot(ItemData item, int amount)
        {
            return CollectLootCounted(item, amount) == Mathf.Max(1, amount);
        }

        /// <summary>
        /// The counted loot path (U1C2H): the run host hands the offer to the bag's own add algorithm and
        /// reports back the exact units that committed, so a chest or a ground pickup can shrink itself by
        /// that much instead of guessing from a bool. No placement rule lives here, and no unit is credited
        /// twice: the bag is what counts, and RunDirector reads its loot figures straight off the bag.
        /// </summary>
        public int CollectLootCounted(ItemData item, int amount)
        {
            if (_inventory == null || item == null) return 0;

            int requested = Mathf.Max(1, amount);
            _inventory.TryAdd(item, requested, out _, out int accepted);
            return Mathf.Clamp(accepted, 0, requested);
        }

        /// <summary>
        /// The instance half of the loot contract (U2B9): a chest that rolled a gear item hands over that very
        /// <see cref="ItemInstance"/>, and this is the only path that accepts it. It goes straight to the bag's
        /// own instance placement, so the accepted item is the same object - same UID, same rarity, same accepted
        /// affix records, quantity 1 - with no mint, no copy and no reroll, and it is credited exactly once
        /// because the bag counts it once.
        ///
        /// A run that is not in a state to receive loot refuses it: a run that has not started, one that is
        /// already dead or extracted, a player who is dead, and a player with no bag at all all leave the item
        /// where it was. A refusal changes nothing on either side, which is what lets the chest keep the
        /// identical item for the next open.
        /// </summary>
        public bool TryCollectLootInstance(ItemInstance instance, out string reason)
        {
            reason = null;

            if (instance == null)
            {
                reason = "no item instance to collect";
                return false;
            }

            if (_state != RunState.Running)
            {
                reason = $"the run is {_state}: {instance.Definition.displayName} [{instance.UidText}] is not " +
                         "loot a finished or unstarted run can take";
                return false;
            }

            if (playerRig == null || _inventory == null)
            {
                reason = $"no player bag to carry {instance.Definition.displayName} [{instance.UidText}]";
                return false;
            }

            if (playerRig.IsDead)
            {
                reason = $"the player is dead: {instance.Definition.displayName} [{instance.UidText}] stays " +
                         "where it is";
                return false;
            }

            // The bag's own rule, read unchanged: one identity is one item, so a UID this player already
            // carries or already wears is refused rather than granted a second time.
            return _inventory.TryAddInstance(instance, out reason);
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
