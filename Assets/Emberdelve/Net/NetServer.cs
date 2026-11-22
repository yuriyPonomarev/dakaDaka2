using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Emberdelve.Combat;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Flow;
using Emberdelve.AI;
using Emberdelve.Interaction;
using Emberdelve.Items;
using Emberdelve.Level;
using Emberdelve.Net.Rules;
using Unity.Collections;
using Emberdelve.Player;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Emberdelve.Net
{
    /// <summary>
    /// U3A3: the server side of the network layer (dedicated server and host). Owned and driven by NetBootstrap.
    ///
    ///   - loads the dungeon scene through NGO scene management once the server listens (clients follow on their own);
    ///   - ConnectionApprovalCallback: the pure NetApproval rule, fed with this process's own facts (admitted/reserved seats,
    ///     slots in use, the RunDirector's approved class list). Approvals that arrive before the dungeon scene (and so the
    ///     RunDirector) exists are HELD (response.Pending) and decided the moment it does;
    ///   - OnClientConnected: RunDirector.TrySpawnNetworkPlayer (id, party, class, sender bound, registered) and only then
    ///     NetworkObject.SpawnAsPlayerObject(clientId): nothing is ever half-spawned;
    ///   - OnClientDisconnect: RunDirector.RemovePlayer (session, party seat, registry, sender) and despawn, immediately;
    ///   - one status line every two seconds: "status players=N senders=N registry=N clients=N";
    ///   - U3A4a: the authoritative movement simulation (NetMovementServer): every admitted remote player is registered after its
    ///     spawn and stepped once per network tick from its sanitized input queue; unregistered (final counters logged) on removal.
    ///
    /// Everything a client says is data: the connect payload is a slot string and a class id string, nothing else.
    /// </summary>
    internal sealed partial class NetServer
    {
        private const float StatusSeconds = 2f;
        private const float SceneLoadTimeoutSeconds = 60f;
        private const float UnconnectedReservationSeconds = 20f;

        private sealed class Approved
        {
            public string Slot;
            public string ClassId;
            /// <summary>The skill kit the client asked for ("a1,a2;p1,p2"); validated against the class pool at spawn.</summary>
            public string Kit;
            public float ApprovedAt;
            public bool Connected;
            public bool ModulePlanReady;
            public float ModulePlanSentAt;
        }

        private sealed class Pending
        {
            public ulong ClientId;
            public byte[] Payload;
            public NetworkManager.ConnectionApprovalResponse Response;
        }

        private readonly NetworkManager _nm;
        private readonly NetLaunchArgs _args;
        private readonly GameObject _playerPrefab;
        private readonly Action<string> _log;
        private readonly Action<string> _logError;
        private readonly NetIdentityTable _table = new NetIdentityTable();
        private readonly Dictionary<ulong, Approved> _approved = new Dictionary<ulong, Approved>();
        private readonly List<Pending> _pending = new List<Pending>();
        private readonly List<ulong> _awaitingSpawn = new List<ulong>();
        private readonly List<ulong> _scratch = new List<ulong>();
        private readonly List<NetIdentity> _pendingCorpseDisconnects = new List<NetIdentity>();
        // U3A5 rejoin policy: slot (lower case) -> the player id its last actor had. A returning slot is a NEW actor with a NEW id.
        private readonly Dictionary<string, int> _lastRemovedBySlot = new Dictionary<string, int>();

        private readonly Dictionary<int, PlayerRig> _bots = new Dictionary<int, PlayerRig>();

        private NetMovementServer _movement;
        private NetSkillServer _skills;
        private NetCombatServer _combat;
        private NetInventoryServer _inventory;
        private NetExtractionServer _extraction;
        private RunDirector _director;
        private bool _sceneRequested;
        private bool _sceneFailed;
        private float _sceneRequestedAt;
        private float _nextModuleApplyAttemptAt;
        private string _lastModuleApplyReason;
        private float _nextStatusAt;
        private float _nextRaidClockPublishAt;
        private bool _inventoryContestAttempted;
        private bool _floorLootProofAttempted;
        private bool _floorLootProofReady;
        private NetIdentity _floorLootProofA;
        private NetIdentity _floorLootProofB;
        private ulong _floorLootProofContestId;
        private ulong _floorLootProofDeadId;
        private string _floorLootProofContestUid = "";
        private string _floorLootProofDeadUid = "";
        private Vector3 _floorLootProofNearA;
        private Vector3 _floorLootProofNearB;
        private GameObject _floorLootProofBlocker;
        private string _floorLootProofLastAction = "";
        private bool _inventoryDropProofAttempted;
        private bool _inventoryDropProofReady;
        private bool _inventoryDropProofCommitLogged;
        private bool _inventoryDropProofRollbackLogged;
        private ulong _inventoryDropProofRevision;
        private NetIdentity _inventoryDropProofOwner;
        private NetIdentity _inventoryDropProofPeer;
        private ItemInstance _inventoryDropProofItem;
        private EquipmentSlot _inventoryDropProofSlot;
        private bool _corpseProofAttempted;
        private bool _corpseDeathProofAttempted;
        private bool _corpseDeathProofStimulusApplied;
        private bool _corpseDeathProofResultLogged;
        private bool _corpseDeathProofDisconnectRefusalArmed;
        private NetIdentity _corpseDeathProofActor;
        private NetIdentity _corpseDeathProofDisconnectActor;
        private string _corpseDeathProofUid;
        private string _corpseDeathProofDisconnectUid;
        private NetIdentity _corpseProofPlayerA;
        private NetIdentity _corpseProofPlayerB;
        private CorpseContainerModel _corpseProofModel;
        private CorpseLootWorldPresenter _corpseProofHostPresenter;
        private CorpseContainerDocument _corpseProofHostDocument;
        private ulong _corpseProofCorpseId;
        private Vector3 _corpseProofApproach;
        private Vector3 _corpseProofBApproach;
        private string _corpseProofLastAction = "";
        private GameObject _corpseProofBlocker;
        private bool _combatEnemyProofAttempted;
        private EnemyAgent _combatEnemyProofTarget;
        private float _combatEnemyProofHealthAtStage;
        private int _combatEnemyProofSwingsAtStage;
        private float _combatEnemyProofSwingAt;
        private bool _combatEnemyProofReleased;
        private bool _combatEnemyProofDeathApplied;
        private bool _extractionProofAttempted;
        private PlayerRig _extractionProofRig;
        private bool _extractionProofStimulusApplied;
        private bool _timeoutProofAttempted;
        private bool _timeoutProofOutcomeLogged;
        private readonly List<NetIdentity> _timeoutProofActors = new List<NetIdentity>(2);
        private string _timeoutProofUidA;
        private string _timeoutProofUidB;
        private bool _runResultSubscribed;
        private DungeonModuleCatalog _moduleCatalog;
        private DungeonModulePlan _modulePlan;
        private bool _moduleReadyHandlerRegistered;
        private bool _modularProofPassReported;
        private bool _doorProofAttempted;
        private bool _doorProofFixtureReady;
        private NetIdentity _doorProofActorA;
        private NetIdentity _doorProofActorB;
        private DungeonDoorInteractable _doorProofDoor;
        private GameObject _doorProofOccluder;
        private string _doorProofLastStage = string.Empty;
        private Vector3 _doorProofNearPosition;
        private Vector3 _doorProofApproach;
        private EnemyAgent _doorProofAiEnemy;
        private Vector3 _doorProofAiStartPosition;
        private int _doorProofAiTargetCombatId;
        private float _doorProofAiStartedAt;
        private bool _doorProofAiRunning;

        public NetServer(NetworkManager nm, NetLaunchArgs args, GameObject playerPrefab, Action<string> log, Action<string> logError)
        {
            _nm = nm;
            _args = args;
            _playerPrefab = playerPrefab;
            _log = log;
            _logError = logError;
        }

        public NetIdentityTable Table => _table;

        /// <summary>True once the dungeon scene is loaded here and its RunDirector is running (admission can decide).</summary>
        public bool IsReady => _director != null;
        public bool CanStart => !_sceneFailed && _modulePlan != null;

        /// <summary>Connections that hold a seat (approved, spawned or about to be).</summary>
        public int SeatCount => _approved.Count;

        public int BotCount => _bots.Count;
        public int RegisteredAdventurerCount
        {
            get
            {
                int count = 0;
                foreach (var entity in CombatRegistry.All) if (entity is PlayerRig) count++;
                return count;
            }
        }
        private RoyalePopulationDriver _initialPopulation;
        private RoyaleDescentDriver _descentPopulation;
        private NetRoyaleMapServer _royaleMaps;
        private float _nextRoyaleMapTick;
#if DEVELOPMENT_BUILD || UNITY_EDITOR
        private bool _royaleMapProofPrepared, _royaleMapProofReported;
        private readonly bool _royaleMapProof = Array.IndexOf(Environment.GetCommandLineArgs(), "-ember-royale-map-proof") >= 0;
#endif
        private float _nextPopulationTick;
        private uint _lastDescentRevision;
        private string _populationWaitReason;

        /// <summary>Authority-only destination population. Bots never acquire a transport connection
        /// or PlayerObject ownership; ordinary NGO spawning publishes them to every observer.</summary>
        public bool TrySpawnRoyaleBot(int floor, int party, string classId, out PlayerRig rig, out string reason)
        {
            rig = null; reason = "Network bot spawning is unavailable";
            if (_nm == null || !_nm.IsServer || !_nm.IsListening || _director == null || _combat == null
                || !_director.TryResolveClass(classId, out var character)) return false;
            PlayerRig created = null;
            bool committed = false;
            try
            {
                if (!_director.FloorCoordinator.TrySpawnBot(floor, party, _playerPrefab, character, out created, out reason)) return false;
                var networkObject = created.GetComponent<NetworkObject>();
                var networkRig = created.GetComponent<NetPlayerRig>();
                if (networkObject == null || networkRig == null)
                { reason = "Bot prefab is missing its network components"; return false; }
                networkObject.DontDestroyWithOwner = true;
                networkObject.Spawn(true);
                if (!networkObject.IsSpawned || networkObject.IsPlayerObject || networkRig.Role != NetPlayerRole.Server
                    || !_combat.OnBotAdmitted(created, networkRig))
                { reason = "Bot network admission failed"; return false; }
                _bots.Add(created.PlayerId, created);
                rig = created; committed = true; reason = null;
                _log("bot admitted id=" + rig.PlayerId + " party=" + rig.PartyId + " floor=" + floor + " netId=" + networkObject.NetworkObjectId);
                return true;
            }
            catch (Exception error)
            {
                reason = "Bot spawn failed: " + error.Message;
                _logError(reason);
                return false;
            }
            finally
            {
                if (!committed && created != null) RetireBot(created, "bot spawn rollback");
            }
        }

        private void TickRoyalePopulation(float now)
        {
            if (_director == null || !_director.IsStarted || !_director.RoyaleMode.HasValue) return;
            bool newTeamEntry = _director.HasTeamDescent && _director.DescentRevision != _lastDescentRevision;
            if (!newTeamEntry && now < _nextPopulationTick) return;
            _nextPopulationTick = now + .25f;
            _lastDescentRevision = _director.DescentRevision;
            if (_initialPopulation != null && _initialPopulation.Started)
            {
                if (_descentPopulation == null) _descentPopulation = new RoyaleDescentDriver(_director,
                    _director.Config != null ? _director.Config.raidDurationSeconds : 600,
                    _director.FloorCoordinator.TryPrepareDestination, SpawnInitialBot,
                    floor => _royaleMaps != null && _royaleMaps.PublicStateReady(floor),
                    (floor, session) => session.IsBot || (session.Rig.TryGetComponent<NetPlayerRig>(out var view)
                        && _royaleMaps != null && _royaleMaps.PublicStateReadyForClient(floor, view.OwnerClientId)));
                if (_descentPopulation.Tick(now, out string wait)) _log("Royale destination population arrived");
                else if (wait != _populationWaitReason) { _populationWaitReason = wait; _log("Royale descent: " + wait); }
                return;
            }
            if (_initialPopulation == null)
                _initialPopulation = new RoyalePopulationDriver(_director, 1,
                    _director.Config != null ? _director.Config.raidDurationSeconds : 600, 15f, SpawnInitialBot);
            // Approved connections must all finish their authoritative map ACK and actor admission.
            bool ready = _approved.Count > 0 && _table.Count == _approved.Count && _pending.Count == 0;
            if (_initialPopulation.Tick(now, ready, out string reason))
                _log("Royale floor 1 started population=" + _director.FloorCoordinator.ReservedCount(1));
            else if (reason != _populationWaitReason)
            { _populationWaitReason = reason; _log("Royale population: " + reason); }
        }

        private bool SpawnInitialBot(int floor, int party, out PlayerRig rig, out string reason)
        {
            rig = null; reason = "No approved class for bot";
            string preferred = _bots.Count % 2 == 0 ? "warrior" : "archer";
            if (!_director.TryResolveClass(preferred, out var character))
            {
                foreach (var session in _director.Players)
                    if (!session.IsBot && session.Class != null) { character = session.Class; break; }
            }
            return character != null && TrySpawnRoyaleBot(floor, party, character.classId, out rig, out reason);
        }

        // Removal is lifecycle-only, not a substitute for death/corpse settlement.
        private void RetireBot(PlayerRig rig, string reason)
        {
            if (rig == null) return;
            _bots.Remove(rig.PlayerId);
            _combat?.OnBotRemoved(rig.CombatId);
            _director?.RemovePlayer(rig.PlayerId, reason);
            var networkObject = rig.GetComponent<NetworkObject>();
            if (networkObject != null && networkObject.IsSpawned)
            {
                try { networkObject.Despawn(true); }
                catch (Exception error)
                {
                    _logError("Bot despawn failed id=" + rig.PlayerId + ": " + error.Message);
                    UnityEngine.Object.Destroy(rig.gameObject);
                }
            }
            else if (Application.isPlaying) UnityEngine.Object.Destroy(rig.gameObject);
            else UnityEngine.Object.DestroyImmediate(rig.gameObject);
        }

        private void RetireAllBots()
        {
            foreach (var rig in new List<PlayerRig>(_bots.Values)) RetireBot(rig, "shutdown");
            _bots.Clear();
        }

        public void Install()
        {
            _nm.ConnectionApprovalCallback = Approve;
            NetIdentityTable.Current = _table;
            _movement = new NetMovementServer(_nm, _table, _log, _logError);
            _skills = new NetSkillServer(_nm, _table, _args.SkillAbusePolicy, SlotOfClient, _log, _logError);
            _combat = new NetCombatServer(_nm, _table, _skills, _log, _logError); // U3B3: shares the skill channel's reject ledger
            _inventory = new NetInventoryServer(_nm, _table, _skills, _log, _args.DoorProofServer);
            _extraction = new NetExtractionServer(_nm, _table, _skills, _log);

#if !DEVELOPMENT_BUILD && !UNITY_EDITOR
            if (_args.ModularMapProofServer || _args.DoorProofServer || _args.FloorLootProofServer || _args.InventoryDropProofServer || _args.CombatTestFixtureServer)
            {
                _sceneFailed = true;
                _logError("modular/door/floor-loot proof modes are available only in development builds");
                return;
            }
#endif
            _moduleCatalog = Resources.Load<DungeonModuleCatalog>("Emberdelve/DungeonModuleCatalog");
            if (_moduleCatalog == null)
            {
                _sceneFailed = true;
                _logError("required Resources/Emberdelve/DungeonModuleCatalog is missing; server will not load a legacy dungeon");
                return;
            }
            ulong raidSeed = RunContext.ResolveRaidSeed(out string seedSource);
            RunContext.SetNextRaidSeed(raidSeed);
            int mapSeed = unchecked((int)(raidSeed ^ (raidSeed >> 32)));
            if (DungeonModulePlanner.TryCreate(mapSeed, _moduleCatalog.Modules, out _modulePlan, out string reason))
            {
                _modulePlan = _modulePlan.WithRoyaleMode(RunContext.SelectedRoyaleMode);
                string variation = string.Empty;
                if (_args.ModularMapProofServer && !FindDifferentSeedPlan(_modulePlan, out variation))
                {
                    _modulePlan = null;
                    _sceneFailed = true;
                    _logError("[modular-proof] planner seed variation FAILED: " + variation);
                    return;
                }
                DungeonModulePlanSession.Install(_modulePlan);
                _log("modular plan seed=" + mapSeed + " hash=" + _modulePlan.LayoutHash + " raidSeedSource=" + seedSource);
                if (_args.ModularMapProofServer) _log("[modular-proof] planner seed variation PASS (candidate not scene-applied) " + variation);
            }
            else
            {
                _modulePlan = null;
                _sceneFailed = true;
                _logError("required modular catalog cannot make a valid map: " + reason);
            }
        }

        /// <summary>The dev slot a connection was approved or admitted with ("" when unknown): the key of the skill abuse memory.</summary>
        private string SlotOfClient(ulong clientId)
        {
            if (_table.TryGetByClient(clientId, out NetIdentity row) && row != null) return row.Slot ?? "";
            return _approved.TryGetValue(clientId, out Approved info) && info.Slot != null ? info.Slot : "";
        }

        /// <summary>Players / sender bindings / combat entities right now, in the status-line format.</summary>
        public string CountsText()
        {
            int players = _director != null ? _director.Players.Count : 0;
            return "players=" + players + " senders=" + CombatSenders.BoundCount + " registry=" + CombatRegistry.All.Count;
        }

        // ---- scene + readiness -----------------------------------------------------------------

        /// <summary>Called every frame by NetBootstrap.</summary>
        public void Tick(float now)
        {
            if (_nm == null || !_nm.IsListening) return;

            EnsureModuleReadyHandler();

            if (_movement != null) _movement.EnsureSubscribed();
            if (_skills != null) _skills.Tick(now);
            if (_combat != null) _combat.Tick(now);
            if (_inventory != null) _inventory.Tick(now);
            if (_extraction != null) _extraction.Tick(now);
            RetryCorpseDisconnects();

            if (!_sceneRequested && !_sceneFailed) RequestDungeonScene(now);
            PurgeModulePlanWaiters(now);

            if (_director == null && _sceneRequested && !_sceneFailed) CheckReady(now);

            if (_director != null || _sceneFailed)
            {
                FlushPendingApprovals();
                FlushAwaitingSpawns();
            }

            if (_director != null && _director.RoyaleMode.HasValue && now >= _nextRoyaleMapTick)
            {
                _nextRoyaleMapTick = now + .25f;
                if (_royaleMaps == null) _royaleMaps = new NetRoyaleMapServer(_nm, _director,
                    client => _approved.TryGetValue(client, out var info) && info.Connected && info.ModulePlanReady, _log, client => _inventory?.PublishDestinationWorld(client));
                _royaleMaps.Tick(now);
            }
            TickRoyalePopulation(now);
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            if (_royaleMapProof && _initialPopulation != null && _initialPopulation.Started)
            {
                if (!_royaleMapProofPrepared)
                {
                    _royaleMapProofPrepared = true;
                    for (int floor = 2; floor <= 3; floor++)
                    {
                        if (_director.FloorCoordinator.CopyPreparedPlan(floor) == null
                            && !_director.FloorCoordinator.TryPrepareDestination(floor, out string reason))
                            _logError("Royale geometry proof preparation failed floor=" + floor + " reason=" + reason);
                    }
                }
                if (!_royaleMapProofReported && _royaleMaps != null && _royaleMaps.PublicStateReady(2) && _royaleMaps.PublicStateReady(3))
                { _royaleMapProofReported = true; _log("Royale geometry proof BOTH DESTINATIONS ACKNOWLEDGED publicState=true"); }
            }
            TickRoyaleDescentProof();
#endif
            PublishRaidClock(now);

#if DEVELOPMENT_BUILD || UNITY_EDITOR
            if (_args.InventoryContestServer && !_inventoryContestAttempted && _table.Count >= 2 && _director != null)
            {
                _inventoryContestAttempted = true;
                PrepareInventoryContestFixture();
            }
            if (_args.FloorLootProofServer && !_floorLootProofAttempted && _table.Count >= 2 && _director != null)
            {
                _floorLootProofAttempted = true;
                PrepareFloorLootProofFixture();
            }
            if (_args.FloorLootProofServer && _floorLootProofReady) ApplyFloorLootProofAction();
            if (_args.InventoryDropProofServer && !_inventoryDropProofAttempted && _table.Count >= 2 && _director != null)
            {
                _inventoryDropProofAttempted = true;
                PrepareInventoryDropProofFixture();
            }
            if (_args.InventoryDropProofServer && _inventoryDropProofReady && !_inventoryDropProofCommitLogged)
                ObserveInventoryDropProofCommit();
            if (_args.CorpseProofServer && !_corpseProofAttempted && _table.Count >= 2 && _director != null)
            {
                _corpseProofAttempted = true;
                PrepareCorpseProofFixture();
            }
            if (_args.CorpseProofServer && _corpseProofModel != null) ApplyCorpseProofAction();
            if (_args.CorpseDeathProofServer && !_corpseDeathProofAttempted && _table.Count >= 2 && _director != null)
            {
                _corpseDeathProofAttempted = true;
                PrepareCorpseDeathProofFixture();
            }
            if (_args.CorpseDeathProofServer && _corpseDeathProofActor != null) ApplyCorpseDeathProofAction();
            if (_args.CombatEnemyProofServer && !_combatEnemyProofAttempted && _table.Count >= 2 && _director != null)
            {
                _combatEnemyProofAttempted = true;
                PrepareCombatEnemyProofFixture();
            }
            if (_args.CombatEnemyProofServer && _combatEnemyProofTarget != null) TickCombatEnemyPresentationFixture();
            if (_args.ExtractionProofServer && !_extractionProofAttempted && _table.Count >= 2 && _director != null)
            {
                _extractionProofAttempted = true;
                PrepareExtractionProofFixture();
            }
            if (_args.ExtractionProofServer && !_extractionProofStimulusApplied && _extractionProofRig != null)
                ApplyExtractionProofStimulus();
            if (_args.TimeoutProofServer && !_timeoutProofAttempted && _table.Count >= 2 && _director != null)
            {
                _timeoutProofAttempted = true;
                PrepareTimeoutProofFixture();
            }
            if (_args.TimeoutProofServer && !_timeoutProofOutcomeLogged && _timeoutProofActors.Count == 2 && _director != null)
                ObserveTimeoutProofOutcome();
            if (_args.DoorProofServer && !_doorProofAttempted && _table.Count >= 2 && _director != null)
                PrepareDoorProofFixture();
            if (_args.DoorProofServer && _doorProofFixtureReady)
            {
                ApplyDoorProofStage();
                TickDoorAiProof();
            }
#endif


            PurgeStaleReservations(now);

            if (now >= _nextStatusAt)
            {
                _nextStatusAt = now + StatusSeconds;
                _log("status " + CountsText() + " clients=" + _approved.Count + " ready=" + (_director != null));
            }
        }

#if DEVELOPMENT_BUILD || UNITY_EDITOR
        private void PrepareCombatEnemyProofFixture()
        {
            NetIdentity owner = null;
            foreach (NetIdentity row in _table.All)
                if (string.Equals(row.Slot, "ene-a", StringComparison.OrdinalIgnoreCase)) { owner = row; break; }

            EnemyAgent target = null;
            EnemyAgent[] enemies = UnityEngine.Object.FindObjectsByType<EnemyAgent>(FindObjectsSortMode.None);
            float nearest = float.PositiveInfinity;
            if (owner?.Rig != null)
            {
                for (int i = 0; i < enemies.Length; i++)
                {
                    EnemyAgent candidate = enemies[i];
                    if (candidate == null || !candidate.isActiveAndEnabled || candidate.IsDead) continue;
                    float distance = (candidate.transform.position - owner.Rig.transform.position).sqrMagnitude;
                    if (distance >= nearest) continue;
                    nearest = distance;
                    target = candidate;
                }
            }

            if (owner?.Rig?.Movement == null || target == null)
            {
                _logError("combat-enemy-proof fixture unavailable: scripted player or live enemy missing");
                return;
            }

            // This fixture verifies standing Hit/Attack/Death presentation, not the separate dormant encounter.
            // Exercise the normal wake state and complete its delay before holding the target; otherwise the
            // first scripted waist-height swing would aim above a sleeping body and its Hit reaction is suppressed.
            if (target.Brain != null && (target.IsDormant || target.IsWaking))
            {
                target.Brain.NotifyDamaged(new DamageInfo(0f, target.CombatCenter, Vector3.up,
                    owner.Rig.transform.position, 0, EntityKind.Neutral, DamageType.Physical, false, false, 0f));
                target.Brain.Tick(EnemyBrain.WakeSeconds, Time.time, target.transform.position, target.transform.forward, null);
            }

            // Keep the live target from killing the scripted attacker while the proof client exercises the normal
            // AttackPress, movement replication, swing geometry and server hit resolution paths. This is a dev-only
            // fixture; the target remains alive and damageable so the player's hit still resolves through production combat.
            target.SetStunned(true);
            if (!target.IsStunned)
            {
                _logError("combat-enemy-proof fixture could not hold the live target still");
                return;
            }

            _combatEnemyProofTarget = target;
            // Isolate this presentation fixture from unrelated roaming enemies. The selected
            // target resumes normal combat after the owner's real hit below.
            for (int i = 0; i < enemies.Length; i++)
                if (enemies[i] != null && enemies[i] != target && !enemies[i].IsDead)
                    enemies[i].SetStunned(true);
            _combatEnemyProofHealthAtStage = target.CurrentHealth;
            _combatEnemyProofSwingsAtStage = target.SwingsBegun;

            LayerMask blockers = _director.Config != null ? _director.Config.levelMask.value : LayerMask.GetMask(GameConfig.LevelLayerName);
            blockers |= LayerMask.GetMask(GameConfig.InteractLayerName, GameConfig.LootLayerName);
            const float stagingRadius = 1.25f;
            for (int step = 0; step < 24; step++)
            {
                float angle = step * 15f * Mathf.Deg2Rad;
                Vector3 offset = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * stagingRadius;
                Vector3 candidate = target.transform.position + offset + Vector3.up * 0.15f;
                Vector3 eye = candidate + Vector3.up * 1.2f;
                Vector3 enemyCenter = target.transform.position + Vector3.up * 1.15f;
                if (Physics.Linecast(eye, enemyCenter, blockers, QueryTriggerInteraction.Ignore)) continue;

                owner.Rig.Movement.TeleportTo(candidate);
                Physics.SyncTransforms();
                float distance = Vector3.Distance(owner.Rig.transform.position, target.transform.position);
                _log("combat-enemy-proof fixture player=" + owner.PlayerId + " enemy=" + target.CombatId +
                     " distance=" + distance.ToString("F3", CultureInfo.InvariantCulture) +
                     " lineOfSight=True enemyStunned=True placement=server-dev");
                return;
            }

            _logError("combat-enemy-proof fixture found no clear staging point around a live enemy");
            _combatEnemyProofTarget = null;
        }

        private void TickCombatEnemyPresentationFixture()
        {
            EnemyAgent target = _combatEnemyProofTarget;
            if (target == null || target.IsDead) return;

            // Only release the real enemy AI after the normal client attack has damaged this exact staged target.
            if (!_combatEnemyProofReleased && target.CurrentHealth < _combatEnemyProofHealthAtStage)
            {
                target.SetStunned(false);
                _combatEnemyProofReleased = !target.IsStunned;
                if (_combatEnemyProofReleased)
                    _log("combat-enemy-proof action=release-ai enemy=" + target.CombatId + " cause=real-client-health-decrease");
            }

            // Wait for the AI and combat authority to accept a real enemy swing before exercising the server death path.
            if (_combatEnemyProofReleased && _combatEnemyProofSwingAt <= 0f && target.SwingsBegun > _combatEnemyProofSwingsAtStage)
            {
                _combatEnemyProofSwingAt = Time.realtimeSinceStartup;
                _log("combat-enemy-proof action=real-swing-start enemy=" + target.CombatId + " swings=" + target.SwingsBegun);
            }

            if (_combatEnemyProofSwingAt > 0f && !_combatEnemyProofDeathApplied && Time.realtimeSinceStartup - _combatEnemyProofSwingAt >= 2f)
            {
                DamageInfo lethal = new DamageInfo(target.CurrentHealth + 1f, target.CombatCenter, Vector3.up,
                    target.transform.position, 0, EntityKind.Neutral, DamageType.Physical, false, false, 0f);
                target.ApplyDamage(lethal);
                _combatEnemyProofDeathApplied = target.IsDead;
                _log("combat-enemy-proof action=server-death-stimulus enemy=" + target.CombatId + " dead=" + target.IsDead + " afterRealSwing=True delay=2.0");
            }
        }

        private void PrepareInventoryContestFixture()
        {
            NetIdentity a = null, b = null;
            foreach (NetIdentity row in _table.All)
            {
                if (string.Equals(row.Slot, "contest-a", StringComparison.OrdinalIgnoreCase)) a = row;
                else if (string.Equals(row.Slot, "contest-b", StringComparison.OrdinalIgnoreCase)) b = row;
            }
            ItemCatalog catalog = _director.Config != null ? _director.Config.itemCatalog : null;
            if (a?.Rig == null || b?.Rig == null || _director.Chests == null || catalog == null || _director.Session == null)
            {
                _logError("chest-contest fixture unavailable: players, chests, catalog or run session missing");
                return;
            }

            ItemData gear = null;
            for (int i = 0; i < catalog.Items.Count; i++)
                if (catalog.Items[i] != null && catalog.Items[i].IsEquippable) { gear = catalog.Items[i]; break; }
            string reason = gear == null ? "no equippable catalog item" : null;
            if (gear == null || !catalog.TryMintCommon(_director.Session.Issuer, gear, out ItemInstance instance, out reason))
            {
                _logError("chest-contest fixture could not mint item: " + reason);
                return;
            }

            for (int i = 0; i < _director.Chests.Chests.Count; i++)
            {
                ChestInteractable chest = _director.Chests.Chests[i];
                if (chest == null || chest.Variant != ChestVariant.Supply || chest.LootTable == null || chest.IsRolled) continue;
                if (!TryPlaceContestPlayers(chest, a.Rig, b.Rig, out float distanceA, out float distanceB)) continue;
                if (!chest.InstallContestFixtureInstance(instance)) continue;
                _inventory?.PublishChestState(chest);
                if (Array.IndexOf(Environment.GetCommandLineArgs(), "-ember-item-model-review") >= 0)
                    GrantItemModelReviewArmor(a, catalog);
                _log("chest-contest fixture chest=" + chest.ChestId + " uid=" + instance.UidText +
                     " playerA=" + a.PlayerId + " playerB=" + b.PlayerId + " clientA=" + a.ClientId + " clientB=" + b.ClientId +
                     " reach=" + _director.InteractionReach.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) +
                     " canInteractA=True canInteractB=True distanceA=" + distanceA.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) +
                     " distanceB=" + distanceB.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) +
                     " actorA=" + a.Rig.transform.position.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) +
                     " actorB=" + b.Rig.transform.position.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) +
                     " chest=" + chest.transform.position.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
                return;
            }
            _logError("chest-contest fixture unavailable: no Supply chest passed production CanInteract for both server actors");
        }

        // Explicit development visual review only; ordinary contest fixtures keep their original loadouts.
        private void GrantItemModelReviewArmor(NetIdentity owner, ItemCatalog catalog)
        {
            if (owner?.Rig?.Inventory == null) return;
            foreach (string id in new[] { "boiled_cuirass", "graspwrap_gloves", "footpad_boots", "ashen_cloak", "emberthread_mantle" })
            {
                if (!catalog.TryResolveItem(id, out ItemData definition, out string reason) ||
                    !catalog.TryMintCommon(_director.Session.Issuer, definition, out ItemInstance instance, out reason) ||
                    !owner.Rig.Inventory.TryAddInstance(instance, out reason))
                {
                    _logError("item-model-review grant failed item=" + id + " reason=" + reason);
                    return;
                }
                // TryAddInstance raises the existing InventoryChanged event; NetInventoryServer sends the owner snapshot.
                _log("item-model-review granted player=" + owner.PlayerId + " item=" + id + " uid=" + instance.UidText);
            }
            _log("item-model-review ready player=" + owner.PlayerId + " bagRevision=" + owner.Rig.Inventory.Revision +
                " bagCells=" + owner.Rig.Inventory.UsedCells + " equippedByFixture=False");
        }

        private static bool TryPlaceContestPlayers(ChestInteractable chest, PlayerRig a, PlayerRig b,
            out float distanceA, out float distanceB)
        {
            distanceA = distanceB = 0f;
            Vector3 originalA = a.transform.position, originalB = b.transform.position;
            Vector3 center = chest.transform.position;
            Collider collider = chest.GetComponent<Collider>();
            float floorY = collider != null ? collider.bounds.min.y : center.y - chest.BodySize.y * 0.5f;
            const float radius = 1.65f;
            const float side = 0.35f;
            for (int step = 0; step < 24; step++)
            {
                float angle = step * 15f * Mathf.Deg2Rad;
                Vector3 forward = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
                Vector3 right = new Vector3(-forward.z, 0f, forward.x);
                Vector3 basePosition = new Vector3(center.x, floorY + 0.15f, center.z) + forward * radius;
                a.Movement.TeleportTo(basePosition - right * side);
                b.Movement.TeleportTo(basePosition + right * side);
                Physics.SyncTransforms();
                if (!ContestPositionClear(a, floorY) || !ContestPositionClear(b, floorY)) continue;
                if (!chest.CanInteract(a, out _) || !chest.CanInteract(b, out _)) continue;
                Vector3 pointA = collider != null ? collider.ClosestPoint(a.InteractionOrigin) : center;
                Vector3 pointB = collider != null ? collider.ClosestPoint(b.InteractionOrigin) : center;
                distanceA = Vector3.Distance(a.InteractionOrigin, pointA);
                distanceB = Vector3.Distance(b.InteractionOrigin, pointB);
                return true;
            }
            a.Movement.TeleportTo(originalA);
            b.Movement.TeleportTo(originalB);
            return false;
        }

        // Check the grounded controller volume, not just the interaction ray. A teleport inside a
        // pillar can initially pass LOS and then be pushed away when physics advances on a client.
        private static bool ContestPositionClear(PlayerRig rig, float floorY)
        {
            CharacterController controller = rig.GetComponent<CharacterController>();
            if (controller == null) return false;
            int mask = LayerMask.GetMask(GameConfig.LevelLayerName);
            if (mask == 0) return false;
            float radius = controller.radius + 0.02f;
            Vector3 bottom = new Vector3(rig.transform.position.x, floorY + radius + 0.04f, rig.transform.position.z);
            Vector3 top = bottom + Vector3.up * Mathf.Max(0f, controller.height - 2f * radius - 0.08f);
            return !Physics.CheckCapsule(bottom, top, radius, mask, QueryTriggerInteraction.Ignore);
        }

        private void PrepareFloorLootProofFixture()
        {
            foreach (NetIdentity row in _table.All)
            {
                if (string.Equals(row.Slot, "loot-a", StringComparison.OrdinalIgnoreCase)) _floorLootProofA = row;
                else if (string.Equals(row.Slot, "loot-b", StringComparison.OrdinalIgnoreCase)) _floorLootProofB = row;
            }
            ItemCatalog catalog = _director.Config != null ? _director.Config.itemCatalog : null;
            if (_inventory == null || _floorLootProofA?.Rig?.Movement == null || _floorLootProofB?.Rig?.Movement == null ||
                catalog == null || _director.Session == null)
            { _logError("floor-loot-proof fixture unavailable: two admitted rigs, item catalog, or session missing"); return; }

            ItemData gear = null;
            for (int i = 0; i < catalog.Items.Count; i++)
                if (catalog.Items[i] != null && catalog.Items[i].IsEquippable) { gear = catalog.Items[i]; break; }
            string reason = gear == null ? "no equippable catalog item" : null;
            if (gear == null || !catalog.TryMintCommon(_director.Session.Issuer, gear, out ItemInstance contest, out reason) ||
                !catalog.TryMintCommon(_director.Session.Issuer, gear, out ItemInstance dead, out reason))
            { _logError("floor-loot-proof could not mint unique instances: " + reason); return; }

            ChestInteractable stage = null;
            LootPickup contestPickup = null;
            float distanceA = 0f, distanceB = 0f;
            Vector3 originalA = _floorLootProofA.Rig.transform.position;
            Vector3 originalB = _floorLootProofB.Rig.transform.position;
            const float actorRadius = 1.65f;
            const float actorSide = 0.35f;
            float[] pickupRadii = { 1.8f, 2.05f, 2.3f, 2.55f };
            if (_director.Chests != null)
            {
                foreach (ChestInteractable chest in _director.Chests.Chests)
                {
                    if (chest == null || chest.LootTable == null) continue;
                    Collider chestCollider = chest.GetComponent<Collider>();
                    Vector3 center = chest.transform.position;
                    float floorY = chestCollider != null ? chestCollider.bounds.min.y : center.y - chest.BodySize.y * .5f;
                    bool found = false;
                    for (int step = 0; step < 24 && !found; step++)
                    {
                        float angle = step * 15f * Mathf.Deg2Rad;
                        Vector3 forward = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
                        Vector3 right = new Vector3(-forward.z, 0f, forward.x);
                        Vector3 basePosition = new Vector3(center.x, floorY + 0.15f, center.z) + forward * actorRadius;
                        _floorLootProofA.Rig.Movement.TeleportTo(basePosition - right * actorSide);
                        _floorLootProofB.Rig.Movement.TeleportTo(basePosition + right * actorSide);
                        Physics.SyncTransforms();
                        if (!ContestPositionClear(_floorLootProofA.Rig, floorY) || !ContestPositionClear(_floorLootProofB.Rig, floorY)) continue;
                        if (!chest.CanInteract(_floorLootProofA.Rig, out _) || !chest.CanInteract(_floorLootProofB.Rig, out _)) continue;

                        Vector3 actorsMidpoint = (_floorLootProofA.Rig.transform.position + _floorLootProofB.Rig.transform.position) * .5f;
                        for (int radiusIndex = 0; radiusIndex < pickupRadii.Length; radiusIndex++)
                        {
                            Vector3 itemPoint = new Vector3(center.x, floorY + .45f, center.z) + forward * pickupRadii[radiusIndex];
                            if (contestPickup == null) contestPickup = _director.SpawnLootInstance(contest, itemPoint);
                            else contestPickup.PlaceAt(itemPoint);
                            if (contestPickup == null) break;
                            Physics.SyncTransforms();
                            // The pickup may not have a network ID yet. Check the same home-position
                            // geometry that the server will use for the real network requests.
                            bool canA = contestPickup.ValidateNetworkInteraction(_floorLootProofA.Rig, _director.InteractionReach, out _);
                            bool canB = contestPickup.ValidateNetworkInteraction(_floorLootProofB.Rig, _director.InteractionReach, out _);
                            if (!canA || !canB) continue;

                            stage = chest;
                            found = true;
                            Vector3 chestPointA = chestCollider != null ? chestCollider.ClosestPoint(_floorLootProofA.Rig.InteractionOrigin) : center;
                            Vector3 chestPointB = chestCollider != null ? chestCollider.ClosestPoint(_floorLootProofB.Rig.InteractionOrigin) : center;
                            distanceA = Vector3.Distance(_floorLootProofA.Rig.InteractionOrigin, chestPointA);
                            distanceB = Vector3.Distance(_floorLootProofB.Rig.InteractionOrigin, chestPointB);
                            break;
                        }
                    }
                    if (found) break;
                    _floorLootProofA.Rig.Movement.TeleportTo(originalA);
                    _floorLootProofB.Rig.Movement.TeleportTo(originalB);
                    Physics.SyncTransforms();
                }
            }
            if (stage == null || contestPickup == null)
            {
                if (contestPickup != null)
                {
                    contestPickup.gameObject.SetActive(false);
                    UnityEngine.Object.Destroy(contestPickup.gameObject);
                }
                _floorLootProofA.Rig.Movement.TeleportTo(originalA);
                _floorLootProofB.Rig.Movement.TeleportTo(originalB);
                Physics.SyncTransforms();
                _logError("floor-loot-proof fixture could not find a chest approach and pickup point accepted by production CanInteract for both actors");
                return;
            }

            _floorLootProofNearA = _floorLootProofA.Rig.transform.position;
            _floorLootProofNearB = _floorLootProofB.Rig.transform.position;
            Vector3 itemPosition = contestPickup.HomePosition;
            LootPickup deadPickup = _director.SpawnLootInstance(dead, itemPosition + Vector3.right * .4f);
            _floorLootProofContestUid = contest.UidText;
            _floorLootProofDeadUid = dead.UidText;
            if (deadPickup == null ||
                !_inventory.TryGetFloorLootProofId(contest.UidText, out _floorLootProofContestId) ||
                !_inventory.TryGetFloorLootProofId(dead.UidText, out _floorLootProofDeadId))
            {
                contestPickup.gameObject.SetActive(false);
                UnityEngine.Object.Destroy(contestPickup.gameObject);
                if (deadPickup != null) { deadPickup.gameObject.SetActive(false); UnityEngine.Object.Destroy(deadPickup.gameObject); }
                _logError("floor-loot-proof fixture pickups were not admitted to the server registry");
                return;
            }

            _floorLootProofReady = true;
            _log("floor-loot-proof fixture ready item=" + _floorLootProofContestId + " uid=" + _floorLootProofContestUid +
                 " deadItem=" + _floorLootProofDeadId + " deadUid=" + _floorLootProofDeadUid +
                 " playerA=" + _floorLootProofA.PlayerId + " clientA=" + _floorLootProofA.ClientId +
                 " playerB=" + _floorLootProofB.PlayerId + " clientB=" + _floorLootProofB.ClientId +
                 " reach=" + _director.InteractionReach.ToString("F3", CultureInfo.InvariantCulture) +
                 " canInteractA=True canInteractB=True distanceA=" + distanceA.ToString("F3", CultureInfo.InvariantCulture) +
                 " distanceB=" + distanceB.ToString("F3", CultureInfo.InvariantCulture));
        }

        private void PrepareInventoryDropProofFixture()
        {
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            foreach (NetIdentity row in _table.All)
            {
                if (string.Equals(row.Slot, "drop-a", StringComparison.OrdinalIgnoreCase)) _inventoryDropProofOwner = row;
                else if (string.Equals(row.Slot, "drop-b", StringComparison.OrdinalIgnoreCase)) _inventoryDropProofPeer = row;
            }
            ItemCatalog catalog = _director.Config != null ? _director.Config.itemCatalog : null;
            if (_inventoryDropProofOwner?.Rig == null || _inventoryDropProofPeer?.Rig == null || catalog == null ||
                _director.Session == null || string.IsNullOrEmpty(_args.InventoryDropProofExpectedFile))
            { _logError("inventory-drop-proof fixture unavailable: two admitted rigs, catalog, session, or expected snapshot path missing"); return; }

            ItemData definition = null;
            ItemInstance rolled = null;
            string reason = "no catalog main-hand gear definition with a Rare affix roll";
            for (int i = 0; i < catalog.Items.Count; i++)
            {
                ItemData candidate = catalog.Items[i];
                if (candidate == null || !candidate.IsEquippable || candidate.equipSlot != EquipmentSlot.Chest) continue;
                if (!catalog.TryGetAuthority(out ItemInstanceAuthority authority, out reason) ||
                    !LootRoller.TryRoll(candidate, 1, AffixRarity.Rare, authority, new DeterministicRng(0xD09A1107UL),
                        _director.Session.Issuer, out ItemInstance candidateInstance, out reason) ||
                    candidateInstance == null || !candidateInstance.HasAffixes) continue;
                definition = candidate;
                rolled = candidateInstance;
                break;
            }
            _inventoryDropProofItem = rolled;
            if (definition == null || _inventoryDropProofItem == null)
            { _logError("inventory-drop-proof fixture could not roll an affixed chest instance: " + reason); return; }

            Inventory bag = _inventoryDropProofOwner.Rig.Inventory;
            Equipment equipment = _inventoryDropProofOwner.Rig.Equipment;
            if (bag == null || equipment == null || !bag.TryAddInstance(_inventoryDropProofItem, out reason))
            { _logError("inventory-drop-proof fixture could not stage the item in the owner's bag: " + reason); return; }
            int bagIndex = -1;
            for (int i = 0; i < bag.Slots.Count; i++)
                if (bag.InstanceAt(i) != null && bag.InstanceAt(i).Uid == _inventoryDropProofItem.Uid) { bagIndex = i; break; }
            _inventoryDropProofSlot = EquipmentSlot.Chest;
            if (bagIndex < 0 || !equipment.TryEquipFromInventory(bagIndex, _inventoryDropProofSlot, out reason) ||
                equipment.EquippedInstance(_inventoryDropProofSlot)?.Uid != _inventoryDropProofItem.Uid)
            { _logError("inventory-drop-proof fixture could not equip the staged UID: " + reason); return; }

            string expectedPath = Path.GetFullPath(_args.InventoryDropProofExpectedFile);
            Directory.CreateDirectory(Path.GetDirectoryName(expectedPath));
            File.WriteAllText(expectedPath, JsonUtility.ToJson(_inventoryDropProofItem.ToSnapshot()));
            _inventoryDropProofRevision = bag.Revision;
            _director.FailNextGroundDropPublicationForProof = true;
            _inventoryDropProofReady = true;
            _log("inventory-drop-proof fixture ready uid=" + _inventoryDropProofItem.UidText +
                 " item=" + _inventoryDropProofItem.DefinitionId + " rarity=" + _inventoryDropProofItem.RarityValue +
                 " affixes=" + _inventoryDropProofItem.AffixCount + " slot=" + _inventoryDropProofSlot +
                 " owner=" + _inventoryDropProofOwner.PlayerId + " ownerClient=" + _inventoryDropProofOwner.ClientId +
                 " peer=" + _inventoryDropProofPeer.PlayerId + " peerClient=" + _inventoryDropProofPeer.ClientId +
                 " revision=" + _inventoryDropProofOwner.Rig.Inventory.Revision);
#endif
        }

        private void ObserveInventoryDropProofCommit()
        {
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            if (!_inventoryDropProofRollbackLogged && !_director.FailNextGroundDropPublicationForProof &&
                _inventoryDropProofItem != null && _inventoryDropProofOwner?.Rig != null && _inventory != null)
            {
                PlayerRig rig = _inventoryDropProofOwner.Rig;
                ItemInstance retained = rig.Equipment.EquippedInstance(_inventoryDropProofSlot);
                if (retained != null && retained.SameIdentityAs(_inventoryDropProofItem) &&
                    rig.Inventory.Revision == _inventoryDropProofRevision &&
                    !rig.Inventory.HoldsInstanceUid(retained.Uid) && _inventory.CountFloorLootProofUid(retained.UidText) == 0)
                {
                    _inventoryDropProofRollbackLogged = true;
                    _log("inventory-drop-proof rollback uid=" + retained.UidText + " exactInstance=True floorCount=0 revision=" + rig.Inventory.Revision);
                }
            }
            if (_inventoryDropProofItem == null || _inventory == null || _inventoryDropProofOwner?.Rig == null ||
                _inventoryDropProofOwner.Rig.Equipment.HoldsUid(_inventoryDropProofItem.Uid) ||
                _inventoryDropProofOwner.Rig.Inventory.HoldsInstanceUid(_inventoryDropProofItem.Uid)) return;
            if (!_inventory.TryGetFloorLootProofId(_inventoryDropProofItem.UidText, out ulong pickupId) ||
                !_inventory.TryGetFloorLootProofPickup(pickupId, out LootPickup pickup) || pickup.Instance == null ||
                !pickup.Instance.SameIdentityAs(_inventoryDropProofItem) ||
                _inventory.CountFloorLootProofUid(_inventoryDropProofItem.UidText) != 1) return;
            _inventoryDropProofCommitLogged = true;
            _log("inventory-drop-proof committed uid=" + _inventoryDropProofItem.UidText + " pickupId=" + pickupId +
                 " uidCount=1 exactInstance=True");
#endif
        }

        private void ApplyFloorLootProofAction()
        {
            string path = _args.FloorLootProofActionFile;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            string command;
            try { command = ReadSharedText(path).Trim(); }
            catch (IOException) { return; }
            if (string.IsNullOrEmpty(command) || command == _floorLootProofLastAction) return;
            _floorLootProofLastAction = command;
            string stage = command.Split('|')[0];
            Vector3 point = _director != null && _inventory != null ?
                (_floorLootProofContestId != 0 ? GetFloorLootPosition(_floorLootProofContestId) : Vector3.zero) : Vector3.zero;
            if (point == Vector3.zero && _floorLootProofA != null) point = _floorLootProofA.Rig.transform.position;

            if (stage == "far")
            {
                _floorLootProofB.Rig.Movement.TeleportTo(_floorLootProofNearB + Vector3.right * (_director.InteractionReach + 6f));
                _log("floor-loot-proof stage=far player=" + _floorLootProofB.PlayerId + " item=" + _floorLootProofContestId);
            }
            else if (stage == "blocked")
            {
                Vector3 fromPickup = _floorLootProofNearB - point;
                fromPickup.y = 0f;
                if (fromPickup.sqrMagnitude < 0.01f) fromPickup = Vector3.forward;
                fromPickup.Normalize();
                Vector3 interactionOffset = _floorLootProofB.Rig.InteractionOrigin - _floorLootProofB.Rig.transform.position;
                Vector3 desiredOrigin = point + fromPickup * 1.65f;
                _floorLootProofB.Rig.Movement.TeleportTo(desiredOrigin - interactionOffset);
                Physics.SyncTransforms();
                Vector3 origin = _floorLootProofB.Rig.InteractionOrigin;
                Vector3 offset = point - origin;
                if (_floorLootProofBlocker == null)
                {
                    _floorLootProofBlocker = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    _floorLootProofBlocker.name = "FloorLootProofOccluder";
                }
                _floorLootProofBlocker.transform.position = (origin + point) * .5f;
                _floorLootProofBlocker.transform.rotation = Quaternion.LookRotation(offset.normalized, Vector3.up);
                _floorLootProofBlocker.transform.localScale = new Vector3(1.2f, 2.4f, .2f);
                int layer = LayerMask.NameToLayer(GameConfig.LevelLayerName);
                if (layer >= 0) _floorLootProofBlocker.layer = layer;
                Physics.SyncTransforms();
                string blockedReason = "pickup_unavailable";
                bool pickupAvailable = _inventory.TryGetFloorLootProofPickup(_floorLootProofContestId, out LootPickup currentPickup);
                bool sameFixturePickup = pickupAvailable && currentPickup.NetworkLootId == _floorLootProofContestId &&
                    currentPickup.Instance != null && string.Equals(currentPickup.Instance.UidText, _floorLootProofContestUid, StringComparison.Ordinal);
                if (!sameFixturePickup) blockedReason = pickupAvailable ? "fixture_pickup_identity_mismatch" : "pickup_unavailable";
                bool canInteract = sameFixturePickup &&
                    currentPickup.ValidateNetworkInteraction(_floorLootProofB.Rig, _director.InteractionReach, out blockedReason);
                bool blocked = !canInteract && string.Equals(blockedReason, "No line of sight", StringComparison.Ordinal);
                _log("floor-loot-proof stage=blocked player=" + _floorLootProofB.PlayerId + " item=" + _floorLootProofContestId +
                     " uid=" + (currentPickup != null && currentPickup.Instance != null ? currentPickup.Instance.UidText : "-") +
                     " verified=" + blocked + " reason=" + (blockedReason ?? "clear").Replace(' ', '_') +
                     " origin=" + origin.ToString("F3", CultureInfo.InvariantCulture) +
                     " point=" + point.ToString("F3", CultureInfo.InvariantCulture) +
                     " distance=" + Vector3.Distance(origin, point).ToString("F3", CultureInfo.InvariantCulture));
            }
            else if (stage == "near")
            {
                if (_floorLootProofBlocker != null)
                {
                    _floorLootProofBlocker.SetActive(false);
                    Physics.SyncTransforms();
                    UnityEngine.Object.Destroy(_floorLootProofBlocker);
                }
                _floorLootProofBlocker = null;
                _floorLootProofA.Rig.Movement.TeleportTo(_floorLootProofNearA);
                _floorLootProofB.Rig.Movement.TeleportTo(_floorLootProofNearB);
                Physics.SyncTransforms();
                _log("floor-loot-proof stage=near player=" + _floorLootProofB.PlayerId + " item=" + _floorLootProofContestId);
            }
            else if (stage == "dead")
            {
                DamageInfo lethal = new DamageInfo(_floorLootProofB.Rig.Health.CurrentHealth + 1f,
                    _floorLootProofB.Rig.transform.position, Vector3.zero, _floorLootProofB.Rig.transform.position,
                    0, EntityKind.Neutral, DamageType.Physical, false, false, 0f);
                _floorLootProofB.Rig.ApplyDamage(lethal);
                _log("floor-loot-proof stage=dead player=" + _floorLootProofB.PlayerId + " dead=" + _floorLootProofB.Rig.IsDead +
                     " item=" + _floorLootProofDeadId);
            }
        }

        private Vector3 GetFloorLootPosition(ulong pickupId)
        {
            return _inventory != null && _inventory.TryGetFloorLootProofPosition(pickupId, out Vector3 position) ? position : Vector3.zero;
        }

        private void PrepareCorpseProofFixture()
        {
            foreach (NetIdentity row in _table.All)
            {
                if (string.Equals(row.Slot, "corpse-a", StringComparison.OrdinalIgnoreCase)) _corpseProofPlayerA = row;
                else if (string.Equals(row.Slot, "corpse-b", StringComparison.OrdinalIgnoreCase)) _corpseProofPlayerB = row;
            }
            ItemCatalog catalog = _director.Config != null ? _director.Config.itemCatalog : null;
            if (_inventory == null || _corpseProofPlayerA?.Rig == null || _corpseProofPlayerB?.Rig == null ||
                _corpseProofPlayerA.Rig.Movement == null || _corpseProofPlayerB.Rig.Movement == null ||
                catalog == null || _director.Session == null)
            {
                _logError("corpse-proof fixture unavailable: server, two player rigs, catalog or run session missing");
                return;
            }

            ItemData gear = null;
            int smallestFootprint = int.MaxValue;
            for (int i = 0; i < catalog.Items.Count; i++)
            {
                ItemData candidate = catalog.Items[i];
                if (candidate == null || !candidate.IsEquippable) continue;
                Vector2Int footprint = candidate.EffectiveFootprint;
                int area = footprint.x * footprint.y;
                if (area >= smallestFootprint) continue;
                smallestFootprint = area;
                gear = candidate;
            }
            string reason = gear == null ? "no equippable catalog item" : null;
            if (gear == null || !catalog.TryMintCommon(_director.Session.Issuer, gear, out ItemInstance instance, out reason))
            {
                _logError("corpse-proof fixture could not mint unique test gear: " + reason);
                return;
            }

            PlayerRig actorA = _corpseProofPlayerA.Rig;
            bool placed = false;
            Vector3 origin = actorA.InteractionOrigin;
            LayerMask levelMask = LayerMask.GetMask(GameConfig.LevelLayerName);
            for (int step = 0; step < 24; step++)
            {
                float angle = step * 15f * Mathf.Deg2Rad;
                Vector3 direction = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
                Vector3 corpsePosition = origin + direction * 1.2f;
                if (!_director.IsInteractionActorAdmitted(actorA, out _)) continue;
                if (levelMask != 0 && Physics.Raycast(origin, direction, out _, 1.08f, levelMask, QueryTriggerInteraction.Ignore)) continue;
                _corpseProofApproach = direction;

                GameObject source = new GameObject("CorpseProof_SourceLoadout");
                Inventory bag = source.AddComponent<Inventory>();
                Equipment equipment = source.AddComponent<Equipment>();
                bag.SetCatalog(catalog);
                bag.SetIssuer(_director.Session.Issuer);
                bag.ConfigureGrid(actorA.Inventory.GridWidth, actorA.Inventory.GridHeight);
                // ConfigureGrid is intentionally a no-op when the requested dimensions already match
                // the component defaults. A freshly added Inventory has a zero-filled cell map until
                // its first Recompute, which otherwise looks occupied to CanPlace despite FreeCells=Capacity.
                bag.Clear();
                if (!bag.TryAddInstance(instance, out reason) ||
                    !CorpseLootManifest.TryCapture(int.MaxValue, corpsePosition, bag, equipment,
                        out CorpseLootManifest manifest, out reason) ||
                    !CorpseContainerModel.TryCreate(manifest, out _corpseProofModel, out reason))
                {
                    UnityEngine.Object.Destroy(source);
                    _logError("corpse-proof fixture could not build a valid server model: " + reason);
                    return;
                }
                UnityEngine.Object.Destroy(source);
                // Do not register a corpse until both players have a clear, distinct and
                // production-valid approach to it on this particular modular layout.
                if (!PositionCorpseProofANear() || !PositionCorpseProofBNear())
                {
                    _corpseProofModel = null;
                    continue;
                }
                PositionCorpseProofB(false);
                if (!_inventory.RegisterCorpse(_corpseProofModel, out _corpseProofCorpseId, out reason))
                {
                    _logError("corpse-proof fixture registration failed: " + reason);
                    _corpseProofModel = null;
                    return;
                }
                if (_corpseProofPlayerA.ClientId == NetworkManager.ServerClientId && NetRole.Current == NetRoleKind.Host)
                {
                    _corpseProofHostPresenter = _corpseProofPlayerA.Object.GetComponent<CorpseLootWorldPresenter>();
                    if (_corpseProofHostPresenter == null)
                    {
                        _logError("corpse-proof host fixture has no listen-host corpse presenter");
                        _corpseProofModel = null;
                        return;
                    }
                    _corpseProofHostPresenter.ProofStateObserved += OnCorpseProofHostState;
                    _corpseProofHostPresenter.ProofResultObserved += OnCorpseProofHostResult;
                    _corpseProofHostPresenter.ProofPresenceObserved += OnCorpseProofHostPresence;
                    NetPlayerRig hostView = _corpseProofPlayerA.Object.GetComponent<NetPlayerRig>();
                    _log("corpse-proof-host presenter=ready role=" + (hostView != null ? hostView.Role.ToString() : "missing"));
                }
                placed = true;
                break;
            }

            if (!placed || !_corpseProofModel.TryGetEntry(1, out CorpseItemView view))
            {
                _logError("corpse-proof fixture could not find a clear nearby staging point or item row");
                _corpseProofModel = null;
                return;
            }

            if (!_inventory.ValidateCorpseActorForProof(_corpseProofPlayerA, _corpseProofModel, out reason))
            {
                _logError("corpse-proof fixture A failed the production spatial check: " + reason);
                return;
            }
            float distanceA = Vector3.Distance(actorA.InteractionOrigin, _corpseProofModel.WorldPosition);
            float distanceB = Vector3.Distance(_corpseProofPlayerB.Rig.InteractionOrigin, _corpseProofModel.WorldPosition);
            bool canB = _inventory.ValidateCorpseActorForProof(_corpseProofPlayerB, _corpseProofModel, out string reasonB);
            if (canB || reasonB != "Too far away")
            {
                _logError("corpse-proof fixture B must fail the production check for range, got: " + (reasonB ?? "authorized"));
                return;
            }
            _log("corpse-proof fixture id=" + _corpseProofCorpseId + " uid=" + view.Uid + " entry=" + view.EntryId +
                 " revision=" + _corpseProofModel.Revision + " playerA=" + _corpseProofPlayerA.PlayerId + " clientA=" + _corpseProofPlayerA.ClientId +
                 " playerB=" + _corpseProofPlayerB.PlayerId + " clientB=" + _corpseProofPlayerB.ClientId +
                 " reach=" + _director.InteractionReach.ToString("F3", CultureInfo.InvariantCulture) +
                 " canA=True canB=" + canB + " distanceA=" + distanceA.ToString("F3", CultureInfo.InvariantCulture) +
                 " distanceB=" + distanceB.ToString("F3", CultureInfo.InvariantCulture) + " stage=far");
        }

        private void PrepareCorpseDeathProofFixture()
        {
            foreach (NetIdentity row in _table.All)
            {
                if (string.Equals(row.Slot, "corpse-death-a", StringComparison.OrdinalIgnoreCase)) _corpseDeathProofActor = row;
                else if (string.Equals(row.Slot, "corpse-death-b", StringComparison.OrdinalIgnoreCase)) _corpseDeathProofDisconnectActor = row;
            }
            PlayerRig rig = _corpseDeathProofActor?.Rig;
            PlayerRig disconnectRig = _corpseDeathProofDisconnectActor?.Rig;
            ItemCatalog catalog = _director.Config != null ? _director.Config.itemCatalog : null;
            if (rig == null || rig.Inventory == null || disconnectRig == null || disconnectRig.Inventory == null || catalog == null || _director.Session == null)
            {
                _logError("corpse-death-proof fixture unavailable: both admitted rigs, bags, catalog or run issuer missing");
                return;
            }
            ItemData gear = null;
            int areaBest = int.MaxValue;
            for (int i = 0; i < catalog.Items.Count; i++)
            {
                ItemData candidate = catalog.Items[i];
                if (candidate == null || !candidate.IsEquippable) continue;
                Vector2Int footprint = candidate.EffectiveFootprint;
                int area = footprint.x * footprint.y;
                if (area >= areaBest) continue;
                gear = candidate;
                areaBest = area;
            }
            string reason = gear == null ? "no equippable catalog item" : null;
            if (gear == null || !catalog.TryMintCommon(_director.Session.Issuer, gear, out ItemInstance item, out reason) ||
                !rig.Inventory.TryAddInstance(item, out reason))
            {
                _logError("corpse-death-proof fixture could not install a unique carried item: " + reason);
                return;
            }
            _corpseDeathProofUid = item.UidText;
            if (!catalog.TryMintCommon(_director.Session.Issuer, gear, out ItemInstance disconnectItem, out reason) ||
                !disconnectRig.Inventory.TryAddInstance(disconnectItem, out reason))
            {
                _logError("corpse-death-proof fixture could not install a second unique disconnect UID: " + reason);
                return;
            }
            _corpseDeathProofDisconnectUid = disconnectItem.UidText;
            foreach (NetIdentity row in _table.All)
            {
                if (ReferenceEquals(row, _corpseDeathProofActor) || row.Rig == null || row.Rig.Movement == null) continue;
                Vector3 near = rig.transform.position + rig.transform.forward * 1.1f;
                row.Rig.Movement.TeleportTo(near);
                break;
            }
            _log("corpse-death-proof fixture ready player=" + rig.PlayerId + " client=" + _corpseDeathProofActor.ClientId + " uid=" + _corpseDeathProofUid +
                 " disconnectPlayer=" + disconnectRig.PlayerId + " disconnectClient=" + _corpseDeathProofDisconnectActor.ClientId +
                 " disconnectUid=" + _corpseDeathProofDisconnectUid);
        }

        private void ApplyCorpseDeathProofAction()
        {
            string path = _args.CorpseDeathProofActionFile;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
            string action;
            try { action = ReadSharedText(path).Trim().ToLowerInvariant(); }
            catch (IOException) { return; }
            if (action == "refuse" && !_corpseDeathProofStimulusApplied)
            {
                _corpseDeathProofStimulusApplied = true;
                _inventory?.SetDeathCustodyRefusalForProof(true);
                PlayerRig rig = _corpseDeathProofActor.Rig;
                Vector3 source = rig.transform.position + Vector3.back;
                var damage = new DamageInfo(rig.Health.CurrentHealth + 1f, rig.CombatCenter,
                    (source - rig.CombatCenter).normalized, source, rig.CombatId, EntityKind.RemotePlayer,
                    DamageType.Physical, false, false, 0f);
                rig.ApplyDamage(damage);
                bool held = LoadoutHoldsUid(rig, _corpseDeathProofUid);
                _log("corpse-death-proof stage=refused player=" + rig.PlayerId + " dead=" + rig.Health.IsDead +
                     " uidHeldOnSource=" + held + " resultPublished=" + FindSession(rig.PlayerId)?.HasResult + " corpseFound=" +
                     _inventory.TryFindCorpseUidForProof(_corpseDeathProofUid, out _));
                return;
            }
            if (action == "retry" && _corpseDeathProofStimulusApplied)
                _inventory?.SetDeathCustodyRefusalForProof(false);
            if (action == "disconnect-refuse" && _corpseDeathProofStimulusApplied)
            {
                _inventory?.SetDeathCustodyRefusalForProof(true);
                if (!_corpseDeathProofDisconnectRefusalArmed)
                {
                    _corpseDeathProofDisconnectRefusalArmed = true;
                    _log("corpse-death-proof disconnect stage=refusal-armed");
                }
            }
            if (action == "disconnect-retry" && _corpseDeathProofStimulusApplied)
                _inventory?.SetDeathCustodyRefusalForProof(false);

            if (!_corpseDeathProofResultLogged && _corpseDeathProofStimulusApplied && action == "retry" &&
                _inventory != null && _inventory.TryFindCorpseUidForProof(_corpseDeathProofUid, out ulong corpseId))
            {
                PlayerRig rig = _corpseDeathProofActor.Rig;
                bool sourceEmpty = !LoadoutHoldsUid(rig, _corpseDeathProofUid);
                bool resultPublished = FindSession(rig.PlayerId)?.HasResult == true;
                if (!sourceEmpty || !resultPublished) return;
                _corpseDeathProofResultLogged = true;
                _log("corpse-death-proof stage=committed player=" + rig.PlayerId + " corpse=" + corpseId + " uid=" + _corpseDeathProofUid +
                     " sourceEmpty=True resultPublished=True");
            }
        }

        private PlayerSession FindSession(int playerId)
        {
            if (_director == null) return null;
            for (int i = 0; i < _director.Players.Count; i++)
                if (_director.Players[i].PlayerId == playerId) return _director.Players[i];
            return null;
        }

        private static bool LoadoutHoldsUid(PlayerRig rig, string uid)
        {
            if (rig == null || string.IsNullOrEmpty(uid)) return false;
            LoadoutSnapshot snapshot = LoadoutSnapshot.Capture(rig.Inventory, rig.Equipment);
            if (snapshot == null) return false;
            foreach (ContainerSnapshot container in new[] { snapshot.bag, snapshot.equipment })
                if (container?.entries != null)
                    for (int i = 0; i < container.entries.Length; i++)
                        if (container.entries[i]?.item != null && container.entries[i].item.uid == uid) return true;
            return false;
        }

        // Bounded extension of the existing opt-in corpse fixture for graphical acceptance.
        // It stages sources via production chest/death/drop paths; clicks still use the review input bridge.
        private void ApplyLootUiReview(string action)
        {
            PlayerRig actor = _corpseProofPlayerA?.Rig;
            PlayerRig other = _corpseProofPlayerB?.Rig;
            if (actor == null || other == null || _inventory == null) return;
            actor.GetComponent<CorpseLootWorldPresenter>()?.CloseContainerUi();
            if (action == "loot-ui-chest" || action == "loot-ui-chest-client")
            {
                foreach (var chest in UnityEngine.Object.FindObjectsByType<ChestInteractable>(FindObjectsSortMode.None))
                    if (chest.Variant == ChestVariant.Supply && !chest.IsEmpty && TryPlaceContestPlayers(chest,actor,other,out _,out _))
                    { _log("loot-ui chest opened=" + chest.Open(action == "loot-ui-chest-client" ? other : actor)); return; }
            }
            else if (action == "loot-ui-enemy")
            {
                foreach (EnemyData enemy in Resources.FindObjectsOfTypeAll<EnemyData>())
                    if (enemy.dropTable != null)
                    {
                        _director.DropEnemyLoot(enemy, "loot-ui-review-enemy", actor.transform.position + Vector3.forward * .7f);
                        _log("loot-ui enemy opened=" + _inventory.OpenNewestLootForReview(actor,"enemy")); return;
                    }
            }
            else if (action == "loot-ui-player")
            {
                other.Movement.TeleportTo(actor.transform.position + Vector3.forward * .7f);
                Physics.SyncTransforms();
                other.ApplyDamage(new DamageInfo(other.Health.CurrentHealth + 1000,other.CombatCenter,Vector3.forward,
                    actor.transform.position,actor.CombatId,EntityKind.RemotePlayer,DamageType.Physical,false,false,0));
                _log("loot-ui player dead=" + other.IsDead + " opened=" + _inventory.OpenNewestLootForReview(actor,"player"));
            }
        }

        private void ApplyCorpseProofAction()
        {
            string path = _args.CorpseProofActionFile;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
            string action;
            try { action = ReadSharedText(path).Trim().ToLowerInvariant(); }
            catch (IOException) { return; }
            if (action.StartsWith("loot-ui-", StringComparison.Ordinal))
            {
                if (action == _corpseProofLastAction) return;
                _corpseProofLastAction = action;
                ApplyLootUiReview(action);
                return;
            }
            if (action.StartsWith("host-", StringComparison.Ordinal))
            {
                if (_corpseProofPlayerA?.ClientId != NetworkManager.ServerClientId)
                {
                    _logError("corpse-proof host action requested without host-owned corpse-proof player A");
                    return;
                }
                if (action == _corpseProofLastAction) return;
                _corpseProofLastAction = action;
                ApplyCorpseProofHostAction(action);
                return;
            }
            if (action != "far" && action != "blocked" && action != "near") return;
            if (action == _corpseProofLastAction) return;
            _corpseProofLastAction = action;

            if (_corpseProofBlocker != null)
            {
                _corpseProofBlocker.SetActive(false);
                UnityEngine.Object.Destroy(_corpseProofBlocker);
                _corpseProofBlocker = null;
            }
            if (action == "far")
            {
                Vector3 root = _corpseProofModel.WorldPosition + Vector3.right * (_director.InteractionReach + 8f) - Vector3.up;
                _corpseProofPlayerB.Rig.Movement.TeleportTo(root);
            }
            else
            {
                if (action == "near" && !PositionCorpseProofANear())
                {
                    _logError("corpse-proof fixture could not stage A inside reach with clear line of sight");
                    return;
                }
                if (!PositionCorpseProofBNear())
                {
                    _logError("corpse-proof fixture could not stage B inside reach with clear line of sight");
                    return;
                }
                if (action == "blocked")
                {
                    Vector3 from = _corpseProofPlayerB.Rig.InteractionOrigin;
                    Vector3 to = _corpseProofModel.WorldPosition;
                    Vector3 ray = to - from;
                    int levelLayer = LayerMask.NameToLayer(GameConfig.LevelLayerName);
                    if (levelLayer >= 0 && ray.sqrMagnitude > 0.04f)
                    {
                        _corpseProofBlocker = GameObject.CreatePrimitive(PrimitiveType.Cube);
                        _corpseProofBlocker.name = "CorpseProof_LOSBlocker";
                        _corpseProofBlocker.layer = levelLayer;
                        _corpseProofBlocker.transform.position = from + ray * 0.5f;
                        _corpseProofBlocker.transform.rotation = Quaternion.LookRotation(ray.normalized);
                        _corpseProofBlocker.transform.localScale = new Vector3(0.7f, 0.7f, 0.35f);
                    }
                }
            }
            Physics.SyncTransforms();
            bool canInteract = _inventory.ValidateCorpseActorForProof(_corpseProofPlayerB, _corpseProofModel, out string reason);
            float distance = Vector3.Distance(_corpseProofPlayerB.Rig.InteractionOrigin, _corpseProofModel.WorldPosition);
            bool lineOfSight = reason != "No line of sight";
            _log("corpse-proof stage=" + action + " playerB=" + _corpseProofPlayerB.PlayerId +
                 " canInteract=" + canInteract + " distance=" + distance.ToString("F3", CultureInfo.InvariantCulture) +
                 " lineOfSight=" + lineOfSight + " reason=" + (reason ?? "none").Replace(' ', '_'));
        }

        private void PositionCorpseProofB(bool near)
        {
            if (_corpseProofPlayerB?.Rig?.Movement == null || _corpseProofModel == null) return;
            if (near) { PositionCorpseProofBNear(); return; }
            Vector3 root = _corpseProofModel.WorldPosition + Vector3.right * (_director.InteractionReach + 8f) - Vector3.up;
            _corpseProofPlayerB.Rig.Movement.TeleportTo(root);
            Physics.SyncTransforms();
        }

        private bool PositionCorpseProofBNear()
        {
            Vector3 center = _corpseProofModel.WorldPosition;
            for (int step = 0; step < 24; step++)
            {
                float angle = step * 15f * Mathf.Deg2Rad;
                Vector3 side = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
                _corpseProofPlayerB.Rig.Movement.TeleportTo(center - side * 1.45f - Vector3.up);
                Physics.SyncTransforms();
                Vector3 offset = _corpseProofPlayerB.Rig.transform.position - _corpseProofPlayerA.Rig.transform.position;
                offset.y = 0f;
                if (offset.sqrMagnitude < 0.8f * 0.8f ||
                    !ContestPositionClear(_corpseProofPlayerB.Rig, center.y - 1f)) continue;
                if (!_inventory.ValidateCorpseActorForProof(_corpseProofPlayerB, _corpseProofModel, out _)) continue;
                _corpseProofBApproach = side;
                return true;
            }
            return false;
        }

        private bool PositionCorpseProofANear()
        {
            Vector3 center = _corpseProofModel.WorldPosition;
            for (int step = 0; step < 24; step++)
            {
                float angle = step * 15f * Mathf.Deg2Rad;
                Vector3 side = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
                _corpseProofPlayerA.Rig.Movement.TeleportTo(center - side * 1.45f - Vector3.up);
                Physics.SyncTransforms();
                if (ContestPositionClear(_corpseProofPlayerA.Rig, center.y - 1f) &&
                    _inventory.ValidateCorpseActorForProof(_corpseProofPlayerA, _corpseProofModel, out _)) return true;
            }
            return false;
        }

        private void ApplyCorpseProofHostAction(string action)
        {
            if (_corpseProofHostPresenter == null || _corpseProofModel == null || _corpseProofPlayerA?.Rig == null)
            {
                _logError("corpse-proof host action unavailable: presenter, model or host rig missing");
                return;
            }

            if (_corpseProofBlocker != null)
            {
                _corpseProofBlocker.SetActive(false);
                UnityEngine.Object.Destroy(_corpseProofBlocker);
                _corpseProofBlocker = null;
            }

            PlayerRig hostRig = _corpseProofPlayerA.Rig;
            if (action == "host-far")
            {
                Vector3 root = _corpseProofModel.WorldPosition + Vector3.right * (_director.InteractionReach + 8f) - Vector3.up;
                hostRig.Movement.TeleportTo(root);
            }
            else if (action == "host-blocked" || action == "host-open")
            {
                if (!PositionCorpseProofHostNear())
                {
                    _logError("corpse-proof host could not reach a clear nearby staging point");
                    return;
                }

                if (action == "host-blocked")
                {
                    Vector3 from = hostRig.InteractionOrigin;
                    Vector3 to = _corpseProofModel.WorldPosition;
                    Vector3 ray = to - from;
                    int levelLayer = LayerMask.NameToLayer(GameConfig.LevelLayerName);
                    if (levelLayer < 0 || ray.sqrMagnitude <= 0.04f)
                    {
                        _logError("corpse-proof host could not create a level-layer LOS blocker");
                        return;
                    }
                    _corpseProofBlocker = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    _corpseProofBlocker.name = "CorpseProof_HostLOSBlocker";
                    _corpseProofBlocker.layer = levelLayer;
                    _corpseProofBlocker.transform.position = from + ray * 0.5f;
                    _corpseProofBlocker.transform.rotation = Quaternion.LookRotation(ray.normalized);
                    _corpseProofBlocker.transform.localScale = new Vector3(0.7f, 0.7f, 0.35f);
                }
            }
            else if (action != "host-stale" && action != "host-transfer")
            {
                return;
            }

            Physics.SyncTransforms();
            if (action == "host-stale" || action == "host-transfer")
            {
                SendCorpseProofHostTransfer(action == "host-stale");
                return;
            }

            bool allowed = _inventory.ValidateCorpseActorForProof(_corpseProofPlayerA, _corpseProofModel,
                out string reason);
            float distance = Vector3.Distance(hostRig.InteractionOrigin, _corpseProofModel.WorldPosition);
            _log("corpse-proof-host stage=" + action.Substring("host-".Length) + " canInteract=" + allowed +
                 " distance=" + distance.ToString("F3", CultureInfo.InvariantCulture) +
                 " reason=" + (reason ?? "none").Replace(' ', '_'));

            bool queued = _corpseProofHostPresenter.RequestState(_corpseProofCorpseId, null, out uint sequence);
            _log("corpse-proof-host request=state stage=" + action.Substring("host-".Length) +
                 " seq=" + sequence + " queued=" + queued);
        }

        private bool PositionCorpseProofHostNear()
        {
            Vector3 center = _corpseProofModel.WorldPosition;
            for (int step = 0; step < 24; step++)
            {
                float angle = step * 15f * Mathf.Deg2Rad;
                Vector3 side = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
                _corpseProofPlayerA.Rig.Movement.TeleportTo(center - side * 1.45f - Vector3.up);
                Physics.SyncTransforms();
                if (!ContestPositionClear(_corpseProofPlayerA.Rig, center.y - 1f)) continue;
                if (!_inventory.ValidateCorpseActorForProof(_corpseProofPlayerA, _corpseProofModel, out _)) continue;
                _corpseProofApproach = side;
                return true;
            }
            return false;
        }

        private void SendCorpseProofHostTransfer(bool stale)
        {
            CorpseContainerDocument document = _corpseProofHostDocument;
            if (document == null || document.corpseId != _corpseProofCorpseId ||
                document.entries == null || document.entries.Length != 1 || document.entries[0]?.item == null)
            {
                _logError("corpse-proof host transfer has no current one-row state");
                return;
            }

            ulong bagRevision = _corpseProofPlayerA.Rig.Inventory.Revision;
            ulong expectedBagRevision = stale ? bagRevision + 1 : bagRevision;
            var request = new CorpseTransferUiRequest(document.corpseId, document.entries[0].entryId, 1,
                document.revision, expectedBagRevision);
            bool queued = _corpseProofHostPresenter.RequestTransfer(request, null, out uint sequence);
            _log("corpse-proof-host request=transfer stage=" + (stale ? "stale" : "commit") +
                 " corpse=" + request.CorpseId + " entry=" + request.EntryId +
                 " uid=" + document.entries[0].item.uid + " corpseRevision=" + request.CorpseRevision +
                 " bagRevision=" + request.BagRevision + " seq=" + sequence + " queued=" + queued);
        }

        private void OnCorpseProofHostState(CorpseContainerDocument document)
        {
            if (document == null || document.corpseId != _corpseProofCorpseId) return;
            _corpseProofHostDocument = document;
            ulong entry = document.entries != null && document.entries.Length > 0 ? document.entries[0].entryId : 0;
            string uid = document.entries != null && document.entries.Length > 0
                ? document.entries[0].item?.uid ?? string.Empty : string.Empty;
            _log("corpse-proof-host state corpse=" + document.corpseId + " revision=" + document.revision +
                 " entries=" + (document.entries != null ? document.entries.Length : -1) +
                 " entry=" + entry + " uid=" + uid);
        }

        private void OnCorpseProofHostResult(ulong corpseId, uint sequence, bool accepted,
            ulong corpseRevision, string reason)
        {
            if (corpseId != _corpseProofCorpseId) return;
            bool held = false;
            string uid = _corpseProofHostDocument?.entries != null && _corpseProofHostDocument.entries.Length > 0
                ? _corpseProofHostDocument.entries[0].item?.uid ?? string.Empty : string.Empty;
            if (Guid.TryParseExact(uid, "N", out Guid parsed) && _corpseProofPlayerA?.Rig?.Inventory != null)
                held = _corpseProofPlayerA.Rig.Inventory.HoldsInstanceUid(parsed);
            _log("corpse-proof-host result corpse=" + corpseId + " seq=" + sequence +
                 " accepted=" + accepted + " corpseRevision=" + corpseRevision +
                 " bagRevision=" + (_corpseProofPlayerA?.Rig?.Inventory?.Revision ?? 0) +
                 " held=" + held + " reason=" + (string.IsNullOrEmpty(reason) ? "none" : reason.Replace(' ', '_')));
        }

        private void OnCorpseProofHostPresence(ulong corpseId, ulong revision, bool removed)
        {
            if (corpseId == _corpseProofCorpseId)
                _log("corpse-proof-host presence corpse=" + corpseId + " revision=" + revision + " removed=" + removed);
        }

        private void PrepareDoorProofFixture()
        {
            foreach (NetIdentity row in _table.All)
            {
                if (string.Equals(row.Slot, "door-a", StringComparison.OrdinalIgnoreCase)) _doorProofActorA = row;
                else if (string.Equals(row.Slot, "door-b", StringComparison.OrdinalIgnoreCase)) _doorProofActorB = row;
            }
            if (_doorProofActorA?.Rig?.Movement == null || _doorProofActorB?.Rig?.Movement == null) return;
            DungeonDoorInteractable[] candidates = UnityEngine.Object.FindObjectsByType<DungeonDoorInteractable>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            if (candidates.Length == 0) return;
            Array.Sort(candidates, (a, b) => string.CompareOrdinal(a != null ? a.DoorId : string.Empty, b != null ? b.DoorId : string.Empty));
            _doorProofDoor = candidates[0];
            if (_doorProofDoor == null || !_doorProofDoor.ApplyReplicatedState(_doorProofDoor.DoorId, false))
            { _doorProofAttempted = true; _logError("door-proof fixture could not initialize a closed door"); return; }
            if (!TryPlaceDoorProofActor(_doorProofActorA, _doorProofDoor.transform.forward, 0f, out _doorProofNearPosition) ||
                !TryPlaceDoorProofActor(_doorProofActorB, -_doorProofDoor.transform.forward, 0f, out _))
            { _doorProofAttempted = true; _logError("door-proof fixture could not place both actors within production door reach/LOS"); return; }
            _doorProofApproach = (_doorProofNearPosition - _doorProofDoor.transform.position).normalized;
            _doorProofAttempted = true;
            _doorProofFixtureReady = true;
            _log("door-proof fixture id=" + _doorProofDoor.DoorId + " clientA=" + _doorProofActorA.ClientId +
                 " playerA=" + _doorProofActorA.PlayerId + " clientB=" + _doorProofActorB.ClientId +
                 " playerB=" + _doorProofActorB.PlayerId + " canA=True canB=True open=" + _doorProofDoor.IsOpen +
                 " blocker=" + _doorProofDoor.BlockerEnabled + " revision=" + (_inventory != null ? _inventory.DoorRevision : 0));
        }

        private bool TryPlaceDoorProofActor(NetIdentity actor, Vector3 preferredDirection, float lateral, out Vector3 placed, bool requireLineOfSightRay = false)
        {
            placed = Vector3.zero;
            if (_doorProofDoor == null || actor?.Rig?.Movement == null) return false;
            Vector3 forward = preferredDirection;
            forward.y = 0f;
            forward = forward.sqrMagnitude > 0.01f ? forward.normalized : Vector3.forward;
            Vector3 right = Vector3.Cross(Vector3.up, forward).normalized;
            Collider panel = _doorProofDoor.BlockerCollider;
            float y = panel != null ? panel.bounds.min.y + 0.15f : _doorProofDoor.transform.position.y + 0.15f;
            float[] radii = { 0.8f, 1.1f, 1.4f, 1.7f, 2.0f, 2.3f };
            Vector3[] directions = { forward, -forward };
            for (int d = 0; d < directions.Length; d++)
                for (int i = 0; i < radii.Length; i++)
                {
                    Vector3 candidate = _doorProofDoor.transform.position + directions[d] * radii[i] + right * lateral;
                    candidate.y = y;
                    actor.Rig.Movement.TeleportTo(candidate);
                    Physics.SyncTransforms();
                    if (requireLineOfSightRay && Vector3.Distance(actor.Rig.InteractionOrigin,
                        DoorProofInteractionPoint(actor.Rig.InteractionOrigin)) <= 1.1f) continue;
                    if (_doorProofDoor.CanInteract(actor.Rig, out _)) { placed = candidate; return true; }
                }
            return false;
        }

        private void ApplyDoorProofStage()
        {
            string path = _args.DoorProofServerActionFile;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
            string stage;
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream, Encoding.UTF8)) stage = reader.ReadToEnd().Trim().ToLowerInvariant();
            }
            catch (IOException) { return; }
            if (stage == _doorProofLastStage || (stage != "far" && stage != "los" && stage != "near" && stage != "ai" &&
                stage != "secret-far" && stage != "secret-near")) return;
            _doorProofLastStage = stage;
            if (stage == "secret-far" || stage == "secret-near")
            {
                ApplySecretDoorProofStage(stage);
                return;
            }
            if (stage == "ai")
            {
                StartDoorAiProof();
                return;
            }
            if (stage == "far")
            {
                if (_doorProofOccluder != null) _doorProofOccluder.SetActive(false);
                _doorProofActorA.Rig.Movement.TeleportTo(_doorProofDoor.transform.position + _doorProofApproach * 10f);
            }
            else
            {
                if (!TryPlaceDoorProofActor(_doorProofActorA, _doorProofApproach, 0f, out _doorProofNearPosition, stage == "los"))
                { _logError("door-proof stage could not restore actor A to a reachable position"); return; }
                if (stage == "los")
                {
                    if (_doorProofOccluder == null)
                    {
                        _doorProofOccluder = GameObject.CreatePrimitive(PrimitiveType.Cube);
                        _doorProofOccluder.name = "DoorProofOccluder";
                        _doorProofOccluder.layer = LayerMask.NameToLayer(GameConfig.LevelLayerName);
                    }
                    _doorProofOccluder.SetActive(true);
                    Vector3 origin = _doorProofActorA.Rig.InteractionOrigin;
                    Vector3 destination = DoorProofInteractionPoint(origin);
                    Vector3 direction = destination - origin;
                    _doorProofOccluder.transform.position = Vector3.Lerp(origin, destination, 0.5f);
                    _doorProofOccluder.transform.rotation = Quaternion.LookRotation(direction.normalized, Vector3.up);
                    _doorProofOccluder.transform.localScale = new Vector3(1.2f, 2.8f, 0.25f);
                }
                else if (_doorProofOccluder != null) _doorProofOccluder.SetActive(false);
            }
            Physics.SyncTransforms();
            bool can = _doorProofDoor.CanInteract(_doorProofActorA.Rig, out string reason);
            float rayDistance = Vector3.Distance(_doorProofActorA.Rig.InteractionOrigin,
                DoorProofInteractionPoint(_doorProofActorA.Rig.InteractionOrigin));
            _log("door-proof staged stage=" + stage + " id=" + _doorProofDoor.DoorId + " canInteract=" + can +
                 " reason=" + (string.IsNullOrEmpty(reason) ? "-" : reason.Replace(' ', '_')) + " open=" +
                 _doorProofDoor.IsOpen + " revision=" + (_inventory != null ? _inventory.DoorRevision : 0) +
                 " rayDistance=" + rayDistance.ToString("F3", CultureInfo.InvariantCulture));
        }

        private void ApplySecretDoorProofStage(string stage)
        {
            if (_inventory == null || _doorProofActorA?.Rig?.Movement == null) return;
            if (_doorProofOccluder != null) _doorProofOccluder.SetActive(false);
            if (_doorProofAiEnemy != null) _doorProofAiEnemy.gameObject.SetActive(false);
            _doorProofAiRunning = false;
            if (stage == "secret-far")
            {
                var candidates = UnityEngine.Object.FindObjectsByType<DungeonDoorInteractable>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
                Array.Sort(candidates, (a, b) => string.CompareOrdinal(a.DoorId, b.DoorId));
                _doorProofDoor = Array.Find(candidates, door => door.IsSecret && !door.IsOpen);
                if (_doorProofDoor == null) { _logError("secret-proof has no authored closed secret"); return; }
                uint before = _inventory.DoorRevision;
                bool enemyAccepted = _inventory.RequestEnemyDoorOpen(_doorProofDoor.DoorId);
                _log("secret-proof enemy id=" + _doorProofDoor.DoorId + " accepted=" + enemyAccepted +
                    " open=" + _doorProofDoor.IsOpen + " blocker=" + _doorProofDoor.BlockerEnabled +
                    " revisionUnchanged=" + (_inventory.DoorRevision == before));
                Collider panel = _doorProofDoor.BlockerCollider;
                Vector3 remoteWall = panel.bounds.center + panel.transform.forward * 2f;
                remoteWall.y = panel.bounds.min.y + .15f;
                _doorProofActorA.Rig.Movement.TeleportTo(remoteWall);
            }
            else if (_doorProofDoor == null || !_doorProofDoor.IsSecret ||
                !TryPlaceDoorProofActor(_doorProofActorA, -_doorProofDoor.transform.forward, 0f, out _))
            { _logError("secret-proof could not place actor beside visible lever"); return; }
            Physics.SyncTransforms();
            bool can = _doorProofDoor.CanInteract(_doorProofActorA.Rig, out string reason);
            _log("secret-proof staged stage=" + stage + " id=" + _doorProofDoor.DoorId + " canInteract=" + can +
                " reason=" + (string.IsNullOrEmpty(reason) ? "-" : reason.Replace(' ', '_')) +
                " open=" + _doorProofDoor.IsOpen + " revision=" + _inventory.DoorRevision);
        }

        private void StartDoorAiProof()
        {
            if (_doorProofDoor == null || _doorProofDoor.IsOpen || _doorProofActorA?.Rig?.Movement == null ||
                _doorProofActorB?.Rig?.Movement == null || _inventory == null)
            { _logError("door-proof enemy-ai fixture prerequisites failed"); return; }

            GameObject enemiesRoot = null;
            GameObject[] sceneRoots = SceneManager.GetActiveScene().GetRootGameObjects();
            for (int i = 0; i < sceneRoots.Length; i++)
                if (sceneRoots[i] != null && sceneRoots[i].name == "Enemies") { enemiesRoot = sceneRoots[i]; break; }
            if (enemiesRoot == null) { _logError("door-proof enemy-ai Enemies root missing"); return; }

            EnemyAgent[] candidates = enemiesRoot.GetComponentsInChildren<EnemyAgent>(true);
            for (int i = 0; i < candidates.Length; i++)
                if (_doorProofAiEnemy == null && candidates[i] != null && candidates[i].Data != null)
                    _doorProofAiEnemy = candidates[i];
            if (_doorProofAiEnemy == null) { _logError("door-proof enemy-ai has no authored EnemyAgent"); return; }

            // The door proof normally disables the enemy root. Activate only this authored agent so its regular
            // Awake/Start/Update and movement sweep run, without unrelated monsters disturbing the two-client fixture.
            for (int i = 0; i < candidates.Length; i++)
                if (candidates[i] != null && candidates[i] != _doorProofAiEnemy) candidates[i].gameObject.SetActive(false);
            enemiesRoot.SetActive(true);

            Collider panel = _doorProofDoor.BlockerCollider;
            if (panel == null) { _logError("door-proof enemy-ai door has no blocker collider"); return; }
            Vector3 normal = panel.transform.forward;
            normal.y = 0f;
            normal.Normalize();
            Vector3 center = _doorProofDoor.transform.position;
            float floorY = panel.bounds.min.y + 0.15f;
            Vector3 enemyStart = center + normal * 2.5f;
            enemyStart.y = floorY;
            Vector3 targetPosition = center - normal * 3.5f;
            targetPosition.y = floorY;
            Vector3 observerPosition = center + Vector3.Cross(Vector3.up, normal).normalized * 25f;
            observerPosition.y = floorY;

            _doorProofActorA.Rig.Movement.TeleportTo(targetPosition);
            _doorProofActorB.Rig.Movement.TeleportTo(observerPosition);
            _doorProofAiEnemy.transform.position = enemyStart;
            _doorProofAiEnemy.transform.rotation = Quaternion.LookRotation(-normal, Vector3.up);
            Physics.SyncTransforms();

            _doorProofAiStartPosition = enemyStart;
            _doorProofAiTargetCombatId = _doorProofActorA.Rig.CombatId;
            _doorProofAiStartedAt = Time.realtimeSinceStartup;
            _doorProofAiRunning = true;
            _log("door-proof enemy-ai started id=" + _doorProofDoor.DoorId + " enemy=" + _doorProofAiEnemy.name +
                 " targetCombatId=" + _doorProofAiTargetCombatId + " closed=True revision=" + _inventory.DoorRevision +
                 " enemyStart=" + enemyStart.ToString("F2", CultureInfo.InvariantCulture) +
                 " target=" + targetPosition.ToString("F2", CultureInfo.InvariantCulture));
        }

        private void TickDoorAiProof()
        {
            if (!_doorProofAiRunning || _doorProofAiEnemy == null || _doorProofDoor == null || _inventory == null) return;
            if (_doorProofDoor.IsOpen)
            {
                float moved = Vector3.Distance(_doorProofAiStartPosition, _doorProofAiEnemy.transform.position);
                bool targetMatched = _doorProofAiEnemy.CurrentTargetCombatId == _doorProofAiTargetCombatId;
                bool passed = targetMatched && moved > 0.25f;
                _doorProofAiRunning = false;
                _log("door-proof enemy-ai opened accepted=" + passed + " targetMatched=" + targetMatched +
                     " targetCombatId=" + _doorProofAiEnemy.CurrentTargetCombatId + " moved=" + moved.ToString("F3", CultureInfo.InvariantCulture) +
                     " revision=" + _inventory.DoorRevision + " blocker=" + _doorProofDoor.BlockerEnabled);
                return;
            }

            if (Time.realtimeSinceStartup - _doorProofAiStartedAt > 15f)
            {
                _doorProofAiRunning = false;
                _logError("door-proof enemy-ai timeout targetCombatId=" + _doorProofAiEnemy.CurrentTargetCombatId +
                          " moved=" + Vector3.Distance(_doorProofAiStartPosition, _doorProofAiEnemy.transform.position).ToString("F3", CultureInfo.InvariantCulture) +
                          " doorOpen=False");
            }
        }

        private Vector3 DoorProofInteractionPoint(Vector3 origin)
        {
            if (_doorProofDoor == null) return origin;
            Collider[] colliders = _doorProofDoor.GetComponentsInChildren<Collider>(false);
            Vector3 closest = _doorProofDoor.transform.position;
            float bestSqr = float.MaxValue;
            for (int i = 0; i < colliders.Length; i++)
            {
                Collider collider = colliders[i];
                if (collider == null || !collider.enabled) continue;
                Vector3 point = collider.ClosestPoint(origin);
                float sqr = (point - origin).sqrMagnitude;
                if (sqr < bestSqr) { bestSqr = sqr; closest = point; }
            }
            return closest;
        }

        private void PrepareExtractionProofFixture()
        {
            NetIdentity owner = null;
            foreach (NetIdentity row in _table.All)
                if (string.Equals(row.Slot, "extract-a", StringComparison.OrdinalIgnoreCase)) { owner = row; break; }

            ExtractionPoint point = UnityEngine.Object.FindFirstObjectByType<ExtractionPoint>();
            ItemCatalog catalog = _director.Config != null ? _director.Config.itemCatalog : null;
            if (owner?.Rig == null || owner.Rig.Inventory == null || owner.Rig.Health == null || owner.Rig.Movement == null || point == null || catalog == null)
            {
                _logError("extraction-proof fixture unavailable: owner, extraction point or item catalog missing");
                return;
            }

            if (_director.Config.requireLootToExtract && owner.Rig.Inventory.RecoveredValue <= 0)
            {
                ItemData loot = null;
                for (int i = 0; i < catalog.Items.Count; i++)
                {
                    ItemData candidate = catalog.Items[i];
                    if (candidate != null && !candidate.IsEquippable && candidate.value > 0) { loot = candidate; break; }
                }
                string addReason = loot == null ? "no value item" : null;
                if (loot == null || !owner.Rig.Inventory.TryAdd(loot, 1, out addReason))
                {
                    _logError("extraction-proof fixture could not seed required carried loot: " + addReason);
                    return;
                }
            }

            Vector3 center = point.transform.position;
            Collider collider = point.GetComponent<Collider>();
            float floorY = collider != null ? collider.bounds.min.y : center.y - 0.5f;
            bool positioned = false;
            float reach = _director.InteractionReach;
            for (int ring = 0; ring < 5 && !positioned; ring++)
            {
                float radius = 0.55f + ring * 0.2f;
                for (int step = 0; step < 24; step++)
                {
                    float angle = step * 15f * Mathf.Deg2Rad;
                    Vector3 forward = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
                    Vector3 candidate = new Vector3(center.x, floorY + 0.15f, center.z) + forward * radius;
                    owner.Rig.Movement.TeleportTo(candidate);
                    Physics.SyncTransforms();
                    if (point.CanInteract(owner.Rig, out _))
                    {
                        positioned = true;
                        break;
                    }
                }
            }

            if (!positioned)
            {
                _logError("extraction-proof fixture failed production CanInteract at point reach=" + reach.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
                return;
            }

            _log("extraction-proof fixture player=" + owner.PlayerId + " client=" + owner.ClientId +
                 " point=" + point.name + " reach=" + reach.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) +
                 " canInteract=True recovered=" + owner.Rig.Inventory.RecoveredValue +
                 " revision=" + owner.Rig.Inventory.Revision +
                 " hold=" + point.HoldDuration.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
            _extractionProofRig = owner.Rig;
        }

        private void ApplyExtractionProofStimulus()
        {
            string path = _args.ExtractionProofActionFile;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
            string mode;
            try { mode = ReadSharedText(path).Trim().ToLowerInvariant(); }
            catch (IOException) { return; }
            if (mode != "damage" && mode != "death") return;

            _extractionProofStimulusApplied = true;
            NetIdentity attacker = null;
            foreach (NetIdentity row in _table.All)
                if (!ReferenceEquals(row.Rig, _extractionProofRig)) { attacker = row; break; }
            int sourceCombatId = attacker?.Rig != null ? attacker.Rig.CombatId : _extractionProofRig.CombatId;
            Vector3 sourcePosition = attacker?.Rig != null ? attacker.Rig.transform.position : _extractionProofRig.transform.position + Vector3.back;
            EntityKind sourceKind = attacker?.Rig != null ? attacker.Rig.Kind : EntityKind.RemotePlayer;
            float amount = mode == "death" ? _extractionProofRig.Health.CurrentHealth + 1f : 1f;
            var info = new DamageInfo(amount, _extractionProofRig.CombatCenter, (sourcePosition - _extractionProofRig.CombatCenter).normalized,
                sourcePosition, sourceCombatId, sourceKind, DamageType.Physical, false, false, 0f);
            _extractionProofRig.ApplyDamage(info); // normal server PlayerRig -> HealthComponent damage path
            _log("extraction-proof stimulus mode=" + mode + " player=" + _extractionProofRig.PlayerId +
                 " amount=" + amount.ToString("R", CultureInfo.InvariantCulture) + " health=" +
                 _extractionProofRig.Health.CurrentHealth.ToString("R", CultureInfo.InvariantCulture) +
                 " dead=" + _extractionProofRig.Health.IsDead);
        }

#endif


#if DEVELOPMENT_BUILD || UNITY_EDITOR
        private void PrepareTimeoutProofFixture()
        {
            _timeoutProofActors.Clear();
            foreach (NetIdentity row in _table.All)
            {
                if (row?.Rig == null || row.Rig.Inventory == null || row.Rig.Health == null) continue;
                if (!string.Equals(row.Slot, "extract-a", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(row.Slot, "extract-b", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(row.Slot, "extract-a", StringComparison.OrdinalIgnoreCase)) _timeoutProofActors.Insert(0, row);
                else _timeoutProofActors.Add(row);
            }
            if (_timeoutProofActors.Count != 2)
            {
                _logError("timeout-proof fixture requires admitted slots extract-a and extract-b");
                _timeoutProofActors.Clear();
                return;
            }

            ItemCatalog catalog = _director.Config != null ? _director.Config.itemCatalog : null;
            ItemData gear = null;
            int smallestArea = int.MaxValue;
            if (catalog != null)
                for (int i = 0; i < catalog.Items.Count; i++)
                {
                    ItemData candidate = catalog.Items[i];
                    if (candidate == null || !candidate.IsEquippable) continue;
                    Vector2Int footprint = candidate.EffectiveFootprint;
                    int area = footprint.x * footprint.y;
                    if (area >= smallestArea) continue;
                    gear = candidate;
                    smallestArea = area;
                }
            if (gear == null || _director.Session == null)
            {
                _logError("timeout-proof fixture has no equippable item or raid issuer in the server catalog");
                _timeoutProofActors.Clear();
                return;
            }
            for (int i = 0; i < _timeoutProofActors.Count; i++)
            {
                NetIdentity actor = _timeoutProofActors[i];
                string mintReason = null;
                string addReason = null;
                bool minted = catalog.TryMintCommon(_director.Session.Issuer, gear, out ItemInstance instance, out mintReason);
                bool added = minted && actor.Rig.Inventory.TryAddInstance(instance, out addReason);
                if (!minted || !added)
                {
                    _logError("timeout-proof fixture could not seed player=" + actor.PlayerId + " UID gear: " + (mintReason ?? addReason));
                    _timeoutProofActors.Clear();
                    return;
                }
                if (string.Equals(actor.Slot, "extract-a", StringComparison.OrdinalIgnoreCase)) _timeoutProofUidA = instance.UidText;
                else _timeoutProofUidB = instance.UidText;
            }

            bool productionCountdown = _director.Config != null && _director.Config.enableRaidCountdown;
            const int proofDurationSeconds = 5;
            if (!_director.ConfigureRaidCountdownForProof(proofDurationSeconds))
            {
                _logError("timeout-proof fixture could not install its process-local server deadline");
                _timeoutProofActors.Clear();
                return;
            }
            _log("timeout-proof fixture duration=" + proofDurationSeconds + " productionCountdown=" + productionCountdown +
                 " players=" + _timeoutProofActors[0].PlayerId + "," + _timeoutProofActors[1].PlayerId +
                 " uidA=" + _timeoutProofUidA + " uidB=" + _timeoutProofUidB);
        }

        private void ObserveTimeoutProofOutcome()
        {
            if (!_director.RaidExpired) return;
            if (!_director.TryGetSession(_timeoutProofActors[0].PlayerId, out PlayerSession a) || a == null || !a.HasResult || a.State != RunState.Dead ||
                !_director.TryGetSession(_timeoutProofActors[1].PlayerId, out PlayerSession b) || b == null || !b.HasResult || b.State != RunState.Dead)
                return;

            _timeoutProofOutcomeLogged = true;
            bool extractionA = _director.ExtractionAllowed(_timeoutProofActors[0].Rig, out string reasonA);
            bool extractionB = _director.ExtractionAllowed(_timeoutProofActors[1].Rig, out string reasonB);
            ulong corpseA = 0, corpseB = 0;
            bool uidAInCorpseA = _inventory != null && _inventory.TryFindCorpseUidForProof(a.PlayerId, _timeoutProofUidA, out corpseA);
            bool uidBInCorpseB = _inventory != null && _inventory.TryFindCorpseUidForProof(b.PlayerId, _timeoutProofUidB, out corpseB);
            bool uidAInCorpseB = _inventory != null && _inventory.TryFindCorpseUidForProof(b.PlayerId, _timeoutProofUidA, out _);
            bool uidBInCorpseA = _inventory != null && _inventory.TryFindCorpseUidForProof(a.PlayerId, _timeoutProofUidB, out _);
            int corpses = _inventory != null ? _inventory.RegisteredCorpseCount : 0;
            _log("timeout-proof outcome expired=" + _director.RaidExpired +
                 " productionCountdown=" + (_director.Config != null && _director.Config.enableRaidCountdown) +
                 " playerA=" + a.PlayerId + " resultA=" + a.Result.Outcome + " extractionA=" + extractionA +
                 " reasonA=" + (reasonA ?? "-").Replace(' ', '_') +
                 " playerB=" + b.PlayerId + " resultB=" + b.Result.Outcome + " extractionB=" + extractionB +
                 " reasonB=" + (reasonB ?? "-").Replace(' ', '_') + " corpses=" + corpses +
                 " uidA=" + _timeoutProofUidA + " uidAInCorpseA=" + uidAInCorpseA + " corpseA=" + corpseA + " uidAInCorpseB=" + uidAInCorpseB +
                 " uidB=" + _timeoutProofUidB + " uidBInCorpseB=" + uidBInCorpseB + " corpseB=" + corpseB + " uidBInCorpseA=" + uidBInCorpseA);
        }
#endif

        private void PublishRaidClock(float now)
        {
            if (_director == null || now < _nextRaidClockPublishAt) return;
            _nextRaidClockPublishAt = now + 0.25f;

            foreach (NetIdentity row in _table.All)
            {
                if (row?.Object == null) continue;
                NetPlayerRig view = row.Object.GetComponent<NetPlayerRig>();
                if (view != null)
                {
                    view.SetRaidClockFromServer(RaidClockWire.From(_director.ClockFor(row.Rig)));
                    view.SetDescentPortalsFromServer(DescentPortalWire.Capture(_director, row.Rig));
                    view.SetRoyaleZoneFromServer(RoyaleZoneWire.Capture(_director, row.Rig));
                }
            }
        }

        private void RequestDungeonScene(float now)
        {
            _sceneRequested = true;
            _sceneRequestedAt = now;

            string sceneName = NetTuning.ModularDungeonSceneName;
            try
            {
                if (_nm.SceneManager == null)
                {
                    Fail("scene management is off");
                    return;
                }

                SceneEventProgressStatus status = _nm.SceneManager.LoadScene(sceneName, LoadSceneMode.Single);
                _log("loading scene " + sceneName + " status=" + status);
                if (status != SceneEventProgressStatus.Started) Fail("scene load not started: " + status);
            }
            catch (Exception e)
            {
                Fail("scene load threw " + e.Message);
            }
        }

        private void CheckReady(float now)
        {
            if (_modulePlan == null)
            {
                Fail("required authoritative modular plan is missing; refusing to host another map");
                return;
            }

            if (now >= _nextModuleApplyAttemptAt && !DungeonModulePlanSession.TryApplyToLoadedScene(out string applyReason))
            {
                _nextModuleApplyAttemptAt = now + 0.25f;
                if (!string.Equals(_lastModuleApplyReason, applyReason, StringComparison.Ordinal))
                {
                    _lastModuleApplyReason = applyReason;
                    _log("modular scene apply pending: " + applyReason);
                }
                if (!DungeonModuleSceneAssembler.IsTransientInitializationFailure(applyReason))
                {
                    Fail("modular scene rejected the authoritative plan: " + applyReason);
                    return;
                }
                if (GameServices.TryGet(out RunDirector existing) && existing != null && existing.State == RunState.Running)
                    Fail("modular scene is missing or rejected its plan consumer: " + applyReason);
                else if (now - _sceneRequestedAt > SceneLoadTimeoutSeconds) Fail("modular scene did not apply plan: " + applyReason);
                return;
            }

            if (GameServices.TryGet(out RunDirector director) && director != null && director.State == RunState.Running)
            {
                _director = director;
                if (!_runResultSubscribed)
                {
                    GameEvents.RunEnded += OnRunEnded;
                    _runResultSubscribed = true;
                }
                if (_args.PartySizeOverride > 0)
                {
                    director.SetPartySizeOverride(_args.PartySizeOverride); // U3B3 proof switch: one player per party makes players hostile
                    _log("party size override=" + _args.PartySizeOverride);
                }

                _log("ready scene=" + NetTuning.ModularDungeonSceneName + " " + CountsText() + " maxPlayers=" + _args.MaxPlayers +
                     " enemies=" + (NetRole.EnemiesDisabled ? "off" : "on"));
                return;
            }

            if (now - _sceneRequestedAt > SceneLoadTimeoutSeconds) Fail("dungeon scene not ready after " + SceneLoadTimeoutSeconds + "s");
        }

        private void Fail(string why)
        {
            _sceneFailed = true;
            _logError("server cannot host the dungeon: " + why);
        }

        // ---- approval --------------------------------------------------------------------------

        private void Approve(NetworkManager.ConnectionApprovalRequest request, NetworkManager.ConnectionApprovalResponse response)
        {
            ulong clientId = request.ClientNetworkId;
            bool hostSelf = clientId == NetworkManager.ServerClientId;

            if (_director == null && !_sceneFailed && !hostSelf)
            {
                // The dungeon (and the class list that lives in it) is still loading: hold the decision.
                response.Pending = true;
                _pending.Add(new Pending { ClientId = clientId, Payload = request.Payload, Response = response });
                _log("approval id=" + clientId + " held (dungeon loading)");
                return;
            }

            Decide(clientId, request.Payload, response);
        }

        private void FlushPendingApprovals()
        {
            if (_pending.Count == 0) return;

            Pending[] held = _pending.ToArray();
            _pending.Clear();
            for (int i = 0; i < held.Length; i++)
            {
                Decide(held[i].ClientId, held[i].Payload, held[i].Response);
                held[i].Response.Pending = false;
            }
        }

        private void Decide(ulong clientId, byte[] payload, NetworkManager.ConnectionApprovalResponse response)
        {
            NetApprovalResult r = _sceneFailed || (_director != null && _director.RoyaleMode.HasValue && _director.FloorCoordinator.IsAdmissionClosed(1))
                ? new NetApprovalResult(false, _sceneFailed ? "server not ready" : "Royale admission closed", "", "")
                : NetApproval.Evaluate(payload, NetTuning.ProtocolVersion, _approved.Count,
                    _director != null && _director.RoyaleMode.HasValue
                        ? new Emberdelve.Rules.RoyaleRoster(_director.RoyaleMode.Value).Capacity : _args.MaxPlayers,
                    SlotInUse, ClassApproved);

            response.Approved = r.Approved;
            response.CreatePlayerObject = false;
            response.Pending = false;
            response.Reason = r.Approved ? "" : r.Reason;

            if (r.Approved)
                _approved[clientId] = new Approved { Slot = r.Slot, ClassId = r.ClassId, Kit = r.Kit, ApprovedAt = Time.unscaledTime };

            _log("approval id=" + clientId + " approved=" + r.Approved +
                 " slot=" + (r.Slot.Length == 0 ? "-" : r.Slot) + " class=" + (r.ClassId.Length == 0 ? "-" : r.ClassId) +
                 " reason=" + (r.Approved ? "-" : r.Reason));
        }

        private bool SlotInUse(string slot)
        {
            foreach (KeyValuePair<ulong, Approved> pair in _approved)
                if (string.Equals(pair.Value.Slot, slot, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // The host's own approval can come before the dungeon exists; its class is then checked at spawn time instead.
        private bool ClassApproved(string classId)
        {
            return _director == null || _director.TryResolveClass(classId, out _);
        }

        /// <summary>A seat granted to a connection that never completed the join is given back (killed during scene sync, etc.).</summary>
        private void PurgeStaleReservations(float now)
        {
            if (_approved.Count == 0) return;

            _scratch.Clear();
            foreach (KeyValuePair<ulong, Approved> pair in _approved)
            {
                if (!pair.Value.Connected && now - pair.Value.ApprovedAt > UnconnectedReservationSeconds) _scratch.Add(pair.Key);
            }

            for (int i = 0; i < _scratch.Count; i++)
            {
                _approved.Remove(_scratch[i]);
                _log("reservation expired client=" + _scratch[i] + " (never completed the join)");
            }
        }

        // ---- connect / disconnect --------------------------------------------------------------

        public void OnClientConnected(ulong clientId)
        {
            if (!_approved.TryGetValue(clientId, out Approved info))
            {
                // Not through our approval (should not happen with ConnectionApproval on); do not give it an actor.
                _logError("client " + clientId + " connected without an approval record; disconnecting it");
                if (clientId != NetworkManager.ServerClientId) _nm.DisconnectClient(clientId, "not approved");
                return;
            }

            info.Connected = true;

            if (_modulePlan != null && clientId != NetworkManager.ServerClientId)
            {
                EnsureModuleReadyHandler();
                if (!_awaitingSpawn.Contains(clientId)) _awaitingSpawn.Add(clientId);
                if (!SendModulePlan(clientId))
                {
                    _awaitingSpawn.Remove(clientId);
                    _approved.Remove(clientId);
                    _nm.DisconnectClient(clientId, "modular map synchronization unavailable");
                }
                return;
            }
            info.ModulePlanReady = true;

            if (_director == null)
            {
                if (!_awaitingSpawn.Contains(clientId)) _awaitingSpawn.Add(clientId);
                return;
            }

            SpawnActor(clientId, info);
        }

        private void FlushAwaitingSpawns()
        {
            if (_awaitingSpawn.Count == 0) return;

            ulong[] waiting = _awaitingSpawn.ToArray();
            _awaitingSpawn.Clear();
            for (int i = 0; i < waiting.Length; i++)
            {
                if (_sceneFailed || !_approved.TryGetValue(waiting[i], out Approved info))
                {
                    if (waiting[i] != NetworkManager.ServerClientId) _nm.DisconnectClient(waiting[i], "server not ready");
                    continue;
                }

                if (_modulePlan != null && !info.ModulePlanReady)
                {
                    _awaitingSpawn.Add(waiting[i]);
                    continue;
                }

                SpawnActor(waiting[i], info);
            }
        }

        private void EnsureModuleReadyHandler()
        {
            if (_modulePlan == null || _moduleReadyHandlerRegistered || _nm.CustomMessagingManager == null) return;
            _nm.CustomMessagingManager.RegisterNamedMessageHandler(NetDungeonModulePlanClient.ReadyMessage, OnModulePlanReady);
            _moduleReadyHandlerRegistered = true;
        }

        private bool SendModulePlan(ulong clientId)
        {
            if (_modulePlan == null || _nm.CustomMessagingManager == null || !_approved.TryGetValue(clientId, out Approved info)) return false;
            byte[] payload = DungeonModulePlanCodec.Encode(_modulePlan);
            using (FastBufferWriter writer = new FastBufferWriter(payload.Length, Allocator.Temp))
            {
                writer.WriteBytesSafe(payload, payload.Length);
                _nm.CustomMessagingManager.SendNamedMessage(NetDungeonModulePlanClient.PlanMessage, clientId, writer, NetworkDelivery.ReliableSequenced);
            }
            info.ModulePlanSentAt = Mathf.Max(Time.unscaledTime, 0.0001f);
            _log("modular plan sent client=" + clientId + " hash=" + _modulePlan.LayoutHash);
            return true;
        }

        private void PurgeModulePlanWaiters(float now)
        {
            if (_modulePlan == null) return;
            _scratch.Clear();
            foreach (KeyValuePair<ulong, Approved> pair in _approved)
            {
                Approved info = pair.Value;
                if (info.Connected && !info.ModulePlanReady && info.ModulePlanSentAt > 0f && now - info.ModulePlanSentAt > SceneLoadTimeoutSeconds)
                    _scratch.Add(pair.Key);
            }
            for (int i = 0; i < _scratch.Count; i++)
            {
                ulong clientId = _scratch[i];
                _approved.Remove(clientId);
                _awaitingSpawn.Remove(clientId);
                _logError("modular map readiness timed out client=" + clientId + "; no player actor spawned");
                _nm.DisconnectClient(clientId, "modular map readiness timeout");
            }
        }

        private void OnModulePlanReady(ulong clientId, FastBufferReader reader)
        {
            if (!_approved.TryGetValue(clientId, out Approved info) || clientId == NetworkManager.ServerClientId ||
                !info.Connected || info.ModulePlanSentAt <= 0f || info.ModulePlanReady)
            {
                if (clientId != NetworkManager.ServerClientId) _nm.DisconnectClient(clientId, "unexpected modular map acknowledgement");
                return;
            }

            // NamedMessage.Deserialize has already consumed the RPC hash, so Length is the whole
            // underlying message buffer. Validate the unread payload, not the total buffer length.
            int remainingBytes = reader.Length - reader.Position;
            if (remainingBytes != ModulePlanAckCodec.PayloadBytes || !reader.TryBeginRead(ModulePlanAckCodec.PayloadBytes))
            {
                _logError("modular plan acknowledgement has invalid remaining length client=" + clientId +
                          " remaining=" + remainingBytes + " total=" + reader.Length + " position=" + reader.Position);
                _nm.DisconnectClient(clientId, "malformed modular map acknowledgement");
                return;
            }

            byte[] payload = new byte[ModulePlanAckCodec.PayloadBytes];
            try
            {
                reader.ReadBytesSafe(ref payload, payload.Length);
            }
            catch (Exception e)
            {
                _logError("modular plan acknowledgement malformed client=" + clientId + ": " + e.Message);
                _nm.DisconnectClient(clientId, "malformed modular map acknowledgement");
                return;
            }
            if (!ModulePlanAckCodec.TryDecode(payload, out uint hash, out bool accepted, out string decodeReason))
            {
                _logError("modular plan acknowledgement invalid client=" + clientId + ": " + decodeReason);
                _nm.DisconnectClient(clientId, "malformed modular map acknowledgement");
                return;
            }
            if (!accepted || !uint.TryParse(_modulePlan.LayoutHash, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint expected) || hash != expected)
            {
                _logError("modular plan refused client=" + clientId + " success=" + accepted + " hash=" + hash);
                _nm.DisconnectClient(clientId, "modular map could not be applied");
                return;
            }

            info.ModulePlanReady = true;
            _log("modular plan ready client=" + clientId + " hash=" + _modulePlan.LayoutHash);
            if (_director != null) SpawnActor(clientId, info);
        }

        private void SpawnActor(ulong clientId, Approved info)
        {
            if (_table.TryGetByClient(clientId, out _)) return;

            if (_modulePlan != null && clientId != NetworkManager.ServerClientId && !info.ModulePlanReady)
            {
                _logError("modular actor spawn blocked before matching client plan ACK client=" + clientId);
                _nm.DisconnectClient(clientId, "modular map plan was not acknowledged");
                return;
            }

            bool local = clientId == NetworkManager.ServerClientId;
            PlayerRig rig = null;
            string reason;
            bool joined;
            try
            {
                if (_director.TryResolveClass(info.ClassId, out CharacterClassData approvedClass))
                {
                    // The client's chosen skills, checked against what that class may take; anything else falls back to the class kit.
                    CharacterClassData withKit = Emberdelve.Skills.SkillLoadoutStore.ApplyKit(approvedClass, info.Kit);
                    joined = _director.TrySpawnNetworkPlayer(_playerPrefab, withKit, local, out rig, out reason);
                }
                else joined = _director.TrySpawnNetworkPlayer(_playerPrefab, info.ClassId, local, out rig, out reason);
            }
            catch (Exception e)
            {
                joined = false;
                reason = "spawn threw " + e.Message;
            }

            if (!joined || rig == null)
            {
                _logError("spawn refused client=" + clientId + " reason='" + reason + "'");
                RefuseAfterApproval(clientId, reason);
                return;
            }

            NetworkObject netObject = rig.GetComponent<NetworkObject>();
            if (netObject == null)
            {
                _director.RemovePlayer(rig.PlayerId, "prefab has no NetworkObject", true);
                _logError("spawn refused client=" + clientId + " reason='prefab has no NetworkObject'");
                RefuseAfterApproval(clientId, "server error");
                return;
            }

            NetIdentity row = new NetIdentity
            {
                ClientId = clientId,
                Slot = info.Slot,
                ClassId = info.ClassId,
                PlayerId = rig.PlayerId,
                PartyId = rig.PartyId,
                Rig = rig,
                Object = netObject
            };

#if DEVELOPMENT_BUILD || UNITY_EDITOR
            if (_args.CombatTestFixtureServer && !TryPlaceCombatTestActor(rig, _table.Count, out string fixtureReason))
            {
                _director.RemovePlayer(rig.PlayerId, "combat test fixture could not be staged", true);
                _logError("combat test fixture refused actor id=" + rig.PlayerId + ": " + fixtureReason);
                RefuseAfterApproval(clientId, "combat test fixture unavailable");
                return;
            }
#endif

            try
            {
                netObject.DontDestroyWithOwner = true;
                netObject.SpawnAsPlayerObject(clientId, true);
            }
            catch (Exception e)
            {
                _director.RemovePlayer(row.PlayerId, "spawn failed", true);
                _logError("SpawnAsPlayerObject threw for client " + clientId + ": " + e.Message);
                RefuseAfterApproval(clientId, "server error");
                return;
            }

            _table.Add(row);
            if (_args.ModularMapProofServer && clientId != NetworkManager.ServerClientId)
                _log("[modular-proof] actor spawned after ACK client=" + clientId + " hash=" + _modulePlan.LayoutHash + " networkObject=" + netObject.NetworkObjectId);
            if (clientId == NetworkManager.ServerClientId) _inventory?.SendCorpsePresenceTo(clientId);
            if (_movement != null) _movement.Register(row);
            _log("admitted client=" + clientId + " id=" + row.PlayerId + " party=" + row.PartyId + " slot=" + row.Slot +
                 " class=" + row.ClassId + " netId=" + netObject.NetworkObjectId + " " + CountsText());

            if (_lastRemovedBySlot.TryGetValue(row.Slot.ToLowerInvariant(), out int previousId))
            {
                _log("rejoin slot=" + row.Slot + " newId=" + row.PlayerId + " previousId=" + previousId +
                     " (admitted as a new actor; nothing carried over)");
            }

            // U3B2: the connection's skill gate and reject ledger (on probation when the slot was disconnected for abuse recently).
            if (_skills != null) _skills.OnAdmitted(row);
            if (_combat != null) _combat.OnAdmitted(row);
            if (_args.ModularMapProofServer) ReportModularProofIfComplete();
        }

#if DEVELOPMENT_BUILD || UNITY_EDITOR
        private bool TryPlaceCombatTestActor(PlayerRig rig, int participantSlot, out string reason)
        {
            reason = string.Empty;
            DungeonModuleSceneAssembler assembler = UnityEngine.Object.FindFirstObjectByType<DungeonModuleSceneAssembler>();
            if (assembler == null || !assembler.TryGetCombatTestPair(out Vector3 first, out Vector3 second, out reason))
                return false;

            Vector3 position = participantSlot == 0 ? first : second;
            Vector3 other = participantSlot == 0 ? second : first;
            CharacterController controller = rig != null ? rig.GetComponent<CharacterController>() : null;
            if (rig == null) { reason = "player rig is missing"; return false; }
            bool controllerWasEnabled = controller != null && controller.enabled;
            if (controllerWasEnabled) controller.enabled = false;
            rig.transform.SetPositionAndRotation(position, Quaternion.LookRotation(other - position, Vector3.up));
            if (controllerWasEnabled) controller.enabled = true;

            _log("combat-fixture id=" + rig.PlayerId + " slot=" + Mathf.Clamp(participantSlot, 0, 1) +
                 " room=entry pos=(" + position.x.ToString("F3", CultureInfo.InvariantCulture) + "," +
                 position.y.ToString("F3", CultureInfo.InvariantCulture) + "," + position.z.ToString("F3", CultureInfo.InvariantCulture) +
                 ") target=(" + other.x.ToString("F3", CultureInfo.InvariantCulture) + "," +
                 other.y.ToString("F3", CultureInfo.InvariantCulture) + "," + other.z.ToString("F3", CultureInfo.InvariantCulture) + ") los=True");
            return true;
        }
#endif

        private bool FindDifferentSeedPlan(DungeonModulePlan baseline, out string result)
        {
            for (int offset = 1; offset <= 128; offset++)
            {
                int candidateSeed = unchecked(baseline.Seed + offset);
                if (!DungeonModulePlanner.TryCreate(candidateSeed, _moduleCatalog.Modules, out DungeonModulePlan candidate, out _)) continue;
                bool same = true;
                for (int i = 0; i < baseline.Placements.Length; i++)
                {
                    DungeonModulePlacement a = baseline.Placements[i];
                    DungeonModulePlacement b = candidate.Placements[i];
                    if (a.ModuleIndex != b.ModuleIndex || a.QuarterTurnsClockwise != b.QuarterTurnsClockwise || a.OpenSockets != b.OpenSockets)
                    { same = false; break; }
                }
                if (!same)
                {
                    result = "baseSeed=" + baseline.Seed + " baseHash=" + baseline.LayoutHash + " variantSeed=" + candidateSeed + " variantHash=" + candidate.LayoutHash + " layoutChanged=true";
                    return true;
                }
            }
            result = "no different authored layout found across 128 consecutive seeds from " + baseline.Seed;
            return false;
        }

        private void ReportModularProofIfComplete()
        {
            if (_modularProofPassReported || _modulePlan == null) return;
            int readyClients = 0;
            int spawnedClients = 0;
            foreach (KeyValuePair<ulong, Approved> pair in _approved)
            {
                if (pair.Key == NetworkManager.ServerClientId || !pair.Value.Connected || !pair.Value.ModulePlanReady) continue;
                readyClients++;
                if (_table.TryGetByClient(pair.Key, out _)) spawnedClients++;
            }
            if (readyClients < 2 || spawnedClients < 2) return;
            _modularProofPassReported = true;
            _log("[modular-proof] TWO-CLIENT PASS remotes=" + spawnedClients + " seed=" + _modulePlan.Seed + " hash=" + _modulePlan.LayoutHash + " everySpawnAfterMatchingACK=true");
        }

        private void RefuseAfterApproval(ulong clientId, string reason)
        {
            _approved.Remove(clientId);
            if (clientId != NetworkManager.ServerClientId) _nm.DisconnectClient(clientId, reason);
        }

        private void OnRunEnded(int playerId, RunResult result)
        {
            // Bots have no owner UI. Their corpse custody and replicated death remain normal actor flow.
            if (result.PlayerId == playerId && _bots.ContainsKey(playerId)) return;
            if (result.PlayerId != playerId || !_table.TryGetByPlayer(playerId, out NetIdentity row) || row == null)
            {
                _logError("run-result not sent: no matching admitted owner for player=" + playerId);
                return;
            }
            if (!RunResultWire.TryCreate(in result, out RunResultWire wire))
            {
                _logError("run-result not sent: invalid server result for player=" + playerId);
                return;
            }

            NetPlayerRig view = row.Object != null ? row.Object.GetComponent<NetPlayerRig>() : null;
            if (view == null || !row.Object.IsSpawned)
            {
                _logError("run-result not sent: owner object unavailable for player=" + playerId);
                return;
            }

            if (row.ClientId == NetworkManager.ServerClientId)
            {
                NetOwnerRunResultView receiver = view.GetComponent<NetOwnerRunResultView>();
                if (receiver == null || !receiver.ReceiveServerResult(wire, false))
                {
                    _logError("run-result not applied to host owner for player=" + playerId);
                    return;
                }
            }
            else
            {
                view.RunResultRpc(wire);
            }
            _log("run-result sent owner=" + row.ClientId + " player=" + playerId + " outcome=" + result.Outcome +
                 " class=" + (result.ClassName ?? "").Replace(' ', '_') +
                 " loot=" + result.LootValue.ToString("R", CultureInfo.InvariantCulture) +
                 " items=" + result.ItemsCollected + " kills=" + result.EnemiesSlain +
                 " duration=" + result.DurationSeconds.ToString("R", CultureInfo.InvariantCulture) + " gear=" + result.GearScore);
        }

        public void OnClientDisconnected(ulong clientId)
        {
            _approved.Remove(clientId);
            _awaitingSpawn.Remove(clientId);
            for (int i = _pending.Count - 1; i >= 0; i--)
                if (_pending[i].ClientId == clientId) _pending.RemoveAt(i);

            if (_table.RemoveByClient(clientId, out NetIdentity row))
            {
                string custodyReason = "inventory service unavailable or source rig/loadout missing";
                bool custodyCommitted = _inventory != null && row?.Rig != null && row.Rig.Inventory != null && row.Rig.Equipment != null &&
                    _inventory.TryCommitDeath(row.PlayerId, row.Rig.transform.position, row.Rig.Inventory, row.Rig.Equipment, out custodyReason);
                if (custodyCommitted)
                {
                    RemoveActor(row, "disconnect");
                }
                else
                {
                    string reason = row?.Rig == null ? "source rig unavailable" : custodyReason;
                    RemoveActor(row, "disconnect-custody-pending", false);
                    if (row?.Object != null && row.Object.IsSpawned && !_pendingCorpseDisconnects.Contains(row))
                    {
                        row.Object.DontDestroyWithOwner = true;
                        _pendingCorpseDisconnects.Add(row);
                        _log("disconnect actor retained with original loadout; corpse custody retry pending player=" + row.PlayerId + " reason=" + reason);
#if DEVELOPMENT_BUILD || UNITY_EDITOR
                        if (_args.CorpseDeathProofServer && ReferenceEquals(row, _corpseDeathProofDisconnectActor))
                            _log("corpse-death-proof disconnect stage=retained player=" + row.PlayerId + " uid=" + _corpseDeathProofDisconnectUid +
                                 " uidHeldOnSource=" + LoadoutHoldsUid(row.Rig, _corpseDeathProofDisconnectUid) +
                                 " spawned=" + row.Object.IsSpawned + " owner=" + row.Object.OwnerClientId);
#endif
                    }
                }
            }
            if (_combat != null) _combat.OnClosed(clientId);
            if (_inventory != null) _inventory.OnClosed(clientId);
            if (_extraction != null) _extraction.OnClosed(clientId);
            if (_skills != null) _skills.OnClosed(clientId); // a connection that never got an actor still has a ledger
        }

        private void RemoveActor(NetIdentity row, string reason) => RemoveActor(row, reason, true);

        private void RemoveActor(NetIdentity row, string reason, bool despawnObject)
        {
            if (_movement != null) _movement.Unregister(row.PlayerId, reason);
            if (_combat != null) _combat.OnClosed(row.ClientId);
            if (_inventory != null) _inventory.OnClosed(row.ClientId);
            if (_extraction != null) _extraction.OnClosed(row.ClientId);
            if (_skills != null) _skills.OnClosed(row.ClientId);
            if (_director != null) _director.RemovePlayer(row.PlayerId, reason, false);

            NetworkObject netObject = row.Object;
            if (despawnObject && netObject != null && netObject.IsSpawned)
            {
                try { netObject.Despawn(true); }
                catch (Exception e) { _logError("despawn threw for id=" + row.PlayerId + ": " + e.Message); }
            }
            else if (despawnObject && row.Rig != null)
            {
                UnityEngine.Object.Destroy(row.Rig.gameObject);
            }

            if (reason != "shutdown") _lastRemovedBySlot[row.Slot.ToLowerInvariant()] = row.PlayerId;
            _log("removed client=" + row.ClientId + " id=" + row.PlayerId + " slot=" + row.Slot + " reason=" + reason + " " + CountsText());
        }

        private void RetryCorpseDisconnects()
        {
            if (_pendingCorpseDisconnects.Count == 0 || _inventory == null) return;
            for (int i = _pendingCorpseDisconnects.Count - 1; i >= 0; i--)
            {
                NetIdentity row = _pendingCorpseDisconnects[i];
                if (row?.Rig == null || row.Rig.Inventory == null || row.Rig.Equipment == null) continue;
                if (!_inventory.TryCommitDeath(row.PlayerId, row.Rig.transform.position, row.Rig.Inventory, row.Rig.Equipment, out string reason))
                    continue;
#if DEVELOPMENT_BUILD || UNITY_EDITOR
                bool proofDisconnect = _args.CorpseDeathProofServer && ReferenceEquals(row, _corpseDeathProofDisconnectActor);
                bool proofSourceEmpty = proofDisconnect && !LoadoutHoldsUid(row.Rig, _corpseDeathProofDisconnectUid);
#endif
                NetworkObject netObject = row.Object;
                if (netObject != null && netObject.IsSpawned)
                {
                    try { netObject.Despawn(true); }
                    catch (Exception e) { _logError("pending disconnect corpse rig despawn threw for id=" + row.PlayerId + ": " + e.Message); continue; }
                }
                else if (row.Rig != null) UnityEngine.Object.Destroy(row.Rig.gameObject);
                _pendingCorpseDisconnects.RemoveAt(i);
                _log("disconnect corpse custody committed player=" + row.PlayerId + " and retained rig despawned");
#if DEVELOPMENT_BUILD || UNITY_EDITOR
                if (proofDisconnect)
                {
                    bool found = _inventory.TryFindCorpseUidForProof(_corpseDeathProofDisconnectUid, out ulong corpseId);
                    _log("corpse-death-proof disconnect stage=committed player=" + row.PlayerId + " corpse=" + corpseId +
                         " uid=" + _corpseDeathProofDisconnectUid + " corpseHeld=" + found +
                         " sourceEmpty=" + proofSourceEmpty +
                         " spawned=" + (row.Object != null && row.Object.IsSpawned));
                }
#endif
            }
        }

        /// <summary>Server stops: every remaining actor is taken out of the run first so the counts end at zero.</summary>
        public void OnShutdownBegin()
        {
            RetireAllBots();
            RetryCorpseDisconnects();
            if (_pendingCorpseDisconnects.Count > 0)
                _logError("server shutdown with " + _pendingCorpseDisconnects.Count + " disconnect loadout(s) still on retained rigs; no durable corpse persistence is configured");
            NetIdentity[] rows = new List<NetIdentity>(_table.All).ToArray();
            if (rows.Length > 0 || (_inventory != null && _inventory.RegisteredCorpseCount > 0))
                _log("server shutdown: " + rows.Length + " active rig(s), " + (_inventory != null ? _inventory.RegisteredCorpseCount : 0) +
                     " in-memory corpse(s); inventory is session-only and no durable stash is configured");
            for (int i = 0; i < rows.Length; i++)
            {
                _table.RemoveByClient(rows[i].ClientId, out _);
                RemoveActor(rows[i], "shutdown");
            }

            _approved.Clear();
            _pending.Clear();
            _awaitingSpawn.Clear();
        }

        public void Dispose()
        {
            _royaleMaps?.Dispose(); _royaleMaps = null;
            RetireAllBots();
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            if (_doorProofOccluder != null) UnityEngine.Object.Destroy(_doorProofOccluder);
            if (_corpseProofHostPresenter != null)
            {
                _corpseProofHostPresenter.ProofStateObserved -= OnCorpseProofHostState;
                _corpseProofHostPresenter.ProofResultObserved -= OnCorpseProofHostResult;
                _corpseProofHostPresenter.ProofPresenceObserved -= OnCorpseProofHostPresence;
                _corpseProofHostPresenter = null;
            }
#endif
            if (_runResultSubscribed)
            {
                GameEvents.RunEnded -= OnRunEnded;
                _runResultSubscribed = false;
            }
            if (_extraction != null) { _extraction.Dispose(); _extraction = null; }
            if (_inventory != null) { _inventory.Dispose(); _inventory = null; }
            if (_movement != null)
            {
                _movement.Dispose();
                _movement = null;
            }

            if (_combat != null)
            {
                _combat.Dispose();
                _combat = null;
            }

            if (_skills != null)
            {
                _skills.Dispose();
                _skills = null;
            }

            if (NetIdentityTable.Current == _table) NetIdentityTable.Current = null;
        }

        private static string ReadSharedText(string path)
        {
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream, Encoding.UTF8)) return reader.ReadToEnd();
            }
            catch (IOException) { return string.Empty; }
            catch (UnauthorizedAccessException) { return string.Empty; }
        }
    }
}
