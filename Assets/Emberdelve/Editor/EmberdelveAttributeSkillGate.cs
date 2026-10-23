using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Emberdelve.AI;
using Emberdelve.Combat;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.InputLayer;
using Emberdelve.Items;
using Emberdelve.Movement;
using Emberdelve.Player;
using Emberdelve.Skills;
using Emberdelve.Stats;

namespace Emberdelve.EditorTools
{
    /// <summary>
    /// U2A5 gate: the CHA half of Docs/Design/ATTRIBUTES.md - ability effect_scale =
    /// clamp(1 + 0.04 * CHA modifier, 0.6, 1.4) - verified through real casts, not through the helper.
    ///
    /// Lifecycle is the pattern EmberdelveAttributeVitalsGate / EmberdelveAttributeActionGate proved:
    /// [InitializeOnLoad] subscribes once, Run() records the request in SessionState (which survives the
    /// play-mode domain reload), opens a transient unsaved empty scene and asks for play mode.
    /// EnteredPlayMode builds the rigs once with real AddComponent calls, so PlayerRig.Awake,
    /// PlayerMovement.Awake (+CharacterController, minMoveDistance 0), PlayerLook.Awake, HealthComponent,
    /// StatusComponent, SkillCaster.Awake and LocalCombatAuthority.Awake all run the way a scene rig runs
    /// them. Nothing is saved, no scene or asset is written, and no Awake is invoked by hand or reflection;
    /// reflection is used once, read-only, to see which ManaComponent the caster actually holds.
    /// The pass is a coroutine so real frames, real physics and real Update order drive the measurements.
    ///
    /// Dash (warrior): SkillCaster.TryCastSlot -> DashBehavior -> PlayerMovement.TryStartDash ->
    /// CharacterController.Move. The measured dash speed stays exactly the authored dashDistance /
    /// dashDuration at every scale (that is what "scaled together" means, and it is what leaves the
    /// authored trajectory speed and PlayerMovement's 24 m/s cap untouched); the duration derived from
    /// PlayerMovement's own DashProgress is dashDuration * scale; the ground actually covered is
    /// dashDistance * scale; and a real Emv_Level wall still stops the dash. Every dash is measured only
    /// after PlayerMovement stops reporting it - a game-time bound derived from the authored scaled duration
    /// plus a fixed settling margin, with an independent real-time watchdog - never after a fixed frame count.
    ///
    /// Battle Cry (warrior): real radial scan, real authority hits, real statuses on real targets. The self
    /// buff and the target slow scale as DELTAS from neutral 1 (1.25 -> 1 + 0.25 * scale, 0.55 ->
    /// 1 - 0.45 * scale) and their durations as duration * scale, inside the bounds StatusSpec and
    /// StatusComponent already enforce. The authored flat 14 damage in the asset, the 6 m radius, the 4 target
    /// cap, the 32 MP cost and the 14 s cooldown are identical at every scale - and so is the number a victim's
    /// pool actually loses, 14 x the instigator multiplier the authority reads from the warrior (1.12 STR x
    /// 1.2 Ashen Might) = 18.816, because BattleCryBehavior carries the scale into its two statuses and hands
    /// the damage intent none. A buff already running is not retroactively
    /// resized by gear equipped afterwards; the next accepted cast is.
    ///
    /// Multi-shot / Piercing Shot (archer): real magazine, real spread volley, real piercing line, real
    /// authority. Each accepted arrow's damage is weapon falloff x the skill's own multiplier x the
    /// instigator multiplier the authority read from the rig x the cast's CHA scale - each exactly once -
    /// and is neither the unscaled number nor the scale applied twice. Projectile count, spread, range,
    /// ammo, mana cost and cooldown are identical at every scale, and
    /// PlayerRig.GetOutgoingDamageMultiplier does not move at all, so CHA cannot be counted twice.
    ///
    /// Ordinary attacks and an enemy instigator are unchanged at every CHA level. Shared class, skill,
    /// weapon and item definitions are compared as pristine JSON before and after the whole pass.
    ///
    /// Run from the command line, e.g.
    ///   Unity.exe -batchmode -nographics -projectPath . -logFile Logs/u2a5.log
    ///           -executeMethod Emberdelve.EditorTools.EmberdelveAttributeSkillGate.Run
    /// Exit 0 only when every check passed.
    /// </summary>
    [InitializeOnLoad]
    public static class EmberdelveAttributeSkillGate
    {
        private const string Tag = "[Emberdelve][AttributeSkill]";

        private const string RequestKey = "Emberdelve.AttributeSkill.Request";
        private const string PendingKey = "Emberdelve.AttributeSkill.Pending";
        private const string DoneKey = "Emberdelve.AttributeSkill.Done";
        private const string ExitCodeKey = "Emberdelve.AttributeSkill.ExitCode";

        private static double _watchdogStartedAt;

        static EmberdelveAttributeSkillGate()
        {
            EditorApplication.playModeStateChanged += HandlePlayModeChange;
        }

        public static void Run()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Debug.LogError(Tag + " cannot request play mode from inside play mode");
                EditorApplication.Exit(1);
                return;
            }

            SessionState.SetString(RequestKey, string.Empty);
            SessionState.SetBool(PendingKey, false);
            SessionState.SetBool(DoneKey, false);
            SessionState.SetInt(ExitCodeKey, 1);

            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            Debug.Log(Tag + " requesting play mode in a transient empty scene");

