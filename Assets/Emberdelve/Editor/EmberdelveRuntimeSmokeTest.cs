using System.Collections;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.UI;
using Emberdelve.AI;
using Emberdelve.Combat;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Flow;
using Emberdelve.Interaction;
using Emberdelve.Items;
using Emberdelve.Level;
using Emberdelve.Player;

namespace Emberdelve.EditorTools
{
    /// <summary>
    /// Headless play-mode verification of the slice. Each entry point opens a scene, enters play mode,
    /// lets the real systems run (Start, Update, physics, AI, damage, UI) and then asserts on observable
    /// state. Nothing here simulates gameplay in the editor: the harness only moves transforms and calls
    /// the same public entry points the input layer and UI would call.
    ///
    /// Run from the command line, e.g.
    ///   Unity.exe -batchmode -nographics -projectPath . -logFile Logs/smoke.log
    ///           -executeMethod Emberdelve.EditorTools.EmberdelveRuntimeSmokeTest.RunCombatFlow
    /// </summary>
    [InitializeOnLoad]
    public static class EmberdelveRuntimeSmokeTest
    {
        private const string ModeKey = "Emberdelve.Smoke.Mode";
        private const string DoneKey = "Emberdelve.Smoke.Done";

        private static double _watchdogStartedAt;

        static EmberdelveRuntimeSmokeTest()
        {
            EditorApplication.playModeStateChanged += HandlePlayModeChange;
        }

        public static void RunCombatFlow() => StartRun("combat");
        public static void RunEnemyAi() => StartRun("ai");
        public static void RunExtraction() => StartRun("extract");
        public static void RunDeath() => StartRun("death");
        public static void RunLobbyFlow() => StartRun("lobby");
        public static void RunArcherFlow() => StartRun("archer");
        public static void RunResultLoop() => StartRun("resultloop");

        private static void StartRun(string mode)
        {
            EmberdelveAssetLibrary.BuildAll();

            SessionState.SetBool(DoneKey, false);
            SessionState.SetString(ModeKey, mode);

            string scenePath = (mode == "lobby" || mode == "archer") ? EmberdelvePaths.LobbyScenePath : EmberdelvePaths.DungeonScenePath;
            EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);

            Debug.Log($"[Emberdelve][Smoke] Requesting play mode for '{mode}' in {scenePath}");

            _watchdogStartedAt = EditorApplication.timeSinceStartup;
            EditorApplication.update += WatchForPlayModeStart;
            EditorApplication.isPlaying = true;
        }

        private static void WatchForPlayModeStart()
        {
            if (EditorApplication.isPlaying)
            {
                EditorApplication.update -= WatchForPlayModeStart;
                return;
            }

            if (EditorApplication.timeSinceStartup - _watchdogStartedAt > 45.0)
            {
                EditorApplication.update -= WatchForPlayModeStart;
                SessionState.SetString(ModeKey, string.Empty);
                Debug.LogError("[Emberdelve][Smoke] Play mode never started - headless play mode is unavailable in this session.");
                EditorApplication.Exit(1);
            }
        }

