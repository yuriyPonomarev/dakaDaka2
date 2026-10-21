using System.Collections;
using System.Collections.Generic;
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
using Emberdelve.InputLayer;
using Emberdelve.Interaction;
using Emberdelve.Items;
using Emberdelve.Level;
using Emberdelve.Player;
using Emberdelve.Skills;

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

        /// <summary>
        /// Session key holding the code the editor must exit with. SessionState is used for the other
        /// markers because it survives the play mode transition, and the exit code needs the same trip:
        /// it is written inside play mode by the completion pass or the watchdog and consumed by the
        /// ExitingPlayMode handler after the transition back to the editor.
        /// </summary>
        private const string ExitCodeKey = "Emberdelve.Smoke.ExitCode";

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

        /// <summary>P4b: warrior sword and crossbow through the REAL input -> component -> authority path.</summary>
        public static void RunRealCombatWarrior() => StartRun("realcombat");

        /// <summary>P4b: archer bow and rapier through the REAL input -> component -> authority path.</summary>
        public static void RunRealCombatArcher() => StartRun("realcombatarcher");

        /// <summary>Milestone 5: grid storage, equipment slots and containers.</summary>
        public static void RunInventoryFlow() => StartRun("items");

        /// <summary>U1C1: satchel pointer routing and conserving cursor operations (move/swap/merge/equip/cancel).</summary>
        public static void RunInventoryUiFlow() => StartRun("inventoryui");

        private static void StartRun(string mode)
        {
            EmberdelveAssetLibrary.BuildAll();

            SessionState.SetBool(DoneKey, false);
            SessionState.SetString(ModeKey, mode);
            SessionState.SetInt(ExitCodeKey, 0);

            // The item flows need the archer, whose class carries no shield, so they go through the lobby the
            // same way the archer flow does. The satchel pointer flow needs that same class plus the dungeon
            // HUD that builds the screen it clicks, so it descends through the lobby too.
            string scenePath = (mode == "lobby" || mode == "archer" || mode == "items" || mode == "inventoryui" || mode == "realcombatarcher")
                ? EmberdelvePaths.LobbyScenePath
                : EmberdelvePaths.DungeonScenePath;
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
                SessionState.SetInt(ExitCodeKey, 1);
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
                    // Whatever the harness recorded (completion pass or watchdog) is the code the process
                    // has to leave with. A run that logged FAIL must not come out of the editor as 0.
                    int exitCode = SessionState.GetInt(ExitCodeKey, 1);
                    SessionState.SetBool(DoneKey, false);
                    SessionState.SetString(ModeKey, string.Empty);
                    Debug.Log($"[Emberdelve][Smoke] Play mode finished, exiting with stored code {exitCode}");
                    EditorApplication.delayCall += () => EditorApplication.Exit(exitCode);
                }
                else if (!string.IsNullOrEmpty(SessionState.GetString(ModeKey, string.Empty)))
                {
                    // Play mode ended without the harness finishing its pass. An unfinished run is not a
                    // pass, so it leaves with a failure code instead of hanging the batch process.
                    Debug.LogError("[Emberdelve][Smoke] Play mode ended before the harness finished its pass - reporting failure");
                    SessionState.SetInt(ExitCodeKey, 1);
                    SessionState.SetString(ModeKey, string.Empty);
                    EditorApplication.delayCall += () => EditorApplication.Exit(1);
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

            /// <summary>
            /// Every command sequence the real combat components actually claimed, in order. The shared
            /// per-entity allocator is verified from these observed claims instead of assumed.
            /// </summary>
            private readonly List<SequenceClaim> _claims = new List<SequenceClaim>();

            private readonly struct SequenceClaim
            {
                public readonly int CombatId;
                public readonly int Sequence;
                public readonly string Source;

                public SequenceClaim(int combatId, int sequence, string source)
                {
                    CombatId = combatId;
                    Sequence = sequence;
                    Source = source;
                }

                public override string ToString() => $"{Source}#{CombatId}:{Sequence}";
            }

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
                    SessionState.SetInt(ExitCodeKey, 1);
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
                else if (Mode == "realcombatarcher")
                {
                    yield return LobbyFlow();
                    _rig = FindFirstObjectByType<PlayerRig>();
                    _director = FindFirstObjectByType<RunDirector>();
                    yield return RealCombatArcherFlow();
                }
                else if (Mode == "items")
                {
                    // Same reason: the item tests need the class that carries no shield by default.
                    yield return LobbyFlow();
                    yield return ItemFlow();
                }
                else if (Mode == "inventoryui")
                {
                    // The satchel screen is built by the dungeon HUD, so a run has to exist before the pointer
                    // fixtures can click it. Same lobby route as the item tests: the pointer fixtures measure
                    // equipment transfers as well, and the archer carries no shield to get in the way.
                    yield return LobbyFlow();
                    _rig = FindFirstObjectByType<PlayerRig>();
                    _director = FindFirstObjectByType<RunDirector>();
                    yield return EmberdelveInventoryUiChecks.Run(_rig, _director, Check);
                }
                else if (_rig == null || _director == null)
                {
                    Check(false, "dungeon scene contains a PlayerRig and a RunDirector");
                }
                else
                {
                    yield return DungeonBaseline();

                    if (Mode == "combat") yield return CombatFlow();
                    else if (Mode == "realcombat") yield return RealCombatWarriorFlow();
                    else if (Mode == "ai") yield return EnemyAiFlow();
                    else if (Mode == "extract") yield return ExtractionFlow();
                    else if (Mode == "death") yield return DeathFlow();
                    else if (Mode == "resultloop") yield return ResultLoopFlow();
                }

                int exitCode = _failures == 0 ? 0 : 1;
                Debug.Log($"[Emberdelve][Smoke:{Mode}] {(_failures == 0 ? "PASS" : "FAIL")} checks={_checks} failures={_failures} exit={exitCode}");
                SessionState.SetInt(ExitCodeKey, exitCode);
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

                // Record what the real components claim before anything attacks, so the ordering of the
                // shared allocator can be asserted from observed sequences.
                _claims.Clear();
                WatchComponentSequences();

                // Runs first so the real crossbow shot happens before the synthetic authority tests below
                // have spent the sword cooldown clock. (Those tests now wait for the authority's own
                // cooldown instead of forging timestamps; see WaitForWeaponReady.)
                yield return LoadoutFlow();

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
                Physics.SyncTransforms();
                yield return WaitFrames(2);

                Check(enemy.Brain != null && enemy.Brain.State != EnemyState.Patrol,
                    $"enemy noticed the player at close range (state {(enemy.Brain != null ? enemy.Brain.State.ToString() : "n/a")})");

                // A real swing through MeleeCombat: windup -> active window -> arc scan -> authority.
                MeleeCombat melee = _rig.GetComponent<MeleeCombat>();
                Check(melee != null && melee.enabled, "warrior melee combat enabled");
                if (melee != null && melee.enabled)
                {
                    // Freeze the agent first. Teleporting an enemy leaves its nav path stale, and a stale
                    // path walks it out of the sword's probe mid-swing - a harness artifact, not a property
                    // of the swing. The swing itself is untouched: real windup, real arc scan, real
                    // authority, real damage.
                    enemy.enabled = false;

                    // Place the enemy by the measurement the swing actually makes: the distance from the
                    // point the scan originates at (the eye) to the enemy CombatCenter, not root-to-root.
                    float placedDistance = PlaceCombatCenterInRange(enemy, _rig.AimOrigin, arena, enemySpot, 1.9f);
                    _rig.transform.rotation = Quaternion.LookRotation(enemy.CombatCenter - _rig.AimOrigin);
                    Physics.SyncTransforms();
                    yield return WaitFrames(2);

                    float swingHealthBefore = enemy.CurrentHealth;
                    int swingAcceptedBefore = _localAuthority != null ? _localAuthority.AcceptedCount : 0;
                    int swingRejectedBefore = _localAuthority != null ? _localAuthority.RejectedCount : 0;

                    // Record the command sequence the swing actually claimed, so a rejection can be
                    // attributed to a specific authority rule instead of guessed at.
                    int swingSequence = -1;
                    System.Action<AttackIntent> swingStarted = intent => swingSequence = intent.Sequence;
                    melee.AttackStarted += swingStarted;

                    melee.BeginSwing();

                    Vector3 scanOrigin = _rig.AimOrigin;
                    Vector3 scanDirection = _rig.AimDirection;
                    Vector3 toEnemy = enemy.CombatCenter - scanOrigin;
                    Debug.Log($"[Emberdelve][Smoke:{Mode}] melee swing diag: placedDist={placedDistance:F2} origin={scanOrigin} dir={scanDirection} enemyCenter={enemy.CombatCenter} dist={toEnemy.magnitude:F2} angle={Vector3.Angle(scanDirection, toEnemy):F1} reach={weapon.reach:F2}+{weapon.hitSphereRadius:F2} arc={weapon.arcAngle:F0} enabled={melee.enabled} swinging={melee.IsSwinging}");

                    yield return WaitSeconds(0.3f);

                    Vector3 midOrigin = _rig.AimOrigin;
                    Vector3 midDirection = _rig.AimDirection;
                    Vector3 midToEnemy = enemy.CombatCenter - midOrigin;
                    System.Collections.Generic.List<HitCandidate> scan = new System.Collections.Generic.List<HitCandidate>(4);
                    int scanCount = HitScanner.CollectMeleeHits(midOrigin, midDirection, weapon, _rig.CombatId,
                        _director.Config != null ? _director.Config.hitMask : Physics.AllLayers,
                        _director.Config != null ? _director.Config.levelMask : Physics.DefaultRaycastLayers,
                        scan, 4);
                    Debug.Log($"[Emberdelve][Smoke:{Mode}] melee active diag: origin={midOrigin} enemyCenter={midToEnemy + midOrigin} dist={midToEnemy.magnitude:F2} angle={Vector3.Angle(midDirection, midToEnemy):F1} scanHits={scanCount} swinging={melee.IsSwinging} health={enemy.CurrentHealth:F0}");

                    yield return WaitSeconds(0.9f);

                    enemy.enabled = true;

                    bool swingDamaged = enemy.CurrentHealth < swingHealthBefore;
                    bool swingAcceptedHit = _localAuthority != null && _localAuthority.AcceptedCount > swingAcceptedBefore;

                    Check(swingDamaged,
                        $"melee swing damaged the enemy ({swingHealthBefore:F0} -> {enemy.CurrentHealth:F0})");
                    Check(swingAcceptedHit, "authority accepted the melee hit");

                    melee.AttackStarted -= swingStarted;
                    if (!swingDamaged || !swingAcceptedHit)
                    {
                        SequenceDiag($"meleeSwing rejected {swingRejectedBefore} -> {(_localAuthority != null ? _localAuthority.RejectedCount : 0)}",
                            _rig.CombatId, swingSequence);
                    }
                }

                // Ordering proof for the shared allocator: the crossbow shot and the sword swing are two
                // different components attacking the same combat entity, and their claimed sequences must
                // strictly increase. That is exactly what the authority's anti-replay rule requires.
                Check(ClaimsStrictlyIncrease(out string claimTrail),
                    $"real attack sequences strictly increase per combat entity ({claimTrail})");

                // A legitimate swing can kill its target outright (26 base damage, x1.8 on a head hit,
                // against an enemy the crossbow shot already weakened). The validation tests below must
                // attack a live entity: SubmitAttack returns false for a dead target without counting a
                // rejection. So re-acquire and re-place a live target rather than attacking a corpse.
                if (enemy.IsDead)
                {
                    Debug.Log($"[Emberdelve][Smoke:{Mode}] swing target #{enemy.CombatId} died to the real swing; validation tests need a live target");
                    enemy = PickAliveEnemy();
                    if (enemy == null) Check(false, "a live enemy is available for the authority validation tests");
                    if (enemy == null) yield break;

                    if (!TryPlaceEnemyBesideSpawn(enemy, _rig.transform.position, 1, DominantAxis(_rig.transform.forward), out enemySpot))
                    {
                        Check(false, "could not place the validation target next to the player");
                        yield break;
                    }

                    enemy.transform.position = enemySpot;
                    Physics.SyncTransforms();
                    float validationDistance = PlaceCombatCenterInRange(enemy, _rig.AimOrigin, _rig.transform.position, enemySpot, 1.9f);
                    Physics.SyncTransforms();
                    yield return WaitFrames(2);
                    Debug.Log($"[Emberdelve][Smoke:{Mode}] validation target diag: #{enemy.CombatId} health={enemy.CurrentHealth:F0} centerDist={validationDistance:F2}");
                }

                int acceptedBefore = _localAuthority != null ? _localAuthority.AcceptedCount : 0;
                int rejectedBefore = _localAuthority != null ? _localAuthority.RejectedCount : 0;

                // The cooldown is measured on the AUTHORITY clock, never on a timestamp inside the intent,
                // so these intents carry plain Time.time and the test waits for the real cooldown instead of
                // stamping synthetic future times. The target is frozen so it cannot walk or strike back
                // while the fixture waits on real time; it is re-enabled afterwards if it survives.
                enemy.enabled = false;
                yield return WaitForWeaponReady(_rig.CombatId, weapon);

                // Validation must refuse a hit claimed 40m away with a 2m sword.
                int tooFarSequence = CombatRegistry.NextSequence(_rig.CombatId);
                int consumedBeforeTooFar = _localAuthority != null ? _localAuthority.LastSequenceFor(_rig.CombatId) : -1;
                AttackIntent tooFar = new AttackIntent(_rig.CombatId, EntityKind.LocalPlayer, weapon, 1f,
                    _rig.CombatCenter, Vector3.forward, weapon.reach, tooFarSequence, Time.time);
                bool farAccepted = _authority.SubmitAttack(tooFar,
                    new HitCandidate(enemy.CombatId, _rig.CombatCenter + Vector3.forward * 40f, false));
                Check(!farAccepted, "authority rejects a hit point claimed 40 m from the target (hit-point rule; real melee reach rejection is in EmberdelveCombatAuthorityGate)");
                Check(_localAuthority != null && _localAuthority.LastAttackRejectReason.Contains("not on the target"),
                    "the far claim is rejected as a hit point that is not on the target (" + (_localAuthority != null ? _localAuthority.LastAttackRejectReason : "n/a") + ")");
                if (farAccepted) SequenceDiag("tooFar", _rig.CombatId, tooFarSequence);

                // The replay test reuses the exact sequence the authority has now consumed. tooFar passed
                // the sequence rule - which is what makes it the last consumed one - and only then failed
                // on geometry. Asserted before the replay so the reuse is proven, not assumed.
                Check(_localAuthority != null && tooFarSequence > consumedBeforeTooFar &&
                      _localAuthority.LastSequenceFor(_rig.CombatId) == tooFarSequence,
                      $"authority consumed {consumedBeforeTooFar} -> {tooFarSequence} for #{_rig.CombatId}, so the replay reuses a live sequence");

                AttackIntent replay = new AttackIntent(_rig.CombatId, EntityKind.LocalPlayer, weapon, 1f,
                    _rig.CombatCenter, Vector3.forward, weapon.reach, tooFarSequence, Time.time);
                bool replayAccepted = _authority.SubmitAttack(replay, new HitCandidate(enemy.CombatId, enemy.CombatCenter, false));
                Check(!replayAccepted, "authority rejects a repeated command sequence");
                Check(_localAuthority != null && _localAuthority.LastAttackRejectReason.Contains("sequence"),
                    "the replay is rejected by the sequence rule (" + (_localAuthority != null ? _localAuthority.LastAttackRejectReason : "n/a") + ")");
                if (replayAccepted) SequenceDiag("replay", _rig.CombatId, tooFarSequence);

                // Cooldown. Only an ACCEPTED attack spends the weapon cooldown on the authority clock, so
                // one legitimate attack is made first, then an immediate second one on a fresh sequence is the
                // only thing that can trip the cooldown rule. Its claimed timestamp is a far-future stamp on
                // purpose: the authority ignores it.
                float healthBefore = enemy.CurrentHealth;
                int firstSequence = CombatRegistry.NextSequence(_rig.CombatId);
                Check(firstSequence > tooFarSequence, $"the first legitimate attack claims a fresh sequence ({tooFarSequence} -> {firstSequence})");
                Vector3 firstOrigin = _rig.CombatCenter;
                AttackIntent first = new AttackIntent(_rig.CombatId, EntityKind.LocalPlayer, weapon, 1f,
                    firstOrigin, (enemy.CombatCenter - firstOrigin).normalized, weapon.reach + weapon.hitSphereRadius, firstSequence, Time.time);
                bool firstAccepted = _authority.SubmitAttack(first, new HitCandidate(enemy.CombatId, enemy.CombatCenter, false));
                Check(firstAccepted, "authority accepts a legitimate attack once the cooldown has expired (" + (_localAuthority != null ? _localAuthority.LastAttackRejectReason : "n/a") + ")");

                // The legitimate attack can kill a weakened target; the cooldown rule precedes every geometry
                // rule, so the probe may use any live hostile id and can only fail on the cooldown.
                EnemyAgent spamTarget = enemy.IsDead ? PickAliveEnemy() : enemy;
                Check(spamTarget != null, "a live hostile is available for the cooldown probe");
                if (spamTarget == null) yield break;

                int spamSequence = CombatRegistry.NextSequence(_rig.CombatId);
                Check(spamSequence > firstSequence,
                    $"the cooldown intent claims a fresh sequence ({firstSequence} -> {spamSequence})");
                AttackIntent spam = new AttackIntent(_rig.CombatId, EntityKind.LocalPlayer, weapon, 1f,
                    firstOrigin, (enemy.CombatCenter - firstOrigin).normalized, weapon.reach + weapon.hitSphereRadius, spamSequence, Time.time + 100f);
                bool spamAccepted = _authority.SubmitAttack(spam, new HitCandidate(spamTarget.CombatId, spamTarget.CombatCenter, false));
                Check(!spamAccepted, "authority rejects attacks faster than the weapon cooldown");
                Check(_localAuthority != null && _localAuthority.LastAttackRejectReason.Contains("cooldown"),
                    "the spam is rejected by the cooldown rule even with a far-future timestamp (" + (_localAuthority != null ? _localAuthority.LastAttackRejectReason : "n/a") + ")");
                if (spamAccepted) SequenceDiag("spam", _rig.CombatId, spamSequence);

                Check(_localAuthority != null && _localAuthority.RejectedCount >= rejectedBefore + 3 &&
                      _localAuthority.AcceptedCount == acceptedBefore + 1,
                    $"rejections recorded (rejected {(_localAuthority != null ? _localAuthority.RejectedCount : 0)}, accepted {(_localAuthority != null ? _localAuthority.AcceptedCount : 0)})");

                // Now attack properly until the enemy drops. Every one of these is meant to be valid, so
                // each must claim a sequence strictly greater than the one the authority last consumed, and
                // each waits for the authority real weapon cooldown before it is submitted.
                int accepted = firstAccepted ? 1 : 0;
                bool everySequenceFresh = true;
                for (int i = 0; i < 16 && !enemy.IsDead; i++)
                {
                    yield return WaitForWeaponReady(_rig.CombatId, weapon);

                    Vector3 origin = _rig.CombatCenter;
                    Vector3 direction = (enemy.CombatCenter - origin).normalized;
                    int consumedBefore = _localAuthority != null ? _localAuthority.LastSequenceFor(_rig.CombatId) : -1;
                    int freshSequence = CombatRegistry.NextSequence(_rig.CombatId);
                    if (freshSequence <= consumedBefore) everySequenceFresh = false;

                    AttackIntent intent = new AttackIntent(_rig.CombatId, EntityKind.LocalPlayer, weapon, 1f,
                        origin, direction, weapon.reach + weapon.hitSphereRadius, freshSequence, Time.time);

                    if (_authority.SubmitAttack(intent, new HitCandidate(enemy.CombatId, enemy.CombatCenter, false))) accepted++;
                    else SequenceDiag($"validAttack[{i}] consumedBefore={consumedBefore}", _rig.CombatId, freshSequence);
                    yield return WaitFrames(1);
                }

                if (!enemy.IsDead) enemy.enabled = true;

                Check(everySequenceFresh, "every intended-valid attack claimed a sequence greater than the last one consumed");
                Check(accepted > 0, $"authority accepted valid attacks ({accepted})");
                Check(enemy.CurrentHealth < healthBefore, $"enemy health dropped ({healthBefore:F0} -> {enemy.CurrentHealth:F0})");
                Check(enemy.IsDead, "enemy died from accumulated damage");
                Check(_director.Kills > 0, $"run director counted slain enemies ({_director.Kills})");
                Check(_localAuthority != null && _localAuthority.AcceptedCount >= acceptedBefore + accepted,
                    "authority counted accepted attacks");

                yield return DroppedLootFlow();
                yield return LootFlow();
                yield return DefenseFlow();
                yield return WarriorSkillFlow();
            }

            // ---------------------------------------------------------------- loadout switching

            /// <summary>
            /// Warrior primary/secondary slots: sword and crossbow, both present, switching in both
            /// directions, the right combat component going live, the crossbow actually damaging an enemy
            /// through the authority, and ammunition surviving a swap instead of refilling for free.
            /// Leaves the rig back on the primary weapon so the guard tests that follow are unaffected.
            /// </summary>
            private IEnumerator LoadoutFlow()
            {
                ClassLoadout loadout = _rig.Loadout;
                Check(loadout != null, "player rig exposes a loadout");
                if (loadout == null) yield break;

                MeleeCombat melee = _rig.GetComponent<MeleeCombat>();
                RangedCombat ranged = _rig.GetComponent<RangedCombat>();

                Check(loadout.Primary != null && loadout.Primary.weaponId == "ashen_short_sword",
                    $"warrior primary is the sword ({Name(loadout.Primary)})");
                Check(loadout.Secondary != null && loadout.Secondary.weaponId == "ember_crossbow",
                    $"warrior secondary is the crossbow ({Name(loadout.Secondary)})");
                Check(loadout.EquippedCount == 2, $"warrior kit exposes two weapon slots ({loadout.EquippedCount})");
                Check(!loadout.UsingSecondary && loadout.ActiveWeapon == loadout.Primary, "run starts with the primary in hand");

                Check(melee != null && melee.enabled && melee.Weapon == loadout.Primary, "melee component live for the primary");
                Check(ranged != null && !ranged.enabled, "ranged component off while a melee primary is in hand");
                Check(_rig.CanGuard && _rig.Defense.CanBlock, $"sword main hand can block ({_rig.Defense})");
                Check(!_rig.CanParry, $"sword plus shield kit declares no parry ({_rig.Defense})");

                Check(loadout.SwitchWeapon(), "switch to the secondary accepted");
                Check(loadout.UsingSecondary && loadout.ActiveWeapon != null && loadout.ActiveWeapon.weaponId == "ember_crossbow",
                    $"crossbow now in hand ({Name(loadout.ActiveWeapon)})");
                Check(ranged.enabled && ranged.Weapon != null && ranged.Weapon.weaponId == "ember_crossbow",
                    "ranged component activated for the crossbow");
                Check(!melee.enabled, "melee component deactivated for the crossbow");
                Check(!_rig.CanGuard && !_rig.Defense.CanBlock, $"crossbow main hand carries no block capability ({_rig.Defense})");

                ClassLoadout.RangedAmmoState bolts = loadout.GetAmmoState(loadout.Secondary);
                Check(bolts != null && bolts.reserve > 0, $"crossbow starts with reserve bolts ({bolts})");
                Check(ranged.Magazine == loadout.Secondary.magazineSize, $"crossbow magazine full at {ranged.Magazine}");

                EnemyAgent target = PickAliveEnemy();
                Check(target != null, "an enemy is available for the crossbow shot");
                if (target != null)
                {
                    if (TryPlaceEnemyBesideSpawn(target, _rig.transform.position, 2, DominantAxis(_rig.transform.forward), out Vector3 spot))
                    {
                        target.transform.position = spot;
                        _rig.transform.rotation = Quaternion.LookRotation(target.CombatCenter - _rig.GetMuzzlePosition());
                        Physics.SyncTransforms();
                        yield return WaitFrames(2);

                        int magazineBefore = ranged.Magazine;
                        int acceptedBefore = _localAuthority != null ? _localAuthority.AcceptedCount : 0;
                        float enemyHealthBefore = target.CurrentHealth;

                        ranged.Fire(1f);
                        yield return WaitSeconds(0.6f);

                        Check(ranged.Magazine < magazineBefore, $"crossbow shot consumed a bolt ({magazineBefore} -> {ranged.Magazine})");
                        Check(target.CurrentHealth < enemyHealthBefore,
                            $"crossbow damaged the enemy ({enemyHealthBefore:F0} -> {target.CurrentHealth:F0})");
                        Check(_localAuthority != null && _localAuthority.AcceptedCount > acceptedBefore,
                            "authority accepted the crossbow attack");
                    }
                    else
                    {
                        Check(false, "no walkable cell two cells ahead of the player for the crossbow shot");
                    }
                }

                int spentMagazine = ranged.Magazine;
                int spentReserve = ranged.Reserve;

                Check(loadout.SwitchWeapon(), "switch back to the primary accepted");
                Check(!loadout.UsingSecondary && melee.enabled && !ranged.enabled, "primary and melee component restored");
                Check(_rig.CanGuard, "block capability returns with the sword in hand");

                Check(loadout.SwitchWeapon(), "switch to the crossbow a second time accepted");
                Check(ranged.enabled && ranged.Magazine == spentMagazine && ranged.Reserve == spentReserve,
                    $"crossbow ammo persisted across the swap ({spentMagazine}/{spentReserve} -> {ranged.Magazine}/{ranged.Reserve})");

                Check(loadout.SwitchToSlot(0), "switch back to slot 0 accepted");
                Check(!loadout.UsingSecondary && melee.enabled && !ranged.enabled,
                    "loadout left on the primary weapon for the rest of the run");

                // The authority tracks the last accepted attack time per combat id. The crossbow shot above
                // is the player's most recent accepted attack, so let the sword cooldown expire before the
                // melee assertions that follow, otherwise a legitimate swing is rejected as spam.
                yield return WaitSeconds(1.4f);

                Debug.Log($"[Emberdelve][Smoke:{Mode}] loadout diag: primary={Name(loadout.Primary)} secondary={Name(loadout.Secondary)} active={Name(loadout.ActiveWeapon)} ammo={bolts}");
            }

            private static string Name(WeaponData weapon)
            {
                return weapon != null ? weapon.displayName + " [" + weapon.weaponId + "]" : "none";
            }

            // ---------------------------------------------------------------- telegraph + blocking

            /// <summary>
            /// Milestone 2 assertions, driven through the same public entry points input and AI use:
            /// the warned wedge, the direction the hit is actually submitted along, and the directional
            /// guard reduction. Damage is measured on the rig, not on an event subscription, so the
            /// HUD-visible number is the number being asserted.
            /// </summary>
            private IEnumerator DefenseFlow()
            {
                Check(_rig.Defense.CanBlock, $"warrior kit resolves a block capability ({_rig.Defense})");
                Check(!_rig.Defense.CanParry, $"warrior kit resolves no parry capability yet ({_rig.Defense})");

                WeaponData shield = AssetDatabase.LoadAssetAtPath<WeaponData>(EmberdelvePaths.Data + "/Weapon_EmberbulwarkShield.asset");
                Check(shield != null, "shield weapon asset exists");
                if (shield != null)
                {
                    Check(shield.IsShield, "shield asset carries the shield defense role");
                    Check(shield.CanBlock, "shield declares block");
                    Check(!shield.CanParry, "shield can never parry");
                    DefenseCapability declared = DefenseCapability.Block | DefenseCapability.Parry;
                    DefenseCapability allowed = DefenseProfile.Sanitize(declared, shield.IsShield);
                    Check((allowed & DefenseCapability.Block) != 0 && (allowed & DefenseCapability.Parry) == 0,
                        "the shield rule strips Parry from anything a shield declares");
                }

                Check(!_rig.IsDead, $"player still alive for the defense tests ({_rig.CurrentHealth:F0}/{_rig.MaxHealth:F0})");
                if (_rig.IsDead || _authority == null) yield break;

                EnemyAgent enemy = PickAliveEnemy();
                Check(enemy != null, "an alive enemy is available for the defense tests");
                if (enemy == null) yield break;

                if (!TryPlaceEnemyBesideSpawn(enemy, _rig.transform.position, 1, DominantAxis(_rig.transform.forward), out Vector3 enemySpot))
                {
                    Check(false, "no walkable cell next to the player for the telegraph test");
                    yield break;
                }

                enemy.enabled = true;
                enemy.transform.position = enemySpot;
                Physics.SyncTransforms();
                yield return WaitFrames(3);

                // Let the real brain start a real windup.
                float deadline = Time.unscaledTime + 9f;
                while (!enemy.IsWindingUp && Time.unscaledTime < deadline) yield return null;

                Check(enemy.IsWindingUp, $"enemy entered a windup telegraph (state {(enemy.Brain != null ? enemy.Brain.State.ToString() : "n/a")})");
                if (!enemy.IsWindingUp) yield break;

                Vector3 warned = new Vector3(enemy.Brain.TelegraphDirection.x, 0f, enemy.Brain.TelegraphDirection.z);
                warned.Normalize();

                Vector3 toEnemy = enemy.CombatCenter - _rig.transform.position;
                toEnemy.y = 0f;
                float warnedAngle = Vector3.Angle(warned, -toEnemy);
                Check(warnedAngle < 25f, $"telegraph points at the player it is winding up against ({warnedAngle:F1} deg off)");

                AttackTelegraphVisual visual = null;
                foreach (AttackTelegraphVisual candidate in FindObjectsByType<AttackTelegraphVisual>(FindObjectsSortMode.None))
                {
                    if (candidate != null && candidate.transform.parent == enemy.transform) { visual = candidate; break; }
                }

                Check(visual != null, "enemy carries a procedural attack telegraph visual");
                if (visual != null)
                {
                    Check(visual.gameObject.activeInHierarchy, "telegraph is visible during the windup");
                    float visualAngle = Vector3.Angle(visual.transform.forward, warned);
                    Check(visualAngle < 12f, $"telegraph visual holds the committed world direction even while the enemy turns ({visualAngle:F1} deg off)");
                    Check(visual.GetComponent<Collider>() == null, "telegraph carries no collider, so it cannot affect a combat query");
                }

                float healthBeforeStrike = _rig.CurrentHealth;
                _capturedValid = false;
                _rig.Health.Damaged += CaptureDamage;
                deadline = Time.unscaledTime + 3f;
                while (_rig.CurrentHealth >= healthBeforeStrike && Time.unscaledTime < deadline) yield return null;

                float strikeDamage = healthBeforeStrike - _rig.CurrentHealth;
                Check(strikeDamage > 0f, $"the windup resolved into a real hit ({healthBeforeStrike:F0} -> {_rig.CurrentHealth:F0})");

                if (_capturedValid && strikeDamage > 0f)
                {
                    Vector3 landed = new Vector3(_capturedDamage.Direction.x, 0f, _capturedDamage.Direction.z);
                    float directionError = landed.sqrMagnitude < 0.0001f
                        ? 180f
                        : Vector3.Angle(warned, landed);
                    Check(directionError < 12f,
                        $"the hit arrived along the warned direction ({directionError:F1} deg between telegraph and damage direction)");
                }

                // Freeze the agent so the guard measurements below only contain the hits submitted here.
                enemy.enabled = false;
                Physics.SyncTransforms();
                yield return WaitFrames(2);

                yield return GuardReductionFlow(enemy);

                _rig.Health.Damaged -= CaptureDamage;

                yield return OffAxisWindupFlow(enemy);
            }

            /// <summary>
            /// R1a M1: the real enemy windup path with a player who strafes after the direction was committed.
            /// Inside the warned wedge the hit must still land (the enemy submits the player's own centre, which
            /// the authority accepts); outside the wedge no intent is submitted at all and the attack whiffs.
            /// </summary>
            private IEnumerator OffAxisWindupFlow(EnemyAgent enemy)
            {
                if (enemy == null || enemy.IsDead || enemy.Data == null) yield break;

                MeleeCombat melee = _rig.GetComponent<MeleeCombat>();
                if (melee != null) melee.enabled = false;
                _rig.SetGuardHeld(false);

                Vector3 anchor = _rig.transform.position;
                Vector3 enemyHome = enemy.transform.position;
                float standoff = Mathf.Min(1.8f, enemy.Data.attackRange * 0.85f);
                float halfCone = Mathf.Clamp(enemy.Data.attackTelegraphCone, 40f, 200f) * 0.5f;

                float[] angles = { 45f, -45f, halfCone + 40f };
                bool[] expectHit = { true, true, false };

                for (int i = 0; i < angles.Length; i++)
                {
                    string label = $"off-axis windup {angles[i]:F0} deg ({(expectHit[i] ? "inside" : "outside")} the {halfCone * 2f:F0} deg wedge)";

                    _rig.transform.position = anchor;
                    _rig.Health.SetHealth(_rig.MaxHealth);
                    enemy.transform.position = enemyHome;
                    Physics.SyncTransforms();
                    enemy.enabled = true;
                    yield return WaitFrames(2);

                    float deadline = Time.unscaledTime + 9f;
                    while (!enemy.IsWindingUp && Time.unscaledTime < deadline) yield return null;
                    Check(enemy.IsWindingUp, $"{label}: the real enemy brain started a windup");
                    if (!enemy.IsWindingUp) break;

                    // The direction is committed now. The player strafes to angles[i] off that direction.
                    Vector3 warned = enemy.Brain.TelegraphDirection;
                    warned.y = 0f;
                    warned.Normalize();
                    Vector3 offset = Quaternion.AngleAxis(angles[i], Vector3.up) * warned * standoff;
                    _rig.transform.position = new Vector3(enemy.transform.position.x + offset.x, anchor.y, enemy.transform.position.z + offset.z);
                    Physics.SyncTransforms();

                    float before = _rig.CurrentHealth;
                    int acceptedBefore = _localAuthority != null ? _localAuthority.AcceptedCount : 0;
                    int rejectedBefore = _localAuthority != null ? _localAuthority.RejectedCount : 0;

                    deadline = Time.unscaledTime + 4f;
                    while (enemy.IsWindingUp && Time.unscaledTime < deadline) yield return null;
                    yield return WaitFrames(2);

                    float lost = before - _rig.CurrentHealth;
                    if (expectHit[i])
                    {
                        Check(lost > 0f, $"{label}: the strike landed on the strafing player ({lost:F1} damage)");
                        Check(_localAuthority != null && _localAuthority.AcceptedCount == acceptedBefore + 1 && _localAuthority.RejectedCount == rejectedBefore,
                            $"{label}: the authority accepted exactly one attack and rejected none");
                    }
                    else
                    {
                        Check(lost <= 0f, $"{label}: no damage when the player left the warned wedge ({lost:F1})");
                        Check(_localAuthority != null && _localAuthority.AcceptedCount == acceptedBefore && _localAuthority.RejectedCount == rejectedBefore,
                            $"{label}: no intent was submitted for the whiff");
                        Check(enemy.LastAttackWhiffed, $"{label}: the enemy recorded a whiff");
                    }

                    enemy.enabled = false;
                    yield return WaitFrames(2);
                }

                enemy.enabled = false;
                _rig.transform.position = anchor;
                _rig.Health.SetHealth(_rig.MaxHealth);
                Physics.SyncTransforms();
                if (melee != null) melee.enabled = true;
            }

            private IEnumerator GuardReductionFlow(EnemyAgent enemy)
            {
                WeaponData claw = enemy.Data != null ? enemy.Data.attackWeapon : null;
                Check(claw != null, "enemy carries an attack weapon for the guard tests");
                if (claw == null) yield break;

                Vector3 origin = enemy.transform.position + Vector3.up * 1.2f;
                Vector3 toSource = _rig.CombatCenter - origin;
                toSource.y = 0f;
                if (toSource.sqrMagnitude < 0.0001f)
                {
                    Check(false, "enemy is on top of the player, cannot measure block arcs");
                    yield break;
                }

                toSource.Normalize();
                Vector3 towardSource = -toSource;
                float maxRange = Mathf.Max(1f, enemy.Data.attackRange) + 0.4f;
                int enemyConsumedBefore = _localAuthority != null ? _localAuthority.LastSequenceFor(enemy.CombatId) : -1;

                // MeleeCombat re-asserts guard from input every frame, and headless batch mode has no
                // mouse device, so it would clear the guard this harness sets directly.
                MeleeCombat melee = _rig.GetComponent<MeleeCombat>();
                if (melee != null) melee.enabled = false;

                _rig.SetGuardHeld(true);
                Check(_rig.IsGuarding, "guard is active for the block tests");

                // 1. Facing the source: inside the block arc, damage must be reduced.
                _rig.transform.rotation = Quaternion.LookRotation(towardSource);
                Physics.SyncTransforms();
                yield return WaitFrames(2);

                // Each submission waits for the enemy weapon real cooldown on the authority clock; the
                // three intents carry Time.time, not synthetic future stamps.
                yield return WaitForWeaponReady(enemy.CombatId, claw);
                float before = _rig.CurrentHealth;
                _capturedValid = false;
                int frontSequence = CombatRegistry.NextSequence(enemy.CombatId);
                bool frontAccepted = _authority.SubmitAttack(
                    new AttackIntent(enemy.CombatId, EntityKind.Enemy, claw, 1f, origin, toSource, maxRange, frontSequence, Time.time),
                    new HitCandidate(_rig.CombatId, _rig.CombatCenter, false));
                float blockedDamage = before - _rig.CurrentHealth;

                Check(frontAccepted, "authority accepted the front-facing attack");
                Check(_capturedValid, "player damage was recorded on the rig");
                Check(_capturedValid && _capturedDamage.Blocked, "front-facing guard reports the hit as blocked");
                Check(_capturedValid && _capturedDamage.BlockReduction > 0.2f,
                    $"guard reports a reduction ({(_capturedValid ? _capturedDamage.BlockReduction : 0f):F2})");
                Check(blockedDamage > 0f && blockedDamage < claw.baseDamage * 0.6f,
                    $"front-facing block reduced the damage ({blockedDamage:F1} from a {claw.baseDamage:F1} weapon)");
                Check(frontAccepted && frontSequence > enemyConsumedBefore,
                    $"enemy intent claimed a fresh sequence from the same allocator (consumed {enemyConsumedBefore} -> used {frontSequence})");
                if (!frontAccepted) SequenceDiag("guardFront", enemy.CombatId, frontSequence);

                // 2. Facing away while still holding guard: full damage.
                _rig.transform.rotation = Quaternion.LookRotation(-towardSource);
                Physics.SyncTransforms();
                yield return WaitFrames(2);

                yield return WaitForWeaponReady(enemy.CombatId, claw);
                before = _rig.CurrentHealth;
                _capturedValid = false;
                int behindSequence = CombatRegistry.NextSequence(enemy.CombatId);
                bool behindAccepted = _authority.SubmitAttack(
                    new AttackIntent(enemy.CombatId, EntityKind.Enemy, claw, 1f, origin, toSource, maxRange, behindSequence, Time.time),
                    new HitCandidate(_rig.CombatId, _rig.CombatCenter, false));
                float behindDamage = before - _rig.CurrentHealth;

                Check(_capturedValid && !_capturedDamage.Blocked, "guard does not hold when facing away from the source");
                Check(Mathf.Abs(behindDamage - claw.baseDamage) < 0.75f,
                    $"wrong-facing block takes full damage ({behindDamage:F1} vs weapon {claw.baseDamage:F1})");
                Check(blockedDamage < behindDamage - 0.5f,
                    $"correct-facing block takes less than wrong-facing ({blockedDamage:F1} < {behindDamage:F1})");

                // 3. Ninety degrees off: still outside the arc, so still full damage.
                Vector3 sideways = Vector3.Cross(Vector3.up, towardSource).normalized;
                _rig.transform.rotation = Quaternion.LookRotation(sideways);
                Physics.SyncTransforms();
                yield return WaitFrames(2);

                yield return WaitForWeaponReady(enemy.CombatId, claw);
                before = _rig.CurrentHealth;
                _capturedValid = false;
                int sideSequence = CombatRegistry.NextSequence(enemy.CombatId);
                bool sideAccepted = _authority.SubmitAttack(
                    new AttackIntent(enemy.CombatId, EntityKind.Enemy, claw, 1f, origin, toSource, maxRange, sideSequence, Time.time),
                    new HitCandidate(_rig.CombatId, _rig.CombatCenter, false));
                float sideDamage = before - _rig.CurrentHealth;

                Check(_capturedValid && !_capturedDamage.Blocked, "guard does not hold ninety degrees off the source");
                Check(sideDamage > blockedDamage + 0.5f,
                    $"side-facing block takes more damage than a correct block ({sideDamage:F1} vs {blockedDamage:F1})");

                // The enemy's own combat id goes through the same allocator as its real attacks did, so
                // these three synthetic intents must strictly increase and must all be accepted.
                Check(behindSequence > frontSequence && sideSequence > behindSequence,
                    $"enemy validation sequences strictly increase ({frontSequence} -> {behindSequence} -> {sideSequence})");
                Check(behindAccepted && sideAccepted, "later enemy intents were accepted, so their sequences were fresh");
                if (!behindAccepted) SequenceDiag("guardBehind", enemy.CombatId, behindSequence);
                if (!sideAccepted) SequenceDiag("guardSide", enemy.CombatId, sideSequence);

                _rig.SetGuardHeld(false);
                Check(!_rig.IsGuarding, "guard released after the block tests");

                if (melee != null) melee.enabled = true;

                Debug.Log($"[Emberdelve][Smoke:{Mode}] guard diag: blocked={blockedDamage:F1} behind={behindDamage:F1} side={sideDamage:F1} weapon={claw.baseDamage:F1} profile={_rig.Defense}");
            }

            private DamageInfo _capturedDamage;
            private bool _capturedValid;

            private void CaptureDamage(DamageInfo damage)
            {
                _capturedDamage = damage;
                _capturedValid = true;
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

                bool startedEmpty = _director.LootValue <= 0;
                Check(startedEmpty, $"run starts with no recovered loot (value {_director.LootValue})");

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

                    Check(!gate.CanInteract(out string emptyReason) && !string.IsNullOrEmpty(emptyReason),
                        $"gate refuses interaction with no loot ({emptyReason ?? "no reason"})");

                    if (interactor != null && _rig.Input != null)
                    {
                        EmberdelveInput input = _rig.Input;
                        float fullHold = gate.HoldDuration + 1.0f;
                        int endedCount = 0;
                        string lastToast = null;
                        System.Action<RunResult> onEnded = _ => endedCount++;
                        System.Action<string> onToast = message => lastToast = message;

                        // Live enemies would damage the player and legitimately interrupt the holds below, so the
                        // fixture parks them; damage is injected deliberately in step (3).
                        foreach (EnemyAgent agent in FindObjectsByType<EnemyAgent>(FindObjectsSortMode.None))
                            agent.gameObject.SetActive(false);
                        GameEvents.RunEnded += onEnded;
                        GameEvents.ToastRaised += onToast;

                        try
                        {
                            // (4) the authority itself refuses a direct call with zero loot.
                            bool directAccepted = _director.BeginExtraction(out string directReason);
                            Check(!directAccepted && !string.IsNullOrEmpty(directReason),
                                $"RunDirector.BeginExtraction refuses zero loot ({directReason ?? "no reason"})");
                            Check(_director.State == RunState.Running && endedCount == 0,
                                $"direct refused call left the run Running (got {_director.State})");

                            // (1) zero loot + a full real hold through the Interactor: nothing extracts. The toast is
                            // cleared first, so only the hold path itself can satisfy the surfaced-reason check.
                            lastToast = null;
                            input.InteractHeldOverride = true;
                            yield return WaitSeconds(fullHold);
                            input.InteractHeldOverride = false;

                            Check(_director.State == RunState.Running && endedCount == 0,
                                $"zero loot + full hold does not extract (state {_director.State})");
                            Check(!string.IsNullOrEmpty(lastToast) && lastToast.Contains("relic"),
                                $"hold path surfaced the refusal reason ({lastToast ?? "none"})");

                            // Let the bar drain to zero so later steps start from a known state.
                            yield return WaitUntil(() => interactor.HoldProgress <= 0.0001f, 5f);
                            Check(interactor.HoldProgress <= 0.0001f,
                                $"hold progress drained to zero after release ({interactor.HoldProgress:F3})");

                            // Give the player loot, as the original flow did.
                            LootPickup loot = FindFirstObjectByType<LootPickup>();
                            if (loot != null) loot.OnInteractInstant();
                            Check(_director.LootValue > 0, "loot requirement satisfied before extraction");
                            Check(gate.CanInteract(out string reason), $"gate accepts interaction ({reason ?? "ok"})");

                            // Disabled gameplay input blocks the hold even while the override says "held".
                            input.SetGameplayInputEnabled(false);
                            input.InteractHeldOverride = true;
                            yield return WaitSeconds(gate.HoldDuration * 0.6f);
                            Check(interactor.HoldProgress <= 0.0001f && _director.State == RunState.Running,
                                $"disabled input does not advance the hold ({interactor.HoldProgress:F2})");
                            input.InteractHeldOverride = false;
                            input.SetGameplayInputEnabled(true);
                            yield return WaitFrames(2);

                            // (3) damage mid-hold resets the progress.
                            input.InteractHeldOverride = true;
                            yield return WaitSeconds(gate.HoldDuration * 0.5f);
                            float before = interactor.HoldProgress;
                            Check(before > 0.25f && before < 0.9f, $"hold is part-way before damage ({before:F2})");
                            _rig.ApplyDamage(new DamageInfo(1f, _rig.transform.position, Vector3.forward,
                                _rig.transform.position + Vector3.forward, 0, EntityKind.Enemy, DamageType.Physical,
                                false, false, 0f));
                            yield return WaitFrames(2);
                            float after = interactor.HoldProgress;
                            Check(after < before * 0.5f && after < 0.2f,
                                $"damage mid-hold reset the progress ({before:F2} -> {after:F2})");
                            Check(_director.State == RunState.Running, "run still Running after the interrupted hold");

                            // (2) with loot, an uninterrupted full hold extracts.
                            yield return WaitSeconds(fullHold);
                            input.InteractHeldOverride = false;
                            yield return WaitFrames(3);
                            Check(endedCount == 1, $"full hold with loot extracted (RunEnded x{endedCount})");
                            Check(_director.State == RunState.Extracted, $"state is Extracted after the hold (got {_director.State})");

                            // A second attempt through every layer must not extract again.
                            gate.OnHoldComplete();
                            bool again = _director.BeginExtraction(out string againReason);
                            Check(!again && !string.IsNullOrEmpty(againReason), $"second BeginExtraction refused ({againReason ?? "no reason"})");
                            yield return WaitFrames(2);
                            Check(endedCount == 1, $"extraction fired exactly once even after repeat attempts (RunEnded x{endedCount})");
                        }
                        finally
                        {
                            GameEvents.RunEnded -= onEnded;
                            GameEvents.ToastRaised -= onToast;
                            input.InteractHeldOverride = null;
                        }
                    }
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
                int sequenceRejections = 0;

                for (int i = 0; i < 24 && !_rig.IsDead; i++)
                {
                    // Real cooldown wait on the authority clock instead of a stamp 1.2s per step ahead.
                    yield return WaitForWeaponReady(enemy.CombatId, claw);
                    if (_rig.IsDead) break;
                    enemy.transform.position = _rig.transform.position + new Vector3(0f, 0f, -1.1f);
                    Vector3 origin = enemy.CombatCenter;
                    Vector3 direction = (_rig.CombatCenter - origin).normalized;

                    int consumedBefore = _localAuthority != null ? _localAuthority.LastSequenceFor(enemy.CombatId) : -1;
                    int freshSequence = CombatRegistry.NextSequence(enemy.CombatId);
                    if (freshSequence <= consumedBefore) sequenceRejections++;

                    AttackIntent intent = new AttackIntent(enemy.CombatId, EntityKind.Enemy, claw, 1f,
                        origin, direction, claw.reach + claw.hitSphereRadius, freshSequence, Time.time);

                    if (_authority.SubmitAttack(intent, new HitCandidate(_rig.CombatId, _rig.CombatCenter, false))) accepted++;
                    else if (freshSequence <= consumedBefore) SequenceDiag($"deathAttack[{i}] consumedBefore={consumedBefore}", enemy.CombatId, freshSequence);
                    yield return WaitFrames(2);
                }

                // The enemy's own live attacks share this combat id, so its synthetic intents must still be
                // fresh. A rejection here would mean the allocator collided, not the cooldown clock.
                Check(sequenceRejections == 0, $"no enemy intent was rejected as a replay ({accepted} accepted)");

                Check(accepted > 0, $"authority accepted enemy attacks against the player ({accepted})");
                Check(_rig.CurrentHealth < healthAtStart, $"player took damage ({healthAtStart:F0} -> {_rig.CurrentHealth:F0})");
                Check(_rig.IsDead, "player health reached zero");
                Check(_director.State == RunState.Dead, $"run state is Dead (got {_director.State})");
                Check(GameObject.Find("Emberdelve_Result") != null, "result screen built after death");
                Check(RunContext.HasResult && RunContext.LastResult.Outcome == RunState.Dead,
                    "death recorded in the run result");

                // ---- Milestone 4: death has to shut the kit down, not leave it running
                ManaComponent mana = _rig.Mana;
                SkillCaster caster = _rig.Skills;
                Check(mana != null && caster != null, "dead rig still exposes its mana pool and skill caster");
                if (mana != null && caster != null)
                {
                    float manaAtDeath = mana.CurrentMana;
                    Check(!caster.CanCastSlot(0, out string deathReason) && deathReason == "dead",
                        $"skills are refused while dead ({deathReason})");
                    Check(!caster.TryCastSlot(0), "a dead player cannot cast");
                    Check(!caster.TryCastSlot(1), "a dead player cannot cast the second skill either");
                    Check(!_rig.Movement.IsDashing, "death cancelled any dash in progress");
                    Check(!mana.RegenerationAllowed, "death suspended mana regeneration");
                    Check(_rig.Status == null || _rig.Status.ActiveCount == 0, "death cleared the player's statuses");

                    yield return WaitSeconds(2f);
                    Check(Mathf.Abs(mana.CurrentMana - manaAtDeath) < 0.01f,
                        $"mana regeneration is stopped while dead ({manaAtDeath:F0} -> {mana.CurrentMana:F0})");
                }
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
                // U2A8 fixture migration. Old assumption: the live ceiling equals the class asset's
                // maxHealth field. Since U2A3 that field is the pristine BASE and the ceiling a run start
                // fills is the derived one: the archer authors CON 12 -> floor((12-10)/2) = 1 ->
                // 80 + 10*1 = 90 (Docs/Design/ATTRIBUTES.md). Both halves are asserted, so neither a missing
                // derivation nor a class asset written by equipment can satisfy this check.
                Check(chosen.maxHealth == 80,
                    $"archer class asset still authors the base health 80 ({chosen.maxHealth})");
                Check(Mathf.Approximately(rig.MaxHealth, 90f),
                    $"derived archer health applied at run start (got {rig.MaxHealth}, expected 80 + 10*1 = 90)");

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

                _claims.Clear();
                WatchComponentSequences();

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

                yield return ArcherLoadoutFlow();
                yield return ArcherSkillFlow();

                // Bow shot then rapier thrust: one combat entity, two components, one allocator. The
                // sequences must strictly increase or the authority would treat the rapier as a replay.
                Check(ClaimsStrictlyIncrease(out string archerTrail),
                    $"archer attack sequences strictly increase per combat entity ({archerTrail})");
                UnwatchComponentSequences();
            }

            /// <summary>
            /// Archer secondary slot: the rapier. Same switching assertions as the warrior side, plus the
            /// first place a Parry capability is actually claimed, and proof that swapping back to the bow
            /// restores the partially spent magazine rather than a fresh one.
            /// </summary>
            private IEnumerator ArcherLoadoutFlow()
            {
                ClassLoadout loadout = _rig.Loadout;
                Check(loadout != null, "archer rig exposes a loadout");
                if (loadout == null) yield break;

                RangedCombat ranged = _rig.GetComponent<RangedCombat>();
                MeleeCombat melee = _rig.GetComponent<MeleeCombat>();

                Check(loadout.Primary != null && loadout.Primary.weaponId == "hollow_bow",
                    $"archer primary is the bow ({Name(loadout.Primary)})");
                Check(loadout.Secondary != null && loadout.Secondary.weaponId == "ember_rapier",
                    $"archer secondary is the rapier ({Name(loadout.Secondary)})");
                Check(loadout.EquippedCount == 2, $"archer kit exposes two weapon slots ({loadout.EquippedCount})");

                int bowMagazine = ranged != null ? ranged.Magazine : 0;
                int bowReserve = ranged != null ? ranged.Reserve : 0;
                Check(bowMagazine > 0, $"bow ammo recorded before the swap ({bowMagazine} / {bowReserve})");

                Check(loadout.SwitchWeapon(), "archer can switch to the rapier");
                Check(loadout.UsingSecondary && loadout.ActiveWeapon.weaponId == "ember_rapier",
                    $"rapier now in hand ({Name(loadout.ActiveWeapon)})");
                Check(melee != null && melee.enabled && melee.Weapon != null && melee.Weapon.weaponId == "ember_rapier",
                    "melee component activated for the rapier");
                Check(ranged != null && !ranged.enabled, "ranged component deactivated for the rapier");

                Check(loadout.Secondary.CanParry, "rapier weapon data declares a parry capability");
                Check(_rig.CanParry && _rig.Defense.CanParry, $"rapier main hand reports that capability on the rig ({_rig.Defense})");
                Check(!_rig.CanGuard && !_rig.Defense.CanBlock,
                    $"rapier still cannot block for the archer, whose class reduction is zero ({_rig.Defense})");

                EnemyAgent target = PickAliveEnemy();
                Check(target != null, "an enemy is available for the rapier thrust");
                if (target != null && melee != null)
                {
                    if (TryPlaceEnemyBesideSpawn(target, _rig.transform.position, 1, DominantAxis(_rig.transform.forward), out Vector3 spot))
                    {
                        target.transform.position = spot;
                        Physics.SyncTransforms();
                        yield return WaitFrames(2);

                        float healthBefore = target.CurrentHealth;
                        int acceptedBefore = _localAuthority != null ? _localAuthority.AcceptedCount : 0;

                        // Freeze the agent so the rapier test measures the swing, and not an enemy walking
                        // off a stale path or its own attack inflating the shared accepted counter.
                        target.enabled = false;

                        float placedDistance = PlaceCombatCenterInRange(target, _rig.AimOrigin, _rig.transform.position, spot, 2.0f);
                        _rig.transform.rotation = Quaternion.LookRotation(target.CombatCenter - _rig.AimOrigin);
                        Physics.SyncTransforms();
                        yield return WaitFrames(2);

                        int rapierSequence = -1;
                        System.Action<AttackIntent> rapierStarted = intent => rapierSequence = intent.Sequence;
                        int rapierRejectedBefore = _localAuthority != null ? _localAuthority.RejectedCount : 0;

                        WeaponData rapier = melee.Weapon;
                        float rapierReach = rapier != null ? rapier.reach : 0f;
                        float rapierRadius = rapier != null ? rapier.hitSphereRadius : 0f;
                        float rapierArc = rapier != null ? rapier.arcAngle : 0f;
                        Vector3 rapierToCenter = target.CombatCenter - _rig.AimOrigin;
                        Debug.Log($"[Emberdelve][Smoke:{Mode}] rapier setup diag: placedDist={placedDistance:F2} origin={_rig.AimOrigin} enemyCenter={target.CombatCenter} dist={rapierToCenter.magnitude:F2} angle={Vector3.Angle(_rig.AimDirection, rapierToCenter):F1} reach={rapierReach:F2}+{rapierRadius:F2} arc={rapierArc:F0}");

                        melee.AttackStarted += rapierStarted;

                        melee.BeginSwing();
                        yield return WaitSeconds(1.0f);

                        melee.AttackStarted -= rapierStarted;
                        target.enabled = true;

                        bool rapierDamaged = target.CurrentHealth < healthBefore;
                        bool rapierAcceptedHit = _localAuthority != null && _localAuthority.AcceptedCount > acceptedBefore;

                        Check(rapierDamaged,
                            $"rapier thrust damaged the enemy ({healthBefore:F0} -> {target.CurrentHealth:F0})");
                        Check(rapierAcceptedHit, "authority accepted the rapier hit");

                        if (!rapierDamaged || !rapierAcceptedHit)
                        {
                            SequenceDiag($"rapierThrust rejected {rapierRejectedBefore} -> {(_localAuthority != null ? _localAuthority.RejectedCount : 0)}",
                                _rig.CombatId, rapierSequence);
                        }
                    }
                    else
                    {
                        Check(false, "no walkable cell next to the archer for the rapier thrust");
                    }
                }

                Check(loadout.SwitchWeapon(), "archer can switch back to the bow");
                Check(!loadout.UsingSecondary && ranged.enabled && !melee.enabled, "bow and ranged component restored");
                Check(ranged.Magazine == bowMagazine && ranged.Reserve == bowReserve,
                    $"bow ammo persisted across the swap ({bowMagazine}/{bowReserve} -> {ranged.Magazine}/{ranged.Reserve})");
                Check(!_rig.CanParry && !_rig.Defense.CanParry, $"parry capability leaves when the rapier is unequipped ({_rig.Defense})");

                Debug.Log($"[Emberdelve][Smoke:{Mode}] archer loadout diag: primary={Name(loadout.Primary)} secondary={Name(loadout.Secondary)} active={Name(loadout.ActiveWeapon)}");
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
            // ---------------------------------------------------------------- P4b: real input path

            // These flows exist because the first windowed review (P4a) reported "combat looks unwinnable".
            // Every earlier combat fixture called BeginSwing()/Fire() directly and parked the enemy at the exact
            // distance the scan measures, so a defect anywhere between the button press and the authority could
            // hide behind them. Here the only thing the harness does is press the primary button (the
            // EmberdelveInput override seam) and place an enemy where a player would find one - at ordinary
            // distances, in front, slightly off-axis, standing and after the player really moved. Everything
            // else (Update state machine, probe origin, hit point, authority validation, damage) is the shipped
            // path, and a rejection is attributed to the authority's own reason string.

            private struct RealCase
            {
                public string Label;
                public float Distance;
                public float YawOffset;
                public bool ExpectHit;
            }

            private IEnumerator RealCombatWarriorFlow()
            {
                if (!PrepareRealCombat(out EmberdelveInput input)) yield break;

                ClassLoadout loadout = _rig.Loadout;
                MeleeCombat melee = _rig.GetComponent<MeleeCombat>();
                RangedCombat ranged = _rig.GetComponent<RangedCombat>();
                Check(loadout != null && melee != null && ranged != null, "warrior rig has loadout, melee and ranged components");
                if (loadout == null || melee == null || ranged == null) yield break;

                Check(_rig.ActiveWeapon != null && _rig.ActiveWeapon == melee.Weapon && melee.enabled,
                    $"the authority-visible weapon is the weapon the melee component swings ({Name(_rig.ActiveWeapon)})");

                yield return RealMeleeMatrix("warrior sword standing", input, melee, false);

                yield return MovePlayerForReal(1.6f);
                yield return RealMeleeMatrix("warrior sword after moving", input, melee, true);

                Check(loadout.SwitchWeapon() && ranged.enabled && !melee.enabled, "warrior switched to the crossbow");
                yield return RealRangedMatrix("warrior crossbow", input, ranged);
                loadout.SwitchToSlot(0);
                CleanupRealCombat(input);
            }

            private IEnumerator RealCombatArcherFlow()
            {
                if (!PrepareRealCombat(out EmberdelveInput input)) yield break;

                ClassLoadout loadout = _rig.Loadout;
                MeleeCombat melee = _rig.GetComponent<MeleeCombat>();
                RangedCombat ranged = _rig.GetComponent<RangedCombat>();
                Check(loadout != null && melee != null && ranged != null, "archer rig has loadout, melee and ranged components");
                if (loadout == null || melee == null || ranged == null) yield break;

                Check(_rig.ActiveWeapon != null && _rig.ActiveWeapon == ranged.Weapon && ranged.enabled,
                    $"the authority-visible weapon is the weapon the ranged component fires ({Name(_rig.ActiveWeapon)})");

                yield return RealRangedMatrix("archer bow", input, ranged);

                Check(loadout.SwitchWeapon() && melee.enabled && !ranged.enabled, "archer switched to the rapier");
                yield return RealMeleeMatrix("archer rapier standing", input, melee, false);
                yield return MovePlayerForReal(1.6f);
                yield return RealMeleeMatrix("archer rapier after moving", input, melee, true);

                loadout.SwitchToSlot(0);
                CleanupRealCombat(input);
            }

            private bool PrepareRealCombat(out EmberdelveInput input)
            {
                input = null;
                if (_rig == null || _director == null)
                {
                    Check(false, "real combat flow found a rig and a director");
                    return false;
                }

                _authority = null;
                if (GameServices.TryGet<ICombatAuthority>(out ICombatAuthority resolved)) _authority = resolved;
                _localAuthority = resolved as LocalCombatAuthority;
                Check(_localAuthority != null, "the offline authority is the one under test");
                input = _rig.Input;
                Check(input != null, "rig exposes the input layer");
                if (_localAuthority == null || input == null) return false;

                // The player is not under test for damage taken: freeze every enemy in place (they stay
                // registered, collidable and hostile - only their own AI Update stops), so each case measures
                // the player's attack and nothing else.
                EnemyAgent[] agents = FindObjectsByType<EnemyAgent>(FindObjectsSortMode.None);
                for (int i = 0; i < agents.Length; i++) agents[i].enabled = false;
                Check(agents.Length > 0, $"enemies present for the real input cases ({agents.Length})");
                return agents.Length > 0;
            }

            private void CleanupRealCombat(EmberdelveInput input)
            {
                input.PrimaryPressedOverride = null;
                input.PrimaryReleasedOverride = null;
                input.PrimaryHeldOverride = null;
                EnemyAgent[] agents = FindObjectsByType<EnemyAgent>(FindObjectsSortMode.None);
                for (int i = 0; i < agents.Length; i++) agents[i].enabled = true;
            }

            /// <summary>Moves the player with the CharacterController (as movement does), in an open direction.</summary>
            private IEnumerator MovePlayerForReal(float metres)
            {
                CharacterController controller = _rig.GetComponent<CharacterController>();
                if (controller == null) { Check(false, "player has a CharacterController to move"); yield break; }

                Vector3 start = _rig.transform.position;
                float yaw = FindOpenYaw(metres + 0.6f, 0f, metres + 1f, out bool found, true);
                Check(found, "an open direction exists to move the player");
                if (!found) yield break;

                Vector3 direction = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
                _rig.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
                const int steps = 12;
                for (int i = 0; i < steps; i++)
                {
                    controller.Move(direction * (metres / steps));
                    yield return null;
                }

                float moved = Vector3.Distance(start, _rig.transform.position);
                Check(moved > metres * 0.6f, $"the player really moved ({moved:F2} m of {metres:F1})");
            }

            private Vector3 RealCasePoint(float yaw, float distance, float yawOffset)
            {
                Vector3 dir = Quaternion.Euler(0f, yaw + yawOffset, 0f) * Vector3.forward;
                return _rig.transform.position + dir * distance;
            }

            /// <summary>
            /// Picks a player yaw whose whole fan of case points is open floor with a clear line from the
            /// player's body, so a case can only fail for a combat reason and never because it stands in a wall.
            /// </summary>
            private float FindOpenYaw(float maxDistance, float maxOffset, float clearance, out bool found, bool straightOnly = false)
            {
                LayerMask levelMask = _director != null && _director.Config != null ? _director.Config.levelMask : Physics.DefaultRaycastLayers;
                float[] offsets = straightOnly ? new[] { 0f } : new[] { -maxOffset, 0f, maxOffset };

                for (int step = 0; step < 16; step++)
                {
                    float yaw = _rig.transform.eulerAngles.y + step * 22.5f;
                    bool ok = true;

                    for (int o = 0; o < offsets.Length && ok; o++)
                    {
                        for (float d = 0.8f; d <= maxDistance + 0.01f && ok; d += 0.6f)
                        {
                            Vector3 p = RealCasePoint(yaw, d, offsets[o]);
                            Vector2Int cell = _layout.CellFromWorld(p);
                            if (!_grid.IsWalkable(cell.x, cell.y)) ok = false;
                        }

                        Vector3 far = RealCasePoint(yaw, Mathf.Max(maxDistance, clearance), offsets[o]) + Vector3.up * 1.1f;
                        if (ok && !HitScanner.HasLineOfSight(_rig.CombatCenter, far, levelMask, 0.2f)) ok = false;
                    }

                    if (ok) { found = true; return yaw; }
                }

                found = false;
                return _rig.transform.eulerAngles.y;
            }

            private IEnumerator RealMeleeMatrix(string label, EmberdelveInput input, MeleeCombat melee, bool subset)
            {
                WeaponData weapon = melee.Weapon;
                Check(weapon != null && melee.enabled, $"{label}: melee component live with {Name(weapon)}");
                if (weapon == null || !melee.enabled) yield break;

                float halfArc = weapon.arcAngle * 0.5f;
                float maxReach = weapon.reach + weapon.hitSphereRadius;
                float far = Mathf.Max(0.9f, maxReach - 0.4f);   // inside the probe once the eye/centre height gap is counted
                float[] distances = subset ? new[] { 1.4f, far } : new[] { 0.9f, 1.8f, far };
                float[] offsets = subset ? new[] { 0f, halfArc * 0.5f } : new[] { 0f, halfArc * 0.35f, halfArc * 0.7f };

                float yaw = FindOpenYaw(far, halfArc * 0.7f, far + 1.5f, out bool found);
                Check(found, $"{label}: an open fan of floor exists around the player");
                if (!found) yield break;

                _rig.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
                Physics.SyncTransforms();

                EnemyAgent enemy = PickAliveEnemy();
                Check(enemy != null, $"{label}: an enemy is available");
                if (enemy == null) yield break;

                List<RealCase> cases = new List<RealCase>();
                for (int d = 0; d < distances.Length; d++)
                {
                    for (int o = 0; o < offsets.Length; o++)
                    {
                        cases.Add(new RealCase { Label = $"d={distances[d]:F1}m off={offsets[o]:F0}deg", Distance = distances[d], YawOffset = offsets[o], ExpectHit = true });
                        if (offsets[o] > 0.5f)
                            cases.Add(new RealCase { Label = $"d={distances[d]:F1}m off=-{offsets[o]:F0}deg", Distance = distances[d], YawOffset = -offsets[o], ExpectHit = true });
                    }
                }

                if (!subset)
                {
                    // Controls: the harness must be able to tell "no hit" from "hit", or green means nothing.
                    cases.Add(new RealCase { Label = "behind the player (180deg)", Distance = 1.5f, YawOffset = 180f, ExpectHit = false });
                    cases.Add(new RealCase { Label = $"beyond reach ({maxReach + 1.2f:F1}m)", Distance = maxReach + 1.2f, YawOffset = 0f, ExpectHit = false });
                }

                int exercised = 0;
                for (int i = 0; i < cases.Count; i++)
                {
                    RealCase c = cases[i];
                    Vector3 spot = RealCasePoint(yaw, c.Distance, c.YawOffset);

                    // Controls may land in a wall; skip them rather than test a wall.
                    if (!c.ExpectHit)
                    {
                        Vector2Int cell = _layout.CellFromWorld(spot);
                        if (!_grid.IsWalkable(cell.x, cell.y))
                        {
                            Debug.Log($"[Emberdelve][Smoke:{Mode}] {label}: control '{c.Label}' skipped (not open floor)");
                            continue;
                        }
                    }

                    enemy.Health.Heal(10000f);
                    Vector3 root = enemy.transform.position;
                    enemy.transform.position = new Vector3(spot.x, root.y, spot.z);
                    Physics.SyncTransforms();

                    // Ready: the swing machine idle and past its cooldown, and the authority's own cooldown over.
                    yield return WaitUntil(() => !melee.IsSwinging && melee.CooldownRemaining <= 0f
                        && _localAuthority.WeaponCooldownRemaining(_rig.CombatId, weapon) <= 0f, weapon.cooldown + 2f);
                    yield return WaitFrames(2);

                    float healthBefore = enemy.CurrentHealth;
                    int acceptedBefore = _localAuthority.AcceptedCount;
                    int rejectedBefore = _localAuthority.RejectedCount;
                    Vector3 eye = _rig.AimOrigin;
                    float eyeDist = (enemy.CombatCenter - eye).magnitude;
                    float angle = Vector3.Angle(_rig.AimDirection, enemy.CombatCenter - eye);

                    // The scan's arc is a 3D angle from the eye to the target's chest, so at point blank the
                    // drop from eye to chest (about 30 degrees at 0.9 m) eats part of a narrow arc. That is the
                    // documented HitScanner rule, not what this matrix tests: a case whose own measured angle is
                    // outside the arc is reported and skipped instead of asserted as a hit (P4b.md, finding F4).
                    if (c.ExpectHit && angle > halfArc - 2f)
                    {
                        Debug.Log($"[Emberdelve][Smoke:{Mode}] {label}: {c.Label} skipped, its measured {angle:F0}deg is outside the {weapon.arcAngle:F0}deg arc");
                        continue;
                    }

                    // THE REAL PATH: one press of the primary button, held for exactly one frame.
                    input.PrimaryPressedOverride = true;
                    yield return null;
                    input.PrimaryPressedOverride = null;

                    yield return WaitUntil(() => !melee.IsSwinging, 2f);
                    yield return WaitFrames(2);

                    bool damaged = enemy.CurrentHealth < healthBefore;
                    bool accepted = _localAuthority.AcceptedCount > acceptedBefore;
                    string diag = $"eye-to-centre {eyeDist:F2}m, {angle:F0}deg off aim, reach {maxReach:F2}m, authority: " +
                                  $"{(accepted ? "accepted" : $"rejected x{_localAuthority.RejectedCount - rejectedBefore} '{_localAuthority.LastAttackRejectReason}'")}";

                    if (c.ExpectHit)
                    {
                        Check(damaged && accepted, $"{label}: {c.Label} -> real swing damaged the enemy ({healthBefore:F0} -> {enemy.CurrentHealth:F0}; {diag})");
                    }
                    else
                    {
                        Check(!damaged && !accepted, $"{label}: {c.Label} -> no hit, as it must be ({diag})");
                    }
                    exercised++;
                }

                Check(exercised >= (subset ? 4 : 10), $"{label}: enough cases were exercised ({exercised})");
            }

            private IEnumerator RealRangedMatrix(string label, EmberdelveInput input, RangedCombat ranged)
            {
                WeaponData weapon = ranged.Weapon;
                Check(weapon != null && weapon.IsRanged && ranged.enabled, $"{label}: ranged component live with {Name(weapon)}");
                if (weapon == null || !ranged.enabled) yield break;

                // Distances a player actually shoots at; lateral offsets stay inside the body.
                float[] distances = { 3f, 6f, 10f };
                float yaw = FindOpenYaw(10f, 0f, 11f, out bool found, true);
                if (!found) yaw = FindOpenYaw(6f, 0f, 7f, out found, true);
                Check(found, $"{label}: an open firing lane exists");
                if (!found) yield break;

                _rig.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
                Physics.SyncTransforms();

                EnemyAgent enemy = PickAliveEnemy();
                Check(enemy != null, $"{label}: an enemy is available");
                if (enemy == null) yield break;

                Vector3 right = Quaternion.Euler(0f, yaw, 0f) * Vector3.right;
                int cases = 0;
                for (int i = 0; i < distances.Length; i++)
                {
                    for (int side = 0; side < 2; side++)
                    {
                        float lateral = side == 0 ? 0f : 0.15f;
                        Vector3 spot = RealCasePoint(yaw, distances[i], 0f) + right * lateral;
                        Vector2Int cell = _layout.CellFromWorld(spot);
                        if (!_grid.IsWalkable(cell.x, cell.y)) continue;

                        // Refill so the matrix measures aim and authority, not the size of the magazine.
                        if (ranged.Magazine < 2) ranged.RefundAmmo(weapon.magazineSize);

                        enemy.Health.Heal(10000f);
                        Vector3 root = enemy.transform.position;
                        enemy.transform.position = new Vector3(spot.x, root.y, spot.z);
                        Physics.SyncTransforms();

                        yield return WaitUntil(() => _localAuthority.WeaponCooldownRemaining(_rig.CombatId, weapon) <= 0f, weapon.cooldown + 2f);
                        yield return WaitFrames(2);

                        int magazineBefore = ranged.Magazine;
                        float healthBefore = enemy.CurrentHealth;
                        int acceptedBefore = _localAuthority.AcceptedCount;
                        int rejectedBefore = _localAuthority.RejectedCount;

                        // THE REAL PATH: press, hold for the draw, release.
                        input.PrimaryPressedOverride = true;
                        yield return null;
                        input.PrimaryPressedOverride = null;
                        yield return WaitSeconds(Mathf.Max(0.1f, weapon.drawTime) + 0.12f);
                        input.PrimaryReleasedOverride = true;
                        yield return null;
                        input.PrimaryReleasedOverride = null;
                        yield return WaitFrames(3);

                        bool damaged = enemy.CurrentHealth < healthBefore;
                        bool accepted = _localAuthority.AcceptedCount > acceptedBefore;
                        string diag = $"authority: {(accepted ? "accepted" : $"rejected x{_localAuthority.RejectedCount - rejectedBefore} '{_localAuthority.LastAttackRejectReason}'")}, magazine {magazineBefore} -> {ranged.Magazine}";
                        Check(damaged && accepted && ranged.Magazine == magazineBefore - 1,
                            $"{label}: {distances[i]:F0}m lateral {lateral:F2}m -> real shot damaged the enemy ({healthBefore:F0} -> {enemy.CurrentHealth:F0}; {diag})");
                        cases++;
                    }
                }

                Check(cases >= 4, $"{label}: at least four open firing cases were exercised ({cases})");
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

                if (_rig.Mana != null && _rig.Skills != null)
                {
                    // U2A8 fixture migration. Old assumption: the fresh run's mana ceiling equals the raw
                    // class field (EffectiveMaxMana = the authored 75). Accepted rule: max_mana =
                    // max(10, authored 75 + 5 * INT modifier), and this run descends through the dungeon
                    // scene's authored fallback class (warrior, INT 8 -> floor((8-10)/2) = -1) -> 70
                    // (Docs/Design/ATTRIBUTES.md). The authored base is asserted separately so the base and
                    // the derived ceiling are both visible.
                    Check(_rig.Class != null && _rig.Class.maxMana == 75 && _rig.Class.intelligence == 8,
                        $"fresh run descends with the authored warrior mana base 75 and INT 8 ({(_rig.Class != null ? _rig.Class.classId : "none")})");
                    Check(_rig.Mana.MaxMana == 70,
                        $"fresh run reconfigured the mana pool to the derived 75 + 5*-1 (got {_rig.Mana.MaxMana}, current {_rig.Mana.CurrentMana:F0})");
                    Check(_rig.Mana.CurrentMana >= _rig.Mana.MaxMana - 0.5f, "fresh run starts with full mana");
                    Check(_rig.Skills.KitValid && _rig.Skills.KitProblems.Count == 0, "fresh run resolved a valid skill kit");
                    Check(_rig.Skills.IsReady(0) && _rig.Skills.IsReady(1),
                        $"fresh run starts with both skills off cooldown ({_rig.Skills.CooldownRemaining(0):F1}/{_rig.Skills.CooldownRemaining(1):F1})");
                    Check(_rig.Status == null || _rig.Status.ActiveCount == 0, "fresh run starts with no statuses");
                }

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

            // ---------------------------------------------------------------- items: grid, equipment, containers

            /// <summary>
            /// Milestone 5 assertions. The bag is tested as a grid (footprints, overlap refusal, stacking,
            /// move, swap, removal, overflow), equipment is tested as numbers that change while an item is worn
            /// and return when it is not, and containers are tested as seeded loot tables that hand their loot
            /// to the run host and keep whatever the player cannot carry.
            /// </summary>
            private IEnumerator ItemFlow()
            {
                _rig = FindFirstObjectByType<PlayerRig>();
                _director = FindFirstObjectByType<RunDirector>();

                Check(_rig != null && _director != null, "item flow found a rig and a director");
                if (_rig == null || _director == null) yield break;

                if (!GameServices.TryGet<ICombatAuthority>(out ICombatAuthority resolved)) resolved = null;
                _authority = resolved;
                _localAuthority = resolved as LocalCombatAuthority;
                Check(_authority != null, "combat authority resolved for the armor measurement");

                Inventory inventory = _rig.Inventory;
                Equipment equipment = _rig.Equipment;
                Check(inventory != null, "player rig carries an inventory");
                Check(equipment != null, "player rig carries equipment slots");
                if (inventory == null || equipment == null) yield break;

                GameConfig config = _director.Config;
                Check(config != null, "run director resolved a game config");
                if (config == null) yield break;

                ItemData coin = AssetDatabase.LoadAssetAtPath<ItemData>(EmberdelvePaths.Data + "/Item_EmberCoin.asset");
                ItemData draught = AssetDatabase.LoadAssetAtPath<ItemData>(EmberdelvePaths.Data + "/Item_BloodDraught.asset");
                ItemData relic = AssetDatabase.LoadAssetAtPath<ItemData>(EmberdelvePaths.Data + "/Item_AshenRelic.asset");
                ItemData cuirass = AssetDatabase.LoadAssetAtPath<ItemData>(EmberdelvePaths.Data + "/Item_BoiledCuirass.asset");
                ItemData boots = AssetDatabase.LoadAssetAtPath<ItemData>(EmberdelvePaths.Data + "/Item_FootpadBoots.asset");
                ItemData blade = AssetDatabase.LoadAssetAtPath<ItemData>(EmberdelvePaths.Data + "/Item_EmberFangBlade.asset");
                ItemData shieldItem = AssetDatabase.LoadAssetAtPath<ItemData>(EmberdelvePaths.Data + "/Item_EmberbulwarkShieldItem.asset");

                Check(coin != null && draught != null && relic != null, "consumable and currency items loadable");
                Check(cuirass != null && boots != null && blade != null && shieldItem != null,
                    "equipment items loadable");
                if (cuirass == null || boots == null || blade == null || shieldItem == null || coin == null) yield break;

                Debug.Log("[Emberdelve][Smoke:items] ---- grid storage");
                yield return GridStorageFlow(inventory, coin, cuirass, boots, shieldItem, config);

                Debug.Log("[Emberdelve][Smoke:items] ---- equipment slots");
                yield return EquipmentFlow(inventory, equipment, cuirass, boots, blade, shieldItem);

                Debug.Log("[Emberdelve][Smoke:items] ---- equipment transfers");
                yield return EquipmentTransferFlow(inventory, equipment, cuirass, blade, coin);

                Debug.Log("[Emberdelve][Smoke:items] ---- containers");
                yield return ChestFlow(inventory, equipment, coin, cuirass, draught, relic);
            }

            private IEnumerator GridStorageFlow(Inventory inventory, ItemData coin, ItemData cuirass,
                ItemData boots, ItemData shieldItem, GameConfig config)
            {
                Check(inventory.GridWidth == config.inventoryGridWidth && inventory.GridHeight == config.inventoryGridHeight,
                    $"bag is the grid the config declares ({inventory.GridWidth}x{inventory.GridHeight})");
                Check(inventory.Capacity == inventory.GridWidth * inventory.GridHeight,
                    $"capacity is cells, not slots ({inventory.Capacity})");

                // A deterministic starting point: everything the flow measures below is measured against an
                // empty bag, so the numbers are exact rather than relative to whatever the class handed out.
                inventory.Clear();
                yield return WaitFrames(2);

                Check(inventory.Slots.Count == 0 && inventory.UsedCells == 0,
                    "bag cleared for the grid tests");
                Check(inventory.FreeCells == inventory.Capacity, $"every cell free ({inventory.FreeCells})");

                // ---- footprint
                Check(cuirass.EffectiveFootprint == new Vector2Int(2, 3),
                    $"cuirass declares a 2x3 footprint ({cuirass.EffectiveFootprint})");
                Check(boots.EffectiveFootprint == new Vector2Int(2, 2),
                    $"boots declare a 2x2 footprint ({boots.EffectiveFootprint})");
                Check(coin.EffectiveFootprint == new Vector2Int(1, 1),
                    $"coins take one cell ({coin.EffectiveFootprint})");

                Check(inventory.TryAdd(cuirass, 1, out string cuirassReason),
                    $"2x3 cuirass placed ({cuirassReason})");
                Check(inventory.Slots.Count == 1, "one stack in the bag");

                Vector2Int cuirassOrigin = inventory.Slots[0].Origin;
                Check(inventory.UsedCells == 6, $"the cuirass occupies six cells ({inventory.UsedCells})");

                bool allCellsMarked = true;
                for (int x = 0; x < 2; x++)
                {
                    for (int y = 0; y < 3; y++)
                    {
                        if (!inventory.IsCellOccupied(cuirassOrigin + new Vector2Int(x, y))) allCellsMarked = false;
                    }
                }
                Check(allCellsMarked, "every cell under the footprint is marked occupied");
                Check(inventory.SlotAtCell(cuirassOrigin + new Vector2Int(1, 2)).Item == cuirass,
                    "the grid reports the item when asked what is at a covered cell");

                // ---- overlap refusal, and the refusal changes nothing
                Check(!inventory.TryAddAt(boots, 1, cuirassOrigin + new Vector2Int(1, 1), out string overlapReason),
                    "boots cannot be placed on top of the cuirass");
                Check(!string.IsNullOrEmpty(overlapReason), $"the refusal explains itself ({overlapReason})");
                Check(inventory.UsedCells == 6 && inventory.Slots.Count == 1,
                    $"a refused placement changed nothing ({inventory.UsedCells} cells, {inventory.Slots.Count} stacks)");

                // ---- stacking
                Check(inventory.TryAdd(coin, 20, out string coinReason), $"20 coins added ({coinReason})");

                int coinStacks = 0;
                int coinUnits = 0;
                for (int i = 0; i < inventory.Slots.Count; i++)
                {
                    if (inventory.Slots[i].Item != coin) continue;
                    coinStacks++;
                    coinUnits += inventory.Slots[i].Count;
                    Check(inventory.Slots[i].Count <= coin.StackLimitForFootprint,
                        $"a coin stack respects its limit ({inventory.Slots[i].Count}/{coin.StackLimitForFootprint})");
                }

                Check(coinUnits == 20, $"all twenty coins are carried ({coinUnits})");
                Check(coinStacks == 3, $"coins fill stack limits rather than one cell each ({coinStacks} stacks)");
                Check(inventory.UsedCells == 9, $"three coin cells plus the cuirass ({inventory.UsedCells})");
                Check(inventory.TotalUnits == 21, $"unit count counts stacks ({inventory.TotalUnits})");

                // Equippable items never stack, so two more cuirasses are two more footprints.
                Check(cuirass.CanStack == false, "an equippable item cannot stack");
                int cuirassStacksBefore = 0;
                for (int i = 0; i < inventory.Slots.Count; i++) if (inventory.Slots[i].Item == cuirass) cuirassStacksBefore++;

                Check(inventory.TryAdd(cuirass, 2, out string twoCuirass), $"two cuirasses added as two stacks ({twoCuirass})");
                int cuirassStacks = 0;
                for (int i = 0; i < inventory.Slots.Count; i++) if (inventory.Slots[i].Item == cuirass) cuirassStacks++;
                Check(cuirassStacks == cuirassStacksBefore + 2,
                    $"equippable items are separate stacks ({cuirassStacksBefore} -> {cuirassStacks})");
                Check(inventory.UsedCells == 21, $"each cuirass holds its own six cells ({inventory.UsedCells})");

                // ---- move
                int firstCuirass = inventory.IndexOfItem(cuirass);
                Vector2Int oldOrigin = inventory.Slots[firstCuirass].Origin;
                Vector2Int newOrigin = inventory.FindFreeOrigin(new Vector2Int(2, 3));
                Check(newOrigin.x >= 0, "a free 2x3 hole exists to move into");

                if (newOrigin.x >= 0)
                {
                    Check(inventory.TryMove(firstCuirass, newOrigin, out string moveReason),
                        $"cuirass moved to cell {newOrigin} ({moveReason})");
                    Check(inventory.Slots[firstCuirass].Origin == newOrigin, "the stack records its new origin");

                    bool oldFreed = true;
                    bool newFilled = true;
                    for (int x = 0; x < 2; x++)
                    {
                        for (int y = 0; y < 3; y++)
                        {
                            if (inventory.IsCellOccupied(oldOrigin + new Vector2Int(x, y))) oldFreed = false;
                            if (!inventory.IsCellOccupied(newOrigin + new Vector2Int(x, y))) newFilled = false;
                        }
                    }
                    Check(oldFreed, "the cells it left are free again");
                    Check(newFilled, "the cells it entered are occupied");
                    Check(inventory.UsedCells == 21, $"moving costs the same cells ({inventory.UsedCells})");
                }

                // An illegal move has to roll back completely, not leave the item half-placed. The target is
                // the cell the cuirass is standing on right now, so the refusal is about occupancy.
                int bootsIndex = inventory.IndexOfItem(boots);
                Check(inventory.TryAdd(boots, 1, out string bootsReason), $"boots added ({bootsReason})");
                bootsIndex = inventory.IndexOfItem(boots);
                Vector2Int bootsOrigin = inventory.Slots[bootsIndex].Origin;

                int cuirassNow = inventory.IndexOfItem(cuirass);
                Vector2Int occupiedTarget = inventory.Slots[cuirassNow].Origin;

                Check(!inventory.TryMove(bootsIndex, occupiedTarget, out string badMoveReason),
                    "boots cannot be moved onto the cell the cuirass occupies");
                Check(!string.IsNullOrEmpty(badMoveReason), $"the refused move explains itself ({badMoveReason})");
                Check(inventory.Slots[bootsIndex].Origin == bootsOrigin, "the refused move left the item where it was");
                Check(inventory.IsCellOccupied(bootsOrigin) && inventory.IsCellOccupied(bootsOrigin + new Vector2Int(1, 1)),
                    "its original cells are still marked, so the grid did not half-apply the move");

                // ---- swap
                Check(inventory.TryAdd(shieldItem, 1, out string shieldReason), $"shield item added ({shieldReason})");
                int shieldIndex = inventory.IndexOfItem(shieldItem);
                Vector2Int shieldOrigin = inventory.Slots[shieldIndex].Origin;

                Check(inventory.TrySwap(bootsIndex, shieldIndex, out string swapReason),
                    $"two 2x2 items swap places ({swapReason})");
                Check(inventory.Slots[bootsIndex].Origin == shieldOrigin && inventory.Slots[shieldIndex].Origin == bootsOrigin,
                    $"the origins exchanged (boots {inventory.Slots[bootsIndex].Origin}, shield {inventory.Slots[shieldIndex].Origin})");
                Check(inventory.SlotAtCell(bootsOrigin).Item == shieldItem && inventory.SlotAtCell(shieldOrigin).Item == boots,
                    "the grid occupancy follows the swap");

                // A 2x3 cannot trade places with a 1x1 unless the surrounding cells happen to be free.
                int coinIndex = inventory.IndexOfItem(coin);
                Vector2Int coinOrigin = inventory.Slots[coinIndex].Origin;
                Vector2Int cuirassSwapOrigin = inventory.Slots[inventory.IndexOfItem(cuirass)].Origin;

                Check(!inventory.TrySwap(inventory.IndexOfItem(cuirass), coinIndex, out string badSwapReason),
                    "a 2x3 cuirass cannot swap into a lone coin cell");
                Check(!string.IsNullOrEmpty(badSwapReason), $"the refused swap explains itself ({badSwapReason})");
                Check(inventory.Slots[inventory.IndexOfItem(cuirass)].Origin == cuirassSwapOrigin &&
                      inventory.Slots[inventory.IndexOfItem(coin)].Origin == coinOrigin,
                    "a refused swap left both items where they were");

                // ---- removal frees exactly the cells the item held
                int usedBeforeRemove = inventory.UsedCells;
                int removedCoins = inventory.TryRemoveAt(coinIndex, 1);
                Check(removedCoins == 1, $"one coin removed ({removedCoins})");
                Check(inventory.Slots[inventory.IndexOfItem(coin)].Count == coin.StackLimitForFootprint - 1,
                    "the remaining stack shrank by one");
                Check(inventory.UsedCells == usedBeforeRemove, "a partial removal still needs the same cell");

                int coinCellsFreed = 0;
                while (inventory.IndexOfItem(coin) >= 0)
                {
                    int index = inventory.IndexOfItem(coin);
                    int count = inventory.Slots[index].Count;
                    coinCellsFreed += inventory.Slots[index].Size.x * inventory.Slots[index].Size.y;
                    inventory.TryRemoveAt(index, count);
                }
                Check(coinCellsFreed == 3, $"three coin cells were released ({coinCellsFreed})");
                Check(inventory.UsedCells == usedBeforeRemove - 3, $"the grid shrank with the stacks ({inventory.UsedCells})");
                Check(inventory.IndexOfItem(coin) < 0, "the item is gone from the bag");

                // ---- overflow: measured from an empty bag of identical shapes, so the expected count comes
                // from geometry and not from an area division. Equal-area capacity is not rectangular
                // packing: a 10x4 bag holds floor(10/2) x floor(4/3) = 5 upright 2x3 items (30 cells), while
                // 40/6 would claim 6. The expectation is computed from the grid and footprint axes.
                inventory.Clear();
                yield return WaitFrames(2);

                int capacity = inventory.Capacity;
                int gridWidth = inventory.GridWidth;
                int gridHeight = inventory.GridHeight;
                Vector2Int cuirassShape = cuirass.EffectiveFootprint;
                int cellsPerCuirass = cuirassShape.x * cuirassShape.y;
                int fitAcross = gridWidth / cuirassShape.x;
                int fitDown = gridHeight / cuirassShape.y;
                int expectedFit = fitAcross * fitDown;
                int expectedUsed = expectedFit * cellsPerCuirass;
                int expectedFree = capacity - expectedUsed;

                Check(cuirassShape.x > 0 && cuirassShape.y > 0 && expectedFit > 0 && expectedUsed <= capacity,
                    $"the packing expectation is well defined ({gridWidth}x{gridHeight} bag, {cuirassShape} item, {fitAcross} across x {fitDown} down = {expectedFit} items)");

                // Bounded insertion: a bag that keeps saying yes cannot spin the coroutine until the harness
                // watchdog fires, and the extra attempts are what turn that into a visible failure.
                int placed = 0;
                int placementAttempts = 0;
                int attemptCap = expectedFit + 2;
                while (placementAttempts < attemptCap && inventory.TryAdd(cuirass, 1))
                {
                    placed++;
                    placementAttempts++;
                }

                Check(placed == expectedFit,
                    $"the bag took exactly the rectangles its cells allow ({placed} placed, expected {expectedFit} = {fitAcross} across x {fitDown} down in {gridWidth}x{gridHeight})");
                Check(placementAttempts < attemptCap,
                    $"insertion attempts stayed bounded ({placementAttempts} attempts, cap {attemptCap})");
                Check(inventory.UsedCells == expectedUsed,
                    $"every item paid for its own cells ({inventory.UsedCells} used, expected {expectedUsed})");
                Check(inventory.FreeCells == expectedFree,
                    $"the leftover cells are the geometric remainder ({inventory.FreeCells} free, expected {expectedFree})");
                Check(inventory.UsedSlots == expectedFit,
                    $"each equippable item is its own stack ({inventory.UsedSlots} stacks for {expectedFit} items)");

                Check(!inventory.TryAdd(cuirass, 1, out string overflowReason), "one more cuirass does not fit");
                Check(overflowReason != null && overflowReason.Contains("no room"),
                    $"the overflow refusal says why ({overflowReason})");
                Check(inventory.UsedCells == expectedUsed && inventory.UsedSlots == expectedFit,
                    $"the refused cuirass changed nothing ({inventory.UsedCells} cells, {inventory.UsedSlots} stacks)");

                // Free cells and a free hole are different things, which is the cost of a real grid.
                bool noHoleForShape = inventory.FindFreeOrigin(cuirassShape).x < 0;
                Check(inventory.FreeCells > 0 && noHoleForShape,
                    $"leftover cells are too scattered to hold a {cuirassShape} item ({inventory.FreeCells} free, no hole)");
                if (expectedFree >= cellsPerCuirass)
                {
                    // The remainder here is a band one cell tall: enough area for another item, no block
                    // wide and tall enough to put it in.
                    Check(inventory.FreeCells >= cellsPerCuirass && noHoleForShape,
                        $"free cells outnumber one footprint yet contain no hole ({inventory.FreeCells} free, needs {cellsPerCuirass} in one {cuirassShape} block)");
                }

                // A partial add keeps what fits and reports the rest instead of losing it silently. The
                // request is derived from the grid, not from a magic number: the bag holds no coins yet, so
                // the room that exists is every free cell times the coin's own stack limit.
                Check(inventory.IndexOfItem(coin) < 0, "the fragmented bag holds no coins before the partial add");

                int coinStackLimit = coin.StackLimitForFootprint;
                int freeCellsBeforeCoins = inventory.FreeCells;
                int coinStacksBefore = 0;
                int coinUnitsBefore = 0;
                int coinStackSpaceBefore = 0;
                for (int i = 0; i < inventory.Slots.Count; i++)
                {
                    if (inventory.Slots[i].Item != coin) continue;
                    coinStacksBefore++;
                    coinUnitsBefore += inventory.Slots[i].Count;
                    coinStackSpaceBefore += Mathf.Max(0, coinStackLimit - inventory.Slots[i].Count);
                }

                int expectedCoinCapacity = coinStackSpaceBefore + freeCellsBeforeCoins * coinStackLimit;
                int coinRequest = expectedCoinCapacity + 1;

                // Snapshot the cuirasses so the partial add can be shown not to disturb them.
                int fullBagCuirassStacks = 0;
                List<Vector2Int> fullBagCuirassOrigins = new List<Vector2Int>();
                List<int> fullBagCuirassCounts = new List<int>();
                for (int i = 0; i < inventory.Slots.Count; i++)
                {
                    if (inventory.Slots[i].Item != cuirass) continue;
                    fullBagCuirassOrigins.Add(inventory.Slots[i].Origin);
                    fullBagCuirassCounts.Add(inventory.Slots[i].Count);
                    fullBagCuirassStacks++;
                }

                int unitsBefore = inventory.TotalUnits;
                int usedCellsBefore = inventory.UsedCells;
                int stacksBefore = inventory.UsedSlots;

                Check(coinStackLimit > 0 && expectedCoinCapacity > 0 && coinRequest > expectedCoinCapacity,
                    $"the request is strictly larger than the room that exists ({coinRequest} coins requested, room for {expectedCoinCapacity} = {freeCellsBeforeCoins} free cells x {coinStackLimit})");

                bool tookEveryCoin = inventory.TryAdd(coin, coinRequest, out string partialReason);
                int gained = inventory.TotalUnits - unitsBefore;
                int leftover = coinRequest - gained;

                Check(!tookEveryCoin, $"the bag cannot take every coin requested ({coinRequest} requested, {gained} accepted)");
                Check(gained == expectedCoinCapacity,
                    $"the bag took exactly the capacity it had ({gained} accepted, room for {expectedCoinCapacity})");
                Check(gained > 0 && gained < coinRequest,
                    $"the add is partial, neither a refusal nor a success ({gained} of {coinRequest})");
                Check(leftover == coinRequest - expectedCoinCapacity && leftover > 0,
                    $"the coins that did not fit are accounted for ({leftover} left out of {coinRequest})");
                Check(partialReason != null && partialReason.Contains("left") && partialReason.Contains(leftover.ToString()),
                    $"the partial add reports what it could not take ({partialReason})");

                int coinStacksAfter = 0;
                int coinUnitsAfter = 0;
                bool coinStacksWithinLimit = true;
                for (int i = 0; i < inventory.Slots.Count; i++)
                {
                    if (inventory.Slots[i].Item != coin) continue;
                    coinStacksAfter++;
                    coinUnitsAfter += inventory.Slots[i].Count;
                    if (inventory.Slots[i].Count > coinStackLimit) coinStacksWithinLimit = false;
                }

                Check(coinUnitsAfter == coinUnitsBefore + expectedCoinCapacity,
                    $"every accepted coin is carried in a stack ({coinUnitsAfter} coins, was {coinUnitsBefore})");
                Check(coinStacksAfter == coinStacksBefore + freeCellsBeforeCoins && coinStacksWithinLimit,
                    $"each scattered free cell became one coin stack at its limit ({coinStacksAfter} coin stacks, limit {coinStackLimit})");
                Check(inventory.UsedCells == usedCellsBefore + freeCellsBeforeCoins && inventory.FreeCells == 0,
                    $"the new stacks filled exactly the leftover cells ({inventory.UsedCells} used, {inventory.FreeCells} free)");
                Check(inventory.TotalUnits == unitsBefore + gained && inventory.UsedSlots == stacksBefore + coinStacksAfter - coinStacksBefore,
                    $"totals follow the accepted units ({inventory.TotalUnits} units, {inventory.UsedSlots} stacks)");

                int fullBagCuirassStacksNow = 0;
                bool cuirassSnapshotIntact = true;
                for (int i = 0; i < inventory.Slots.Count; i++)
                {
                    if (inventory.Slots[i].Item != cuirass) continue;
                    if (fullBagCuirassStacksNow >= fullBagCuirassStacks)
                    {
                        cuirassSnapshotIntact = false;
                        break;
                    }
                    if (inventory.Slots[i].Origin != fullBagCuirassOrigins[fullBagCuirassStacksNow] ||
                        inventory.Slots[i].Count != fullBagCuirassCounts[fullBagCuirassStacksNow] ||
                        inventory.Slots[i].Size != cuirassShape) cuirassSnapshotIntact = false;
                    fullBagCuirassStacksNow++;
                }
                cuirassSnapshotIntact = cuirassSnapshotIntact && fullBagCuirassStacksNow == fullBagCuirassStacks;

                Check(cuirassSnapshotIntact,
                    $"the partial add left the cuirass stacks where they were ({fullBagCuirassStacks} before, {fullBagCuirassStacksNow} after)");

                bool cuirassCellsStillMarked = true;
                for (int i = 0; i < fullBagCuirassStacks; i++)
                {
                    for (int x = 0; x < cuirassShape.x; x++)
                    {
                        for (int y = 0; y < cuirassShape.y; y++)
                        {
                            if (!inventory.IsCellOccupied(fullBagCuirassOrigins[i] + new Vector2Int(x, y))) cuirassCellsStillMarked = false;
                        }
                    }
                }
                Check(cuirassCellsStillMarked,
                    $"every cell the cuirasses hold is still marked after the coin add ({fullBagCuirassStacks * cellsPerCuirass} cuirass cells)");

                inventory.Clear();
                yield return WaitFrames(2);
                Check(inventory.UsedCells == 0 && inventory.FreeCells == inventory.Capacity,
                    "bag cleared again after the grid tests");
            }

            private IEnumerator EquipmentFlow(Inventory inventory, Equipment equipment, ItemData cuirass,
                ItemData boots, ItemData blade, ItemData shieldItem)
            {
                CharacterClassData classData = _rig.Class;
                Check(classData != null && classData.classId == "archer", $"item flow runs as the class chosen in the lobby ({(classData != null ? classData.classId : "none")})");
                if (classData == null) yield break;

                RangedCombat ranged = _rig.GetComponent<RangedCombat>();
                MeleeCombat melee = _rig.GetComponent<MeleeCombat>();

                float healthBase = _rig.MaxHealth;
                float speedBase = _rig.Movement != null ? _rig.Movement.ResolveGroundSpeed(false, false) : 0f;
                WeaponData bowBase = _rig.MainHandWeapon;

                // U2A8 fixture migration. Old assumption: the unworn archer's ceiling is the class asset's
                // maxHealth field. That field is the pristine base (80); the ceiling the run start filled is
                // the derived one, CON 12 -> floor((12-10)/2) = 1 -> 80 + 10*1 = 90 (Docs/Design/
                // ATTRIBUTES.md). The raw field is asserted too, so a class asset mutated by equipment
                // would fail here instead of quietly matching a mutated baseline.
                Check(classData.maxHealth == 80, $"archer class asset still authors the base health 80 ({classData.maxHealth})");
                Check(Mathf.Approximately(healthBase, 90f),
                    $"baseline unworn health is the derived 80 + 10*1 (got {healthBase}, expected 90)");
                Check(_rig.Defense.HasShield == false, "baseline archer carries no shield");
                Check(_rig.Defense.CanBlock == false, "baseline archer cannot block");
                Check(_rig.Defense.CanParry == false, "baseline archer cannot parry");
                Check(bowBase != null && bowBase.IsRanged, $"baseline main hand is a ranged weapon ({bowBase.displayName})");
                Check(ranged != null && ranged.enabled, "ranged combat is live for the baseline weapon");
                Check(melee != null && !melee.enabled, "melee combat is off for the baseline weapon");
                Check(equipment.FilledCount == 0, $"nothing worn at the start ({equipment.FilledCount})");

                // ---- wearing armor changes the pools and the defense numbers
                Check(inventory.TryAdd(cuirass, 1, out string addCuirass), $"cuirass put in the bag ({addCuirass})");
                int cuirassIndex = inventory.IndexOfItem(cuirass);
                Check(cuirassIndex >= 0, "cuirass found in the bag");

                float healthBeforeEquip = _rig.CurrentHealth;
                Check(equipment.TryEquipFromInventory(cuirassIndex, out string equipReason),
                    $"cuirass equipped from the bag ({equipReason})");
                Check(equipment.Equipped(EquipmentSlot.Chest) == cuirass, "the chest slot holds the cuirass");
                Check(inventory.IndexOfItem(cuirass) < 0, "the worn cuirass is no longer in the bag");
                // U2A8 fixture migration. Old assumption: equipping the cuirass paid its own 30 hit points
                // into the CURRENT pool. Accepted rule (Docs/Design/ATTRIBUTES.md, U2A3): a gear change moves
                // the ceiling and clamps current pools downward only - it never heals or refills. Expected
                // ceiling = the derived unworn 90 plus the item's authored flat 30 = 120, because its CON +1
                // takes 12 -> 13, still modifier 1, so the attribute term does not move. Both reads bracket
                // the same synchronous equip, so no regeneration tick can sit between them and a capacity
                // payout would show up as a current-pool jump.
                Check(_rig.MaxHealth >= healthBase + cuirass.StatTotal(SkillStat.MaxHealth) - 0.5f,
                    $"max health rose by the item's own number ({healthBase} -> {_rig.MaxHealth})");
                Check(Mathf.Abs(_rig.MaxHealth - 120f) <= 0.011f,
                    $"worn ceiling is the derived 90 plus the cuirass' own flat 30 (got {_rig.MaxHealth}, expected 120)");
                Check(_rig.CurrentHealth <= healthBeforeEquip + 0.001f,
                    $"equipping the cuirass healed nothing ({healthBeforeEquip:F0} -> {_rig.CurrentHealth:F0}): a bigger ceiling clamps down only, it never pays out hit points");
                Check(_rig.Defense.BlockReduction >= cuirass.StatTotal(SkillStat.BlockReduction) - 0.001f,
                    $"block reduction includes the armor ({_rig.Defense.BlockReduction:F2})");
                Check(_rig.Stats != null && _rig.Stats.HasSourceWithModifiers(CharacterStatStack.EquipmentSourceKey),
                    "the stat stack records an equipment source");

                // ---- movement speed from worn boots
                float speedWithBootsBefore = speedBase;
                Check(inventory.TryAdd(boots, 1, out string addBoots), $"boots put in the bag ({addBoots})");
                Check(equipment.TryEquipFromInventory(inventory.IndexOfItem(boots), out string bootsReason),
                    $"boots equipped ({bootsReason})");
                Check(equipment.Equipped(EquipmentSlot.Legs) == boots, "the legs slot holds the boots");

                yield return WaitFrames(2);
                float speedWearingBoots = _rig.Movement != null ? _rig.Movement.ResolveGroundSpeed(false, false) : 0f;
                float bootsMultiplier = boots.MultiplierFor(SkillStat.MoveSpeed);
                Check(speedWearingBoots > speedWithBootsBefore + 0.05f,
                    $"wearing the boots made the resolved speed faster ({speedWithBootsBefore:F2} -> {speedWearingBoots:F2})");
                Check(Mathf.Abs(speedWearingBoots - speedWithBootsBefore * bootsMultiplier) < 0.12f,
                    $"the speed matches the item's declared multiplier x{bootsMultiplier:F2}");

                // ---- a weapon item changes what is held, and what the kit can do
                Check(inventory.TryAdd(blade, 1, out string addBlade), $"blade put in the bag ({addBlade})");
                Check(equipment.TryEquipFromInventory(inventory.IndexOfItem(blade), out string bladeReason),
                    $"blade equipped ({bladeReason})");
                Check(equipment.Equipped(EquipmentSlot.Weapon) == blade, "the weapon slot holds the blade");
                // GrantsWeapon is a bool, so comparing against it would pass for any non-null weapon. The
                // granted reference itself is what has to be in the hand.
                WeaponData bladeWeapon = blade.grantedWeapon;
                Check(bladeWeapon != null && blade.GrantsWeapon,
                    $"the blade declares the weapon it grants ({(bladeWeapon != null ? bladeWeapon.displayName : "none")})");
                Check(_rig.MainHandWeapon != null && ReferenceEquals(_rig.MainHandWeapon, bladeWeapon),
                    $"the main hand holds exactly the weapon the item grants ({(_rig.MainHandWeapon != null ? _rig.MainHandWeapon.displayName : "none")} vs {(bladeWeapon != null ? bladeWeapon.displayName : "none")})");
                Check(_rig.Defense.CanParry, "the blade's Parry capability is live");
                Check(ranged != null && !ranged.enabled, "ranged combat switched off for a melee main hand");
                Check(melee != null && melee.enabled, "melee combat switched on for a melee main hand");

                // ---- the off hand: carried, then actually usable
                Check(inventory.TryAdd(shieldItem, 1, out string addShield), $"shield put in the bag ({addShield})");
                Check(equipment.TryEquipFromInventory(inventory.IndexOfItem(shieldItem), out string shieldReason),
                    $"shield equipped ({shieldReason})");
                Check(equipment.Equipped(EquipmentSlot.OffHand) == shieldItem, "the off hand slot holds the shield item");
                WeaponData shieldWeapon = shieldItem.grantedWeapon;
                Check(shieldWeapon != null && shieldItem.GrantsWeapon,
                    $"the shield item declares the shield it grants ({(shieldWeapon != null ? shieldWeapon.displayName : "none")})");
                Check(_rig.OffhandWeapon != null && ReferenceEquals(_rig.OffhandWeapon, shieldWeapon),
                    "the rig holds exactly the shield the item grants");
                Check(_rig.Defense.HasShield, "the defense profile knows a shield is carried");
                Check(_rig.Defense.CanBlock,
                    "with a one-handed main hand the shield contributes, and the armor supplies the reduction");

                // ---- measured defense, not just flags. The flow itself takes the cuirass off and puts it
                // back on through the bag, so the armor is the only thing that changes between the hits it
                // measures.
                yield return GuardWithGearFlow(inventory, equipment, cuirass);

                // A two-handed main hand must cancel the off hand again.
                Check(equipment.TryUnequip(EquipmentSlot.Weapon, out string unequipBladeReason),
                    $"blade unequipped ({unequipBladeReason})");
                Check(_rig.MainHandWeapon == bowBase, $"the class weapon went back into the main hand ({(_rig.MainHandWeapon != null ? _rig.MainHandWeapon.displayName : "none")})");
                Check(_rig.Defense.CanBlock == false,
                    "the shield contributes nothing while a two-handed weapon is held");
                Check(_rig.Defense.CanParry == false, "the parry capability left with the blade");
                Check(ranged != null && ranged.enabled, "ranged combat came back for the ranged main hand");
                Check(equipment.Equipped(EquipmentSlot.Weapon) == null, "the weapon slot is empty again");

                // ---- unequipping puts everything back
                Check(equipment.TryUnequip(EquipmentSlot.OffHand, out string unequipShieldReason),
                    $"shield unequipped ({unequipShieldReason})");
                Check(_rig.OffhandWeapon == null, "the archer's off hand is empty again");
                Check(_rig.Defense.HasShield == false, "no shield in the defense profile");

                Check(equipment.TryUnequip(EquipmentSlot.Legs, out string unequipBootsReason),
                    $"boots unequipped ({unequipBootsReason})");
                yield return WaitFrames(2);
                float speedAfterBoots = _rig.Movement != null ? _rig.Movement.ResolveGroundSpeed(false, false) : 0f;
                Check(Mathf.Abs(speedAfterBoots - speedBase) < 0.12f,
                    $"the speed bonus is gone when the boots come off ({speedWearingBoots:F2} -> {speedAfterBoots:F2})");

                Check(equipment.TryUnequip(EquipmentSlot.Chest, out string unequipCuirassReason),
                    $"cuirass unequipped ({unequipCuirassReason})");
                Check(Mathf.Approximately(_rig.MaxHealth, healthBase) || _rig.MaxHealth <= healthBase + 0.5f,
                    $"max health returned to the class number ({_rig.MaxHealth})");
                Check(_rig.Defense.BlockReduction <= 0.001f,
                    $"no block reduction left from gear ({_rig.Defense.BlockReduction:F2})");
                Check(equipment.FilledCount == 0, $"nothing worn ({equipment.FilledCount})");
                Check(inventory.IndexOfItem(cuirass) >= 0 && inventory.IndexOfItem(boots) >= 0 &&
                      inventory.IndexOfItem(blade) >= 0 && inventory.IndexOfItem(shieldItem) >= 0,
                    "every unequipped item went back into the bag");

                // ---- a full bag refuses an unequip instead of deleting the item. The item is worn first so
                // filling the bag afterwards leaves no hole big enough to put it back into.
                inventory.Clear();
                yield return WaitFrames(2);

                Check(inventory.TryAdd(cuirass, 1, out string lastCuirass), $"one cuirass in an empty bag ({lastCuirass})");
                Check(equipment.TryEquipFromInventory(inventory.IndexOfItem(cuirass), out string equipAnyway),
                    $"cuirass equipped again ({equipAnyway})");

                // Worn gear occupies no bag cells, so taking it off frees nothing in the grid. The fill is
                // bounded by the packing this grid's own geometry allows, so a bag that kept saying yes cannot
                // spin this coroutine into the watchdog, and the extra attempts become a visible failure.
                Vector2Int chestShape = cuirass.EffectiveFootprint;
                int cellsPerChest = chestShape.x * chestShape.y;
                int bagFitAcross = inventory.GridWidth / chestShape.x;
                int bagFitDown = inventory.GridHeight / chestShape.y;
                int expectedBagFit = bagFitAcross * bagFitDown;
                int fillCap = expectedBagFit + 2;

                int fill = 0;
                while (fill < fillCap && inventory.TryAdd(cuirass, 1)) fill++;

                Check(fill < fillCap, $"the fill loop terminated on its own ({fill} cuirasses, cap {fillCap})");
                Check(fill == expectedBagFit,
                    $"the bag holds exactly the {expectedBagFit} cuirasses its cells allow ({fill} = {bagFitAcross} across x {bagFitDown} down)");
                Check(fill > 0 && inventory.UsedCells == fill * cellsPerChest,
                    $"worn gear occupies no bag cells ({inventory.UsedCells} used by {fill} bag cuirasses at {cellsPerChest} cells each)");
                Check(inventory.FindFreeOrigin(chestShape).x < 0,
                    $"the full bag holds no {chestShape} hole for the worn cuirass ({inventory.FreeCells} free cells, none of them a whole hole)");

                int unitsBeforeStuck = inventory.TotalUnits;
                int valueBeforeStuck = inventory.CarriedValue;
                int stacksBeforeStuck = inventory.UsedSlots;
                int cellsBeforeStuck = inventory.UsedCells;
                int collectedBeforeStuck = inventory.LifetimeCollected;
                int recoveredBeforeStuck = inventory.RecoveredValue;

                Check(!equipment.TryUnequip(EquipmentSlot.Chest, out string stuckReason),
                    "an unequip into a full bag is refused");
                Check(stuckReason != null && stuckReason.Contains("hole"),
                    $"the refusal says what is missing ({stuckReason})");
                Check(equipment.Equipped(EquipmentSlot.Chest) == cuirass, "the item is still worn, not destroyed");
                Check(inventory.TotalUnits == unitsBeforeStuck && inventory.CarriedValue == valueBeforeStuck &&
                      inventory.UsedSlots == stacksBeforeStuck && inventory.UsedCells == cellsBeforeStuck,
                      $"the refused unequip conserved the bag exactly ({inventory.UsedSlots} stacks, {inventory.UsedCells} cells)");
                Check(inventory.LifetimeCollected == collectedBeforeStuck && inventory.RecoveredValue == recoveredBeforeStuck,
                      $"the refused unequip credited nothing ({inventory.LifetimeCollected} collected, {inventory.RecoveredValue} recovered)");

                // ---- making the success honest: the destination has to come from the bag, not from the item
                // being taken off. One controlled bag cuirass is removed with the bag's own API to open a real
                // 2x3 hole, the bag is re-snapshot after that removal, and only then is the transfer required
                // to land in the hole that genuinely exists.
                int holeMakerIndex = inventory.IndexOfItem(cuirass);
                Check(holeMakerIndex >= 0, "the bag holds a cuirass to clear a hole with");
                Vector2Int holeOrigin = holeMakerIndex >= 0
                    ? inventory.Slots[holeMakerIndex].Origin
                    : new Vector2Int(-1, -1);
                int holeUnits = holeMakerIndex >= 0 ? inventory.TryRemoveAt(holeMakerIndex, 1) : 0;
                Check(holeUnits == 1, $"one bag cuirass was removed to open a hole ({holeUnits} removed from {holeOrigin})");

                Vector2Int plannedHole = inventory.FindFreeOrigin(chestShape);
                Check(plannedHole.x >= 0, $"the bag now has a {chestShape} hole to unequip into ({plannedHole})");

                int unitsBeforeFree = inventory.TotalUnits;
                int valueBeforeFree = inventory.CarriedValue;
                int stacksBeforeFree = inventory.UsedSlots;
                int cellsBeforeFree = inventory.UsedCells;
                int collectedBeforeFree = inventory.LifetimeCollected;
                int recoveredBeforeFree = inventory.RecoveredValue;

                Check(equipment.TryUnequip(EquipmentSlot.Chest, out string unequipIntoHole),
                    $"the worn cuirass comes off into the hole the bag opened ({unequipIntoHole})");
                Check(equipment.Equipped(EquipmentSlot.Chest) == null, "the chest slot is empty again");
                Check(inventory.IndexOfItem(cuirass) >= 0, "the unequipped cuirass is back in the bag");
                Check(inventory.TotalUnits == unitsBeforeFree + 1 &&
                      inventory.CarriedValue == valueBeforeFree + cuirass.value,
                      $"exactly one unit and its value moved back ({inventory.TotalUnits} units, {inventory.CarriedValue} value)");
                Check(inventory.UsedSlots == stacksBeforeFree + 1 &&
                      inventory.UsedCells == cellsBeforeFree + cellsPerChest,
                      $"the unequipped item paid for its own {chestShape} footprint ({inventory.UsedSlots} stacks, {inventory.UsedCells} cells)");
                Check(inventory.LifetimeCollected == collectedBeforeFree && inventory.RecoveredValue == recoveredBeforeFree,
                      $"taking worn gear off farmed no collection credit ({inventory.LifetimeCollected} collected, {inventory.RecoveredValue} recovered)");

                int cuirassStacksAfterHole = 0;
                Vector2Int landedOrigin = new Vector2Int(-1, -1);
                bool landedAsWholeShape = false;
                for (int i = 0; i < inventory.Slots.Count; i++)
                {
                    InventorySlot slot = inventory.Slots[i];
                    if (slot.Item != cuirass) continue;
                    cuirassStacksAfterHole++;
                    if (slot.Count == 1 && slot.Size == chestShape && slot.Origin == plannedHole)
                    {
                        landedAsWholeShape = true;
                        landedOrigin = slot.Origin;
                    }
                }

                Check(cuirassStacksAfterHole == fill,
                    $"the bag holds one stack per cuirass with nothing duplicated ({cuirassStacksAfterHole} stacks for {fill} cuirasses)");
                Check(landedAsWholeShape,
                    $"the unequipped cuirass landed as one whole {chestShape} footprint in the hole the bag opened ({landedOrigin})");
                Check(landedAsWholeShape && inventory.IsCellOccupied(landedOrigin + new Vector2Int(chestShape.x - 1, chestShape.y - 1)),
                    "every cell of that footprint is marked occupied, so the hole was really filled and not invented");

                Check(!equipment.TryUnequip(EquipmentSlot.Chest, out string emptySlotReason),
                    "unequipping an empty slot is refused");
                Check(!string.IsNullOrEmpty(emptySlotReason) && emptySlotReason.Contains("nothing"),
                    $"the refusal says the slot was empty ({emptySlotReason})");

                // ---- a second equip/unequip cycle on the same definition: the hand goes out and comes back,
                // and the run is not credited for gear it already had.
                inventory.Clear();
                yield return WaitFrames(2);

                Check(inventory.TryAdd(blade, 1, out string addBladeAgain), $"the blade is put back in the bag ({addBladeAgain})");
                int unitsBeforeCycle = inventory.TotalUnits;
                int valueBeforeCycle = inventory.CarriedValue;
                int collectedBeforeCycle = inventory.LifetimeCollected;
                int recoveredBeforeCycle = inventory.RecoveredValue;

                Check(equipment.TryEquipFromInventory(inventory.IndexOfItem(blade), out string equipBladeAgain),
                    $"the blade is equipped a second time ({equipBladeAgain})");
                Check(inventory.IndexOfItem(blade) < 0, "the blade left the bag on the second equip too");
                Check(equipment.TryUnequip(EquipmentSlot.Weapon, out string unequipBladeAgain),
                    $"and was taken off again ({unequipBladeAgain})");
                Check(_rig.MainHandWeapon == bowBase, "the class weapon is back in the main hand after the cycle");
                Check(inventory.IndexOfItem(blade) >= 0, "the blade went back into the bag on the second unequip too");
                Check(inventory.TotalUnits == unitsBeforeCycle && inventory.CarriedValue == valueBeforeCycle,
                    $"the cycle conserved the bag ({inventory.TotalUnits} units, {inventory.CarriedValue} value)");
                Check(inventory.LifetimeCollected == collectedBeforeCycle && inventory.RecoveredValue == recoveredBeforeCycle,
                    $"the cycle farmed no extraction credit ({inventory.LifetimeCollected} collected, {inventory.RecoveredValue} recovered)");

                equipment.Clear();
                inventory.Clear();
                yield return WaitFrames(2);
                Check(equipment.FilledCount == 0 && inventory.UsedCells == 0, "gear and bag cleared for the container tests");
                Check(_rig.MaxHealth <= healthBase + 0.5f, $"pools back at the class numbers ({_rig.MaxHealth})");
            }

            /// <summary>
            /// U1B ownership and conservation checks for the equipment transfer: an unowned definition is
            /// refused, the selected bag stack is the one consumed, a replacement that only fits in the space
            /// the incoming item vacates is allowed, a geometrically impossible replacement changes nothing, and
            /// no equip/unequip cycle credits the run with loot it never collected.
            /// </summary>
            private IEnumerator EquipmentTransferFlow(Inventory inventory, Equipment equipment,
                ItemData cuirass, ItemData blade, ItemData coin)
            {
                equipment.Clear();
                inventory.Clear();
                yield return WaitFrames(2);

                Check(equipment.FilledCount == 0 && inventory.Slots.Count == 0 && inventory.UsedCells == 0,
                    "gear and bag are empty for the transfer tests");

                // A 1x1 chest plate cloned from the cuirass so the impossible-swap geometry is real without
                // editing a shipped asset. It is destroyed before this flow returns.
                ItemData probePlate = UnityEngine.Object.Instantiate(cuirass);
                probePlate.name = "U1B_ProbeChestPlate";
                probePlate.itemId = "u1b_probe_chest_plate";
                probePlate.displayName = "U1B Probe Plate";
                probePlate.description = "Temporary 1x1 chest plate used only by the transfer fixtures.";
                probePlate.gridFootprint = new Vector2Int(1, 1);
                probePlate.maxStack = 1;
                probePlate.equipSlot = EquipmentSlot.Chest;
                probePlate.statModifiers = new StatModifier[0];
                probePlate.grantedWeapon = null;
                probePlate.kind = ItemKind.Armor;
                probePlate.value = 10;

                Check(probePlate.EffectiveFootprint == Vector2Int.one && probePlate.IsEquippable && !probePlate.CanStack,
                    "the cloned plate is a 1x1 equippable item, used only for geometry");

                int collectedBefore = inventory.LifetimeCollected;
                int recoveredBefore = inventory.RecoveredValue;
                int collectedEvents = 0;
                int equipNotifications = 0;
                int unequipNotifications = 0;

                System.Action<ItemData, int> onCollected = (item, total) => collectedEvents++;
                System.Action<ItemData, bool> onEquipment = (item, equipped) =>
                {
                    if (equipped) equipNotifications++; else unequipNotifications++;
                };

                // A subscriber that reads the whole picture from inside the notification: the bag, the slots,
                // both hands and the numbers the equipment source produced. Everything is copied out of the
                // live collections because the notification is raised synchronously in the middle of a
                // transfer, and it is only counted while armed - the loot adds that set these fixtures up are
                // legitimate collection, not equipment commits.
                int transferNotifications = 0;
                bool probeArmed = false;
                CommitSnapshot seenAtNotification = null;

                System.Action onInventoryChanged = () =>
                {
                    if (!probeArmed) return;
                    transferNotifications++;
                    seenAtNotification = CaptureCommit(inventory, equipment, _rig);
                };

                GameEvents.ItemCollected += onCollected;
                GameEvents.EquipmentChanged += onEquipment;
                GameEvents.InventoryChanged += onInventoryChanged;

                try
                {
                    // Arming is scoped to one equipment call so nothing else in the flow can be mistaken for
                    // a transfer, and a refusal that notifies nothing shows up as a zero instead of a stale
                    // count left over from the previous scenario.
                    System.Action ArmProbe = () =>
                    {
                        transferNotifications = 0;
                        seenAtNotification = null;
                        probeArmed = true;
                    };

                    System.Action DisarmProbe = () =>
                    {
                        probeArmed = false;
                    };

                    // ---- 1. refusals change nothing: null, a bad index, something that cannot be worn
                    int unitsStart = inventory.TotalUnits;
                    int valueStart = inventory.CarriedValue;
                    int cellsStart = inventory.UsedCells;
                    int stacksStart = inventory.UsedSlots;
                    float maxHealthStart = _rig.MaxHealth;
                    float reductionStart = _rig.Defense.BlockReduction;
                    WeaponData mainHandStart = _rig.MainHandWeapon;
                    WeaponData offhandStart = _rig.OffhandWeapon;

                    ArmProbe();

                    Check(!equipment.TryEquipItem(null, out string nullReason) && nullReason == "no item",
                        $"a null item is refused ({nullReason})");

                    // Each out-of-range call is evaluated on its own. Two out-params behind one && leave the
                    // second unassigned whenever the first refusal short-circuits, and the message then reads a
                    // local the compiler has no proof was ever set (CS0165). Neither check is dropped.
                    bool lowRefused = !equipment.TryEquipFromInventory(-1, out string lowIndexReason);
                    bool highRefused = !equipment.TryEquipFromInventory(inventory.Slots.Count, out string highIndexReason);

                    Check(lowRefused, $"a negative bag index is refused ({lowIndexReason})");
                    Check(!string.IsNullOrEmpty(lowIndexReason), $"the negative index refusal explains itself ({lowIndexReason})");
                    Check(highRefused, $"a bag index past the last stack is refused ({highIndexReason})");
                    Check(!string.IsNullOrEmpty(highIndexReason), $"the out-of-range index refusal explains itself ({highIndexReason})");

                    Check(!equipment.TryEquipItem(coin, out string coinReason) && coinReason.Contains("cannot be worn"),
                        $"a carried item that is not equipment is refused ({coinReason})");
                    Check(!equipment.TryUnequip(EquipmentSlot.None, out string noSlotReason),
                        $"the None slot is not a slot ({noSlotReason})");

                    DisarmProbe();


                    Check(inventory.TotalUnits == unitsStart && inventory.CarriedValue == valueStart &&
                          inventory.UsedSlots == stacksStart && inventory.UsedCells == cellsStart,
                          $"the refusals left the bag exactly as it was ({inventory.UsedSlots} stacks, {inventory.UsedCells} cells)");
                    Check(equipment.FilledCount == 0 && _rig.MaxHealth == maxHealthStart &&
                          Mathf.Abs(_rig.Defense.BlockReduction - reductionStart) < 0.0001f,
                          "the refusals changed no slot, pool or defense number");
                    Check(ReferenceEquals(_rig.MainHandWeapon, mainHandStart) && ReferenceEquals(_rig.OffhandWeapon, offhandStart),
                        "the refusals did not touch the hands");
                    Check(collectedEvents == 0 && equipNotifications == 0 && unequipNotifications == 0,
                        "a refusal notifies nothing");
                    Check(transferNotifications == 0 && seenAtNotification == null,
                        $"a refusal raised no inventory notification for anyone to look at ({transferNotifications} notifications)");

                    // ---- 2. an equippable definition the bag does not hold is not an item
                    Check(inventory.IndexOfItem(cuirass) < 0 && !equipment.IsEquipped(cuirass),
                        "the cuirass is neither in the bag nor worn");

                    ArmProbe();
                    bool unownedRefused = !equipment.TryEquipItem(cuirass, out string unownedReason);
                    DisarmProbe();

                    Check(unownedRefused,
                        "equipping a definition the bag does not hold is refused");
                    Check(!string.IsNullOrEmpty(unownedReason) && unownedReason.Contains("not in the bag"),
                        $"the refusal says the item is not carried ({unownedReason})");
                    Check(equipment.Equipped(EquipmentSlot.Chest) == null && equipment.FilledCount == 0,
                        "no item appeared in a slot");
                    Check(inventory.TotalUnits == unitsStart && inventory.CarriedValue == valueStart &&
                          inventory.UsedSlots == stacksStart && inventory.UsedCells == cellsStart,
                          $"the unowned refusal created nothing ({inventory.UsedSlots} stacks, {inventory.UsedCells} cells)");
                    Check(inventory.LifetimeCollected == collectedBefore && inventory.RecoveredValue == recoveredBefore,
                        $"the unowned refusal credited nothing ({inventory.LifetimeCollected} collected, {inventory.RecoveredValue} recovered)");
                    Check(transferNotifications == 0 && seenAtNotification == null,
                        $"the unowned refusal notified nothing ({transferNotifications} notifications)");

                    // ---- 3. the selected stack is consumed, not the first stack with the same definition
                    Check(inventory.TryAdd(cuirass, 1, out string addFirst), $"first cuirass added ({addFirst})");
                    Check(inventory.TryAdd(cuirass, 1, out string addSecond), $"second cuirass added ({addSecond})");

                    // Those two adds are real loot, so the baseline the transfer checks are measured against
                    // has to move with them. Everything below is equipment movement, not collection.
                    collectedBefore = inventory.LifetimeCollected;
                    recoveredBefore = inventory.RecoveredValue;
                    collectedEvents = 0;

                    Check(inventory.Slots.Count == 2 && inventory.UsedCells == 12,
                        $"two copies of one definition occupy two footprints ({inventory.Slots.Count} stacks, {inventory.UsedCells} cells)");

                    Vector2Int firstOrigin = inventory.Slots[0].Origin;
                    Vector2Int secondOrigin = inventory.Slots[1].Origin;
                    Check(firstOrigin != secondOrigin, $"the two copies sit at different cells ({firstOrigin} and {secondOrigin})");

                    int unitsBeforeSelected = inventory.TotalUnits;
                    int valueBeforeSelected = inventory.CarriedValue;
                    float wornHealthExpected = maxHealthStart + cuirass.StatTotal(SkillStat.MaxHealth);
                    float wornReductionExpected = reductionStart + cuirass.StatTotal(SkillStat.BlockReduction);

                    ArmProbe();
                    bool equippedSelected = equipment.TryEquipFromInventory(1, out string equipSecond);
                    DisarmProbe();

                    Check(equippedSelected,
                        $"the copy at bag slot 1 equipped ({equipSecond})");
                    Check(transferNotifications == 1,
                        $"one committed equip raised exactly one inventory notification ({transferNotifications})");

                    // Read from inside that notification: the observer is allowed to see a bag with the
                    // equipped copy gone only when the slot, the filled count and the stat source already agree
                    // that it is being worn. A notification raised from the bag helper would fail all of these.
                    Check(seenAtNotification != null && seenAtNotification.Slot(EquipmentSlot.Chest) == cuirass &&
                          seenAtNotification.FilledCount == 1,
                          "from inside the notification the chest slot already held the cuirass");
                    Check(seenAtNotification != null && seenAtNotification.CountOf(cuirass) == 1 &&
                          seenAtNotification.StacksOf(cuirass) == 1 &&
                          seenAtNotification.OriginOf(cuirass) == firstOrigin &&
                          seenAtNotification.OriginOf(cuirass) != secondOrigin,
                          "from inside the notification the selected bag footprint had already left while the copy that was not selected remained as exactly one unit in one stack at its own cell: " + firstOrigin + " held, " + secondOrigin + " empty (" + (seenAtNotification != null ? seenAtNotification.CountOf(cuirass) : -1) + " units in " + (seenAtNotification != null ? seenAtNotification.StacksOf(cuirass) : -1) + " stack at " + (seenAtNotification != null ? seenAtNotification.OriginOf(cuirass) : new Vector2Int(-1, -1)) + ", two copies of one definition counted without UIDs until U2)");
                    Check(seenAtNotification != null && seenAtNotification.BagStacks == 1 &&
                          seenAtNotification.BagOrigins.Count == 1 &&
                          seenAtNotification.BagOrigins[0] == firstOrigin && seenAtNotification.BagCounts[0] == 1,
                          "from inside the notification the copy that was not selected was still one unit at its own cell");
                    Check(seenAtNotification != null && seenAtNotification.BagCells == 6 &&
                          seenAtNotification.BagUnits == unitsBeforeSelected - 1 &&
                          seenAtNotification.BagValue == valueBeforeSelected - cuirass.value,
                          $"from inside the notification the bag had committed exactly the unit that moved ({(seenAtNotification != null ? seenAtNotification.BagUnits : -1)} units, {(seenAtNotification != null ? seenAtNotification.BagCells : -1)} cells)");
                    Check(seenAtNotification != null && seenAtNotification.MaxHealth > maxHealthStart + 0.5f &&
                          Mathf.Abs(seenAtNotification.MaxHealth - wornHealthExpected) < 0.5f,
                          $"from inside the notification the stat source was already refreshed ({(seenAtNotification != null ? seenAtNotification.MaxHealth : 0f):F0} HP against {wornHealthExpected:F0} expected)");
                    Check(seenAtNotification != null && seenAtNotification.BlockReduction >= wornReductionExpected - 0.001f,
                          $"from inside the notification the defense number already carried the armor ({(seenAtNotification != null ? seenAtNotification.BlockReduction : 0f):F2} against {wornReductionExpected:F2})");
                    Check(equipment.Equipped(EquipmentSlot.Chest) == cuirass, "the chest slot holds the cuirass");
                    Check(inventory.Slots.Count == 1 && inventory.Slots[0].Origin == firstOrigin,
                        $"the copy that was not selected is still at its own cell ({inventory.Slots[0].Origin})");
                    Check(inventory.UsedCells == 6, $"only the selected footprint left the grid ({inventory.UsedCells} cells)");
                    Check(inventory.TotalUnits == unitsBeforeSelected - 1 &&
                          inventory.CarriedValue == valueBeforeSelected - cuirass.value,
                          $"carried totals follow the one unit that moved ({inventory.TotalUnits} units, {inventory.CarriedValue} value)");
                    Check(inventory.LifetimeCollected == collectedBefore && inventory.RecoveredValue == recoveredBefore,
                        $"equipping is not collecting ({inventory.LifetimeCollected} collected, {inventory.RecoveredValue} recovered)");
                    Check(collectedEvents == 0, "equipping raised no ItemCollected event");
                    Check(equipNotifications == 1 && unequipNotifications == 0,
                        $"one equip notification for one equip ({equipNotifications} up, {unequipNotifications} down)");

                    // ---- 4. a valid replacement that only fits in the space the incoming item vacates.
                    // Coins stack, so they are placed one cell at a time: the bag has to be full to the last
                    // cell, which leaves the hole the incoming cuirass will free as the only 2x3 hole it has.
                    int coinsPlaced = 0;
                    int coinAttempts = 0;
                    int cellBudget = inventory.Capacity;
                    for (int y = 0; y < inventory.GridHeight; y++)
                    {
                        for (int x = 0; x < inventory.GridWidth; x++)
                        {
                            Vector2Int cell = new Vector2Int(x, y);
                            if (inventory.IsCellOccupied(cell)) continue;
                            if (coinAttempts >= cellBudget) break;
                            coinAttempts++;
                            if (inventory.TryAddAt(coin, 1, cell, out string fillReason)) coinsPlaced++;
                        }
                    }

                    Check(coinAttempts <= cellBudget, $"the fill loop stayed bounded ({coinAttempts} attempts, budget {cellBudget})");
                    Check(coinsPlaced > 0 && inventory.FreeCells == 0,
                        $"the bag is full to the last cell ({inventory.UsedCells}/{inventory.Capacity} used, {coinsPlaced} single coins)");
                    Check(inventory.UsedSlots == 1 + coinsPlaced, $"each single coin is its own stack ({inventory.UsedSlots} stacks)");

                    collectedBefore = inventory.LifetimeCollected;
                    recoveredBefore = inventory.RecoveredValue;
                    collectedEvents = 0;

                    int unitsBeforeSwap = inventory.TotalUnits;
                    int valueBeforeSwap = inventory.CarriedValue;
                    int stacksBeforeSwap = inventory.UsedSlots;
                    int cellsBeforeSwap = inventory.UsedCells;
                    float maxHealthBeforeSwap = _rig.MaxHealth;
                    float reductionBeforeSwap = _rig.Defense.BlockReduction;
                    int bagCuirassIndex = inventory.IndexOfItem(cuirass);
                    Check(bagCuirassIndex >= 0, "the bag still holds one cuirass to replace the worn one with");
                    Vector2Int vacatedOrigin = inventory.Slots[bagCuirassIndex].Origin;

                    ArmProbe();
                    bool replaced = equipment.TryEquipFromInventory(bagCuirassIndex, out string swapReason);
                    DisarmProbe();

                    Check(replaced,
                        $"the worn cuirass is replaced using the space the incoming copy vacates ({swapReason})");
                    Check(transferNotifications == 1,
                        $"one committed replacement raised exactly one inventory notification ({transferNotifications})");
                    Check(seenAtNotification != null && seenAtNotification.Slot(EquipmentSlot.Chest) == cuirass &&
                          seenAtNotification.FilledCount == 1,
                          "from inside the notification the chest slot already held the incoming copy");
                    Check(seenAtNotification != null && seenAtNotification.CountOf(cuirass) == 1 &&
                          seenAtNotification.StacksOf(cuirass) == 1,
                          $"from inside the notification the bag held exactly one cuirass, not the incoming one missing and not both copies at once ({(seenAtNotification != null ? seenAtNotification.CountOf(cuirass) : -1)} units)");
                    Check(seenAtNotification != null && seenAtNotification.OriginOf(cuirass) == vacatedOrigin &&
                          seenAtNotification.HasWholeFootprint(cuirass, cuirass.EffectiveFootprint),
                          $"from inside the notification the replaced copy already occupied the {cuirass.EffectiveFootprint} hole the selected incoming copy vacated ({(seenAtNotification != null ? seenAtNotification.OriginOf(cuirass) : new Vector2Int(-1, -1))} against {vacatedOrigin})");
                    Check(seenAtNotification != null && seenAtNotification.BagStacks == stacksBeforeSwap &&
                          seenAtNotification.BagCells == cellsBeforeSwap && seenAtNotification.BagUnits == unitsBeforeSwap &&
                          seenAtNotification.BagValue == valueBeforeSwap && seenAtNotification.FreeCells == 0,
                          $"from inside the notification the full bag was conserved: same stacks, cells, units and value ({(seenAtNotification != null ? seenAtNotification.UsedText : "none")})");
                    Check(seenAtNotification != null && Mathf.Abs(seenAtNotification.MaxHealth - maxHealthBeforeSwap) < 0.01f &&
                          Mathf.Abs(seenAtNotification.BlockReduction - reductionBeforeSwap) < 0.0001f,
                          $"from inside the notification the pools and defense were the worn-gear numbers, not a half-applied pair ({(seenAtNotification != null ? seenAtNotification.MaxHealth : 0f):F0} HP, {(seenAtNotification != null ? seenAtNotification.BlockReduction : 0f):F2} block)");
                    Check(equipment.Equipped(EquipmentSlot.Chest) == cuirass, "the chest slot still holds the cuirass definition");
                    Check(inventory.UsedSlots == stacksBeforeSwap && inventory.UsedCells == cellsBeforeSwap &&
                          inventory.FreeCells == 0,
                          $"the bag is still full, with the same number of stacks ({inventory.UsedSlots} stacks, {inventory.UsedCells} cells)");
                    Check(inventory.TotalUnits == unitsBeforeSwap && inventory.CarriedValue == valueBeforeSwap,
                        $"a same-definition replacement conserves units and value ({inventory.TotalUnits} units, {inventory.CarriedValue} value)");
                    Check(inventory.IsCellOccupied(vacatedOrigin),
                        $"the hole the incoming copy vacated ({vacatedOrigin}) is where the replaced copy landed");

                    bool replacedCopyIsWhole = false;
                    for (int i = 0; i < inventory.Slots.Count; i++)
                    {
                        if (inventory.Slots[i].Item != cuirass) continue;
                        replacedCopyIsWhole = inventory.Slots[i].Count == 1 &&
                                              inventory.Slots[i].Size == cuirass.EffectiveFootprint;
                        break;
                    }
                    Check(replacedCopyIsWhole, "the replaced cuirass is back in the bag as one full unstacked footprint");
                    Check(inventory.LifetimeCollected == collectedBefore && inventory.RecoveredValue == recoveredBefore,
                        $"the replacement credited no loot ({inventory.LifetimeCollected} collected, {inventory.RecoveredValue} recovered)");
                    Check(collectedEvents == 0, "the replacement raised no ItemCollected event");
                    Check(equipNotifications == 2 && unequipNotifications == 0,
                        $"a replacement is one equip, not an unequip plus an equip ({equipNotifications} up, {unequipNotifications} down)");

                    // ---- 5. a replacement that cannot fit anywhere changes absolutely nothing
                    // The bag is full, so one single-coin cell is cleared to make room for the 1x1 plate.
                    int coinCellIndex = inventory.IndexOfItem(coin);
                    Check(coinCellIndex >= 0 && inventory.TryRemoveAt(coinCellIndex, 1) == 1,
                        "one single coin was taken out to make room for the probe plate");
                    Check(inventory.FreeCells == 1, $"the bag has exactly one free cell now ({inventory.FreeCells})");
                    Check(inventory.TryAdd(probePlate, 1, out string addProbe), $"the 1x1 probe plate added ({addProbe})");
                    Check(inventory.FreeCells == 0, $"and filled it to the last cell ({inventory.UsedCells} used)");

                    collectedBefore = inventory.LifetimeCollected;
                    recoveredBefore = inventory.RecoveredValue;
                    collectedEvents = 0;

                    int unitsBeforeBad = inventory.TotalUnits;
                    int valueBeforeBad = inventory.CarriedValue;
                    int stacksBeforeBad = inventory.UsedSlots;
                    int cellsBeforeBad = inventory.UsedCells;
                    int orderBeforeBad = inventory.Slots.Count;
                    Vector2Int probeOrigin = inventory.Slots[inventory.IndexOfItem(probePlate)].Origin;
                    List<Vector2Int> originsBefore = new List<Vector2Int>();
                    List<int> countsBefore = new List<int>();
                    for (int i = 0; i < inventory.Slots.Count; i++)
                    {
                        originsBefore.Add(inventory.Slots[i].Origin);
                        countsBefore.Add(inventory.Slots[i].Count);
                    }

                    ArmProbe();
                    bool impossibleRefused = !equipment.TryEquipFromInventory(inventory.IndexOfItem(probePlate), out string impossibleReason);
                    DisarmProbe();

                    Check(impossibleRefused,
                        "a 1x1 replacement cannot free a 2x3 hole for the cuirass it replaces, so it is refused");
                    Check(!string.IsNullOrEmpty(impossibleReason) && impossibleReason.Contains("hole"),
                        $"the refused replacement says where it ran out of room ({impossibleReason})");

                    bool gridIdentical = inventory.Slots.Count == orderBeforeBad && inventory.UsedSlots == stacksBeforeBad;
                    for (int i = 0; i < inventory.Slots.Count && gridIdentical; i++)
                    {
                        if (inventory.Slots[i].Origin != originsBefore[i]) gridIdentical = false;
                        if (inventory.Slots[i].Count != countsBefore[i]) gridIdentical = false;
                    }
                    Check(gridIdentical, "every stack kept its item, count, origin and order");

                    bool cellsIdentical = inventory.UsedCells == cellsBeforeBad;
                    for (int y = 0; y < inventory.GridHeight && cellsIdentical; y++)
                    {
                        for (int x = 0; x < inventory.GridWidth && cellsIdentical; x++)
                        {
                            Vector2Int cell = new Vector2Int(x, y);
                            bool occupiedNow = inventory.IsCellOccupied(cell);
                            bool occupiedBefore = false;
                            for (int s = 0; s < originsBefore.Count; s++)
                            {
                                Vector2Int size = inventory.Slots[s].Size;
                                if (cell.x >= originsBefore[s].x && cell.x < originsBefore[s].x + size.x &&
                                    cell.y >= originsBefore[s].y && cell.y < originsBefore[s].y + size.y)
                                {
                                    occupiedBefore = true;
                                    break;
                                }
                            }
                            if (occupiedNow != occupiedBefore) cellsIdentical = false;
                        }
                    }
                    Check(cellsIdentical, $"the occupancy map is unchanged ({inventory.UsedCells} cells used)");
                    Check(inventory.IsCellOccupied(probeOrigin) && inventory.IndexOfItem(probePlate) >= 0,
                        "the probe plate is still in the bag at the cell it was placed at");
                    Check(inventory.TotalUnits == unitsBeforeBad && inventory.CarriedValue == valueBeforeBad,
                        $"totals are untouched ({inventory.TotalUnits} units, {inventory.CarriedValue} value)");
                    Check(inventory.LifetimeCollected == collectedBefore && inventory.RecoveredValue == recoveredBefore,
                        $"collection counters are untouched ({inventory.LifetimeCollected} collected, {inventory.RecoveredValue} recovered)");
                    Check(equipment.Equipped(EquipmentSlot.Chest) == cuirass && equipment.FilledCount == 1,
                        "the chest slot still holds the cuirass it held before the refusal");
                    Check(collectedEvents == 0 && equipNotifications == 2 && unequipNotifications == 0,
                        $"the refused replacement raised no collection and no equipment notification ({collectedEvents} collected, {equipNotifications} up, {unequipNotifications} down)");
                    Check(transferNotifications == 0 && seenAtNotification == null,
                        $"the refused replacement raised no inventory notification either ({transferNotifications})");

                    // ---- 6. a failed unequip keeps the item, and a successful one is not loot
                    ArmProbe();
                    bool stuckUnequipRefused = !equipment.TryUnequip(EquipmentSlot.Chest, out string stuckChestReason);
                    DisarmProbe();

                    Check(stuckUnequipRefused,
                        "taking the cuirass off into a bag with no 2x3 hole is refused");
                    Check(!string.IsNullOrEmpty(stuckChestReason) && stuckChestReason.Contains("hole"),
                        $"the refusal names the missing hole ({stuckChestReason})");
                    Check(equipment.Equipped(EquipmentSlot.Chest) == cuirass, "the cuirass is still worn, not destroyed");
                    Check(inventory.UsedCells == cellsBeforeBad && inventory.UsedSlots == stacksBeforeBad,
                        $"the refused unequip added nothing to the bag ({inventory.UsedSlots} stacks, {inventory.UsedCells} cells)");
                    Check(inventory.LifetimeCollected == collectedBefore && inventory.RecoveredValue == recoveredBefore,
                        $"the refused unequip credited nothing ({inventory.LifetimeCollected} collected, {inventory.RecoveredValue} recovered)");
                    Check(unequipNotifications == 0, "the refused unequip raised no equipment notification");
                    Check(transferNotifications == 0 && seenAtNotification == null,
                        $"the refused unequip raised no inventory notification ({transferNotifications})");

                    // A successful unequip still needs a hole of the item's own shape. Clearing the bag is
                    // the honest way to open one here, and it does not touch the lifetime counters.
                    inventory.Clear();
                    yield return WaitFrames(2);
                    Check(inventory.UsedCells == 0 && inventory.FreeCells == inventory.Capacity,
                        "the bag was emptied, so the unequip has somewhere to land");

                    int unitsBeforeUnequip = inventory.TotalUnits;
                    int valueBeforeUnequip = inventory.CarriedValue;
                    int stacksBeforeUnequip = inventory.UsedSlots;
                    int cellsBeforeUnequip = inventory.UsedCells;

                    ArmProbe();
                    bool unequipCommitted = equipment.TryUnequip(EquipmentSlot.Chest, out string freeUnequip);
                    DisarmProbe();

                    Check(unequipCommitted,
                        $"with a 2x3 hole free the cuirass comes off ({freeUnequip})");
                    Check(transferNotifications == 1,
                        $"one committed unequip raised exactly one inventory notification ({transferNotifications})");
                    Check(seenAtNotification != null && seenAtNotification.Slot(EquipmentSlot.Chest) == null &&
                          seenAtNotification.FilledCount == 0,
                          "from inside the notification the chest slot was already empty, so the item was not simultaneously worn and carried");
                    Check(seenAtNotification != null && seenAtNotification.CountOf(cuirass) == 1 &&
                          seenAtNotification.StacksOf(cuirass) == 1 &&
                          seenAtNotification.HasWholeFootprint(cuirass, cuirass.EffectiveFootprint),
                          "from inside the notification the unequipped cuirass was one whole unstacked footprint in the bag");
                    Check(seenAtNotification != null && seenAtNotification.BagStacks == stacksBeforeUnequip + 1 &&
                          seenAtNotification.BagCells == cellsBeforeUnequip + cuirass.EffectiveFootprint.x * cuirass.EffectiveFootprint.y &&
                          seenAtNotification.BagUnits == unitsBeforeUnequip + 1 &&
                          seenAtNotification.BagValue == valueBeforeUnequip + cuirass.value,
                          $"from inside the notification the bag had already paid for the footprint and gained exactly one unit ({(seenAtNotification != null ? seenAtNotification.UsedText : "none")})");
                    Check(seenAtNotification != null && Mathf.Abs(seenAtNotification.MaxHealth - maxHealthStart) < 0.01f &&
                          Mathf.Abs(seenAtNotification.BlockReduction - reductionStart) < 0.0001f,
                          $"from inside the notification the worn contribution was already out of the pools and the defense profile ({(seenAtNotification != null ? seenAtNotification.MaxHealth : 0f):F0} HP, {(seenAtNotification != null ? seenAtNotification.BlockReduction : 0f):F2} block)");
                    Check(equipment.Equipped(EquipmentSlot.Chest) == null && equipment.FilledCount == 0,
                        "the chest slot is empty");
                    Check(inventory.IndexOfItem(cuirass) >= 0, "the cuirass is in the bag");
                    Check(inventory.TotalUnits == unitsBeforeUnequip + 1 &&
                          inventory.CarriedValue == valueBeforeUnequip + cuirass.value,
                          $"the bag totals gained exactly the one item that moved back ({inventory.TotalUnits} units, {inventory.CarriedValue} value)");
                    Check(inventory.LifetimeCollected == collectedBefore && inventory.RecoveredValue == recoveredBefore,
                        $"moving worn gear is not collecting ({inventory.LifetimeCollected} collected, {inventory.RecoveredValue} recovered)");
                    Check(collectedEvents == 0, "the unequip raised no ItemCollected event");
                    Check(unequipNotifications == 1 && equipNotifications == 2,
                        $"one unequip notification for one unequip ({equipNotifications} up, {unequipNotifications} down)");

                    // ---- 7. re-equipping the same definition does not stack its modifiers a second time
                    int cuirassIndex = inventory.IndexOfItem(cuirass);
                    Check(cuirassIndex >= 0, "the unequipped cuirass is available to wear again");

                    ArmProbe();
                    bool reEquipped = equipment.TryEquipFromInventory(cuirassIndex, out string reEquipCuirass);
                    DisarmProbe();

                    Check(reEquipped,
                        $"the same cuirass equipped again ({reEquipCuirass})");
                    Check(transferNotifications == 1 && seenAtNotification != null &&
                          seenAtNotification.Slot(EquipmentSlot.Chest) == cuirass && !seenAtNotification.Holds(cuirass),
                          $"from inside that notification the slot held the cuirass and the bag no longer did ({transferNotifications})");

                    float maxHealthWorn = _rig.MaxHealth;
                    float reductionWorn = _rig.Defense.BlockReduction;
                    Check(maxHealthWorn > maxHealthStart && reductionWorn > reductionStart,
                        $"wearing it raised the numbers once ({maxHealthStart:F0} -> {maxHealthWorn:F0} HP, {reductionStart:F2} -> {reductionWorn:F2} block)");

                    equipment.RefreshStats();
                    equipment.RefreshStats();
                    Check(Mathf.Abs(_rig.MaxHealth - maxHealthWorn) < 0.01f &&
                          Mathf.Abs(_rig.Defense.BlockReduction - reductionWorn) < 0.0001f,
                        $"refreshing the equipment source adds nothing on top ({_rig.MaxHealth:F0} HP, {_rig.Defense.BlockReduction:F2} block)");

                    ArmProbe();
                    bool finalUnequipCommitted = equipment.TryUnequip(EquipmentSlot.Chest, out string finalUnequip);
                    DisarmProbe();

                    Check(finalUnequipCommitted,
                        $"and it comes back off ({finalUnequip})");
                    Check(transferNotifications == 1 && seenAtNotification != null &&
                          seenAtNotification.Slot(EquipmentSlot.Chest) == null && seenAtNotification.Holds(cuirass) &&
                          Mathf.Abs(seenAtNotification.MaxHealth - maxHealthStart) < 0.01f,
                          $"from inside that notification the slot was empty, the bag held the cuirass and the pool was the class number again ({transferNotifications})");
                    Check(Mathf.Abs(_rig.MaxHealth - maxHealthStart) < 0.01f &&
                          Mathf.Abs(_rig.Defense.BlockReduction - reductionStart) < 0.0001f,
                        $"one contribution in, one contribution out ({_rig.MaxHealth:F0} HP, {_rig.Defense.BlockReduction:F2} block)");

                    // ---- 8. a weapon item leaves the hand the way it found it, every time
                    Check(inventory.TryAdd(blade, 1, out string addBladeTransfer), $"the blade added ({addBladeTransfer})");

                    collectedBefore = inventory.LifetimeCollected;
                    recoveredBefore = inventory.RecoveredValue;
                    collectedEvents = 0;

                    WeaponData classWeapon = _rig.MainHandWeapon;
                    Check(classWeapon != null && classWeapon.IsRanged, $"the class weapon in hand is ranged ({classWeapon.displayName})");

                    ArmProbe();
                    bool bladeEquipped = equipment.TryEquipFromInventory(inventory.IndexOfItem(blade), out string equipBladeTransfer);
                    DisarmProbe();

                    Check(bladeEquipped,
                        $"the blade equipped ({equipBladeTransfer})");
                    Check(transferNotifications == 1 && seenAtNotification != null &&
                          ReferenceEquals(seenAtNotification.MainHand, blade.grantedWeapon) &&
                          !seenAtNotification.Holds(blade) && seenAtNotification.Slot(EquipmentSlot.Weapon) == blade,
                          $"from inside the notification the hand already held the blade's own weapon and the bag had released the blade ({transferNotifications})");
                    Check(ReferenceEquals(_rig.MainHandWeapon, blade.grantedWeapon),
                        "the main hand holds exactly the blade's granted weapon");

                    ArmProbe();
                    bool bladeUnequipped = equipment.TryUnequip(EquipmentSlot.Weapon, out string unequipBladeTransfer);
                    DisarmProbe();

                    Check(bladeUnequipped,
                        $"the blade unequipped ({unequipBladeTransfer})");
                    Check(transferNotifications == 1 && seenAtNotification != null &&
                          ReferenceEquals(seenAtNotification.MainHand, classWeapon) &&
                          seenAtNotification.Holds(blade) && seenAtNotification.Slot(EquipmentSlot.Weapon) == null,
                          $"from inside the notification the class weapon was already back in the hand and the blade was already back in the bag ({transferNotifications})");
                    Check(ReferenceEquals(_rig.MainHandWeapon, classWeapon),
                        $"the class weapon is back in the main hand ({(_rig.MainHandWeapon != null ? _rig.MainHandWeapon.displayName : "none")})");
                    Check(inventory.IndexOfItem(blade) >= 0, "the blade is in the bag, not duplicated");

                    int bladeStacks = 0;
                    for (int i = 0; i < inventory.Slots.Count; i++)
                    {
                        if (inventory.Slots[i].Item == blade && inventory.Slots[i].Count == 1) bladeStacks++;
                    }
                    Check(bladeStacks == 1, $"one blade exists in the bag ({bladeStacks})");
                    Check(inventory.LifetimeCollected == collectedBefore && inventory.RecoveredValue == recoveredBefore,
                        $"no equip or unequip cycle farmed extraction credit ({inventory.LifetimeCollected} collected, {inventory.RecoveredValue} recovered)");

                    Debug.Log($"[Emberdelve][Smoke:items] transfer diag: stacks={inventory.UsedSlots} cells={inventory.UsedCells} units={inventory.TotalUnits} value={inventory.CarriedValue} collected={inventory.LifetimeCollected} recovered={inventory.RecoveredValue} equipEvents={equipNotifications} unequipEvents={unequipNotifications} collectedEvents={collectedEvents} transferNotifications={transferNotifications}");
                }
                finally
                {
                    GameEvents.ItemCollected -= onCollected;
                    GameEvents.EquipmentChanged -= onEquipment;
                    GameEvents.InventoryChanged -= onInventoryChanged;

                    equipment.Clear();
                    inventory.Clear();
                    UnityEngine.Object.Destroy(probePlate);
                }
            }

            /// <summary>
            /// What the whole gear-plus-bag picture looked like at the instant an observer was notified.
            /// Every collection here is a copy taken inside the callback, so a notification raised in the
            /// middle of a transfer is judged on the state the observer actually saw rather than on whatever
            /// the flow looks like a few statements later.
            /// </summary>
            private sealed class CommitSnapshot
            {
                public int BagUnits;
                public int BagValue;
                public int BagStacks;
                public int BagCells;
                public int FreeCells;
                public int FilledCount;
                public float MaxHealth;
                public float BlockReduction;
                public WeaponData MainHand;
                public WeaponData Offhand;

                public readonly List<ItemData> BagItems = new List<ItemData>();
                public readonly List<int> BagCounts = new List<int>();
                public readonly List<Vector2Int> BagOrigins = new List<Vector2Int>();
                public readonly List<Vector2Int> BagSizes = new List<Vector2Int>();
                public readonly List<ItemData> EquipSlots = new List<ItemData>();

                public int CountOf(ItemData item)
                {
                    int total = 0;
                    for (int i = 0; i < BagItems.Count; i++) if (BagItems[i] == item) total += BagCounts[i];
                    return total;
                }

                public int StacksOf(ItemData item)
                {
                    int stacks = 0;
                    for (int i = 0; i < BagItems.Count; i++) if (BagItems[i] == item) stacks++;
                    return stacks;
                }

                public bool Holds(ItemData item) => CountOf(item) > 0;

                public Vector2Int OriginOf(ItemData item)
                {
                    for (int i = 0; i < BagItems.Count; i++) if (BagItems[i] == item) return BagOrigins[i];
                    return new Vector2Int(-1, -1);
                }

                public bool HasWholeFootprint(ItemData item, Vector2Int size)
                {
                    for (int i = 0; i < BagItems.Count; i++)
                    {
                        if (BagItems[i] == item && BagCounts[i] == 1 && BagSizes[i] == size) return true;
                    }
                    return false;
                }

                public ItemData Slot(EquipmentSlot slot)
                {
                    int index = (int)slot;
                    return index >= 0 && index < EquipSlots.Count ? EquipSlots[index] : null;
                }

                /// <summary>One readable line for a failure message: what the observer saw, all of it.</summary>
                public string UsedText =>
                    $"{BagStacks} stacks, {BagCells} cells, {BagUnits} units, {BagValue} value, {FreeCells} free, worn={DescribeGear()}";

                public string DescribeGear()
                {
                    System.Text.StringBuilder builder = new System.Text.StringBuilder();
                    for (int i = 0; i < EquipSlots.Count; i++)
                    {
                        if (EquipSlots[i] == null) continue;
                        if (builder.Length > 0) builder.Append('/');
                        builder.Append(EquipSlots[i].displayName);
                    }
                    return builder.Length > 0 ? builder.ToString() : "nothing";
                }
            }

            /// <summary>Copies the live bag, slots, hands and resolved numbers into one snapshot.</summary>
            private static CommitSnapshot CaptureCommit(Inventory inventory, Equipment equipment, PlayerRig rig)
            {
                CommitSnapshot snapshot = new CommitSnapshot();
                if (inventory != null)
                {
                    snapshot.BagUnits = inventory.TotalUnits;
                    snapshot.BagValue = inventory.CarriedValue;
                    snapshot.BagStacks = inventory.UsedSlots;
                    snapshot.BagCells = inventory.UsedCells;
                    snapshot.FreeCells = inventory.FreeCells;

                    IReadOnlyList<InventorySlot> slots = inventory.Slots;
                    for (int i = 0; i < slots.Count; i++)
                    {
                        snapshot.BagItems.Add(slots[i].Item);
                        snapshot.BagCounts.Add(slots[i].Count);
                        snapshot.BagOrigins.Add(slots[i].Origin);
                        snapshot.BagSizes.Add(slots[i].Size);
                    }
                }

                if (equipment != null)
                {
                    IReadOnlyList<ItemData> equipSlots = equipment.Slots;
                    for (int i = 0; i < equipSlots.Count; i++) snapshot.EquipSlots.Add(equipSlots[i]);
                    snapshot.FilledCount = equipment.FilledCount;
                }

                if (rig != null)
                {
                    snapshot.MainHand = rig.MainHandWeapon;
                    snapshot.Offhand = rig.OffhandWeapon;
                    snapshot.MaxHealth = rig.MaxHealth;
                    snapshot.BlockReduction = rig.Defense.BlockReduction;
                }

                return snapshot;
            }

            /// <summary>
            /// The armor has to change a real hit, not only a flag. The same enemy claw, submitted through the
            /// authority from the same place at the same distance, is measured with the cuirass off and on, and
            /// then from inside, behind and beside the guard arc. Damage is read from the rig's own health pool
            /// and from the DamageInfo the authority produced for that specific submission.
            /// </summary>
            private IEnumerator GuardWithGearFlow(Inventory inventory, Equipment equipment, ItemData cuirass)
            {
                Check(_authority != null, "an authority is available to resolve the measured hits");
                Check(_rig != null && _rig.Health != null && !_rig.IsDead, "a living rig is available to take the measured hits");
                if (_authority == null || _rig == null || _rig.Health == null || _rig.IsDead) yield break;

                EnemyAgent enemy = PickAliveEnemy();
                Check(enemy != null && enemy.Data != null && enemy.Data.attackWeapon != null,
                    "a live enemy with an attack weapon is available for the armor measurement");
                if (enemy == null || enemy.Data == null || enemy.Data.attackWeapon == null) yield break;

                WeaponData claw = enemy.Data.attackWeapon;
                float declaredReduction = cuirass.StatTotal(SkillStat.BlockReduction);
                Check(declaredReduction > 0f, $"the cuirass declares a block reduction to measure ({declaredReduction:F2})");

                MeleeCombat melee = _rig.GetComponent<MeleeCombat>();
                bool meleeWasEnabled = melee != null && melee.enabled;
                bool cuirassWornAtEntry = equipment.Equipped(EquipmentSlot.Chest) == cuirass;
                float healthAtEntry = _rig.CurrentHealth;

                GearHitMeasurement nakedFront = new GearHitMeasurement();
                GearHitMeasurement wornFront = new GearHitMeasurement();
                GearHitMeasurement wornRear = new GearHitMeasurement();
                GearHitMeasurement wornSide = new GearHitMeasurement();

                // CaptureDamage is subscribed for exactly this measurement and nowhere else in the items flow,
                // so a capture here is a hit this flow caused.
                _rig.Health.Damaged += CaptureDamage;
                _capturedValid = false;
                _capturedDamage = DamageInfo.Invalid;

                try
                {
                    // One geometry for every measured hit: the same frozen attacker at the same standoff from
                    // the same anchor, with only the facing and the worn armor changing between submissions.
                    Vector3 anchor = _rig.transform.position;
                    Vector3 initialFacing = FlatForward(_rig.transform);
                    enemy.enabled = false;
                    enemy.transform.position = anchor + initialFacing * MeasuredStandoffDistance + Vector3.up * 0.1f;
                    Physics.SyncTransforms();
                    yield return WaitFrames(2);

                    if (melee != null) melee.enabled = false;

                    // ---- the independent variable really comes off, into the bag
                    Check(cuirassWornAtEntry, "the cuirass is worn when the measurement starts");
                    Check(equipment.TryUnequip(EquipmentSlot.Chest, out string nakedUnequip),
                        $"the cuirass is unequipped for the baseline ({nakedUnequip})");
                    Check(equipment.Equipped(EquipmentSlot.Chest) == null, "the chest slot is empty for the baseline");
                    Check(inventory.IndexOfItem(cuirass) >= 0, "the unequipped cuirass is back in the bag rather than deleted");
                    Check(_rig.Defense.BlockReduction <= 0.001f && !_rig.Defense.CanBlock,
                        $"without the armor the archer's own kit declares no reduction to guard with ({_rig.Defense})");

                    yield return SubmitMeasuredGuardHit(enemy, claw, 0, 0f, anchor, nakedFront);
                    CheckMeasurement("naked front", nakedFront, enemy);
                    Check(!nakedFront.blocked && nakedFront.infoReduction <= 0.001f,
                        "the unarmored baseline is legitimately unblocked, because the kit declares zero reduction");
                    Check(Mathf.Abs(nakedFront.damage - claw.baseDamage) < 0.75f,
                        $"the unarmored hit took the weapon's own number ({nakedFront.damage:F1} vs {claw.baseDamage:F1})");

                    // ---- same attack, same geometry, one more piece worn, put back through the bag
                    int cuirassIndex = inventory.IndexOfItem(cuirass);
                    Check(cuirassIndex >= 0, "the cuirass is picked back up out of the bag it was returned to");
                    Check(equipment.TryEquipFromInventory(cuirassIndex, out string wornEquip),
                        $"the cuirass is equipped again from the bag ({wornEquip})");
                    Check(equipment.Equipped(EquipmentSlot.Chest) == cuirass, "the chest slot holds the cuirass again");
                    Check(inventory.IndexOfItem(cuirass) < 0, "the worn cuirass is out of the bag again");
                    Check(_rig.Defense.BlockReduction >= declaredReduction - 0.001f && _rig.Defense.CanBlock,
                        $"wearing it raises the resolved reduction to its declared number ({_rig.Defense})");

                    yield return SubmitMeasuredGuardHit(enemy, claw, 1, 0f, anchor, wornFront);
                    CheckMeasurement("worn front", wornFront, enemy);
                    Check(wornFront.blocked, "worn armor blocks a hit that arrives inside the guard arc");
                    Check(wornFront.damage < nakedFront.damage - 0.25f,
                        $"the same attack lands softer behind the cuirass ({nakedFront.damage:F1} -> {wornFront.damage:F1})");
                    Check(Mathf.Abs(wornFront.infoReduction - declaredReduction) < 0.001f,
                        $"the authority reports the reduction the item declares ({wornFront.infoReduction:F2} vs {declaredReduction:F2})");
                    Check(Mathf.Abs(wornFront.damage - nakedFront.damage * (1f - declaredReduction)) < 0.75f,
                        $"the health delta is that reduction applied to the same hit ({wornFront.damage:F1} vs {nakedFront.damage * (1f - declaredReduction):F1})");
                    Check(Mathf.Abs(wornFront.travelDistance - nakedFront.travelDistance) < 0.05f,
                        $"both hits travelled the same distance ({nakedFront.travelDistance:F2} vs {wornFront.travelDistance:F2})");
                    Check(wornFront.facingError <= _rig.Defense.BlockArcDegrees * 0.5f,
                        $"the source was inside the guard arc for the blocked hit ({wornFront.facingError:F1} of {(_rig.Defense.BlockArcDegrees * 0.5f):F1} degrees)");

                    // ---- the arc stays directional even with the armor worn
                    yield return SubmitMeasuredGuardHit(enemy, claw, 2, 180f, anchor, wornRear);
                    CheckMeasurement("worn rear", wornRear, enemy);
                    Check(!wornRear.blocked, "facing away from the source drops the block even with the armor worn");
                    Check(wornRear.damage > wornFront.damage + 0.25f,
                        $"the rear hit took more damage than the blocked one ({wornRear.damage:F1} vs {wornFront.damage:F1})");
                    Check(Mathf.Abs(wornRear.damage - nakedFront.damage) < 0.75f,
                        $"the rear hit took the weapon's full number ({wornRear.damage:F1} vs {claw.baseDamage:F1})");

                    yield return SubmitMeasuredGuardHit(enemy, claw, 3, 90f, anchor, wornSide);
                    CheckMeasurement("worn side", wornSide, enemy);
                    Check(!wornSide.blocked, "ninety degrees off the source is outside the guard arc");
                    Check(wornSide.damage > wornFront.damage + 0.25f,
                        $"the side hit took more damage than the blocked one ({wornSide.damage:F1} vs {wornFront.damage:F1})");

                    Debug.Log($"[Emberdelve][Smoke:items] armor measurement: claw={claw.baseDamage:F1} arc={(_rig.Defense.BlockArcDegrees * 0.5f):F1}deg reduction={declaredReduction:F2} " +
                              $"nakedFront={nakedFront.damage:F1}/blocked={nakedFront.blocked} " +
                              $"wornFront={wornFront.damage:F1}/blocked={wornFront.blocked} " +
                              $"wornRear={wornRear.damage:F1}/blocked={wornRear.blocked} " +
                              $"wornSide={wornSide.damage:F1}/blocked={wornSide.blocked}");
                }
                finally
                {
                    // Subscription, gear, guard, the frozen agent and the pool all go back the way they were,
                    // including when an assertion failed part way through.
                    _rig.Health.Damaged -= CaptureDamage;
                    _capturedValid = false;
                    _capturedDamage = DamageInfo.Invalid;

                    _rig.SetGuardHeld(false);

                    if (cuirassWornAtEntry && equipment.Equipped(EquipmentSlot.Chest) != cuirass)
                    {
                        int restoreIndex = inventory.IndexOfItem(cuirass);
                        if (restoreIndex >= 0) equipment.TryEquipFromInventory(restoreIndex, out string restoreReason);
                    }

                    if (melee != null) melee.enabled = meleeWasEnabled;
                    enemy.enabled = true;
                    _rig.Health.SetHealth(Mathf.Min(healthAtEntry, _rig.MaxHealth));
                }
            }

            private const float MeasuredStandoffDistance = 1.6f;

            /// <summary>
            /// One controlled submission. The rig is re-seated at the same anchor and turned
            /// <paramref name="turnFromSource"/> degrees away from facing the attacker; the attacker is put back
            /// at the same standoff; the pool is topped up outside the measured interval; and the intent travels
            /// from the attacker to the rig, which is the direction the attack actually moves in. The health
            /// delta and the DamageInfo the authority produced for this submission are both recorded.
            /// </summary>
            private IEnumerator SubmitMeasuredGuardHit(EnemyAgent enemy, WeaponData claw, int hitIndex,
                float turnFromSource, Vector3 anchor, GearHitMeasurement result)
            {
                // player -> source is the direction the rig has to face. source -> player is the direction the
                // attack travels. They are opposites, and the guard arc is measured against the first one.
                Vector3 towardSource = enemy.transform.position - anchor;
                towardSource.y = 0f;
                if (towardSource.sqrMagnitude < 0.0001f)
                {
                    result.reason = "the attacker is on top of the rig, so there is no facing to measure";
                    yield break;
                }
                towardSource.Normalize();

                Vector3 facing = Quaternion.AngleAxis(turnFromSource, Vector3.up) * towardSource;
                _rig.transform.position = anchor;
                _rig.transform.rotation = Quaternion.LookRotation(facing);
                enemy.transform.position = anchor + towardSource * MeasuredStandoffDistance + Vector3.up * 0.1f;
                Physics.SyncTransforms();
                yield return WaitFrames(2);

                Vector3 origin = enemy.transform.position + Vector3.up * 1.2f;
                Vector3 hitPoint = _rig.CombatCenter;
                Vector3 travel = hitPoint - origin;
                travel.y = 0f;
                if (travel.sqrMagnitude < 0.0001f)
                {
                    result.reason = "the attack has no travel direction from that origin";
                    yield break;
                }
                travel.Normalize();

                result.facingError = Vector3.Angle(FlatForward(_rig.transform), towardSource);
                result.travelDistance = Vector3.Distance(origin, hitPoint);
                result.maxRange = Mathf.Max(1f, enemy.Data.attackRange) + 0.6f;
                result.turnFromSource = turnFromSource;

                // Real wait for the enemy weapon cooldown on the authority clock (no forged stamp).
                yield return WaitForWeaponReady(enemy.CombatId, claw);
                _rig.Health.SetHealth(_rig.MaxHealth);
                _rig.SetGuardHeld(true);
                yield return WaitFrames(1);

                result.guarding = _rig.IsGuarding;
                result.canBlock = _rig.Defense.CanBlock;
                result.rigReduction = _rig.Defense.BlockReduction;

                float before = _rig.CurrentHealth;
                int acceptedBefore = _localAuthority != null ? _localAuthority.AcceptedCount : 0;
                _capturedValid = false;
                _capturedDamage = DamageInfo.Invalid;

                int sequence = CombatRegistry.NextSequence(enemy.CombatId);
                result.sequence = sequence;
                result.accepted = _authority.SubmitAttack(
                    new AttackIntent(enemy.CombatId, EntityKind.Enemy, claw, 1f, origin, travel, result.maxRange,
                        sequence, Time.time),
                    new HitCandidate(_rig.CombatId, hitPoint, false));

                result.damage = before - _rig.CurrentHealth;
                result.sequenceConsumed = _localAuthority != null && _localAuthority.LastSequenceFor(enemy.CombatId) == sequence;
                result.acceptedCountGrew = _localAuthority != null && _localAuthority.AcceptedCount == acceptedBefore + 1;
                result.captured = _capturedValid;
                result.blocked = _capturedValid && _capturedDamage.Blocked;
                result.infoReduction = _capturedValid ? _capturedDamage.BlockReduction : 0f;
                result.sourceCombatId = _capturedValid ? _capturedDamage.SourceCombatId : -1;
                result.sourceKind = _capturedValid ? _capturedDamage.SourceKind : EntityKind.Neutral;
            }

            /// <summary>The facts every measured submission has to satisfy, whoever is wearing what.</summary>
            private void CheckMeasurement(string label, GearHitMeasurement m, EnemyAgent enemy)
            {
                Check(m.reason == null, $"{label}: the submission was set up ({m.reason ?? "ok"})");
                Check(m.accepted, $"{label}: the authority accepted the attack");
                Check(m.acceptedCountGrew, $"{label}: the authority counted exactly one accepted hit");
                Check(m.sequenceConsumed, $"{label}: the intent claimed the sequence the authority consumed (#{m.sequence})");
                Check(m.captured, $"{label}: the hit arrived on the rig as a DamageInfo");
                Check(m.sourceCombatId == enemy.CombatId && m.sourceKind == EntityKind.Enemy,
                    $"{label}: the capture names this enemy as its source (#{m.sourceCombatId} vs #{enemy.CombatId})");
                Check(m.damage > 0f, $"{label}: real health left the pool ({m.damage:F1})");
                Debug.Log($"[Emberdelve][Smoke:items] measurement {label}: turn={m.turnFromSource:F0}deg travel={m.travelDistance:F2}m range={m.maxRange:F2}m facing={m.facingError:F1}deg guarding={m.guarding} canBlock={m.canBlock} rigReduction={m.rigReduction:F2} blocked={m.blocked} infoReduction={m.infoReduction:F2} damage={m.damage:F1}");
            }

            /// <summary>What one measured submission did, read back from the authority and the health pool.</summary>
            private sealed class GearHitMeasurement
            {
                public string reason;
                public bool accepted;
                public bool acceptedCountGrew;
                public bool sequenceConsumed;
                public int sequence;
                public bool captured;
                public bool blocked;
                public float damage;
                public float infoReduction;
                public float rigReduction;
                public bool canBlock;
                public bool guarding;
                public int sourceCombatId;
                public EntityKind sourceKind;
                public float travelDistance;
                public float facingError;
                public float maxRange;
                public float turnFromSource;
            }

            private IEnumerator ChestFlow(Inventory inventory, Equipment equipment, ItemData coin,
                ItemData cuirass, ItemData draught, ItemData relic)
            {
                ChestSpawner spawner = _director.Chests;
                Check(spawner != null, "run director built a chest spawner from the layout");
                if (spawner == null) yield break;

                Check(spawner.Count >= 2, $"at least two containers exist in the level ({spawner.Count})");

                int supply = 0, ember = 0, sealedCount = 0;
                for (int i = 0; i < spawner.Chests.Count; i++)
                {
                    ChestInteractable chest = spawner.Chests[i];
                    if (chest == null) continue;

                    switch (chest.Variant)
                    {
                        case ChestVariant.Supply: supply++; break;
                        case ChestVariant.Ember: ember++; break;
                        case ChestVariant.Sealed: sealedCount++; break;
                    }
                }

                Check(supply >= 1 && ember >= 1 && sealedCount >= 1,
                    $"the level holds each variant (supply {supply}, ember {ember}, sealed {sealedCount})");

                if (_layout != null)
                {
                    bool allWalkable = true;
                    for (int i = 0; i < _layout.chestSpawns.Count; i++)
                    {
                        if (!_layout.IsWalkableCell(_layout.chestSpawns[i].cell)) allWalkable = false;
                    }
                    Check(allWalkable, "every chest sits on a walkable cell");
                }

                // ---- the variants differ in rules, not just in colour
                ChestInteractable supplyChest = FindChest(spawner, ChestVariant.Supply);
                ChestInteractable emberChest = FindChest(spawner, ChestVariant.Ember);
                ChestInteractable sealedChest = FindChest(spawner, ChestVariant.Sealed);

                if (supplyChest == null || emberChest == null || sealedChest == null) yield break;

                Check(!supplyChest.RequiresHold, "a supply chest opens on one press");
                Check(emberChest.RequiresHold && emberChest.HoldDuration > 0.5f,
                    $"an ember chest has to be held open ({emberChest.HoldDuration:F1}s)");
                Check(sealedChest.HoldDuration > emberChest.HoldDuration,
                    $"a sealed chest takes longer still ({sealedChest.HoldDuration:F1}s)");
                Check(supplyChest.LootTable != null && emberChest.LootTable != null &&
                      supplyChest.LootTable != emberChest.LootTable,
                    "the variants read different loot tables");

                // ---- determinism: the same seed always rolls the same contents
                List<LootDrop> rollA = new List<LootDrop>();
                List<LootDrop> rollB = new List<LootDrop>();
                List<LootDrop> rollC = new List<LootDrop>();

                emberChest.LootTable.Roll(emberChest.Seed, rollA);
                emberChest.LootTable.Roll(emberChest.Seed, rollB);
                emberChest.LootTable.Roll(emberChest.Seed + 977, rollC);

                Check(rollA.Count == rollB.Count && rollA.Count > 0, $"a seeded roll produced contents ({rollA.Count} drops)");

                bool identical = rollA.Count == rollB.Count;
                for (int i = 0; i < rollA.Count && identical; i++)
                {
                    if (rollA[i].Item != rollB[i].Item || rollA[i].Amount != rollB[i].Amount) identical = false;
                }
                Check(identical, "the same seed rolled the same contents twice");

                bool otherSeedDiffers = rollC.Count != rollA.Count;
                for (int i = 0; i < rollA.Count && !otherSeedDiffers; i++)
                {
                    if (rollC[i].Item != rollA[i].Item || rollC[i].Amount != rollA[i].Amount) otherSeedDiffers = true;
                }
                Check(otherSeedDiffers, "a different seed rolled something different, so the roll is really seeded");

                for (int i = 0; i < rollA.Count; i++)
                {
                    Check(rollA[i].Item != null && rollA[i].Amount > 0, $"drop {i} is a real item and amount ({rollA[i]})");
                }

                // ---- opening a chest hands its loot to the run host
                inventory.Clear();
                yield return WaitFrames(2);

                Check(supplyChest.CanInteract(out string supplyBlocked), "the supply chest can be opened");
                int unitsBefore = inventory.TotalUnits;
                int valueBefore = _director.LootValue;

                Check(supplyChest.Open(), "opening the supply chest handed something over");
                Check(supplyChest.Opened, "the chest records that it was opened");
                Check(inventory.TotalUnits > unitsBefore,
                    $"the player received the loot ({unitsBefore} -> {inventory.TotalUnits} units)");
                Check(_director.LootValue > valueBefore,
                    $"the run recorded the recovered value ({valueBefore} -> {_director.LootValue})");
                Check(supplyChest.TakenCount > 0 && supplyChest.Remaining.Count == 0,
                    $"everything came out ({supplyChest.TakenCount} taken, {supplyChest.Remaining.Count} left)");

                // The lid swings over about three quarters of a second, so the check waits for it.
                yield return WaitSeconds(1.2f);
                Check(supplyChest.Lid != null && Quaternion.Angle(supplyChest.Lid.localRotation, Quaternion.identity) > 30f,
                    "the lid actually swung open");

                Check(!supplyChest.CanInteract(out string emptyReason), "an emptied chest cannot be opened again");
                Check(!string.IsNullOrEmpty(emptyReason) && emptyReason.Contains("empty"),
                    $"the chest says so ({emptyReason})");

                // ---- a full bag leaves loot in the chest, and reopening finishes the job
                int fill = 0;
                while (inventory.TryAdd(cuirass, 1)) fill++;
                Check(fill > 0, $"bag filled with {fill} cuirasses to test leftovers");

                Check(emberChest.Open(), "the ember chest was opened with a nearly full bag");
                Check(emberChest.Opened, "the ember chest records that it was opened");
                Check(emberChest.Remaining.Count > 0,
                    $"loot the player could not carry stayed in the chest ({emberChest.Remaining.Count} stacks)");
                Check(emberChest.LeftBehindCount > 0, $"the chest counted the refusals ({emberChest.LeftBehindCount})");
                Check(emberChest.IsEmpty == false, "a chest with leftovers is not empty");

                int dropsLeft = emberChest.Remaining.Count;
                int carriedAfterPartial = inventory.TotalUnits;
                inventory.Clear();
                yield return WaitFrames(2);

                Check(emberChest.Open(), "reopening the same chest finished the job");
                Check(emberChest.Remaining.Count == 0, "nothing is left in the chest after a second open");
                Check(emberChest.IsEmpty, "the chest now reports itself empty");
                Check(inventory.TotalUnits > 0 && inventory.TotalUnits <= carriedAfterPartial,
                    $"the leftover loot was still there to be taken ({dropsLeft} drops -> {inventory.TotalUnits} units)");

                // ---- the sealed chest is gated by what the run has already recovered
                sealedChest.CanInteract(out string sealedReason);
                Debug.Log($"[Emberdelve][Smoke:items] sealed chest gate: {sealedReason}");

                if (_director.LootValue < ChestInteractable.SealedUnlockValue)
                {
                    Check(!sealedChest.CanInteract(out string stillSealed),
                        $"a sealed chest stays shut until the run has earned it ({stillSealed})");
                    Check(stillSealed != null && stillSealed.Contains("Sealed"), $"the gate explains itself ({stillSealed})");

                    _director.CollectLoot(relic, 1);
                    Check(_director.LootValue >= ChestInteractable.SealedUnlockValue,
                        $"recovering value opens the gate ({_director.LootValue})");
                }

                Check(sealedChest.CanInteract(out string nowOpenReason), "the sealed chest will open now");
                Check(sealedChest.Open(), "the sealed chest opened");
                Check(sealedChest.Remaining.Count == 0, "the sealed chest gave everything up");

                bool gotRelic = inventory.IndexOfItem(relic) >= 0;
                Check(gotRelic, "the sealed chest produced its guaranteed relic");

                // ---- the interact path itself: prompt, hold rules and range
                ChestInteractable target = FindNearestChest(spawner, _rig.transform.position);
                if (target != null)
                {
                    Vector2Int cell = CellOf(_layout, target.transform.position);
                    Vector3 stand = _layout.CellCenterWorld(cell) - _layout.CellCenterWorld(Vector2Int.zero) * 0f;
                    PlacePlayerAtCell(cell, target.transform.position);

                    yield return WaitFrames(3);

                    float distance = Vector3.Distance(_rig.transform.position, target.transform.position);
                    Check(distance <= _director.Config.interactionRange + 1.2f,
                        $"the player can stand close enough to interact ({distance:F1}m against a {_director.Config.interactionRange:F1}m range)");

                    Collider collider = target.GetComponent<Collider>();
                    Check(collider != null, "the chest carries a collider the interact probe can find");
                    Check(collider == null || collider.gameObject.layer == LayerMask.NameToLayer(GameConfig.InteractLayerName),
                        $"the chest is on the interact layer ({(collider != null ? collider.gameObject.layer : -1)})");

                    IInteractable asInteractable = target;
                    Check(!string.IsNullOrEmpty(asInteractable.PromptText), $"the chest offers a prompt ({asInteractable.PromptText})");
                }

                Debug.Log($"[Emberdelve][Smoke:items] chest summary: {DescribeChests(spawner)}");
            }

            private static ChestInteractable FindChest(ChestSpawner spawner, ChestVariant variant)
            {
                for (int i = 0; i < spawner.Chests.Count; i++)
                {
                    if (spawner.Chests[i] != null && spawner.Chests[i].Variant == variant) return spawner.Chests[i];
                }
                return null;
            }

            private static ChestInteractable FindNearestChest(ChestSpawner spawner, Vector3 from)
            {
                ChestInteractable best = null;
                float bestDistance = float.MaxValue;

                for (int i = 0; i < spawner.Chests.Count; i++)
                {
                    ChestInteractable chest = spawner.Chests[i];
                    if (chest == null) continue;

                    float distance = Vector3.Distance(from, chest.transform.position);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = chest;
                    }
                }

                return best;
            }

            private static Vector2Int CellOf(DungeonLayoutData layout, Vector3 world)
            {
                return layout != null ? layout.CellFromWorld(world) : Vector2Int.zero;
            }

            private void PlacePlayerAtCell(Vector2Int cell, Vector3 chestPosition)
            {
                Vector3 approach = chestPosition - _layout.CellCenterWorld(cell);
                approach.y = 0f;
                if (approach.sqrMagnitude < 0.01f) approach = Vector3.back;
                approach.Normalize();

                Vector3 stand = _layout.CellCenterWorld(cell) + approach * 1.1f;
                stand.y = _rig.transform.position.y;

                _rig.transform.position = stand;
                _rig.transform.rotation = Quaternion.LookRotation(-approach);
                Physics.SyncTransforms();
            }

            private static string DescribeChests(ChestSpawner spawner)
            {
                System.Text.StringBuilder builder = new System.Text.StringBuilder();
                for (int i = 0; i < spawner.Chests.Count; i++)
                {
                    ChestInteractable chest = spawner.Chests[i];
                    if (chest == null) continue;
                    if (builder.Length > 0) builder.Append(" | ");
                    builder.Append(chest.Variant).Append(" seed=").Append(chest.Seed)
                           .Append(" taken=").Append(chest.TakenCount)
                           .Append(" left=").Append(chest.Remaining.Count);
                }
                return builder.ToString();
            }

            // ---------------------------------------------------------------- warrior skills

            /// <summary>
            /// Milestone 4 assertions for the Warrior kit: the exactly-two kit rule, the mana pool, Dash as
            /// real collision-respecting movement, Battle Cry as authority-applied area damage plus a status,
            /// both passives as measurable numbers, and mana spend / refusal / regen.
            /// Runs last so it cannot disturb the Milestone 1-3 assertions before them.
            /// </summary>
            private IEnumerator WarriorSkillFlow()
            {
                SkillCaster caster = _rig.Skills;
                ManaComponent mana = _rig.Mana;
                CharacterClassData classData = _rig.Class;
                MeleeCombat melee = _rig.GetComponent<MeleeCombat>();
                WeaponData sword = _rig.Loadout != null ? _rig.Loadout.Primary : null;

                Check(caster != null, "warrior rig exposes a skill caster");
                Check(mana != null, "warrior rig exposes a mana pool");
                if (caster == null || mana == null) yield break;

                Check(caster.KitValid && caster.KitProblems.Count == 0,
                    $"warrior kit satisfies the exactly-two rule ({caster.KitProblems.Count} problems)");
                Check(classData.activeSkills != null && classData.activeSkills.Length == 2 &&
                      classData.passiveSkills != null && classData.passiveSkills.Length == 2,
                      "warrior class asset declares 2 active and 2 passive skills");

                SkillData dash = caster.ActiveSkill(0);
                SkillData cry = caster.ActiveSkill(1);
                Check(dash != null && dash.skillId == "warrior_dash", $"warrior active 1 is Dash ({SkillName(dash)})");
                Check(cry != null && cry.skillId == "warrior_battle_cry", $"warrior active 2 is Battle Cry ({SkillName(cry)})");
                Check(dash != null && cry != null && dash.effect == SkillEffectKind.Dash &&
                      cry.effect == SkillEffectKind.AreaCry, "warrior actives select two different effect behaviors");

                List<string> passiveIds = new List<string>();
                foreach (SkillData passive in caster.PassiveSkills) if (passive != null) passiveIds.Add(passive.skillId);
                Check(passiveIds.Count == 2, $"warrior exposes two passive skills ({passiveIds.Count})");
                Check(passiveIds.Contains("warrior_ashen_might"), "warrior passive 1 is Ashen Might");
                Check(passiveIds.Contains("warrior_bulwark_training"), "warrior passive 2 is Bulwark Training");
                Check(caster.PassiveSkills[0] != null && !string.IsNullOrEmpty(caster.PassiveSkills[0].displayName) &&
                      !string.IsNullOrEmpty(caster.PassiveSkills[0].description), "passives expose a name and a description");

                // U2A8 fixture migration. Old assumption: the mana ceiling is the raw class field
                // (EffectiveMaxMana = the authored 75). Accepted rule: max_mana = max(10, authored 75 +
                // 5 * INT modifier) with the warrior's INT 8 -> floor((8-10)/2) = -1 -> 70 (Docs/Design/
                // ATTRIBUTES.md). The authored base is asserted separately so a class asset written by
                // equipment is still caught.
                Check(classData.maxMana == 75, $"warrior class asset still authors the base mana 75 ({classData.maxMana})");
                Check(mana.MaxMana == 70,
                    $"mana pool is the derived 75 + 5*-1 (got {mana.MaxMana}, expected 70)");
                Check(mana.CurrentMana >= mana.MaxMana - 0.5f, $"mana starts full ({mana.CurrentMana:F0}/{mana.MaxMana})");
                Check(mana.RegenPerSecond > 0f, $"mana regen configured ({mana.RegenPerSecond:F1}/s)");

                GameObject hud = GameObject.Find("Emberdelve_Hud");
                Transform hudRoot = hud != null ? hud.transform.Find("HudRoot") : null;
                Transform skillBar = hudRoot != null ? hudRoot.Find("SkillBar") : null;
                Check(skillBar != null && skillBar.childCount == 2,
                    $"HUD builds a two-slot skill bar ({(skillBar != null ? skillBar.childCount : 0)} slots)");
                Check(hudRoot != null && hudRoot.Find("ManaFill") != null, "HUD builds a mana bar");
                Text manaLabel = hudRoot != null ? FindChildText(hudRoot, "ManaLabel") : null;
                Check(manaLabel != null && manaLabel.text.Contains("Mana"), $"HUD mana readout ({(manaLabel != null ? manaLabel.text : "none")})");
                Text kitLabel = hudRoot != null ? FindChildText(hudRoot, "KitLabel") : null;
                Check(kitLabel != null && kitLabel.text.Contains("Ashen Might") && kitLabel.text.Contains("Bulwark Training"),
                    "HUD names both passives");
                Check(!string.IsNullOrEmpty(caster.KeyLabel(0)) && caster.KeyLabel(0) != "?",
                    $"skill slot 1 reports its real binding ({caster.KeyLabel(0)})");

                // ---- passive 1: Ashen Might, measured as the authority's own damage number
                float outgoingMelee = _rig.GetOutgoingDamageMultiplier(sword);
                float outgoingRanged = _rig.GetOutgoingDamageMultiplier(_rig.Loadout != null ? _rig.Loadout.Secondary : null);
                // U2A8 fixture migration. Old assumption: the only melee factor is the Ashen Might passive
                // (1.20). Since U2A4 the authority reads ONE outgoing multiplier that already carries the
                // attribute term: warrior STR 16 -> floor((16-10)/2) = 3 -> 1 + 0.04*3 = 1.12, then the
                // passive 1.20, giving 1.344 (Docs/Design/ATTRIBUTES.md; the same number
                // EmberdelveAttributeActionGate asserts through the real authority).
                Check(Mathf.Abs(outgoingMelee - 1.344f) < 0.03f,
                    $"Ashen Might 1.20 x STR factor 1.12 = 1.344 ({outgoingMelee:F3})");
                Check(Mathf.Abs(outgoingRanged - 1f) < 0.03f, $"Ashen Might does not touch ranged damage ({outgoingRanged:F2})");

                EnemyAgent mightTarget = PickAliveEnemy();
                Check(mightTarget != null, "an enemy is available for the Ashen Might swing");
                if (mightTarget != null && melee != null && melee.enabled && sword != null)
                {
                    Check(PlacePlayerAtSpawnFacing(1), "player re-seated at the spawn cell for the skill tests");
                    mightTarget.enabled = false;
                    PlaceCombatCenterInRange(mightTarget, _rig.AimOrigin, _rig.transform.position,
                        _rig.transform.position + FlatForward(_rig.transform) * 2f, 1.9f);
                    _rig.transform.rotation = Quaternion.LookRotation(mightTarget.CombatCenter - _rig.AimOrigin);
                    Physics.SyncTransforms();
                    yield return WaitFrames(2);

                    // The authority records the last accepted attack per combat id on its own clock, so a
                    // real-time swing fired too soon after the validation attacks is correctly refused as faster
                    // than the cooldown. Wait for the authority own clock to clear instead of pretending the
                    // rejection did not happen.
                    float weaponClock = _localAuthority != null ? _localAuthority.WeaponCooldownRemaining(_rig.CombatId, sword) : 0f;
                    if (weaponClock > 0f)
                    {
                        Debug.Log($"[Emberdelve][Smoke:{Mode}] authority weapon clock for #{_rig.CombatId} still has {weaponClock:F1}s of cooldown from the validation attacks; waiting for it to clear before the passive swing");
                        yield return WaitUntil(() => _localAuthority == null || _localAuthority.WeaponCooldownRemaining(_rig.CombatId, sword) <= 0f, weaponClock + 4f);
                    }

                    Check(_localAuthority == null || _localAuthority.WeaponCooldownRemaining(_rig.CombatId, sword) <= 0f,
                        "the authority's weapon cooldown clock accepts a real-time swing now");

                    float healthBefore = mightTarget.CurrentHealth;
                    int acceptedBefore = _localAuthority != null ? _localAuthority.AcceptedCount : 0;
                    DamageInfo captured = DamageInfo.Invalid;
                    System.Action<DamageInfo> onDamaged = damage => captured = damage;
                    if (mightTarget.Health != null) mightTarget.Health.Damaged += onDamaged;

                    melee.BeginSwing();
                    yield return WaitSeconds(1.1f);

                    if (mightTarget.Health != null) mightTarget.Health.Damaged -= onDamaged;
                    ParkEnemyFar(mightTarget, "keep the Dash corridor clear of the melee test target");

                    bool damaged = captured.IsValid && mightTarget.CurrentHealth < healthBefore;
                    Check(damaged, $"Ashen Might swing damaged the enemy through the authority ({healthBefore:F0} -> {mightTarget.CurrentHealth:F0})");
                    if (damaged)
                    {
                        float expected = sword.baseDamage * (captured.Headshot ? DamageResolver.HeadshotMultiplier : 1f) * outgoingMelee;
                        Check(Mathf.Abs(captured.Amount - expected) < 0.8f,
                            $"melee damage is weapon base x passive ({captured.Amount:F1} vs expected {expected:F1}, headshot={captured.Headshot})");
                        Check(captured.Amount > sword.baseDamage + 1f,
                            $"the passive made the swing hit harder than the weapon alone ({captured.Amount:F1} > {sword.baseDamage:F1})");
                        Check(captured.SourceCombatId == _rig.CombatId && captured.SourceKind == EntityKind.LocalPlayer,
                            "the authority attributes the damage to the local player");
                        Check(_localAuthority != null && _localAuthority.AcceptedCount > acceptedBefore, "authority accepted the passive swing");
                    }
                }

                // ---- active 1: Dash in an open corridor
                if (dash != null)
                {
                    // Dash asks for 4.6m, so the test needs a corridor at least that long: three clear cells.
                    Check(PlacePlayerAtSpawnFacing(3), "player re-seated with three clear cells for the full Dash distance");
                    ClearEnemiesNear(_rig.transform.position, 9f, "the Dash corridor");
                    ClearCorridorAhead(_rig.transform.position, FlatForward(_rig.transform), dash.dashDistance, 0.6f, "the Dash corridor");
                    yield return WaitFrames(3);

                    ReportObstaclesAhead("pre-dash");
                    bool corridorClear = CorridorIsClear(_rig.transform.position, FlatForward(_rig.transform), dash.dashDistance + 0.6f, 0.6f, "the Dash corridor");
                    Check(corridorClear, "the Dash corridor is physically clear for the distance the skill asks for");

                    if (!corridorClear)
                    {
                        Debug.Log($"[Emberdelve][Smoke:{Mode}] open-corridor Dash precondition failed, skipping its movement assertions rather than reporting a movement bug; the wall case below still runs");
                    }

                    Vector3 before = _rig.transform.position;
                    float manaBefore = mana.CurrentMana;
                    int cost = dash.EffectiveManaCost;

                    Check(mana.HasAmount(cost), $"warrior can afford Dash ({cost} MP)");
                    Check(caster.TryCastSlot(0), "Dash cast accepted");
                    Check(_rig.Movement.IsDashing, "movement reports a dash in progress");
                    Check(Mathf.Abs(manaBefore - mana.CurrentMana - cost) < 0.6f,
                        $"Dash spent exactly its mana ({manaBefore:F0} -> {mana.CurrentMana:F0}, cost {cost})");
                    Check(!caster.IsReady(0) && caster.CooldownRemaining(0) > 0f,
                        $"Dash goes on cooldown ({caster.CooldownRemaining(0):F1}s of {dash.EffectiveCooldown:F1}s)");

                    yield return WaitSeconds(0.7f);

                    if (corridorClear)
                    {
                        float moved = Vector3.Distance(before, _rig.transform.position);
                        Check(moved > 3.0f && moved < dash.dashDistance + 1.5f,
                            $"Dash moved the player {moved:F2}m (asked for {dash.dashDistance:F1}m)");
                        Check(!_rig.Movement.DashWasBlocked, "the corridor dash was not blocked by geometry");
                        Check(_rig.Movement.IsGrounded, "Dash did not launch the player off the ground");
                        Vector2Int landed = _layout.CellFromWorld(_rig.transform.position);
                        Check(_grid.IsWalkable(landed.x, landed.y), $"Dash ended on walkable ground (cell {landed})");
                    }

                    RestoreClearedCorridor();

                    // cooldown refusal, reported to the HUD with a reason
                    string capturedReason = null;
                    int capturedSlot = -2;
                    System.Action<SkillSnapshot, string> onReject = (snapshot, reason) =>
                    {
                        capturedSlot = snapshot.Slot;
                        capturedReason = reason;
                    };
                    GameEvents.SkillCastRejected += onReject;

                    Check(!caster.CanCastSlot(0, out string cooldownReason), "Dash is refused while on cooldown");
                    Check(cooldownReason != null && cooldownReason.ToLower().Contains("cooldown"),
                        $"the refusal names the cooldown ({cooldownReason})");

                    float manaBeforeRefusal = mana.CurrentMana;
                    Check(!caster.TryCastSlot(0), "a cooldown-blocked cast is refused");
                    Check(Mathf.Abs(mana.CurrentMana - manaBeforeRefusal) < 0.001f, "a refused cast spends no mana");
                    Check(caster.CooldownRemaining(0) > 0f, "a refused cast does not restart or shorten the cooldown");
                    GameEvents.SkillCastRejected -= onReject;
                    Check(capturedSlot == 0 && capturedReason != null && capturedReason.ToLower().Contains("cooldown"),
                        $"the refusal is reported to the HUD (slot {capturedSlot}: {capturedReason})");

                    Text cooldownLabel = skillBar != null ? FindChildText(skillBar, "CooldownLabel") : null;
                    Check(cooldownLabel != null && cooldownLabel.text.Contains("s"),
                        $"the skill slot shows the remaining cooldown ({(cooldownLabel != null ? cooldownLabel.text : "none")})");

                    yield return WaitUntil(() => caster.IsReady(0), dash.EffectiveCooldown + 6f);
                    Check(caster.IsReady(0), "Dash is ready again when its cooldown expires");

                    // Dash into a wall: the wall decides how far it goes
                    if (TryFindWalkableCellFacingAWall(out Vector2Int wallCell, out Vector2Int blockedDirection))
                    {
                        _rig.transform.position = _layout.CellCenterWorld(wallCell) + Vector3.up * 0.15f;
                        _rig.transform.rotation = Quaternion.LookRotation(new Vector3(blockedDirection.x, 0f, blockedDirection.y));
                        Physics.SyncTransforms();
                        yield return WaitFrames(4);

                        Vector3 wallBefore = _rig.transform.position;
                        float manaBeforeWall = mana.CurrentMana;
                        Check(caster.TryCastSlot(0), "Dash cast accepted against a wall");
                        yield return WaitSeconds(0.7f);

                        float wallMoved = Vector3.Distance(wallBefore, _rig.transform.position);
                        Check(_rig.Movement.DashWasBlocked, "Dash reports that geometry stopped it");
                        Check(wallMoved < dash.dashDistance * 0.6f,
                            $"Dash did not pass through the wall ({wallMoved:F2}m of {dash.dashDistance:F1}m asked for)");
                        Vector2Int stillIn = _layout.CellFromWorld(_rig.transform.position);
                        Check(_grid.IsWalkable(stillIn.x, stillIn.y), $"the player is still inside walkable bounds (cell {stillIn})");
                        Check(Mathf.Abs(manaBeforeWall - mana.CurrentMana - cost) < 0.6f,
                            "a wall-blocked Dash still costs its mana: the cast happened, the wall decided the distance");
                    }
                    else
                    {
                        Check(false, "no walkable cell with a blocked neighbour found, cannot test Dash collision");
                    }
                }

                // ---- active 2: Battle Cry
                if (cry != null)
                {
                    Check(PlacePlayerAtSpawnFacing(1), "player re-seated for Battle Cry");
                    ClearEnemiesNear(_rig.transform.position, 9f, "the Battle Cry area");
                    ClearCorridorAhead(_rig.transform.position, FlatForward(_rig.transform), 7f, 2.5f, "the Battle Cry area");
                    yield return WaitFrames(2);

                    EnemyAgent cryTarget = PickAliveEnemy();
                    Check(cryTarget != null, "an enemy is available for Battle Cry");
                    if (cryTarget == null) yield break;

                    cryTarget.enabled = false;
                    PlaceEnemyAhead(cryTarget, 4.0f, 0f);
                    cryTarget.enabled = true;
                    Physics.SyncTransforms();
                    yield return WaitFrames(2);

                    EnemyAgent farTarget = PickAliveEnemyExcept(cryTarget);
                    if (farTarget != null)
                    {
                        farTarget.enabled = false;
                        farTarget.transform.position = _layout.CellCenterWorld(FindFarWalkableCell(_layout.CellFromWorld(_rig.transform.position))) + Vector3.up * 0.1f;
                        Physics.SyncTransforms();
                        yield return WaitFrames(2);
                    }

                    float cryManaBefore = mana.CurrentMana;
                    float cryHealthBefore = cryTarget.CurrentHealth;
                    float farHealthBefore = farTarget != null ? farTarget.CurrentHealth : 0f;
                    float playerHealthBefore = _rig.CurrentHealth;
                    int acceptedBeforeCry = _localAuthority != null ? _localAuthority.AcceptedCount : 0;
                    int consumedBeforeCry = _localAuthority != null ? _localAuthority.LastSequenceFor(_rig.CombatId) : -1;

                    DamageInfo cryDamage = DamageInfo.Invalid;
                    System.Action<DamageInfo> cryOnDamaged = damage => cryDamage = damage;
                    if (cryTarget.Health != null) cryTarget.Health.Damaged += cryOnDamaged;

                    Check(caster.TryCastSlot(1), "Battle Cry cast accepted");
                    yield return WaitFrames(3);

                    if (cryTarget.Health != null) cryTarget.Health.Damaged -= cryOnDamaged;

                    Check(Mathf.Abs(cryManaBefore - mana.CurrentMana - cry.EffectiveManaCost) < 0.6f,
                        $"Battle Cry spent its mana ({cryManaBefore:F0} -> {mana.CurrentMana:F0}, cost {cry.EffectiveManaCost})");
                    Check(cryDamage.IsValid, "Battle Cry damage arrived as an authority DamageInfo, not a direct health write");
                    Check(cryTarget.CurrentHealth < cryHealthBefore,
                        $"Battle Cry damaged the hostile inside its radius ({cryHealthBefore:F0} -> {cryTarget.CurrentHealth:F0})");
                    Check(_localAuthority != null && _localAuthority.AcceptedCount > acceptedBeforeCry,
                        "authority accepted the Battle Cry hit");
                    Check(_localAuthority != null && _localAuthority.LastSequenceFor(_rig.CombatId) > consumedBeforeCry,
                        $"Battle Cry claimed a fresh command sequence ({consumedBeforeCry} -> {(_localAuthority != null ? _localAuthority.LastSequenceFor(_rig.CombatId) : -1)})");
                    Check(_rig.CurrentHealth >= playerHealthBefore - 0.01f,
                        $"Battle Cry does not damage its own caster ({playerHealthBefore:F0} -> {_rig.CurrentHealth:F0})");

                    if (farTarget != null)
                    {
                        float farDistance = Vector3.Distance(_rig.CombatCenter, farTarget.CombatCenter);
                        Check(farDistance > cry.radius + 1f, $"the second enemy is outside the cry radius ({farDistance:F1}m vs {cry.radius:F1}m)");
                        Check(farTarget.CurrentHealth >= farHealthBefore - 0.01f,
                            $"Battle Cry does not reach outside its radius ({farHealthBefore:F0} -> {farTarget.CurrentHealth:F0})");
                    }

                    Check(cryTarget.HasStatus(StatusKind.Slow), "Battle Cry leaves the target slowed");
                    float slowMultiplier = cryTarget.Status != null ? cryTarget.Status.SpeedMultiplier : 1f;
                    Check(slowMultiplier < 0.95f, $"the slowed monster's speed multiplier is reduced ({slowMultiplier:F2})");
                    Check(cryTarget.EffectiveChaseSpeed < cryTarget.Data.chaseSpeed - 0.5f,
                        $"the slow lowers effective chase speed ({cryTarget.EffectiveChaseSpeed:F2} vs {cryTarget.Data.chaseSpeed:F2})");
                    Check(_rig.HasStatus(StatusKind.Empowered), "Battle Cry empowers the caster");

                    float buffed = _rig.GetOutgoingDamageMultiplier(sword);
                    Check(buffed > outgoingMelee + 0.05f, $"the self buff raises outgoing damage ({outgoingMelee:F2} -> {buffed:F2})");
                    Check(!caster.IsReady(1), $"Battle Cry goes on cooldown ({caster.CooldownRemaining(0):F1}/{caster.CooldownRemaining(1):F1}s)");

                    // The slow has to change how far the monster actually gets.
                    Check(PlacePlayerAtSpawnFacing(3), "player re-seated with a corridor three cells clear");
                    ClearEnemiesNear(_rig.transform.position, 9f, "the slow chase corridor");
                    ClearCorridorAhead(_rig.transform.position, FlatForward(_rig.transform), 7f, 2.0f, "the slow chase corridor");
                    cryTarget.enabled = false;
                    PlaceEnemyAhead(cryTarget, 6.0f, 0f);
                    cryTarget.transform.rotation = Quaternion.LookRotation(_rig.transform.position - cryTarget.transform.position);
                    cryTarget.enabled = true;
                    Physics.SyncTransforms();
                    yield return WaitFrames(2);

                    float distanceBeforeChase = Vector3.Distance(cryTarget.transform.position, _rig.transform.position);
                    yield return WaitSeconds(1.5f);
                    float distanceAfterChase = Vector3.Distance(cryTarget.transform.position, _rig.transform.position);
                    float closed = distanceBeforeChase - distanceAfterChase;
                    float unslowedWouldClose = cryTarget.Data.chaseSpeed * 1.5f;
                    Debug.Log($"[Emberdelve][Smoke:{Mode}] battle cry diag: start={distanceBeforeChase:F2} end={distanceAfterChase:F2} closed={closed:F2} unslowed={unslowedWouldClose:F2} slow={slowMultiplier:F2}");
                    Check(closed > 0.4f, $"the slowed monster still moves ({closed:F2}m closed in 1.5s)");
                    Check(closed < unslowedWouldClose * 0.8f,
                        $"the slow measurably reduces the distance it closes ({closed:F2}m < {unslowedWouldClose:F2}m unslowed)");

                    // The chase measurement is finished, so this hostile must not stay live 3-4m in front of
                    // the player: the cooldown wait and the mana measurement below are long, and a monster
                    // that is still walking at the caster can kill them mid-measurement. Death suspends mana
                    // regeneration on purpose (SkillCaster.HandleDeath -> ManaComponent.SetRegenerationActive),
                    // so a corpse would be reported as a regen bug. Same isolation the archer kit already
                    // applies to its volley and piercing targets, applied after the slow/chase assertions.
                    ParkEnemyFar(cryTarget, "the cry target must not chase the player during the cooldown and mana measurements");

                    RestoreClearedCorridor();
                }

                // ---- passive 2: Bulwark Training
                Check(_rig.Defense.CanBlock, "warrior still resolves a block capability");
                Check(_rig.Defense.BlockReduction > classData.blockReduction + 0.05f,
                    $"Bulwark Training raises guard reduction above the class value ({_rig.Defense.BlockReduction:F2} vs {_rig.Class.blockReduction:F2})");
                Check(Mathf.Abs(_rig.Defense.BlockReduction - (classData.blockReduction + 0.12f)) < 0.03f,
                    $"Bulwark Training adds exactly its declared 0.12 ({_rig.Defense.BlockReduction:F2})");

                // ---- insufficient mana. The runtime reports a cooldown before mana, so the mana rule is
                // tested on slots that are genuinely off cooldown; a refused cast must then leave none behind.
                yield return WaitUntil(() => caster.IsReady(0) && caster.IsReady(1), 16f);
                Check(caster.CooldownRemaining(0) <= 0f && caster.CooldownRemaining(1) <= 0f,
                    $"both warrior skills are off cooldown before the mana refusal test ({caster.CooldownRemaining(0):F1}/{caster.CooldownRemaining(1):F1}s)");

                Check(!_rig.IsDead, $"the warrior is alive for the mana refusal test ({_rig.CurrentHealth:F0}/{_rig.MaxHealth})");
                mana.TrySpend((int)mana.CurrentMana);
                Check(mana.CurrentMana < dash.EffectiveManaCost, $"mana drained below the kit's cheapest cost ({mana.CurrentMana:F0})");
                Check(!caster.CanCastSlot(0, out string manaReason), "Dash is refused when mana cannot cover it");
                Check(manaReason != null && manaReason.ToLower().Contains("mana"), $"the refusal names mana ({manaReason})");

                float manaBeforeManaRefusal = mana.CurrentMana;
                Vector3 positionBeforeManaRefusal = _rig.transform.position;
                int acceptedBeforeManaRefusal = _localAuthority != null ? _localAuthority.AcceptedCount : 0;
                Check(!caster.TryCastSlot(0), "an unaffordable cast is refused");
                Check(Mathf.Abs(mana.CurrentMana - manaBeforeManaRefusal) < 0.001f, "an unaffordable cast spends nothing");
                Check(Vector3.Distance(_rig.transform.position, positionBeforeManaRefusal) < 0.01f,
                    "an unaffordable cast does not move the player");
                Check(!_rig.Movement.IsDashing, "an unaffordable cast does not start a dash");
                Check(caster.CooldownRemaining(0) <= 0f, "an unaffordable cast does not start a cooldown");
                Check(_localAuthority != null && _localAuthority.AcceptedCount == acceptedBeforeManaRefusal,
                    "an unaffordable cast submits nothing to the authority");

                // ---- regen
                mana.Restore(mana.MaxMana);
                Check(mana.CurrentMana >= mana.MaxMana - 0.01f, $"mana can be restored to full ({mana.CurrentMana:F0}/{mana.MaxMana})");
                Check(mana.TrySpend(30), "mana spend accepted for the regen measurement");
                float afterSpend = mana.CurrentMana;
                Check(mana.RegenDelayRemaining > 0f, "spending mana pauses regeneration");

                // ManaComponent.Update only moves the pool while the component is enabled and active, the pool
                // is configured, not full and allowed to regenerate, and scaled game time is advancing. Each
                // of those is asserted before the wait so a pool that never moved names which one broke
                // instead of leaving the regen arithmetic to guess between a runtime bug and a dead caster.
                Check(!_rig.IsDead, $"the warrior is alive for the mana regen measurement ({_rig.CurrentHealth:F0}/{_rig.MaxHealth})");
                Check(mana.RegenerationAllowed, "mana regeneration is allowed for this living warrior");
                Check(mana.enabled && mana.gameObject.activeInHierarchy,
                    "the mana pool component is enabled and active in the hierarchy");
                Check(ReferenceEquals(_rig.Mana, mana) &&
                      _rig.Skills != null && ReferenceEquals(_rig.Skills.GetComponent<ManaComponent>(), mana) &&
                      _rig.GetComponents<ManaComponent>().Length == 1,
                      $"rig, caster and harness read one single mana pool (instance {mana.GetInstanceID()})");
                Check(Time.timeScale > 0.9f, $"the scaled clock is running for the regen tick ({Time.timeScale:F2})");

                ManaRegenDiag("before", mana, afterSpend, 0f, 0f);
                float regenScaledStart = Time.time;
                float regenUnscaledStart = Time.unscaledTime;

                yield return WaitSeconds(classData.manaRegenDelay + 1.5f);

                float regenScaledElapsed = Time.time - regenScaledStart;
                float regenUnscaledElapsed = Time.unscaledTime - regenUnscaledStart;
                ManaRegenDiag("after", mana, afterSpend, regenScaledElapsed, regenUnscaledElapsed);

                Check(!_rig.IsDead, $"the warrior survived the regen window ({_rig.CurrentHealth:F0}/{_rig.MaxHealth}, run state {(_director != null ? _director.State.ToString() : "none")})");
                Check(mana.RegenerationAllowed, "mana regeneration was still allowed at the end of the regen window");
                Check(mana.enabled && mana.gameObject.activeInHierarchy,
                    "the mana pool component was still enabled and active at the end of the regen window");
                Check(regenScaledElapsed >= classData.manaRegenDelay && regenScaledElapsed >= regenUnscaledElapsed - 0.5f,
                    $"scaled game time advanced with the real clock over the regen window ({regenScaledElapsed:F2}s scaled vs {regenUnscaledElapsed:F2}s unscaled, delay {classData.manaRegenDelay:F1}s)");

                Check(mana.CurrentMana > afterSpend + 0.5f, $"mana regenerates after its delay ({afterSpend:F0} -> {mana.CurrentMana:F0})");
                Check(mana.CurrentMana <= mana.MaxMana + 0.01f, $"mana regen does not overshoot the pool ({mana.CurrentMana:F0}/{mana.MaxMana})");

                string warriorPassives = caster.Stats != null ? caster.Stats.Describe() : "none";
                Debug.Log($"[Emberdelve][Smoke:{Mode}] warrior skill diag: kit={caster.KitValid} mana={mana.CurrentMana:F0}/{mana.MaxMana} defense={_rig.Defense} passives={warriorPassives}");
            }

            /// <summary>
            /// Milestone 4 assertions for the Archer kit: the kit rule, Fleet Footing through the same speed
            /// rule a server would validate, Farshot through the authority's damage number, Multi-shot as a
            /// real three-arrow volley that spends real arrows and hits each target once, Piercing Shot as
            /// penetration with a target cap and no duplicate hits, and the ammo / mana / weapon refusals.
            /// </summary>
            private IEnumerator ArcherSkillFlow()
            {
                SkillCaster caster = _rig.Skills;
                ManaComponent mana = _rig.Mana;
                RangedCombat ranged = _rig.GetComponent<RangedCombat>();
                ClassLoadout loadout = _rig.Loadout;
                CharacterClassData classData = _rig.Class;
                WeaponData bow = loadout != null ? loadout.Primary : null;

                Check(caster != null && mana != null && ranged != null, "archer rig exposes a skill caster, a mana pool and ranged combat");
                if (caster == null || mana == null || ranged == null) yield break;

                // The skill tests need a living player and a lane nobody wanders into. Enemies are parked,
                // not removed, and the ones each test needs are placed explicitly afterwards.
                ClearEnemiesNear(_rig.transform.position, 80f, "the archer skill tests");
                if (_rig.IsDead)
                {
                    Check(false, "archer died before its skill tests, later results would be about a corpse rather than the kit");
                    yield break;
                }

                if (_rig.Health != null && _rig.CurrentHealth < _rig.MaxHealth)
                {
                    _rig.Health.Heal(_rig.MaxHealth);
                    Debug.Log($"[Emberdelve][Smoke:{Mode}] archer healed to {_rig.CurrentHealth:F0}/{_rig.MaxHealth} before the skill tests");
                }

                Check(caster.KitValid && caster.KitProblems.Count == 0,
                    $"archer kit satisfies the exactly-two rule ({caster.KitProblems.Count} problems)");

                SkillData multi = caster.ActiveSkill(0);
                SkillData pierce = caster.ActiveSkill(1);
                Check(multi != null && multi.skillId == "archer_multi_shot", $"archer active 1 is Multi-shot ({SkillName(multi)})");
                Check(pierce != null && pierce.skillId == "archer_piercing_shot", $"archer active 2 is Piercing Shot ({SkillName(pierce)})");
                Check(multi != null && pierce != null && multi.effect == SkillEffectKind.SpreadShot &&
                      pierce.effect == SkillEffectKind.PiercingShot, "archer actives select two different projectile behaviors");
                Check(multi != null && multi.projectileCount == 3 && pierce != null && pierce.projectileMaxTargets == 2,
                    $"the projectile skills declare their own counts (volley {multi.projectileCount}, pierce {pierce.projectileMaxTargets})");

                List<string> passiveIds = new List<string>();
                foreach (SkillData passive in caster.PassiveSkills) if (passive != null) passiveIds.Add(passive.skillId);
                Check(passiveIds.Count == 2, $"archer exposes two passive skills ({passiveIds.Count})");
                Check(passiveIds.Contains("archer_farshot"), "archer passive 1 is Farshot");
                Check(passiveIds.Contains("archer_fleet_footing"), "archer passive 2 is Fleet Footing");

                // ---- passive 1: Fleet Footing, measured through the resolved speed rule
                float walk = _rig.Movement.ResolveGroundSpeed(false, false);
                float sprint = _rig.Movement.ResolveGroundSpeed(true, false);
                // U2A8 fixture migration. Old assumption: Fleet Footing's 1.15 is the only multiplier on the
                // authored speeds. Accepted walking rule (Docs/Design/ATTRIBUTES.md, U2A4):
                // clamp(authored 4.3 * max(0.5, 1 + 0.025 * DEX modifier) * passive + flat, 2, 8), and sprint
                // keeps its own authored 6.4 base with the same DEX factor. Archer DEX 16 ->
                // floor((16-10)/2) = 3 -> factor 1.075, Fleet Footing 1.15: walk 4.3*1.075*1.15 = 5.315875,
                // sprint 6.4*1.075*1.15 = 7.912.
                Check(Mathf.Abs(walk - 5.315875f) < 0.06f,
                    $"Fleet Footing sits on top of the DEX term ({walk:F4} against 4.3*1.075*1.15 = 5.315875)");
                Check(Mathf.Abs(sprint - 7.912f) < 0.06f,
                    $"sprint keeps its authored 6.4 base with the same factors ({sprint:F4} against 6.4*1.075*1.15 = 7.912)");
                Check(sprint > walk + 0.1f, $"the resolved speed rule still distinguishes sprint from walk ({sprint:F2} > {walk:F2})");

                // ---- passive 2: Farshot
                float outgoingRanged = _rig.GetOutgoingDamageMultiplier(bow);
                float outgoingMelee = _rig.GetOutgoingDamageMultiplier(loadout != null ? loadout.Secondary : null);
                // U2A8 fixture migration. Old assumption: the ranged factor is Farshot's 1.18 alone and the
                // melee factor is 1. Accepted: the archer's DEX 16 -> modifier 3 -> 1 + 0.04*3 = 1.12 rides on
                // the bow (bow/crossbow scale off DEX), so 1.12*1.18 = 1.3216; the archer's secondary is the
                // rapier, whose authored finesse marker makes it read the better of STR 0 and DEX 3 -> 1.12,
                // with no 1.18 in it. Both numbers still separate the passive from the attribute term.
                Check(Mathf.Abs(outgoingRanged - 1.3216f) < 0.03f,
                    $"Farshot 1.18 x DEX factor 1.12 = 1.3216 ({outgoingRanged:F4})");
                Check(Mathf.Abs(outgoingMelee - 1.12f) < 0.03f,
                    $"Farshot adds nothing to the finesse rapier: max(STR 0, DEX 3) gives 1.12, not 1.18 or 1.3216 ({outgoingMelee:F4})");

                // ---- the per-activation ability scale both actives carry
                // Accepted rule (Docs/Design/ATTRIBUTES.md, proven live in U2A5): ability effect_scale =
                // clamp(1 + 0.04 * CHA modifier, 0.6, 1.4) rides on a skill activation's damage magnitude.
                // It never scales mana cost, cooldown or the number of projectiles, and an ordinary arrow
                // never carries it. The expectation below is fixture arithmetic from the authored class
                // score - floor((8-10)/2) = -1 -> 1 - 0.04 = 0.96 - not a call into the resolver whose
                // resolved DamageInfo.Amount is what it asserts.
                int authoredCha = classData != null ? classData.charisma : 0;
                Check(classData != null && authoredCha == 8,
                    $"the archer class asset authors CHA 8 ({authoredCha})");
                int chaModifier = Mathf.FloorToInt((authoredCha - 10) / 2f);
                float abilityScale = Mathf.Clamp(1f + 0.04f * chaModifier, 0.6f, 1.4f);
                Check(chaModifier == -1 && Mathf.Abs(abilityScale - 0.96f) < 0.0001f,
                    $"CHA 8 -> modifier {chaModifier} -> effect scale {abilityScale:F4} (clamp(1 + 0.04 * -1, 0.6, 1.4))");

                // ---- ammo refusal: a skill cannot fire without the rounds the weapon actually holds
                int magazineStart = ranged.Magazine;
                Check(ranged.TryConsumeAmmo(ranged.Magazine), $"magazine emptied for the ammo refusal test ({magazineStart} -> {ranged.Magazine})");
                float manaBeforeAmmoRefusal = mana.CurrentMana;
                int acceptedBeforeAmmoRefusal = _localAuthority != null ? _localAuthority.AcceptedCount : 0;
                Check(!caster.CanCastSlot(0, out string ammoReason), "Multi-shot is refused with an empty magazine");
                Check(ammoReason != null && (ammoReason.ToLower().Contains("arrow") || ammoReason.ToLower().Contains("ammo")),
                    $"the refusal names the missing ammunition ({ammoReason})");
                Check(!caster.TryCastSlot(0), "an unarmable volley is refused");
                Check(Mathf.Abs(mana.CurrentMana - manaBeforeAmmoRefusal) < 0.001f, "a refused volley spends no mana");
                Check(_localAuthority != null && _localAuthority.AcceptedCount == acceptedBeforeAmmoRefusal,
                    "a refused volley submits nothing to the authority");

                ClassLoadout.RangedAmmoState bowAmmo = loadout != null ? loadout.GetAmmoState(bow) : null;
                ranged.Initialize(bow, bowAmmo != null ? bowAmmo.reserve : ranged.Reserve,
                    bowAmmo != null ? bowAmmo.maxReserve : 0, Mathf.Max(1, bow.magazineSize));
                Check(ranged.Magazine == bow.magazineSize, $"re-equipping the bow restores its magazine ({ranged.Magazine}/{bow.magazineSize})");

                // ---- mana refusal, before any of the skills are on cooldown
                mana.TrySpend((int)mana.CurrentMana);
                Check(mana.CurrentMana < multi.EffectiveManaCost, $"mana drained below the kit's costs ({mana.CurrentMana:F0})");
                Check(!caster.CanCastSlot(0, out string manaReason), "Multi-shot is refused when mana cannot cover it");
                Check(manaReason != null && manaReason.ToLower().Contains("mana"), $"the refusal names mana ({manaReason})");
                float manaBeforeManaRefusal = mana.CurrentMana;
                int magazineBeforeManaRefusal = ranged.Magazine;
                Check(!caster.TryCastSlot(0), "an unaffordable volley is refused");
                Check(Mathf.Abs(mana.CurrentMana - manaBeforeManaRefusal) < 0.001f, "an unaffordable volley spends no mana");
                Check(ranged.Magazine == magazineBeforeManaRefusal, "an unaffordable volley spends no arrows");
                mana.Restore(mana.MaxMana);
                Check(mana.CurrentMana >= mana.MaxMana - 0.01f, $"mana restored for the skill tests ({mana.CurrentMana:F0}/{mana.MaxMana})");

                // ---- active 1: Multi-shot, three arrows into three separate targets
                // The lane the volley actually needs comes from the skill's own spread angle, not from a
                // guess: at depth d the outer arrows sit at d * tan(spread / 2) to either side, and each of
                // them needs its own target footprint. Those bounds are settled BEFORE any target is placed,
                // and the player is then seated in the first cell / orientation the level really offers for
                // a lane that size (PlacePlayerInClearLane), instead of being pinned to the spawn cell where
                // Chest_Supply_5_4_0 stands 1.475m to the side of the corridor and a 1.62m half lane cannot
                // fit past it.
                float volleyDepth = 4.0f;
                float volleyLateral = volleyDepth * Mathf.Tan(Mathf.Max(1f, multi.spreadDegrees) * 0.5f * Mathf.Deg2Rad);
                float volleyLaneHalf = volleyLateral + 0.7f;

                bool volleyLanePlaced = PlacePlayerInClearLane(volleyDepth + 0.4f, volleyLaneHalf,
                    "the volley corridor", out Vector2Int volleyCell, out Vector2Int volleyFacing);
                yield return WaitFrames(2);

                bool volleyLaneClear = volleyLanePlaced && CorridorIsClear(_rig.transform.position, FlatForward(_rig.transform),
                    volleyDepth + 0.4f, volleyLaneHalf, "the volley corridor");
                Check(volleyLaneClear,
                    $"the volley lane is clear for a {multi.spreadDegrees:F0} degree spread out to {volleyDepth:F1}m (lane half width {volleyLaneHalf:F2}m, cell {volleyCell} facing {volleyFacing} at {_rig.transform.position})");

                EnemyAgent shotA = PickAliveEnemy();
                EnemyAgent shotB = PickAliveEnemyExcept(shotA);
                EnemyAgent shotC = PickAliveEnemyExcept(shotA, shotB);
                Check(shotA != null && shotB != null && shotC != null,
                    $"three live enemies are available for the volley ({(shotA != null ? 1 : 0) + (shotB != null ? 1 : 0) + (shotC != null ? 1 : 0)})");
                if (shotA == null || shotB == null || shotC == null) yield break;

                foreach (EnemyAgent target in new[] { shotA, shotB, shotC }) target.enabled = false;
                PlaceEnemyAhead(shotA, volleyDepth, 0f);
                PlaceEnemyAhead(shotB, volleyDepth, volleyLateral);
                PlaceEnemyAhead(shotC, volleyDepth, -volleyLateral);
                AimAtPoint(_rig.transform.position + FlatForward(_rig.transform) * volleyDepth + Vector3.up * 1.15f);
                yield return WaitFrames(3);

                Debug.Log($"[Emberdelve][Smoke:{Mode}] volley geometry diag: spread={multi.spreadDegrees:F0} deg lateral={volleyLateral:F2}m depth={volleyDepth:F1}m A={shotA.transform.position} B={shotB.transform.position} C={shotC.transform.position} muzzle={_rig.GetMuzzlePosition()} aim={_rig.AimDirection}");

                int volleyBefore = ranged.Magazine;
                int acceptedBeforeVolley = _localAuthority != null ? _localAuthority.AcceptedCount : 0;
                int consumedBeforeVolley = _localAuthority != null ? _localAuthority.LastSequenceFor(_rig.CombatId) : -1;
                int arrowsBefore = CountProjectileVisuals();
                float manaBeforeVolley = mana.CurrentMana;

                Dictionary<EnemyAgent, DamageInfo> volleyDamage = new Dictionary<EnemyAgent, DamageInfo>();
                System.Action<EnemyAgent, DamageInfo> recorder = (target, damage) => volleyDamage[target] = damage;
                System.Action<DamageInfo> handlerA = damage => recorder(shotA, damage);
                System.Action<DamageInfo> handlerB = damage => recorder(shotB, damage);
                System.Action<DamageInfo> handlerC = damage => recorder(shotC, damage);
                if (shotA.Health != null) shotA.Health.Damaged += handlerA;
                if (shotB.Health != null) shotB.Health.Damaged += handlerB;
                if (shotC.Health != null) shotC.Health.Damaged += handlerC;

                Check(caster.TryCastSlot(0), "Multi-shot cast accepted");
                yield return WaitSeconds(0.6f);

                int arrowsAfter = CountProjectileVisuals();
                Check(ranged.Magazine == volleyBefore - multi.projectileCount,
                    $"Multi-shot spent one arrow per projectile from the magazine ({volleyBefore} -> {ranged.Magazine}, volley {multi.projectileCount})");
                Check(Mathf.Abs(manaBeforeVolley - mana.CurrentMana - multi.EffectiveManaCost) < 0.6f,
                    $"Multi-shot spent its mana ({manaBeforeVolley:F0} -> {mana.CurrentMana:F0})");
                Check(arrowsAfter >= arrowsBefore + multi.projectileCount,
                    $"Multi-shot spawned one arrow visual per projectile ({arrowsBefore} -> {arrowsAfter})");

                int hitTargets = 0;
                if (volleyLaneClear)
                {
                    Check(_localAuthority != null && _localAuthority.AcceptedCount == acceptedBeforeVolley + 3,
                        $"the authority accepted exactly one hit per arrow ({acceptedBeforeVolley} -> {(_localAuthority != null ? _localAuthority.AcceptedCount : -1)})");
                    Check(_localAuthority != null && _localAuthority.LastSequenceFor(_rig.CombatId) == consumedBeforeVolley + 3,
                        $"each arrow claimed its own fresh sequence ({consumedBeforeVolley} -> {(_localAuthority != null ? _localAuthority.LastSequenceFor(_rig.CombatId) : -1)})");

                    foreach (KeyValuePair<EnemyAgent, DamageInfo> pair in volleyDamage)
                    {
                        if (!pair.Value.IsValid) continue;
                        hitTargets++;
                        // Weapon falloff x the skill's own authored multiplier x the archer's outgoing ranged
                        // factor (DEX 1.12 x Farshot 1.18) x the CHA 8 activation scale 0.96 x headshot if the
                        // arrow actually took one. 34 * 0.7 * 1.3216 * 0.96 = 30.1959168 on a body hit.
                        float expected = bow.DamageAtRange(Vector3.Distance(pair.Value.SourcePosition, pair.Value.Point))
                                         * multi.damageMultiplier * outgoingRanged * abilityScale
                                         * (pair.Value.Headshot ? DamageResolver.HeadshotMultiplier : 1f);
                        Check(Mathf.Abs(pair.Value.Amount - expected) < 0.9f,
                            $"target {pair.Key.name} took one arrow's damage ({pair.Value.Amount:F1} vs expected {expected:F1}), not several");
                    }

                    Check(hitTargets == 3, $"the spread hit three separate targets ({hitTargets}/3)");
                }
                else
                {
                    Debug.Log($"[Emberdelve][Smoke:{Mode}] volley lane precondition failed, skipping its hit assertions rather than reporting a projectile bug");
                }

                RestoreClearedCorridor();

                foreach (EnemyAgent target in new[] { shotA, shotB, shotC })
                {
                    if (target.Health != null) target.Health.Damaged -= target == shotA ? handlerA : (target == shotB ? handlerB : handlerC);
                    ParkEnemyFar(target, "the volley targets must not chase the player during later tests");
                }

                // ---- active 2: Piercing Shot, penetration with a cap and no duplicate hits
                Check(PlacePlayerAtSpawnFacing(3), "player re-seated with three cells of clear corridor");
                ClearEnemiesNear(_rig.transform.position, 12f, "the piercing line");
                ClearCorridorAhead(_rig.transform.position, FlatForward(_rig.transform), 8f, 0.5f, "the piercing line");
                yield return WaitFrames(2);

                bool pierceLaneClear = CorridorIsClear(_rig.transform.position, FlatForward(_rig.transform),
                    7.4f, 0.5f, "the piercing line");
                Check(pierceLaneClear, "the piercing line is clear for the distance the shot must travel");

                EnemyAgent lineA = PickAliveEnemy();
                EnemyAgent lineB = PickAliveEnemyExcept(lineA);
                EnemyAgent lineC = PickAliveEnemyExcept(lineA, lineB);
                Check(lineA != null && lineB != null && lineC != null, "three live enemies are available for the piercing line");
                if (lineA == null || lineB == null || lineC == null) yield break;

                foreach (EnemyAgent target in new[] { lineA, lineB, lineC }) target.enabled = false;
                PlaceEnemyAhead(lineA, 3.0f, 0f);
                PlaceEnemyAhead(lineB, 5.0f, 0f);
                PlaceEnemyAhead(lineC, 7.0f, 0f);
                AimAtPoint(_rig.transform.position + FlatForward(_rig.transform) * 5.0f + Vector3.up * 1.0f);
                yield return WaitFrames(3);

                int pierceMagazineBefore = ranged.Magazine;
                int acceptedBeforePierce = _localAuthority != null ? _localAuthority.AcceptedCount : 0;
                float pierceManaBefore = mana.CurrentMana;
                float healthA = lineA.CurrentHealth;
                float healthB = lineB.CurrentHealth;
                float healthC = lineC.CurrentHealth;

                DamageInfo pierceDamageA = DamageInfo.Invalid;
                DamageInfo pierceDamageB = DamageInfo.Invalid;
                DamageInfo pierceDamageC = DamageInfo.Invalid;
                int hitsA = 0;
                int hitsB = 0;
                System.Action<DamageInfo> pierceA = damage => { pierceDamageA = damage; hitsA++; };
                System.Action<DamageInfo> pierceB = damage => { pierceDamageB = damage; hitsB++; };
                System.Action<DamageInfo> pierceC = damage => pierceDamageC = damage;
                if (lineA.Health != null) lineA.Health.Damaged += pierceA;
                if (lineB.Health != null) lineB.Health.Damaged += pierceB;
                if (lineC.Health != null) lineC.Health.Damaged += pierceC;

                Check(caster.TryCastSlot(1), "Piercing Shot cast accepted");
                yield return WaitSeconds(0.6f);

                if (lineA.Health != null) lineA.Health.Damaged -= pierceA;
                if (lineB.Health != null) lineB.Health.Damaged -= pierceB;
                if (lineC.Health != null) lineC.Health.Damaged -= pierceC;
                foreach (EnemyAgent target in new[] { lineA, lineB, lineC }) ParkEnemyFar(target, "the piercing line targets must not chase the player during later tests");

                Check(ranged.Magazine == pierceMagazineBefore - 1, $"Piercing Shot spent exactly one arrow ({pierceMagazineBefore} -> {ranged.Magazine})");
                Check(Mathf.Abs(pierceManaBefore - mana.CurrentMana - pierce.EffectiveManaCost) < 0.6f, "Piercing Shot spent its mana");

                if (pierceLaneClear)
                {
                    Check(lineA.CurrentHealth < healthA, $"Piercing Shot hit the first body ({healthA:F0} -> {lineA.CurrentHealth:F0})");
                    Check(lineB.CurrentHealth < healthB, $"Piercing Shot penetrated to the second body ({healthB:F0} -> {lineB.CurrentHealth:F0})");
                    Check(hitsA == 1 && hitsB == 1, $"no entity was hit twice by the same arrow (A {hitsA}, B {hitsB})");
                    Check(lineC.CurrentHealth >= healthC - 0.01f,
                        $"the shot stopped at its target cap ({pierce.projectileMaxTargets}) and did not reach the third body ({healthC:F0} -> {lineC.CurrentHealth:F0})");
                    Check(_localAuthority != null && _localAuthority.AcceptedCount == acceptedBeforePierce + pierce.projectileMaxTargets,
                        $"the authority accepted both hits of the one activation ({acceptedBeforePierce} -> {(_localAuthority != null ? _localAuthority.AcceptedCount : -1)}, cap {pierce.projectileMaxTargets})");

                    // The two accepted hits share one activation id. A second button press is a new activation,
                    // and the authority must refuse it while the skill's own cooldown is still running.
                    Check(!caster.TryCastSlot(1), "the skill key cannot fire Piercing Shot again during its cooldown");
                    Check(caster.CanCastSlot(1, out string pierceCooldownReason) == false &&
                          pierceCooldownReason != null && pierceCooldownReason.ToLower().Contains("cooldown"),
                          $"the refusal names the cooldown ({pierceCooldownReason})");

                    // M2 (R1b review): this block used lineA, which the Piercing Shot above kills, so it never ran. lineC
                    // is never hit by that shot (the target cap is 2), so it is alive and the checks really execute.
                    if (lineC != null && !lineC.IsDead && _authority != null)
                    {
                        Vector3 muzzle = _rig.GetMuzzlePosition();
                        Vector3 toward = lineC.CombatCenter - muzzle;
                        if (toward.sqrMagnitude > 0.0001f)
                        {
                            int forgedSequence = CombatRegistry.NextSequence(_rig.CombatId);
                            SkillIntent forged = new SkillIntent(_rig.CombatId, EntityKind.LocalPlayer, pierce, bow,
                                muzzle, toward.normalized, Mathf.Max(1f, bow.rangedRange), forgedSequence, Time.time,
                                pierce.TargetStatusSpec, 987654321);

                            int acceptedBeforeForged = _localAuthority != null ? _localAuthority.AcceptedCount : 0;
                            bool forgedAccepted = _authority.SubmitSkill(forged, new HitCandidate(lineC.CombatId, lineC.CombatCenter, false));

                            // R1b: the old fixture minted a fresh activation id client side (CombatRegistry.NextActivationId)
                            // and expected the cooldown to refuse it. Ids are now allocated by the authority, so the two
                            // halves of that claim are asserted separately: an id the authority never issued carries no
                            // hit, and a genuine new cast request (TryBeginSkillCast) is refused by the cooldown.
                            Check(!forgedAccepted, "the authority refuses a hit that carries an activation id it never issued");
                            bool beginAgain = _authority.TryBeginSkillCast(_rig.CombatId, pierce.skillId, out int _, out string beginReason);
                            Check(!beginAgain && beginReason != null && beginReason.ToLower().Contains("cooldown"),
                                $"the authority refuses a new activation of the same skill while its cooldown runs ({beginReason})");
                            Check(_localAuthority != null && _localAuthority.AcceptedCount == acceptedBeforeForged,
                                "the refused activation added no accepted hit");
                        }
                    }
                }
                else
                {
                    Debug.Log($"[Emberdelve][Smoke:{Mode}] piercing lane precondition failed, skipping its hit assertions rather than reporting a projectile bug");
                }

                if (pierceDamageA.IsValid)
                {
                    // Same chain as the volley arrow, with Piercing Shot's authored 1.35 instead of 0.7 and the
                    // same CHA 8 activation scale: 34 * 1.35 * 1.3216 * 0.96 = 58.2349824 on a body hit, which
                    // is the number the accepted U2A5 gate measures for this same class at this same CHA.
                    float expected = bow.DamageAtRange(Vector3.Distance(pierceDamageA.SourcePosition, pierceDamageA.Point))
                                     * pierce.damageMultiplier * outgoingRanged * abilityScale
                                     * (pierceDamageA.Headshot ? DamageResolver.HeadshotMultiplier : 1f);
                    Check(Mathf.Abs(pierceDamageA.Amount - expected) < 0.9f,
                        $"the piercing arrow's damage is weapon falloff x skill multiplier x Farshot x CHA effect scale ({pierceDamageA.Amount:F1} vs expected {expected:F1})");
                    Check(pierceDamageA.Amount > bow.baseDamage * 0.9f,
                        $"the piercing multiplier made it a heavy arrow ({pierceDamageA.Amount:F1} vs bow base {bow.baseDamage:F1})");
                }

                // ---- the projectile skills respect what is in hand. Both are on cooldown from the tests
                // above, so wait for them to be genuinely ready before asserting the weapon rule.
                yield return WaitUntil(() => caster.IsReady(0) && caster.IsReady(1), 16f);
                Check(!_rig.IsDead, "the archer is still alive, so a refusal can be attributed to the rules and not to death");
                Check(_rig.Mana != null && _rig.Mana.RegenerationAllowed,
                    "mana regeneration is still allowed for this living archer");
                Check(caster.IsReady(0) && caster.IsReady(1),
                    $"both archer skills are off cooldown before the weapon requirement test ({caster.CooldownRemaining(0):F1}/{caster.CooldownRemaining(1):F1}s)");

                Check(loadout != null && loadout.SwitchWeapon(), "archer can switch to the rapier");
                Check(!caster.CanCastSlot(0, out string weaponReason), "Multi-shot is refused without a ranged weapon in hand");
                Check(weaponReason != null && weaponReason.ToLower().Contains("ranged"), $"the refusal names the weapon requirement ({weaponReason})");
                float manaBeforeWeaponRefusal = mana.CurrentMana;
                Check(!caster.TryCastSlot(0), "an unweapon-appropriate cast is refused");
                Check(Mathf.Abs(mana.CurrentMana - manaBeforeWeaponRefusal) < 0.001f, "a weapon-refused cast spends no mana");
                Check(!caster.TryCastSlot(1), "Piercing Shot is refused without a ranged weapon too");
                Check(loadout.SwitchToSlot(0), "archer switches back to the bow");
                Check(ranged.enabled, "ranged combat is live again for the bow");

                // ---- mana regen
                Check(mana.TrySpend(40), "mana spend accepted for the regen measurement");
                float afterSpend = mana.CurrentMana;
                Check(mana.RegenDelayRemaining > 0f, "spending mana pauses regeneration");
                yield return WaitSeconds(classData.manaRegenDelay + 1.5f);
                Check(mana.CurrentMana > afterSpend + 0.5f, $"mana regenerates after its delay ({afterSpend:F0} -> {mana.CurrentMana:F0})");
                Check(mana.CurrentMana <= mana.MaxMana + 0.01f, $"mana regen does not overshoot ({mana.CurrentMana:F0}/{mana.MaxMana})");

                string archerPassives = caster.Stats != null ? caster.Stats.Describe() : "none";
                Debug.Log($"[Emberdelve][Smoke:{Mode}] archer skill diag: kit={caster.KitValid} mana={mana.CurrentMana:F0}/{mana.MaxMana} mag={ranged.Magazine}/{ranged.Reserve} walk={walk:F2} sprint={sprint:F2} passives={archerPassives}");
            }

            private static string SkillName(SkillData skill)
            {
                return skill != null ? skill.displayName + " [" + skill.skillId + "]" : "none";
            }

            private int CountProjectileVisuals()
            {
                ProjectileVisual[] visuals = FindObjectsByType<ProjectileVisual>(FindObjectsSortMode.None);
                return visuals != null ? visuals.Length : 0;
            }

            private void AimAtPoint(Vector3 point)
            {
                Vector3 direction = point - _rig.AimOrigin;
                if (direction.sqrMagnitude < 0.0001f) direction = _rig.transform.forward;
                _rig.transform.rotation = Quaternion.LookRotation(direction);
                Physics.SyncTransforms();
            }

            /// <summary>Places an enemy at a chosen distance ahead of, and beside, the player.</summary>
            private void PlaceEnemyAhead(EnemyAgent enemy, float depth, float lateral)
            {
                Vector3 forward = FlatForward(_rig.transform);
                Vector3 right = Vector3.Cross(Vector3.up, forward);
                if (right.sqrMagnitude < 0.0001f) right = Vector3.right;
                right.Normalize();

                Vector3 position = _rig.transform.position + forward * depth + right * lateral;
                position.y = 0f;
                enemy.transform.position = position;

                // Face the target at the player, so its collider footprint is predictable from the player's
                // point of view instead of whatever direction it happened to be wandering when it was moved.
                Vector3 face = _rig.transform.position - position;
                face.y = 0f;
                if (face.sqrMagnitude > 0.0001f) enemy.transform.rotation = Quaternion.LookRotation(face);

                Physics.SyncTransforms();
            }

            /// <summary>Re-seats the player at the level's spawn cell, facing a direction with clear walkable ground.</summary>
            private bool PlacePlayerAtSpawnFacing(int clearCells)
            {
                if (_rig == null || _layout == null || _grid == null) return false;

                Vector2Int start = _layout.playerStartCell;
                Vector2Int[] candidates =
                {
                    new Vector2Int(0, 1), new Vector2Int(1, 0), new Vector2Int(-1, 0), new Vector2Int(0, -1)
                };

                for (int i = 0; i < candidates.Length; i++)
                {
                    bool clear = true;
                    for (int step = 1; step <= clearCells; step++)
                    {
                        Vector2Int cell = start + candidates[i] * step;
                        if (!_grid.IsWalkable(cell.x, cell.y)) { clear = false; break; }
                    }

                    if (!clear) continue;

                    _rig.transform.position = _layout.CellCenterWorld(start) + Vector3.up * 0.15f;
                    _rig.transform.rotation = Quaternion.LookRotation(new Vector3(candidates[i].x, 0f, candidates[i].y));
                    Physics.SyncTransforms();
                    return true;
                }

                return false;
            }

            /// <summary>
            /// Extra clearance a lane search demands beyond the lane the test asked for, so a candidate that
            /// only misses a chest by centimetres is not chosen. It only decides which lane is picked; the
            /// assertion a flow makes afterwards still uses the lane the skill actually needs.
            /// </summary>
            private const float LaneClearanceMargin = 0.35f;

            /// <summary>How far behind the standing cell a lane probe starts, so geometry sitting in the
            /// player's own cell is seen: a raycast that begins inside a box reports nothing.</summary>
            private const float LaneProbeBackOff = 0.75f;

            /// <summary>
            /// Seats the player in the nearest walkable cell / cardinal orientation the level really offers
            /// for a lane of the requested size, decided BEFORE any target is placed. Two bounds are applied:
            /// the nav cells the lane rectangle touches must all be walkable - grown by a full wall thickness,
            /// because a wall box is centred on the boundary of a blocked cell and sticks half its thickness
            /// into the walkable one - and the lane is then proven with the same multi-height probe
            /// CorridorIsClear already uses, after the bookkeeping the other corridors already do
            /// (ClearEnemiesNear parks the enemies out of it, ClearCorridorAhead moves the loot pickups, both
            /// undone by ParkEnemyFar's parking record / RestoreClearedCorridor). Level geometry is never
            /// moved, filtered, shrunk or ignored: a chest, prop or wall in the lane fails that candidate and
            /// the search moves on, and a level with no lane this size returns false so the caller keeps its
            /// own precondition failure visible.
            /// </summary>
            private bool PlacePlayerInClearLane(float laneLength, float laneHalfWidth, string reason,
                out Vector2Int chosenCell, out Vector2Int chosenFacing)
            {
                chosenCell = _layout != null ? _layout.playerStartCell : Vector2Int.zero;
                chosenFacing = Vector2Int.zero;

                if (_rig == null || _layout == null || _grid == null) return false;

                int cell = Mathf.Max(1, _layout.CellSize);
                float wallInset = Mathf.Max(0.05f, _layout.wallThickness);
                Vector2Int reference = _layout.CellFromWorld(_rig.transform.position);

                Vector2Int[] directions =
                {
                    new Vector2Int(0, 1), new Vector2Int(1, 0), new Vector2Int(-1, 0), new Vector2Int(0, -1)
                };

                // Nearest first, so the fixture stays in the room it was already standing in.
                List<Vector2Int> cells = new List<Vector2Int>();
                for (int y = 0; y < _grid.height; y++)
                {
                    for (int x = 0; x < _grid.width; x++)
                    {
                        if (_grid.IsWalkable(x, y)) cells.Add(new Vector2Int(x, y));
                    }
                }

                cells.Sort((a, b) =>
                {
                    int da = (a.x - reference.x) * (a.x - reference.x) + (a.y - reference.y) * (a.y - reference.y);
                    int db = (b.x - reference.x) * (b.x - reference.x) + (b.y - reference.y) * (b.y - reference.y);
                    if (da != db) return da.CompareTo(db);
                    if (a.x != b.x) return a.x.CompareTo(b.x);
                    return a.y.CompareTo(b.y);
                });

                // The lane is a rectangle starting at the standing cell's centre: laneLength long ahead,
                // laneHalfWidth to either side. The player stands at a cell centre, not a cell edge, so the
                // grown rectangle reaches half a cell further than its own length in cell counts.
                int depthCells = Mathf.FloorToInt((laneLength + LaneClearanceMargin + wallInset) / cell + 0.5f);
                int lateralCells = Mathf.FloorToInt((laneHalfWidth + LaneClearanceMargin + wallInset) / cell + 0.5f);

                for (int c = 0; c < cells.Count; c++)
                {
                    Vector2Int candidate = cells[c];

                    for (int d = 0; d < directions.Length; d++)
                    {
                        Vector2Int facing = directions[d];
                        Vector2Int side = new Vector2Int(facing.y, -facing.x); // Cross(up, forward) in cell space

                        bool boundsOk = true;
                        for (int j = 0; j <= depthCells && boundsOk; j++)
                        {
                            for (int k = -lateralCells; k <= lateralCells; k++)
                            {
                                if (_grid.IsWalkable(candidate.x + facing.x * j + side.x * k,
                                                      candidate.y + facing.y * j + side.y * k)) continue;
                                boundsOk = false;
                            }
                        }
                        if (!boundsOk) continue;

                        Vector3 anchor = _layout.CellCenterWorld(candidate) + Vector3.up * 0.15f;
                        Vector3 forward = new Vector3(facing.x, 0f, facing.y);

                        // Seat the player before probing, so the probe sees the configuration the test will
                        // actually run in, and refuse a cell something already occupies.
                        _rig.transform.position = anchor;
                        _rig.transform.rotation = Quaternion.LookRotation(forward);
                        Physics.SyncTransforms();

                        if (!StandingSpotIsFree(anchor)) continue;

                        ClearEnemiesNear(anchor, laneLength + laneHalfWidth + 1f, reason);
                        ClearCorridorAhead(anchor, forward, laneLength + LaneClearanceMargin,
                            laneHalfWidth + LaneClearanceMargin, reason);

                        bool laneClear = CorridorIsClear(anchor - forward * LaneProbeBackOff, forward,
                            laneLength + LaneClearanceMargin + LaneProbeBackOff,
                            laneHalfWidth + LaneClearanceMargin, reason);
                        if (!laneClear) continue;

                        chosenCell = candidate;
                        chosenFacing = facing;
                        Debug.Log($"[Emberdelve][Smoke:{Mode}] {reason} lane chosen at cell {candidate} facing {facing} " +
                                  $"(anchor {anchor}, {laneLength:F2}m long, {laneHalfWidth * 2f:F2}m wide, " +
                                  $"proven with {LaneClearanceMargin:F2}m extra clearance and {LaneProbeBackOff:F2}m back-off)");
                        return true;
                    }
                }

                // No lane of that size exists: put the player back at the spawn cell the way the other tests
                // do and let the caller report its own precondition failure rather than hiding it.
                PlacePlayerAtSpawnFacing(1);
                Debug.Log($"[Emberdelve][Smoke:{Mode}] no walkable cell offers a clear {laneLength:F2}m x {laneHalfWidth * 2f:F2}m lane for {reason}");
                return false;
            }

            /// <summary>True when nothing solid occupies the cell the player would be seated in. A chest or
            /// prop standing on walkable ground is still an obstacle, and a lane probe that starts inside it
            /// cannot report it, so the standing cell is checked as its own volume.</summary>
            private bool StandingSpotIsFree(Vector3 anchor)
            {
                if (_rig == null) return false;

                Collider[] overlaps = Physics.OverlapSphere(anchor + Vector3.up * 0.9f, 0.5f, ~0, QueryTriggerInteraction.Ignore);
                if (overlaps == null) return true;

                for (int i = 0; i < overlaps.Length; i++)
                {
                    Collider collider = overlaps[i];
                    if (collider == null) continue;
                    if (collider.transform == _rig.transform || collider.transform.IsChildOf(_rig.transform)) continue;

                    Debug.Log($"[Emberdelve][Smoke:{Mode}] standing spot at {anchor} is occupied by '{collider.name}' of '{collider.transform.root.name}' layer={collider.gameObject.layer}");
                    return false;
                }

                return true;
            }

            /// <summary>Finds the walkable cell nearest the player that has a blocked neighbour, for the Dash collision test.</summary>
            private bool TryFindWalkableCellFacingAWall(out Vector2Int walkableCell, out Vector2Int blockedDirection)
            {
                walkableCell = Vector2Int.zero;
                blockedDirection = Vector2Int.zero;

                if (_layout == null || _grid == null) return false;

                Vector2Int reference = _layout.CellFromWorld(_rig != null ? _rig.transform.position : Vector3.zero);
                float best = float.MaxValue;

                Vector2Int[] directions =
                {
                    new Vector2Int(1, 0), new Vector2Int(-1, 0), new Vector2Int(0, 1), new Vector2Int(0, -1)
                };

                for (int y = 0; y < _grid.height; y++)
                {
                    for (int x = 0; x < _grid.width; x++)
                    {
                        if (!_grid.IsWalkable(x, y)) continue;

                        float distance = (x - reference.x) * (x - reference.x) + (y - reference.y) * (y - reference.y);
                        if (distance >= best) continue;

                        for (int d = 0; d < directions.Length; d++)
                        {
                            if (_grid.IsWalkable(x + directions[d].x, y + directions[d].y)) continue;

                            best = distance;
                            walkableCell = new Vector2Int(x, y);
                            blockedDirection = directions[d];
                            break;
                        }
                    }
                }

                return blockedDirection != Vector2Int.zero;
            }

            /// <summary>The walkable cell furthest from a reference cell, used to park test enemies.</summary>
            private void ReportObstaclesAhead(string label)
            {
                if (_rig == null) return;

                Vector3 forward = FlatForward(_rig.transform);
                Vector3 right = Vector3.Cross(Vector3.up, forward);
                if (right.sqrMagnitude < 0.0001f) right = Vector3.right;
                right.Normalize();

                Debug.Log($"[Emberdelve][Smoke:{Mode}] {label} obstacle scan from {_rig.transform.position} facing {forward}");

                for (float height = 0.3f; height <= 1.7f; height += 0.35f)
                {
                    Vector3 origin = _rig.transform.position + Vector3.up * height;
                    for (int side = -1; side <= 1; side++)
                    {
                        Vector3 rayOrigin = origin + right * (side * 0.55f);
                        RaycastHit hit;
                        if (!Physics.Raycast(rayOrigin, forward, out hit, 6f, ~0, QueryTriggerInteraction.Ignore)) continue;

                        Debug.Log($"[Emberdelve][Smoke:{Mode}] {label} hit h={height:F2} side={side} d={hit.distance:F2} collider='{hit.collider.name}' of '{hit.collider.transform.root.name}' layer={hit.collider.gameObject.layer} pos={hit.point}");
                    }
                }
            }

            /// <summary>
            /// Moves loot pickups out of the strip a movement or projectile test is about to use. Harness
            /// bookkeeping only - the pickups still exist, they are simply not lying in the test corridor.
            /// </summary>
            private void ClearCorridorAhead(Vector3 anchor, Vector3 forward, float length, float halfWidth, string reason)
            {
                if (_layout == null) return;

                LootPickup[] pickups = FindObjectsByType<LootPickup>(FindObjectsSortMode.None);
                if (pickups == null) return;

                Vector2Int far = FindFarWalkableCell(_layout.CellFromWorld(anchor));
                int moved = 0;

                for (int i = 0; i < pickups.Length; i++)
                {
                    LootPickup pickup = pickups[i];
                    if (pickup == null) continue;

                    Vector3 original = pickup.transform.position;
                    Vector3 offset = original - anchor;
                    float depth = Vector3.Dot(offset, forward);
                    if (depth < -0.6f || depth > length + 0.6f) continue;

                    Vector3 lateral = offset - forward * depth;
                    if (lateral.magnitude > halfWidth + 0.7f) continue;

                    pickup.gameObject.SetActive(false);
                    pickup.transform.position = _layout.CellCenterWorld(far) + Vector3.up * 0.45f;
                    // LootPickup.Update snaps the pickup back to its home position every frame, so the whole
                    // GameObject has to come out of the scene: that takes its colliders with it.
                    pickup.enabled = false;
                    Physics.SyncTransforms();
                    _clearedLoot.Add(new KeyValuePair<LootPickup, Vector3>(pickup, original));
                    Debug.Log($"[Emberdelve][Smoke:{Mode}] moved loot '{pickup.name}' out of {reason} (was {depth:F1}m ahead, {lateral.magnitude:F1}m to the side)");
                    moved++;
                }

                if (moved > 0) Debug.Log($"[Emberdelve][Smoke:{Mode}] cleared {moved} loot pickup(s) from {reason}");
            }

            /// <summary>
            /// True when nothing solid stands in a strip of ground, checked at several heights so a low prop
            /// cannot be missed. Used to prove a test's precondition before it asserts what movement did.
            /// </summary>
            /// <summary>Puts loot pickups cleared by ClearCorridorAhead back where the level put them.</summary>
            private void RestoreClearedCorridor()
            {
                if (_clearedLoot.Count == 0) return;

                int restored = 0;
                for (int i = 0; i < _clearedLoot.Count; i++)
                {
                    LootPickup pickup = _clearedLoot[i].Key;
                    if (pickup == null) continue;

                    pickup.transform.position = _clearedLoot[i].Value;
                    pickup.gameObject.SetActive(true);
                    pickup.enabled = true;
                    restored++;
                }

                _clearedLoot.Clear();
                Physics.SyncTransforms();
                Debug.Log($"[Emberdelve][Smoke:{Mode}] restored {restored} loot pickup(s) to their level positions");
            }

            private bool CorridorIsClear(Vector3 anchor, Vector3 forward, float length, float halfWidth, string reason)
            {
                bool clear = true;

                for (float height = 0.3f; height <= 1.7f; height += 0.35f)
                {
                    for (int side = -2; side <= 2; side++)
                    {
                        Vector3 right = Vector3.Cross(Vector3.up, forward);
                        if (right.sqrMagnitude < 0.0001f) right = Vector3.right;
                        right.Normalize();

                        Vector3 origin = anchor + Vector3.up * height + right * (side * halfWidth * 0.5f);
                        RaycastHit hit;
                        if (!Physics.Raycast(origin, forward, out hit, length, ~0, QueryTriggerInteraction.Ignore)) continue;
                        if (hit.collider is CharacterController) continue;

                        clear = false;
                        Debug.Log($"[Emberdelve][Smoke:{Mode}] {reason} is not clear: h={height:F2} side={side} d={hit.distance:F2} collider='{hit.collider.name}' of '{hit.collider.transform.root.name}' layer={hit.collider.gameObject.layer}");
                    }
                }

                return clear;
            }

            private Vector2Int FindFarWalkableCell(Vector2Int reference)
            {
                Vector2Int best = reference;
                float bestScore = -1f;

                if (_grid == null || _layout == null) return best;

                for (int y = 0; y < _grid.height; y++)
                {
                    for (int x = 0; x < _grid.width; x++)
                    {
                        if (!_grid.IsWalkable(x, y)) continue;

                        int dx = x - reference.x;
                        int dy = y - reference.y;
                        float score = dx * dx + dy * dy;
                        if (score > bestScore)
                        {
                            bestScore = score;
                            best = new Vector2Int(x, y);
                        }
                    }
                }

                return best;
            }

            /// <summary>
            /// Moves an enemy out of the area a skill test is about to measure, and leaves it frozen. Harness
            /// bookkeeping only: the enemy is still a live combatant, it is simply not standing in the way.
            /// </summary>
            private void ParkEnemyFar(EnemyAgent enemy, string reason)
            {
                if (enemy == null || _layout == null) return;

                enemy.enabled = false;
                Vector2Int far = FindFarWalkableCell(_layout.CellFromWorld(_rig != null ? _rig.transform.position : Vector3.zero));
                enemy.transform.position = _layout.CellCenterWorld(far) + Vector3.up * 0.1f;
                Physics.SyncTransforms();
                Debug.Log($"[Emberdelve][Smoke:{Mode}] parked enemy #{enemy.CombatId} at cell {far}: {reason}");
            }

            private void ClearEnemiesNear(Vector3 anchor, float radius, string reason)
            {
                EnemyAgent[] all = FindObjectsByType<EnemyAgent>(FindObjectsSortMode.None);
                int parked = 0;
                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i] == null) continue;
                    if (Vector3.Distance(all[i].transform.position, anchor) > radius) continue;

                    // Corpses count: a slain agent's colliders stay in the level, and a harness-placed one is
                    // standing exactly where a corridor test now wants to move.
                    ParkEnemyFar(all[i], reason + (all[i].IsDead ? " (corpse)" : string.Empty));
                    parked++;
                }

                if (parked > 0) Debug.Log($"[Emberdelve][Smoke:{Mode}] cleared {parked} enemy agent(s) from {reason}");
            }

            private EnemyAgent PickAliveEnemyExcept(params EnemyAgent[] skip)            {
                EnemyAgent[] all = FindObjectsByType<EnemyAgent>(FindObjectsSortMode.None);
                EnemyAgent best = null;
                float bestDistance = float.MaxValue;
                Vector3 reference = _rig != null ? _rig.transform.position : transform.position;

                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i] == null || all[i].IsDead) continue;

                    bool excluded = false;
                    for (int s = 0; s < skip.Length; s++)
                    {
                        if (skip[s] == all[i]) { excluded = true; break; }
                    }
                    if (excluded) continue;

                    float distance = Vector3.Distance(all[i].transform.position, reference);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = all[i];
                    }
                }

                return best;
            }

            // ---------------------------------------------------------------- helpers

            private LootPickup _sceneLoot;
            private readonly List<KeyValuePair<LootPickup, Vector3>> _clearedLoot = new List<KeyValuePair<LootPickup, Vector3>>();

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

            /// <summary>
            /// Places an enemy so the measurement a melee swing actually makes is satisfied: the distance
            /// from the point the swing scans from (the player's eye) to the enemy CombatCenter, along the
            /// direction towards the requested cell. Root-to-root placement is not good enough because the
            /// enemy's CombatCenter sits above its root and the scan starts at eye height.
            /// Returns the achieved CombatCenter distance.
            /// </summary>
            private static float PlaceCombatCenterInRange(EnemyAgent enemy, Vector3 scanOrigin, Vector3 anchor, Vector3 through, float desired)
            {
                Vector3 outward = through - anchor;
                outward.y = 0f;

                if (outward.sqrMagnitude < 0.01f)
                {
                    outward = anchor - through;
                    outward.y = 0f;
                }

                if (outward.sqrMagnitude < 0.01f) outward = Vector3.forward;
                outward.Normalize();

                float vertical = enemy.CombatCenter.y - scanOrigin.y;
                float horizontal = Mathf.Max(0.4f, desired);
                float squared = desired * desired - vertical * vertical;
                if (squared > 0.04f) horizontal = Mathf.Sqrt(squared);

                Vector3 placed = enemy.transform.position;
                placed.x = anchor.x + outward.x * horizontal;
                placed.z = anchor.z + outward.z * horizontal;
                enemy.transform.position = placed;
                Physics.SyncTransforms();

                return (enemy.CombatCenter - scanOrigin).magnitude;
            }

            private void ClaimMelee(AttackIntent intent) => _claims.Add(new SequenceClaim(intent.InstigatorCombatId, intent.Sequence, "melee"));
            private void ClaimRanged(AttackIntent intent) => _claims.Add(new SequenceClaim(intent.InstigatorCombatId, intent.Sequence, "ranged"));

            /// <summary>Records the sequence each real attack component claims for its combat entity.</summary>
            private void WatchComponentSequences()
            {
                MeleeCombat melee = _rig != null ? _rig.GetComponent<MeleeCombat>() : null;
                RangedCombat ranged = _rig != null ? _rig.GetComponent<RangedCombat>() : null;
                if (melee != null) melee.AttackStarted += ClaimMelee;
                if (ranged != null) ranged.AttackStarted += ClaimRanged;
            }

            private void UnwatchComponentSequences()
            {
                MeleeCombat melee = _rig != null ? _rig.GetComponent<MeleeCombat>() : null;
                RangedCombat ranged = _rig != null ? _rig.GetComponent<RangedCombat>() : null;
                if (melee != null) melee.AttackStarted -= ClaimMelee;
                if (ranged != null) ranged.AttackStarted -= ClaimRanged;
            }

            /// <summary>
            /// Concise, failure-only: the sequence an intent claimed versus the sequence the authority has
            /// now consumed for that entity, plus its accepted/rejected totals.
            /// </summary>
            private void SequenceDiag(string label, int combatId, int usedSequence)
            {
                Debug.Log($"[Emberdelve][Smoke:{Mode}] SEQ DIAG {label}: entity #{combatId} used={usedSequence} authorityConsumed={(_localAuthority != null ? _localAuthority.LastSequenceFor(combatId) : -1)} accepted={(_localAuthority != null ? _localAuthority.AcceptedCount : -1)} rejected={(_localAuthority != null ? _localAuthority.RejectedCount : -1)}");
            }

            /// <summary>True when every claim for the same combat id is strictly greater than the previous one.</summary>
            private bool ClaimsStrictlyIncrease(out string trail)
            {
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                bool ok = _claims.Count >= 2;

                for (int i = 0; i < _claims.Count; i++)
                {
                    if (i > 0 && _claims[i].CombatId == _claims[i - 1].CombatId &&
                        _claims[i].Sequence <= _claims[i - 1].Sequence)
                    {
                        ok = false;
                    }

                    sb.Append(_claims[i]);
                    if (i < _claims.Count - 1) sb.Append(" -> ");
                }

                trail = sb.ToString();
                return ok;
            }

            /// <summary>
            /// One concise line covering everything a mana regen tick depends on, so a pool that did not move
            /// can be attributed to a named precondition (dead caster, suspended regen, disabled pool, paused
            /// clock, a second pool, a live attacker still in reach) rather than guessed at. Read-only.
            /// </summary>
            private void ManaRegenDiag(string stage, ManaComponent mana, float afterSpend, float scaledElapsed, float unscaledElapsed)
            {
                if (mana == null || _rig == null) return;

                int liveEnemies = 0;
                float nearestLive = -1f;
                EnemyAgent[] agents = FindObjectsByType<EnemyAgent>(FindObjectsSortMode.None);
                for (int i = 0; i < agents.Length; i++)
                {
                    if (agents[i] == null || agents[i].IsDead || !agents[i].enabled) continue;
                    liveEnemies++;
                    float distance = Vector3.Distance(agents[i].transform.position, _rig.transform.position);
                    if (nearestLive < 0f || distance < nearestLive) nearestLive = distance;
                }

                SkillCaster caster = _rig.Skills;
                ManaComponent casterPool = caster != null ? caster.GetComponent<ManaComponent>() : null;

                Debug.Log($"[Emberdelve][Smoke:{Mode}] mana regen diag {stage}: mana={mana.CurrentMana:F2}/{mana.MaxMana} spentForMeasurement={afterSpend:F0} poolInstance={mana.GetInstanceID()} rigPool={(_rig.Mana != null ? _rig.Mana.GetInstanceID() : -1)} casterPool={(casterPool != null ? casterPool.GetInstanceID() : -1)} poolsOnRig={_rig.GetComponents<ManaComponent>().Length} componentEnabled={mana.enabled} gameObjectActive={mana.gameObject.activeInHierarchy} regenAllowed={mana.RegenerationAllowed} regenRate={mana.RegenPerSecond:F1} regenDelayRemaining={mana.RegenDelayRemaining:F2} isFull={mana.IsFull} hp={_rig.CurrentHealth:F0}/{_rig.MaxHealth} dead={_rig.IsDead} runState={(_director != null ? _director.State.ToString() : "none")} timeScale={Time.timeScale:F2} scaledElapsed={scaledElapsed:F2} unscaledElapsed={unscaledElapsed:F2} liveEnabledEnemies={liveEnemies} nearestLiveEnemy={nearestLive:F1}m");
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

            /// <summary>
            /// Waits (real time) until the authority would accept another attack from this entity with this
            /// weapon. This is how fixtures drive legitimate attacks now that cooldowns run on the authority
            /// own clock and an intent timestamp can no longer be used to skip them.
            /// </summary>
            private IEnumerator WaitForWeaponReady(int combatId, WeaponData weapon)
            {
                if (_localAuthority == null || weapon == null) yield break;
                yield return WaitUntil(() => _localAuthority.WeaponCooldownRemaining(combatId, weapon) <= 0f,
                    Mathf.Max(0.1f, weapon.cooldown) * 2f + 1f);
                if (_localAuthority.WeaponCooldownRemaining(combatId, weapon) > 0f) Check(false,
                    $"the authority weapon cooldown for #{combatId} cleared in real time");
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
