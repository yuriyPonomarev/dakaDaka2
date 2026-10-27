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
        public RunState State = RunState.Running;
        public int Kills;
        public float JoinedAt;
        public float EndedAt;
        public bool HasResult;
        public RunResult Result;
        internal System.Action DiedHandler;

        public bool IsRunning => State == RunState.Running;
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
        IEnemyDropHost, IRaidSessionHost, ILocalPlayerSource, IInteractionRulesSource, IInteractionAdmissionSource
    {
        [Header("Configuration")]
        [SerializeField] private GameConfig config;
        [SerializeField] private CharacterClassData fallbackClass;
        [SerializeField] private DungeonLayoutData layout;

        [Header("Scene wiring")]
        [Tooltip("The locally controlled player's rig (party 0). More players join through TryJoin / SpawnPlayer.")]
        [SerializeField] private PlayerRig playerRig;

        private RunState _state = RunState.Briefing;
        private float _startedAt;
        private bool _paused;
        private bool _started;
        private readonly List<PlayerSession> _sessions = new List<PlayerSession>();
        private readonly Dictionary<int, PlayerSession> _byPlayerId = new Dictionary<int, PlayerSession>();
        private PlayerSession _local;
        private int _spawnedCount;

        /// <summary>The run-wide state: Briefing, Running, Paused, and the final outcome once no player is left in the run.</summary>
        public RunState State => _state;
        public GameConfig Config => config;
        public DungeonLayoutData Layout => layout;

        /// <summary>Every admitted player, in join order.</summary>
        public IReadOnlyList<PlayerSession> Players => _sessions;

        /// <summary>
        /// The locally controlled player's session (presentation binds to it: HUD, bag screen, feedback). It is NOT consulted by any
        /// gameplay rule: interactions, loot, extraction and combat all name their actor.
        /// </summary>
        public PlayerSession LocalSession => _local;
        public int LocalPlayerId => _local != null ? _local.PlayerId : (playerRig != null ? playerRig.PlayerId : GameEvents.NoPlayer);

        /// <summary>The locally controlled rig (presentation only).</summary>
        public PlayerRig Player => _local != null ? _local.Rig : playerRig;

        /// <summary>The local player's own run state (Running until they die or extract), not the run's.</summary>
        public RunState LocalPlayerState => _local != null ? _local.State : _state;

        /// <summary>The local player's bag (presentation only).</summary>
        public Inventory Inventory => _local != null ? _local.Inventory : null;
        public int Kills => _local != null ? _local.Kills : 0;
        public int ItemsCollected => _local != null && _local.Inventory != null ? _local.Inventory.LifetimeCollected : 0;
        public int LootValue => _local != null && _local.Inventory != null ? _local.Inventory.RecoveredValue : 0;
        public float Elapsed => Mathf.Max(0f, Time.unscaledTime - _startedAt);

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
        }

        private CharacterClassData ClassFor(PlayerRig rig) => RunContext.GetSelectedClassOrDefault(fallbackClass);

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
            reason = null;
            if (rig == null) { reason = "no rig"; return false; }
            if (selectedClass == null) { reason = "no class selected"; return false; }
            if (_state == RunState.Dead || _state == RunState.Extracted) { reason = "the run already ended"; return false; }
            if (_byPlayerId.ContainsKey(rig.PlayerId)) { reason = "this player is already in the run"; return false; }

            if (!TryResolveParty(requestedParty, out int party, out reason)) return false;

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
            if (locallyControlled && _local == null)
            {
                _local = session;
                playerRig = rig;
            }

            if (_started) GameEvents.RaiseStatusLine(session.PlayerId, selectedClass.displayName);
            return true;
        }

        /// <summary>The party a joining player gets: the requested one when it has room, the first party with room for a negative request.</summary>
        private bool TryResolveParty(int requestedParty, out int party, out string reason)
        {
            reason = null;
            int cap = config != null ? Mathf.Clamp(config.maxPartySize, 1, 3) : CombatTeams.DefaultMaxPartySize;
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

        private void OnDestroy()
        {
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

            Time.timeScale = 1f;
        }

        private void Update()
        {
            PlayerRig rig = Player;
            if (rig == null) return;

            EmberdelveInput input = rig.Input;
            if (input == null) return;

            if (input.PausePressed) TogglePause();
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

            bool controlAllowed = !InventoryOpen && !_paused;
            rig.SetInputEnabled(controlAllowed);

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
            if (_local != null && _local.IsRunning) _local.Rig.SetInputEnabled(!_paused);
            SetState(_paused ? RunState.Paused : RunState.Running);
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
            CompletePlayer(session, RunState.Dead);
        }

        /// <summary>R2: the extraction rule for ONE actor: that player must still be in the run, alive, and (data) carrying value.</summary>
        public bool ExtractionAllowed(IInteractionActor actor, out string reason)
        {
            PlayerSession session = SessionOf(actor);
            if (session == null)
            {
                reason = "Not a player of this run";
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

            CompletePlayer(SessionOf(actor), RunState.Extracted);
            return true;
        }

        /// <summary>
        /// Ends ONE player's run (death or extraction): records their result, tells only that player's viewers, takes the rig
        /// out of play when it left the dungeon, and finishes the run itself once nobody is still in it.
        /// </summary>
        private void CompletePlayer(PlayerSession session, RunState outcome)
        {
            if (session == null || !session.IsRunning) return;

            session.State = outcome;
            session.EndedAt = Time.unscaledTime;

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
                Mathf.Max(0f, session.EndedAt - _startedAt),
                session.PlayerId);
            session.HasResult = true;

            bool anyoneLeft = false;
            for (int i = 0; i < _sessions.Count; i++) if (_sessions[i].IsRunning) { anyoneLeft = true; break; }

            // The run-wide outcome is published first (as before), then the player's own state and result.
            if (!anyoneLeft) FinishRun(outcome);

            if (session.IsLocal)
            {
                _paused = false;
                Time.timeScale = 1f;
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
            Time.timeScale = 1f;
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
            return session.Inventory.TryAddInstance(instance, out reason);
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

        private LootPickup BuildPickupBody(ItemData item, Vector3 worldPosition)
        {
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

            return root.AddComponent<LootPickup>();
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
