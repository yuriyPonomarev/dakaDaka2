using System;
using System.Collections.Generic;
using Emberdelve.Combat;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Items;
using Emberdelve.Player;
using Emberdelve.Stats;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Emberdelve.EditorTools
{
    [InitializeOnLoad]
    public static class EmberdelveActionSpeedGate
    {
        private const string Pending = "Emberdelve.ActionSpeed.Pending";
        private const string Done = "Emberdelve.ActionSpeed.Done";
        private const string Code = "Emberdelve.ActionSpeed.Code";
        private static double _requestedAt;
        static EmberdelveActionSpeedGate()
        {
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.EnteredPlayMode && SessionState.GetBool(Pending, false))
                {
                    SessionState.SetBool(Pending, false);
                    new GameObject("D4b1_ActionSpeedGate").AddComponent<ActionSpeedHarness>();
                }
                if (state != PlayModeStateChange.ExitingPlayMode || !SessionState.GetBool(Done, false)) return;
                int code = SessionState.GetInt(Code, 1);
                SessionState.SetBool(Done, false);
                EditorApplication.delayCall += () => EmberdelveGateExit.Exit(code);
            };
        }
        [MenuItem("Emberdelve/Validation/DEX Melee Action Speed (D4b1)")]
        public static void Run()
        {
            try
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Run from edit mode.");
                for (int i = 0; i < SceneManager.sceneCount; i++)
                    if (SceneManager.GetSceneAt(i).isDirty) throw new InvalidOperationException("Save open scenes before running the gate.");
                EmberdelveBootstrapMenu.BuildDataOnly();
                SessionState.SetBool(Pending, true);
                SessionState.SetBool(Done, false);
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                _requestedAt = EditorApplication.timeSinceStartup;
                EditorApplication.update += Watchdog;
                EditorApplication.isPlaying = true;
            }
            catch (Exception exception) { Debug.LogException(exception); EmberdelveGateExit.Exit(1); }
        }
        private static void Watchdog()
        {
            if (EditorApplication.isPlaying) { EditorApplication.update -= Watchdog; return; }
            if (EditorApplication.timeSinceStartup - _requestedAt < 45) return;
            EditorApplication.update -= Watchdog;
            SessionState.SetBool(Pending, false);
            Debug.LogError("[ActionSpeed] Play mode did not start.");
            EmberdelveGateExit.Exit(1);
        }
        internal static void Finish(int checks, int failures)
        {
            Debug.Log("[Emberdelve][ActionSpeed] " + (failures == 0 ? "PASS" : "FAIL") + " checks=" + checks + " failures=" + failures);
            SessionState.SetInt(Code, failures == 0 ? 0 : 1);
            SessionState.SetBool(Done, true);
            EditorApplication.isPlaying = false;
        }
    }

    public sealed class ActionSpeedHarness : MonoBehaviour
    {
        private int _checks, _failures;
        private float _clock;
        private LocalCombatAuthority _authority;
        private GameConfig _config;
        private WeaponData _sword;
        private ItemData _boots;
        private readonly List<UnityEngine.Object> _cleanup = new List<UnityEngine.Object>();
        private readonly List<ICombatEntity> _registered = new List<ICombatEntity>();
        private void Check(bool ok, string label) { _checks++; if (ok) return; _failures++; Debug.LogError("[ActionSpeed] mismatch " + label); }
        private void Close(float actual, float expected, string label, float tolerance = .00002f) =>
            Check(Mathf.Abs(actual - expected) <= tolerance, label + " actual=" + actual + " expected=" + expected);
        private static T Load<T>(string name) where T : UnityEngine.Object => AssetDatabase.LoadAssetAtPath<T>(EmberdelvePaths.Data + "/" + name);
        private T Clone<T>(T source) where T : UnityEngine.Object { T copy = Instantiate(source); _cleanup.Add(copy); return copy; }
        private void Start()
        {
            try { Execute(); }
            catch (Exception exception) { _failures++; Debug.LogException(exception); }
            finally
            {
                foreach (ICombatEntity actor in _registered) CombatRegistry.Unregister(actor.CombatId, actor);
                foreach (UnityEngine.Object item in _cleanup) if (item != null) Destroy(item);
                EmberdelveActionSpeedGate.Finish(_checks, _failures);
            }
        }
        private void Execute()
        {
            _config = Load<GameConfig>("EmberdelveGameConfig.asset");
            _sword = Load<WeaponData>("Weapon_AshenShortSword.asset");
            _boots = Load<ItemData>("Item_FootpadBoots.asset");
            CharacterClassData warrior = Load<CharacterClassData>("Class_Warrior.asset");
            CharacterClassData archer = Load<CharacterClassData>("Class_Archer.asset");
            Check(_config != null && _sword != null && _boots != null && warrior != null && archer != null, "authored fixtures exist");
            if (_config == null || _sword == null || _boots == null || warrior == null || archer == null) return;
            AttributeCurveProfile authoredProfile = warrior.attributeCurveProfile;
            Check(authoredProfile != null && archer.attributeCurveProfile == authoredProfile, "both actual classes share the authored profile");
            Check(authoredProfile.TryCaptureAction(out PiecewiseLinearCurve authored, out string why), "authored action table validates: " + why);
            float[] xs = { 1, 6, 10, 16, 22, 30 }, ys = { .82f, .92f, 1f, 1.12f, 1.2f, 1.28f };
            Check(authored.Count == 6, "exact action breakpoint count");
            for (int i = 0; i < Math.Min(6, authored.Count); i++) { Close(authored.GetPoint(i).x, xs[i], "action knot score"); Close(authored.GetPoint(i).value, ys[i], "action knot factor"); }
            string swordBefore = EditorJsonUtility.ToJson(_sword), profileBefore = EditorJsonUtility.ToJson(authoredProfile);
            string warriorBefore = EditorJsonUtility.ToJson(warrior), archerBefore = EditorJsonUtility.ToJson(archer), bootsBefore = EditorJsonUtility.ToJson(_boots);
            GameObject host = new GameObject("D4b1_Authority"); _cleanup.Add(host);
            _authority = host.AddComponent<LocalCombatAuthority>();
            var serialized = new SerializedObject(_authority); serialized.FindProperty("config").objectReferenceValue = _config; serialized.ApplyModifiedPropertiesWithoutUndo();
            _authority.SetClock(() => _clock); _authority.AutoStepSwings = false;
            CheckClass(warrior, -20f, false);
            CheckClass(archer, 20f, true);
            CheckSlowClient(warrior);
            CheckCustomRates(warrior);
            CheckCrossWeaponFloor(warrior);
            CheckNeutralEnemy();
            Check(swordBefore == EditorJsonUtility.ToJson(_sword) && profileBefore == EditorJsonUtility.ToJson(authoredProfile) &&
                warriorBefore == EditorJsonUtility.ToJson(warrior) && archerBefore == EditorJsonUtility.ToJson(archer) && bootsBefore == EditorJsonUtility.ToJson(_boots),
                "shared weapons/classes/profile/items never mutated");
        }
        private PlayerRig Rig(CharacterClassData source, float x, WeaponData primary = null, WeaponData secondary = null)
        {
            CharacterClassData owner = Clone(source);
            // Exercise each real class's original scores/passives using the same authored melee weapon.
            owner.primaryWeapon = primary != null ? primary : _sword;
            if (secondary != null) owner.secondaryWeapon = secondary;
            GameObject body = new GameObject("D4b1_" + source.classId); _cleanup.Add(body); body.transform.position = new Vector3(x, 0, 0);
            PlayerRig rig = body.AddComponent<PlayerRig>(); rig.WireRig();
            body.AddComponent<Inventory>(); body.AddComponent<MeleeCombat>(); body.AddComponent<RangedCombat>(); body.AddComponent<ClassLoadout>(); rig.WireRig();
            GameObject anchor = new GameObject("ActionGate_WeaponAnchor"); anchor.transform.SetParent(body.transform, false);
            var wiring = new SerializedObject(rig.Loadout); wiring.FindProperty("weaponAnchor").objectReferenceValue = anchor.transform;
            wiring.ApplyModifiedPropertiesWithoutUndo();
            rig.Inventory.Initialize(rig); rig.Equipment.Initialize(rig); rig.ApplyClass(owner, _config); rig.Movement.ExternalDriven = true;
            Physics.SyncTransforms(); return rig;
        }
        private MeleeSwingIntent Intent(PlayerRig rig)
        {
            rig.TryGetAimPose(out _, out float yaw, out float pitch);
            return new MeleeSwingIntent(rig.CombatId, rig.ActiveWeapon.weaponId, 0, 0, yaw, pitch, CombatRegistry.NextSequence(rig.CombatId));
        }
        private MeleeSwingStatus Begin(PlayerRig rig, string label)
        {
            MeleeSwingIntent intent = Intent(rig);
            Check(_authority.TryBeginMeleeSwing(rig.CombatSender, in intent, out int id, out string reason), label + ": " + reason);
            Check(_authority.TryGetMeleeSwingStatus(id, out MeleeSwingStatus status), "accepted status exists"); return status;
        }
        private void Timing(MeleeSwingStatus status, float rate, float windup, float active, float recovery, float recoil, float interval, string label)
        {
            Close(status.ActionSpeedFactor, rate, label + " factor"); Close(status.Timing.Windup, windup, label + " windup");
            Close(status.Timing.Active, active, label + " active"); Close(status.Timing.Recovery, recovery, label + " recovery");
            Close(status.Timing.WallRecoil, recoil, label + " recoil"); Close(status.RepeatInterval, interval, label + " interval");
        }
        private void CheckClass(CharacterClassData source, float x, bool archer)
        {
            PlayerRig rig = Rig(source, x); _clock = 0;
            EffectiveStatsSnapshot bare = rig.EffectiveStats;
            Close(bare.ActionSpeedFactor, archer ? 1.12f : 1f, "real class action rate");
            rig.Health.SetHealth(40); Check(EmberdelveKitFixtures.SpendMana(rig.Mana, 20), "spend real mana");
            float hp = rig.CurrentHealth, mp = rig.Mana.CurrentMana;
            RangedCombat ranged = rig.GetComponent<RangedCombat>(); int magazine = ranged.Magazine, reserve = ranged.Reserve;
            MeleeCombat melee = rig.GetComponent<MeleeCombat>();
            Check(melee.TryBeginSwing(0), "real MeleeCombat uses sender-bound authority begin");
            Check(_authority.TryGetMeleeSwingStatus(melee.LastSwingId, out MeleeSwingStatus first), "real MeleeCombat accepted status");
            Timing(first, archer ? 1.12f : 1f, archer ? .2142857143f : .24f, archer ? .2678571429f : .3f,
                archer ? .2678571429f : .3f, archer ? .3125f : .35f, archer ? .75f : .84f, "bare literal timing");
            Close(melee.ActiveTiming.Active, archer ? .2678571429f : .3f, "client consumes authority timing once");
            Close(melee.CooldownRemaining, archer ? .75f : .84f, "client consumes complete accepted interval");
            WeaponVisualRig visual = rig.Loadout.Visual;
            Check(visual != null && visual.IsSwinging, "actual first-person visual received the accepted swing");
            if (visual != null)
            {
                visual.ApplySwingTime(archer ? .3482142857f : .39f);
                Vector3 expected = rig.Look.AimTransform.TransformPoint(_sword.swingPatterns[0].ActivePose(.5f).Base);
                Check(Vector3.Distance(visual.BladeBase, expected) < .0001f,
                    "visual reaches authored midpoint at the independent accepted-time literal");
            }
            Check(rig.Inventory.TryAdd(_boots, 1) && rig.Equipment.TryEquipItem(_boots, out string equipReason), "actual boots equipped during swing");
            EffectiveStatsSnapshot worn = rig.EffectiveStats;
            Close(worn.ActionSpeedFactor, archer ? 1.1333333333f : 1.02f, "DEX17/11 interpolates live action factor");
            Close(bare.ActionSpeedFactor, archer ? 1.12f : 1f, "old capture immutable after gear");
            _clock = .1f; _authority.StepSwings();
            _authority.TryGetMeleeSwingStatus(first.SwingId, out MeleeSwingStatus frozen);
            Close(frozen.Timing.Active, first.Timing.Active, "regear cannot retime active record");
            Close(frozen.RepeatInterval, first.RepeatInterval, "regear cannot shorten previous accepted interval");
            MeleeSwingIntent early = Intent(rig);
            Check(!_authority.TryBeginMeleeSwing(rig.CombatSender, in early, out _, out string earlyWhy) && earlyWhy.Contains("progress"), "early begin refused");
            Check(!_authority.TryBeginMeleeSwing(rig.CombatSender, in early, out _, out string replayWhy) && replayWhy.Contains("stale"), "replay refused");
            _clock = first.RepeatInterval - .001f;
            MeleeSwingIntent before = Intent(rig);
            Check(!_authority.TryBeginMeleeSwing(rig.CombatSender, in before, out _, out _), "one millisecond before accepted interval refused");
            _clock = first.RepeatInterval;
            MeleeSwingStatus next = Begin(rig, "next swing accepted at exact prior interval");
            Timing(next, archer ? 1.1333333333f : 1.02f, archer ? .2117647059f : .2352941176f,
                archer ? .2647058824f : .2941176471f, archer ? .2647058824f : .2941176471f,
                archer ? .3088235294f : .3431372549f, archer ? .7411764706f : .8235294118f, "boots literal timing");
            Check(rig.Equipment.TryUnequip(EquipmentSlot.Feet, out _), "boots unequipped during second swing");
            Close(worn.ActionSpeedFactor, archer ? 1.1333333333f : 1.02f, "old worn capture immutable after removal");
            Close(rig.EffectiveStats.ActionSpeedFactor, archer ? 1.12f : 1f, "remove boots restores action baseline");
            Check(rig.CurrentHealth == hp && rig.Mana.CurrentMana == mp && ranged.Magazine == magazine && ranged.Reserve == reserve,
                "melee/regear never refill health, mana or ammunition");
            _clock = 10f; _authority.StepSwings();
        }
        private void SetRate(PlayerRig rig, float factor, bool replaceProfile = false)
        {
            CharacterClassData owner = rig.Class;
            if (replaceProfile) owner.attributeCurveProfile = Clone(owner.attributeCurveProfile);
            owner.attributeCurveProfile.action = new[] { new CurvePoint(1, factor), new CurvePoint(30, factor) };
            rig.AttributeStats.Initialize(owner);
        }
        private WeaponData StaticWeapon(bool tiny = false)
        {
            WeaponData weapon = Clone(_sword);
            SwingPatternData pattern = Clone(_sword.swingPatterns[0]);
            pattern.keys = new[] { new SwingKey(0, new Vector3(0, 0, .3f), 0, 0), new SwingKey(1, new Vector3(0, 0, .3f), 0, 0) };
            if (tiny) { pattern.windupTime = .001f; pattern.activeTime = .02f; pattern.recoveryTime = .001f; pattern.wallRecoilTime = .001f; weapon.cooldown = .01f; }
            weapon.swingPatterns = new[] { pattern }; weapon.comboSteps = new ComboStep[0];
            weapon.impactZones = new ImpactZone[0]; weapon.hitSlowDuration = 0;
            return weapon;
        }
        private void CheckSlowClient(CharacterClassData warrior)
        {
            CharacterClassData lowDex = Clone(warrior);
            lowDex.dexterity = 1;
            PlayerRig rig = Rig(lowDex, 200f); _clock = 0f;
            Close(rig.EffectiveStats.ActionSpeedFactor, .82f, "real DEX1 actor uses the authored low-end rate");
            MeleeCombat melee = rig.GetComponent<MeleeCombat>();
            Check(melee.TryBeginSwing(0), "slow real MeleeCombat begins through its authority");
            Check(_authority.TryGetMeleeSwingStatus(melee.LastSwingId, out MeleeSwingStatus accepted),
                "slow client has an accepted authority record");
            Timing(accepted, .82f, .2926829268f, .3658536585f, .3658536585f,
                .4268292683f, 1.0243902439f, "DEX1 independent timing");
            Close(melee.CooldownRemaining, 1.0243902439f, "slow client HUD uses accepted interval, not raw weapon cooldown");
            Check(melee.CooldownRemaining > _sword.cooldown,
                "a slow actor legitimately needs longer than the authored neutral weapon cooldown");
            _clock = accepted.RepeatInterval; _authority.StepSwings();
        }
        private void CheckCustomRates(CharacterClassData warrior)
        {
            WeaponData weapon = StaticWeapon();
            PlayerRig rig = Rig(warrior, 60f, weapon); _clock = 0;
            SetRate(rig, .5f, true);
            rig.TryGetAimPose(out Vector3 eye, out _, out _);
            var target = new BodyTarget { CombatId = CombatRegistry.AllocateId(), Center = eye + new Vector3(0, 0, 1.2f) };
            CombatRegistry.Register(target.CombatId, target); _registered.Add(target);
            EffectiveStatsSnapshot slowCapture = rig.EffectiveStats;
            MeleeSwingStatus slow = Begin(rig, "slow authored action rate accepted");
            Timing(slow, .5f, .48f, .6f, .6f, .7f, 1.68f, "slow literal timing");
            // Editing a profile alone cannot change a running Stats owner or an immutable old capture.
            rig.Class.attributeCurveProfile.action[0].value = 2f;
            rig.Class.attributeCurveProfile.action[1].value = 2f;
            Close(rig.EffectiveStats.ActionSpeedFactor, .5f, "mutable SO edit is not a runtime action-rate input");
            rig.AttributeStats.Rebuild(null); rig.AttributeStats.RebuildFromInstances(null);
            Close(rig.EffectiveStats.ActionSpeedFactor, .5f, "both rebuild paths preserve captured action curve");
            rig.AttributeStats.Initialize(rig.Class);
            Close(rig.EffectiveStats.ActionSpeedFactor, 2f, "versioned reinitialize refreshes action capture");
            Close(slowCapture.ActionSpeedFactor, .5f, "old effective capture remains slow");
            _clock = 1.2f; _authority.StepSwings();
            _authority.TryGetMeleeSwingStatus(slow.SwingId, out MeleeSwingStatus stillSlow);
            Close(stillSlow.Timing.Active, .6f, "fast regear does not retime old active duration");
            Check(target.Received.Count == 1 && stillSlow.Hits.Count == 1, "slow swing applies one real geometry contact");
            if (target.Received.Count > 0) Close(target.Received[0].Amount, 34.944f, "slow body damage unchanged");
            if (stillSlow.Hits.Count > 0) Check(stillSlow.Hits[0].Sample == Mathf.CeilToInt(stillSlow.SampleCount / 15f),
                "slow contact window retains authored normalized1/15 active phase");
            _clock = slow.RepeatInterval - .001f;
            MeleeSwingIntent early = Intent(rig);
            Check(!_authority.TryBeginMeleeSwing(rig.CombatSender, in early, out _, out _), "fast regear cannot bypass the prior slow floor");
            _clock = slow.RepeatInterval;
            MeleeSwingStatus fast = Begin(rig, "fast swing starts at exact prior accepted interval");
            Timing(fast, 2f, .12f, .15f, .15f, .175f, .42f, "fast literal timing");
            Check(slow.SampleCount > fast.SampleCount, "slower accepted active phase reserves more aim-turn samples");
            target.Received.Clear();
            SetRate(rig, .5f);
            _clock += fast.Timing.ActiveEnd + .001f; _authority.StepSwings();
            _authority.TryGetMeleeSwingStatus(fast.SwingId, out MeleeSwingStatus stillFast);
            Close(stillFast.Timing.Active, .15f, "slow regear does not stretch old active duration");
            if (stillFast.Hits.Count > 0) Check(stillFast.Hits[0].Sample == Mathf.CeilToInt(stillFast.SampleCount / 15f),
                "fast contact window retains authored normalized1/15 active phase");
            Check(target.Received.Count == 1, "fast swing keeps single-hit geometry");
            if (target.Received.Count > 0) Close(target.Received[0].Amount, 34.944f, "fast body damage unchanged");
            _clock = slow.RepeatInterval + fast.RepeatInterval + .001f;
            MeleeSwingStatus slowAgain = Begin(rig, "slow regear does not extend previous fast repeat interval");
            Close(slowAgain.RepeatInterval, 1.68f, "newly accepted slow swing receives its own slow interval");
            _clock = 10f; _authority.StepSwings();

            WeaponData tiny = StaticWeapon(true);
            PlayerRig floorRig = Rig(warrior, 90f, tiny); _clock = 0;
            SetRate(floorRig, 100f, true);
            floorRig.TryGetAimPose(out Vector3 floorEye, out _, out _);
            var floorTarget = new BodyTarget { CombatId = CombatRegistry.AllocateId(), Center = floorEye + new Vector3(0, 0, 1.2f) };
            CombatRegistry.Register(floorTarget.CombatId, floorTarget); _registered.Add(floorTarget);
            MeleeSwingStatus floored = Begin(floorRig, "fast rate with active/cooldown floors accepted");
            Timing(floored, 100f, .00001f, .02f, .00001f, .00001f, .08f, "floored literal timing");
            _clock = floored.Timing.Total + .001f; _authority.StepSwings();
            _authority.TryGetMeleeSwingStatus(floored.SwingId, out MeleeSwingStatus floorDone);
            Check(floorDone.Hits.Count == 1 && floorDone.Hits[0].Sample == floorDone.SampleCount,
                "active floor preserves a full-phase contact window rather than over-scaling it");
            _clock = .079f; MeleeSwingIntent floorEarly = Intent(floorRig);
            Check(!_authority.TryBeginMeleeSwing(floorRig.CombatSender, in floorEarly, out _, out _), "active completion cannot bypass cooldown floor");
            // Use the published float boundary: .1f*.8f is one ULP above the literal .08f.
            // The independent .08 timing literal and the .079 early refusal above remain asserted.
            _clock = floored.RepeatInterval; Begin(floorRig, "next swing accepted at exact cooldown floor");
            _clock = 10f; _authority.StepSwings();
        }
        private void CheckCrossWeaponFloor(CharacterClassData warrior)
        {
            WeaponData longMelee = Clone(_sword); longMelee.cooldown = 2f;
            WeaponData fastRanged = Clone(Load<WeaponData>("Weapon_EmberCrossbow.asset"));
            if (fastRanged == null) throw new InvalidOperationException("Ranged fixture missing.");
            fastRanged.cooldown = .1f; fastRanged.magazineSize = 2;
            PlayerRig rig = Rig(warrior, 120f, longMelee, fastRanged); _clock = 0;
            MeleeSwingStatus melee = Begin(rig, "neutral long-cooldown melee begins");
            Close(melee.RepeatInterval, 1.6f, "neutral2s cooldown follows existing authority.8 grace");
            _clock = .85f; _authority.StepSwings();
            Check(rig.Loadout.SwitchToSlot(1), "real loadout switches to fast ranged after melee ends");
            RangedCombat ranged = rig.GetComponent<RangedCombat>(); int magazine = ranged.Magazine, reserve = ranged.Reserve;
            float hp = rig.CurrentHealth, mp = rig.Mana.CurrentMana;
            AttackIntent Shot() => new AttackIntent(rig.CombatId, rig.Kind, fastRanged, 1f, rig.GetMuzzlePosition(),
                rig.AimDirection, fastRanged.rangedRange, CombatRegistry.NextSequence(rig.CombatId), _clock);
            AttackIntent tooEarly = Shot();
            Check(!_authority.LaunchProjectile(rig.CombatSender, tooEarly, out string reason) && reason.Contains("cooldown"),
                "melee-to-fast-ranged switch cannot bypass frozen prior melee interval");
            Check(ranged.Magazine == magazine && ranged.Reserve == reserve, "early ranged refusal spends no ammo");
            Close(_authority.WeaponCooldownRemaining(rig.CombatId, fastRanged), .75f, "HUD reads remaining prior frozen interval");
            _clock = 1.6f; AttackIntent accepted = Shot();
            Check(_authority.LaunchProjectile(rig.CombatSender, accepted, out reason), "ranged launch accepted at exact prior melee floor: " + reason);
            Close(_authority.WeaponCooldownRemaining(rig.CombatId, fastRanged), .08f, "different accepted attack stamp clears old floor applicability");
            _clock = 1.681f; AttackIntent next = Shot();
            Check(_authority.LaunchProjectile(rig.CombatSender, next, out reason), "next ranged shot follows its own unscaled cooldown: " + reason);
            Check(ranged.Magazine == magazine - 2 && ranged.Reserve == reserve && rig.CurrentHealth == hp && rig.Mana.CurrentMana == mp,
                "accepted ranged shots spend two rounds without health/mana/reserve refill");
        }
        private void CheckNeutralEnemy()
        {
            _clock = 0;
            var enemy = new EnemyActor { CombatId = CombatRegistry.AllocateId(), Weapon = _sword, Eye = new Vector3(150, 1.7f, 0) };
            CombatRegistry.Register(enemy.CombatId, enemy); _registered.Add(enemy);
            var intent = new MeleeSwingIntent(enemy.CombatId, _sword.weaponId, 0, 0, 0, 0, CombatRegistry.NextSequence(enemy.CombatId));
            Check(_authority.TryBeginMeleeSwing(in intent, out int id, out string reason), "non-action-source enemy begins normally: " + reason);
            _authority.TryGetMeleeSwingStatus(id, out MeleeSwingStatus status);
            Timing(status, 1f, .24f, .3f, .3f, .35f, .84f, "enemy authored timings unchanged");
            foreach (float factor in new[] { float.NaN, 0f, -1f, float.PositiveInfinity, float.Epsilon, 1e-20f })
            {
                var rated = new RatedActor { CombatId = CombatRegistry.AllocateId(), Weapon = _sword,
                    Eye = new Vector3(170, 1.7f, 0), Factor = factor };
                CombatRegistry.Register(rated.CombatId, rated); _registered.Add(rated);
                var request = new MeleeSwingIntent(rated.CombatId, _sword.weaponId, 0, 0, 0, 0, CombatRegistry.NextSequence(rated.CombatId));
                Check(_authority.TryBeginMeleeSwing(in request, out int rateId, out reason), "registered rate source accepted safely: " + factor);
                _authority.TryGetMeleeSwingStatus(rateId, out MeleeSwingStatus safe);
                if (factor == 1e-20f) Check(safe.SampleCount == SwingGeometry.MaxSamples && !float.IsInfinity(safe.Timing.Total),
                    "extreme finite duration keeps sample count bounded before int conversion");
                else Timing(safe, 1f, .24f, .3f, .3f, .35f, .84f, "invalid rate or arithmetic falls back to neutral");
            }
        }
        private class EnemyActor : ICombatEntity, IActiveWeaponProvider, IAimPoseProvider
        {
            public int CombatId { get; set; }
            public WeaponData Weapon; public Vector3 Eye;
            public EntityKind Kind => EntityKind.Enemy;
            public int TeamId => CombatTeams.MonsterTeam;
            public Vector3 CombatCenter => Eye;
            public Collider HeadCollider => null;
            public WeaponData ActiveWeapon => Weapon;
            public bool IsDead => false;
            public float CurrentHealth => 1000;
            public float MaxHealth => 1000;
            public void ApplyDamage(DamageInfo damage) { }
            public bool TryGetAimPose(out Vector3 eye, out float yaw, out float pitch) { eye = Eye; yaw = pitch = 0; return true; }
        }
        private sealed class RatedActor : EnemyActor, IActionSpeedSource
        {
            public float Factor;
            public float ActionSpeedFactor => Factor;
        }
        private sealed class BodyTarget : ICombatEntity, IHitShapeProvider
        {
            public int CombatId { get; set; }
            public Vector3 Center;
            public readonly List<DamageInfo> Received = new List<DamageInfo>();
            public EntityKind Kind => EntityKind.Enemy;
            public int TeamId => CombatTeams.MonsterTeam;
            public Vector3 CombatCenter => Center;
            public Collider HeadCollider => null;
            public bool IsDead => false;
            public float CurrentHealth => 1000;
            public float MaxHealth => 1000;
            public void ApplyDamage(DamageInfo damage) => Received.Add(damage);
            public void CollectHitShapes(List<HitShape> shapes) => shapes.Add(new HitShape(HitLocation.Body, Center, Center, .15f));
        }
    }
}
