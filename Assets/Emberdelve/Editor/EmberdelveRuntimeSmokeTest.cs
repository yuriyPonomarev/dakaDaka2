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

                // Record what the real components claim before anything attacks, so the ordering of the
                // shared allocator can be asserted from observed sequences.
                _claims.Clear();
                WatchComponentSequences();

                // Runs first on purpose. The synthetic authority tests below deliberately use future
                // timestamps, and the authority records the last accepted attack time per combat id, so a
                // real-time crossbow shot fired after them would be rejected as faster than the cooldown.
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
                    Check(enemy != null, "a live enemy is available for the authority validation tests");
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
                // Synthetic intents take their sequences from the same per-combat-id allocator the combat
                // components use, so they are shaped exactly like real ones.
                float baseTime = Time.time;

                // Validation must refuse a hit claimed 40m away with a 2m sword.
                int tooFarSequence = CombatRegistry.NextSequence(_rig.CombatId);
                int consumedBeforeTooFar = _localAuthority != null ? _localAuthority.LastSequenceFor(_rig.CombatId) : -1;
                AttackIntent tooFar = new AttackIntent(_rig.CombatId, EntityKind.LocalPlayer, weapon, 1f,
                    _rig.CombatCenter, Vector3.forward, weapon.reach, tooFarSequence, baseTime);
                bool farAccepted = _authority.SubmitAttack(tooFar,
                    new HitCandidate(enemy.CombatId, _rig.CombatCenter + Vector3.forward * 40f, false));
                Check(!farAccepted, "authority rejects a hit claimed beyond weapon reach");
                if (farAccepted) SequenceDiag("tooFar", _rig.CombatId, tooFarSequence);

                // The replay test reuses the exact sequence the authority has now consumed. tooFar passed
                // the sequence rule - which is what makes it the last consumed one - and only then failed
                // on range. Asserted before the replay so the reuse is proven, not assumed.
                Check(_localAuthority != null && tooFarSequence > consumedBeforeTooFar &&
                      _localAuthority.LastSequenceFor(_rig.CombatId) == tooFarSequence,
                      $"authority consumed {consumedBeforeTooFar} -> {tooFarSequence} for #{_rig.CombatId}, so the replay reuses a live sequence");

                AttackIntent replay = new AttackIntent(_rig.CombatId, EntityKind.LocalPlayer, weapon, 1f,
                    _rig.CombatCenter, Vector3.forward, weapon.reach, tooFarSequence, baseTime + 10f);
                bool replayAccepted = _authority.SubmitAttack(replay, new HitCandidate(enemy.CombatId, enemy.CombatCenter, false));
                Check(!replayAccepted, "authority rejects a repeated command sequence");
                if (replayAccepted) SequenceDiag("replay", _rig.CombatId, tooFarSequence);

                // Faster than the weapon cooldown must be rejected, and on a fresh later sequence so the
                // rejection can only come from the cooldown rule.
                int spamSequence = CombatRegistry.NextSequence(_rig.CombatId);
                Check(spamSequence > tooFarSequence,
                    $"the cooldown intent claims a fresh sequence ({tooFarSequence} -> {spamSequence})");
                AttackIntent spam = new AttackIntent(_rig.CombatId, EntityKind.LocalPlayer, weapon, 1f,
                    _rig.CombatCenter, Vector3.forward, weapon.reach, spamSequence, baseTime + 0.05f);
                bool spamAccepted = _authority.SubmitAttack(spam, new HitCandidate(enemy.CombatId, enemy.CombatCenter, false));
                Check(!spamAccepted, "authority rejects attacks faster than the weapon cooldown");
                if (spamAccepted) SequenceDiag("spam", _rig.CombatId, spamSequence);

                Check(_localAuthority != null && _localAuthority.RejectedCount >= rejectedBefore + 3 &&
                      _localAuthority.AcceptedCount == acceptedBefore,
                    $"rejections recorded (rejected {(_localAuthority != null ? _localAuthority.RejectedCount : 0)}, accepted {(_localAuthority != null ? _localAuthority.AcceptedCount : 0)})");

                // Now attack properly until the enemy drops. Every one of these is meant to be valid, so
                // each must claim a sequence strictly greater than the one the authority last consumed.
                float healthBefore = enemy.CurrentHealth;
                int accepted = 0;
                bool everySequenceFresh = true;
                for (int i = 0; i < 16 && !enemy.IsDead; i++)
                {
                    Vector3 origin = _rig.CombatCenter;
                    Vector3 direction = (enemy.CombatCenter - origin).normalized;
                    int consumedBefore = _localAuthority != null ? _localAuthority.LastSequenceFor(_rig.CombatId) : -1;
                    int freshSequence = CombatRegistry.NextSequence(_rig.CombatId);
                    if (freshSequence <= consumedBefore) everySequenceFresh = false;

                    AttackIntent intent = new AttackIntent(_rig.CombatId, EntityKind.LocalPlayer, weapon, 1f,
                        origin, direction, weapon.reach + weapon.hitSphereRadius, freshSequence, baseTime + 5f + i * 1.5f);

                    if (_authority.SubmitAttack(intent, new HitCandidate(enemy.CombatId, enemy.CombatCenter, false))) accepted++;
                    else SequenceDiag($"validAttack[{i}] consumedBefore={consumedBefore}", _rig.CombatId, freshSequence);
                    yield return WaitFrames(2);
                }

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
                float stamp = Time.time + 30f;
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

                float before = _rig.CurrentHealth;
                _capturedValid = false;
                int frontSequence = CombatRegistry.NextSequence(enemy.CombatId);
                bool frontAccepted = _authority.SubmitAttack(
                    new AttackIntent(enemy.CombatId, EntityKind.Enemy, claw, 1f, origin, toSource, maxRange, frontSequence, stamp),
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

                before = _rig.CurrentHealth;
                _capturedValid = false;
                int behindSequence = CombatRegistry.NextSequence(enemy.CombatId);
                bool behindAccepted = _authority.SubmitAttack(
                    new AttackIntent(enemy.CombatId, EntityKind.Enemy, claw, 1f, origin, toSource, maxRange, behindSequence, stamp + 2f),
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

                before = _rig.CurrentHealth;
                _capturedValid = false;
                int sideSequence = CombatRegistry.NextSequence(enemy.CombatId);
                bool sideAccepted = _authority.SubmitAttack(
                    new AttackIntent(enemy.CombatId, EntityKind.Enemy, claw, 1f, origin, toSource, maxRange, sideSequence, stamp + 4f),
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
                int sequenceRejections = 0;
                float baseTime = Time.time;

                for (int i = 0; i < 24 && !_rig.IsDead; i++)
                {
                    enemy.transform.position = _rig.transform.position + new Vector3(0f, 0f, -1.1f);
                    Vector3 origin = enemy.CombatCenter;
                    Vector3 direction = (_rig.CombatCenter - origin).normalized;

                    int consumedBefore = _localAuthority != null ? _localAuthority.LastSequenceFor(enemy.CombatId) : -1;
                    int freshSequence = CombatRegistry.NextSequence(enemy.CombatId);
                    if (freshSequence <= consumedBefore) sequenceRejections++;

                    AttackIntent intent = new AttackIntent(enemy.CombatId, EntityKind.Enemy, claw, 1f,
                        origin, direction, claw.reach + claw.hitSphereRadius, freshSequence, baseTime + i * 1.2f);

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
                    Check(_rig.Mana.MaxMana == (_rig.Class != null ? _rig.Class.EffectiveMaxMana : _rig.Mana.MaxMana),
                        $"fresh run reconfigured the mana pool ({_rig.Mana.CurrentMana:F0}/{_rig.Mana.MaxMana})");
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

                Check(mana.MaxMana == classData.EffectiveMaxMana,
                    $"mana pool comes from the class asset ({mana.MaxMana} vs {classData.EffectiveMaxMana})");
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
                Check(Mathf.Abs(outgoingMelee - 1.2f) < 0.03f, $"Ashen Might multiplies melee damage by 1.20 ({outgoingMelee:F2})");
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

                    // The Milestone 1 validation tests deliberately stamp synthetic future timestamps, and the
                    // authority records the last accepted attack per combat id, so a real-time swing fired too
                    // soon after them is correctly refused as faster than the cooldown. Wait for the authority's
                    // own clock to clear instead of pretending the rejection did not happen.
                    float weaponClock = _localAuthority != null ? _localAuthority.WeaponCooldownRemaining(_rig.CombatId, sword) : 0f;
                    if (weaponClock > 0f)
                    {
                        Debug.Log($"[Emberdelve][Smoke:{Mode}] authority weapon clock for #{_rig.CombatId} is {weaponClock:F1}s ahead from the synthetic validation stamps; waiting for it to clear before the passive swing");
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
                yield return WaitSeconds(classData.manaRegenDelay + 1.5f);
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
                Check(Mathf.Abs(walk - classData.moveSpeed * 1.15f) < 0.06f,
                    $"Fleet Footing raises walk speed ({walk:F2} vs class {classData.moveSpeed:F2})");
                Check(Mathf.Abs(sprint - classData.sprintSpeed * 1.15f) < 0.06f,
                    $"Fleet Footing raises sprint speed ({sprint:F2} vs class {classData.sprintSpeed:F2})");
                Check(sprint > walk + 0.1f, $"the resolved speed rule still distinguishes sprint from walk ({sprint:F2} > {walk:F2})");

                // ---- passive 2: Farshot
                float outgoingRanged = _rig.GetOutgoingDamageMultiplier(bow);
                float outgoingMelee = _rig.GetOutgoingDamageMultiplier(loadout != null ? loadout.Secondary : null);
                Check(Mathf.Abs(outgoingRanged - 1.18f) < 0.03f, $"Farshot multiplies ranged damage by 1.18 ({outgoingRanged:F2})");
                Check(Mathf.Abs(outgoingMelee - 1f) < 0.03f, $"Farshot does not touch melee damage ({outgoingMelee:F2})");

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
                Check(PlacePlayerAtSpawnFacing(1), "player re-seated facing a clear corridor for the volley");
                ClearEnemiesNear(_rig.transform.position, 10f, "the volley corridor");

                // The lane the volley actually needs comes from the skill's own spread angle, not from a
                // guess: at depth d the outer arrows sit at d * tan(spread / 2) to either side.
                float volleyDepth = 4.0f;
                float volleyLateral = volleyDepth * Mathf.Tan(Mathf.Max(1f, multi.spreadDegrees) * 0.5f * Mathf.Deg2Rad);
                float volleyLaneHalf = volleyLateral + 0.7f;

                ClearCorridorAhead(_rig.transform.position, FlatForward(_rig.transform), volleyDepth + 1.5f, volleyLaneHalf, "the volley corridor");
                yield return WaitFrames(2);

                bool volleyLaneClear = CorridorIsClear(_rig.transform.position, FlatForward(_rig.transform),
                    volleyDepth + 0.4f, volleyLaneHalf, "the volley corridor");
                Check(volleyLaneClear,
                    $"the volley lane is clear for a {multi.spreadDegrees:F0} degree spread out to {volleyDepth:F1}m (lane half width {volleyLaneHalf:F2}m)");

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
                        float expected = bow.DamageAtRange(Vector3.Distance(pair.Value.SourcePosition, pair.Value.Point))
                                         * multi.damageMultiplier * outgoingRanged
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

                    if (lineA != null && !lineA.IsDead && _authority != null)
                    {
                        Vector3 muzzle = _rig.GetMuzzlePosition();
                        Vector3 toward = lineA.CombatCenter - muzzle;
                        if (toward.sqrMagnitude > 0.0001f)
                        {
                            int forgedSequence = CombatRegistry.NextSequence(_rig.CombatId);
                            SkillIntent forged = new SkillIntent(_rig.CombatId, EntityKind.LocalPlayer, pierce, bow,
                                muzzle, toward.normalized, Mathf.Max(1f, bow.rangedRange), forgedSequence, Time.time,
                                pierce.TargetStatusSpec, CombatRegistry.NextActivationId());

                            int acceptedBeforeForged = _localAuthority != null ? _localAuthority.AcceptedCount : 0;
                            bool forgedAccepted = _authority.SubmitSkill(forged, new HitCandidate(lineA.CombatId, lineA.CombatCenter, false));

                            Check(!forgedAccepted, "the authority refuses a new activation of the same skill while its cooldown runs");
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
                    float expected = bow.DamageAtRange(Vector3.Distance(pierceDamageA.SourcePosition, pierceDamageA.Point))
                                     * pierce.damageMultiplier * outgoingRanged
                                     * (pierceDamageA.Headshot ? DamageResolver.HeadshotMultiplier : 1f);
                    Check(Mathf.Abs(pierceDamageA.Amount - expected) < 0.9f,
                        $"the piercing arrow's damage is weapon falloff x skill multiplier x Farshot ({pierceDamageA.Amount:F1} vs expected {expected:F1})");
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