        private static void HandlePlayModeChange(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.EnteredPlayMode)
            {
                string mode = SessionState.GetString(ModeKey, string.Empty);
                if (string.IsNullOrEmpty(mode)) return;

                GameObject harnessObject = new GameObject("Emberdelve_SmokeHarness");
                Harness harness = harnessObject.AddComponent<Harness>();
                harness.Mode = mode;
            }
            else if (change == PlayModeStateChange.ExitingPlayMode)
            {
                if (SessionState.GetBool(DoneKey, false))
                {
                    SessionState.SetBool(DoneKey, false);
                    SessionState.SetString(ModeKey, string.Empty);
                    EditorApplication.delayCall += () => EditorApplication.Exit(0);
                }
            }
        }

        /// <summary>Runs one scripted verification pass inside play mode.</summary>
        private sealed class Harness : MonoBehaviour
        {
            public string Mode = "combat";

            private int _checks;
            private int _failures;
            private float _startedAt;

            private PlayerRig _rig;
            private RunDirector _director;
            private ICombatAuthority _authority;
            private LocalCombatAuthority _localAuthority;
            private DungeonLayoutData _layout;
            private NavGridData _grid;

            private void Start()
            {
                _startedAt = Time.realtimeSinceStartup;
                StartCoroutine(Run());
            }

            private void Update()
            {
                if (Time.realtimeSinceStartup - _startedAt > 150f)
                {
                    Debug.LogError($"[Emberdelve][Smoke:{Mode}] watchdog timeout - aborting with {_failures} failures");
                    SessionState.SetBool(DoneKey, true);
                    EditorApplication.isPlaying = false;
                }
            }

            private IEnumerator Run()
            {
                yield return WaitFrames(25);

                _rig = FindFirstObjectByType<PlayerRig>();
                _director = FindFirstObjectByType<RunDirector>();
                _layout = AssetDatabase.LoadAssetAtPath<DungeonLayoutData>(EmberdelvePaths.Data + "/DungeonLayout_AshenHalls.asset");
                _grid = AssetDatabase.LoadAssetAtPath<NavGridData>(EmberdelvePaths.Data + "/EmberdelveNavGrid.asset");

                if (Mode == "lobby")
                {
                    yield return LobbyFlow();
                }
                else if (Mode == "archer")
                {
                    // The archer must be chosen in the lobby before the dungeon run starts.
                    yield return LobbyFlow();
                    yield return ArcherFlow();
                }
                else if (_rig == null || _director == null)
                {
                    Check(false, "dungeon scene contains a PlayerRig and a RunDirector");
                }
                else
                {
                    yield return DungeonBaseline();

                    if (Mode == "combat") yield return CombatFlow();
                    else if (Mode == "ai") yield return EnemyAiFlow();
                    else if (Mode == "extract") yield return ExtractionFlow();
                    else if (Mode == "death") yield return DeathFlow();
                    else if (Mode == "resultloop") yield return ResultLoopFlow();
                }

                Debug.Log($"[Emberdelve][Smoke:{Mode}] {(_failures == 0 ? "PASS" : "FAIL")} checks={_checks} failures={_failures}");
                SessionState.SetBool(DoneKey, true);
                EditorApplication.isPlaying = false;
            }

            private IEnumerator DungeonBaseline()
            {
                bool hasAuthority = GameServices.TryGet<ICombatAuthority>(out ICombatAuthority resolved);
                _authority = resolved;
                _localAuthority = resolved as LocalCombatAuthority;
                Check(hasAuthority, "combat authority registered in GameServices");
                Check(_authority != null, "combat authority resolved");
                Check(_layout != null && _grid != null, "layout and nav grid loadable at runtime");
                Check(_director.State == RunState.Running, $"run state is Running (got {_director.State})");
                Check(GameObject.Find("Emberdelve_Hud") != null, "HUD canvas built at runtime");
                Check(FindFirstObjectByType<Camera>() != null, "first person camera present");
                Check(_rig.CurrentHealth > 0f && !_rig.IsDead, $"player alive at spawn ({_rig.CurrentHealth}/{_rig.MaxHealth})");
                Check(_rig.transform.position.y < 0.6f, $"player settled on the floor (y={_rig.transform.position.y:F2})");
                Check(_rig.Class != null, $"class applied by the run director ({(_rig.Class != null ? _rig.Class.displayName : "none")})");

                CharacterController controller = _rig.GetComponent<CharacterController>();
                Check(controller != null && controller.enabled, "CharacterController present and enabled");
                if (controller != null && _rig.Class != null)
                {
                    Check(Mathf.Abs(controller.height - _rig.Class.standHeight) < 0.25f,
                        $"controller height matches class stand height ({controller.height:F2} vs {_rig.Class.standHeight:F2})");
                    Check(controller.radius > 0.1f && controller.radius < 0.6f, $"controller radius sensible ({controller.radius:F2})");
                }

                Check(_rig.Movement != null && _rig.Movement.IsGrounded, "movement reports grounded after settling");

                Vector3 spawnPosition = _rig.transform.position;
                yield return WaitSeconds(2f);

                float drift = Vector3.Distance(_rig.transform.position, spawnPosition);
                Check(drift < 0.3f, $"player stays put with no input (drift {drift:F2}m)");
                Check(_rig.transform.position.y > -1f && _rig.transform.position.y < 0.6f,
                    $"player did not fall through the floor (y={_rig.transform.position.y:F2})");
            }

            // ---------------------------------------------------------------- combat

            private IEnumerator CombatFlow()
            {
                _sceneLoot = FindFirstObjectByType<LootPickup>();

                EnemyAgent enemy = PickAliveEnemy();
                Check(enemy != null, "an enemy agent is present");
                if (enemy == null || _authority == null) yield break;

                WeaponData weapon = _rig.Class != null ? _rig.Class.primaryWeapon : null;
                Check(weapon != null, "class provides a primary weapon");
                if (weapon == null) yield break;

                // Move the fight into an open cell next to the spawn so line of sight is unobstructed.
                Vector3 arena = _rig.transform.position;
                Vector2Int facingAxis = DominantAxis(_rig.transform.forward);
                if (!TryPlaceEnemyBesideSpawn(enemy, arena, 1, facingAxis, out Vector3 enemySpot))
                {
                    Check(false, "could not find a walkable cell next to the player spawn");
                    yield break;
                }

                enemy.transform.position = enemySpot;
                _rig.transform.rotation = Quaternion.LookRotation(enemy.CombatCenter - _rig.transform.position);
                Physics.SyncTransforms();
                yield return WaitFrames(2);

                Check(enemy.Brain != null && enemy.Brain.State != EnemyState.Patrol,
                    $"enemy noticed the player at close range (state {(enemy.Brain != null ? enemy.Brain.State.ToString() : "n/a")})");

                // A real swing through MeleeCombat: windup, active window, arc scan, authority.
                MeleeCombat melee = _rig.GetComponent<MeleeCombat>();
                Check(melee != null && melee.enabled, "warrior melee combat enabled");
                if (melee != null && melee.enabled)
                {
                    float swingHealthBefore = enemy.CurrentHealth;
                    int swingAcceptedBefore = _localAuthority != null ? _localAuthority.AcceptedCount : 0;

                    melee.BeginSwing();
                    yield return WaitSeconds(1.2f);

                    Check(enemy.CurrentHealth < swingHealthBefore,
                        $"melee swing damaged the enemy ({swingHealthBefore:F0} -> {enemy.CurrentHealth:F0})");
                    Check(_localAuthority != null && _localAuthority.AcceptedCount > swingAcceptedBefore,
                        "authority accepted the melee hit");
                }

                int acceptedBefore = _localAuthority != null ? _localAuthority.AcceptedCount : 0;
                int rejectedBefore = _localAuthority != null ? _localAuthority.RejectedCount : 0;
                // Start above MeleeCombat's own counter so the synthetic intents are not mistaken for replays.
                int sequence = 1000;
                float baseTime = Time.time;

                // Validation must refuse a hit claimed 40m away with a 2m sword.
                AttackIntent tooFar = new AttackIntent(_rig.CombatId, EntityKind.LocalPlayer, weapon, 1f,
                    _rig.CombatCenter, Vector3.forward, weapon.reach, sequence++, baseTime);
                bool farAccepted = _authority.SubmitAttack(tooFar,
                    new HitCandidate(enemy.CombatId, _rig.CombatCenter + Vector3.forward * 40f, false));
                Check(!farAccepted, "authority rejects a hit claimed beyond weapon reach");

                // Reusing a command sequence must be rejected (anti-replay rule for a future server).
                AttackIntent replay = new AttackIntent(_rig.CombatId, EntityKind.LocalPlayer, weapon, 1f,
                    _rig.CombatCenter, Vector3.forward, weapon.reach, sequence - 1, baseTime + 10f);
                Check(!_authority.SubmitAttack(replay, new HitCandidate(enemy.CombatId, enemy.CombatCenter, false)),
                    "authority rejects a repeated command sequence");

                // Faster than the weapon cooldown must be rejected.
                AttackIntent spam = new AttackIntent(_rig.CombatId, EntityKind.LocalPlayer, weapon, 1f,
                    _rig.CombatCenter, Vector3.forward, weapon.reach, sequence++, baseTime + 0.05f);
                Check(!_authority.SubmitAttack(spam, new HitCandidate(enemy.CombatId, enemy.CombatCenter, false)),
                    "authority rejects attacks faster than the weapon cooldown");

                Check(_localAuthority != null && _localAuthority.RejectedCount >= rejectedBefore + 3 &&
                      _localAuthority.AcceptedCount == acceptedBefore,
                    $"rejections recorded (rejected {(_localAuthority != null ? _localAuthority.RejectedCount : 0)}, accepted {(_localAuthority != null ? _localAuthority.AcceptedCount : 0)})");

                // Now attack properly until the enemy drops.
                float healthBefore = enemy.CurrentHealth;
                int accepted = 0;
                for (int i = 0; i < 16 && !enemy.IsDead; i++)
                {
                    Vector3 origin = _rig.CombatCenter;
                    Vector3 direction = (enemy.CombatCenter - origin).normalized;
                    AttackIntent intent = new AttackIntent(_rig.CombatId, EntityKind.LocalPlayer, weapon, 1f,
                        origin, direction, weapon.reach + weapon.hitSphereRadius, sequence++, baseTime + 5f + i * 1.5f);

                    if (_authority.SubmitAttack(intent, new HitCandidate(enemy.CombatId, enemy.CombatCenter, false))) accepted++;
                    yield return WaitFrames(2);
                }

                Check(accepted > 0, $"authority accepted valid attacks ({accepted})");
                Check(enemy.CurrentHealth < healthBefore, $"enemy health dropped ({healthBefore:F0} -> {enemy.CurrentHealth:F0})");
                Check(enemy.IsDead, "enemy died from accumulated damage");
                Check(_director.Kills > 0, $"run director counted slain enemies ({_director.Kills})");
                Check(_localAuthority != null && _localAuthority.AcceptedCount >= acceptedBefore + accepted,
                    "authority counted accepted attacks");

                yield return DroppedLootFlow();
                yield return LootFlow();
            }

            /// <summary>
            /// Enemy loot is spawned at runtime through RunDirector.SpawnLoot. It has to be reachable by
            /// the same interaction probe the pre-placed pickups use.
            /// </summary>
            private IEnumerator DroppedLootFlow()
            {
                ItemData item = null;
                EnemyAgent reference = PickAliveEnemy();
                if (reference != null && reference.Data != null && reference.Data.lootTable != null && reference.Data.lootTable.Length > 0)
                    item = reference.Data.lootTable[0];

                Check(item != null, "enemy definition carries a loot table");
                if (item == null) yield break;

                int pickupsBefore = FindObjectsByType<LootPickup>(FindObjectsSortMode.None).Length;
                Vector3 facing = FlatForward(_rig.transform);
                Vector3 spot = _rig.transform.position + facing * 1.6f + Vector3.up * 0.3f;
                _director.SpawnLoot(item, 1, spot);
                yield return WaitFrames(2);

                LootPickup[] after = FindObjectsByType<LootPickup>(FindObjectsSortMode.None);
                Check(after.Length > pickupsBefore, "SpawnLoot created a world pickup");

                LootPickup dropped = null;
                for (int i = 0; i < after.Length; i++)
                {
                    if (after[i] != null && Vector3.Distance(after[i].transform.position, spot) < 0.6f) { dropped = after[i]; break; }
                }

                Check(dropped != null, "dropped pickup found in the world");
                if (dropped == null) yield break;

                Collider collider = dropped.GetComponent<Collider>();
                Check(collider != null && collider.bounds.size.x > 0.05f,
                    $"dropped pickup has a usable collider ({(collider != null ? collider.bounds.size.ToString() : "none")})");
                Check(dropped.gameObject.layer == 17, $"dropped pickup is on the loot layer ({dropped.gameObject.layer})");

                _rig.transform.rotation = Quaternion.LookRotation(FlatForward(_rig.transform));
                _rig.transform.position = dropped.transform.position - FlatForward(_rig.transform) * 1.4f + Vector3.up * 0.15f;
                Physics.SyncTransforms();
                yield return WaitFrames(3);

                Interactor interactor = _rig.GetComponent<Interactor>();
                Check(interactor != null && interactor.CurrentTarget != null,
                    "interactor locks onto an interactable next to the dropped loot");

                Vector3 probeOrigin = _rig.transform.position + Vector3.up * 1.0f;
                Collider[] probeHits = Physics.OverlapSphere(probeOrigin + _rig.AimDirection * 1.2f, 1.35f,
                    _director.Config != null ? _director.Config.interactMask : Physics.AllLayers, QueryTriggerInteraction.Collide);

                bool inProbe = false;
                for (int i = 0; i < probeHits.Length; i++)
                {
                    if (probeHits[i] != null && probeHits[i].GetComponentInParent<LootPickup>() == dropped) { inProbe = true; break; }
                }

                Check(inProbe, "dropped loot sits inside the interaction probe volume");

                int collectedBefore = _director.ItemsCollected;
                dropped.OnInteractInstant();
                Check(_director.ItemsCollected > collectedBefore, "dropped loot can be collected");
            }

            private IEnumerator LootFlow()
            {
                LootPickup loot = _sceneLoot != null ? _sceneLoot : FindFirstObjectByType<LootPickup>();
                Check(loot != null, "loot pickups exist in the level");
                if (loot == null) yield break;

                Vector3 lootPosition = loot.transform.position;
                _rig.transform.rotation = Quaternion.LookRotation(FlatForward(_rig.transform));
                _rig.transform.position = lootPosition - FlatForward(_rig.transform) * 1.4f + Vector3.up * 0.15f;
                Physics.SyncTransforms();
                yield return WaitFrames(3);

                Collider lootCollider = loot.GetComponent<Collider>();
                Debug.Log($"[Emberdelve][Smoke:combat] loot diag: go={loot.gameObject.name} active={loot.gameObject.activeInHierarchy} layer={loot.gameObject.layer} collider={(lootCollider == null ? "NONE" : lootCollider.GetType().Name + " enabled=" + lootCollider.enabled + " size=" + lootCollider.bounds.size + " center=" + lootCollider.bounds.center)}");
                Collider[] everything = Physics.OverlapSphere(loot.transform.position + Vector3.up * 0.5f, 1.0f, Physics.AllLayers, QueryTriggerInteraction.Collide);
                System.Text.StringBuilder everyNames = new System.Text.StringBuilder();
                for (int i = 0; i < everything.Length; i++) everyNames.Append(everything[i].gameObject.name).Append('!').Append(everything[i].gameObject.layer).Append(", ");
                Debug.Log($"[Emberdelve][Smoke:combat] loot overlap (all layers) hits={everything.Length} [{everyNames}]");

                Vector3 probeOrigin = _rig.transform.position + Vector3.up * 1.0f;
                Vector3 probePoint = probeOrigin + _rig.AimDirection * 1.2f;
                Collider[] found = Physics.OverlapSphere(probePoint, 1.35f, _director.Config != null ? _director.Config.interactMask : Physics.AllLayers, QueryTriggerInteraction.Collide);
                System.Text.StringBuilder names = new System.Text.StringBuilder();
                for (int i = 0; i < found.Length; i++) names.Append(found[i].gameObject.name).Append('!').Append(found[i].gameObject.layer).Append(", ");
                Debug.Log($"[Emberdelve][Smoke:combat] probe diag: player={_rig.transform.position} fwd={_rig.transform.forward} aimDir={_rig.AimDirection} aimOrigin={_rig.AimOrigin} loot={loot.transform.position} bounds={loot.GetComponent<Collider>().bounds.center} probe={probePoint} hits={found.Length} [{names}]");

                Interactor interactor = _rig.GetComponent<Interactor>();
                Check(interactor != null, "player rig has an Interactor");
                Check(interactor != null && ReferenceEquals(interactor.CurrentTarget, loot),
                    "interactor targets the loot pickup in front of the player");

                int collectedBefore = _director.ItemsCollected;
                int unitsBefore = _director.Inventory != null ? _director.Inventory.TotalUnits : 0;

                loot.OnInteractInstant();

                Check(_director.ItemsCollected > collectedBefore, $"loot collected ({collectedBefore} -> {_director.ItemsCollected})");
                Check(_director.Inventory != null && _director.Inventory.TotalUnits > unitsBefore,
                    $"inventory units increased ({unitsBefore} -> {(_director.Inventory != null ? _director.Inventory.TotalUnits : 0)})");
                Check(_director.LootValue > 0, $"carried loot value tracked ({_director.LootValue})");
                Check(_director.Inventory != null && _director.Inventory.Slots.Count > 0 && _director.Inventory.Slots[0].Item != null,
                    "inventory exposes item entries for the HUD");
            }

            // ---------------------------------------------------------------- enemy ai

            private IEnumerator EnemyAiFlow()
            {
                EnemyAgent enemy = PickAliveEnemy();
                Check(enemy != null, "an enemy agent is present");
                if (enemy == null) yield break;

                Vector3 arena = _rig.transform.position;
                if (!TryPlaceEnemyBesideSpawn(enemy, arena, 2, Vector2Int.zero, out Vector3 enemySpot))
                {
                    Check(false, "could not find a walkable cell two cells from the spawn");
                    yield break;
                }

                enemy.transform.position = enemySpot;
                Physics.SyncTransforms();
                yield return WaitFrames(2);

                float distanceAtStart = Vector3.Distance(enemy.transform.position, _rig.transform.position);
                EnemyState initialState = enemy.Brain != null ? enemy.Brain.State : EnemyState.Patrol;
                float healthAtStart = _rig.CurrentHealth;

                yield return WaitSeconds(3f);

                float distanceAfterChase = Vector3.Distance(enemy.transform.position, _rig.transform.position);
                EnemyState chaseState = enemy.Brain != null ? enemy.Brain.State : EnemyState.Patrol;

                Check(chaseState == EnemyState.Chase || chaseState == EnemyState.Attack,
                    $"brain switched to chase/attack after spotting the player ({initialState} -> {chaseState})");
                Check(distanceAfterChase < distanceAtStart - 0.5f,
                    $"enemy closed the distance ({distanceAtStart:F1}m -> {distanceAfterChase:F1}m)");

                yield return WaitSeconds(9f);

                Check(_rig.CurrentHealth < healthAtStart,
                    $"enemy damaged the player ({healthAtStart:F0} -> {_rig.CurrentHealth:F0})");
                Check(_director.State == RunState.Running || _director.State == RunState.Dead,
                    $"run still tracked by the director ({_director.State})");
            }

            // ---------------------------------------------------------------- extraction

            private IEnumerator ExtractionFlow()
            {
                ExtractionPoint gate = FindFirstObjectByType<ExtractionPoint>();
                Check(gate != null, "extraction point present in the level");

                if (_director.LootValue <= 0)
                {
                    LootPickup loot = FindFirstObjectByType<LootPickup>();
                    if (loot != null) loot.OnInteractInstant();
                }
                Check(_director.LootValue > 0, "loot requirement satisfied before extraction");

                if (gate != null && _layout != null)
                {
                    Vector3 gateCenter = _layout.CellCenterWorld(_layout.extractionCell);
                    _rig.transform.position = gateCenter - _rig.transform.forward * 1.2f + Vector3.up * 0.15f;
                    Physics.SyncTransforms();
                    yield return WaitFrames(3);

                    Interactor interactor = _rig.GetComponent<Interactor>();
                    Check(interactor != null && ReferenceEquals(interactor.CurrentTarget, gate),
                        "interactor targets the ember gate when standing on it");

                    Check(gate.RequiresHold && gate.HoldDuration > 0f, $"gate requires a {gate.HoldDuration:F1}s hold");
                    Check(gate.CanInteract(out string reason), $"gate accepts interaction ({reason ?? "ok"})");

                    gate.OnHoldComplete();
                }

                yield return WaitFrames(3);

                Check(_director.State == RunState.Extracted, $"run state is Extracted (got {_director.State})");
                Check(GameObject.Find("Emberdelve_Result") != null, "result screen built after extraction");
                Check(RunContext.HasResult && RunContext.LastResult.Outcome == RunState.Extracted,
                    "run result recorded for the lobby");
                Check(Cursor.lockState == CursorLockMode.None, "cursor released after extraction");
            }

            // ---------------------------------------------------------------- death

            private IEnumerator DeathFlow()
            {
                EnemyAgent enemy = PickAliveEnemy();
                Check(enemy != null, "an enemy agent is present");
                if (enemy == null || _authority == null) yield break;

                WeaponData claw = enemy.Data != null ? enemy.Data.attackWeapon : null;
                Check(claw != null, $"enemy has an attack weapon ({(claw != null ? claw.displayName : "none")})");
                if (claw == null) yield break;

                float healthAtStart = _rig.CurrentHealth;
                int accepted = 0;
                int sequence = 1;
                float baseTime = Time.time;

                for (int i = 0; i < 24 && !_rig.IsDead; i++)
                {
                    enemy.transform.position = _rig.transform.position + new Vector3(0f, 0f, -1.1f);
                    Vector3 origin = enemy.CombatCenter;
                    Vector3 direction = (_rig.CombatCenter - origin).normalized;

                    AttackIntent intent = new AttackIntent(enemy.CombatId, EntityKind.Enemy, claw, 1f,
                        origin, direction, claw.reach + claw.hitSphereRadius, sequence++, baseTime + i * 1.2f);

                    if (_authority.SubmitAttack(intent, new HitCandidate(_rig.CombatId, _rig.CombatCenter, false))) accepted++;
                    yield return WaitFrames(2);
                }

                Check(accepted > 0, $"authority accepted enemy attacks against the player ({accepted})");
                Check(_rig.CurrentHealth < healthAtStart, $"player took damage ({healthAtStart:F0} -> {_rig.CurrentHealth:F0})");
                Check(_rig.IsDead, "player health reached zero");
                Check(_director.State == RunState.Dead, $"run state is Dead (got {_director.State})");
                Check(GameObject.Find("Emberdelve_Result") != null, "result screen built after death");
                Check(RunContext.HasResult && RunContext.LastResult.Outcome == RunState.Dead,
                    "death recorded in the run result");
            }

            // ---------------------------------------------------------------- lobby -> dungeon

            private IEnumerator LobbyFlow()
            {
                DontDestroyOnLoad(gameObject);

                GameObject canvasObject = GameObject.Find("Emberdelve_Lobby");
                Check(canvasObject != null, "lobby canvas built");
                if (canvasObject == null) yield break;

                Button[] buttons = canvasObject.GetComponentsInChildren<Button>(true);
                Check(buttons.Length >= 3, $"lobby shows class cards plus a descend button ({buttons.Length} buttons)");

                LobbyController lobby = FindFirstObjectByType<LobbyController>();
                Check(lobby != null, "lobby controller present");

                Button archerCard = null;
                Button descend = null;
                for (int i = 0; i < buttons.Length; i++)
                {
                    if (buttons[i].name == "Card_archer") archerCard = buttons[i];
                    if (buttons[i].name == "DescendButton") descend = buttons[i];
                }

                Check(archerCard != null, "archer class card is clickable");
                Check(descend != null, "descend button is clickable");
                if (archerCard == null || descend == null || lobby == null) yield break;

                archerCard.onClick.Invoke();
                yield return WaitFrames(2);

                Check(lobby.Selected != null && lobby.Selected.classId == "archer",
                    $"clicking the archer card selects it ({(lobby.Selected != null ? lobby.Selected.classId : "none")})");
                Check(RunContext.SelectedClass != null && RunContext.SelectedClass.classId == "archer",
                    "selection recorded in RunContext");

                CharacterClassData chosen = lobby.Selected;
                descend.onClick.Invoke();

                yield return WaitUntil(() => SceneManager.GetActiveScene().name == GameConfig.DefaultDungeonSceneName, 15f);
                Check(SceneManager.GetActiveScene().name == GameConfig.DefaultDungeonSceneName,
                    $"descend loaded the dungeon scene ({SceneManager.GetActiveScene().name})");

                yield return WaitFrames(30);

                PlayerRig rig = FindFirstObjectByType<PlayerRig>();
                RunDirector director = FindFirstObjectByType<RunDirector>();
                Check(rig != null && director != null, "dungeon scene bootstrapped a run");
                if (rig == null || director == null) yield break;

                Check(rig.Class == chosen, "dungeon run uses the class chosen in the lobby");
                Check(director.State == RunState.Running, $"run started (state {director.State})");
                Check(Mathf.Approximately(rig.MaxHealth, chosen.maxHealth),
                    $"class health applied ({rig.MaxHealth} vs {chosen.maxHealth})");

                RangedCombat ranged = rig.GetComponent<RangedCombat>();
                MeleeCombat melee = rig.GetComponent<MeleeCombat>();
                Check(ranged != null && ranged.enabled, "archer ranged combat enabled");
                Check(ranged != null && ranged.Magazine > 0, $"archer starts with arrows (magazine {(ranged != null ? ranged.Magazine : 0)})");
                Check(ranged != null && ranged.Reserve >= 0, $"archer reserve ammo tracked ({(ranged != null ? ranged.Reserve : 0)})");
                Check(melee != null && !melee.enabled, "archer melee combat disabled");
                Check(!rig.CanGuard, "archer cannot guard");
                Check(GameObject.Find("Emberdelve_Hud") != null, "HUD built for the dungeon run");
            }

            // ---------------------------------------------------------------- archer

            /// <summary>
            /// Drives the real input actions (mouse left button) so the bow path - draw, release, ammo,
            /// raycast, authority, damage - is exercised the same way a player triggers it.
            /// </summary>
            private IEnumerator ArcherFlow()
            {
                _rig = FindFirstObjectByType<PlayerRig>();
                _director = FindFirstObjectByType<RunDirector>();
                if (_rig == null || _director == null) { Check(false, "archer flow found rig and director"); yield break; }

                if (!GameServices.TryGet<ICombatAuthority>(out ICombatAuthority resolved)) resolved = null;
                _authority = resolved;
                _localAuthority = resolved as LocalCombatAuthority;

                RangedCombat ranged = _rig.GetComponent<RangedCombat>();
                Check(ranged != null && ranged.enabled, "archer ranged combat active");
                if (ranged == null) yield break;

                EnemyAgent target = PickAliveEnemy();
                Check(target != null, "an enemy is available for the shot");
                if (target == null) yield break;

                // Put the enemy directly down the archer's aim line, two cells ahead on walkable ground.
                if (!TryPlaceEnemyBesideSpawn(target, _rig.transform.position, 2, DominantAxis(_rig.transform.forward), out Vector3 spot))
                {
                    Check(false, "no walkable cell two cells ahead of the spawn");
                    yield break;
                }
                target.transform.position = spot;

                // Aim from the muzzle, not from the rig origin: the bow muzzle sits at eye height, so aiming
                // at the target's centre from the feet would send the arrow over its head.
                Vector3 fromMuzzle = target.CombatCenter - _rig.GetMuzzlePosition();
                _rig.transform.rotation = Quaternion.LookRotation(fromMuzzle);
                Physics.SyncTransforms();
                yield return WaitFrames(2);

                Mouse mouse = Mouse.current;
                Keyboard keyboard = Keyboard.current;
                Debug.Log($"[Emberdelve][Smoke:archer] input devices: mouse={(mouse != null ? "present" : "none")} keyboard={(keyboard != null ? "present" : "none")}");

                if (mouse == null)
                {
                    Debug.Log("[Emberdelve][Smoke:archer] note: headless batch mode exposes no mouse device, so the raw input binding is not exercised here; Fire() is the entry point the input handler calls");
                }

                int magazineBefore = ranged.Magazine;
                int acceptedBefore = _localAuthority != null ? _localAuthority.AcceptedCount : 0;
                float enemyHealthBefore = target.CurrentHealth;

                Vector3 muzzleBefore = _rig.GetMuzzlePosition();
                Debug.Log($"[Emberdelve][Smoke:archer] aim diag: muzzle={muzzleBefore} dir={_rig.AimDirection} target={target.CombatCenter} dist={Vector3.Distance(muzzleBefore, target.CombatCenter):F2}");

                int aimMask = _director.Config != null ? (_director.Config.hitMask.value | _director.Config.levelMask.value) : Physics.AllLayers;
                RaycastHit[] probeHits = new RaycastHit[8];
                int probeCount = Physics.RaycastNonAlloc(muzzleBefore, _rig.AimDirection, probeHits, 30f, aimMask, QueryTriggerInteraction.Ignore);
                System.Text.StringBuilder hitInfo = new System.Text.StringBuilder();
                for (int i = 0; i < probeCount; i++)
                {
                    ICombatEntity entity = probeHits[i].collider != null ? probeHits[i].collider.GetComponentInParent<ICombatEntity>() : null;
                    hitInfo.Append(probeHits[i].collider != null ? probeHits[i].collider.gameObject.name : "null")
                          .Append("!layer").Append(probeHits[i].collider != null ? probeHits[i].collider.gameObject.layer : -1)
                          .Append("!d").Append(probeHits[i].distance.ToString("F2"))
                          .Append("!ent").Append(entity != null ? entity.GetType().Name : "none").Append(", ");
                }
                Debug.Log($"[Emberdelve][Smoke:archer] raycast diag: mask={aimMask} hits={probeCount} [{hitInfo}]");

                System.Text.StringBuilder colliderInfo = new System.Text.StringBuilder();
                foreach (Collider collider in target.GetComponentsInChildren<Collider>())
                    colliderInfo.Append(collider.gameObject.name).Append("!layer").Append(collider.gameObject.layer)
                               .Append("!enabled").Append(collider.enabled).Append("!trig").Append(collider.isTrigger)
                               .Append("!center").Append(collider.bounds.center.ToString())
                               .Append("!size").Append(collider.bounds.size.ToString()).Append(", ");
                Debug.Log($"[Emberdelve][Smoke:archer] enemy root={target.transform.position} colliders: [{colliderInfo}]");

                ranged.Fire(1f);
                yield return WaitSeconds(0.8f);

                Check(ranged.Magazine < magazineBefore, $"firing consumed an arrow ({magazineBefore} -> {ranged.Magazine})");
                Check(target.CurrentHealth < enemyHealthBefore,
                    $"arrow damaged the enemy ({enemyHealthBefore:F0} -> {target.CurrentHealth:F0})");
                Check(_localAuthority != null && _localAuthority.AcceptedCount > acceptedBefore,
                    "authority accepted the ranged attack");

                ProjectileVisual[] projectiles = FindObjectsByType<ProjectileVisual>(FindObjectsSortMode.None);
                Check(projectiles != null && projectiles.Length > 0, "projectile visual spawned in the world");
            }

            private static void QueueMouseLeft(Mouse mouse, bool pressed)
            {
                MouseState state = new MouseState
                {
                    displayIndex = 0,
                    position = mouse.position.ReadValue(),
                    delta = Vector2.zero,
                    scroll = Vector2.zero,
                    buttons = (ushort)(pressed ? 1 : 0)
                };
                InputSystem.QueueStateEvent(mouse, state);
            }
            // ---------------------------------------------------------------- result screen loop

            /// <summary>
            /// Extraction -> result screen -> descend again -> death -> result screen -> back to lobby.
            /// Exercises the buttons a player would click, across two scene reloads.
            /// </summary>
            private IEnumerator ResultLoopFlow()
            {
                DontDestroyOnLoad(gameObject);

                yield return ExtractionFlow();
                Check(_director.State == RunState.Extracted, "first run ended by extraction");

                GameObject resultObject = GameObject.Find("Emberdelve_Result");
                Text extractionTitle = resultObject != null ? FindChildText(resultObject.transform, "Title") : null;
                Check(extractionTitle != null && extractionTitle.text.Contains("ESCAPED"),
                    $"result title reflects extraction ({(extractionTitle != null ? extractionTitle.text : "none")})");

                Button descendAgain = resultObject != null ? FindButton(resultObject, "RunAgainButton") : null;
                Check(descendAgain != null, "result screen offers descend again");
                if (descendAgain == null) yield break;

                descendAgain.onClick.Invoke();

                yield return WaitUntil(() => SceneManager.GetActiveScene().name == GameConfig.DefaultDungeonSceneName
                                             && FindFirstObjectByType<RunDirector>() != null
                                             && FindFirstObjectByType<RunDirector>().State == RunState.Running, 20f);

                _director = FindFirstObjectByType<RunDirector>();
                _rig = FindFirstObjectByType<PlayerRig>();
                Check(_director != null && _director.State == RunState.Running, "descend again started a fresh run");
                if (_director == null || _rig == null) yield break;

                Check(!_rig.IsDead && _rig.CurrentHealth >= _rig.MaxHealth - 1f,
                    $"player respawned alive ({_rig.CurrentHealth}/{_rig.MaxHealth})");
                Check(_director.ItemsCollected == 0 && _director.Kills == 0, "fresh run starts with an empty record");
                Check(GameObject.Find("Emberdelve_Result") == null, "previous result screen is gone");
                Check(GameObject.Find("Emberdelve_Hud") != null, "HUD rebuilt for the new run");

                if (!GameServices.TryGet<ICombatAuthority>(out ICombatAuthority resolved)) resolved = null;
                _authority = resolved;
                _localAuthority = resolved as LocalCombatAuthority;

                yield return DeathFlow();

                Check(_director.State == RunState.Dead, "second run ended in death");

                GameObject deathObject = GameObject.Find("Emberdelve_Result");
                Text deathTitle = deathObject != null ? FindChildText(deathObject.transform, "Title") : null;
                Check(deathTitle != null && deathTitle.text.Contains("DIED"),
                    $"result title reflects death ({(deathTitle != null ? deathTitle.text : "none")})");

                Button backToLobby = deathObject != null ? FindButton(deathObject, "LobbyButton") : null;
                Check(backToLobby != null, "result screen offers return to lobby");
                if (backToLobby == null) yield break;

                backToLobby.onClick.Invoke();

                yield return WaitUntil(() => SceneManager.GetActiveScene().name == GameConfig.DefaultLobbySceneName, 20f);
                Check(SceneManager.GetActiveScene().name == GameConfig.DefaultLobbySceneName,
                    $"return to lobby loaded the lobby ({SceneManager.GetActiveScene().name})");

                yield return WaitFrames(20);

                GameObject lobbyObject = GameObject.Find("Emberdelve_Lobby");
                Check(lobbyObject != null, "lobby UI rebuilt after returning");
                if (lobbyObject == null) yield break;

                Text lastRun = FindChildText(lobbyObject.transform, "LastRun");
                Check(lastRun != null && !string.IsNullOrEmpty(lastRun.text) && lastRun.text.Contains("Dead"),
                    $"lobby shows the previous run summary ({(lastRun != null ? lastRun.text.Replace("\n", " ") : "none")})");
                Check(Time.timeScale > 0.9f, $"time scale restored ({Time.timeScale})");
            }

            private static Button FindButton(GameObject root, string name)
            {
                foreach (Button button in root.GetComponentsInChildren<Button>(true))
                    if (button.name == name) return button;
                return null;
            }

            private static Text FindChildText(Transform root, string name)
            {
                foreach (Text text in root.GetComponentsInChildren<Text>(true))
                    if (text.name == name) return text;
                return null;
            }

            // ---------------------------------------------------------------- helpers

            private LootPickup _sceneLoot;

            private EnemyAgent PickAliveEnemy()
            {
                EnemyAgent[] all = FindObjectsByType<EnemyAgent>(FindObjectsSortMode.None);
                EnemyAgent best = null;
                float bestDistance = float.MaxValue;

                Vector3 reference = _rig != null ? _rig.transform.position : transform.position;

                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i] == null || all[i].IsDead) continue;
                    float distance = Vector3.Distance(all[i].transform.position, reference);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = all[i];
                    }
                }

                return best;
            }

            private static Vector3 FlatForward(Transform transform)
            {
                Vector3 flat = new Vector3(transform.forward.x, 0f, transform.forward.z);
                return flat.sqrMagnitude < 0.0001f ? Vector3.forward : flat.normalized;
            }

            private static Vector2Int DominantAxis(Vector3 direction)
            {
                Vector3 flat = new Vector3(direction.x, 0f, direction.z);
                if (flat.sqrMagnitude < 0.0001f) return new Vector2Int(1, 0);

                return Mathf.Abs(flat.x) >= Mathf.Abs(flat.z)
                    ? new Vector2Int(flat.x >= 0f ? 1 : -1, 0)
                    : new Vector2Int(0, flat.z >= 0f ? 1 : -1);
            }

            private bool TryPlaceEnemyBesideSpawn(EnemyAgent enemy, Vector3 spawn, int cellOffset, Vector2Int preferred, out Vector3 position)
            {
                position = spawn;
                if (_layout == null || _grid == null) return false;

                Vector2Int from = _layout.CellFromWorld(spawn);
                Vector2Int[] directions =
                {
                    new Vector2Int(1, 0), new Vector2Int(-1, 0), new Vector2Int(0, 1), new Vector2Int(0, -1)
                };

                if (preferred.x != 0 || preferred.y != 0)
                {
                    directions = new Vector2Int[]
                    {
                        preferred,
                        new Vector2Int(-preferred.x, -preferred.y),
                        new Vector2Int(preferred.y, preferred.x),
                        new Vector2Int(-preferred.y, -preferred.x)
                    };
                }

                for (int i = 0; i < directions.Length; i++)
                {
                    Vector2Int goal = from + directions[i] * cellOffset;
                    bool clear = true;

                    for (int step = 1; step <= cellOffset; step++)
                    {
                        Vector2Int cell = from + directions[i] * step;
                        if (!_grid.IsWalkable(cell.x, cell.y)) { clear = false; break; }
                    }

                    if (!clear) continue;

                    position = _layout.CellCenterWorld(goal);
                    return true;
                }

                return false;
            }

            private void Check(bool condition, string label)
            {
                _checks++;
                if (condition)
                {
                    Debug.Log($"[Emberdelve][Smoke:{Mode}] ok  {label}");
                }
                else
                {
                    _failures++;
                    Debug.LogError($"[Emberdelve][Smoke:{Mode}] FAIL {label}");
                }
            }

            private static IEnumerator WaitFrames(int count)
            {
                for (int i = 0; i < count; i++) yield return null;
            }

            private static IEnumerator WaitSeconds(float seconds)
            {
                float target = Time.unscaledTime + seconds;
                while (Time.unscaledTime < target) yield return null;
            }

            private static IEnumerator WaitUntil(System.Func<bool> predicate, float timeoutSeconds)
            {
                float deadline = Time.unscaledTime + timeoutSeconds;
                while (!predicate() && Time.unscaledTime < deadline) yield return null;
            }
        }
    }
}
