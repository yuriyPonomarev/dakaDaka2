using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Emberdelve.Data;
using Emberdelve.Items;
using Emberdelve.Player;
using Emberdelve.Combat;
using Emberdelve.Core;
using Emberdelve.Net.Rules;
using Emberdelve.Stats;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Emberdelve.EditorTools
{
    /// <summary>D4a authored-curve integration, real worn gear, pools and CharacterController walking.</summary>
    [InitializeOnLoad]
    public static class EmberdelveAttributeCurveGate
    {
        private const string Pending = "Emberdelve.AttributeCurve.Pending";
        private const string Done = "Emberdelve.AttributeCurve.Done";
        private const string Code = "Emberdelve.AttributeCurve.Code";
        private static double _requestedAt;

        static EmberdelveAttributeCurveGate()
        {
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.EnteredPlayMode && SessionState.GetBool(Pending, false))
                {
                    SessionState.SetBool(Pending, false);
                    new GameObject("D4a_AttributeCurveGate").AddComponent<AttributeCurveHarness>();
                }
                if (state != PlayModeStateChange.ExitingPlayMode || !SessionState.GetBool(Done, false)) return;
                int code = SessionState.GetInt(Code, 1);
                SessionState.SetBool(Done, false);
                EditorApplication.delayCall += () => EmberdelveGateExit.Exit(code);
            };
        }

        [MenuItem("Emberdelve/Validation/Attribute Movement Curves (D4a)")]
        public static void Run()
        {
            try
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode)
                    throw new InvalidOperationException("Run the curve gate from edit mode.");
                for (int i = 0; i < SceneManager.sceneCount; i++)
                    if (SceneManager.GetSceneAt(i).isDirty)
                        throw new InvalidOperationException("Save the open scene before running the curve gate.");
                // The first pass applies the migration. The second must preserve every data byte and GUID.
                EmberdelveBootstrapMenu.BuildDataOnly();
                var first = DataHashes();
                EmberdelveBootstrapMenu.BuildDataOnly();
                var second = DataHashes();
                if (first.Count != second.Count) throw new InvalidOperationException("Second build changed data file count.");
                foreach (var pair in first)
                    if (!second.TryGetValue(pair.Key, out string hash) || hash != pair.Value)
                        throw new InvalidOperationException("Second build changed " + pair.Key);
                Debug.Log("[AttributeCurve] double-build data and meta bytes identical: " + first.Count);
                SessionState.SetBool(Done, false);
                SessionState.SetBool(Pending, true);
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                _requestedAt = EditorApplication.timeSinceStartup;
                EditorApplication.update += Watchdog;
                EditorApplication.isPlaying = true;
            }
            catch (Exception exception)
            {
                SessionState.SetBool(Pending, false);
                Debug.LogException(exception);
                EmberdelveGateExit.Exit(1);
            }
        }

        private static void Watchdog()
        {
            if (EditorApplication.isPlaying) { EditorApplication.update -= Watchdog; return; }
            if (EditorApplication.timeSinceStartup - _requestedAt < 45) return;
            EditorApplication.update -= Watchdog;
            SessionState.SetBool(Pending, false);
            Debug.LogError("[AttributeCurve] Play mode did not start.");
            EmberdelveGateExit.Exit(1);
        }

        private static Dictionary<string, string> DataHashes()
        {
            var hashes = new Dictionary<string, string>();
            using (var sha = SHA256.Create())
                foreach (string path in Directory.GetFiles(EmberdelvePaths.Data, "*", SearchOption.AllDirectories))
                    hashes.Add(path, Convert.ToBase64String(sha.ComputeHash(File.ReadAllBytes(path))));
            return hashes;
        }

        internal static void Finish(int checks, int failures)
        {
            Debug.Log("[Emberdelve][AttributeCurve] " + (failures == 0 ? "PASS" : "FAIL") +
                " checks=" + checks + " failures=" + failures);
            SessionState.SetInt(Code, failures == 0 ? 0 : 1);
            SessionState.SetBool(Done, true);
            EditorApplication.isPlaying = false;
        }
    }

    public sealed class AttributeCurveHarness : MonoBehaviour
    {
        private int _checks, _failures;
        private readonly List<UnityEngine.Object> _cleanup = new List<UnityEngine.Object>();
        private void Check(bool condition, string label)
        {
            _checks++;
            if (condition) return;
            _failures++;
            Debug.LogError("[AttributeCurve] mismatch " + label);
        }
        private void Close(float actual, float literal, string label, float tolerance = .00002f) =>
            Check(Mathf.Abs(actual - literal) <= tolerance, label + ": actual=" + actual + " expected=" + literal);
        private static T Load<T>(string name) where T : UnityEngine.Object =>
            AssetDatabase.LoadAssetAtPath<T>(EmberdelvePaths.Data + "/" + name);
        private T Clone<T>(T asset) where T : UnityEngine.Object
        {
            T copy = Instantiate(asset); _cleanup.Add(copy); return copy;
        }

        private void Start()
        {
            try { Execute(); }
            catch (Exception exception) { _failures++; Debug.LogException(exception); }
            finally
            {
                foreach (UnityEngine.Object item in _cleanup) if (item != null) Destroy(item);
                EmberdelveAttributeCurveGate.Finish(_checks, _failures);
            }
        }

        private void Execute()
        {
            CharacterClassData warrior = Load<CharacterClassData>("Class_Warrior.asset");
            CharacterClassData archer = Load<CharacterClassData>("Class_Archer.asset");
            AttributeCurveProfile profile = Load<AttributeCurveProfile>("AttributeCurve_Default.asset");
            ItemData boots = Load<ItemData>("Item_FootpadBoots.asset");
            GameConfig config = Load<GameConfig>("EmberdelveGameConfig.asset");
            Check(warrior != null && archer != null && profile != null && boots != null && config != null,
                "builder-generated fixtures exist");
            if (warrior == null || archer == null || profile == null || boots == null || config == null) return;
            Check(warrior.attributeCurveProfile == profile && archer.attributeCurveProfile == profile,
                "both real classes reference the generated profile");
            Check(profile.TryCaptureMovement(out PiecewiseLinearCurve curve, out string reason), "authored curve validates: " + reason);
            if (curve == null) return;
            float[] xs = { 1, 6, 10, 16, 22, 30 };
            float[] ys = { .875f, .95f, 1f, 1.075f, 1.135f, 1.175f };
            Check(curve.Count == 6, "exact six authored breakpoints");
            for (int i = 0; i < Math.Min(6, curve.Count); i++)
            {
                Close(curve.GetPoint(i).x, xs[i], "authored score knot " + i);
                Close(curve.GetPoint(i).value, ys[i], "authored factor knot " + i);
            }
            string classBefore = EditorJsonUtility.ToJson(warrior);
            string bootsBefore = EditorJsonUtility.ToJson(boots);
            string profileBefore = EditorJsonUtility.ToJson(profile);
            CheckCapturedProfile(warrior, profile);

            GameObject floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _cleanup.Add(floor);
            floor.transform.position = new Vector3(0, -.5f, 40);
            floor.transform.localScale = new Vector3(100, 1, 200);
            int level = LayerMask.NameToLayer(GameConfig.LevelLayerName);
            if (level >= 0) floor.layer = level;
            Physics.SyncTransforms();
            CheckClass(warrior, config, boots, -10f, false);
            CheckClass(archer, config, boots, 10f, true);
            Check(EditorJsonUtility.ToJson(warrior) == classBefore && EditorJsonUtility.ToJson(boots) == bootsBefore &&
                EditorJsonUtility.ToJson(profile) == profileBefore, "all shared class, item and profile assets unchanged at runtime");
        }

        private void CheckCapturedProfile(CharacterClassData warrior, AttributeCurveProfile source)
        {
            CharacterClassData owner = Clone(warrior);
            AttributeCurveProfile profile = Clone(source);
            owner.attributeCurveProfile = profile;
            profile.movement = new[] { new CurvePoint(1, 1.25f), new CurvePoint(30, 1.25f) };
            var stats = new StatsComponent();
            stats.Initialize(owner);
            Close(stats.Derived.MoveSpeed, 5.375f, "Initialize captures custom profile");
            profile.movement[0].value = 1.5f;
            profile.movement[1].value = 1.5f;
            stats.Rebuild(null);
            Close(stats.MoveSpeed, 5.375f, "definition rebuild preserves immutable captured points");
            stats.RebuildFromInstances(null);
            Close(stats.Derived.MoveSpeed, 5.375f, "instance rebuild preserves immutable captured points");
            Close(stats.ResolveMoveSpeed(1, 0), 5.375f, "Derived and resolver agree after profile mutation");
            stats.Initialize(owner);
            Close(stats.MoveSpeed, 6.45f, "new class initialization captures newly authored profile");
            owner.attributeCurveProfile = null;
            stats.Initialize(owner);
            Close(stats.MoveSpeed, 4.3f, "missing profile uses identical defaults");
            owner.dexterity = 11;
            stats.Initialize(owner);
            Close(stats.MoveSpeedFactor, 1.0125f, "fallback also interpolates odd DEX");
            owner.attributeCurveProfile = profile;
            profile.movement = new[] { new CurvePoint(1, 0), new CurvePoint(30, 1) };
            Check(!profile.TryCaptureMovement(out _, out _), "non-positive movement profile refused");
            profile.movement = new[] { new CurvePoint(1, 1), new CurvePoint(1, 2) };
            Check(!profile.TryCaptureMovement(out _, out _), "duplicate-score movement profile refused");
            // CaptureMovement intentionally emits one warning for malformed authored data.
            stats.Initialize(owner);
            Close(stats.MoveSpeedFactor, 1.0125f, "invalid profile safely falls back to default");
            Close(stats.ResolveMoveSpeed(3, 0), 8f, "walking ceiling preserved");
            Close(stats.ResolveMoveSpeed(.1f, 0), 2f, "walking floor preserved");
            Close(stats.ResolveMoveSpeed(1.18f, .1f), 5.237425f, "walking multiplier before flat");
            Close(stats.ResolveAuthoredSpeed(1.7f, 1, 0), 1.72125f, "authored crouch stays below walking floor");
            Close(stats.ResolveAuthoredSpeed(10f, 1, 0), 10.125f, "authored sprint stays above walking ceiling");
        }

        private void CheckClass(CharacterClassData data, GameConfig config, ItemData boots, float x, bool archer)
        {
            GameObject body = new GameObject("D4a_" + data.classId);
            _cleanup.Add(body);
            body.transform.position = new Vector3(x, .05f, 0);
            PlayerRig rig = body.AddComponent<PlayerRig>();
            rig.WireRig();
            body.AddComponent<Inventory>();
            body.AddComponent<RangedCombat>();
            body.AddComponent<ClassLoadout>();
            rig.WireRig();
            rig.Inventory.Initialize(rig);
            rig.Equipment.Initialize(rig);
            rig.ApplyClass(data, config);
            rig.Movement.ExternalDriven = true;
            Physics.SyncTransforms();
            EffectiveStatsSnapshot bare = rig.EffectiveStats;
            Close(rig.AttributeStats.MoveSpeed, archer ? 4.6225f : 4.3f, "literal attribute class baseline");
            Close(bare.WalkSpeed, archer ? 5.315875f : 4.3f, "literal effective class baseline with passive");
            Check(bare.MaxHealth == (archer ? 90 : 120) && bare.MaxMana == (archer ? 90 : 70),
                "HP and mana formulas preserved on both classes");
            Close(bare.ManaRegenPerSecond, archer ? 4f : 3f, "mana regen unchanged");
            Close(rig.AttributeStats.GetWeaponDamage(data.primaryWeapon), archer ? 38.08f : 29.12f,
                "weapon modifier math unchanged");
            rig.Health.SetHealth(archer ? 60f : 80f);
            Check(EmberdelveKitFixtures.SpendMana(rig.Mana, 30), "spend mana through real pool");
            float hp = rig.CurrentHealth, mp = rig.Mana.CurrentMana;
            Check(rig.Inventory.TryAdd(boots, 1), "carry actual authored boots in live inventory");
            Check(rig.Equipment.TryEquipItem(boots, out string reason), "wear actual boots: " + reason);
            EffectiveStatsSnapshot worn = rig.EffectiveStats;
            Check(worn.Attributes.Effective.Dexterity == (archer ? 17 : 11), "actual worn DEX score");
            Check(rig.AttributeStats.GetModifier(SixAttribute.Dexterity) == (archer ? 3 : 0),
                "odd score still displays old D&D modifier");
            Close(rig.AttributeStats.MoveSpeedFactor, archer ? 1.085f : 1.0125f, "odd DEX now changes locomotion");
            Close(worn.Derived.MoveSpeed, archer ? 4.6655f : 4.35375f, "Derived uses same score curve");
            Close(worn.WalkSpeed, archer ? 6.3310835f : 5.137425f, "actual gear/passive order before held penalties");
            Close(worn.SprintSpeed, archer ? 8.943872f : 5.8968f, "boots sprint stays independently authored and unclamped");
            Close(rig.Movement.ResolveWalkSpeed(), archer ? 6.3310835f : 5.137425f, "movement reads same effective capture");
            Check(rig.CurrentHealth == hp && rig.Mana.CurrentMana == mp, "equip does not refill either pool");
            Close(bare.WalkSpeed, archer ? 5.315875f : 4.3f, "old effective capture remains immutable after equip");
            RealWalking(rig, archer ? 6.24666905f : 4.8291795f);
            Check(rig.Equipment.TryUnequip(EquipmentSlot.Feet, out reason), "unequip actual boots: " + reason);
            Close(rig.EffectiveStats.WalkSpeed, archer ? 5.315875f : 4.3f, "unequip exact baseline restoration");
            Close(worn.WalkSpeed, archer ? 6.3310835f : 5.137425f, "old worn capture remains immutable after unequip");
            Check(rig.CurrentHealth == hp && rig.Mana.CurrentMana == mp, "unequip and walking do not refill either pool");
            RealWalking(rig, archer ? 5.2449966667f : 4.042f);
        }

        private void RealWalking(PlayerRig rig, float literalPhysicalSpeed)
        {
            var frame = new MoveInputFrame { MoveY = 1f, Yaw = 0f };
            const float dt = 1f / 60f;
            for (int i = 0; i < 120; i++) rig.Movement.Step(in frame, dt);
            Vector3 start = rig.transform.position;
            for (int i = 0; i < 60; i++) rig.Movement.Step(in frame, dt);
            Vector3 delta = rig.transform.position - start;
            Close(new Vector2(delta.x, delta.z).magnitude, literalPhysicalSpeed,
                "one second of CharacterController walking, including authored held penalties", .005f);
            Check(rig.Movement.IsGrounded, "physical walking remains on the real floor");
        }
    }
}
