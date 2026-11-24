using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Emberdelve.AI;
using Emberdelve.Combat;
using Emberdelve.Core;
using Emberdelve.Data;

namespace Emberdelve.EditorTools
{
    /// <summary>
    /// Shared by the D1c4 gates: predicts, from the very geometry the authority sweeps, where on a target a real EnemyAgent's
    /// swept swing will make first contact. It mirrors the contact rule (first touched sample opens the contact; within the pattern's
    /// contact window the deepest volume decides; ties go to the lower location number) and reads nothing from the hit-location
    /// table: the damage factor is a LITERAL (<see cref="LiteralFactor"/>) written here, so a gate that checks
    /// "base x literal factor" still fails when the table in code drifts.
    /// </summary>
    public static class EnemySweepPredictor
    {
        /// <summary>The documented per-location damage factors, as literals (head 1.5, body 1.0, arms 0.8, hands 0.7, legs 0.6, feet 0.5).</summary>
        public static float LiteralFactor(HitLocation location)
        {
            switch (location)
            {
                case HitLocation.Head: return 1.5f;
                case HitLocation.Body: return 1.0f;
                case HitLocation.Arms: return 0.8f;
                case HitLocation.Hands: return 0.7f;
                case HitLocation.Legs: return 0.6f;
                case HitLocation.Feet: return 0.5f;
                default: return 1.0f;
            }
        }

        /// <summary>
        /// True and the predicted location when the enemy's swing, aimed along <paramref name="flatDirection"/> from its attack origin,
        /// touches <paramref name="target"/>. The enemy must stand where it will stand when the swing begins.
        /// </summary>
        public static bool TryPredict(EnemyAgent enemy, Vector3 flatDirection, IHitShapeProvider target, out HitLocation location)
        {
            location = HitLocation.Body;
            if (enemy == null || target == null || enemy.Data == null) return false;

            WeaponData weapon = enemy.Data.attackWeapon;
            SwingPatternData pattern = enemy.AttackPattern;
            if (weapon == null || pattern == null) return false;

            flatDirection.y = 0f;
            if (flatDirection.sqrMagnitude < 1e-6f) return false;
            float yaw = Mathf.Atan2(flatDirection.x, flatDirection.z) * Mathf.Rad2Deg;

            List<HitShape> shapes = new List<HitShape>(16);
            target.CollectHitShapes(shapes);
            SwingFrame frame = new SwingFrame(enemy.AttackOrigin, yaw, 0f);
            float length = pattern.BladeLength(weapon);
            int samples = SwingGeometry.SampleCount(pattern, weapon);
            int window = Mathf.CeilToInt(pattern.contactWindow / Mathf.Max(1e-5f, pattern.activeTime / samples));
            int first = -1;
            float bestDepth = -1f;
            for (int i = 0; i <= samples; i++)
            {
                if (first >= 0 && i > first + window) break;
                SwingGeometry.BladeSegment(frame, pattern.ActivePose(i / (float)samples), length, out Vector3 b, out Vector3 tip);
                for (int k = 0; k < shapes.Count; k++)
                {
                    float d = SwingGeometry.SegmentDistance(b, tip, shapes[k].A, shapes[k].B, out Vector3 onBlade);
                    float depth = shapes[k].Radius + pattern.sweepRadius - d;
                    if (depth < 0f) continue;
                    if (first < 0) first = i;
                    bool better = bestDepth < 0f || depth > bestDepth + 1e-4f
                        || (Mathf.Abs(depth - bestDepth) <= 1e-4f && (int)shapes[k].Location < (int)location);
                    if (better)
                    {
                        bestDepth = depth;
                        location = shapes[k].Location;
                    }
                }
            }

            return first >= 0;
        }
    }

    /// <summary>
    /// D1c4 gate: monsters attack through the same swept melee authority the player does. A real EnemyAgent (the Hollow Servitor's
    /// claw, the Ashen Warden's maul) begins a swing at the end of its telegraph; the swing is the pattern arc of its weapon; the
    /// hit lands where the sweep touches (the hit-location table) for base damage x literal factor; walls stop it; a player
    /// outside the arc takes nothing; two monsters each hit once; monsters do not hit monsters; dying or being stunned mid-swing
    /// ends the swing; the maul still breaks a heater guard and applies its slow through the swept path. The telegraph the player
    /// sees is the pattern's ground projection. Fixtures stand far outside the dungeon (Origin) and run in real time with the
    /// scene's authority replaced by a fresh one wired to a gate-owned config.
    ///
    /// Run: Unity -batchmode -nographics -projectPath . -logFile Logs/d1c4_enemysweep.log
    ///      -executeMethod Emberdelve.EditorTools.EmberdelveEnemySweepGate.Run
    /// </summary>
    [InitializeOnLoad]
    public static class EmberdelveEnemySweepGate
    {
        private const string Pending = "Emberdelve.EnemySweepGate.Pending";

