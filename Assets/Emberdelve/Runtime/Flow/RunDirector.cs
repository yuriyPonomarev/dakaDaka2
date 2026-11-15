using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Combat;
using Emberdelve.Interaction;
using Emberdelve.InputLayer;
using Emberdelve.Items;
using Emberdelve.Level;
using Emberdelve.AI;
using Emberdelve.Player;
using Emberdelve.Rules;

namespace Emberdelve.Flow
{
    /// <summary>
    /// R2/R7: what the run knows about ONE player: identity, party, the rig and bag it acts through, and its own run state
    /// (running, dead or extracted), kills, clock and result. The run director keeps one per admitted rig, keyed by player
    /// id; nothing about a run lives in a single "the player" field.
    /// </summary>
    public sealed class PlayerSession
    {
        public int PlayerId;
        public int PartyId;
        public PlayerRig Rig;
        public CharacterClassData Class;
        public Inventory Inventory;
        public bool IsLocal;
        public bool IsBot { get; internal set; }
        internal bool AwaitingInitialFloor;
        public RunState State = RunState.Running;
        public int Kills;
        public float JoinedAt;
        public float EndedAt;
        public bool HasResult;
        public RunResult Result;
        public int Floor { get; internal set; } = 1;
        internal bool DeathCustodyPending;
        internal float NextDeathCustodyAttemptAt;
        internal bool ExtractionSettlementPending;
        internal System.Action DiedHandler;

        public bool IsRunning => State == RunState.Running && !ExtractionSettlementPending
            && (Rig == null || !Rig.IsInFloorTransit);
    }