            SessionState.SetString(RequestKey, "skills");
            SessionState.SetBool(PendingKey, true);

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
                SessionState.SetString(RequestKey, string.Empty);
                SessionState.SetBool(PendingKey, false);
                SessionState.SetInt(ExitCodeKey, 1);
                Debug.LogError(Tag + " play mode never started - headless play mode is unavailable in this session.");
                EditorApplication.Exit(1);
            }
        }

        private static void HandlePlayModeChange(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.EnteredPlayMode)
            {
                if (string.IsNullOrEmpty(SessionState.GetString(RequestKey, string.Empty))) return;
                if (!SessionState.GetBool(PendingKey, false)) return;

                SessionState.SetBool(PendingKey, false);

                Debug.Log(Tag + " entered play mode, building the rigs through their real Awake");

                GameObject harnessObject = new GameObject("Emberdelve_AttributeSkillHarness");
                harnessObject.AddComponent<Pass>();
            }
            else if (change == PlayModeStateChange.ExitingPlayMode)
            {
                if (string.IsNullOrEmpty(SessionState.GetString(RequestKey, string.Empty))) return;

                if (SessionState.GetBool(DoneKey, false))
                {
                    int exitCode = SessionState.GetInt(ExitCodeKey, 1);
                    SessionState.SetBool(DoneKey, false);
                    SessionState.SetString(RequestKey, string.Empty);
                    Debug.Log(Tag + $" play mode finished, exiting with stored code {exitCode}");
                    EditorApplication.delayCall += () => EditorApplication.Exit(exitCode);
                }
                else
                {
                    Debug.LogError(Tag + " play mode ended before the pass finished its assertions - reporting failure");
                    SessionState.SetInt(ExitCodeKey, 1);
                    SessionState.SetString(RequestKey, string.Empty);
                    EditorApplication.delayCall += () => EditorApplication.Exit(1);
                }
            }
        }
        /// <summary>One coroutine verification pass, running inside real play mode against real rigs.</summary>
        private sealed class Pass : MonoBehaviour
        {
            /// <summary>Numbers the runtime produces exactly: statuses, costs, cooldowns, damage.</summary>
            private const float Exact = 0.011f;

            /// <summary>Dash duration derived from PlayerMovement's own DashProgress.</summary>
            private const float DurationTolerance = 0.012f;

            /// <summary>Dash speed read from PlayerMovement's own DesiredMoveIntent.</summary>
            private const float SpeedTolerance = 0.06f;

            /// <summary>Ground a controller-driven dash actually covers (one frame of overshoot at 20.9 m/s).</summary>
            private const float DistanceTolerance = 0.9f;

            /// <summary>
            /// How much longer than the authored scaled duration (0.22 * scale, i.e. 0.2200..0.3080 s here, all
            /// of them inside PlayerMovement's own [0.05, 2] duration clamp) the pass waits for a dash to
            /// actually end, in GAME seconds. A frame count is not a bound: the headless player loop measured
            /// ~3100-3500 FPS, so the old 600-frame guard stopped sampling ~0.17 s into every dash and the
            /// blocked flow's fixed 40-frame wait asserted 0.013 s into a 0.2552 s dash. No FPS cap, no
            /// timeScale change, no controller.Move call, no fabricated dash state.
            /// </summary>
            private const float DashFinishMargin = 0.35f;

            /// <summary>Independent real-time watchdog on the same wait, so a loop whose game clock stalls
            /// cannot turn a game-time bound into an unbounded coroutine.</summary>
            private const float DashRealtimeWatchdog = 8f;

            /// <summary>Last-resort finiteness guard, far above the ~1100 frames a 0.308 s dash legitimately
            /// spends at the frame rate this run actually measured (it truncates nothing legitimate).</summary>
            private const int DashFinishFrameGuard = 100000;

            /// <summary>
            /// The damage one Battle Cry victim's pool actually loses, fixture arithmetic from the accepted
            /// runtime rather than a query of it: the skill's authored flat 14 x the instigator multiplier
            /// LocalCombatAuthority reads from the warrior (STR 16 -> modifier 3 -> 1 + 0.04*3 = 1.12) x its
            /// Ashen Might passive 1.2 = 18.816, at CHA 8, 10, 18 and 30 alike, because BattleCryBehavior
            /// scales only the two statuses and builds its SkillIntent with the default effect scale 1.
            /// </summary>
            private const float ExpectedCryDamage = 18.816f;

            private const float ArcherSpawnX = 60f;

            /// <summary>Ground depth of an ordinary-attack host: sword reach 2.3 + 0.6 = 2.9, rapier 2.7 + 0.45 = 3.15.</summary>
            private const float MeleeHostDepth = 2.2f;

            /// <summary>Distance from the muzzle to an archer host. The bow's falloff starts at 18 m, so every host below sits in its flat band.</summary>
            private const float ShotDistance = 10f;

            /// <summary>Standoff for the Hollow Servitor: inside its authored 2.2 m attack range, outside a dodge.</summary>
            private const float EnemyStandoff = 1.8f;

            /// <summary>Real seconds left between two attacks from the same instigator so the authority's own weapon clock (cooldown x 0.8 grace) is not the thing that fails a measurement.</summary>
            private const float WeaponAttackSpacing = 1.1f;

            /// <summary>Slack on top of an authored skill cooldown before casting it again, so the authority's clock tolerance (0.05s) is never the reason a hit is refused.</summary>
            private const float AuthorityClockSlack = 0.35f;

            /// <summary>Longest the pass waits for one real swing or one real enemy attack to resolve through the authority.</summary>
            private const float ResolveTimeout = 6f;

            /// <summary>How many Battle Cry casts the pass has already taken, so the authority's cry clock can be spaced honestly.</summary>
            private int _cryCastsTaken;

            /// <summary>Inner bound of the guarded driver (MoveNext calls). Far above the ~1e6 a full pass with
            /// the real cooldown spacing legitimately spends, and the Update watchdog is the outer bound.</summary>
            private const int MaxDriverSteps = 20000000;

            /// <summary>Inner bound of the guarded driver (iterator nesting). The pass nests about five deep.</summary>
            private const int MaxDriverDepth = 64;

            private int _checks;
            private int _failures;
            private float _startedAt;
            private bool _stopping;
            private bool _cleaned;
            private bool _playerDamageHooked;

            private readonly List<GameObject> _objects = new List<GameObject>(32);
            private readonly List<GameObject> _targets = new List<GameObject>(24);
            private readonly List<ItemData> _tempItems = new List<ItemData>(12);
            private ItemCatalog _testCatalog;
            private int _charmSerial;
            private readonly List<DamageInfo> _captured = new List<DamageInfo>(8);
            private readonly List<TestTarget> _subscribed = new List<TestTarget>(8);

            private CharacterClassData _warriorClass;
            private CharacterClassData _archerClass;
            private WeaponData _sword;
            private WeaponData _bow;
            private WeaponData _claw;
            private ItemData _cuirass;
            private EnemyData _servitor;
            private NavGridData _navGrid;
            private GameConfig _config;

            private string _pristineWarrior;
            private string _pristineArcher;
            private string _pristineSword;
            private string _pristineBow;
            private string _pristineDash;
            private string _pristineCry;
            private string _pristineMulti;
            private string _pristinePierce;
            private string _pristineCuirass;

            private LocalCombatAuthority _authority;
            private EnemyAgent _enemy;

            private PlayerRig _warrior;
            private PlayerRig _archer;
            private RangedCombat _archerRanged;
            private Vector3 _warriorSpawn;
            private Vector3 _archerSpawn;
            private PlayerRig _builtRig;

            private float _dashDurationBaseline;
            private float _dashDistanceBaseline;
            private float _dashDurationMeasured;
            private float _dashDistanceMeasured;
            private bool _dashFinished;
            private float _dashLastElapsed;
            private float _dashLastProgress;

            private void Start()
            {
                _startedAt = Time.realtimeSinceStartup;
                StartCoroutine(Run());
            }

            private void Update()
            {
                if (_stopping) return;

                // Raised from 150s when the five pending scenarios landed: the authority keeps its own
                // per-entity, per-skill cooldown clock (LocalCombatAuthority.SkillClockFor), so a fixture that
                // casts Battle Cry / Multi-shot / Piercing Shot again has to let the authored cooldown expire in
                // real time or every hit of the next cast is genuinely refused. Nine cry casts at 14s plus the
                // archer skills at 9s and 11s is real gameplay time, not a stall.
                if (Time.realtimeSinceStartup - _startedAt > 420f)
                {
                    Debug.LogError(Tag + " watchdog: the pass never finished - reporting failure");
                    SessionState.SetInt(ExitCodeKey, 1);
                    SessionState.SetBool(DoneKey, true);
                    _stopping = true;
                    EditorApplication.isPlaying = false;
                }
            }

            /// <summary>
            /// Driver of the pass. C# forbids a yield inside a try that has a catch (CS1626), so the scenario
            /// sequence lives in Scenarios() and this method only traverses it: every MoveNext and every
            /// Current read goes through TryStep/TryCurrent, the only catches in the pass, and the single yield
            /// below sits outside every try-with-catch. Nested iterators (BuildRig, WaitFrames, WaitSeconds,
            /// WaitForCaptured, SpaceForNextCryCast, each scenario) are advanced through that guard on a stack
            /// instead of being handed straight to Unity, so an exception raised anywhere inside a scenario is
            /// recorded as a genuine failure here instead of escaping the gate as an unhandled exception.
            /// Non-enumerator yields (null, a WaitForSeconds, ...) pass through to Unity unchanged.
            /// </summary>
            private IEnumerator Run()
            {
                Stack<IEnumerator> stack = new Stack<IEnumerator>();
                stack.Push(Scenarios());

                System.Exception fault = null;
                int steps = 0;

                while (stack.Count > 0 && fault == null)
                {
                    // Inner bound of the driver itself; the Update() watchdog is the outer bound.
                    if (++steps > MaxDriverSteps)
                    {
                        fault = new System.Exception(Tag + " driver step cap " + MaxDriverSteps +
                                                     " reached - the pass is not finite");
                        break;
                    }

                    IEnumerator current = stack.Peek();

                    bool moved;
                    if (!TryStep(current, out moved, out fault)) break;
                    if (!moved)
                    {
                        DisposeOnce(current);
                        stack.Pop();
                        continue;
                    }

                    object yielded;
                    if (!TryCurrent(current, out yielded, out fault)) break;

                    IEnumerator nested = yielded as IEnumerator;
                    if (nested != null)
                    {
                        if (stack.Count >= MaxDriverDepth)
                        {
                            fault = new System.Exception(Tag + " driver depth cap " + MaxDriverDepth +
                                                         " reached - a scenario yielded its own iterator");
                            break;
                        }

                        stack.Push(nested);
                        continue;
                    }

                    yield return yielded;
                }

                // Finished or faulted: dispose every iterator still open exactly once, so the finally blocks
                // an iterator owns run. A teardown that throws is reported, never swallowed.
                while (stack.Count > 0)
                {
                    DisposeOnce(stack.Pop());
                }

                if (fault != null)
                {
                    _failures++;
                    Debug.LogError(Tag + " gate threw: " + fault);
                }

                Cleanup();

                int exitCode = _failures == 0 ? 0 : 1;
                Debug.Log($"{Tag} {(_failures == 0 ? "PASS" : "FAIL")} checks={_checks} failures={_failures} " +
                          $"authorityAccepted={(_authority != null ? _authority.AcceptedCount : -1)} " +
                          $"authorityRejected={(_authority != null ? _authority.RejectedCount : -1)} exit={exitCode}");

                SessionState.SetInt(ExitCodeKey, exitCode);
                SessionState.SetBool(DoneKey, true);
                _stopping = true;
                EditorApplication.isPlaying = false;
            }

            /// <summary>
            /// The scenario sequence, in the order it has always run: every scenario, every expected value and
            /// every tolerance untouched. It is an iterator with no catch of its own, which is what makes the
            /// yields legal; a throw inside it is caught by the driver above.
            /// </summary>
            private IEnumerator Scenarios()
            {
                LoadAssets();
                CheckAttributeRuleBand();

                BuildAuthority();
                BuildFloors();

                yield return BuildRig("U2A5_Warrior", new Vector3(0f, 0.15f, 0f));
                _warrior = _builtRig;
                _warriorSpawn = _warrior.transform.position;
                _warrior.ApplyClass(_warriorClass, _config);
                yield return WaitFrames(3);

                CheckWarriorKit();
                yield return RunDashFlow();
                yield return RunBattleCryFlow();
                yield return RunBattleCrySnapshotFlow();
                yield return RunWarriorOrdinaryAttackUnchanged();
                yield return RunEnemyAttackUnchanged();

                yield return BuildRig("U2A5_Archer", new Vector3(ArcherSpawnX, 0.15f, 0f));
                _archer = _builtRig;
                _archerSpawn = _archer.transform.position;
                _archerRanged = _archer.GetComponent<RangedCombat>();
                _archer.ApplyClass(_archerClass, _config);
                yield return WaitFrames(3);

                yield return RunMultiShotFlow();
                yield return RunPiercingFlow();
                yield return RunArcherOrdinaryAttackUnchanged();

                CheckDefinitionsUnchanged();
            }

            /// <summary>MoveNext under a guard. Returns false only when MoveNext threw, in which case fault
            /// carries the genuine exception and the driver stops the pass.</summary>
            private static bool TryStep(IEnumerator enumerator, out bool moved, out System.Exception fault)
            {
                fault = null;
                try
                {
                    moved = enumerator.MoveNext();
                    return true;
                }
                catch (System.Exception exception)
                {
                    moved = false;
                    fault = exception;
                    return false;
                }
            }

            /// <summary>Current under the same guard: a property that throws is a fault, not a silent null.</summary>
            private static bool TryCurrent(IEnumerator enumerator, out object value, out System.Exception fault)
            {
                fault = null;
                try
                {
                    value = enumerator.Current;
                    return true;
                }
                catch (System.Exception exception)
                {
                    value = null;
                    fault = exception;
                    return false;
                }
            }

            /// <summary>Disposes one iterator once so the finally blocks it owns run. Idempotent teardowns
            /// (Cleanup) make a second pass harmless.</summary>
            private void DisposeOnce(IEnumerator enumerator)
            {
                if (enumerator == null) return;

                try
                {
                    System.IDisposable disposable = enumerator as System.IDisposable;
                    if (disposable != null) disposable.Dispose();
                }
                catch (System.Exception exception)
                {
                    _failures++;
                    Debug.LogError(Tag + " iterator teardown threw: " + exception);
                }
            }

            // ------------------------------------------------------------------ authored data

            private void LoadAssets()
            {
                _warriorClass = LoadAsset<CharacterClassData>(EmberdelvePaths.Data + "/Class_Warrior.asset", "Class_Warrior");
                _archerClass = LoadAsset<CharacterClassData>(EmberdelvePaths.Data + "/Class_Archer.asset", "Class_Archer");
                _sword = LoadAsset<WeaponData>(EmberdelvePaths.Data + "/Weapon_AshenShortSword.asset", "Weapon_AshenShortSword");
                _bow = LoadAsset<WeaponData>(EmberdelvePaths.Data + "/Weapon_HollowBow.asset", "Weapon_HollowBow");
                _claw = LoadAsset<WeaponData>(EmberdelvePaths.Data + "/Weapon_HollowClaw.asset", "Weapon_HollowClaw");
                _cuirass = LoadAsset<ItemData>(EmberdelvePaths.Data + "/Item_BoiledCuirass.asset", "Item_BoiledCuirass");
                _servitor = LoadAsset<EnemyData>(EmberdelvePaths.Data + "/Enemy_HollowServitor.asset", "Enemy_HollowServitor");
                _navGrid = LoadAsset<NavGridData>(EmberdelvePaths.Data + "/EmberdelveNavGrid.asset", "EmberdelveNavGrid");
                _config = LoadAsset<GameConfig>(EmberdelvePaths.Data + "/EmberdelveGameConfig.asset", "EmberdelveGameConfig");

                SkillData dash = LoadAsset<SkillData>(EmberdelvePaths.Data + "/Skill_WarriorDash.asset", "Skill_WarriorDash");
                SkillData cry = LoadAsset<SkillData>(EmberdelvePaths.Data + "/Skill_WarriorBattleCry.asset", "Skill_WarriorBattleCry");
                SkillData multi = LoadAsset<SkillData>(EmberdelvePaths.Data + "/Skill_ArcherMultiShot.asset", "Skill_ArcherMultiShot");
                SkillData pierce = LoadAsset<SkillData>(EmberdelvePaths.Data + "/Skill_ArcherPiercingShot.asset", "Skill_ArcherPiercingShot");

                _pristineWarrior = EditorJsonUtility.ToJson(_warriorClass);
                _pristineArcher = EditorJsonUtility.ToJson(_archerClass);
                _pristineSword = EditorJsonUtility.ToJson(_sword);
                _pristineBow = EditorJsonUtility.ToJson(_bow);
                _pristineDash = EditorJsonUtility.ToJson(dash);
                _pristineCry = EditorJsonUtility.ToJson(cry);
                _pristineMulti = EditorJsonUtility.ToJson(multi);
                _pristinePierce = EditorJsonUtility.ToJson(pierce);
                _pristineCuirass = EditorJsonUtility.ToJson(_cuirass);
            }

            /// <summary>
            /// The helper band itself. These are the only helper-only assertions in the pass, and they exist
            /// to pin the two clamps a legal score can never reach on its own (the 1..30 score band caps the
            /// CHA modifier at +10 / -5). Everything else below is measured from casted gameplay.
            /// </summary>
            private void CheckAttributeRuleBand()
            {
                CheckClose("AttributeRules.AbilityEffectScale(modifier -1)", 0.96f, AttributeRules.AbilityEffectScale(-1), "CHA 8/9");
                CheckClose("AttributeRules.AbilityEffectScale(modifier 0)", 1f, AttributeRules.AbilityEffectScale(0), "CHA 10/11 is neutral");
                CheckClose("AttributeRules.AbilityEffectScale(modifier 2)", 1.08f, AttributeRules.AbilityEffectScale(2), "CHA 14/15");
                CheckClose("AttributeRules.AbilityEffectScale(modifier 4)", 1.16f, AttributeRules.AbilityEffectScale(4), "CHA 18/19");
                CheckClose("AttributeRules.AbilityEffectScale(modifier 9)", 1.36f, AttributeRules.AbilityEffectScale(9), "CHA 28/29");
                CheckClose("AttributeRules.AbilityEffectScale(modifier 10)", 1.4f, AttributeRules.AbilityEffectScale(10), "the highest modifier a legal score can produce");
                CheckClose("AttributeRules.AbilityEffectScale(modifier 12) clamps at the ceiling", AttributeRules.MaxEffectScale,
                    AttributeRules.AbilityEffectScale(12), "1 + 0.04*12 = 1.48 would have been wrong");
                CheckClose("AttributeRules.AbilityEffectScale(modifier -12) clamps at the floor", AttributeRules.MinEffectScale,
                    AttributeRules.AbilityEffectScale(-12), "1 + 0.04*-12 = 0.52 would have been wrong");
                CheckClose("AttributeRules.AbilityEffectScale(modifier -5)", 0.8f, AttributeRules.AbilityEffectScale(-5), "score 1, the lowest legal CHA");
            }

            // ---------------------------------------------------------------- harness plumbing

            private void BuildAuthority()
            {
                GameObject authorityObject = new GameObject("U2A5_CombatAuthority");
                _objects.Add(authorityObject);
                _authority = authorityObject.AddComponent<LocalCombatAuthority>();
                SetReference(_authority, "config", _config);
            }

            /// <summary>
            /// Real Emv_Level ground under both test areas, so a dash is a grounded controller dash and the
            /// line-of-sight rays stay above the floor. Same layer and mask the level builder uses.
            /// </summary>
            private void BuildFloors()
            {
                BuildLevelBox("U2A5_Floor_Warrior", new Vector3(0f, -0.5f, 8f), new Vector3(26f, 1f, 34f));
                BuildLevelBox("U2A5_Floor_Archer", new Vector3(ArcherSpawnX, -0.5f, 8f), new Vector3(26f, 1f, 34f));
                Physics.SyncTransforms();
            }

            private GameObject BuildLevelBox(string name, Vector3 center, Vector3 scale)
            {
                GameObject box = GameObject.CreatePrimitive(PrimitiveType.Cube);
                box.name = name;
                box.transform.position = center;
                box.transform.localScale = scale;
                int levelLayer = LayerMask.NameToLayer(GameConfig.LevelLayerName);
                if (levelLayer >= 0) box.layer = levelLayer;
                _objects.Add(box);
                Physics.SyncTransforms();
                return box;
            }

            /// <summary>
            /// The rig the level builder builds, minus the UI/interaction pieces this slice does not touch:
            /// real components, real serialized wiring, real Awake. PlayerRig is added after the components it
            /// requires so Unity cannot add an unwired one implicitly, and every component PlayerRig's own
            /// Awake/WireRig owns (mana, stamina, status, equipment, SkillCaster) is then taken from the body
            /// with Ensure/GetComponent, so each one exists exactly once and rig, caster and assertions share
            /// it. The finished rig lands in _builtRig because an iterator method cannot take an out
            /// parameter.
            /// </summary>
            private IEnumerator BuildRig(string name, Vector3 position)
            {
                GameObject body = new GameObject(name);
                body.transform.position = position;
                int playerLayer = LayerMask.NameToLayer(GameConfig.PlayerLayerName);
                if (playerLayer >= 0) body.layer = playerLayer;
                _objects.Add(body);

                CharacterController controller = body.AddComponent<CharacterController>();
                controller.height = 1.8f;
                controller.radius = 0.34f;
                controller.center = new Vector3(0f, 0.9f, 0f);
                controller.skinWidth = 0.03f;
                controller.slopeLimit = 52f;
                controller.stepOffset = 0.32f;

                EmberdelveInput input = body.AddComponent<EmberdelveInput>();
                PlayerMovement movement = body.AddComponent<PlayerMovement>();
                PlayerLook look = body.AddComponent<PlayerLook>();
                HealthComponent health = body.AddComponent<HealthComponent>();
                Inventory inventory = body.AddComponent<Inventory>();
                ClassLoadout loadout = body.AddComponent<ClassLoadout>();

                // PlayerRig is the composition root and its real Awake runs WireRig, which Ensures the
                // ManaComponent, StaminaComponent and StatusComponent, the Equipment component and the
                // SkillCaster - and SkillCaster.Awake then Ensures that very same ManaComponent. Everything
                // the rig owns is therefore taken from the body afterwards instead of being added a second
                // time: the duplicate AddComponent calls this replaces created a second pool and a second
                // status set, so the caster spent from one ManaComponent while the assertions read another.
                PlayerRig rig = body.AddComponent<PlayerRig>();
                _builtRig = rig;

                MeleeCombat melee = body.AddComponent<MeleeCombat>();
                RangedCombat ranged = body.AddComponent<RangedCombat>();

                ManaComponent mana = ManaComponent.Ensure(body);
                StaminaComponent stamina = StaminaComponent.Ensure(body);
                StatusComponent status = StatusComponent.Ensure(body);
                Equipment equipment = Equipment.Ensure(body);
                SkillCaster skills = body.GetComponent<SkillCaster>();
                if (skills == null) skills = body.AddComponent<SkillCaster>();

                GameObject pivot = new GameObject(name + "_LookPivot");
                pivot.transform.SetParent(body.transform, false);
                pivot.transform.localPosition = new Vector3(0f, 1.66f, 0f);

                GameObject cameraObject = new GameObject(name + "_Camera");
                cameraObject.transform.SetParent(pivot.transform, false);
                Camera camera = cameraObject.AddComponent<Camera>();
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.nearClipPlane = 0.05f;
                camera.farClipPlane = 200f;

                GameObject anchor = new GameObject(name + "_WeaponAnchor");
                anchor.transform.SetParent(pivot.transform, false);
                anchor.transform.localPosition = Vector3.zero;

                SetReference(movement, "input", input);
                SetReference(movement, "controller", controller);
                SetReference(movement, "groundMask", LayerMask.GetMask(GameConfig.LevelLayerName));
                SetReference(look, "input", input);
                SetReference(look, "movement", movement);
                SetReference(look, "lookPivot", pivot.transform);
                SetReference(look, "cameraTransform", cameraObject.transform);
                SetReference(rig, "input", input);
                SetReference(rig, "movement", movement);
                SetReference(rig, "look", look);
                SetReference(rig, "health", health);
                SetReference(rig, "mana", mana);
                SetReference(rig, "stamina", stamina);
                SetReference(rig, "status", status);
                SetReference(rig, "skills", skills);
                SetReference(rig, "inventory", inventory);
                SetReference(rig, "equipment", equipment);
                SetReference(rig, "loadout", loadout);
                SetReference(loadout, "weaponAnchor", anchor.transform);
                SetReference(loadout, "meleeCombat", melee);
                SetReference(loadout, "rangedCombat", ranged);
                SetReference(loadout, "targetCamera", camera);
                SetReference(melee, "rig", rig);
                SetReference(melee, "config", _config);
                SetReference(ranged, "rig", rig);
                SetReference(ranged, "config", _config);

                rig.WireRig();
                rig.Inventory.Initialize(rig);
                rig.Equipment.Initialize(rig);

                CheckRigComponentOwnership(rig, name);

                Physics.SyncTransforms();
                yield return WaitFrames(2);
            }
            private void CheckWarriorKit()
            {
                SkillCaster caster = _warrior.Skills;
                SkillData dash = caster.ActiveSkill(0);
                SkillData cry = caster.ActiveSkill(1);

                Check(dash != null && dash.skillId == "warrior_dash", $"warrior active 1 is Dash ({SkillName(dash)})");
                Check(cry != null && cry.skillId == "warrior_battle_cry", $"warrior active 2 is Battle Cry ({SkillName(cry)})");

                CheckInt("warrior CHA score", 10, _warrior.AttributeStats.GetAttribute(SixAttribute.Charisma), "authored 16/10/14/8/10/10");
                CheckInt("warrior CHA modifier", 0, _warrior.AttributeStats.GetModifier(SixAttribute.Charisma), "floor((10-10)/2) = 0");
                CheckClose("warrior ability scale query at CHA 10", 1f, _warrior.AttributeStats.GetAbilityEffectScale(), "neutral");

                CheckClose("Dash asset authored distance", 4.6f, dash.dashDistance, "the gate reads the asset, it never writes it");
                CheckClose("Dash asset authored duration", 0.22f, dash.dashDuration, "the gate reads the asset, it never writes it");
                CheckInt("Dash asset mana cost", 24, dash.EffectiveManaCost, "cost is not attribute data");
                CheckClose("Dash asset cooldown", 7f, dash.EffectiveCooldown, "cooldown is not attribute data");

                CheckClose("Battle Cry asset radius", 6f, cry.radius, "cast validation is not scaled");
                CheckInt("Battle Cry target cap", 4, cry.maxTargets, "target count is not scaled");
                CheckClose("Battle Cry flat damage", 14f, cry.flatDamage, "the cry's own damage number is not CHA data in this slice");
                CheckClose("Battle Cry self magnitude", 1.25f, cry.selfStatusMagnitude, "authored magnitude stays authored");
                CheckClose("Battle Cry target magnitude", 0.55f, cry.targetStatusMagnitude, "authored magnitude stays authored");
                CheckClose("Battle Cry self duration", 6f, cry.selfStatusDuration, "authored duration stays authored");
                CheckClose("Battle Cry target duration", 4f, cry.targetStatusDuration, "authored duration stays authored");
                CheckInt("Battle Cry mana cost", 32, cry.EffectiveManaCost, "cost is not attribute data");
                CheckClose("Battle Cry cooldown", 14f, cry.EffectiveCooldown, "cooldown is not attribute data");
            }

            // ------------------------------------------------------------------------ dash

            private IEnumerator RunDashFlow()
            {
                SkillData dash = _warrior.Skills.ActiveSkill(0);
                float authoredSpeed = dash.dashDistance / dash.dashDuration;

                yield return MeasureDash("warrior CHA 10 (scale 1.00)", 1f, authoredSpeed);

                EquipCharm(_warrior, "u2a5_warrior_cha8", EquipmentSlot.Head, 8);
                CheckInt("warrior CHA 18 after the charm", 18, _warrior.AttributeStats.GetAttribute(SixAttribute.Charisma), "10 + 8");
                CheckClose("warrior ability scale query at CHA 18", 1.16f, _warrior.AttributeStats.GetAbilityEffectScale(), "modifier 4");
                yield return MeasureDash("warrior CHA 18 (scale 1.16)", 1.16f, authoredSpeed);

                CheckClose("dash duration at CHA 18 is 1.16x the measured baseline", _dashDurationBaseline * 1.16f,
                    _dashDurationMeasured, DurationTolerance, "the same skill's duration grew by the scale and nothing else");
                CheckClose("ground covered at CHA 18 is 1.16x the measured baseline", _dashDistanceBaseline * 1.16f,
                    _dashDistanceMeasured, DistanceTolerance, "the same skill's distance grew by the same scale");

                UnequipAll(_warrior);
                EquipCharm(_warrior, "u2a5_warrior_cha10_head", EquipmentSlot.Head, 10);
                EquipCharm(_warrior, "u2a5_warrior_cha10_chest", EquipmentSlot.Chest, 10);
                CheckInt("warrior CHA clamped to the legal 30", 30, _warrior.AttributeStats.GetAttribute(SixAttribute.Charisma), "10 + 10 + 10, score ceiling 30");
                CheckClose("warrior ability scale query at the 1.4 ceiling", AttributeRules.MaxEffectScale,
                    _warrior.AttributeStats.GetAbilityEffectScale(), "the highest scale a legal score can reach");
                yield return MeasureDash("warrior CHA 30 (scale 1.40)", 1.4f, authoredSpeed);

                UnequipAll(_warrior);
                CheckInt("warrior CHA back to 10", 10, _warrior.AttributeStats.GetAttribute(SixAttribute.Charisma),
                    "recomputed from the pristine base, nothing stacked");
                yield return MeasureDash("warrior CHA 10 again (scale 1.00, no stacking)", 1f, authoredSpeed);

                yield return RunDashBlocked();

                CheckClose("Dash asset distance still authored after every scaled cast", 4.6f, dash.dashDistance, "shared SkillData never mutated");
                CheckClose("Dash asset duration still authored after every scaled cast", 0.22f, dash.dashDuration, "shared SkillData never mutated");
            }

            /// <summary>
            /// One real dash, measured only through what PlayerMovement itself reports: DesiredMoveIntent for
            /// the speed it is running, DashProgress for the duration it asked for (progress is
            /// elapsed / askedDuration because every frame subtracts its own deltaTime), and the transform for
            /// the ground actually covered. Mana and cooldown are read from SkillCaster in the same breath.
            /// </summary>
            private IEnumerator MeasureDash(string label, float scale, float authoredSpeed)
            {
                PlayerRig rig = _warrior;
                SkillCaster caster = rig.Skills;
                SkillData dash = caster.ActiveSkill(0);

                DeactivateTargets();
                rig.Movement.CancelDash();
                rig.ClearStatuses();
                rig.transform.position = _warriorSpawn;
                rig.transform.rotation = Quaternion.LookRotation(Vector3.forward);
                caster.HandleKitRestored();
                rig.Mana.RefillToMax();
                Physics.SyncTransforms();
                yield return WaitFrames(2);

                Check(!rig.Movement.IsDashing, $"{label}: no dash already running before the cast");

                float manaBefore = rig.Mana.CurrentMana;
                Vector3 start = Flat(rig.transform.position);
                float castTime = Time.time;

                Check(caster.TryCastSlot(0), $"{label}: Dash accepted through SkillCaster.TryCastSlot");
                Check(rig.Movement.IsDashing, $"{label}: PlayerMovement reports a dash in progress");
                CheckClose($"{label}: Dash spent exactly its authored mana", dash.EffectiveManaCost,
                    manaBefore - rig.Mana.CurrentMana, "CHA never touches mana cost");
                CheckClose($"{label}: Dash started its authored cooldown", dash.EffectiveCooldown,
                    caster.CooldownRemaining(0), "CHA never touches cooldown");

                yield return null;

                float speed = rig.Movement.DesiredMoveIntent.magnitude;
                CheckClose($"{label}: dash speed equals authored distance / duration", authoredSpeed, speed, SpeedTolerance,
                    "distance and duration carry the same scale, so the authored trajectory speed and the 24 m/s cap are untouched");

                // R6: the dash is over when PlayerMovement says it is. Sampling runs until IsDashing goes
                // false, bounded by elapsed game time derived from the authored scaled duration plus a fixed
                // settling margin, an independent real-time watchdog and a frame guard - not by a counter that
                // stopped 600 frames (~0.17 s at the ~3500 FPS this run measured) into every dash.
                float scaledDuration = dash.dashDuration * scale;
                yield return WaitForDashFinish(rig, castTime, scaledDuration);

                Check(_dashFinished, $"{label}: the dash actually finished - IsDashing went false within " +
                    $"{scaledDuration + DashFinishMargin:F4}s of game time from the cast " +
                    $"(last report: progress {_dashLastProgress:F4} at {_dashLastElapsed:F4}s)");

                Check(_dashLastProgress > 0.01f && _dashLastElapsed > 0.001f,
                    $"{label}: the dash ran long enough to report its own progress (progress {_dashLastProgress:F4} at {_dashLastElapsed:F4}s)");

                // Truncated sampling is not a duration: an unfinished dash reports 0 and fails the checks below.
                float duration = _dashFinished && _dashLastProgress > 0.01f ? _dashLastElapsed / _dashLastProgress : 0f;
                float moved = Vector3.Distance(start, Flat(rig.transform.position));

                CheckClose($"{label}: dash duration is authored duration x scale", dash.dashDuration * scale, duration, DurationTolerance,
                    "PlayerMovement's own progress report, not a mirror of the formula");
                CheckClose($"{label}: asked distance (speed x duration) is authored distance x scale", dash.dashDistance * scale,
                    speed * duration, 0.06f, "the trajectory the controller was asked to travel");
                CheckClose($"{label}: ground actually covered", dash.dashDistance * scale, moved, DistanceTolerance,
                    "real CharacterController movement through the real dash mode");
                Check(!rig.Movement.DashWasBlocked, $"{label}: the clear-corridor dash finished without hitting geometry");
                Check(duration > 0.05f && duration < 2f && !float.IsNaN(duration) && !float.IsInfinity(duration),
                    $"{label}: the dash duration is finite and inside PlayerMovement's own band ({duration:F4}s)");

                _dashDurationMeasured = duration;
                _dashDistanceMeasured = moved;

                if (scale == 1f && _dashDurationBaseline <= 0f)
                {
                    _dashDurationBaseline = duration;
                    _dashDistanceBaseline = moved;
                }

                Debug.Log($"{Tag} {label}: scale={scale:F2} speed={speed:F4} duration={duration:F4} " +
                          $"asked={speed * duration:F3} covered={moved:F3} cooldown={caster.CooldownRemaining(0):F2}");
            }

            /// <summary>
            /// Waits until PlayerMovement itself stops reporting the dash, and records what the dash last
            /// reported while it was still running. The bound is elapsed GAME time from the cast: the authored
            /// scaled duration plus <see cref="DashFinishMargin"/>, plus an independent real-time watchdog
            /// (<see cref="DashRealtimeWatchdog"/>, Time.realtimeSinceStartup) and a frame guard so the wait is
            /// finite whatever the frame rate does. Nothing here caps the frame rate, touches Time.timeScale,
            /// calls controller.Move or writes dash state: the only finish signal read is the runtime's own
            /// IsDashing, and _dashFinished is false whenever the bounds expired with a dash still running.
            /// </summary>
            private IEnumerator WaitForDashFinish(PlayerRig rig, float castTime, float authoredScaledDuration)
            {
                PlayerMovement movement = rig != null ? rig.Movement : null;
                float gameDeadline = castTime + authoredScaledDuration + DashFinishMargin;
                float realDeadline = Time.realtimeSinceStartup + DashRealtimeWatchdog;

                _dashLastElapsed = 0f;
                _dashLastProgress = 0f;

                int frames = 0;

                while (movement != null && movement.IsDashing)
                {
                    _dashLastElapsed = Time.time - castTime;
                    _dashLastProgress = movement.DashProgress;

                    if (Time.time >= gameDeadline) break;
                    if (Time.realtimeSinceStartup >= realDeadline) break;
                    if (++frames > DashFinishFrameGuard) break;

                    yield return null;
                }

                _dashFinished = movement != null && !movement.IsDashing;
            }

            /// <summary>The collision stop is still real at a scaled dash: geometry decides, not the scale.</summary>
            private IEnumerator RunDashBlocked()
            {
                PlayerRig rig = _warrior;
                SkillCaster caster = rig.Skills;
                SkillData dash = caster.ActiveSkill(0);

                UnequipAll(rig);
                EquipCharm(rig, "u2a5_warrior_cha8_blocked", EquipmentSlot.Head, 8);

                DeactivateTargets();
                rig.Movement.CancelDash();
                rig.transform.position = _warriorSpawn;
                rig.transform.rotation = Quaternion.LookRotation(Vector3.forward);
                caster.HandleKitRestored();
                rig.Mana.RefillToMax();

                GameObject wall = BuildLevelBox("U2A5_Dash_Wall", _warriorSpawn + new Vector3(0f, 1.4f, 2.6f), new Vector3(3f, 4f, 0.6f));
                Physics.SyncTransforms();
                yield return WaitFrames(3);

                Vector3 start = Flat(rig.transform.position);
                float castTime = Time.time;
                Check(caster.TryCastSlot(0), "blocked Dash cast accepted at CHA 18");

                // R6: the same finish-bound wait MeasureDash uses. The old fixed 40-frame wait asserted
                // ~0.013s into a 0.2552s dash (measured: 0.27m moved), i.e. while the dash was still running
                // and 1.7m short of the wall, so DashWasBlocked and IsDashing were mid-dash readings rather
                // than the outcome of the collision. The wall itself is unchanged and arithmetically correct:
                // center spawn + (0, 1.4, 2.6) with scale (3, 4, 0.6) spans z 2.3..2.9, x +-1.5, y -0.6..3.4,
                // so the 0.34m-radius / 0.03m-skin capsule contacts it at ~1.96m of travel - inside the
                // 5.336m the scaled dash asks for and above the 0.2m floor the assertion requires.
                yield return WaitForDashFinish(rig, castTime, dash.dashDuration * 1.16f);

                Check(_dashFinished, "the blocked dash ended - IsDashing went false within " +
                    $"{dash.dashDuration * 1.16f + DashFinishMargin:F4}s of game time from the cast");

                float moved = Vector3.Distance(start, Flat(rig.transform.position));
                float asked = dash.dashDistance * 1.16f;

                Check(rig.Movement.DashWasBlocked, "the scaled dash still reports that geometry stopped it");
                Check(moved < asked * 0.75f, $"the scaled dash did not phase through the wall ({moved:F2}m of {asked:F2}m asked for)");
                Check(moved > 0.2f, $"the blocked dash still moved the player a real distance ({moved:F2}m)");
                Check(!rig.Movement.IsDashing, "the blocked dash is over");

                wall.SetActive(false);
                UnequipAll(rig);
                rig.Movement.CancelDash();
                Physics.SyncTransforms();
                yield return WaitFrames(2);
            }
            // ------------------------------------------------------------------- battle cry

            private IEnumerator RunBattleCryFlow()
            {
                yield return CastBattleCry("warrior CHA 10 (scale 1.00)", 1f, 3);

                EquipCharm(_warrior, "u2a5_warrior_cha8_cry", EquipmentSlot.Head, 8);
                yield return CastBattleCry("warrior CHA 18 (scale 1.16)", 1.16f, 3);

                UnequipAll(_warrior);
                EquipCharm(_warrior, "u2a5_warrior_chaneg2_cry", EquipmentSlot.Head, -2);
                CheckInt("warrior CHA 8 after the cursed charm", 8, _warrior.AttributeStats.GetAttribute(SixAttribute.Charisma), "10 - 2");
                CheckClose("warrior ability scale query at CHA 8", 0.96f, _warrior.AttributeStats.GetAbilityEffectScale(), "modifier -1");
                yield return CastBattleCry("warrior CHA 8 (scale 0.96)", 0.96f, 3);

                UnequipAll(_warrior);
                EquipCharm(_warrior, "u2a5_warrior_cha10_head_cry", EquipmentSlot.Head, 10);
                EquipCharm(_warrior, "u2a5_warrior_cha10_chest_cry", EquipmentSlot.Chest, 10);
                CheckClose("warrior ability scale query at CHA 30", AttributeRules.MaxEffectScale,
                    _warrior.AttributeStats.GetAbilityEffectScale(), "clamped band ceiling");
                yield return CastBattleCry("warrior CHA 30 (scale 1.40)", 1.4f, 3);

                UnequipAll(_warrior);
                yield return CastBattleCry("warrior CHA 10 again (scale 1.00, no stacking)", 1f, 3);

                yield return CastBattleCry("warrior CHA 10 target cap (six hosts inside the radius)", 1f, 6);
            }

            /// <summary>
            /// One real Battle Cry: real radial scan, real authority hits, real statuses read back from the
            /// targets and from the caster before any Update can tick a duration down.
            /// </summary>
            private IEnumerator CastBattleCry(string label, float scale, int hostsInside)
            {
                PlayerRig rig = _warrior;
                SkillCaster caster = rig.Skills;
                SkillData cry = caster.ActiveSkill(1);

                // The authority stamps its own per-entity, per-skill cooldown clock the moment a hit of an
                // activation is accepted, and refuses a repeat cast that arrives sooner than the authored 14s.
                // HandleKitRestored only clears the caster's local prediction, so the clock has to run out in
                // real time or every hit of this cast is genuinely refused.
                yield return SpaceForNextCryCast(cry);

                DeactivateTargets();
                rig.ClearStatuses();
                rig.transform.position = _warriorSpawn;
                rig.transform.rotation = Quaternion.LookRotation(Vector3.forward);
                caster.HandleKitRestored();
                rig.Mana.RefillToMax();

                // Fixture arithmetic (R3 correction, the runtime is untouched): a host is inside the burst when
                // its CombatCenter is within radius + 0.35 = 6.35 m of the cast centre (HitScanner.CollectRadial
                // hits). CombatCenter sits 0.75 m above that centre (host y 0.9 + 1.1 against spawn y 0.15 + 1.1),
                // so the ground depth has to satisfy sqrt(z^2 + 0.75^2) <= 6.35, i.e. z <= 6.30 m. At the old
                // 1.4 m spacing the fifth and sixth hosts sat at 7.8 m and 9.2 m - sqrt(7.8^2 + 0.75^2) = 7.87 m,
                // outside the authored 6 m radius plus its own 0.35 m slack - so a six-host run asked the cap to
                // reject hosts the radius had already rejected, and only three were ever reachable. Six hosts now
                // sit at 2.2..5.7 m (farthest 5.74 m, still inside 6.35 m), so the four-target cap is what stops
                // the burst at four, which is the coverage this run exists to prove.
                float hostSpacing = hostsInside > cry.maxTargets ? 0.7f : 1.4f;

                List<TestTarget> inside = new List<TestTarget>(hostsInside);
                for (int i = 0; i < hostsInside; i++)
                {
                    inside.Add(BuildTarget("U2A5_Cry_Host_" + _cryCastsTaken + "_" + i,
                        _warriorSpawn + new Vector3(0f, 0.9f, 2.2f + i * hostSpacing)));
                }

                TestTarget outside = BuildTarget("U2A5_Cry_Outside_" + _cryCastsTaken,
                    _warriorSpawn + new Vector3(0f, 0.9f, 9.5f));
                Physics.SyncTransforms();
                yield return WaitFrames(2);

                float manaBefore = rig.Mana.CurrentMana;
                float playerHealthBefore = rig.CurrentHealth;
                int acceptedBefore = _authority.AcceptedCount;
                int rejectedBefore = _authority.RejectedCount;

                float[] hostHealth = new float[inside.Count];
                for (int i = 0; i < inside.Count; i++) hostHealth[i] = inside[i].CurrentHealth;
                float outsideHealth = outside.CurrentHealth;

                ClearCaptured();
                Check(caster.TryCastSlot(1), $"{label}: Battle Cry accepted through SkillCaster.TryCastSlot");
                _cryCastsTaken++;

                CheckClose($"{label}: Battle Cry spent exactly its authored mana", cry.EffectiveManaCost,
                    manaBefore - rig.Mana.CurrentMana, "Docs/Design/ATTRIBUTES.md: effect_scale never scales mana cost");
                CheckClose($"{label}: Battle Cry started its authored cooldown", cry.EffectiveCooldown,
                    caster.CooldownRemaining(1), "effect_scale never scales cooldown");
                Check(Mathf.Abs(playerHealthBefore - rig.CurrentHealth) < 0.001f,
                    $"{label}: the cry did not damage its own caster ({playerHealthBefore:F1} -> {rig.CurrentHealth:F1})");

                int acceptedHosts = Mathf.Min(hostsInside, cry.maxTargets);
                Check(_authority.AcceptedCount == acceptedBefore + acceptedHosts,
                    $"{label}: the authority accepted {acceptedHosts} of {hostsInside} hosts (cap {cry.maxTargets}, radius {cry.radius:F0}m)");
                Check(_authority.RejectedCount == rejectedBefore,
                    $"{label}: nothing inside the radius was refused ({rejectedBefore} -> {_authority.RejectedCount})");
                Check(!outside.HasStatus(StatusKind.Slow), $"{label}: the host outside the radius was not slowed");
                Check(Mathf.Abs(outsideHealth - outside.CurrentHealth) < 0.001f,
                    $"{label}: the host outside the radius took no damage ({outsideHealth:F1} -> {outside.CurrentHealth:F1})");

                float expectedBuff = 1f + (cry.selfStatusMagnitude - 1f) * scale;
                float expectedSlow = 1f + (cry.targetStatusMagnitude - 1f) * scale;
                float expectedSelfDuration = cry.selfStatusDuration * scale;
                float expectedSlowDuration = cry.targetStatusDuration * scale;

                float buff = rig.Status != null ? rig.Status.OutgoingDamageMultiplier : 1f;
                float buffRemaining = rig.Status != null ? rig.Status.RemainingOf(StatusKind.Empowered) : 0f;

                Check(rig.HasStatus(StatusKind.Empowered), $"{label}: the caster is empowered");
                CheckClose($"{label}: self buff magnitude is 1 + 0.25 * scale", expectedBuff, buff, Exact,
                    "the DELTA from neutral 1 scales, not the authored 1.25 wholesale");
                CheckClose($"{label}: self buff duration is 6 * scale", expectedSelfDuration, buffRemaining, Exact,
                    "the beneficial duration carries the same snapshot");
                Check(buffRemaining > 0f && !float.IsNaN(buffRemaining) && !float.IsInfinity(buffRemaining),
                    $"{label}: the self buff duration is finite and positive ({buffRemaining:F4}s)");

                for (int i = 0; i < Mathf.Min(inside.Count, acceptedHosts); i++)
                {
                    TestTarget host = inside[i];

                    // R6: the fixture expectation was wrong, not the runtime. LocalCombatAuthority.SubmitSkill
                    // -> DamageResolver.ResolveSkill multiplies the skill's authored flat 14 by the instigator
                    // multiplier it reads from the warrior (STR 16 -> modifier 3 -> 1.12) and by the Ashen
                    // Might passive 1.2, and BattleCryBehavior builds its SkillIntent with the default effect
                    // scale 1, so the resolved number is 14 * 1.12 * 1.2 = 18.816 at every CHA. The asset's 14
                    // is asserted unchanged below and the resolver is untouched. The pool is 100000, whose
                    // float ulp is 0.0078125, so the subtraction can read 18.8125 - 0.0035 of quantization,
                    // inside the gate's own Exact band. Same Exact, same assertion, no tolerance widened.
                    CheckClose($"{label}: host {i} took the cry's resolved damage on its pool", ExpectedCryDamage,
                        hostHealth[i] - host.CurrentHealth,
                        $"14 authored flat x 1.12 (STR modifier 3) x 1.2 (Ashen Might) = 18.816, identical at every CHA " +
                        $"because this mapping scales the cry's two statuses and not its flat number " +
                        $"({hostHealth[i]:F4} -> {host.CurrentHealth:F4})");
                    Check(host.HasStatus(StatusKind.Slow), $"{label}: host {i} is slowed");
                    CheckClose($"{label}: host {i} slow magnitude is 1 - 0.45 * scale", expectedSlow,
                        host.Status.SpeedMultiplier, Exact,
                        "the debuff delta deepens from neutral instead of the authored 0.55 being rescaled");
                    CheckClose($"{label}: host {i} slow duration is 4 * scale", expectedSlowDuration,
                        host.Status.RemainingOf(StatusKind.Slow), Exact, "the status the authority applied carries the cast's snapshot");
                    Check(host.Status.RemainingOf(StatusKind.Slow) > 0f && !float.IsInfinity(host.Status.RemainingOf(StatusKind.Slow)),
                        $"{label}: host {i} slow duration is finite and positive");
                }

                // The authority's own resolved number, captured on the host before it reaches the pool: the
                // independent exact half of the measurement above (a DamageInfo is not a 100000-pool subtraction).
                Check(_captured.Count == acceptedHosts,
                    $"{label}: the burst resolved as exactly {acceptedHosts} DamageInfo entries from the authority (got {_captured.Count})");

                for (int i = 0; i < _captured.Count; i++)
                {
                    CheckClose($"{label}: captured cry hit {i} amount", ExpectedCryDamage, _captured[i].Amount,
                        "the authority's own resolved number: flatDamage x the instigator multiplier it read from the rig, no effect scale in this intent");
                }

                if (!Mathf.Approximately(scale, 1f))
                {
                    // R6: the exclusion threshold has to be the gate's own exactness band, not a looser
                    // constant. At scale 0.96 the hypotheses are 1 + 0.25*0.96 = 1.24 (correct) against
                    // 1.25*0.96 = 1.20, and 1 - 0.45*0.96 = 0.568 against 0.55*0.96 = 0.528: both separated by
                    // |1 - scale| = 0.04, so a >0.05 test was mathematically incapable of telling them apart
                    // and failed the correct runtime. 0.04 > Exact 0.011 (and 0.16 at 1.16, 0.40 at 1.40), and
                    // the exact expected-value checks above still pin the measured number itself.
                    Check(Mathf.Abs(buff - cry.selfStatusMagnitude * scale) > Exact,
                        $"{label}: the buff is NOT the authored magnitude multiplied wholesale ({buff:F4} against " +
                        $"{cry.selfStatusMagnitude * scale:F4}, separation must exceed {Exact:F4})");

                    TestTarget first = inside[0];
                    Check(Mathf.Abs(first.Status.SpeedMultiplier - cry.targetStatusMagnitude * scale) > Exact,
                        $"{label}: the slow is NOT the authored magnitude multiplied wholesale " +
                        $"({first.Status.SpeedMultiplier:F4} against {cry.targetStatusMagnitude * scale:F4}, " +
                        $"separation must exceed {Exact:F4})");
                }

                Check(cry.selfStatusMagnitude == 1.25f && cry.targetStatusMagnitude == 0.55f &&
                      cry.selfStatusDuration == 6f && cry.targetStatusDuration == 4f &&
                      cry.flatDamage == 14f && cry.radius == 6f && cry.maxTargets == 4,
                    $"{label}: the shared Battle Cry asset still holds its authored numbers");

                Debug.Log($"{Tag} {label}: scale={scale:F2} buff={buff:F4} buffDuration={buffRemaining:F4} " +
                          $"slow={(inside.Count > 0 ? inside[0].Status.SpeedMultiplier : 0f):F4} " +
                          $"slowDuration={(inside.Count > 0 ? inside[0].Status.RemainingOf(StatusKind.Slow) : 0f):F4} " +
                          $"accepted={_authority.AcceptedCount} rejected={_authority.RejectedCount}");

                DeactivateTargets();
                yield return WaitFrames(1);
            }

            /// <summary>
            /// The snapshot rule: a scale belongs to the cast that took it. Equipping CHA gear while a buff is
            /// already running must not retroactively resize that buff, the next accepted cast must pick the
            /// new number up, and taking the gear off must give the old number back exactly.
            /// </summary>
            private IEnumerator RunBattleCrySnapshotFlow()
            {
                PlayerRig rig = _warrior;
                SkillCaster caster = rig.Skills;

                UnequipAll(rig);
                DeactivateTargets();
                rig.ClearStatuses();
                rig.transform.position = _warriorSpawn;
                caster.HandleKitRestored();
                rig.Mana.RefillToMax();

                TestTarget host = BuildTarget("U2A5_Cry_Snapshot", _warriorSpawn + new Vector3(0f, 0.9f, 3f));
                Physics.SyncTransforms();
                yield return WaitFrames(2);

                yield return SpaceForNextCryCast(caster.ActiveSkill(1));
                Check(caster.TryCastSlot(1), "snapshot Battle Cry accepted at CHA 10");
                _cryCastsTaken++;

                float buffBefore = rig.Status.OutgoingDamageMultiplier;
                float buffDurationBefore = rig.Status.RemainingOf(StatusKind.Empowered);
                float slowBefore = host.Status.SpeedMultiplier;
                float slowDurationBefore = host.Status.RemainingOf(StatusKind.Slow);

                CheckClose("snapshot buff at CHA 10 is the authored 1.25", 1.25f, buffBefore, Exact, "scale 1");

                EquipCharm(rig, "u2a5_warrior_cha10_snapshot", EquipmentSlot.Head, 10);
                CheckClose("CHA 20 query now reports scale 1.2", 1.2f, rig.AttributeStats.GetAbilityEffectScale(), Exact, "modifier 5");

                CheckClose("the running self buff was not retroactively resized (magnitude)", buffBefore,
                    rig.Status.OutgoingDamageMultiplier, Exact, "the effect already in force keeps its cast's snapshot");
                CheckClose("the running self buff was not retroactively resized (duration)", buffDurationBefore,
                    rig.Status.RemainingOf(StatusKind.Empowered), Exact, "no re-derivation of an applied status");
                CheckClose("the slow already on the host was not retroactively deepened", slowBefore,
                    host.Status.SpeedMultiplier, Exact, "the authority applied one snapshot and that is what is running");
                CheckClose("the slow already on the host kept its duration", slowDurationBefore,
                    host.Status.RemainingOf(StatusKind.Slow), Exact, "no re-derivation of an applied status");

                rig.ClearStatuses();
                host.ClearStatuses();
                caster.HandleKitRestored();
                rig.Mana.RefillToMax();
                yield return WaitFrames(2);

                yield return SpaceForNextCryCast(caster.ActiveSkill(1));
                Check(caster.TryCastSlot(1), "second Battle Cry accepted at CHA 20");
                _cryCastsTaken++;
                CheckClose("the next accepted cast picked up the new buff delta", 1.3f, rig.Status.OutgoingDamageMultiplier, Exact,
                    "1 + 0.25 * 1.2");
                CheckClose("the next accepted cast picked up the new duration", 7.2f, rig.Status.RemainingOf(StatusKind.Empowered), Exact,
                    "6 * 1.2");
                CheckClose("the next accepted cast deepened the slow delta", 0.46f, host.Status.SpeedMultiplier, Exact, "1 - 0.45 * 1.2");
                CheckClose("the next accepted cast lengthened the slow", 4.8f, host.Status.RemainingOf(StatusKind.Slow), Exact, "4 * 1.2");

                UnequipAll(rig);
                rig.ClearStatuses();
                host.ClearStatuses();
                caster.HandleKitRestored();
                rig.Mana.RefillToMax();
                yield return WaitFrames(2);

                yield return SpaceForNextCryCast(caster.ActiveSkill(1));
                Check(caster.TryCastSlot(1), "third Battle Cry accepted after unequipping");
                _cryCastsTaken++;
                CheckClose("the removed gear left no residue in the buff (no stacking)", 1.25f, rig.Status.OutgoingDamageMultiplier, Exact,
                    "back to exactly the authored 1.25");
                CheckClose("the removed gear left no residue in the buff duration", 6f, rig.Status.RemainingOf(StatusKind.Empowered), Exact,
                    "back to exactly the authored 6s");
                CheckClose("the removed gear left no residue in the slow", 0.55f, host.Status.SpeedMultiplier, Exact,
                    "back to exactly the authored 0.55");

                DeactivateTargets();
                yield return WaitFrames(1);
            }
            // ------------------------------------------------------------------ test hosts

            /// <summary>
            /// A monster-team host this pass may damage, slow and read back: real HealthComponent, real
            /// StatusComponent, a real collider on the layer the level's enemies wear, and a real id from
            /// CombatRegistry - so HitScanner, the authority and StatusComponent all treat it exactly like a
            /// Hollow Servitor. It has no EnemyAgent and no AI, so it never wanders into the next measurement
            /// and never attacks a rig. Its health pool is huge on purpose: nothing in this pass may kill it.
            /// </summary>
            private TestTarget BuildTarget(string name, Vector3 position)
            {
                GameObject host = new GameObject(name);
                host.transform.position = position;
                int enemyLayer = LayerMask.NameToLayer(GameConfig.EnemyLayerName);
                if (enemyLayer >= 0) host.layer = enemyLayer;
                _objects.Add(host);
                _targets.Add(host);

                host.AddComponent<HealthComponent>();
                host.AddComponent<StatusComponent>();

                BoxCollider body = host.AddComponent<BoxCollider>();
                body.center = new Vector3(0f, 1.1f, 0f);
                body.size = new Vector3(0.7f, 1.7f, 0.5f);

                TestTarget target = host.AddComponent<TestTarget>();
                target.ConfigurePool(100000);
                SubscribeTarget(target);
                return target;
            }

            /// <summary>
            /// Takes every host built so far out of the physics and out of the scans, without destroying it:
            /// the next measurement starts with an empty field of fire, and the counters the authority already
            /// reported stay the ones this pass asserted.
            /// </summary>
            private void DeactivateTargets()
            {
                for (int i = 0; i < _targets.Count; i++)
                {
                    if (_targets[i] != null) _targets[i].SetActive(false);
                }
            }

            /// <summary>
            /// Routes everything the authority resolves onto this host into <see cref="_captured"/>, so a
            /// scenario can assert the DamageInfo the authority actually produced (amount, source, headshot)
            /// instead of inferring it from a health delta. Subscribing twice is refused.
            /// </summary>
            private void SubscribeTarget(TestTarget target)
            {
                if (target == null || _subscribed.Contains(target)) return;
                target.AttachCapture(_captured);
                _subscribed.Add(target);
            }

            private void UnsubscribeTargets()
            {
                for (int i = 0; i < _subscribed.Count; i++)
                {
                    if (_subscribed[i] != null) _subscribed[i].DetachCapture();
                }
                _subscribed.Clear();
            }

            /// <summary>Empties the capture without unsubscribing anything: one measurement, one clean slate.</summary>
            private void ClearCaptured()
            {
                _captured.Clear();
            }

            /// <summary>
            /// Listens to the one damage event the authority raises for the player (GameEvents.PlayerDamaged),
            /// for the scenarios that must read an incoming hit from the receiving side. Subscribes once.
            /// </summary>
            private void HookPlayerDamage()
            {
                if (_playerDamageHooked) return;
                GameEvents.PlayerDamaged += OnPlayerDamaged;
                _playerDamageHooked = true;
            }

            private void UnhookPlayerDamage()
            {
                if (!_playerDamageHooked) return;
                GameEvents.PlayerDamaged -= OnPlayerDamaged;
                _playerDamageHooked = false;
            }

            private void OnPlayerDamaged(int playerId, DamageInfo info)
            {
                _captured.Add(info);
            }

            // -------------------------------------------------------------- ephemeral CHA gear

            /// <summary>
            /// One CHA-only charm, created as an instance, carried in the bag, then worn through the real
            /// Equipment path. Never saved, never a project asset, destroyed with the pass. The bonus is a
            /// single AttributeBonusEntry: no statModifiers, so nothing else can move while the charm is on.
            /// </summary>
            private void EquipCharm(PlayerRig rig, string id, EquipmentSlot slot, int charismaBonus)
            {
                if (rig == null || rig.Inventory == null || rig.Equipment == null)
                {
                    Check(false, $"{id}: the rig has an inventory and equipment to wear it in");
                    return;
                }

                ItemData charm = ScriptableObject.CreateInstance<ItemData>();
                _tempItems.Add(charm);

                // R3 fixture: one item id names exactly one definition in a catalog, and this helper is called
                // several times with the same label for different bonuses (for example the cursed -2 charm in two
                // scenarios). The label stays in every message; the definition's own id gets a serial so each
                // ephemeral charm is a distinct catalog entry.
                charm.itemId = id + "_" + (++_charmSerial);
                charm.displayName = "U2A5 CHA " + (charismaBonus >= 0 ? "+" : string.Empty) + charismaBonus;
                charm.description = "Ephemeral U2A5 gate item: one authored CHA bonus, no other modifiers.";
                charm.kind = ItemKind.Armor;
                charm.equipSlot = slot;
                charm.gridFootprint = new Vector2Int(1, 1);
                charm.maxStack = 1;
                charm.value = 1;
                charm.statModifiers = null;
                charm.attributeBonuses = new[] { new AttributeBonusEntry(SixAttribute.Charisma, charismaBonus) };

                // R3 fixture: the charm is an ad-hoc definition, not a production catalog entry, so the bag mints
                // against an explicit TEST catalog (production items + this charm).
                bool adopted = EmberdelveTestCatalog.Adopt(ref _testCatalog, rig.Inventory, charm, out string adoptReason);
                Check(adopted, $"{id} registered in the test catalog{(adopted ? string.Empty : ": " + adoptReason)}");

                // Surface the bag's own refusal reason (for example an unstable item id) in the log, so a
                // fixture that can never be worn explains itself instead of failing only downstream.
                bool placed = rig.Inventory.TryAdd(charm, 1, out string addReason);
                Check(placed, $"{id} placed in the bag{(placed ? string.Empty : " refused: " + addReason)}");
                if (!placed) Debug.LogError($"[AttributeSkill] charm '{id}' was refused by Inventory.TryAdd: {addReason}");

                bool ok = rig.Equipment.TryEquipItem(charm, out string reason);
                Check(ok, $"equip {id} in {slot}{(ok ? string.Empty : " refused: " + reason)}");
            }

            /// <summary>
            /// Takes every worn item off through the real per-slot unequip, so a later measurement reads the
            /// pristine class numbers and not a leftover charm. Empty slots are skipped instead of reported as
            /// failures, because UnequipAll also runs at the start of a step.
            /// </summary>
            private void UnequipAll(PlayerRig rig)
            {
                if (rig == null || rig.Equipment == null) return;

                for (int i = (int)EquipmentSlot.Weapon; i <= (int)EquipmentSlot.Back; i++)
                {
                    EquipmentSlot slot = (EquipmentSlot)i;
                    ItemData worn = rig.Equipment.Equipped(slot);
                    if (worn == null) continue;

                    bool ok = rig.Equipment.TryUnequip(slot, out string reason);
                    Check(ok, $"unequip {worn.itemId} from {slot}{(ok ? string.Empty : " refused: " + reason)}");
                }
            }

            // ---------------------------------------------------------------- shared definitions

            /// <summary>
            /// The whole pass only ever reads the shared ScriptableObjects. Every definition snapshotted as
            /// pristine JSON in LoadAssets is compared again here, after every scaled cast, gear change and
            /// authority hit.
            /// </summary>
            private void CheckDefinitionsUnchanged()
            {
                Check(_pristineWarrior == EditorJsonUtility.ToJson(_warriorClass), "the warrior class asset is untouched by the whole pass");
                Check(_pristineArcher == EditorJsonUtility.ToJson(_archerClass), "the archer class asset is untouched by the whole pass");
                Check(_pristineSword == EditorJsonUtility.ToJson(_sword), "the sword definition is untouched");
                Check(_pristineBow == EditorJsonUtility.ToJson(_bow), "the bow definition is untouched");
                Check(_pristineDash == EditorJsonUtility.ToJson(LoadAsset<SkillData>(EmberdelvePaths.Data + "/Skill_WarriorDash.asset", "Skill_WarriorDash")),
                    "the Dash definition is untouched");
                Check(_pristineCry == EditorJsonUtility.ToJson(LoadAsset<SkillData>(EmberdelvePaths.Data + "/Skill_WarriorBattleCry.asset", "Skill_WarriorBattleCry")),
                    "the Battle Cry definition is untouched");
                Check(_pristineMulti == EditorJsonUtility.ToJson(LoadAsset<SkillData>(EmberdelvePaths.Data + "/Skill_ArcherMultiShot.asset", "Skill_ArcherMultiShot")),
                    "the Multi-shot definition is untouched");
                Check(_pristinePierce == EditorJsonUtility.ToJson(LoadAsset<SkillData>(EmberdelvePaths.Data + "/Skill_ArcherPiercingShot.asset", "Skill_ArcherPiercingShot")),
                    "the Piercing Shot definition is untouched");
                Check(_pristineCuirass == EditorJsonUtility.ToJson(_cuirass), "the cuirass definition is untouched");
            }

            // ------------------------------------------------------------------------- assertions

            private void Check(bool condition, string name)
            {
                _checks++;
                if (condition)
                {
                    Debug.Log($"{Tag} checked {name}");
                    return;
                }

                _failures++;
                Debug.LogError($"{Tag} mismatch {name}");
            }

            private void CheckInt(string name, int expected, int actual, string note)
            {
                Check(expected == actual, $"{name}: got {actual}, expected {expected} ({note})");
            }

            private void CheckClose(string name, float expected, float actual, string note)
            {
                CheckClose(name, expected, actual, Exact, note);
            }

            /// <summary>The tolerance is explicit where the runtime itself is only accurate to a frame.</summary>
            private void CheckClose(string name, float expected, float actual, float tolerance, string note)
            {
                bool finite = !float.IsNaN(expected) && !float.IsInfinity(expected) &&
                              !float.IsNaN(actual) && !float.IsInfinity(actual);
                Check(finite && Mathf.Abs(expected - actual) <= Mathf.Abs(tolerance),
                    $"{name}: got {actual:F4}, expected {expected:F4} (tolerance {Mathf.Abs(tolerance):F4}) ({note})");
            }

            // ------------------------------------------------------------------------ plumbing

            /// <summary>The gate reads the saved data assets; it does not rebuild or save any of them.</summary>
            private static T LoadAsset<T>(string path, string label) where T : UnityEngine.Object
            {
                T asset = AssetDatabase.LoadAssetAtPath<T>(path);
                if (asset == null) throw new System.Exception($"{Tag} missing asset: {label} at {path}");
                return asset;
            }

            /// <summary>
            /// The same wiring EmberdelveLevelBuilder uses for a component it creates at runtime: assign the
            /// component's own serialized references. Used only for wiring (the authority's GameConfig), never
            /// to fake a value this pass is asserting.
            /// </summary>
            private static void SetReference(Component target, string propertyName, UnityEngine.Object value)
            {
                if (target == null) return;

                SerializedObject serialized = new SerializedObject(target);
                SerializedProperty property = serialized.FindProperty(propertyName);
                if (property == null)
                {
                    Debug.LogWarning($"{Tag} cannot wire '{propertyName}' on {target.GetType().Name}");
                    return;
                }

                property.objectReferenceValue = value;
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }

            /// <summary>
            /// The layer-mask overload EmberdelveLevelBuilder uses: a LayerMask is not an Object reference, it
            /// serializes as an int. Used for PlayerMovement's groundMask so the rig grounds on the same layer
            /// the level builder's floors wear.
            /// </summary>
            private static void SetReference(Component target, string propertyName, LayerMask mask)
            {
                if (target == null) return;

                SerializedObject serialized = new SerializedObject(target);
                SerializedProperty property = serialized.FindProperty(propertyName);
                if (property == null)
                {
                    Debug.LogWarning($"{Tag} cannot wire '{propertyName}' on {target.GetType().Name}");
                    return;
                }

                if (property.propertyType == SerializedPropertyType.LayerMask ||
                    property.propertyType == SerializedPropertyType.Integer)
                {
                    property.intValue = mask.value;
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                    return;
                }

                Debug.LogWarning($"{Tag} '{propertyName}' on {target.GetType().Name} is not a layer mask");
            }

            /// <summary>
            /// Read-only observation of a component's own reference field. SkillCaster's mana pool and rig are
            /// plain private fields rather than serialized ones, so the fixture invariant that asks whether the
            /// caster spends from the pool the rig reports can only be answered by looking. Nothing is written
            /// here, and nothing is used as an assertion value the pass is meant to measure.
            /// </summary>
            private static UnityEngine.Object ObserveReference(Component target, string fieldName)
            {
                if (target == null) return null;

                System.Reflection.FieldInfo field = target.GetType().GetField(fieldName,
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic);
                if (field == null) return null;

                return field.GetValue(target) as UnityEngine.Object;
            }

            /// <summary>
            /// The fixture invariant U2A5-R5 asked for: every component the rig owns exists exactly once on the
            /// body, and the references the rig, the caster, the melee/ranged components and the equipment
            /// report are the same instances. It runs after the real Awake/WireRig wiring, observes only, and
            /// asserts nothing about gameplay numbers.
            /// </summary>
            private void CheckRigComponentOwnership(PlayerRig rig, string label)
            {
                if (rig == null)
                {
                    Check(false, $"{label}: the built rig exists");
                    return;
                }

                GameObject body = rig.gameObject;

                CheckExactlyOne<CharacterController>(body, $"{label} CharacterController");
                CheckExactlyOne<EmberdelveInput>(body, $"{label} EmberdelveInput");
                CheckExactlyOne<PlayerMovement>(body, $"{label} PlayerMovement");
                CheckExactlyOne<PlayerLook>(body, $"{label} PlayerLook");
                CheckExactlyOne<HealthComponent>(body, $"{label} HealthComponent");
                CheckExactlyOne<Inventory>(body, $"{label} Inventory");
                CheckExactlyOne<ClassLoadout>(body, $"{label} ClassLoadout");
                CheckExactlyOne<PlayerRig>(body, $"{label} PlayerRig");
                CheckExactlyOne<MeleeCombat>(body, $"{label} MeleeCombat");
                CheckExactlyOne<RangedCombat>(body, $"{label} RangedCombat");
                CheckExactlyOne<ManaComponent>(body, $"{label} ManaComponent");
                CheckExactlyOne<StaminaComponent>(body, $"{label} StaminaComponent");
                CheckExactlyOne<StatusComponent>(body, $"{label} StatusComponent");
                CheckExactlyOne<SkillCaster>(body, $"{label} SkillCaster");
                CheckExactlyOne<Equipment>(body, $"{label} Equipment");

                CheckSame($"{label} rig mana", rig.Mana, body.GetComponent<ManaComponent>(), "the rig reports the body's only pool");
                CheckSame($"{label} rig status", rig.Status, body.GetComponent<StatusComponent>(), "the rig reports the body's only status set");
                CheckSame($"{label} rig skills", rig.Skills, body.GetComponent<SkillCaster>(), "the rig reports the body's only caster");
                CheckSame($"{label} rig equipment", rig.Equipment, body.GetComponent<Equipment>(), "the rig reports the body's only equipment");
                CheckSame($"{label} rig loadout", rig.Loadout, body.GetComponent<ClassLoadout>(), "the rig reports the body's only loadout");
                CheckSame($"{label} rig inventory", rig.Inventory, body.GetComponent<Inventory>(), "the rig reports the body's only inventory");

                SkillCaster caster = rig.Skills;
                CheckSame($"{label} caster pool", rig.Mana, ObserveReference(caster, "mana"),
                    "SkillCaster spends from the same ManaComponent the rig reports");
                CheckSame($"{label} caster rig", rig, ObserveReference(caster, "rig"), "SkillCaster's rig is this rig");
                CheckSame($"{label} caster loadout", rig.Loadout, ObserveReference(caster, "loadout"),
                    "SkillCaster reads the same ClassLoadout the rig reports");

                CheckSame($"{label} melee rig", rig, ObserveReference(body.GetComponent<MeleeCombat>(), "rig"),
                    "MeleeCombat's rig is this rig");
                CheckSame($"{label} ranged rig", rig, ObserveReference(body.GetComponent<RangedCombat>(), "rig"),
                    "RangedCombat's rig is this rig");
            }

            /// <summary>Exactly one instance of a component the rig owns, counted on the body itself.</summary>
            private void CheckExactlyOne<T>(GameObject body, string name) where T : Component
            {
                T[] found = body.GetComponents<T>();
                Check(found.Length == 1, $"{name}: exactly one on {body.name}, found {found.Length}");
            }

            /// <summary>Two references that must be the very same object, reported by type when they are not.</summary>
            private void CheckSame(string name, UnityEngine.Object expected, UnityEngine.Object actual, string note)
            {
                string expectedType = expected != null ? expected.GetType().Name : "none";
                string actualType = actual != null ? actual.GetType().Name : "none";
                Check(expected != null && actual != null && expected == actual,
                    $"{name}: got {actualType}, expected the same {expectedType} ({note})");
            }

            /// <summary>Ground distance only: a dash or a shot is measured on the plane, not through the air.</summary>
            private static Vector3 Flat(Vector3 value)
            {
                return new Vector3(value.x, 0f, value.z);
            }

            private static string SkillName(SkillData skill)
            {
                return skill != null ? skill.displayName + " [" + skill.skillId + "]" : "none";
            }

            private static IEnumerator WaitFrames(int count)
            {
                for (int i = 0; i < count; i++) yield return null;
            }

            // -------------------------------------------------------------------------- teardown

            /// <summary>
            /// Runs once, from the end of the guarded driver and from nothing else: the damage hook is removed before
            /// the objects go, every capture is detached, then the built GameObjects and the ephemeral item
            /// instances are destroyed. No scene, prefab or asset is touched, so a failed pass leaves nothing
            /// behind for the next run.
            /// </summary>
            private void Cleanup()
            {
                if (_cleaned) return;
                _cleaned = true;

                UnhookPlayerDamage();
                UnsubscribeTargets();
                _captured.Clear();
                _targets.Clear();

                for (int i = 0; i < _objects.Count; i++)
                {
                    if (_objects[i] != null) UnityEngine.Object.Destroy(_objects[i]);
                }
                _objects.Clear();

                for (int i = 0; i < _tempItems.Count; i++)
                {
                    if (_tempItems[i] != null) UnityEngine.Object.Destroy(_tempItems[i]);
                }
                _tempItems.Clear();
                EmberdelveTestCatalog.Release(ref _testCatalog);
            }

            // --------------------------------------------- shared scenario plumbing (U2A5-R3)

            /// <summary>Real seconds, spent in real play mode, so an authority clock has genuinely run out
            /// before the next cast. Finite: the loop is bounded by a frame guard as well as by Time.time.</summary>
            private static IEnumerator WaitSeconds(float seconds)
            {
                float until = Time.time + Mathf.Max(0f, seconds);
                int guard = 0;

                while (Time.time < until && guard < 200000)
                {
                    yield return null;
                    guard++;
                }
            }

            /// <summary>Waits for one real attack to come back out of the authority as a captured DamageInfo.</summary>
            private IEnumerator WaitForCaptured(float seconds)
            {
                float until = Time.time + Mathf.Max(0.1f, seconds);
                int guard = 0;

                while (_captured.Count == 0 && Time.time < until && guard < 200000)
                {
                    yield return null;
                    guard++;
                }
            }

            /// <summary>
            /// Battle Cry carries an authored 14 s cooldown and LocalCombatAuthority stamps its own clock for
            /// (warrior, warrior_battle_cry) the moment a hit of an activation is accepted, refusing a repeat
            /// cast that arrives sooner than that cooldown minus its 0.05 s tolerance. SkillCaster
            /// .HandleKitRestored only clears the caster's local prediction, so the pass waits the authored
            /// cooldown in real time instead of asking the runtime to break its own rule.
            /// </summary>
            private IEnumerator SpaceForNextCryCast(SkillData cry)
            {
                if (_cryCastsTaken <= 0 || cry == null) yield break;
                yield return WaitSeconds(cry.EffectiveCooldown + AuthorityClockSlack);
            }

            /// <summary>
            /// Wears exactly the CHA gear a scenario needs and nothing else. The authored item band is
            /// -10..+10 per item, so a +20 experiment is two charms. UnequipAll runs first, which is also what
            /// keeps a removed charm from leaving a residue in the next measurement.
            /// </summary>
            private void EquipCharisma(PlayerRig rig, int bonus, string idPrefix)
            {
                UnequipAll(rig);
                if (rig == null || bonus == 0) return;

                if (bonus > 10)
                {
                    EquipCharm(rig, idPrefix + "_head", EquipmentSlot.Head, 10);
                    EquipCharm(rig, idPrefix + "_chest", EquipmentSlot.Chest, bonus - 10);
                    return;
                }

                EquipCharm(rig, idPrefix, EquipmentSlot.Head, bonus);
            }

            /// <summary>One host standing at a fixed ground depth in front of a rig, on the plane the shot or
            /// swing is measured on.</summary>
            private TestTarget BuildHostAlongAim(string name, PlayerRig rig, Vector3 origin, Vector3 aim, float distance)
            {
                Vector3 flat = new Vector3(aim.x, 0f, aim.z);
                if (flat.sqrMagnitude < 0.0001f) flat = Vector3.forward;
                flat.Normalize();

                Vector3 position = Flat(origin + flat * distance) + new Vector3(0f, rig.transform.position.y + 0.9f, 0f);
                return BuildTarget(name, position);
            }

            // ---------------------------------- the warrior's ordinary sword swing (U2A5-R3)

            /// <summary>
            /// The warrior's ordinary attack, driven through the real MeleeCombat state machine (BeginSwing ->
            /// windup -> authority TryBeginMeleeSwing -> swept sweep through the host hit volumes) and
            /// asserted from the DamageInfo the authority actually produced, captured on the host before it
            /// reaches the health pool. Docs/Design/ATTRIBUTES.md gives CHA no part in an ordinary attack, so
            /// 26 base x 1.12 (STR 16 -> modifier 3 -> 1 + 0.04*3) x 1.2 (Ashen Might) = 34.944 has to come out
            /// the same way at CHA 8, 10, 18 and 30.
            /// </summary>
            private IEnumerator RunWarriorOrdinaryAttackUnchanged()
            {
                PlayerRig rig = _warrior;
                MeleeCombat melee = rig.GetComponent<MeleeCombat>();

                UnequipAll(rig);
                rig.ClearStatuses();
                rig.SetGuardHeld(false);

                Check(melee != null, "the warrior rig carries the MeleeCombat component a sword swing runs through");
                Check(melee != null && melee.enabled, "MeleeCombat is live because the class put a melee weapon in the main hand");
                Check(rig.MainHandWeapon == _sword, "the Ashen Short Sword the class asset authored is what the warrior holds");
                Check(_sword.DamageScale == WeaponDamageScale.Melee, "the sword scales off STR from its own definition, not from a name");
                CheckClose("warrior outgoing multiplier with no CHA gear worn", 1.344f, rig.GetOutgoingDamageMultiplier(_sword),
                    "STR 1.12 x Ashen Might 1.2 x no status: the ordinary damage picture before any CHA experiment");

                yield return SwingWarriorSword("warrior sword at CHA 10 (ability scale 1.00)", 0, 10, 1f);
                yield return SwingWarriorSword("warrior sword at CHA 18 (ability scale 1.16)", 8, 18, 1.16f);
                yield return SwingWarriorSword("warrior sword at CHA 8 (ability scale 0.96)", -2, 8, 0.96f);
                yield return SwingWarriorSword("warrior sword at CHA 30 (ability scale 1.40)", 20, 30, 1.4f);
                yield return SwingWarriorSword("warrior sword again at CHA 10 (nothing stacked)", 0, 10, 1f);

                UnequipAll(rig);
                DeactivateTargets();
                yield return WaitFrames(1);
            }

            private IEnumerator SwingWarriorSword(string label, int charmBonus, int expectedCha, float expectedScale)
            {
                const float expected = 34.944f;

                PlayerRig rig = _warrior;
                MeleeCombat melee = rig.GetComponent<MeleeCombat>();

                DeactivateTargets();
                EquipCharisma(rig, charmBonus, "u2a5_warrior_sword_gear");
                rig.Movement.CancelDash();
                rig.ClearStatuses();
                rig.SetGuardHeld(false);
                rig.transform.position = _warriorSpawn;
                rig.transform.rotation = Quaternion.LookRotation(Vector3.forward);
                rig.Skills.HandleKitRestored();
                rig.Mana.RefillToMax();

                TestTarget host = BuildTarget("U2A5_Sword_Host_" + _checks, _warriorSpawn + new Vector3(0f, 0.9f, MeleeHostDepth));
                Physics.SyncTransforms();
                yield return WaitFrames(2);

                CheckInt($"{label}: CHA score", expectedCha, rig.AttributeStats.GetAttribute(SixAttribute.Charisma),
                    "pristine class 10 plus the charm, recomputed from the class base every time");
                CheckClose($"{label}: ability scale query", expectedScale, rig.AttributeStats.GetAbilityEffectScale(),
                    "clamp(1 + 0.04 * CHA modifier, 0.6, 1.4)");
                CheckClose($"{label}: outgoing multiplier the authority reads", 1.344f, rig.GetOutgoingDamageMultiplier(_sword),
                    "identical at every CHA level: CHA is not part of an ordinary attack");
                Check(!rig.IsGuarding, $"{label}: the warrior is not guarding, so no block reduction can hide inside the number");

                ClearCaptured();
                float hostHealthBefore = host.CurrentHealth;
                int acceptedBefore = _authority.AcceptedCount;
                int rejectedBefore = _authority.RejectedCount;

                melee.BeginSwing();
                Check(melee.IsSwinging, $"{label}: MeleeCombat reports its swing state machine running");

                yield return WaitForCaptured(ResolveTimeout);

                Check(_captured.Count == 1, $"{label}: the swing resolved as exactly one DamageInfo from the authority (got {_captured.Count})");

                if (_captured.Count > 0)
                {
                    DamageInfo damage = _captured[0];
                    CheckClose($"{label}: resolved damage", expected, damage.Amount,
                        "26 * 1.12 (STR modifier 3) * 1.2 (Ashen Might), the same whatever CHA is worn");
                    Check(damage.SourceCombatId == rig.CombatId && damage.SourceKind == EntityKind.LocalPlayer,
                        $"{label}: attributed to the warrior that swung it ({damage.SourceKind} #{damage.SourceCombatId})");
                    Check(damage.Type == _sword.damageType, $"{label}: the damage type still comes from the sword definition ({damage.Type})");
                    Check(!damage.Headshot, $"{label}: this host has no head collider, so the 1.8x headshot factor is not in the number");
                    Check(!damage.Blocked && damage.BlockReduction <= 0f, $"{label}: an unguarded hit took no block reduction");
                    CheckClose($"{label}: host health delta", expected, hostHealthBefore - host.CurrentHealth,
                        "the number the authority resolved is the number the pool actually lost");
                }

                Check(_authority.AcceptedCount == acceptedBefore + 1,
                    $"{label}: the authority accepted exactly one swing ({acceptedBefore} -> {_authority.AcceptedCount})");
                Check(_authority.RejectedCount == rejectedBefore,
                    $"{label}: nothing was refused ({rejectedBefore} -> {_authority.RejectedCount})");
                Check(_sword.baseDamage == 26f && _sword.reach == 2.3f && _sword.hitSphereRadius == 0.6f,
                    $"{label}: the sword definition still holds its authored numbers");

                Debug.Log($"{Tag} {label}: cha={rig.AttributeStats.GetAttribute(SixAttribute.Charisma)} " +
                          $"scale={rig.AttributeStats.GetAbilityEffectScale():F4} damage={(hostHealthBefore - host.CurrentHealth):F4} " +
                          $"authority(accepted={_authority.AcceptedCount}, rejected={_authority.RejectedCount})");

                yield return WaitSeconds(WeaponAttackSpacing);
            }

            // ------------------------------------- an enemy instigator against the warrior (U2A5-R3)

            /// <summary>
            /// A real Hollow Servitor, built the way the level builds one (real EnemyAgent + EnemyData +
            /// NavGridData + GameConfig through the same serialized wiring the level builder uses), walking,
            /// seeing, winding up and submitting its own attack through the authority. The damage it deals is
            /// the authored 15 of its claw at every CHA level the victim wears, because the authority reads the
            /// outgoing multipliers from the instigator and an enemy has no six-attribute component at all.
            /// </summary>
            private IEnumerator RunEnemyAttackUnchanged()
            {
                PlayerRig rig = _warrior;
                HookPlayerDamage();

                UnequipAll(rig);
                DeactivateTargets();
                rig.ClearStatuses();
                rig.SetGuardHeld(false);
                rig.transform.position = _warriorSpawn;
                rig.transform.rotation = Quaternion.LookRotation(Vector3.forward);

                GameObject enemyObject = new GameObject("U2A5_Enemy_Servitor");
                enemyObject.transform.position = _warriorSpawn + new Vector3(0f, 0f, EnemyStandoff);
                enemyObject.transform.rotation = Quaternion.LookRotation(-Vector3.forward);
                int enemyLayer = LayerMask.NameToLayer(GameConfig.EnemyLayerName);
                if (enemyLayer >= 0) enemyObject.layer = enemyLayer;
                _objects.Add(enemyObject);

                enemyObject.AddComponent<HealthComponent>();
                _enemy = enemyObject.AddComponent<EnemyAgent>();
                SetReference(_enemy, "data", _servitor);
                SetReference(_enemy, "navGrid", _navGrid);
                SetReference(_enemy, "config", _config);
                Physics.SyncTransforms();
                yield return WaitFrames(3);

                Check(_enemy != null && !_enemy.IsDead, "the Hollow Servitor is a live monster-team combat entity");
                Check(_servitor.attackWeapon == _claw, "the enemy attacks with the authored Hollow Claw the gate also loaded");
                CheckClose("the enemy's outgoing multiplier is status-only (1)", 1f, _enemy.GetOutgoingDamageMultiplier(_claw),
                    "an entity with no six-attribute component contributes no attribute factor at all");

                yield return TakeEnemyHit("servitor claw against the warrior at CHA 10", 0, 10, 1f);
                yield return TakeEnemyHit("servitor claw against the warrior at CHA 18 (CHA gear worn)", 8, 18, 1.16f);
                yield return TakeEnemyHit("servitor claw against the warrior at CHA 8 (cursed CHA gear)", -2, 8, 0.96f);

                UnequipAll(rig);
                if (_enemy != null) _enemy.enabled = false;
                enemyObject.SetActive(false);
                yield return WaitFrames(1);
            }

            private IEnumerator TakeEnemyHit(string label, int charmBonus, int expectedCha, float expectedScale)
            {
                PlayerRig rig = _warrior;

                if (_enemy != null) _enemy.enabled = true;

                DeactivateTargets();
                EquipCharisma(rig, charmBonus, "u2a5_warrior_enemy_gear");
                rig.ClearStatuses();
                rig.SetGuardHeld(false);
                rig.transform.position = _warriorSpawn;
                rig.transform.rotation = Quaternion.LookRotation(Vector3.forward);
                rig.Mana.RefillToMax();
                Physics.SyncTransforms();
                yield return WaitFrames(2);

                CheckInt($"{label}: CHA score", expectedCha, rig.AttributeStats.GetAttribute(SixAttribute.Charisma),
                    "the victim's own CHA, which the authority never consults for incoming damage");
                CheckClose($"{label}: ability scale query", expectedScale, rig.AttributeStats.GetAbilityEffectScale(),
                    "clamp(1 + 0.04 * CHA modifier, 0.6, 1.4)");
                CheckClose($"{label}: the victim's own outgoing multiplier", 1.344f, rig.GetOutgoingDamageMultiplier(_sword),
                    "unchanged by its CHA gear, so nothing about the victim rescales the hit it receives");
                Check(!rig.IsGuarding, $"{label}: the warrior is not guarding, so no block reduction is folded into the incoming number");

                ClearCaptured();
                int acceptedBefore = _authority.AcceptedCount;
                int rejectedBefore = _authority.RejectedCount;
                float healthBefore = rig.CurrentHealth;

                yield return WaitForCaptured(ResolveTimeout);

                if (_enemy != null) _enemy.enabled = false;

                float delta = healthBefore - rig.CurrentHealth;

                Check(_captured.Count >= 1, $"{label}: the enemy's swing came back through GameEvents.PlayerDamaged");
                CheckClose($"{label}: incoming health loss", 15f, delta,
                    "the authored 15 of the Hollow Claw, applied once, whatever CHA the victim wears");
                Check(_authority.AcceptedCount == acceptedBefore + 1,
                    $"{label}: the authority accepted exactly one enemy attack ({acceptedBefore} -> {_authority.AcceptedCount})");
                Check(_authority.RejectedCount == rejectedBefore,
                    $"{label}: the enemy's attack was not refused ({rejectedBefore} -> {_authority.RejectedCount})");

                if (_captured.Count > 0)
                {
                    DamageInfo damage = _captured[0];
                    Check(damage.SourceKind == EntityKind.Enemy && _enemy != null && damage.SourceCombatId == _enemy.CombatId,
                        $"{label}: attributed to the enemy agent that swung it ({damage.SourceKind} #{damage.SourceCombatId})");
                    Check(damage.Type == _claw.damageType, $"{label}: the damage type still comes from the claw definition ({damage.Type})");
                    CheckClose($"{label}: resolved damage", 15f, damage.Amount, "15 base damage, no attribute factor, no headshot, no block");
                    Check(!damage.Blocked && !damage.Headshot && damage.BlockReduction <= 0f,
                        $"{label}: unguarded body hit, so neither a block nor a headshot touched it");
                }

                Debug.Log($"{Tag} {label}: victimCHA={rig.AttributeStats.GetAttribute(SixAttribute.Charisma)} " +
                          $"scale={rig.AttributeStats.GetAbilityEffectScale():F4} incoming={delta:F4} " +
                          $"authority(accepted={_authority.AcceptedCount}, rejected={_authority.RejectedCount})");

                yield return WaitFrames(2);
            }

            // --------------------------------------------- the archer's projectile skills (U2A5-R3)

            /// <summary>
            /// Multi-shot through the real kit: SkillCaster.TryCastSlot -> MultiShotBehavior -> RangedCombat
            /// magazine -> RangedSkillUtility.FireOne -> authority-owned projectile flight -> DamageResolver.
            /// Each of the three authored arrows resolves its own live flight, sequence and impact, and carries the cast's CHA scale exactly
            /// once: 34 flat-band bow damage x 0.7 skill multiplier x 1.3216 instigator multipliers (DEX 1.12 x
            /// Farshot 1.18) x the scale. Count, spread, range, ammo, mana and cooldown are identical at 0.96,
            /// 1.00 and 1.16.
            /// </summary>
            private IEnumerator RunMultiShotFlow()
            {
                SkillData multi = _archer.Skills.ActiveSkill(0);

                Check(multi != null && multi.skillId == "archer_multi_shot", $"archer active 1 is Multi-shot ({SkillName(multi)})");
                CheckInt("Multi-shot authored projectile count", 3, multi.projectileCount, "three arrows, and CHA never changes it");
                CheckClose("Multi-shot authored spread", 26f, multi.spreadDegrees, "the fan angle is authored data");
                CheckClose("Multi-shot authored weapon multiplier", 0.7f, multi.damageMultiplier, "each arrow carries 0.7 of the bow");
                CheckInt("Multi-shot authored mana cost", 26, multi.EffectiveManaCost, "cost is not attribute data");
                CheckClose("Multi-shot authored cooldown", 9f, multi.EffectiveCooldown, "cooldown is not attribute data");
                CheckInt("Multi-shot targets per projectile", 1, multi.projectileMaxTargets, "one arrow, one body");
                Check(_archerRanged != null && _archerRanged.enabled, "RangedCombat is live because the class put a bow in the main hand");
                Check(_archer.MainHandWeapon == _bow, "the Hollow Bow the class asset authored is what the archer holds");
                Check(_bow.DamageScale == WeaponDamageScale.Ranged, "the bow scales off DEX from its own definition");

                yield return CastMultiShot("archer CHA 8 (scale 0.96, the class's own CHA)", 0, 8, 0.96f, 30.1959168f);
                yield return CastMultiShot("archer CHA 10 (scale 1.00)", 2, 10, 1f, 31.45408f);
                yield return CastMultiShot("archer CHA 18 (scale 1.16)", 10, 18, 1.16f, 36.4867328f);

                CheckInt("Multi-shot projectile count still authored after every scaled cast", 3, multi.projectileCount, "shared SkillData never mutated");
                CheckClose("Multi-shot spread still authored after every scaled cast", 26f, multi.spreadDegrees, "shared SkillData never mutated");
                CheckClose("Multi-shot weapon multiplier still authored", 0.7f, multi.damageMultiplier, "shared SkillData never mutated");
                CheckClose("Multi-shot mana cost still authored", 26f, multi.EffectiveManaCost, "shared SkillData never mutated");
                CheckClose("Multi-shot cooldown still authored", 9f, multi.EffectiveCooldown, "shared SkillData never mutated");

                DeactivateTargets();
            }

            private IEnumerator CastMultiShot(string label, int charmBonus, int expectedCha, float expectedScale, float expectedArrow)
            {
                PlayerRig rig = _archer;
                SkillCaster caster = rig.Skills;
                SkillData multi = caster.ActiveSkill(0);
                RangedCombat ranged = _archerRanged;
                float unscaled = _bow.baseDamage * multi.damageMultiplier * 1.3216f;

                DeactivateTargets();
                EquipCharisma(rig, charmBonus, "u2a5_archer_multi_gear");
                rig.ClearStatuses();
                rig.transform.position = _archerSpawn;
                rig.transform.rotation = Quaternion.LookRotation(Vector3.forward);
                caster.HandleKitRestored();
                rig.Mana.RefillToMax();
                ranged.Initialize(_bow, _archerClass.startingReserveAmmo, _archerClass.maxReserveAmmo, -1);

                Vector3 muzzle = rig.GetMuzzlePosition();
                Vector3 aim = rig.AimDirection;
                int shots = multi.projectileCount;

                // Fixture arithmetic, not a mirror of the runtime: one host per authored arrow, laid out on the
                // fan the skill asset itself declares (spreadDegrees across the whole volley, so arrow i sits at
                // (i / (count - 1) - 0.5) * spreadDegrees off the aim line). Each host is placed on that arrow's
                // own ray at ShotDistance, which is inside the bow's 18 m falloff start so the damage number
                // this pass asserts is the flat band value and not a distance-dependent one.
                List<TestTarget> volley = new List<TestTarget>(shots);
                for (int i = 0; i < shots; i++)
                {
                    float angle = shots <= 1 ? 0f : (i / (shots - 1f) - 0.5f) * multi.spreadDegrees;
                    Vector3 direction = Quaternion.AngleAxis(angle, Vector3.up) * aim;
                    volley.Add(BuildHostAlongAim($"U2A5_Multi_Host_{_checks}_{i}", rig, muzzle, direction, ShotDistance));
                }

                Physics.SyncTransforms();
                yield return WaitFrames(2);

                CheckInt($"{label}: CHA score", expectedCha, rig.AttributeStats.GetAttribute(SixAttribute.Charisma),
                    "authored 8 plus the charm, recomputed from the class base");
                CheckClose($"{label}: ability scale query", expectedScale, rig.AttributeStats.GetAbilityEffectScale(),
                    "clamp(1 + 0.04 * CHA modifier, 0.6, 1.4)");
                CheckClose($"{label}: outgoing multiplier the authority reads", 1.3216f, rig.GetOutgoingDamageMultiplier(_bow),
                    "DEX 1.12 x Farshot 1.18, identical at every CHA level: CHA cannot ride in through it twice");

                float manaBefore = rig.Mana.CurrentMana;
                int magazineBefore = ranged.Magazine;
                int acceptedBefore = _authority.AcceptedCount;
                int rejectedBefore = _authority.RejectedCount;

                float[] healthBefore = new float[volley.Count];
                for (int i = 0; i < volley.Count; i++) healthBefore[i] = volley[i].CurrentHealth;

                ClearCaptured();

                Check(caster.TryCastSlot(0), $"{label}: Multi-shot accepted through SkillCaster.TryCastSlot");
                CheckClose($"{label}: Multi-shot spent exactly its authored mana", multi.EffectiveManaCost,
                    manaBefore - rig.Mana.CurrentMana, "effect_scale never scales mana cost");
                CheckClose($"{label}: Multi-shot started its authored cooldown", multi.EffectiveCooldown,
                    caster.CooldownRemaining(0), "effect_scale never scales cooldown");
                CheckInt($"{label}: Multi-shot spent exactly its authored arrows", shots, magazineBefore - ranged.Magazine,
                    "projectile count is not scaled by CHA");

                // Step the authority-owned flights before changing any target, aim or gear in this fixture.
                _authority.AdvanceProjectiles(_bow.rangedRange / Mathf.Max(2f, _bow.projectileSpeed) + 0.02f);

                Check(_captured.Count == shots, $"{label}: {shots} arrows resolved as {shots} DamageInfo entries (got {_captured.Count})");
                Check(_authority.AcceptedCount == acceptedBefore + shots,
                    $"{label}: the authority accepted one hit per arrow ({acceptedBefore} -> {_authority.AcceptedCount})");
                Check(_authority.RejectedCount == rejectedBefore,
                    $"{label}: no arrow of the volley was refused ({rejectedBefore} -> {_authority.RejectedCount})");

                for (int i = 0; i < volley.Count; i++)
                {
                    TestTarget host = volley[i];
                    CheckClose($"{label}: arrow {i} health delta", expectedArrow, healthBefore[i] - host.CurrentHealth,
                        $"34 x 0.7 x 1.3216 x {expectedScale:F2}, applied once to this host and to no other");
                    Check(!host.HasStatus(StatusKind.Slow) && !host.HasStatus(StatusKind.Empowered),
                        $"{label}: arrow {i} carries no status (the skill asset applies none, and CHA adds none)");
                }

                for (int i = 0; i < _captured.Count; i++)
                {
                    DamageInfo damage = _captured[i];
                    CheckClose($"{label}: captured arrow {i} amount", expectedArrow, damage.Amount,
                        "the authority's own resolved number, weapon falloff x skill multiplier x instigator multipliers x cast scale");
                    Check(damage.SourceKind == EntityKind.LocalPlayer && damage.SourceCombatId == rig.CombatId,
                        $"{label}: arrow {i} attributed to the archer ({damage.SourceKind} #{damage.SourceCombatId})");
                    Check(damage.Type == _bow.damageType, $"{label}: arrow {i} damage type still comes from the bow ({damage.Type})");
                    Check(!damage.Headshot && !damage.Blocked, $"{label}: arrow {i} is an unblocked body hit on a host with no head collider");
                }

                if (!Mathf.Approximately(expectedScale, 1f))
                {
                    Check(Mathf.Abs(expectedArrow - unscaled) > 0.5f,
                        $"{label}: the arrow is NOT the unscaled number ({expectedArrow:F4} against {unscaled:F4})");
                    Check(Mathf.Abs(expectedArrow - unscaled * expectedScale * expectedScale) > 0.5f,
                        $"{label}: the arrow is NOT the scale applied twice " +
                        $"({expectedArrow:F4} against {unscaled * expectedScale * expectedScale:F4})");
                }

                Debug.Log($"{Tag} {label}: scale={expectedScale:F4} arrow={expectedArrow:F4} mana={manaBefore - rig.Mana.CurrentMana:F0} " +
                          $"arrowsSpent={magazineBefore - ranged.Magazine} cooldown={caster.CooldownRemaining(0):F2} " +
                          $"authority(accepted={_authority.AcceptedCount}, rejected={_authority.RejectedCount})");

                DeactivateTargets();
                yield return WaitSeconds(multi.EffectiveCooldown + AuthorityClockSlack);
            }

            /// <summary>
            /// Piercing Shot through the same real path: one authored arrow, one ray, up to the authored two
            /// bodies, each hit resolved by the authority on its own merits and each carrying the cast's scale
            /// once through 34 x 1.35 x 1.3216 x scale. A third host standing behind the second proves the
            /// two-body cap is the skill's own data and not a range or line-of-sight accident: it sits at the
            /// same flat-band distance as the other two and takes nothing.
            /// </summary>
            private IEnumerator RunPiercingFlow()
            {
                SkillData pierce = _archer.Skills.ActiveSkill(1);

                Check(pierce != null && pierce.skillId == "archer_piercing_shot", $"archer active 2 is Piercing Shot ({SkillName(pierce)})");
                CheckInt("Piercing Shot authored targets per projectile", 2, pierce.projectileMaxTargets, "one arrow through two bodies, and CHA never changes it");
                CheckInt("Piercing Shot authored projectile count", 1, pierce.projectileCount, "one arrow");
                CheckClose("Piercing Shot authored weapon multiplier", 1.35f, pierce.damageMultiplier, "the heavy arrow carries 1.35 of the bow");
                CheckInt("Piercing Shot authored mana cost", 22, pierce.EffectiveManaCost, "cost is not attribute data");
                CheckClose("Piercing Shot authored cooldown", 11f, pierce.EffectiveCooldown, "cooldown is not attribute data");

                yield return CastPiercing("archer CHA 8 (scale 0.96, the class's own CHA)", 0, 8, 0.96f, 58.2349824f);
                yield return CastPiercing("archer CHA 10 (scale 1.00)", 2, 10, 1f, 60.66144f);
                yield return CastPiercing("archer CHA 18 (scale 1.16)", 10, 18, 1.16f, 70.3672704f);

                CheckInt("Piercing Shot target cap still authored after every scaled cast", 2, pierce.projectileMaxTargets, "shared SkillData never mutated");
                CheckClose("Piercing Shot weapon multiplier still authored", 1.35f, pierce.damageMultiplier, "shared SkillData never mutated");

                DeactivateTargets();
            }

            private IEnumerator CastPiercing(string label, int charmBonus, int expectedCha, float expectedScale, float expectedHit)
            {
                PlayerRig rig = _archer;
                SkillCaster caster = rig.Skills;
                SkillData pierce = caster.ActiveSkill(1);
                RangedCombat ranged = _archerRanged;
                float unscaled = _bow.baseDamage * pierce.damageMultiplier * 1.3216f;

                DeactivateTargets();
                EquipCharisma(rig, charmBonus, "u2a5_archer_pierce_gear");
                rig.ClearStatuses();
                rig.transform.position = _archerSpawn;
                rig.transform.rotation = Quaternion.LookRotation(Vector3.forward);
                caster.HandleKitRestored();
                rig.Mana.RefillToMax();
                ranged.Initialize(_bow, _archerClass.startingReserveAmmo, _archerClass.maxReserveAmmo, -1);

                Vector3 muzzle = rig.GetMuzzlePosition();
                Vector3 aim = rig.AimDirection;

                TestTarget first = BuildHostAlongAim($"U2A5_Pierce_First_{_checks}", rig, muzzle, aim, 8f);
                TestTarget second = BuildHostAlongAim($"U2A5_Pierce_Second_{_checks}", rig, muzzle, aim, 11f);
                TestTarget third = BuildHostAlongAim($"U2A5_Pierce_BeyondCap_{_checks}", rig, muzzle, aim, 14f);

                Physics.SyncTransforms();
                yield return WaitFrames(2);

                CheckInt($"{label}: CHA score", expectedCha, rig.AttributeStats.GetAttribute(SixAttribute.Charisma), "authored 8 plus the charm");
                CheckClose($"{label}: ability scale query", expectedScale, rig.AttributeStats.GetAbilityEffectScale(), "clamp(1 + 0.04 * CHA modifier, 0.6, 1.4)");
                CheckClose($"{label}: outgoing multiplier the authority reads", 1.3216f, rig.GetOutgoingDamageMultiplier(_bow),
                    "DEX 1.12 x Farshot 1.18, identical at every CHA level");

                float manaBefore = rig.Mana.CurrentMana;
                int magazineBefore = ranged.Magazine;
                int acceptedBefore = _authority.AcceptedCount;
                int rejectedBefore = _authority.RejectedCount;

                float firstHealth = first.CurrentHealth;
                float secondHealth = second.CurrentHealth;
                float thirdHealth = third.CurrentHealth;

                ClearCaptured();

                Check(caster.TryCastSlot(1), $"{label}: Piercing Shot accepted through SkillCaster.TryCastSlot");
                _authority.AdvanceProjectiles(_bow.rangedRange / Mathf.Max(2f, _bow.projectileSpeed) + 0.02f);
                CheckClose($"{label}: Piercing Shot spent exactly its authored mana", pierce.EffectiveManaCost,
                    manaBefore - rig.Mana.CurrentMana, "effect_scale never scales mana cost");
                CheckClose($"{label}: Piercing Shot started its authored cooldown", pierce.EffectiveCooldown,
                    caster.CooldownRemaining(1), "effect_scale never scales cooldown");
                CheckInt($"{label}: Piercing Shot spent exactly one arrow", 1, magazineBefore - ranged.Magazine, "one projectile whatever CHA says");

                Check(_captured.Count == pierce.projectileMaxTargets,
                    $"{label}: one arrow resolved as exactly {pierce.projectileMaxTargets} hits (got {_captured.Count})");
                Check(_authority.AcceptedCount == acceptedBefore + 1,
                    $"{label}: the authority accepted one piercing projectile launch; captured impact events verify each body ({acceptedBefore} -> {_authority.AcceptedCount})");
                Check(_authority.RejectedCount == rejectedBefore,
                    $"{label}: neither body was refused ({rejectedBefore} -> {_authority.RejectedCount})");

                CheckClose($"{label}: first body health delta", expectedHit, firstHealth - first.CurrentHealth,
                    $"34 x 1.35 x 1.3216 x {expectedScale:F2}, once");
                CheckClose($"{label}: second body health delta", expectedHit, secondHealth - second.CurrentHealth,
                    "the same arrow's second body, hit once on the way through and not twice");
                CheckClose($"{label}: third body took nothing", 0f, thirdHealth - third.CurrentHealth,
                    "the authored two-target cap stopped the arrow, not range or line of sight (it stands at 14 m, inside the same flat band)");

                for (int i = 0; i < _captured.Count; i++)
                {
                    DamageInfo damage = _captured[i];
                    CheckClose($"{label}: captured body {i} amount", expectedHit, damage.Amount, "the authority's own resolved number");
                    Check(damage.SourceKind == EntityKind.LocalPlayer && damage.SourceCombatId == rig.CombatId,
                        $"{label}: body {i} attributed to the archer ({damage.SourceKind} #{damage.SourceCombatId})");
                    Check(damage.Type == _bow.damageType, $"{label}: body {i} damage type still comes from the bow ({damage.Type})");
                    Check(!damage.Headshot && !damage.Blocked, $"{label}: body {i} is an unblocked body hit");
                }

                if (!Mathf.Approximately(expectedScale, 1f))
                {
                    Check(Mathf.Abs(expectedHit - unscaled) > 0.5f,
                        $"{label}: the hit is NOT the unscaled number ({expectedHit:F4} against {unscaled:F4})");
                    Check(Mathf.Abs(expectedHit - unscaled * expectedScale * expectedScale) > 0.5f,
                        $"{label}: the hit is NOT the scale applied twice ({expectedHit:F4} against {unscaled * expectedScale * expectedScale:F4})");
                }

                Debug.Log($"{Tag} {label}: scale={expectedScale:F4} hit={expectedHit:F4} mana={manaBefore - rig.Mana.CurrentMana:F0} " +
                          $"arrowsSpent={magazineBefore - ranged.Magazine} cooldown={caster.CooldownRemaining(1):F2} " +
                          $"authority(accepted={_authority.AcceptedCount}, rejected={_authority.RejectedCount})");

                DeactivateTargets();
                yield return WaitSeconds(pierce.EffectiveCooldown + AuthorityClockSlack);
            }

            // ------------------------------------------ the archer's ordinary attacks (U2A5-R3)

            /// <summary>
            /// The archer's ordinary attacks, both of them, at CHA 8 / 10 / 18: a full-charge bow shot, a
            /// half-charge bow shot, a shot out into the bow's authored falloff ramp, and a swing with the
            /// finesse secondary the class asset authors. They run through the real RangedCombat.Fire and the
            /// real MeleeCombat swing state machine, and every one of them keeps its own authored factors -
            /// DEX 1.12, Farshot 1.18, the charge lerp, the distance falloff, the finesse pick of the better of
            /// STR 0 and DEX 3 - and gains nothing from CHA.
            /// </summary>
            private IEnumerator RunArcherOrdinaryAttackUnchanged()
            {
                PlayerRig rig = _archer;

                yield return ShootArcherBow("archer full-charge bow shot at 10 m, CHA 8 (the class's own CHA)",
                    0, 8, 0.96f, 1f, 44.9344f, ShotDistance);
                yield return ShootArcherBow("archer full-charge bow shot at 10 m, CHA 10", 2, 10, 1f, 1f, 44.9344f, ShotDistance);
                yield return ShootArcherBow("archer full-charge bow shot at 10 m, CHA 18", 10, 18, 1.16f, 1f, 44.9344f, ShotDistance);

                yield return ShootArcherBow("archer half-charge bow shot at 10 m, CHA 10", 2, 10, 1f, 0.5f, 31.45408f, ShotDistance);
                yield return ShootArcherBow("archer half-charge bow shot at 10 m, CHA 18", 10, 18, 1.16f, 0.5f, 31.45408f, ShotDistance);

                yield return ShootArcherBow("archer bow shot at 25 m (inside the authored falloff ramp), CHA 10",
                    2, 10, 1f, 1f, 38.97658f, 25f);
                yield return ShootArcherBow("archer bow shot at 25 m (inside the authored falloff ramp), CHA 18",
                    10, 18, 1.16f, 1f, 38.97658f, 25f);

                yield return SwingArcherRapier("archer finesse rapier swing at CHA 8", 0, 8, 0.96f);
                yield return SwingArcherRapier("archer finesse rapier swing at CHA 10", 2, 10, 1f);
                yield return SwingArcherRapier("archer finesse rapier swing at CHA 18", 10, 18, 1.16f);

                Check(rig.Loadout.SwitchToSlot(0), "the archer is left holding the bow the class asset authored");
                Check(rig.MainHandWeapon == _bow, "the bow is back in the archer's main hand");

                UnequipAll(rig);
                DeactivateTargets();
                yield return WaitFrames(1);
            }

            private IEnumerator ShootArcherBow(string label, int charmBonus, int expectedCha, float expectedScale,
                float charge, float expectedDamage, float distance)
            {
                PlayerRig rig = _archer;
                RangedCombat ranged = _archerRanged;

                DeactivateTargets();
                EquipCharisma(rig, charmBonus, "u2a5_archer_bow_gear");
                rig.ClearStatuses();
                rig.transform.position = _archerSpawn;
                rig.transform.rotation = Quaternion.LookRotation(Vector3.forward);
                rig.Skills.HandleKitRestored();
                rig.Mana.RefillToMax();
                ranged.Initialize(_bow, _archerClass.startingReserveAmmo, _archerClass.maxReserveAmmo, -1);

                TestTarget host = BuildHostAlongAim($"U2A5_Bow_Host_{_checks}", rig, rig.GetMuzzlePosition(), rig.AimDirection, distance);
                Physics.SyncTransforms();
                yield return WaitFrames(2);

                CheckInt($"{label}: CHA score", expectedCha, rig.AttributeStats.GetAttribute(SixAttribute.Charisma),
                    "authored 8 plus the charm, recomputed from the class base");
                CheckClose($"{label}: ability scale query", expectedScale, rig.AttributeStats.GetAbilityEffectScale(),
                    "clamp(1 + 0.04 * CHA modifier, 0.6, 1.4)");
                CheckClose($"{label}: outgoing multiplier the authority reads", 1.3216f, rig.GetOutgoingDamageMultiplier(_bow),
                    "DEX 1.12 x Farshot 1.18: the same number at CHA 8, 10 and 18, so CHA is not in it");

                // Predict both independent collision representations. The authority orders its analytic
                // capsule and physical collider by swept projectile-centre travel, then uses the nearer one.
                Vector3 muzzle = rig.GetMuzzlePosition();
                Vector3 shotDirection = rig.AimDirection.normalized;
                Check(Mathf.Abs(shotDirection.y) < 0.001f, $"{label}: ranged fixture aim remains horizontal for capsule formula");
                float collisionRadius = Mathf.Max(0.01f, _bow.projectileRadius * 0.5f);
                Vector3 expectedPoint = host.CombatCenter;
                float expectedTravel = float.PositiveInfinity;
                if (Physics.SphereCast(muzzle, collisionRadius, shotDirection, out RaycastHit probe,
                        distance + 5f, ~0, QueryTriggerInteraction.Ignore)
                    && probe.collider != null && probe.collider.GetComponentInParent<TestTarget>() == host)
                {
                    expectedPoint = probe.point;
                    expectedTravel = probe.distance;
                }

                float capsuleCenterY = Mathf.Clamp(muzzle.y,
                    host.transform.position.y - 0.4f, host.transform.position.y + 0.4f);
                Vector3 capsuleAxisPoint = new Vector3(host.transform.position.x, capsuleCenterY,
                    host.transform.position.z);
                Vector3 toCapsule = capsuleAxisPoint - muzzle;
                float along = Vector3.Dot(toCapsule, shotDirection);
                Vector3 perpendicular = toCapsule - shotDirection * along;
                float expandedRadius = 0.45f + collisionRadius;
                float radialRemainder = expandedRadius * expandedRadius - perpendicular.sqrMagnitude;
                if (radialRemainder >= 0f)
                {
                    float capsuleTravel = along - Mathf.Sqrt(radialRemainder);
                    if (capsuleTravel >= 0f && capsuleTravel < expectedTravel)
                    {
                        expectedTravel = capsuleTravel;
                        expectedPoint = muzzle + shotDirection * capsuleTravel;
                    }
                }
                float healthBefore = host.CurrentHealth;
                int magazineBefore = ranged.Magazine;
                int acceptedBefore = _authority.AcceptedCount;
                int rejectedBefore = _authority.RejectedCount;

                ClearCaptured();

                ranged.Fire(charge);
                _authority.AdvanceProjectiles(_bow.rangedRange / Mathf.Max(2f, _bow.projectileSpeed) + 0.02f);
                yield return WaitForCaptured(ResolveTimeout);

                Check(_captured.Count == 1, $"{label}: the shot resolved as exactly one DamageInfo from the authority (got {_captured.Count})");

                // R1a: the authority measures falloff from the validated launch muzzle to its ordered impact
                // point (DamageInfo.Point). The expectation below uses an independent physical spherecast and
                // capsule sweep prediction, then retains the existing numeric ramp checks.
                if (distance > _bow.falloffStartRange && _captured.Count > 0)
                {
                    Check(Vector3.Distance(expectedPoint, _captured[0].Point) < 0.05f,
                        $"{label}: the accepted point equals the fixture's own raycast-derived point (off by {Vector3.Distance(expectedPoint, _captured[0].Point):F3} m)");
                    float authoritativeDistance = Vector3.Distance(rig.GetMuzzlePosition(), expectedPoint);
                    float nominal = expectedDamage;
                    expectedDamage = _bow.DamageAtRange(authoritativeDistance, rig.GetFlatWeaponDamageBonus(_bow))
                                     * Mathf.Lerp(_bow.minChargeDamageScale, 1f, charge) * rig.GetOutgoingDamageMultiplier(_bow);
                    Check(Mathf.Abs(authoritativeDistance - distance) < 1f,
                        $"{label}: the authoritative distance {authoritativeDistance:F2} m is within a metre of the nominal {distance:F0} m");
                    Check(Mathf.Abs(expectedDamage - nominal) < 1.2f && expectedDamage < _bow.DamageAtRange(_bow.falloffStartRange) * rig.GetOutgoingDamageMultiplier(_bow) - 0.5f,
                        $"{label}: the falloff expectation ({expectedDamage:F3}) stays near the nominal {nominal:F3} and below the unfalloffed damage");
                }

                CheckClose($"{label}: resolved damage", expectedDamage, healthBefore - host.CurrentHealth,
                    $"charge {charge:F2}: authored falloff band x 1.3216 instigator multipliers, and no CHA term anywhere");
                CheckInt($"{label}: the shot spent exactly one arrow", 1, magazineBefore - ranged.Magazine,
                    "an ordinary shot spends one round whatever CHA says");
                Check(_authority.AcceptedCount == acceptedBefore + 1,
                    $"{label}: the authority accepted exactly one shot ({acceptedBefore} -> {_authority.AcceptedCount})");
                Check(_authority.RejectedCount == rejectedBefore,
                    $"{label}: the shot was not refused ({rejectedBefore} -> {_authority.RejectedCount})");

                if (_captured.Count > 0)
                {
                    DamageInfo damage = _captured[0];
                    CheckClose($"{label}: captured amount", expectedDamage, damage.Amount, "the authority's own resolved number");
                    Check(damage.SourceKind == EntityKind.LocalPlayer && damage.SourceCombatId == rig.CombatId,
                        $"{label}: attributed to the archer ({damage.SourceKind} #{damage.SourceCombatId})");
                    Check(damage.Type == _bow.damageType, $"{label}: damage type still comes from the bow definition ({damage.Type})");
                    Check(!damage.Headshot && !damage.Blocked, $"{label}: an unblocked body hit on a host with no head collider");
                }

                Debug.Log($"{Tag} {label}: scale={expectedScale:F4} charge={charge:F2} damage={(healthBefore - host.CurrentHealth):F4} " +
                          $"magazine={ranged.Magazine} authority(accepted={_authority.AcceptedCount}, rejected={_authority.RejectedCount})");

                yield return WaitSeconds(WeaponAttackSpacing);
            }

            private IEnumerator SwingArcherRapier(string label, int charmBonus, int expectedCha, float expectedScale)
            {
                const float expected = 21.28f;

                PlayerRig rig = _archer;
                MeleeCombat melee = rig.GetComponent<MeleeCombat>();

                DeactivateTargets();
                EquipCharisma(rig, charmBonus, "u2a5_archer_rapier_gear");

                if (!rig.Loadout.UsingSecondary)
                {
                    Check(rig.Loadout.SwitchToSlot(1), $"{label}: the archer loadout switched to its authored secondary");
                }

                WeaponData rapier = rig.MainHandWeapon;
                Check(rapier != null && rapier.finesse && rapier.DamageScale == WeaponDamageScale.Finesse,
                    $"{label}: the weapon in hand is marked finesse by its own definition ({(rapier != null ? rapier.displayName : "none")})");
                Check(rapier != null && rapier.baseDamage == 19f, $"{label}: the rapier still carries its authored 19 base damage");
                Check(melee != null && melee.enabled, $"{label}: MeleeCombat is live for the melee weapon now in the main hand");

                rig.ClearStatuses();
                rig.SetGuardHeld(false);
                rig.transform.position = _archerSpawn;
                rig.transform.rotation = Quaternion.LookRotation(Vector3.forward);
                rig.Skills.HandleKitRestored();
                rig.Mana.RefillToMax();

                TestTarget host = BuildTarget($"U2A5_Rapier_Host_{_checks}", _archerSpawn + new Vector3(0f, 0.9f, MeleeHostDepth));
                Physics.SyncTransforms();
                yield return WaitFrames(2);

                CheckInt($"{label}: CHA score", expectedCha, rig.AttributeStats.GetAttribute(SixAttribute.Charisma), "authored 8 plus the charm");
                CheckClose($"{label}: ability scale query", expectedScale, rig.AttributeStats.GetAbilityEffectScale(), "clamp(1 + 0.04 * CHA modifier, 0.6, 1.4)");
                CheckClose($"{label}: outgoing multiplier the authority reads", 1.12f, rig.GetOutgoingDamageMultiplier(rapier),
                    "finesse picks the better of STR modifier 0 and DEX modifier 3, and this class has no melee passive: no CHA term");

                float healthBefore = host.CurrentHealth;
                int acceptedBefore = _authority.AcceptedCount;
                int rejectedBefore = _authority.RejectedCount;

                ClearCaptured();

                melee.BeginSwing();
                yield return WaitForCaptured(ResolveTimeout);

                Check(_captured.Count == 1, $"{label}: the swing resolved as exactly one DamageInfo from the authority (got {_captured.Count})");
                CheckClose($"{label}: resolved damage", expected, healthBefore - host.CurrentHealth,
                    "19 x 1.12 (finesse, DEX modifier 3), the same at CHA 8, 10 and 18");
                Check(_authority.AcceptedCount == acceptedBefore + 1,
                    $"{label}: the authority accepted exactly one swing ({acceptedBefore} -> {_authority.AcceptedCount})");
                Check(_authority.RejectedCount == rejectedBefore,
                    $"{label}: the swing was not refused ({rejectedBefore} -> {_authority.RejectedCount})");

                if (_captured.Count > 0)
                {
                    DamageInfo damage = _captured[0];
                    CheckClose($"{label}: captured amount", expected, damage.Amount, "the authority's own resolved number");
                    Check(damage.SourceKind == EntityKind.LocalPlayer && damage.SourceCombatId == rig.CombatId,
                        $"{label}: attributed to the archer ({damage.SourceKind} #{damage.SourceCombatId})");
                    Check(rapier != null && damage.Type == rapier.damageType, $"{label}: damage type still comes from the rapier definition ({damage.Type})");
                    Check(!damage.Headshot && !damage.Blocked, $"{label}: an unblocked body hit");
                }

                Debug.Log($"{Tag} {label}: scale={expectedScale:F4} damage={(healthBefore - host.CurrentHealth):F4} " +
                          $"authority(accepted={_authority.AcceptedCount}, rejected={_authority.RejectedCount})");

                yield return WaitSeconds(WeaponAttackSpacing);
            }

            // ----------------------------------------------------------------------- the host

            /// <summary>
            /// The ICombatEntity / IStatusTarget this pass builds its measurements on. Awake allocates its own
            /// id and registers it, exactly as PlayerRig and EnemyAgent do, and OnDestroy hands back the same
            /// (id, instance) pair - so a destroyed host can never be resolved by the authority again.
            /// ApplyDamage records the DamageInfo the authority produced before handing it to the real
            /// HealthComponent, which is what lets a scenario assert the resolved number itself.
            /// </summary>
            private sealed class TestTarget : MonoBehaviour, ICombatEntity, IStatusTarget, IHitShapeProvider
            {
                private HealthComponent _health;
                private StatusComponent _status;
                private Collider _body;
                private int _combatId;
                private List<DamageInfo> _capture;

                public EntityKind Kind => EntityKind.Enemy;
                public int CombatId => _combatId;
                public int TeamId => CombatTeams.MonsterTeam;
                public Vector3 CombatCenter => transform.position + Vector3.up * 1.1f;

                /// <summary>No separate head: a host of this pass is never a headshot, by construction.</summary>
                public Collider HeadCollider => null;

                public Collider BodyCollider => _body;
                public StatusComponent Status => _status;

                /// <summary>
                /// D1a fixture change: the warrior's sword and the archer's rapier are swept segments now, and a
                /// sweep can only strike an entity that exposes hit volumes. This host gets ONE Body volume around
                /// its collider (a 0.8 m tall, 0.45 m radius capsule centred on the transform), so every hit is a
                /// body hit (location multiplier 1.0): the asserted damage numbers are unchanged by construction.
                /// </summary>
                public void CollectHitShapes(List<HitShape> shapes)
                {
                    Vector3 c = transform.position;
                    shapes.Add(new HitShape(HitLocation.Body, c + Vector3.down * 0.4f, c + Vector3.up * 0.4f, 0.45f));
                }

                public bool IsDead => _health != null && _health.IsDead;
                public float CurrentHealth => _health != null ? _health.CurrentHealth : 0f;
                public float MaxHealth => _health != null ? _health.MaxHealth : 0f;

                public void ApplyDamage(DamageInfo damage)
                {
                    if (_capture != null) _capture.Add(damage);
                    if (_health != null) _health.ApplyDamage(damage);
                }

                public void AttachCapture(List<DamageInfo> sink) { _capture = sink; }
                public void DetachCapture() { _capture = null; }

                /// <summary>A pool no scenario in this pass can empty, with no regen to muddy a measurement.</summary>
                public void ConfigurePool(int maxHealth) { if (_health != null) _health.Configure(maxHealth, 0f, 0f); }

                public void ApplyStatus(StatusSpec spec, int sourceCombatId)
                {
                    if (_status != null) _status.ApplyStatus(spec, sourceCombatId);
                }

                public void ClearStatuses() { if (_status != null) _status.ClearStatuses(); }
                public bool HasStatus(StatusKind kind) => _status != null && _status.HasStatus(kind);

                private void Awake()
                {
                    _health = GetComponent<HealthComponent>();
                    _status = GetComponent<StatusComponent>();
                    _body = GetComponent<Collider>();
                    _combatId = CombatRegistry.AllocateId();
                    CombatRegistry.Register(_combatId, this);
                }

                private void OnDestroy()
                {
                    // Unregister(int, ICombatEntity) as PlayerRig and EnemyAgent call it: own id + own instance.
                    CombatRegistry.Unregister(_combatId, this);
                }
            }
        }
    }
}