        static EmberdelveEnemySweepGate()
        {
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.EnteredPlayMode && SessionState.GetBool(Pending, false))
                {
                    SessionState.SetBool(Pending, false);
                    new GameObject("EnemySweep_Gate").AddComponent<EnemySweepHarness>();
                }
            };
        }

        public static void Run()
        {
            SessionState.SetBool(Pending, true);
            EditorSceneManager.OpenScene("Assets/Emberdelve/Scenes/Emberdelve_Level01.unity");
            EditorApplication.EnterPlaymode();
        }
    }

    public sealed class EnemySweepHarness : MonoBehaviour
    {
        private const string Tag = "[EnemySweepGate]";
        private static readonly Vector3 Origin = new Vector3(3000f, 0f, 3000f);
        private const float Standoff = 1.7f;
        private const float Timeout = 8f;

        private int _checks;
        private int _failures;
        private readonly List<UnityEngine.Object> _cleanup = new List<UnityEngine.Object>();
        private readonly List<GameObject> _scene = new List<GameObject>();
        private readonly List<ICombatEntity> _registered = new List<ICombatEntity>();

        private LocalCombatAuthority _authority;
        private GameConfig _config;
        private NavGridData _navGrid;
        private WeaponData _claw;
        private WeaponData _maul;
        private EnemyData _servitor;
        private EnemyData _warden;

        /// <summary>A stand-in player: the game's humanoid hit volumes, a guard that can break, and a status holder.</summary>
        private sealed class FakePlayer : ICombatEntity, IHitShapeProvider, IGuardBreakDefender, IStatusTarget
        {
            public int CombatId { get; set; }
            public EntityKind Kind => EntityKind.LocalPlayer;
            public int TeamId => CombatTeams.PlayerTeam;
            public GameObject Root;
            public HitVolume[] Volumes;
            public StatusComponent Status;
            public bool Guard;
            public float GuardReduction = 0.6f;
            public float ImpactResist = 20f;
            public bool Dead;
            public float Taken;
            public readonly List<DamageInfo> Received = new List<DamageInfo>();

            public Vector3 CombatCenter => Root.transform.position + Vector3.up * 1.15f;
            public Collider HeadCollider => null;
            public bool IsDead => Dead;
            public float CurrentHealth => 100000f - Taken;
            public float MaxHealth => 100000f;

            public void ApplyDamage(DamageInfo damage) { Received.Add(damage); Taken += damage.Amount; }
            public void CollectHitShapes(List<HitShape> shapes) { HitVolumeLayout.Collect(Volumes, shapes); }

            public bool TryResolveGuard(in DamageInfo incoming, CombatFlowTuning tuning, out GuardResult result)
            {
                result = default;
                if (!Guard) return false;
                result = new GuardResult(GuardReduction, false);
                return true;
            }

            public void ApplyStatus(StatusSpec spec, int sourceCombatId) { Status.ApplyStatus(spec, sourceCombatId); }
            public void ClearStatuses() { Status.ClearStatuses(); }
            public bool HasStatus(StatusKind kind) => Status.HasStatus(kind);
        }

        // ------------------------------------------------------------------------------------------------
        // Plumbing
        // ------------------------------------------------------------------------------------------------

        private void Check(bool ok, string label)
        {
            _checks++;
            if (ok) return;
            _failures++;
            Debug.LogError($"{Tag} FAIL {label}");
        }

        private void Near(float expected, float actual, float tolerance, string label)
        {
            Check(Mathf.Abs(expected - actual) <= tolerance, $"{label} (expected {expected:F4}, got {actual:F4})");
        }

        private FakePlayer NewPlayer(Vector3 local)
        {
            GameObject root = new GameObject("EnemySweep_Player");
            root.transform.position = Origin + local;
            _scene.Add(root);
            FakePlayer player = new FakePlayer { CombatId = CombatRegistry.AllocateId(), Root = root };
            player.Volumes = HitVolumeLayout.EnsureHumanoid(root, 0.92f, 1.68f);
            player.Status = StatusComponent.Ensure(root);
            CombatRegistry.Register(player.CombatId, player);
            _registered.Add(player);
            return player;
        }

        private static void SetReference(Component target, string propertyName, UnityEngine.Object value)
        {
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

        /// <summary>Spawns a real EnemyAgent at a local position facing <paramref name="yaw"/> degrees; a frozen one is disabled once started.</summary>
        private IEnumerator Spawn(EnemyData data, Vector3 local, float yaw, bool frozen, Action<EnemyAgent> done)
        {
            GameObject go = new GameObject("EnemySweep_" + data.enemyId);
            go.transform.position = Origin + local;
            go.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            int layer = LayerMask.NameToLayer(GameConfig.EnemyLayerName);
            if (layer >= 0) go.layer = layer;
            _scene.Add(go);

            go.AddComponent<HealthComponent>();
            EnemyAgent agent = go.AddComponent<EnemyAgent>();
            SetReference(agent, "data", data);
            SetReference(agent, "navGrid", _navGrid);
            SetReference(agent, "config", _config);
            if (data.IsCinderWarden)
            {
                var visual = Resources.Load<GameObject>("Emberdelve/NetEnemyVisuals/EnemyVisual_CinderWarden");
                if (visual != null) Instantiate(visual, go.transform, false);
            }
            Physics.SyncTransforms();
            yield return null;
            yield return null;
            if (frozen)
            {
                // These fixtures isolate an already-awake attack. Live end-to-end flows still exercise natural waking.
                agent.Brain.NotifyDamaged(DamageInfo.Invalid);
                agent.Brain.Tick(EnemyBrain.WakeSeconds, Time.time, agent.transform.position, agent.transform.forward, null);
                agent.RefreshDormantHitPose(null);
                agent.enabled = false;
                Physics.SyncTransforms();
            }
            _registered.Add(agent);
            done(agent);
        }

        private IEnumerator Cleanup()
        {
            foreach (ICombatEntity e in _registered) CombatRegistry.Unregister(e.CombatId, e);
            _registered.Clear();
            foreach (GameObject go in _scene) if (go != null) Destroy(go);
            _scene.Clear();
            yield return null;
            yield return null;
        }

        private IEnumerator WaitFor(Func<bool> condition, float seconds)
        {
            float end = Time.time + seconds;
            while (!condition() && Time.time < end) yield return null;
        }

        private bool Status(int swingId, out MeleeSwingStatus status) => _authority.TryGetMeleeSwingStatus(swingId, out status);

        private IEnumerator WaitSwingFinished(int swingId)
        {
            yield return WaitFor(() => Status(swingId, out MeleeSwingStatus s) && s.Finished, Timeout);
        }

        private GameObject AddWall(Vector3 center, Vector3 size)
        {
            GameObject wall = new GameObject("EnemySweep_Wall");
            wall.transform.position = Origin + center;
            BoxCollider box = wall.AddComponent<BoxCollider>();
            box.size = size;
            int level = LayerMask.NameToLayer(GameConfig.LevelLayerName);
            if (level >= 0) wall.layer = level;
            _scene.Add(wall);
            Physics.SyncTransforms();
            return wall;
        }

        // ------------------------------------------------------------------------------------------------
        // Run
        // ------------------------------------------------------------------------------------------------

        private IEnumerator Start()
        {
            yield return null;
            yield return null;
            try { yield return RunAll(); }
            finally { EnemyAttackChoice.Override = -1; }
            Debug.Log($"{Tag} {(_failures == 0 ? "PASS" : "FAIL")} checks={_checks} failures={_failures}");
            EmberdelveGateExit.Exit(_failures == 0 ? 0 : 1);
        }

        private IEnumerator RunAll()
        {
            _claw = AssetDatabase.LoadAssetAtPath<WeaponData>(EmberdelvePaths.Data + "/Weapon_HollowClaw.asset");
            _maul = AssetDatabase.LoadAssetAtPath<WeaponData>(EmberdelvePaths.Data + "/Weapon_WardenMaul.asset");
            _servitor = AssetDatabase.LoadAssetAtPath<EnemyData>(EmberdelvePaths.Data + "/Enemy_HollowServitor.asset");
            _warden = AssetDatabase.LoadAssetAtPath<EnemyData>(EmberdelvePaths.Data + "/Enemy_AshenWarden.asset");
            _navGrid = AssetDatabase.LoadAssetAtPath<NavGridData>(EmberdelvePaths.Data + "/EmberdelveNavGrid.asset");
            Check(_claw != null && _maul != null && _servitor != null && _warden != null, "the claw, maul and both enemy assets are available");
            if (_claw == null || _maul == null || _servitor == null || _warden == null) yield break;

            _config = ScriptableObject.CreateInstance<GameConfig>();
            _cleanup.Add(_config);
            _config.hitMask = 0;
            _config.levelMask = LayerMask.GetMask(GameConfig.LevelLayerName);
            _config.hitLocations = null;
            GameObject authorityObject = new GameObject("EnemySweepGate_Authority");
            _cleanup.Add(authorityObject);
            _authority = authorityObject.AddComponent<LocalCombatAuthority>();
            typeof(LocalCombatAuthority).GetField("config", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(_authority, _config);

            Section("pattern data", PatternData);

            IEnumerator[] flows =
            {
                RealAttack(_servitor, _claw, 15f, "claw"),
                RealAttack(_servitor, _claw, 15f, "claw overhead", 1),
                RealAttack(_warden, _maul, 26f, "maul"),
                CinderWardenFight(),
                LegacyRefused(),
                WallStops(),
                OutsideArc(),
                TwoEnemies(),
                EnemyVsEnemy(),
                DeathMidSwing(),
                StunMidSwing(),
                MaulIsBlockedNotBroken(),
                ClawIsBlocked()
            };

            for (int i = 0; i < flows.Length; i++)
            {
                IEnumerator flow = flows[i];
                EnemyAttackChoice.Override = 0;   // the diagonal unless a flow pins the overhead itself
                int before = _checks;
                int failuresBefore = _failures;
                bool threw = false;
                while (true)
                {
                    object current;
                    try
                    {
                        if (!flow.MoveNext()) break;
                        current = flow.Current;
                    }
                    catch (Exception e)
                    {
                        threw = true;
                        _failures++;
                        Debug.LogError($"{Tag} FAIL flow {i} threw");
                        Debug.LogException(e);
                        break;
                    }

                    yield return current;
                }

                yield return Cleanup();
                Debug.Log($"{Tag} flow {i}{(threw ? " (threw)" : string.Empty)}: {_checks - before} checks, {_failures - failuresBefore} failures");
            }

            foreach (UnityEngine.Object o in _cleanup) if (o != null) Destroy(o);
        }

        private void Section(string name, Action body)
        {
            int checksBefore = _checks;
            int failuresBefore = _failures;
            try { body(); }
            catch (Exception e)
            {
                _failures++;
                Debug.LogError($"{Tag} FAIL section '{name}' threw");
                Debug.LogException(e);
            }
            Debug.Log($"{Tag} section '{name}': {_checks - checksBefore} checks, {_failures - failuresBefore} failures");
        }

        // ------------------------------------------------------------------------------------------------
        // 1. the authored patterns
        // ------------------------------------------------------------------------------------------------

        private void PatternData()
        {
            CheckPattern(_claw, _servitor, "Pattern_ClawSlash", "Minion", 1f, "claw", 0, 2);
            CheckPattern(_claw, _servitor, "Pattern_ClawSlash", "Rogue", 1f, "claw (rogue body)", 0, 2);
            CheckPattern(_claw, _servitor, "Pattern_ClawOverhead", "Minion", 1f, "claw overhead", 1, 2);
            CheckPattern(_claw, _servitor, "Pattern_ClawOverhead", "Rogue", 1f, "claw overhead (rogue body)", 1, 2);
            CheckPattern(_maul, _warden, "Pattern_MaulSweep", "Warrior", EmberdelveEnemyVisualFactory.EliteScale, "maul", 0, 1);
        }

        /// <summary>
        /// The sweep is the visible weapon: the pattern's keys are the held weapon's grip and direction through the attack clip's sweep
        /// window (re-baked here from the shipped variant: model, clip, weapon, grip), the segment is as long as the weapon, the telegraph
        /// wedge is the pattern's yaw range and the brain starts the blow inside the weapon's reach.
        /// </summary>
        private void CheckPattern(WeaponData weapon, EnemyData enemy, string assetName, string variant, float scale, string label,
            int patternIndex, int expectedPatterns)
        {
            bool overhead = patternIndex == EnemyAttackPhase.OverheadVariant;
            Check(weapon.UsesSweptMelee && weapon.swingPatterns != null && weapon.swingPatterns.Length == expectedPatterns,
                $"{label}: the weapon has exactly {expectedPatterns} swing pattern(s)");
            if (!weapon.UsesSweptMelee || weapon.swingPatterns == null || weapon.swingPatterns.Length != expectedPatterns) return;

            SwingPatternData pattern = weapon.swingPatterns[patternIndex];
            Check(pattern != null && pattern.name == assetName, $"{label}: the pattern asset is {assetName}");
            if (pattern == null) return;

            Check(pattern.Validate(out string problem), $"{label}: the pattern validates ({problem})");
            bool baked = EmberdelveEnemyVisualFactory.TryBakeSwing(variant, scale, pattern.keys.Length, out SwingKey[] keys, out float reach,
                out float length, out _, out float activeSeconds, patternIndex);
            Check(baked, $"{label}: the {variant} attack can be baked (model, clip and weapon are present)");
            if (baked)
            {
                float worst = 0f;
                for (int i = 0; i < keys.Length; i++)
                {
                    worst = Mathf.Max(worst, (keys[i].basePos - pattern.keys[i].basePos).magnitude);
                    worst = Mathf.Max(worst, Mathf.Abs(Mathf.DeltaAngle(keys[i].bladeYaw, pattern.keys[i].bladeYaw)) * Mathf.Deg2Rad * length);
                    worst = Mathf.Max(worst, Mathf.Abs(keys[i].bladePitch - pattern.keys[i].bladePitch) * Mathf.Deg2Rad * length);
                }
                Check(worst < 0.01f, $"{label}: every key is the {variant}'s visible weapon at that instant (worst {worst:F3} m)");
                Near(length, pattern.BladeLength(weapon), 0.01f, $"{label}: the swept segment is as long as the visible weapon ({length:F2} m)");
                if (overhead) Check(reach <= weapon.reach + 0.01f && reach >= weapon.reach - 0.35f, $"{label}: the overhead's farthest tip ({reach:F2} m) lies within the weapon reach ({weapon.reach:F2} m)");
                else Near(reach, weapon.reach, 0.01f, $"{label}: the weapon reach is the farthest tip distance ({reach:F2} m)");
                Near(activeSeconds, pattern.activeTime, 1e-3f, $"{label}: the active window plays the clip's sweep window ({activeSeconds:F2} s)");
            }

            pattern.YawRange(out float minYaw, out float maxYaw);
            if (overhead)
            {
                SwingKey first = pattern.keys[0], last = pattern.keys[pattern.keys.Length - 1];
                Check(pattern.kind == SwingKind.Overhead, $"{label}: the pattern is an Overhead swing");
                Check(first.basePos.y > last.basePos.y + 0.3f, $"{label}: the weapon comes down ({first.basePos.y:F2} -> {last.basePos.y:F2})");
                Check(last.bladePitch - first.bladePitch > 70f && last.bladePitch > 25f, $"{label}: the blade turns from raised to pointing down ({first.bladePitch:F0} -> {last.bladePitch:F0})");
                Check(last.basePos.z > 0.3f, $"{label}: the chop ends in front of the body (z {last.basePos.z:F2})");
                Check(Mathf.Abs(last.basePos.x) < 0.35f && Mathf.Abs(last.bladeYaw) < 25f, $"{label}: the chop ends along the committed facing (x {last.basePos.x:F2}, yaw {last.bladeYaw:F0})");
                Near(0.10f, pattern.windupTime, 1e-4f, $"{label}: the pattern windup is 0.10 s");
                Check(weapon.impactZones == null || weapon.impactZones.Length == 0, $"{label}: no impact zones");
                return;
            }
            Check(maxYaw - minYaw >= 40f && maxYaw - minYaw <= 200f, $"{label}: the blade yaw range is a drawable wedge ({minYaw:F0}..{maxYaw:F0})");
            Check(pattern.keys[0].bladeYaw > pattern.keys[pattern.keys.Length - 1].bladeYaw, $"{label}: the blow travels from the attacker's right to the left");
            Near(enemy.attackTelegraphCone, maxYaw - minYaw, 0.01f, $"{label}: the pattern's yaw range equals the enemy's telegraph cone ({enemy.attackTelegraphCone:F0})");
            Check(enemy.attackRange <= pattern.EffectiveReach(weapon) + 0.3f, $"{label}: the brain starts the blow inside the weapon's reach (range {enemy.attackRange:F2}, reach {pattern.EffectiveReach(weapon):F2})");
            Near(0.10f, pattern.windupTime, 1e-4f, $"{label}: the pattern windup is 0.10 s");
            Check(weapon.impactZones == null || weapon.impactZones.Length == 0, $"{label}: no impact zones (the contact point does not scale the damage)");
            Check(weapon.comboSteps == null || weapon.comboSteps.Length == 0, $"{label}: no combo chain");
        }

        // ------------------------------------------------------------------------------------------------
        // 2. a real EnemyAgent attacks end to end
        // ------------------------------------------------------------------------------------------------

        private IEnumerator RealAttack(EnemyData data, WeaponData weapon, float baseDamage, string label, int variant = 0)
        {
            EnemyAttackChoice.Override = variant;
            FakePlayer player = NewPlayer(new Vector3(0f, 0f, Standoff));
            EnemyAgent enemy = null;
            yield return Spawn(data, Vector3.zero, 0f, false, e => enemy = e);

            int acceptedBefore = _authority.AcceptedCount;
            int rejectedBefore = _authority.RejectedCount;
            int startedBefore = _authority.SwingsStarted;

            yield return WaitFor(() => enemy.IsWindingUp, Timeout);
            Check(enemy.IsWindingUp, $"{label}: the monster notices the player and starts its telegraph");
            Check(player.Received.Count == 0, $"{label}: nothing has landed while the telegraph is still warning");

            // the telegraph is the pattern's ground projection
            AttackTelegraphVisual visual = enemy.GetComponentInChildren<AttackTelegraphVisual>(true);
            Check(visual != null && visual.gameObject.activeSelf, $"{label}: the telegraph wedge is lit during the windup");
            if (visual != null)
            {
                yield return null;
                SwingPatternData pattern = weapon.swingPatterns[variant];
                enemy.GetTelegraphWedge(variant, out float minYaw, out float maxYaw);
                Check(enemy.AttackVariantIndex == variant, $"{label}: the windup warns attack {variant} (agent reports {enemy.AttackVariantIndex})");
                Near(minYaw, visual.MinYawDegrees, 1e-3f, $"{label}: the telegraph starts at the pattern's lowest yaw");
                Near(maxYaw, visual.MaxYawDegrees, 1e-3f, $"{label}: the telegraph ends at the pattern's highest yaw");
                Near((maxYaw - minYaw) * 0.5f, visual.HalfAngleDegrees, 1e-3f, $"{label}: the telegraph half angle is half the pattern's yaw range");
                Near(pattern.EffectiveReach(weapon), visual.LastLength, 1e-3f, $"{label}: the telegraph radius is the pattern's effective reach");
                Near(pattern.EffectiveReach(weapon), enemy.TelegraphReach, 1e-3f, $"{label}: the agent reports the pattern's reach as its telegraph radius");
            }

            // where will the sweep touch? (the enemy has settled: it holds still in the attack state)
            Vector3 direction = enemy.Brain.TelegraphDirection;
            bool contact = EnemySweepPredictor.TryPredict(enemy, direction, player, out HitLocation predicted);
            Check(contact, $"{label}: the committed direction sweeps through the player");

            yield return WaitFor(() => enemy.SwingsBegun >= 1, Timeout);
            Check(enemy.SwingsBegun == 1 && _authority.SwingsStarted == startedBefore + 1, $"{label}: exactly one swing record began ({enemy.SwingsBegun}, authority {_authority.SwingsStarted - startedBefore})");
            int swingId = enemy.LastSwingId;
            yield return WaitSwingFinished(swingId);

            Check(Status(swingId, out MeleeSwingStatus status) && status.Finished && status.Hits.Count == 1, $"{label}: the swing finished with exactly one hit");
            Check(player.Received.Count == 1, $"{label}: the player took exactly one hit ({player.Received.Count})");
            if (player.Received.Count == 1)
            {
                DamageInfo damage = player.Received[0];
                Check(damage.Location == predicted, $"{label}: the hit location is the predicted {predicted} (got {damage.Location})");
                Near(baseDamage * EnemySweepPredictor.LiteralFactor(predicted), damage.Amount, 0.02f,
                    $"{label}: damage is {baseDamage:F0} x the literal {predicted} factor {EnemySweepPredictor.LiteralFactor(predicted):F1}");
                Check(damage.Headshot == (predicted == HitLocation.Head), $"{label}: Headshot is true exactly for Head");
                Check(damage.SourceKind == EntityKind.Enemy && damage.SourceCombatId == enemy.CombatId, $"{label}: attributed to the monster that swung it");
            }

            Check(_authority.AcceptedCount == acceptedBefore + 1 && _authority.RejectedCount == rejectedBefore,
                $"{label}: one accepted hit, nothing refused ({_authority.AcceptedCount - acceptedBefore} / {_authority.RejectedCount - rejectedBefore})");
            Check(!enemy.LastAttackWhiffed, $"{label}: a landed swing is not a whiff");
        }

        private IEnumerator CinderWardenFight()
        {
            var data = AssetDatabase.LoadAssetAtPath<EnemyData>(EmberdelvePaths.Data + "/Enemy_CinderWarden.asset");
            Check(data != null && data.IsCinderWarden, "Cinder Warden has an authored boss definition");
            if (data == null) yield break;
            FakePlayer fighter = NewPlayer(new Vector3(0f, 0f, 2.05f));
            FakePlayer behind = NewPlayer(new Vector3(0f, 0f, -2.65f));
            EnemyAgent boss = null;
            yield return Spawn(data, Vector3.zero, 0f, false, enemy => boss = enemy);

            Check(boss != null && !boss.IsDormant && boss.MaxHealth == 320f && boss.CurrentHealth == 320f,
                "Cinder Warden starts awake at 320 HP");
            if (boss == null) yield break;
            var driver = boss.GetComponentInChildren<EnemyVisualDriver>();
            Check(driver != null && driver.Animator != null && driver.Animator.isHuman, "the encounter has its animated Humanoid visual");
            Check(boss.AttackPattern != null && boss.AttackPattern.keys.Length == 13 && boss.AttackPattern.Validate(out _),
                "the club uses a valid 13-sample swept contact path");
            var hitVolumes = new List<HitShape>();
            boss.CollectHitShapes(hitVolumes);
            Check(hitVolumes.Exists(shape => shape.Location == HitLocation.Head && shape.A.y > 2.7f),
                "the standing head hit volume reaches the boss's head");

            yield return WaitFor(() => boss.IsWindingUp, Timeout);
            Check(boss.IsWindingUp && boss.CurrentTargetCombatId == fighter.CombatId, "the boss acquires the player and telegraphs");
            Check(fighter.Received.Count == 0, "no club damage during the telegraph");
            yield return WaitFor(() => boss.SwingsBegun > 0, Timeout);
            int swingId = boss.LastSwingId;
            Check(swingId > 0, "the authority accepts the committed club attack");
            if (swingId == 0) yield break;
            yield return WaitSwingFinished(swingId);
            Check(Status(swingId, out MeleeSwingStatus swing) && swing.Finished && swing.Hits.Count == 1,
                "only one target is hit by the club's active window");
            Check(fighter.Received.Count == 1 && fighter.Received[0].SourceCombatId == boss.CombatId &&
                  fighter.Received[0].Amount > 0f, "the player in the club path receives one damage event");
            Check(behind.Received.Count == 0, "a player behind the club is unharmed");

            boss.ApplyDamage(new DamageInfo(90f, boss.CombatCenter, Vector3.forward, fighter.CombatCenter,
                fighter.CombatId, EntityKind.LocalPlayer, DamageType.Physical, false, false, 0f, HitLocation.Body));
            Near(230f, boss.CurrentHealth, .01f, "Cinder Warden loses HP to the player");
            boss.ApplyDamage(new DamageInfo(230f, boss.CombatCenter, Vector3.forward, fighter.CombatCenter,
                fighter.CombatId, EntityKind.LocalPlayer, DamageType.Physical, false, false, 0f, HitLocation.Body));
            yield return null;
            Check(boss.IsDead && !boss.TryBeginAttack(Vector3.forward, out _), "a defeated boss cannot swing again");
        }

        // ------------------------------------------------------------------------------------------------
        // 3. the legacy arc-less path is closed for a real agent
        // ------------------------------------------------------------------------------------------------

        private IEnumerator LegacyRefused()
        {
            FakePlayer player = NewPlayer(new Vector3(0f, 0f, Standoff));
            EnemyAgent enemy = null;
            yield return Spawn(_servitor, Vector3.zero, 0f, true, e => enemy = e);

            int rejectedBefore = _authority.RejectedCount;
            AttackIntent intent = new AttackIntent(enemy.CombatId, EntityKind.Enemy, _claw, 1f, enemy.CombatCenter,
                player.CombatCenter - enemy.CombatCenter, _claw.reach + 0.4f, CombatRegistry.NextSequence(enemy.CombatId), 0f);
            bool ok = _authority.SubmitAttackAsOwner(intent, new HitCandidate(player.CombatId, player.CombatCenter, false), out string reason);
            Check(!ok && reason.Contains("swept swing"), $"a real agent's claw SubmitAttack is refused ({reason})");
            Check(player.Taken == 0f, "and deals nothing");
            Check(_authority.RejectedCount == rejectedBefore + 1, "and is counted as a rejection");
        }

        // ------------------------------------------------------------------------------------------------
        // 4. walls
        // ------------------------------------------------------------------------------------------------

        private IEnumerator WallStops()
        {
            // control: the frozen agent swings at a player 1.7 m ahead and hits
            {
                FakePlayer player = NewPlayer(new Vector3(0f, 0f, Standoff));
                EnemyAgent enemy = null;
                yield return Spawn(_servitor, Vector3.zero, 0f, true, e => enemy = e);
                bool begun = enemy.TryBeginAttack(Vector3.forward, out string reason);
                Check(begun, $"control: the frozen agent begins its swing ({reason})");
                yield return WaitSwingFinished(enemy.LastSwingId);
                Check(Status(enemy.LastSwingId, out MeleeSwingStatus st) && st.Hits.Count == 1 && player.Received.Count == 1, "control: with nothing in between the swing hits the player");
            }

            yield return Cleanup();

            // a wall between them: the blade moves into the surface and the swing stops
            {
                FakePlayer player = NewPlayer(new Vector3(0f, 0f, Standoff));
                EnemyAgent enemy = null;
                yield return Spawn(_servitor, Vector3.zero, 0f, true, e => enemy = e);
                AddWall(new Vector3(0f, 1.5f, 1.15f), new Vector3(6f, 3f, 0.2f));
                bool begun = enemy.TryBeginAttack(Vector3.forward, out string reason);
                Check(begun, $"wall: the swing begins ({reason})");
                yield return WaitSwingFinished(enemy.LastSwingId);
                Check(Status(enemy.LastSwingId, out MeleeSwingStatus st) && st.WallStopped && st.Hits.Count == 0, $"wall: the swing is stopped by the wall and hits nothing (end {st.EndReason}, hits {st.Hits.Count})");
                Check(player.Taken == 0f && player.Received.Count == 0, "wall: the player behind the wall takes nothing");
            }
        }

        // ------------------------------------------------------------------------------------------------
        // 5. outside the arc
        // ------------------------------------------------------------------------------------------------

        private IEnumerator OutsideArc()
        {
            // the player is 90 degrees to the side: the pattern only covers +-60 degrees
            {
                FakePlayer player = NewPlayer(new Vector3(Standoff, 0f, 0f));
                EnemyAgent enemy = null;
                yield return Spawn(_servitor, Vector3.zero, 0f, true, e => enemy = e);
                bool begun = enemy.TryBeginAttack(Vector3.forward, out string reason);
                Check(begun, $"side: the swing begins ({reason})");
                yield return WaitSwingFinished(enemy.LastSwingId);
                Check(Status(enemy.LastSwingId, out MeleeSwingStatus st) && st.Finished && st.Hits.Count == 0, "side: a player 90 degrees off the aim is outside the arc and is not hit");
                Check(player.Taken == 0f, "side: and takes no damage");
            }

            yield return Cleanup();

            // the player is behind the monster
            {
                FakePlayer player = NewPlayer(new Vector3(0f, 0f, -Standoff));
                EnemyAgent enemy = null;
                yield return Spawn(_servitor, Vector3.zero, 0f, true, e => enemy = e);
                bool begun = enemy.TryBeginAttack(Vector3.forward, out string reason);
                Check(begun, $"behind: the swing begins ({reason})");
                yield return WaitSwingFinished(enemy.LastSwingId);
                Check(Status(enemy.LastSwingId, out MeleeSwingStatus st) && st.Finished && st.Hits.Count == 0, "behind: a player behind the monster is not hit");
                Check(player.Taken == 0f, "behind: and takes no damage");
            }
        }

        // ------------------------------------------------------------------------------------------------
        // 6. two monsters, one player
        // ------------------------------------------------------------------------------------------------

        private IEnumerator TwoEnemies()
        {
            FakePlayer player = NewPlayer(new Vector3(0f, 0f, Standoff));
            EnemyAgent a = null;
            EnemyAgent b = null;
            yield return Spawn(_servitor, Vector3.zero, 0f, true, e => a = e);
            yield return Spawn(_servitor, new Vector3(0f, 0f, 2f * Standoff), 180f, true, e => b = e);

            bool predictedA = EnemySweepPredictor.TryPredict(a, Vector3.forward, player, out HitLocation locationA);
            bool predictedB = EnemySweepPredictor.TryPredict(b, Vector3.back, player, out HitLocation locationB);
            Check(predictedA && predictedB, "both monsters' arcs reach the player standing between them");

            bool beganA = a.TryBeginAttack(Vector3.forward, out string reasonA);
            bool beganB = b.TryBeginAttack(Vector3.back, out string reasonB);
            Check(beganA && beganB, $"both swings begin ({reasonA} / {reasonB})");
            yield return WaitSwingFinished(a.LastSwingId);
            yield return WaitSwingFinished(b.LastSwingId);

            Check(player.Received.Count == 2, $"the player is hit exactly twice, once per monster ({player.Received.Count})");
            if (player.Received.Count == 2)
            {
                bool distinct = player.Received[0].SourceCombatId != player.Received[1].SourceCombatId
                    && (player.Received[0].SourceCombatId == a.CombatId || player.Received[0].SourceCombatId == b.CombatId)
                    && (player.Received[1].SourceCombatId == a.CombatId || player.Received[1].SourceCombatId == b.CombatId);
                Check(distinct, "one hit from each monster");
                float expected = 15f * (EnemySweepPredictor.LiteralFactor(locationA) + EnemySweepPredictor.LiteralFactor(locationB));
                Near(expected, player.Taken, 0.05f, $"the total is 15 x ({locationA} + {locationB} literal factors)");
            }
        }

        // ------------------------------------------------------------------------------------------------
        // 7. monsters do not hit monsters
        // ------------------------------------------------------------------------------------------------

        private IEnumerator EnemyVsEnemy()
        {
            EnemyAgent attacker = null;
            EnemyAgent bystander = null;
            yield return Spawn(_servitor, Vector3.zero, 0f, true, e => attacker = e);
            yield return Spawn(_servitor, new Vector3(0f, 0f, Standoff), 180f, true, e => bystander = e);

            float healthBefore = bystander.CurrentHealth;
            bool begun = attacker.TryBeginAttack(Vector3.forward, out string reason);
            Check(begun, $"the swing begins with a fellow monster in the arc ({reason})");
            yield return WaitSwingFinished(attacker.LastSwingId);
            Check(Status(attacker.LastSwingId, out MeleeSwingStatus st) && st.Finished && st.Hits.Count == 0, "a monster's swing records no hit on a fellow monster");
            Check(Mathf.Approximately(healthBefore, bystander.CurrentHealth), "and the fellow monster's health is untouched");
        }

        // ------------------------------------------------------------------------------------------------
        // 8. dying or being stunned mid-swing
        // ------------------------------------------------------------------------------------------------

        private IEnumerator DeathMidSwing()
        {
            FakePlayer player = NewPlayer(new Vector3(0f, 0f, Standoff));
            EnemyAgent enemy = null;
            yield return Spawn(_servitor, Vector3.zero, 0f, true, e => enemy = e);

            bool begun = enemy.TryBeginAttack(Vector3.forward, out string reason);
            Check(begun, $"the swing begins ({reason})");
            Check(enemy.IsSwinging, "the agent reports a running swing");
            int swingId = enemy.LastSwingId;

            enemy.ApplyDamage(new DamageInfo(100000f, enemy.CombatCenter, Vector3.up, enemy.CombatCenter + Vector3.forward,
                0, EntityKind.Neutral, DamageType.Physical, false, false, 0f));
            Check(enemy.IsDead, "the monster is dead");
            yield return WaitSwingFinished(swingId);
            Check(Status(swingId, out MeleeSwingStatus st) && st.Finished && st.EndReason == SwingEndReason.InstigatorDied, $"the swing ends with InstigatorDied ({st.EndReason})");
            Check(st.Hits.Count == 0 && player.Taken == 0f, "and a monster that died before its blade arrived deals nothing");
            Check(!enemy.IsSwinging, "the dead agent no longer reports a swing");
        }

        private IEnumerator StunMidSwing()
        {
            FakePlayer player = NewPlayer(new Vector3(0f, 0f, Standoff));
            EnemyAgent enemy = null;
            yield return Spawn(_servitor, Vector3.zero, 0f, true, e => enemy = e);

            bool begun = enemy.TryBeginAttack(Vector3.forward, out string reason);
            Check(begun, $"the swing begins ({reason})");
            int swingId = enemy.LastSwingId;

            enemy.SetStunned(true);
            Check(enemy.IsStunned && enemy.ActiveWeapon == null, "a stunned monster holds no active weapon");
            yield return WaitSwingFinished(swingId);
            Check(Status(swingId, out MeleeSwingStatus st) && st.Finished && st.EndReason == SwingEndReason.WeaponChanged, $"the stun ends the swing ({st.EndReason})");
            Check(st.Hits.Count == 0 && player.Taken == 0f, "and the interrupted blade deals nothing");

            bool again = enemy.TryBeginAttack(Vector3.forward, out string stunnedReason);
            Check(!again, $"a stunned monster cannot begin a new swing ({stunnedReason})");
            enemy.SetStunned(false);
            Check(!enemy.IsStunned && enemy.ActiveWeapon == _claw, "releasing the stun gives the claw back");
        }

        // ------------------------------------------------------------------------------------------------
        // 9. guards, impact power and hit slow still work through the swept path
        // ------------------------------------------------------------------------------------------------

        private IEnumerator MaulIsBlockedNotBroken()
        {
            FakePlayer player = NewPlayer(new Vector3(0f, 0f, Standoff));
            player.Guard = true;
            EnemyAgent enemy = null;
            yield return Spawn(_warden, Vector3.zero, 0f, true, e => enemy = e);

            int breaksBefore = _authority.GuardBreaksApplied;
            int slowsBefore = _authority.HitSlowsApplied;
            bool contact = EnemySweepPredictor.TryPredict(enemy, Vector3.forward, player, out HitLocation location);
            Check(contact, "maul: the arc reaches the guarding player");

            bool begun = enemy.TryBeginAttack(Vector3.forward, out string reason);
            Check(begun, $"maul: the swing begins ({reason})");
            yield return WaitFor(() => player.Received.Count > 0, Timeout);
            float slowRemainingAtHit = player.Status.RemainingOf(StatusKind.Slow);
            yield return WaitSwingFinished(enemy.LastSwingId);

            Check(player.Received.Count == 1, $"maul: one hit landed on the guard ({player.Received.Count})");
            if (player.Received.Count == 1)
            {
                DamageInfo damage = player.Received[0];
                Near(30f, damage.ImpactPower, 1e-4f, "maul: the hit carries the maul's impact power 30");
                Check(damage.Blocked && !damage.GuardBroken, "maul: impact power 30 never pierces the heater guard; it holds");
                Near(Mathf.Max(1f, 26f * EnemySweepPredictor.LiteralFactor(location) * (1f - 0.6f)), damage.Amount, 0.05f, "maul: the held guard keeps its full share");
            }

            Check(_authority.GuardBreaksApplied == breaksBefore, "maul: the authority counted no guard break");
            Check(_authority.HitSlowsApplied == slowsBefore && !player.Status.HasStatus(StatusKind.Slow), "maul: a blocked heavy hit applies no hit slow");
            Check(slowRemainingAtHit <= 0f, "maul: nothing slowed the guarding player at the hit");
        }

        private IEnumerator ClawIsBlocked()
        {
            FakePlayer player = NewPlayer(new Vector3(0f, 0f, Standoff));
            player.Guard = true;
            EnemyAgent enemy = null;
            yield return Spawn(_servitor, Vector3.zero, 0f, true, e => enemy = e);

            int breaksBefore = _authority.GuardBreaksApplied;
            int slowsBefore = _authority.HitSlowsApplied;
            EnemySweepPredictor.TryPredict(enemy, Vector3.forward, player, out HitLocation location);

            bool begun = enemy.TryBeginAttack(Vector3.forward, out string reason);
            Check(begun, $"claw: the swing begins ({reason})");
            yield return WaitSwingFinished(enemy.LastSwingId);

            Check(player.Received.Count == 1, $"claw: one hit landed on the guard ({player.Received.Count})");
            if (player.Received.Count == 1)
            {
                DamageInfo damage = player.Received[0];
                Check(damage.Blocked && !damage.GuardBroken, "claw: impact power 12 is below the resist 20, so the guard holds");
                Near(Mathf.Max(1f, 15f * EnemySweepPredictor.LiteralFactor(location) * 0.4f), damage.Amount, 0.05f, "claw: a held guard takes its full 60 percent off");
            }

            Check(_authority.GuardBreaksApplied == breaksBefore && _authority.HitSlowsApplied == slowsBefore && !player.Status.HasStatus(StatusKind.Slow), "claw: no guard break and no slow");
        }
    }
}