    /// <summary>
    /// Owns one dungeon run: admits players (a party each), applies the chosen class to their rigs, tracks every
    /// player's own facts (carried value, kills, extraction state), decides per actor whether extraction is allowed, and turns
    /// a death or a completed hold into that player's RunResult. The run itself ends when no player is still in it. It is also
    /// the loot host, so pickups and enemy drops go through one place - and since U2B9 it is the only host that can receive a
    /// chest's already-rolled gear item, as that exact item. Since R2 every host call names its actor: loot goes to THAT
    /// player's bag, and a call from an entity that is not a running player of this run is refused.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class RunDirector : MonoBehaviour, IExtractionSink, ILootHost, ICountedLootHost, IInstanceLootHost,
        IEnemyDropHost, IRaidSessionHost, ILocalPlayerSource, IExtractionFloorSource, IInteractionRulesSource, IInteractionAdmissionSource, IGroundItemSink, IGroundStackSink
    {
        [Header("Configuration")]
        [SerializeField] private GameConfig config;
        [SerializeField] private CharacterClassData fallbackClass;
        [SerializeField] private DungeonLayoutData layout;

        [Header("Scene wiring")]
        [Tooltip("The locally controlled player's rig (party 0). More players join through TryJoin / SpawnPlayer.")]
        [SerializeField] private PlayerRig playerRig;

        [Header("Network (U3A2)")]
        [Tooltip("The run is hosted for networked players: Start does not join the scene rig (players come from TrySpawnNetworkPlayer) and nothing here touches the pointer. Also implied by NetRole DedicatedServer / Host. Off for the offline game.")]
        [SerializeField] private bool serverMode;
        [Tooltip("Classes a network client may ask for by id (the server's approved catalog). The fallback class is always approved.")]
        [SerializeField] private CharacterClassData[] approvedClasses;

        private RunState _state = RunState.Briefing;
        private float _startedAt;
        private float _offlinePausedDuration;
        private float _offlinePauseStartedAt;
        private bool _raidExpired;
#if DEVELOPMENT_BUILD || UNITY_EDITOR
        private bool _proofRaidCountdownEnabled;
        private int _proofRaidDurationSeconds;
#endif
        private bool _paused;
        private bool _started;
        private bool _startInvoked;
        private bool _runtimeLayoutRequiredBeforeStart;
        private bool _runtimeLayoutInstalled;
        private bool _runtimeLayoutInstallationComplete;
        private bool _startDeferredForRuntimeLayout;
        private readonly List<PlayerSession> _sessions = new List<PlayerSession>();
        private readonly Dictionary<int, PlayerSession> _byPlayerId = new Dictionary<int, PlayerSession>();
        private PlayerSession _local;
        private int _spawnedCount;
        private int _networkSpawnCount;
        private string _runtimeDungeonSceneName;
        private PlayerSession _pendingExtraction;
        private float _nextExtractionSettlementAttemptAt;
        public bool ExtractionSettlementPending => _pendingExtraction != null;
        public RoyalePartyMode? RoyaleMode { get; private set; }
        private RoyaleFloorCoordinator _floorCoordinator;
        public RoyaleFloorCoordinator FloorCoordinator => _floorCoordinator ?? (_floorCoordinator = new RoyaleFloorCoordinator(this));
        private readonly float[] _floorStartedAt = new float[4];
        private readonly int[] _floorDuration = new int[4];
        private readonly bool[] _floorExpired = new bool[4];

        // The coordinator starts each clock once, after preparing that floor's population and map.
        public bool TryStartRoyaleFloor(int floor, int durationSeconds, out string reason)
        {
            reason = "Floor cannot start";
            if (NetRole.IsClientOnly || !RoyaleMode.HasValue || _state != RunState.Running
                || floor < 1 || floor > 3 || durationSeconds <= 0 || _floorDuration[floor] != 0
                || (floor > 1 && _floorDuration[floor - 1] == 0)) return false;
            _floorStartedAt[floor] = Elapsed;
            _floorDuration[floor] = durationSeconds;
            reason = null;
            return true;
        }

        public bool TryGetRoyaleFloorClock(int floor, out float elapsed, out float remaining)
        {
            elapsed = remaining = 0f;
            if (!RoyaleMode.HasValue || floor < 1 || floor > 3 || _floorDuration[floor] == 0) return false;
            elapsed = Mathf.Max(0f, Elapsed - _floorStartedAt[floor]);
            remaining = Mathf.Max(0f, _floorDuration[floor] - elapsed);
            return true;
        }

        public bool IsRoyaleFloorOpen(int floor) => TryGetRoyaleFloorClock(floor, out _, out float remaining)
            && remaining > 0f && !_floorExpired[floor];

        private void TickRoyaleFloors()
        {
            if (NetRole.IsClientOnly || !RoyaleMode.HasValue || _state != RunState.Running) return;
            for (int floor = 1; floor <= 3; floor++)
            {
                if (_floorExpired[floor] || !TryGetRoyaleFloorClock(floor, out _, out float remaining) || remaining > 0f) continue;
                _floorExpired[floor] = true;
                ExpirePlayersOnFloor(floor);
            }
        }

        public bool ConfigureRoyale(RoyalePartyMode mode)
        {
            if (_started || _floorDuration[1] != 0 || NetRole.IsClientOnly || mode < RoyalePartyMode.Solo || mode > RoyalePartyMode.Trio) return false;
            RoyaleMode = mode;
            SetPartySizeOverride((int)mode);
            return true;
        }

        /// <summary>Called only after the authority validates the specific portal, its wave and remaining slots.</summary>
        public bool TryQueueDescent(IInteractionActor actor, int sourceFloor, out string reason)
        {
            reason = "Descent is not available";
            if (NetRole.IsClientOnly || !RoyaleMode.HasValue || sourceFloor < 1 || sourceFloor >= 3) return false;
            if (!IsInteractionActorAdmitted(actor, out reason)) return false;
            PlayerSession session = SessionOf(actor);
            if (session.Floor != sourceFloor) { reason = "Portal belongs to a different floor"; return false; }
            if (!session.Rig.TryBeginFloorTransit(out reason)) return false;
            GameEvents.RaiseStatusLine(session.PlayerId, "Descending to floor " + (sourceFloor + 1));
            return true;
        }

        /// <summary>Destination authority commits an already prepared spawn. No admission/class/kit initialization.
        /// The coordinator must prepare the destination map and content before calling this.</summary>
        public bool TryArriveAtNextFloor(IInteractionActor actor, int destinationFloor,
            Vector3 position, Quaternion rotation, out string reason)
        {
            reason = "No pending floor transfer";
            if (NetRole.IsClientOnly || !RoyaleMode.HasValue) return false;
            PlayerSession session = SessionOf(actor);
            if (session == null || session.State != RunState.Running || !session.Rig.IsInFloorTransit
                || (destinationFloor != session.Floor + 1 && !(session.AwaitingInitialFloor && destinationFloor == session.Floor)) || destinationFloor > 3) return false;
            if (!IsRoyaleFloorOpen(destinationFloor)) { reason = "Destination floor is not open"; return false; }
            if (!session.Rig.TryEndFloorTransit(position, rotation, out reason)) return false;
            session.Floor = destinationFloor;
            session.AwaitingInitialFloor = false;
            GameEvents.RaiseStatusLine(session.PlayerId, "Floor " + destinationFloor);
            return true;
        }

        /// <summary>The run-wide state: Briefing, Running, Paused, and the final outcome once no player is left in the run.</summary>
        public RunState State => _state;
        public bool IsStarted => _started;
        public GameConfig Config => config;
        public DungeonLayoutData Layout => layout;
        public bool RuntimeLayoutInstallationComplete => _runtimeLayoutInstallationComplete;

        /// <summary>Every admitted player, in join order.</summary>
        public IReadOnlyList<PlayerSession> Players => _sessions;

        /// <summary>
        /// The locally controlled player's session (presentation binds to it: HUD, bag screen, feedback). It is NOT consulted by any
        /// gameplay rule: interactions, loot, extraction and combat all name their actor.
        /// </summary>
        public PlayerSession LocalSession => _local;
        public int LocalPlayerId => _local != null ? _local.PlayerId : (!IsServerMode && playerRig != null ? playerRig.PlayerId : GameEvents.NoPlayer);

        /// <summary>The locally controlled rig (presentation only). Null on a server with no local player.</summary>
        public PlayerRig Player => _local != null ? _local.Rig : (IsServerMode ? null : playerRig);

        /// <summary>
        /// U3A2: this process hosts the run for networked players (dedicated server or host): the scene rig is not
        /// joined, players enter through <see cref="TrySpawnNetworkPlayer(GameObject, string, out PlayerRig, out string)"/>, and
        /// <see cref="Player"/> is whatever local player (host) was admitted, else null. False for the offline game.
        /// </summary>
        public bool IsServerMode =>
            serverMode || NetRole.Current == NetRoleKind.DedicatedServer || NetRole.Current == NetRoleKind.Host;

        /// <summary>Marks the run as server hosted. Only meaningful before Start (tools and gates).</summary>
        public void SetServerMode(bool enabled) => serverMode = enabled;

        /// <summary>Installs a per-run generated layout before Start; used by the modular scene assembler only.</summary>
        public bool SetRuntimeLayout(DungeonLayoutData runtimeLayout)
        {
            if (_started || runtimeLayout == null) return false;
            layout = runtimeLayout;
            _runtimeLayoutInstalled = true;
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            Debug.Log("[modular-proof] runtime layout assigned before run start layout=" + runtimeLayout.name, this);
#endif
            return true;
        }

        /// <summary>Explicitly gates this scene's run until its modular assembler commits the authoritative layout.</summary>
        public bool RequireRuntimeLayoutBeforeStart()
        {
            if (_startInvoked || _started) return false;
            _runtimeLayoutRequiredBeforeStart = true;
            return true;
        }

        /// <summary>Marks all scene geometry/gameplay placement checks complete, then releases a deferred Start.</summary>
        public bool CompleteRuntimeLayoutInstallation()
        {
            if (!_runtimeLayoutRequiredBeforeStart || !_runtimeLayoutInstalled || layout == null || _started) return false;
            _runtimeLayoutInstallationComplete = true;
            if (_startDeferredForRuntimeLayout)
            {
                _startDeferredForRuntimeLayout = false;
                BeginRun();
            }
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            Debug.Log("[modular-proof] runtime layout installation complete runStarted=" + _started + " layout=" + layout.name, this);
#endif
            return true;
        }

        public bool SetRuntimeDungeonSceneName(string sceneName)
        {
            if (_started || string.IsNullOrWhiteSpace(sceneName)) return false;
            _runtimeDungeonSceneName = sceneName;
            return true;
        }

#if DEVELOPMENT_BUILD || UNITY_EDITOR
        public bool AdvanceRoyaleFloorForProof(int floor, float elapsed)
        {
            if (!IsServerMode || !_started || !RoyaleMode.HasValue || !float.IsFinite(elapsed)
                || floor < 1 || floor > 3 || _floorDuration[floor] == 0 || elapsed > _floorDuration[floor]
                || !TryGetRoyaleFloorClock(floor, out float current, out _) || elapsed < current) return false;
            _floorStartedAt[floor] = Elapsed - elapsed; return true;
        }

        /// <summary>Applies a process-local raid clock for a development proof without mutating the shared GameConfig asset.</summary>
        public bool ConfigureRaidCountdownForProof(int durationSeconds)
        {
            if (RoyaleMode.HasValue || !IsServerMode || !_started || durationSeconds <= 0 || _raidExpired) return false;
            _proofRaidDurationSeconds = durationSeconds;
            _proofRaidCountdownEnabled = true;
            _startedAt = Time.unscaledTime;
            _offlinePausedDuration = 0f;
            _offlinePauseStartedAt = 0f;
            return true;
        }
#endif

        /// <summary>
        /// Restart and return-to-lobby load a scene, which would unload a network-synchronised one. Only the offline game
        /// (NetRole Offline) may do it; online both calls are ignored until the network lifecycle (U3A5) owns them.
        /// </summary>
        public bool SceneFlowAllowed => NetRole.IsOffline && !ExtractionSettlementPending;

        /// <summary>The local player's own run state (Running until they die or extract), not the run's.</summary>
        public RunState LocalPlayerState => _local != null ? _local.State : _state;

        /// <summary>The local player's bag (presentation only).</summary>
        public Inventory Inventory => _local != null ? _local.Inventory : null;
        public int Kills => _local != null ? _local.Kills : 0;
        public int ItemsCollected => _local != null && _local.Inventory != null ? _local.Inventory.LifetimeCollected : 0;
        public int LootValue => _local != null && _local.Inventory != null ? _local.Inventory.RecoveredValue : 0;
        public float Elapsed => RaidCountdownRules.ElapsedSeconds(
            _startedAt,
            Time.unscaledTime,
            _offlinePausedDuration,
            _offlinePauseStartedAt,
            _paused && NetRole.IsOffline);
        public bool RaidCountdownEnabled =>
            RoyaleMode.HasValue ||
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            _proofRaidCountdownEnabled ||
#endif
            config != null && config.enableRaidCountdown;
        public float RaidSecondsRemaining => RoyaleMode.HasValue
            ? (TryGetRoyaleFloorClock(_local != null ? _local.Floor : 1, out _, out float remaining) ? remaining : 0f)
            : RaidCountdownEnabled
            ? RaidCountdownRules.RemainingSeconds(RaidDurationSeconds, Elapsed)
            : RaidDurationSeconds;
        public bool RaidExpired => RoyaleMode.HasValue
            ? TryGetRoyaleFloorClock(_local != null ? _local.Floor : 1, out _, out float remaining) && remaining <= 0f
            : _raidExpired;

        public PlayerRaidClock ClockFor(IInteractionActor actor)
        {
            if (!RoyaleMode.HasValue)
                return new PlayerRaidClock(RaidCountdownEnabled, RaidCountdownEnabled ? Mathf.Max(0, RaidSecondsRemaining) : 0,
                    RaidCountdownEnabled && RaidExpired);
            var session = SessionOf(actor);
            if (session?.Rig == null) return default;
            bool held = session.Rig.IsInFloorTransit;
            int floor = held && !session.AwaitingInitialFloor ? Mathf.Min(3, session.Floor + 1) : session.Floor;
            bool started = TryGetRoyaleFloorClock(floor, out _, out float remaining);
            bool waiting = held || !started;
            return new PlayerRaidClock(!waiting, waiting ? 0 : remaining, !waiting && remaining <= 0,
                (byte)floor, waiting, session.Rig.FloorMovementGeneration);
        }

        private int RaidDurationSeconds => RaidCountdownRules.NormalizeDurationSeconds(
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            _proofRaidCountdownEnabled ? _proofRaidDurationSeconds :
#endif
            config != null ? config.raidDurationSeconds : RaidCountdownRules.DefaultDurationSeconds);

        private bool RaidDeadlineReached => !RoyaleMode.HasValue && RaidCountdownEnabled &&
            !RaidCountdownRules.IsExtractionWindowOpen(RaidDurationSeconds, Elapsed, !NetRole.IsClientOnly);

        public float InteractionReach => config != null ? Mathf.Max(0.5f, config.interactionAuthorityRange) : InteractionRules.DefaultReach;

        public bool TryGetSession(int playerId, out PlayerSession session) => _byPlayerId.TryGetValue(playerId, out session);

        /// <summary>The session of the actor an interaction names, or null when it is not a player of this run.</summary>
        public PlayerSession SessionOf(IInteractionActor actor)
        {
            if (actor == null || !_byPlayerId.TryGetValue(actor.PlayerId, out PlayerSession session)) return null;
            // PlayerId is only a lookup key, not proof of ownership. A client-supplied implementation could
            // otherwise borrow a live player's id while claiming a different origin, alive flag, or inventory.
            return session != null && object.ReferenceEquals(session.Rig, actor) ? session : null;
        }

        public bool IsInteractionActorAdmitted(IInteractionActor actor, out string reason)
        {
            PlayerSession session = SessionOf(actor);
            if (session == null)
            {
                reason = "Not a player of this run";
                return false;
            }
            if (actor == null || !actor.IsAlive)
            {
                reason = "The dead cannot interact";
                return false;
            }
            if (RoyaleMode.HasValue && !IsRoyaleFloorOpen(session.Floor))
            {
                reason = "Floor is not open";
                return false;
            }
            if (_state != RunState.Running || !session.IsRunning)
            {
                reason = "Run already finished";
                return false;
            }

            reason = null;
            return true;
        }

        /// <summary>
        /// R4/R5: this raid's seed and its one item-identity issuer. Created in Awake, once per run (a retry loads
        /// the scene again and gets a new session), and shared by chests, enemy drops, the starting kit and pickups.
        /// </summary>
        public RaidSession Session { get; private set; }

        /// <summary>Containers built from the layout asset, created once per run scene.</summary>
        public ChestSpawner Chests { get; private set; }

        /// <summary>True while the satchel screen has the pointer.</summary>
        public bool InventoryOpen { get; private set; }

        private void Awake()
        {
            ulong raidSeed = RunContext.ResolveRaidSeed(out string seedSource);
            Session = RaidSession.Begin(raidSeed, seedSource);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            // The raid seed derives every loot outcome of the raid: it is never written to a release log.
            Debug.Log($"[Emberdelve][Raid] {Session}");
#endif
            EnemyAgent.ResetDropKeys();

            if (config != null)
            {
                config.EnsureValid();

                // R7: the hostility switch is per-run state, set from data when the run starts and cleared with the registry.
                CombatTeams.FriendlyFire = config.friendlyFire;

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
            GameServices.Register<ILocalPlayerSource>(this);
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

            void CheckTable(LootTable table, string where)
            {
                if (table == null) return;
                if (table.entries != null)
                {
                    for (int k = 0; k < table.entries.Length; k++)
                        if (table.entries[k] != null) CheckItem(table.entries[k].item, where);
                }
                if (table.affixCatalog != null)
                {
                    for (int k = 0; k < table.affixCatalog.Length; k++)
                    {
                        AffixDefinition a = table.affixCatalog[k];
                        if (a != null && (!catalog.TryResolveAffix(a.id, out AffixDefinition owned, out _) || owned != a))
                            missing.Add($"affix {a.id} in {where}");
                    }
                }
                System.Collections.Generic.List<string> problems = table.Validate();
                for (int k = 0; k < problems.Count; k++) missing.Add("invalid table: " + problems[k]);
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
                        CheckTable(table, "loot table " + (table != null ? table.name : ""));
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
                        if (enemy == null) continue;

                        // R4: the runtime rolls enemy.dropTable (a real LootTable, validated like a chest table). The
                        // legacy flat list is still checked so a stale asset cannot hide a non-catalog item.
                        CheckTable(enemy.dropTable, "enemy drop table of " + enemy.name);
                        if (enemy.lootTable != null)
                        {
                            for (int k = 0; k < enemy.lootTable.Length; k++) CheckItem(enemy.lootTable[k], "legacy drops of " + enemy.name);
                        }
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
            _startInvoked = true;
            if (SceneManager.GetActiveScene().name == GameConfig.DefaultDungeonSceneName && !_runtimeLayoutRequiredBeforeStart)
            {
                Debug.LogError("[RunDirector] Modular Crypts scene has no active module-plan assembler; gameplay remains fail-closed.", this);
                return;
            }
            if (_runtimeLayoutRequiredBeforeStart && !_runtimeLayoutInstallationComplete)
            {
                _startDeferredForRuntimeLayout = true;
#if DEVELOPMENT_BUILD || UNITY_EDITOR
                Debug.Log("[modular-proof] RunDirector.Start deferred until modular plan commit", this);
#endif
                return;
            }
            BeginRun();
        }

        private void BeginRun()
        {
            if (_started) return;
            if (RoyaleMode.HasValue)
            {
                Chests = ChestSpawner.Ensure(layout, config, this);
                var initialMap = FindFirstObjectByType<DungeonModuleSceneAssembler>(FindObjectsInactive.Include);
                if (!FloorCoordinator.TryAdoptInitialMap(initialMap, out string preparationReason))
                { Debug.LogError("[RunDirector] Initial Royale map is not ready: " + preparationReason, this); return; }
            }
            if (IsServerMode)
            {
                StartServerRun();
                return;
            }

            if (playerRig == null)
            {
                Debug.LogError("[RunDirector] No PlayerRig assigned; the run cannot start.", this);
                return;
            }

            // The scene's own rig is the locally controlled player of party 0. Further players join through TryJoin.
            if (!TryJoin(playerRig, 0, true, out string joinReason))
            {
                Debug.LogError("[RunDirector] The scene player could not join the run: " + joinReason, this);
                return;
            }

            CharacterClassData classData = _local != null ? _local.Class : ClassFor(playerRig);

            _startedAt = Time.unscaledTime;
            _started = true;
            GameEvents.DamageDealt += HandleDamageDealt;
            SetState(RunState.Running);

            // Containers come from the layout asset through the run layer, because a chest hands its loot to the
            // run host and the run host is what decides whether the player can actually carry it.
            Chests = ChestSpawner.Ensure(layout, config, this);

            GameEvents.RaiseObjective(config != null ? config.objectiveText : "Survive and extract");
            GameEvents.RaiseStatusLine(playerRig.PlayerId, classData != null ? classData.displayName : "Class");

            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            RecordModularOfflineRunProof();
#endif
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private void RecordModularOfflineRunProof()
        {
            if (IsServerMode || !_runtimeLayoutRequiredBeforeStart || !_started) return;
            DungeonModulePlan plan = DungeonModulePlanSession.Current;
            DungeonModuleSceneAssembler assembler = FindFirstObjectByType<DungeonModuleSceneAssembler>(FindObjectsInactive.Include);
            bool layoutInstalled = _runtimeLayoutInstallationComplete && layout != null && plan != null;
            bool assemblyValidated = assembler != null && assembler.RuntimeMapValidationComplete &&
                                     assembler.AppliedLayoutHash == plan?.LayoutHash && assembler.RuntimeLayout == layout;
            ModularOfflineRunProof.RecordStartedRun(Session != null ? Session.RaidSeed : 0UL,
                plan != null ? plan.Seed : 0, plan != null ? plan.LayoutHash : string.Empty,
                _started, layoutInstalled, assemblyValidated);
        }
#endif

        /// <summary>
        /// U3A2: the hosted run starts with nobody in it. No scene rig joins, the pointer and the pause clock are left
        /// alone, and an empty run stays Running (late join is allowed): only a player's own death or extraction can end
        /// it, never the absence of players.
        /// </summary>
        private void StartServerRun()
        {
            _startedAt = Time.unscaledTime;
            _started = true;
            GameEvents.DamageDealt += HandleDamageDealt;
            SetState(RunState.Running);

            Chests = ChestSpawner.Ensure(layout, config, this);
            GameEvents.RaiseObjective(config != null ? config.objectiveText : "Survive and extract");
            Debug.Log("[Emberdelve][RunDirector] server mode: running with 0 players (role " + NetRole.Current + ")");
        }

        private CharacterClassData ClassFor(PlayerRig rig) => RunContext.GetSelectedClassOrDefault(fallbackClass);

        /// <summary>
        /// Resolves a class id string a network client asked for against the server's approved classes (case-insensitive).
        /// The client only ever sends the id; the class data, and with it every number, is the server's.
        /// </summary>
        public bool TryResolveClass(string classId, out CharacterClassData data)
        {
            data = null;
            if (string.IsNullOrWhiteSpace(classId)) return false;

            if (approvedClasses != null)
            {
                for (int i = 0; i < approvedClasses.Length; i++)
                {
                    CharacterClassData candidate = approvedClasses[i];
                    if (candidate != null && string.Equals(candidate.classId, classId, System.StringComparison.OrdinalIgnoreCase))
                    {
                        data = candidate;
                        return true;
                    }
                }
            }

            if (fallbackClass != null && string.Equals(fallbackClass.classId, classId, System.StringComparison.OrdinalIgnoreCase))
            {
                data = fallbackClass;
                return true;
            }

            return false;
        }

        /// <summary>
        /// R7: admits a rig to the run as one player: assigns its party (the requested one, or the first party with room when
        /// <paramref name="requestedParty"/> is negative), applies the class with the raid's issuer, hooks its death, and records its
        /// session. Refuses a duplicate rig, a full party (<c>GameConfig.maxPartySize</c>, at most 3) and a run that already ended.
        /// </summary>
        public bool TryJoin(PlayerRig rig, int requestedParty, bool locallyControlled, out string reason)
        {
            return TryJoin(rig, requestedParty, locallyControlled, ClassFor(rig), out reason);
        }

        /// <summary>Admits a rig with the trusted host-selected class for that player.</summary>
        public bool TryJoin(PlayerRig rig, int requestedParty, bool locallyControlled,
            CharacterClassData selectedClass, out string reason)
        {
            return TryJoinCore(rig, requestedParty, locallyControlled, selectedClass, 1, false, out reason);
        }

        private bool TryJoinCore(PlayerRig rig, int requestedParty, bool locallyControlled,
            CharacterClassData selectedClass, int floor, bool bot, out string reason)
        {
            reason = null;
            if (!bot && (_raidExpired || (_started && RaidDeadlineReached)))
            {
                reason = "the raid timer has expired";
                return false;
            }
            if (rig == null) { reason = "no rig"; return false; }
            if (selectedClass == null) { reason = "no class selected"; return false; }
            if (_state == RunState.Dead || _state == RunState.Extracted) { reason = "the run already ended"; return false; }
            if (_byPlayerId.ContainsKey(rig.PlayerId)) { reason = "this player is already in the run"; return false; }

            int party = requestedParty;
            // Only the floor coordinator can call the bot factory after validating destination capacity.
            if (!bot && !TryResolveParty(requestedParty, out party, out reason)) return false;
            if (!bot && RoyaleMode.HasValue && !FloorCoordinator.CanAdmitInitialParty(party, out reason)) return false;

            // R1c1: the admitted player owns its rig's combat id as a command sender (sender id = the player id this
            // session is keyed by). Bound before anything else about the rig changes; a refusal leaves it untouched.
            if (!CombatSenders.Bind(CombatSenderId.ForPlayer(rig.PlayerId), rig.CombatId, out reason)) return false;

            rig.AssignParty(party, locallyControlled);

            // R4: the bag mints its starting kit from the raid's one issuer, not from a private one.
            if (rig.Inventory != null) rig.Inventory.SetIssuer(Session.Issuer);

            rig.ApplyClass(selectedClass, config);

            PlayerSession session = new PlayerSession
            {
                PlayerId = rig.PlayerId,
                PartyId = party,
                Rig = rig,
                Class = selectedClass,
                Inventory = rig.Inventory,
                IsLocal = locallyControlled,
                IsBot = bot,
                Floor = floor,
                AwaitingInitialFloor = bot,
                JoinedAt = Time.unscaledTime
            };

            if (session.Inventory != null) session.Inventory.Initialize(rig);

            if (rig.Health != null)
            {
                session.DiedHandler = () => HandlePlayerDeath(session);
                rig.Health.Died += session.DiedHandler;
            }

            _sessions.Add(session);
            _byPlayerId[session.PlayerId] = session;
            if (!bot && RoyaleMode.HasValue && !FloorCoordinator.TryHoldInitialPlayer(rig, out reason))
            {
                RemovePlayer(session.PlayerId, "Initial roster reservation failed");
                return false;
            }
            if (locallyControlled && _local == null)
            {
                _local = session;
                playerRig = rig;
                // A host joins after the server run has started. Its class/kit events fired
                // before LocalPlayerId was bound, so presentation must now seed from the live rig.
                if (_started) GameEvents.RaisePlayerRunState(session.PlayerId, session.State);
            }

            if (_started) GameEvents.RaiseStatusLine(session.PlayerId, selectedClass.displayName);
            return true;
        }

        /// <summary>The party a joining player gets: the requested one when it has room, the first party with room for a negative request.</summary>
        private bool TryResolveParty(int requestedParty, out int party, out string reason)
        {
            reason = null;
            int cap = RoyaleMode.HasValue ? (int)RoyaleMode.Value
                : config != null ? Mathf.Clamp(config.maxPartySize, 1, 3) : CombatTeams.DefaultMaxPartySize;
            if (_partySizeOverride > 0) cap = Mathf.Min(cap, _partySizeOverride); // U3B3 proof switch (-ember-net-party-size)
            party = requestedParty;
            if (party < 0)
            {
                party = 0;
                while (PartySize(party) >= cap) party++;
                return true;
            }

            if (PartySize(party) >= cap)
            {
                reason = $"party {party} is full ({cap} players)";
                return false;
            }

            return true;
        }

        private int _partySizeOverride;

        /// <summary>
        /// U3B3 proof switch, server only (NetServer, from -ember-net-party-size): lowers the largest party. 1 puts every joining player in a
        /// party of their own, so two networked players are hostile to each other. Never raises the authored cap; 0 removes the override.
        /// The offline game never calls it.
        /// </summary>
        public void SetPartySizeOverride(int size)
        {
            _partySizeOverride = Mathf.Clamp(size, 0, 3);
        }

        private int PartySize(int partyId)
        {
            int count = 0;
            for (int i = 0; i < _sessions.Count; i++) if (_sessions[i].PartyId == partyId) count++;
            return count;
        }

        /// <summary>
        /// R7: spawns one more player rig for a local multi-player run (tests and the offline harness; networking will spawn from
        /// a prefab). The scene's own rig is cloned (components, wiring, camera and all; the camera, listener and torch are turned
        /// off because only a locally controlled rig presents), placed at <paramref name="position"/>, and admitted with
        /// <see cref="TryJoin"/> as a remote-controlled player: its gameplay input is off and nothing reads this process's keyboard for it.
        /// </summary>
        public PlayerRig SpawnPlayer(Vector3 position, Quaternion rotation, int requestedParty, out string reason)
        {
            return SpawnPlayer(position, rotation, requestedParty, ClassFor(_local != null ? _local.Rig : playerRig), out reason);
        }

        /// <summary>Spawns one remote rig with an explicit class selected by the trusted run host.</summary>
        public PlayerRig SpawnPlayer(Vector3 position, Quaternion rotation, int requestedParty,
            CharacterClassData selectedClass, out string reason)
        {
            reason = null;
            if (selectedClass == null) { reason = "no class selected"; return null; }
            PlayerRig template = _local != null ? _local.Rig : playerRig;
            if (template == null) { reason = "no template rig"; return null; }

            // Refuse before cloning anything: a full party must not leave a half-joined rig in the scene.
            if (!TryResolveParty(requestedParty, out _, out reason)) return null;

            GameObject clone = Instantiate(template.gameObject, position, rotation);
            _spawnedCount++;
            clone.name = $"{template.gameObject.name}_P{_spawnedCount + 1}";

            Camera[] cameras = clone.GetComponentsInChildren<Camera>(true);
            for (int i = 0; i < cameras.Length; i++)
            {
                cameras[i].enabled = false;
                cameras[i].gameObject.tag = "Untagged";
                AudioListener listener = cameras[i].GetComponent<AudioListener>();
                if (listener != null) listener.enabled = false;
            }

            ClassLoadout inheritedLoadout = clone.GetComponent<ClassLoadout>();
            if (inheritedLoadout != null) inheritedLoadout.DiscardInheritedVisuals();

            Light[] lights = clone.GetComponentsInChildren<Light>(true);
            for (int i = 0; i < lights.Length; i++) lights[i].enabled = false;

            PlayerRig rig = clone.GetComponent<PlayerRig>();
            if (rig == null) { Destroy(clone); reason = "template has no PlayerRig"; return null; }

            // Whatever the template carried is not this player's: start from empty hands and an empty bag.
            if (rig.Equipment != null) rig.Equipment.Clear();
            if (rig.Inventory != null) rig.Inventory.Clear();

            if (!TryJoin(rig, requestedParty, false, selectedClass, out reason))
            {
                Destroy(clone);
                return null;
            }

            rig.SetInputEnabled(false);
            return rig;
        }

        // ---- U3A2: network seams. No networking types here: the Net assembly calls these and then spawns its NetworkObject. ----

        /// <summary>Spawns and admits a remote-controlled player from <paramref name="prefab"/> with the approved class named by <paramref name="classId"/>.</summary>
        public bool TrySpawnNetworkPlayer(GameObject prefab, string classId, out PlayerRig rig, out string reason)
        {
            return TrySpawnNetworkPlayer(prefab, classId, false, out rig, out reason);
        }

        /// <summary>As above; <paramref name="locallyControlled"/> admits the host's own presenting player (camera and input stay as authored).</summary>
        public bool TrySpawnNetworkPlayer(GameObject prefab, string classId, bool locallyControlled, out PlayerRig rig, out string reason)
        {
            rig = null;
            if (!TryResolveClass(classId, out CharacterClassData selected))
            {
                reason = "class '" + (classId ?? "") + "' is not approved";
                return false;
            }

            return TrySpawnNetworkPlayer(prefab, selected, locallyControlled, out rig, out reason);
        }

        /// <summary>
        /// Instantiates <paramref name="prefab"/> at the first free layout spawn and admits it with <see cref="TryJoin"/>
        /// (auto-filled party, trusted class, sender bound) BEFORE the caller spawns the network object, so nothing is ever
        /// half admitted: on any refusal the instance is gone again and the combat registry is untouched. A remote rig has
        /// no camera, listener or lights, no gameplay input, and is driven externally (PlayerMovement.Step).
        /// </summary>
        public bool TrySpawnNetworkPlayer(GameObject prefab, CharacterClassData selectedClass, bool locallyControlled,
            out PlayerRig rig, out string reason)
        {
            rig = null;
            reason = null;
            if (_raidExpired || (_started && RaidDeadlineReached))
            {
                reason = "the raid timer has expired";
                return false;
            }
            if (prefab == null) { reason = "no player prefab"; return false; }
            if (selectedClass == null) { reason = "no class selected"; return false; }
            if (_state == RunState.Dead || _state == RunState.Extracted) { reason = "the run already ended"; return false; }
            if (locallyControlled && _local != null) { reason = "a local player is already in the run"; return false; }
            if (!TryResolveParty(-1, out _, out reason)) return false;

            PickSpawnPose(out Vector3 position, out Quaternion rotation);

            GameObject instance = Instantiate(prefab, position, rotation);
            _networkSpawnCount++;
            instance.name = prefab.name + "_N" + _networkSpawnCount;

            PlayerRig spawned = instance.GetComponent<PlayerRig>();
            if (spawned == null)
            {
                Destroy(instance);
                reason = "prefab has no PlayerRig";
                return false;
            }

            if (!locallyControlled) StripPresentation(instance);

            // Whatever the template carried is not this player's: start from empty hands and an empty bag.
            if (spawned.Equipment != null) spawned.Equipment.Clear();
            if (spawned.Inventory != null) spawned.Inventory.Clear();

            if (!TryJoin(spawned, -1, locallyControlled, selectedClass, out reason))
            {
                // The rig registered itself in its Awake; take it out now instead of at the end of the frame.
                CombatRegistry.Unregister(spawned.CombatId, spawned);
                Destroy(instance);
                return false;
            }

            if (!locallyControlled)
            {
                spawned.SetInputEnabled(false);
                if (spawned.Movement != null) spawned.Movement.ExternalDriven = true;
            }

            rig = spawned;
            Debug.Log($"[Emberdelve][RunDirector] network player joined id={spawned.PlayerId} party={spawned.PartyId} class={selectedClass.classId} local={locallyControlled} players={_sessions.Count}");
            return true;
        }

        internal bool TryCreateRoyaleBot(GameObject prefab, CharacterClassData selectedClass, int party,
            int floor, Vector3 position, out PlayerRig rig, out string reason)
        {
            rig = null; reason = "Bot creation is unavailable";
            if (NetRole.IsClientOnly || !RoyaleMode.HasValue || party < 0 || floor < 1 || floor > 3
                || prefab == null || prefab.scene.IsValid() || selectedClass == null || config == null
                || TryGetRoyaleFloorClock(floor, out _, out _)
                || (NetRole.IsOffline && LocalSession == null)) return false;
            var instance = Instantiate(prefab, position, Quaternion.identity);
            bool committed = false;
            var spawned = instance.GetComponent<PlayerRig>();
            try
            {
                if (spawned == null) { reason = "Bot prefab has no PlayerRig"; return false; }
                StripPresentation(instance);
                spawned.WireRig();
                spawned.Equipment?.Clear(); spawned.Inventory?.Clear();
                if (!TryJoinCore(spawned, party, false, selectedClass, floor, true, out reason)) return false;
                spawned.SetInputEnabled(false);
                if (spawned.Movement != null) spawned.Movement.ExternalDriven = true;
                var melee = spawned.GetComponent<MeleeCombat>(); if (melee != null) melee.RemoteDriven = true;
                var ranged = spawned.GetComponent<RangedCombat>(); if (ranged != null) ranged.RemoteDriven = true;
                if (!spawned.TryBeginFloorTransit(out reason)) return false;
                rig = spawned; committed = true; return true;
            }
            finally
            {
                if (!committed)
                {
                    if (spawned != null)
                    {
                        RemovePlayer(spawned.PlayerId, "Bot creation failed");
                        CombatRegistry.Unregister(spawned.CombatId, spawned);
                    }
                    if (Application.isPlaying) Destroy(instance); else DestroyImmediate(instance);
                }
            }
        }

        private static void StripPresentation(GameObject instance)
        {
            Camera[] cameras = instance.GetComponentsInChildren<Camera>(true);
            for (int i = 0; i < cameras.Length; i++)
            {
                cameras[i].enabled = false;
                cameras[i].gameObject.tag = "Untagged";
                AudioListener listener = cameras[i].GetComponent<AudioListener>();
                if (listener != null) listener.enabled = false;
            }

            ClassLoadout inheritedLoadout = instance.GetComponent<ClassLoadout>();
            if (inheritedLoadout != null) inheritedLoadout.DiscardInheritedVisuals();

            Light[] lights = instance.GetComponentsInChildren<Light>(true);
            for (int i = 0; i < lights.Length; i++) lights[i].enabled = false;
        }

        /// <summary>The first layout spawn no current player stands on (join order = slot order); when all are taken the slots are reused in turn.</summary>
        private void PickSpawnPose(out Vector3 position, out Quaternion rotation)
        {
            Vector3 ground = layout != null ? layout.CellCenterWorld(layout.playerStartCell) : Vector3.zero;
            int slots = layout != null && layout.playerSpawns != null ? layout.playerSpawns.Count : 0;

            if (slots > 0)
            {
                ground = layout.PlayerSpawnWorld(_networkSpawnCount);
                for (int i = 0; i < slots; i++)
                {
                    Vector3 candidate = layout.PlayerSpawnWorld(i);
                    if (!SpawnOccupied(candidate))
                    {
                        ground = candidate;
                        break;
                    }
                }
            }

            position = ground + Vector3.up * 0.15f;

            // Face the middle of the map so a fresh player is not staring at a wall.
            Vector3 toCentre = layout != null
                ? layout.CellCenterWorld(new Vector2Int(layout.gridWidth / 2, layout.gridHeight / 2)) - ground
                : Vector3.forward;
            toCentre.y = 0f;
            float yaw = toCentre.sqrMagnitude > 0.01f ? Mathf.Atan2(toCentre.x, toCentre.z) * Mathf.Rad2Deg : 0f;
            rotation = Quaternion.Euler(0f, yaw, 0f);
        }

        private bool SpawnOccupied(Vector3 ground)
        {
            for (int i = 0; i < _sessions.Count; i++)
            {
                PlayerRig other = _sessions[i].Rig;
                if (other == null) continue;
                Vector3 delta = other.transform.position - ground;
                delta.y = 0f;
                if (delta.sqrMagnitude < 1.2f * 1.2f) return true;
            }

            return false;
        }

        /// <summary>
        /// U3A2: takes one player out of the run immediately (disconnect). Their session and party seat are freed, their death
        /// hook is unsubscribed, and the rig leaves the combat registry (which also unbinds its command sender) so nothing
        /// can target, hit or hear it any more. The rig object itself is left to the caller (the network layer despawns it);
        /// pass <paramref name="destroyRig"/> when nothing else owns it. Returns false for an unknown id (idempotent).
        ///
        /// Run-end audit: removing a player never ends the run. A hosted run with nobody in it stays Running so a late
        /// joiner can enter; the run-wide outcome is still only ever decided by a player's own death or extraction.
        /// </summary>
        public bool RemovePlayer(int playerId, string reason, bool destroyRig = false)
        {
            if (!_byPlayerId.TryGetValue(playerId, out PlayerSession session)) return false;

            PlayerRig rig = session.Rig;
            if (rig != null && rig.Health != null && session.DiedHandler != null) rig.Health.Died -= session.DiedHandler;
            session.DiedHandler = null;

            _floorCoordinator?.ReleasePendingArrival(session);
            _byPlayerId.Remove(playerId);
            _sessions.Remove(session);
            if (_local == session) _local = null;

            if (rig != null)
            {
                CombatRegistry.Unregister(rig.CombatId, rig);
                rig.SetInputEnabled(false);
                if (rig.Movement != null) rig.Movement.CancelDash();
            }

            Debug.Log($"[Emberdelve][RunDirector] player removed id={playerId} reason='{reason}' players={_sessions.Count} senders={CombatSenders.BoundCount} registry={CombatRegistry.All.Count}");

            if (destroyRig && rig != null) Destroy(rig.gameObject);
            return true;
        }

        /// <summary>Offline only: the process-wide time scale belongs to the local game; a networked process never changes it.</summary>
        private static void ResetTimeScale()
        {
            if (NetRole.IsOffline) Time.timeScale = 1f;
        }

        private void OnDestroy()
        {
            _floorCoordinator?.ReleaseOwnedMaps();
            GameServices.Unregister<RunDirector>(this);
            GameServices.Unregister<ILocalPlayerSource>(this);
            GameEvents.DamageDealt -= HandleDamageDealt;

            for (int i = 0; i < _sessions.Count; i++)
            {
                PlayerSession session = _sessions[i];
                if (session.Rig != null && session.Rig.Health != null && session.DiedHandler != null) session.Rig.Health.Died -= session.DiedHandler;
            }

            // R7: the process-wide combat table and its per-run switches are cleared with the run that filled them.
            CombatRegistry.ResetForRun();

            ResetTimeScale();
        }

        private void Update()
        {
            if (ExtractionSettlementPending)
            {
                if (Time.unscaledTime >= _nextExtractionSettlementAttemptAt)
                {
                    _nextExtractionSettlementAttemptAt = Time.unscaledTime + 1f;
                    RetryExtractionSettlement(out _);
                }
                return;
            }
            if (_started && !_raidExpired && _state == RunState.Running && RaidDeadlineReached)
                ExpireRaid();
            if (_started) TickRoyaleFloors();

            RetryPendingDeathCustody();

            PlayerRig rig = Player;
            if (rig == null) return;

            EmberdelveInput input = rig.Input;
            if (input == null) return;

            if (input.PausePressed)
            {
                if (InventoryInputFocus.OwnsEscape) return;
                TogglePause();
            }
        }

        /// <summary>
        /// The satchel screen takes the pointer, so while it is open the player keeps their position and stops
        /// acting: no movement, no attacks, no skills. The world carries on, which is the cost of stopping to
        /// sort a bag in a hostile level. Pausing and the bag are separate states and neither cancels the other.
        /// (Local presentation: it acts on the locally controlled rig only.)
        /// </summary>
        public void SetInventoryOpen(bool open)
        {
            PlayerRig rig = Player;
            if (rig == null) return;

            InventoryOpen = open && _state == RunState.Running && LocalPlayerState == RunState.Running;

            bool terminal = LocalPlayerState == RunState.Dead || LocalPlayerState == RunState.Extracted;
            bool controlAllowed = !InventoryOpen && !_paused && !terminal && !ExtractionSettlementPending;
            rig.SetInputEnabled(controlAllowed);

            if (terminal)
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
            else if (controlAllowed)
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }
        }

        internal void TogglePause()
        {
            if (ExtractionSettlementPending) return;
            if (LocalPlayerState == RunState.Dead || LocalPlayerState == RunState.Extracted) return;
            if (_state != RunState.Running && _state != RunState.Paused) return;

            // Online, pause is the local player's menu only: the clock and the run-wide state belong to everybody.
            bool offline = NetRole.IsOffline;
            if (offline)
            {
                if (_paused)
                {
                    if (Time.unscaledTime >= _offlinePauseStartedAt)
                        _offlinePausedDuration += Time.unscaledTime - _offlinePauseStartedAt;
                    _offlinePauseStartedAt = 0f;
                    _paused = false;
                }
                else
                {
                    _offlinePauseStartedAt = Time.unscaledTime;
                    _paused = true;
                }
            }
            else
            {
                _paused = !_paused;
            }

            if (offline) Time.timeScale = _paused ? 0f : 1f;
            if (_local != null && _local.IsRunning) _local.Rig.SetInputEnabled(!_paused);
            if (offline) SetState(_paused ? RunState.Paused : RunState.Running);
            GameEvents.RaisePause(_paused);

            Cursor.lockState = _paused ? CursorLockMode.None : CursorLockMode.Locked;
            Cursor.visible = _paused;
        }

        private void HandleDamageDealt(int playerId, HitFeedback feedback)
        {
            if (feedback.TargetKind != EntityKind.Enemy || !feedback.TargetKilled) return;
            if (!_byPlayerId.TryGetValue(playerId, out PlayerSession session)) return;
            session.Kills++;
            GameEvents.RaiseStatusLine(playerId, $"Slain: {session.Kills}");
        }

        private void HandlePlayerDeath(PlayerSession session)
        {
            if (session == null || !session.IsRunning) return;
            if (NetRole.IsNetworked)
            {
                session.DeathCustodyPending = true;
                session.NextDeathCustodyAttemptAt = 0f;
                if (session.Rig != null) session.Rig.SetInputEnabled(false);
                TryCommitDeathCustody(session);
                return;
            }
            CompletePlayer(session, RunState.Dead);
        }

        private void RetryPendingDeathCustody()
        {
            float now = Time.unscaledTime;
            for (int i = 0; i < _sessions.Count; i++)
            {
                PlayerSession session = _sessions[i];
                if (session.DeathCustodyPending && now >= session.NextDeathCustodyAttemptAt)
                    TryCommitDeathCustody(session);
            }
        }

        private void TryCommitDeathCustody(PlayerSession session)
        {
            if (session == null || !session.IsRunning || !session.DeathCustodyPending) return;
            session.NextDeathCustodyAttemptAt = Time.unscaledTime + 1f;
            if (!GameServices.TryGet<ICorpseDeathCustodian>(out ICorpseDeathCustodian custodian) || custodian == null)
            {
                Debug.LogError("[RunDirector] dead player " + session.PlayerId + " remains pending: network corpse custody service is unavailable");
                return;
            }
            PlayerRig rig = session.Rig;
            if (rig == null || rig.Inventory == null || rig.Equipment == null)
            {
                Debug.LogError("[RunDirector] dead player " + session.PlayerId + " remains pending: source rig/loadout is unavailable");
                return;
            }
            if (!custodian.TryCommitDeath(session.PlayerId, rig.transform.position, rig.Inventory, rig.Equipment, out string reason))
            {
                Debug.LogWarning("[RunDirector] death custody pending for player " + session.PlayerId + ": " + reason);
                return;
            }
            session.DeathCustodyPending = false;
            CompletePlayer(session, RunState.Dead);
        }

        /// <summary>
        /// Ends each player still in the raid when its timer expires. Timeout is a death outcome: it uses the normal
        /// health/death path so networked loadouts enter session-only corpse custody before the owner result is sent.
        /// No loot is banked or written to durable storage here.
        /// </summary>
        private void ExpireRaid()
        {
            if (_raidExpired) return;
            _raidExpired = true;
            ExpirePlayersOnFloor(0);
        }

        private void ExpirePlayersOnFloor(int floor)
        {

            // Snapshot the count because completing the final session can end the run while this loop is executing.
            // HandlePlayerDeath applies the same custody and exact-once result rules as any other lethal event.
            int count = _sessions.Count;
            for (int i = 0; i < count; i++)
            {
                PlayerSession session = _sessions[i];
                if (session == null || !session.IsRunning || session.Rig == null || session.Rig.Health == null) continue;
                if (floor != 0 && session.Floor != floor) continue;

                PlayerRig rig = session.Rig;
                DamageInfo timeoutDamage = new DamageInfo(
                    rig.Health.CurrentHealth + 1f,
                    rig.transform.position,
                    Vector3.zero,
                    rig.transform.position,
                    0,
                    EntityKind.Neutral,
                    DamageType.Physical,
                    false,
                    false,
                    0f);
                rig.ApplyDamage(timeoutDamage);
            }
        }

        /// <summary>R2: the extraction rule for ONE actor: that player must still be in the run, alive, and (data) carrying value.</summary>
        public int ExtractionFloorFor(IInteractionActor actor)
        {
            var session = SessionOf(actor);
            if (session == null || !session.IsRunning || session.Rig == null || !session.Rig.IsInPlay || session.Rig.IsDead) return -1;
            return RoyaleMode.HasValue ? (session.Floor == 3 && IsRoyaleFloorOpen(3) ? 3 : -1) : 0;
        }

        public bool ExtractionAllowed(IInteractionActor actor, out string reason)
        {
            PlayerSession session = SessionOf(actor);
            if (session == null)
            {
                reason = "Not a player of this run";
                return false;
            }

            if (RoyaleMode.HasValue && (session.Floor != 3 || !IsRoyaleFloorOpen(session.Floor)))
            {
                reason = "Final extraction is only available on floor 3";
                return false;
            }

            if (_raidExpired || RaidDeadlineReached)
            {
                reason = "The raid timer has expired";
                return false;
            }

            if (_state != RunState.Running || !session.IsRunning)
            {
                reason = "Run already finished";
                return false;
            }

            if (!actor.IsAlive)
            {
                reason = "The dead cannot escape";
                return false;
            }

            if (config != null && config.requireLootToExtract && (session.Inventory == null || session.Inventory.RecoveredValue <= 0))
            {
                reason = "Recover at least one relic first";
                return false;
            }

            reason = null;
            return true;
        }

        /// <summary>
        /// The authority for the extraction rule: re-validates here, for the acting player, whatever the caller already
        /// checked, and refuses with a reason instead of completing that player's run. Only the actor's own run ends:
        /// every other player keeps playing.
        /// </summary>
        public bool BeginExtraction(IInteractionActor actor, out string reason)
        {
            if (!ExtractionAllowed(actor, out reason))
            {
                GameEvents.RaiseToast(actor != null ? actor.PlayerId : GameEvents.NoPlayer, reason);
                return false;
            }

            PlayerSession session = SessionOf(actor);
            CompletePlayer(session, RunState.Extracted);
            if (session.ExtractionSettlementPending)
            {
                reason = OfflineProfileSession.LastError ?? "Waiting for loot to be saved";
                return false;
            }
            return session.State == RunState.Extracted;
        }

        /// <summary>Retries the same immutable offline return snapshot; never reopens gameplay while it is pending.</summary>
        public bool RetryExtractionSettlement(out string reason)
        {
            reason = null;
            if (_pendingExtraction == null) return true;
            if (!NetRole.IsOffline) { reason = "Offline settlement belongs to the offline authority"; return false; }
            if (!OfflineProfileSession.RetrySettlement(out reason)) return false;
            PlayerSession session = _pendingExtraction;
            _pendingExtraction = null;
            session.ExtractionSettlementPending = false;
            CompletePlayer(session, RunState.Extracted);
            return session.State == RunState.Extracted;
        }

        /// <summary>
        /// Ends ONE player's run (death or extraction): records their result, tells only that player's viewers, takes the rig
        /// out of play when it left the dungeon, and finishes the run itself once nobody is still in it.
        /// </summary>
        private void CompletePlayer(PlayerSession session, RunState outcome)
        {
            if (session == null || !session.IsRunning) return;

            if (session.IsLocal && NetRole.IsOffline && !Emberdelve.Items.OfflineProfileSession.Complete(
                    outcome == RunState.Extracted, session.Rig, out string saveReason))
            {
                Debug.LogError("[Emberdelve][Profile] " + saveReason);
                GameEvents.RaiseToast(session.PlayerId, saveReason);
                if (outcome == RunState.Extracted)
                {
                    session.ExtractionSettlementPending = true;
                    _pendingExtraction = session;
                    _nextExtractionSettlementAttemptAt = Time.unscaledTime + 1f;
                    _offlinePauseStartedAt = Time.unscaledTime;
                    _paused = true;
                    Time.timeScale = 0f;
                    session.Rig.SetInputEnabled(false);
                    SetState(RunState.Paused);
                    GameEvents.RaiseStatusLine(session.PlayerId, "Saving loot failed. Retrying; raid is paused.");
                    return;
                }
            }

            session.State = outcome;
            session.EndedAt = Elapsed;

            PlayerRig rig = session.Rig;
            if (rig != null)
            {
                if (outcome == RunState.Extracted) rig.MarkExtracted();
                else rig.SetInputEnabled(false);
            }

            CharacterClassData classData = session.Class != null ? session.Class : (rig != null && rig.Class != null ? rig.Class : RunContext.SelectedClass);
            session.Result = new RunResult(
                outcome,
                classData != null ? classData.displayName : "Class",
                session.Inventory != null ? session.Inventory.RecoveredValue : 0,
                session.Inventory != null ? session.Inventory.LifetimeCollected : 0,
                session.Kills,
                session.EndedAt,
                session.PlayerId,
                rig != null ? Emberdelve.Items.GearScore.ForEquipment(rig.Equipment) : 0);
            session.HasResult = true;

            bool anyoneLeft = false;
            for (int i = 0; i < _sessions.Count; i++)
                if (_sessions[i].State == RunState.Running) { anyoneLeft = true; break; }

            // The run-wide outcome is published first (as before), then the player's own state and result.
            if (!anyoneLeft) FinishRun(outcome);

            if (session.IsLocal)
            {
                _paused = false;
                InventoryOpen = false;
                ResetTimeScale();
                GameEvents.RaisePause(false);
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
                RunContext.CompleteRun(session.Result);
            }

            GameEvents.RaisePlayerRunState(session.PlayerId, outcome);
            GameEvents.RaiseRunEnded(session.PlayerId, session.Result);
        }

        /// <summary>No player is left in the run: it ends as Extracted when anyone escaped, otherwise as Dead.</summary>
        private void FinishRun(RunState lastOutcome)
        {
            RunState outcome = lastOutcome;
            for (int i = 0; i < _sessions.Count; i++)
            {
                if (_sessions[i].State == RunState.Extracted) { outcome = RunState.Extracted; break; }
            }

            if (_state != RunState.Running && _state != RunState.Paused) return;
            SetState(outcome);
            _paused = false;
            ResetTimeScale();
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
        public bool CollectLoot(IInteractionActor actor, ItemData item, int amount)
        {
            return CollectLootCounted(actor, item, amount) == Mathf.Max(1, amount);
        }

        /// <summary>
        /// The counted loot path (U1C2H): the run host hands the offer to the ACTING player's own bag (its own session
        /// record, never a default player) and reports back the exact units that committed, so a chest or a ground pickup can shrink
        /// itself by that much instead of guessing from a bool. No placement rule lives here, and no unit is credited
        /// twice: the bag is what counts, and RunDirector reads its loot figures straight off the bag. An actor that is not a
        /// running player of this run (unknown, dead or already extracted) takes nothing.
        /// </summary>
        public int CollectLootCounted(IInteractionActor actor, ItemData item, int amount)
        {
            PlayerSession session = SessionOf(actor);
            if (session == null || session.Inventory == null || item == null) return 0;
            if (!session.IsRunning || !actor.IsAlive) return 0;

            int requested = Mathf.Max(1, amount);
            session.Inventory.TryAdd(item, requested, out _, out int accepted);
            return Mathf.Clamp(accepted, 0, requested);
        }

        /// <summary>
        /// The instance half of the loot contract (U2B9): a chest that rolled a gear item hands over that very
        /// <see cref="ItemInstance"/>, and this is the only path that accepts it. It goes straight to the ACTING
        /// player's bag's own instance placement, so the accepted item is the same object - same UID, same rarity, same accepted
        /// affix records, quantity 1 - with no mint, no copy and no reroll, and it is credited exactly once
        /// because the bag counts it once.
        ///
        /// A run that is not in a state to receive loot refuses it: a run that has not started, one that is
        /// already dead or extracted, a player who is dead, an actor that is not a player of this run, and a player with no bag
        /// at all all leave the item where it was. A refusal changes nothing on either side, which is what lets the chest keep the
        /// identical item for the next open.
        /// </summary>
        public bool TryCollectLootInstance(IInteractionActor actor, ItemInstance instance, out string reason)
        {
            reason = null;

            if (instance == null)
            {
                reason = "no item instance to collect";
                return false;
            }

            PlayerSession session = SessionOf(actor);
            if (session == null)
            {
                reason = $"the actor is not a player of this run: {instance.Definition.displayName} [{instance.UidText}] stays where it is";
                return false;
            }

            if (_state != RunState.Running || !session.IsRunning)
            {
                reason = $"the run is {(session.IsRunning ? _state : session.State)}: {instance.Definition.displayName} [{instance.UidText}] is not " +
                         "loot a finished or unstarted run can take";
                return false;
            }

            if (session.Inventory == null)
            {
                reason = $"no player bag to carry {instance.Definition.displayName} [{instance.UidText}]";
                return false;
            }

            if (!actor.IsAlive)
            {
                reason = $"the player is dead: {instance.Definition.displayName} [{instance.UidText}] stays " +
                         "where it is";
                return false;
            }

            // The bag's own rule, read unchanged: one identity is one item, so a UID this player already
            // carries or already wears is refused rather than granted a second time.
            if (!session.Inventory.TryAddInstance(instance, out reason)) return false;

            // U5A: a weapon or shield picked up goes into the empty matching hand of the live set (the bag keeps it when the hand is taken).
            Equipment gear = session.Rig != null ? session.Rig.Equipment : null;
            if (gear != null) gear.TryAutoSeatHand(instance);
            return true;
        }

        /// <summary>U5A: the ground half of a drop. The server lays the dropped instance in the world as a pickup holding that very instance.</summary>
        public bool TryPrepareDropped(ItemInstance instance, Vector3 position, out IPreparedGroundDrop prepared, out string reason)
        {
            prepared = null;
            if (instance == null || instance.Definition == null)
            {
                reason = "no item to lay on the ground";
                return false;
            }

            if (_state != RunState.Running)
            {
                reason = "the run is not running: nothing can be dropped";
                return false;
            }

            PreparedGroundDrop staged = null;
            try
            {
                LootPickup pickup = BuildPickupBody(instance.Definition, position, false);
                if (pickup == null)
                {
                    reason = "the ground refused the item";
                    return false;
                }

                staged = new PreparedGroundDrop(pickup, this);
                pickup.Configure(instance, this);
                prepared = staged;
                reason = null;
                return true;
            }
            catch
            {
                staged?.Cancel();
                throw;
            }
        }

        public bool TryPrepareDroppedStack(ItemData item, int amount, Vector3 position, out IPreparedGroundDrop prepared, out string reason)
        {
            prepared = null;
            if (_state != RunState.Running || item == null || item.IsEquippable || amount <= 0 ||
                amount > item.EffectiveMaxStack || Config == null || Config.itemCatalog == null || !Config.itemCatalog.Contains(item))
            { reason = "the run cannot stage this item stack"; return false; }
            PreparedGroundDrop staged = null;
            try
            {
                LootPickup pickup = BuildPickupBody(item, position, false);
                if (pickup == null) { reason = "the ground refused the stack"; return false; }
                staged = new PreparedGroundDrop(pickup, this);
                pickup.Configure(item, amount, this);
                prepared = staged;
                reason = null;
                return true;
            }
            catch { staged?.Cancel(); throw; }
        }

        /// <summary>A staged pickup remains inactive until ownership is removed from its source.</summary>
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        public bool FailNextGroundDropPublicationForProof { get; set; }
#endif
        private sealed class PreparedGroundDrop : IPreparedGroundDrop
        {
            private LootPickup _pickup;
            private bool _published;

            private readonly RunDirector _owner;
            internal PreparedGroundDrop(LootPickup pickup, RunDirector owner) { _pickup = pickup; _owner = owner; }

            public void Publish()
            {
                if (_pickup == null || _published) throw new InvalidOperationException("prepared ground drop is no longer publishable");
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                if (_owner.FailNextGroundDropPublicationForProof)
                {
                    _owner.FailNextGroundDropPublicationForProof = false;
                    throw new InvalidOperationException("injected ground publication failure");
                }
#endif
                _published = true;
                _pickup.gameObject.SetActive(true);
            }

            public void Cancel()
            {
                LootPickup pickup = _pickup;
                _pickup = null;
                if (pickup == null) return;
                GameObject root = pickup.gameObject;
                pickup.ClearPreparedInstance();
                try { root.SetActive(false); }
                finally { UnityEngine.Object.Destroy(root); }
            }

            public void Dispose()
            {
                if (!_published) Cancel();
                _pickup = null;
            }
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        /// <summary>Test seam: swap the raid session (fixed seed) and re-point the bag at its issuer.</summary>
        internal void UseSessionForTest(RaidSession session)
        {
            if (session == null) return;
            Session = session;
            for (int i = 0; i < _sessions.Count; i++)
                if (_sessions[i].Inventory != null) _sessions[i].Inventory.SetIssuer(session.Issuer);
        }
#endif

        /// <summary>
        /// R4: rolls the dying enemy's drop table with (raid seed, drop key) and the raid's issuer, then lays the result
        /// on the ground. Carried goods become definition pickups; gear becomes pickups that hold the concrete instance
        /// (UID, rarity, rolled affixes), so taking one gives the bag exactly that item. A table that cannot be produced
        /// drops nothing and logs why.
        /// </summary>
        public void DropEnemyLoot(EnemyData enemy, string dropKey, Vector3 position)
        {
            if (enemy == null || enemy.dropTable == null || Session == null) return;

            ItemCatalog catalog = config != null ? config.itemCatalog : null;
            System.Collections.Generic.List<LootDrop> drops = new System.Collections.Generic.List<LootDrop>();

            if (!Session.TryRollEnemyDrops(enemy, dropKey, catalog, drops, out string reason))
            {
                Debug.LogError($"[Emberdelve][EnemyDrop] {enemy.name} '{dropKey}': drop table refused, nothing dropped - {reason}", this);
                return;
            }

            if (drops.Count > 0 && GameServices.TryGet<IWorldLootContainerHost>(out var containers))
            {
                if (CorpseContainerModel.TryCreateLoot("enemy", enemy.displayName, position + Vector3.up * 0.35f,
                        drops, catalog, out var container, out reason) && containers.TryPublishLoot(container, out _, out reason)) return;
                Debug.LogWarning("[Emberdelve][EnemyDrop] Container unavailable; preserving loot as pickups: " + reason);
            }

            DeterministicRng scatter = Session.ScatterRng(dropKey);
            for (int i = 0; i < drops.Count; i++)
            {
                // Position only: a deterministic point in a 0.7 m disc, drawn from the drop's own stream.
                double angle = scatter.NextDouble() * 6.283185307179586;
                double radius = 0.7 * System.Math.Sqrt(scatter.NextDouble());
                Vector3 at = position + new Vector3((float)(System.Math.Cos(angle) * radius), 0.35f, (float)(System.Math.Sin(angle) * radius));

                LootDrop drop = drops[i];
                if (drop.IsRolled) SpawnLootInstance(drop.Instance, at);
                else SpawnLoot(drop.Item, Mathf.Max(1, drop.Amount), at);
            }
        }

        /// <summary>Lays one concrete item on the ground. The pickup holds this very instance until it is taken.</summary>
        public LootPickup SpawnLootInstance(ItemInstance instance, Vector3 worldPosition)
        {
            if (instance == null) return null;

            LootPickup pickup = BuildPickupBody(instance.Definition, worldPosition);
            if (pickup != null) pickup.Configure(instance, this);
            return pickup;
        }

        public void SpawnLoot(ItemData item, int amount, Vector3 worldPosition)
        {
            if (item == null) return;

            LootPickup pickup = BuildPickupBody(item, worldPosition);
            if (pickup != null) pickup.Configure(item, Mathf.Max(1, amount), this);
        }

        private LootPickup BuildPickupBody(ItemData item, Vector3 worldPosition, bool activate = true)
        {
            GameObject root = GameObject.CreatePrimitive(PrimitiveType.Cube);
            // Primitive creation itself has no gameplay scripts. Deactivate before adding LootPickup so its Awake
            // runs only after the source has committed and the prepared item becomes visible.
            if (!activate) root.SetActive(false);
            root.name = $"Loot_{item.displayName}";
            root.transform.position = worldPosition;
            _floorCoordinator?.ParentRuntimeDrop(root, worldPosition);
            root.transform.localScale = new Vector3(
                Mathf.Max(0.12f, item.worldSize.x),
                Mathf.Max(0.12f, item.worldSize.y),
                Mathf.Max(0.12f, item.worldSize.z));

            int lootLayer = LayerMask.NameToLayer(GameConfig.LootLayerName);
            if (lootLayer >= 0) root.layer = lootLayer;

            BoxCollider box = root.GetComponent<BoxCollider>();
            if (box != null) box.size = new Vector3(1.9f, 1.9f, 1.9f);

            Renderer renderer = root.GetComponent<Renderer>();
            if (renderer != null && item.worldMaterial != null) renderer.material = item.worldMaterial;

            LootPickup pickup = root.AddComponent<LootPickup>();
            return pickup;
        }

        public void RestartRun()
        {
            if (!SceneFlowAllowed)
            {
                Debug.LogWarning("[Emberdelve][RunDirector] RestartRun ignored: the scene is network-controlled (role " + NetRole.Current + ")");
                return;
            }

            if (Emberdelve.Items.OfflineProfileSession.Current != null)
            {
                ReturnToLobby();
                return;
            }
            Time.timeScale = 1f;
            SceneManager.LoadScene(!string.IsNullOrEmpty(_runtimeDungeonSceneName)
                ? _runtimeDungeonSceneName
                : GameConfig.DefaultDungeonSceneName);
        }

        public void ReturnToLobby()
        {
            if (!SceneFlowAllowed)
            {
                Debug.LogWarning("[Emberdelve][RunDirector] ReturnToLobby ignored: the scene is network-controlled (role " + NetRole.Current + ")");
                return;
            }

            if (!Emberdelve.Items.OfflineProfileSession.Complete(false, _local?.Rig, out string saveReason))
            {
                GameEvents.RaiseToast(saveReason);
                return;
            }
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
