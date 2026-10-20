using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Emberdelve.AI;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Player;
using Emberdelve.Combat;
using Emberdelve.Skills;

namespace Emberdelve.EditorTools
{
    /// <summary>
    /// R1b gate: skill casts, skill hits, statuses and ammo submitted to LocalCombatAuthority are requests,
    /// never outcomes. Every client controlled field (skill id, weapon, range, hit point, headshot flag,
    /// timestamp, activation id, effect scale, status spec) is forged and the authority must reject it or
    /// ignore it; the legitimate cast beside it must still land with the authority's own numbers.
    ///
    /// Run: Unity -batchmode -nographics -projectPath . -logFile Logs/r1b_skillgate.log
    ///      -executeMethod Emberdelve.EditorTools.EmberdelveSkillAuthorityGate.Run
    /// The authority clock is injected (internal SetClock, visible to this Editor assembly), so cooldowns and
    /// activation windows are advanced deterministically and a timestamp inside an intent is provably irrelevant.
    /// </summary>
    [InitializeOnLoad]
    public static class EmberdelveSkillAuthorityGate
    {
        private const string Pending = "Emberdelve.SkillAuthorityGate.Pending";

        static EmberdelveSkillAuthorityGate()
        {
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.EnteredPlayMode && SessionState.GetBool(Pending, false))
                {
                    SessionState.SetBool(Pending, false);
                    new GameObject("SkillAuthority_Gate").AddComponent<SkillAuthorityHarness>();
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

    /// <summary>A real, collider-backed hostile for the end-to-end Battle Cry cast.</summary>
    public sealed class SkillGateBody : MonoBehaviour, ICombatEntity, IStatusTarget
    {
        public int CombatId { get; set; }
        public EntityKind Kind => EntityKind.Enemy;
        public int TeamId => CombatTeams.MonsterTeam;
        public Vector3 CombatCenter => transform.position + Vector3.up * 1.1f;
        public Collider HeadCollider => null;
        public bool IsDead => Taken >= 100000f;
        public float CurrentHealth => 100000f - Taken;
        public float MaxHealth => 100000f;
        public float Taken;
        public readonly List<StatusSpec> Statuses = new List<StatusSpec>();

        public void ApplyDamage(DamageInfo damage) => Taken += damage.Amount;
        public void ApplyStatus(StatusSpec spec, int sourceCombatId) => Statuses.Add(spec);
        public void ClearStatuses() => Statuses.Clear();
        public bool HasStatus(StatusKind kind) => Statuses.Exists(s => s.Kind == kind);
    }

    public sealed class SkillAuthorityHarness : MonoBehaviour
    {
        private const string Tag = "[SkillAuthorityGate]";

        private int _checks;
        private int _failures;
        private readonly List<UnityEngine.Object> _cleanup = new List<UnityEngine.Object>();
        private readonly List<ICombatEntity> _registered = new List<ICombatEntity>();

        private LocalCombatAuthority _auth;
        private float _clock = 1000f;

        private SkillData _dash, _cry, _multi, _pierce, _passive;
        private WeaponData _sword, _bow, _rapier;

        /// <summary>A hostile target with a body, an optional head collider and recorded statuses.</summary>
        private sealed class Target : ICombatEntity, IStatusTarget
        {
            public int CombatId { get; set; }
            public EntityKind Kind => EntityKind.Enemy;
            public int TeamId => CombatTeams.MonsterTeam;
            public Vector3 Point;
            public Collider Head;
            public bool Dead;
            public float Taken;
            public DamageInfo Last = DamageInfo.Invalid;
            public readonly List<StatusSpec> Statuses = new List<StatusSpec>();

            public Vector3 CombatCenter => Point;
            public Collider HeadCollider => Head;
            public bool IsDead => Dead;
            public float CurrentHealth => 100000f - Taken;
            public float MaxHealth => 100000f;
            public void ApplyDamage(DamageInfo damage) { Last = damage; Taken += damage.Amount; }
            public void ApplyStatus(StatusSpec spec, int sourceCombatId) => Statuses.Add(spec);
            public void ClearStatuses() => Statuses.Clear();
            public bool HasStatus(StatusKind kind) => Statuses.Exists(s => s.Kind == kind);
        }

        /// <summary>
        /// A skill caster the authority reads through its interfaces only: kit, weapon, mana, ammo, scale and
        /// epoch are all entity state that the test (the "server side") owns and the intents cannot touch.
        /// </summary>
        private sealed class Caster : ICombatEntity, IActiveWeaponProvider, ISkillCasterState, IStatusTarget, IAmmoSource
        {
            public int CombatId { get; set; }
            public EntityKind Kind => EntityKind.LocalPlayer;
            public int TeamId => CombatTeams.PlayerTeam;
            public Vector3 Point = new Vector3(0f, 1.1f, 0f);
            public bool Dead;
            public WeaponData Weapon;
            public List<SkillData> Kit = new List<SkillData>();
            public ManaComponent ManaPool;
            public float Scale = 1f;
            public int Epoch;
            public int Magazine;
            public int Spent;
            public readonly List<StatusSpec> Applied = new List<StatusSpec>();

            public Vector3 CombatCenter => Point;
            public Collider HeadCollider => null;
            public bool IsDead => Dead;
            public float CurrentHealth => 100f;
            public float MaxHealth => 100f;
            public void ApplyDamage(DamageInfo damage) { }
            public WeaponData ActiveWeapon => Weapon;
            public IReadOnlyList<SkillData> ActiveSkills => Kit;
            public ManaComponent Mana => ManaPool;
            public float AbilityEffectScale => Scale;
            public int KitEpoch => Epoch;
            public void ApplyStatus(StatusSpec spec, int sourceCombatId) => Applied.Add(spec);
            public void ClearStatuses() => Applied.Clear();
            public bool HasStatus(StatusKind kind) => Applied.Exists(s => s.Kind == kind);
            public bool HasAmmo(int count) => Magazine >= count;
            public bool TryConsumeAmmo(int count) { if (Magazine < count) return false; Magazine -= count; Spent += count; return true; }
        }

        private void Check(bool ok, string label)
        {
            _checks++;
            if (!ok)
            {
                _failures++;
                Debug.LogError($"{Tag} FAIL {label}");
            }
        }

        private IEnumerator Start()
        {
            yield return null;
            yield return null;

            try { Execute(); }
            catch (Exception e)
            {
                _failures++;
                Debug.LogException(e);
            }
            finally
            {
                foreach (ICombatEntity e in _registered) CombatRegistry.Unregister(e.CombatId, e);
                foreach (UnityEngine.Object o in _cleanup) if (o != null) Destroy(o);
            }

            Debug.Log($"{Tag} {(_failures == 0 ? "PASS" : "FAIL")} checks={_checks} failures={_failures}");
            EditorApplication.Exit(_failures == 0 ? 0 : 1);
        }

        // ---- fixtures -------------------------------------------------------------------------------

        private T Load<T>(string file) where T : UnityEngine.Object
        {
            T asset = AssetDatabase.LoadAssetAtPath<T>(EmberdelvePaths.Data + "/" + file);
            Check(asset != null, "asset available: " + file);
            return asset;
        }

        private Caster NewCaster(WeaponData weapon, int mana, int magazine, params SkillData[] kit)
        {
            GameObject go = new GameObject("SkillGate_Caster");
            _cleanup.Add(go);
            ManaComponent pool = go.AddComponent<ManaComponent>();
            pool.Configure(mana, 0f, 0f);
            Caster c = new Caster
            {
                CombatId = CombatRegistry.AllocateId(),
                Weapon = weapon,
                ManaPool = pool,
                Magazine = magazine,
            };
            c.Kit.AddRange(kit);
            Register(c);
            return c;
        }

        private Target NewTarget(Vector3 point)
        {
            Target t = new Target { CombatId = CombatRegistry.AllocateId(), Point = point };
            Register(t);
            return t;
        }

        private void Register(ICombatEntity entity)
        {
            CombatRegistry.Register(entity.CombatId, entity);
            _registered.Add(entity);
        }

        private bool Begin(Caster c, string skillId, out int activation, out string reason)
        {
            return _auth.TryBeginSkillCast(c.CombatId, skillId, out activation, out reason);
        }

        private struct Outcome
        {
            public bool Accepted;
            public string Reason;
            public float Damage;
            public DamageInfo Info;
        }

        /// <summary>One skill hit request. Every parameter after the activation is something a client controls.</summary>
        private Outcome Hit(Caster c, int activation, SkillData claimedSkill, WeaponData claimedWeapon, Target t, Vector3 point,
            float maxRange, bool headFlag = false, float timestamp = 0f, StatusSpec status = default, float scale = 1f,
            int? sequence = null, int? casterId = null)
        {
            int id = casterId ?? c.CombatId;
            int seq = sequence ?? CombatRegistry.NextSequence(id);
            SkillIntent intent = new SkillIntent(id, EntityKind.LocalPlayer, claimedSkill, claimedWeapon, c.Point,
                point - c.Point, maxRange, seq, timestamp, status, activation, scale);

            float before = t.Taken;
            t.Last = DamageInfo.Invalid;
            bool ok = _auth.SubmitSkill(intent, new HitCandidate(t.CombatId, point, headFlag), out string reason);
            return new Outcome { Accepted = ok, Reason = reason ?? string.Empty, Damage = t.Taken - before, Info = t.Last };
        }

        private Outcome HonestPierceHit(Caster c, int activation, Target t) =>
            Hit(c, activation, _pierce, _bow, t, t.Point, Mathf.Max(1f, _bow.rangedRange));

        private Outcome HonestCryHit(Caster c, int activation, Target t) =>
            Hit(c, activation, _cry, _sword, t, t.Point, _cry.radius);

        private static bool Has(string text, string needle) => text != null && text.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

        private void AdvanceClock(float seconds) => _clock += seconds;

        private float ExpectedWeaponSkill(Caster c, SkillData skill, DamageInfo info, bool head)
        {
            float distance = Vector3.Distance(info.SourcePosition, info.Point);
            return _bow.DamageAtRange(distance) * skill.damageMultiplier * c.Scale * (head ? DamageResolver.HeadshotMultiplier : 1f);
        }

        // ---- the gate -------------------------------------------------------------------------------

        private void Execute()
        {
            PlayerRig rig = FindFirstObjectByType<PlayerRig>();
            Check(rig != null && !rig.IsDead, "a living player rig is available");
            if (rig == null) return;

            // Enemies of the scene must not act on the injected clock or hit the rig while the gate runs.
            foreach (EnemyAgent enemy in FindObjectsByType<EnemyAgent>(FindObjectsSortMode.None)) enemy.gameObject.SetActive(false);

            _dash = Load<SkillData>("Skill_WarriorDash.asset");
            _cry = Load<SkillData>("Skill_WarriorBattleCry.asset");
            _multi = Load<SkillData>("Skill_ArcherMultiShot.asset");
            _pierce = Load<SkillData>("Skill_ArcherPiercingShot.asset");
            _passive = Load<SkillData>("Skill_WarriorAshenMight.asset");
            _sword = rig.MainHandWeapon;
            _bow = Load<WeaponData>("Weapon_HollowBow.asset");
            _rapier = Load<WeaponData>("Weapon_EmberRapier.asset");
            CharacterClassData archerClass = Load<CharacterClassData>("Class_Archer.asset");
            if (_dash == null || _cry == null || _multi == null || _pierce == null || _passive == null || _sword == null || _bow == null || _rapier == null || archerClass == null) return;

            Check(_cry.UsesFlatDamage && _cry.HitBudget == _cry.maxTargets, $"Battle Cry budget is its target cap ({_cry.HitBudget})");
            Check(_multi.HitBudget == 3 && _multi.AmmoPerCast == 3, "Multi-shot budget 3 hits and 3 rounds");
            Check(_pierce.HitBudget == 2 && _pierce.AmmoPerCast == 1, "Piercing Shot budget 2 hits and 1 round");
            Check(_dash.HitBudget == 0, "Dash resolves no hits");

            GameConfig cfg = ScriptableObject.CreateInstance<GameConfig>();
            _cleanup.Add(cfg);
            cfg.levelMask = 0;
            GameObject go = new GameObject("SkillAuthorityGate_Authority");
            _cleanup.Add(go);
            _auth = go.AddComponent<LocalCombatAuthority>();
            typeof(LocalCombatAuthority).GetField("config", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(_auth, cfg);
            _auth.SetClock(() => _clock);

            // The API surface itself: no way left to hand the authority a status spec.
            Check(typeof(ICombatAuthority).GetMethod("ApplyStatus") == null && typeof(LocalCombatAuthority).GetMethod("ApplyStatus") == null,
                "the authority exposes no ApplyStatus(spec) any more: a self status can only come from the skill asset");
            MethodInfo setClock = typeof(LocalCombatAuthority).GetMethod("SetClock", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Check(setClock != null && !setClock.IsPublic, "SetClock is not public API");

            // ================= 1. foreign / unknown skill, kit membership =================
            Caster warrior = NewCaster(_sword, 100, 0, _dash, _cry, _passive);
            int rejectedBefore = _auth.RejectedCount;
            int openBefore = _auth.OpenActivationCount;

            bool b = Begin(warrior, "archer_piercing_shot", out int act, out string reason);
            Check(!b && act == 0 && Has(reason, "not in the caster's kit"), "a real skill of another class is not in the kit (" + reason + ")");
            b = Begin(warrior, "no_such_skill", out act, out reason);
            Check(!b && Has(reason, "not in the caster's kit"), "an unknown skill id is rejected (" + reason + ")");
            b = Begin(warrior, _passive.skillId, out act, out reason);
            Check(!b && Has(reason, "not in the caster's kit"), "a passive in the kit is not an active skill (" + reason + ")");
            b = Begin(warrior, "", out act, out reason);
            Check(!b && Has(reason, "skill id missing"), "an empty skill id is rejected (" + reason + ")");
            b = Begin(warrior, null, out act, out reason);
            Check(!b && Has(reason, "skill id missing"), "a null skill id is rejected (" + reason + ")");
            Check(warrior.ManaPool.CurrentMana == 100f && _auth.OpenActivationCount == openBefore && _auth.RejectedCount == rejectedBefore + 5,
                "five refusals spent no mana, opened no activation and were counted as rejections");

            Caster noKit = NewCaster(_sword, 100, 0);
            noKit.Kit = null;
            b = Begin(noKit, _cry.skillId, out act, out reason);
            Check(!b && Has(reason, "not in the caster's kit"), "a caster with no kit casts nothing (" + reason + ")");
            b = _auth.TryBeginSkillCast(987654, _cry.skillId, out act, out reason);
            Check(!b && Has(reason, "unknown instigator"), "an unknown caster id is rejected (" + reason + ")");

            // The authority spends from the caster's own pool: a kit skill with the caster's mana too low.
            // ================= 2. insufficient mana: authority state unchanged =================
            Caster poor = NewCaster(_sword, 30, 0, _dash, _cry);
            int open2 = _auth.OpenActivationCount;
            b = Begin(poor, _cry.skillId, out act, out reason);
            Check(!b && Has(reason, "not enough mana"), "Battle Cry (32 MP) is refused with 30 mana (" + reason + ")");
            Check(poor.ManaPool.CurrentMana == 30f && _auth.OpenActivationCount == open2 && poor.Applied.Count == 0,
                "a refused cast spent no mana, opened no activation and applied no self status");
            poor.ManaPool.Configure(100, 0f, 0f);
            b = Begin(poor, _cry.skillId, out act, out reason);
            Check(b && act > 0, "the next cast is accepted at once: the refusal never stamped a cooldown (" + reason + ")");
            Check(Mathf.Abs(poor.ManaPool.CurrentMana - (100 - _cry.EffectiveManaCost)) < 0.001f, "the authority spent exactly the skill's mana cost once");

            // ================= 3. wrong weapon =================
            Caster wrongWeapon = NewCaster(_rapier, 100, 5, _multi, _pierce);
            b = Begin(wrongWeapon, _multi.skillId, out act, out reason);
            Check(!b && Has(reason, "ranged weapon"), "Multi-shot with a rapier in hand is refused (" + reason + ")");
            b = Begin(wrongWeapon, _pierce.skillId, out act, out reason);
            Check(!b && Has(reason, "ranged weapon"), "Piercing Shot with a rapier in hand is refused (" + reason + ")");
            Check(wrongWeapon.ManaPool.CurrentMana == 100f && wrongWeapon.Magazine == 5 && wrongWeapon.Spent == 0,
                "the wrong-weapon refusals spent neither mana nor ammo");
            Caster unarmed = NewCaster(null, 100, 5, _multi);
            b = Begin(unarmed, _multi.skillId, out act, out reason);
            Check(!b && Has(reason, "ranged weapon"), "an unarmed caster cannot fire Multi-shot (" + reason + ")");
            Caster crier = NewCaster(null, 100, 0, _cry);
            b = Begin(crier, _cry.skillId, out act, out reason);
            Check(b, "Battle Cry needs no weapon at all (" + reason + ")");

            // ================= 4. no ammo =================
            Caster dry = NewCaster(_bow, 100, 0, _multi, _pierce);
            b = Begin(dry, _pierce.skillId, out act, out reason);
            Check(!b && Has(reason, "ammunition"), "Piercing Shot with an empty magazine is refused (" + reason + ")");
            dry.Magazine = 2;
            b = Begin(dry, _multi.skillId, out act, out reason);
            Check(!b && Has(reason, "ammunition"), "Multi-shot needs 3 rounds and 2 are not enough (" + reason + ")");
            Check(dry.ManaPool.CurrentMana == 100f && dry.Magazine == 2 && dry.Spent == 0, "the ammo refusals spent neither mana nor ammo");
            dry.Magazine = 3;
            b = Begin(dry, _multi.skillId, out act, out reason);
            Check(b && dry.Magazine == 0 && dry.Spent == 3, "with exactly 3 rounds Multi-shot is accepted and the authority spent exactly 3 (once)");
            Check(Mathf.Abs(dry.ManaPool.CurrentMana - (100 - _multi.EffectiveManaCost)) < 0.001f, "and the authority spent the mana too");

            // ================= 5. dead caster =================
            Caster ghost = NewCaster(_sword, 100, 0, _dash, _cry);
            Check(Begin(ghost, _cry.skillId, out int ghostAct, out reason), "ghost opens a cast while alive");
            Target ghostTarget = NewTarget(ghost.Point + Vector3.forward * 2f);
            ghost.Dead = true;
            b = Begin(ghost, _dash.skillId, out act, out reason);
            Check(!b && Has(reason, "dead"), "a dead caster cannot open a cast (" + reason + ")");
            Outcome deadHit = HonestCryHit(ghost, ghostAct, ghostTarget);
            Check(!deadHit.Accepted && Has(deadHit.Reason, "dead") && deadHit.Damage == 0f, "a dead caster's hit on an open activation is rejected (" + deadHit.Reason + ")");

            // ================= 6. positive casts: Dash, Battle Cry (4 targets), Multi-shot, Piercing Shot =================
            Caster w = NewCaster(_sword, 100, 0, _dash, _cry);
            Check(Begin(w, _dash.skillId, out int dashAct, out reason) && dashAct > 0, "Dash cast accepted (" + reason + ")");
            Check(Mathf.Abs(w.ManaPool.CurrentMana - (100 - _dash.EffectiveManaCost)) < 0.001f, "Dash spent its mana through the authority");
            Check(w.Applied.Count == 0, "Dash applies no self status");
            Target dashTarget = NewTarget(w.Point + Vector3.forward * 2f);
            Outcome dashHit = Hit(w, dashAct, _dash, _sword, dashTarget, dashTarget.Point, 3f);
            Check(!dashHit.Accepted && Has(dashHit.Reason, "hit budget") && dashHit.Damage == 0f, "a Dash activation cannot be turned into an attack (" + dashHit.Reason + ")");

            Check(Begin(w, _cry.skillId, out int cryAct, out reason) && cryAct > 0, "Battle Cry cast accepted (" + reason + ")");
            Check(Mathf.Abs(w.ManaPool.CurrentMana - (100 - _dash.EffectiveManaCost - _cry.EffectiveManaCost)) < 0.001f, "Battle Cry spent its mana through the authority");
            Check(w.Applied.Count == 0, "no self buff while the cast is open: a cry does not empower its own damage");
            List<Target> victims = new List<Target>();
            for (int i = 0; i < _cry.maxTargets; i++) victims.Add(NewTarget(w.Point + new Vector3(i * 0.3f, 0f, 2f)));
            int accepted = 0;
            foreach (Target v in victims)
            {
                Outcome o = HonestCryHit(w, cryAct, v);
                if (o.Accepted) accepted++;
                Check(o.Accepted && Mathf.Abs(o.Damage - _cry.flatDamage) < 0.001f && !o.Info.Headshot,
                    $"Battle Cry victim takes exactly the asset's flat {_cry.flatDamage} ({o.Damage:F2}, {o.Reason})");
                Check(v.Statuses.Count == 1 && v.Statuses[0].Kind == _cry.targetStatus, "victim got exactly the asset's slow");
            }
            Check(accepted == _cry.maxTargets, $"all {_cry.maxTargets} legitimate Battle Cry targets landed");
            Target fifth = NewTarget(w.Point + new Vector3(0f, 0f, 3f));
            Outcome fifthHit = HonestCryHit(w, cryAct, fifth);
            Check(!fifthHit.Accepted && Has(fifthHit.Reason, "hit budget") && fifthHit.Damage == 0f && fifth.Statuses.Count == 0,
                "a fifth target is past the activation's hit budget (" + fifthHit.Reason + ")");
            StatusSpec expectedSelf = LocalCombatAuthority.ScaleStatus(_cry.SelfStatusSpec, 1f);
            Check(_auth.CompleteSkillCast(w.CombatId, cryAct), "the caster completes the cast");
            Check(w.Applied.Count == 1 && w.Applied[0].Kind == expectedSelf.Kind
                  && Mathf.Abs(w.Applied[0].Magnitude - expectedSelf.Magnitude) < 0.0001f && Mathf.Abs(w.Applied[0].Duration - expectedSelf.Duration) < 0.0001f,
                "the self buff is the asset's Empowered spec, applied once by the authority at completion");
            Check(!_auth.CompleteSkillCast(w.CombatId, cryAct) && w.Applied.Count == 1, "a second completion applies nothing (no stacked buff)");
            Outcome afterClose = HonestCryHit(w, cryAct, NewTarget(w.Point + new Vector3(0f, 0f, 2.5f)));
            Check(!afterClose.Accepted && Has(afterClose.Reason, "closed") && afterClose.Damage == 0f, "a completed activation takes no further hit (" + afterClose.Reason + ")");

            Caster archer = NewCaster(_bow, 100, 10, _multi, _pierce);
            Check(Begin(archer, _multi.skillId, out int multiAct, out reason), "Multi-shot cast accepted (" + reason + ")");
            Check(archer.Magazine == 7 && archer.Spent == 3, "Multi-shot spent exactly 3 rounds");
            Target[] arrowTargets = { NewTarget(archer.Point + new Vector3(-1f, 0f, 15f)), NewTarget(archer.Point + new Vector3(0f, 0f, 15f)), NewTarget(archer.Point + new Vector3(1f, 0f, 15f)) };
            for (int i = 0; i < 3; i++)
            {
                Outcome o = Hit(archer, multiAct, _multi, _bow, arrowTargets[i], arrowTargets[i].Point, _bow.rangedRange);
                Check(o.Accepted && Mathf.Abs(o.Damage - ExpectedWeaponSkill(archer, _multi, o.Info, false)) < 0.01f,
                    $"volley arrow {i + 1} lands with weapon falloff x damageMultiplier ({o.Damage:F2}, {o.Reason})");
            }
            Outcome fourthArrow = Hit(archer, multiAct, _multi, _bow, NewTarget(archer.Point + new Vector3(0f, 0f, 10f)), archer.Point + new Vector3(0f, 0f, 10f), _bow.rangedRange);
            Check(!fourthArrow.Accepted && Has(fourthArrow.Reason, "hit budget"), "a fourth hit on a 3-arrow volley is past the budget (" + fourthArrow.Reason + ")");

            AdvanceClock(_pierce.cooldownSeconds + 1f);
            Check(Begin(archer, _pierce.skillId, out int pierceAct, out reason), "Piercing Shot cast accepted (" + reason + ")");
            Check(archer.Magazine == 6 && archer.Spent == 4, "Piercing Shot spent exactly one round");
            Target pA = NewTarget(archer.Point + new Vector3(0f, 0f, 12f)), pB = NewTarget(archer.Point + new Vector3(0f, 0f, 18f)), pC = NewTarget(archer.Point + new Vector3(0f, 0f, 24f));
            Outcome pierceA = HonestPierceHit(archer, pierceAct, pA);
            Outcome pierceB = HonestPierceHit(archer, pierceAct, pB);
            Check(pierceA.Accepted && pierceB.Accepted, "the piercing arrow resolves its 2 legitimate bodies (" + pierceA.Reason + " / " + pierceB.Reason + ")");
            Check(Mathf.Abs(pierceA.Damage - ExpectedWeaponSkill(archer, _pierce, pierceA.Info, false)) < 0.01f, "the first body takes weapon falloff x 1.35");
            Outcome pierceC = HonestPierceHit(archer, pierceAct, pC);
            Check(!pierceC.Accepted && Has(pierceC.Reason, "hit budget") && pierceC.Damage == 0f, "a third body is past the piercing budget of 2 (" + pierceC.Reason + ")");

            // ================= 7. activation id reuse / forged ids =================
            // The exhausted activation cannot be used again, so it cannot be used to keep hitting without a cast.
            Outcome reuseSame = HonestPierceHit(archer, pierceAct, pA);
            Check(!reuseSame.Accepted && reuseSame.Damage == 0f, "re-sending a spent activation on a body it already hit is rejected (" + reuseSame.Reason + ")");
            Target fresh = NewTarget(archer.Point + new Vector3(2f, 0f, 14f));
            Outcome reuseFresh = HonestPierceHit(archer, pierceAct, fresh);
            Check(!reuseFresh.Accepted && Has(reuseFresh.Reason, "hit budget") && reuseFresh.Damage == 0f, "a spent activation id buys no hit on a fresh body either (" + reuseFresh.Reason + ")");
            b = Begin(archer, _pierce.skillId, out act, out reason);
            Check(!b && Has(reason, "cooldown"), "a new cast of Piercing Shot is refused while the cooldown runs (" + reason + ")");

            foreach (int forged in new[] { 0, -3, 99999999, pierceAct + 1000 })
            {
                Outcome o = HonestPierceHit(archer, forged, fresh);
                Check(!o.Accepted && Has(o.Reason, "no open activation") && o.Damage == 0f, $"a forged activation id {forged} is rejected ({o.Reason})");
            }

            AdvanceClock(_pierce.cooldownSeconds + 1f);
            Check(Begin(archer, _pierce.skillId, out int pierceAct2, out reason), "after the cooldown a genuine new cast opens a new activation (" + reason + ")");
            Check(pierceAct2 != pierceAct, "and gets a fresh authority-allocated id");
            Outcome oldId = HonestPierceHit(archer, pierceAct, fresh);
            Check(!oldId.Accepted && Has(oldId.Reason, "no open activation") && oldId.Damage == 0f, "the old id is gone once the next cast opened (" + oldId.Reason + ")");

            // Window: an unused activation expires.
            AdvanceClock(60f);
            Outcome late = HonestPierceHit(archer, pierceAct2, fresh);
            Check(!late.Accepted && Has(late.Reason, "window expired") && late.Damage == 0f, "an activation used after its window is rejected (" + late.Reason + ")");

            // Another entity's id / a different skill's id.
            Caster other = NewCaster(_bow, 100, 10, _multi, _pierce);
            Check(Begin(other, _pierce.skillId, out int otherAct, out reason), "a second archer opens its own cast");
            Outcome stolen = Hit(archer, otherAct, _pierce, _bow, fresh, fresh.Point, _bow.rangedRange);
            Check(!stolen.Accepted && Has(stolen.Reason, "belongs to another entity") && stolen.Damage == 0f, "an activation id of another entity is rejected (" + stolen.Reason + ")");
            Outcome wrongSkill = Hit(other, otherAct, _multi, _bow, fresh, fresh.Point, _bow.rangedRange);
            Check(!wrongSkill.Accepted && Has(wrongSkill.Reason, "not the skill"), "an activation opened for Piercing Shot cannot carry a Multi-shot hit (" + wrongSkill.Reason + ")");
            Outcome nullSkill = Hit(other, otherAct, null, _bow, fresh, fresh.Point, _bow.rangedRange);
            Check(!nullSkill.Accepted && Has(nullSkill.Reason, "not the skill"), "a hit with no skill is rejected (" + nullSkill.Reason + ")");
            Outcome sameTarget = HonestPierceHit(other, otherAct, fresh);
            Outcome sameTargetAgain = HonestPierceHit(other, otherAct, fresh);
            Check(sameTarget.Accepted && !sameTargetAgain.Accepted && Has(sameTargetAgain.Reason, "already hit"), "one activation cannot hit the same body twice (" + sameTargetAgain.Reason + ")");

            // ================= 8. forged timestamps cannot touch the cooldown or the window =================
            Caster timed = NewCaster(_sword, 100, 0, _dash, _cry);
            Check(Begin(timed, _cry.skillId, out int timedAct, out reason), "Battle Cry opens at clock T");
            Target timedTarget = NewTarget(timed.Point + Vector3.forward * 2f);
            Outcome futureStamp = Hit(timed, timedAct, _cry, _sword, timedTarget, timedTarget.Point, _cry.radius, timestamp: _clock + 100000f);
            Check(futureStamp.Accepted && Mathf.Abs(futureStamp.Damage - _cry.flatDamage) < 0.001f, "a far-future timestamp does not change the hit (" + futureStamp.Reason + ")");
            AdvanceClock(2f);
            b = Begin(timed, _cry.skillId, out act, out reason);
            Check(!b && Has(reason, "cooldown"), "2s later the cooldown still holds on the authority clock (" + reason + ")");
            Target timedTarget2 = NewTarget(timed.Point + new Vector3(1f, 0f, 2f));
            Outcome pastStamp = Hit(timed, timedAct, _cry, _sword, timedTarget2, timedTarget2.Point, _cry.radius, timestamp: -500f);
            Check(!pastStamp.Accepted && Has(pastStamp.Reason, "window expired"), "a past timestamp does not keep the window open (" + pastStamp.Reason + ")");
            AdvanceClock(_cry.cooldownSeconds);
            Check(Begin(timed, _cry.skillId, out act, out reason), "the cooldown ends on the authority clock, whatever stamp the intents carried (" + reason + ")");

            // Kit epoch (death, respawn, new class): the authority forgets stamps of an older epoch by itself.
            b = Begin(timed, _cry.skillId, out act, out reason);
            Check(!b && Has(reason, "cooldown"), "the fresh cast is on cooldown");
            timed.Epoch++;
            Check(Begin(timed, _cry.skillId, out act, out reason), "a kit re-application (death/respawn) clears the authority cooldown with the caster's own");


            // ================= 9. there is no refund: a cast that hit nothing still costs everything =================
            Check(typeof(ICombatAuthority).GetMethod("CancelSkillCast") == null && typeof(LocalCombatAuthority).GetMethod("CancelSkillCast") == null,
                "no CancelSkillCast on the authority surface: nothing a caller can invoke gives mana, ammo or cooldown back");
            Caster missed = NewCaster(_bow, 100, 6, _multi);
            Check(Begin(missed, _multi.skillId, out int missAct, out reason), "a volley opens");
            Check(_auth.CompleteSkillCast(missed.CombatId, missAct), "and completes with no accepted hit (every arrow missed)");
            Check(missed.Magazine == 3 && missed.Spent == 3 && Mathf.Abs(missed.ManaPool.CurrentMana - (100 - _multi.EffectiveManaCost)) < 0.001f,
                "the missed volley still spent its 3 rounds and its mana");
            b = Begin(missed, _multi.skillId, out act, out reason);
            Check(!b && Has(reason, "cooldown") && missed.Magazine == 3, "and its cooldown still runs: no free recast (" + reason + ")");
            Caster dashNoRefund = NewCaster(_sword, 100, 0, _dash);
            Check(Begin(dashNoRefund, _dash.skillId, out int dashNoRefundAct, out reason), "dash opens");
            Check(_auth.CompleteSkillCast(dashNoRefund.CombatId, dashNoRefundAct), "dash completes (it has no hits to observe)");
            b = Begin(dashNoRefund, _dash.skillId, out act, out reason);
            Check(!b && Has(reason, "cooldown") && Mathf.Abs(dashNoRefund.ManaPool.CurrentMana - (100 - _dash.EffectiveManaCost)) < 0.001f,
                "a dash costs its mana and cooldown, however it was used (" + reason + ")");
            Check(!_auth.CompleteSkillCast(dashNoRefund.CombatId + 999, dashNoRefundAct), "another entity cannot complete someone else's activation");

            // ================= 10. forged effect scale / status / geometry on skill hits =================
            Caster scaled = NewCaster(_bow, 1000, 100, _pierce);
            Target scaleTarget = NewTarget(scaled.Point + new Vector3(0f, 0f, 15f));
            Check(Begin(scaled, _pierce.skillId, out int scaleAct1, out reason), "scale cast 1 opens");
            Outcome honest = HonestPierceHit(scaled, scaleAct1, scaleTarget);
            AdvanceClock(_pierce.cooldownSeconds + 1f);
            Check(Begin(scaled, _pierce.skillId, out int scaleAct2, out reason), "scale cast 2 opens");
            Outcome forgedHigh = Hit(scaled, scaleAct2, _pierce, _bow, scaleTarget, scaleTarget.Point, _bow.rangedRange, scale: 1.4f);
            AdvanceClock(_pierce.cooldownSeconds + 1f);
            Check(Begin(scaled, _pierce.skillId, out int scaleAct3, out reason), "scale cast 3 opens");
            Outcome forgedLow = Hit(scaled, scaleAct3, _pierce, _bow, scaleTarget, scaleTarget.Point, _bow.rangedRange, scale: 0.0001f);
            Check(honest.Accepted && forgedHigh.Accepted && forgedLow.Accepted, "the three scale casts landed");
            Check(Mathf.Abs(forgedHigh.Damage - honest.Damage) < 0.001f && Mathf.Abs(forgedLow.Damage - honest.Damage) < 0.001f,
                $"a forged EffectScale (1.4 / ~0) changes nothing: {honest.Damage:F3} / {forgedHigh.Damage:F3} / {forgedLow.Damage:F3}");
            scaled.Scale = 1.3f;
            AdvanceClock(_pierce.cooldownSeconds + 1f);
            Check(Begin(scaled, _pierce.skillId, out int scaleAct4, out reason), "scale cast 4 (caster CHA scale 1.3) opens");
            Outcome real = Hit(scaled, scaleAct4, _pierce, _bow, scaleTarget, scaleTarget.Point, _bow.rangedRange, scale: 0.6f);
            Check(real.Accepted && Mathf.Abs(real.Damage / honest.Damage - 1.3f) < 0.001f && Mathf.Abs(real.Damage - ExpectedWeaponSkill(scaled, _pierce, real.Info, false)) < 0.01f,
                $"the scale that applies is the caster's own 1.3 (ratio {real.Damage / honest.Damage:F4}), whatever the intent claims");

            // Statuses: forged TargetStatus ignored (asset spec scaled by the CALLER's scale); self status only from the asset.
            Caster cha = NewCaster(_sword, 1000, 0, _cry);
            cha.Scale = 1.2f;
            Check(Begin(cha, _cry.skillId, out int chaAct, out reason), "scaled Battle Cry opens");
            Target statusTarget = NewTarget(cha.Point + Vector3.forward * 2f);
            StatusSpec forgedStatus = new StatusSpec(StatusKind.Empowered, 9999f, 50f);
            Outcome statusHit = Hit(cha, chaAct, _cry, _sword, statusTarget, statusTarget.Point, _cry.radius, status: forgedStatus, scale: 1.4f);
            StatusSpec scaledTarget = LocalCombatAuthority.ScaleStatus(_cry.TargetStatusSpec, 1.2f);
            Check(statusHit.Accepted && statusTarget.Statuses.Count == 1 && statusTarget.Statuses[0].Kind == _cry.targetStatus
                  && Mathf.Abs(statusTarget.Statuses[0].Magnitude - scaledTarget.Magnitude) < 0.0001f && Mathf.Abs(statusTarget.Statuses[0].Duration - scaledTarget.Duration) < 0.0001f,
                "a forged TargetStatus (Empowered 9999s x50) is ignored: the victim gets the asset's slow at the caster's scale");
            Check(Mathf.Abs(statusHit.Damage - _cry.flatDamage) < 0.001f, "and the flat damage is not rescaled by a forged or real CHA");
            Outcome selfHit = Hit(cha, chaAct, _cry, _sword, new Target { CombatId = cha.CombatId, Point = cha.Point }, cha.Point, _cry.radius, status: forgedStatus);
            Check(!selfHit.Accepted && cha.Applied.Count == 0, "a hit aimed at the caster itself (to smuggle a self status) is rejected and adds no status (" + selfHit.Reason + ")");
            StatusSpec scaledSelf = LocalCombatAuthority.ScaleStatus(_cry.SelfStatusSpec, 1.2f);
            Check(_auth.CompleteSkillCast(cha.CombatId, chaAct), "scaled cast completes");
            Check(cha.Applied.Count == 1 && Mathf.Abs(cha.Applied[0].Magnitude - scaledSelf.Magnitude) < 0.0001f && Mathf.Abs(cha.Applied[0].Duration - scaledSelf.Duration) < 0.0001f,
                "the self buff uses the caster's own scale 1.2 (" + (cha.Applied.Count > 0 ? cha.Applied[0].ToString() : "none") + ")");
            Caster dasher = NewCaster(_sword, 100, 0, _dash);
            Check(Begin(dasher, _dash.skillId, out int dashAct2, out reason), "dash opens");
            Target dashVictim = NewTarget(dasher.Point + Vector3.forward * 2f);
            Hit(dasher, dashAct2, _dash, _sword, dashVictim, dashVictim.Point, 3f, status: forgedStatus);
            Check(_auth.CompleteSkillCast(dasher.CombatId, dashAct2), "dash completes");
            Check(dasher.Applied.Count == 0 && dashVictim.Statuses.Count == 0, "forged statuses on a Dash activation applied nothing anywhere");

            // Geometry on skill hits (R1a rules, reused through the shared helper).
            Caster geo = NewCaster(_bow, 100000, 1000, _pierce);
            Target gt = NewTarget(geo.Point + new Vector3(0f, 0f, 20f));
            GameObject headGo = new GameObject("SkillGate_Head");
            _cleanup.Add(headGo);
            SphereCollider head = headGo.AddComponent<SphereCollider>();
            head.radius = 0.15f;
            head.enabled = false;
            gt.Head = head;

            int OpenGeo()
            {
                AdvanceClock(_pierce.cooldownSeconds + 1f);
                gt.Point = geo.Point + new Vector3(0f, 0f, 20f);
                head.transform.position = gt.Point + Vector3.up * 0.6f;
                Physics.SyncTransforms();
                Check(Begin(geo, _pierce.skillId, out int a, out string r), "geometry cast opens (" + r + ")");
                return a;
            }

            int g1 = OpenGeo();
            Outcome atMuzzle = Hit(geo, g1, _pierce, _bow, gt, geo.Point, _bow.rangedRange);
            Check(!atMuzzle.Accepted && Has(atMuzzle.Reason, "not on the target") && atMuzzle.Damage == 0f, "a hit point at the shooter is not on the target (" + atMuzzle.Reason + ")");
            Outcome nan = Hit(geo, g1, _pierce, _bow, gt, new Vector3(float.NaN, 0f, 0f), _bow.rangedRange);
            Check(!nan.Accepted && Has(nan.Reason, "not on the target"), "a NaN hit point is rejected (" + nan.Reason + ")");
            Outcome range1000 = Hit(geo, g1, _pierce, _bow, gt, gt.Point, 1000f);
            Check(!range1000.Accepted && Has(range1000.Reason, "claimed range"), "a claimed MaxRange of 1000 is rejected (" + range1000.Reason + ")");
            Outcome weaponForged = Hit(geo, g1, _pierce, _rapier, gt, gt.Point, _bow.rangedRange);
            Check(!weaponForged.Accepted && Has(weaponForged.Reason, "not the equipped weapon"), "a forged weapon on a skill hit is rejected (" + weaponForged.Reason + ")");
            Outcome weaponNull = Hit(geo, g1, _pierce, null, gt, gt.Point, _bow.rangedRange);
            Check(!weaponNull.Accepted && Has(weaponNull.Reason, "not the equipped weapon"), "a null weapon on a weapon skill hit is rejected (" + weaponNull.Reason + ")");
            gt.Point = geo.Point + new Vector3(0f, 0f, 60f);
            Outcome beyond = Hit(geo, g1, _pierce, _bow, gt, gt.Point, _bow.rangedRange);
            Check(!beyond.Accepted && Has(beyond.Reason, "skill reaches") && beyond.Damage == 0f, "a target beyond the skill's reach is rejected on the authority's own distance (" + beyond.Reason + ")");
            gt.Point = geo.Point + new Vector3(0f, 0f, 20f);
            head.transform.position = gt.Point + Vector3.up * 0.6f;
            Physics.SyncTransforms();
            Outcome bodyHeadFlag = Hit(geo, g1, _pierce, _bow, gt, gt.Point, _bow.rangedRange, headFlag: true);
            Check(bodyHeadFlag.Accepted && !bodyHeadFlag.Info.Headshot && Mathf.Abs(bodyHeadFlag.Damage - ExpectedWeaponSkill(geo, _pierce, bodyHeadFlag.Info, false)) < 0.01f,
                "a forged headshot flag on a body hit is body damage (" + bodyHeadFlag.Reason + ")");

            int g2 = OpenGeo();
            head.enabled = true;
            Physics.SyncTransforms();
            Outcome realHead = Hit(geo, g2, _pierce, _bow, gt, head.transform.position, _bow.rangedRange, headFlag: false);
            Check(realHead.Accepted && realHead.Info.Headshot && Mathf.Abs(realHead.Damage - ExpectedWeaponSkill(geo, _pierce, realHead.Info, true)) < 0.01f,
                "a real head-collider hit with the flag cleared is a headshot x1.8 (" + realHead.Reason + ")");
            head.enabled = false;

            int g3 = OpenGeo();
            Vector3 shortened = gt.Point - Vector3.forward * 1.2f;
            Outcome shortClaim = Hit(geo, g3, _pierce, _bow, gt, shortened, _bow.rangedRange);
            Check(shortClaim.Accepted && Vector3.Distance(shortClaim.Info.Point, gt.Point) <= LocalCombatAuthority.TargetBodyRadius + 0.001f,
                "a hit point claimed in front of the target is pulled inside its body sphere");

            // Area skills never headshot, even with an enabled head collider and a point on it.
            Caster areaCaster = NewCaster(_sword, 1000, 0, _cry);
            Check(Begin(areaCaster, _cry.skillId, out int areaAct, out reason), "area headshot cast opens");
            Target areaTarget = NewTarget(areaCaster.Point + Vector3.forward * 2f);
            GameObject head2Go = new GameObject("SkillGate_Head2");
            _cleanup.Add(head2Go);
            SphereCollider head2 = head2Go.AddComponent<SphereCollider>();
            head2.radius = 0.15f;
            head2.transform.position = areaTarget.Point + Vector3.up * 0.6f;
            areaTarget.Head = head2;
            Physics.SyncTransforms();
            Outcome areaHead = Hit(areaCaster, areaAct, _cry, _sword, areaTarget, head2.transform.position, _cry.radius, headFlag: true);
            Check(areaHead.Accepted && !areaHead.Info.Headshot && Mathf.Abs(areaHead.Damage - _cry.flatDamage) < 0.001f, "an area burst is never a headshot (" + areaHead.Reason + ")");
            areaTarget.Point = areaCaster.Point + Vector3.forward * 20f;
            Outcome farCry = Hit(areaCaster, areaAct, _cry, _sword, areaTarget, areaTarget.Point, _cry.radius);
            Check(!farCry.Accepted, "a Battle Cry hit far outside its radius is rejected (" + farCry.Reason + ")");

            // ================= 11. the real rig: SkillCaster drives the authority (Dash, Battle Cry), then ranged ammo =================
            RealRigChecks(rig, cfg, archerClass);
        }

        private void RealRigChecks(PlayerRig rig, GameConfig cfg, CharacterClassData archerClass)
        {
            SkillCaster caster = rig.Skills;
            Check(caster != null && caster.KitValid, "the real rig has a valid warrior kit");
            if (caster == null) return;

            // The caster talks to the gate's authority (the scene one stays untouched).
            typeof(SkillCaster).GetField("authority", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(caster, _auth);
            rig.Mana.RefillToMax();
            Check(rig.ActiveWeapon == rig.MainHandWeapon && rig.ActiveWeapon != null, "the warrior holds its sword");

            float mana0 = rig.Mana.CurrentMana;
            int open0 = _auth.OpenActivationCount;
            Check(caster.TryCastSlot(0), "Dash accepted through SkillCaster.TryCastSlot -> authority (" + _auth.LastSkillRejectReason + ")");
            Check(Mathf.Abs((mana0 - rig.Mana.CurrentMana) - _dash.EffectiveManaCost) < 0.6f, $"Dash spent its mana exactly once, by the authority ({mana0:F1} -> {rig.Mana.CurrentMana:F1})");
            Check(_auth.OpenActivationCount == open0 + 1, "one activation was opened");
            Check(!caster.TryCastSlot(0), "a second immediate Dash is refused");
            bool again = _auth.TryBeginSkillCast(rig.CombatId, _dash.skillId, out int _, out string againReason);
            Check(!again && Has(againReason, "cooldown"), "and the authority independently refuses it on cooldown (" + againReason + ")");

            // A real Battle Cry on a real body.
            rig.Mana.RefillToMax();
            GameObject bodyGo = new GameObject("SkillGate_Body");
            _cleanup.Add(bodyGo);
            bodyGo.layer = LayerMask.NameToLayer(GameConfig.EnemyLayerName);
            bodyGo.transform.position = rig.transform.position + rig.transform.forward * 2.2f;
            CapsuleCollider col = bodyGo.AddComponent<CapsuleCollider>();
            col.center = Vector3.up * 1.1f;
            col.height = 2f;
            col.radius = 0.4f;
            SkillGateBody body = bodyGo.AddComponent<SkillGateBody>();
            body.CombatId = CombatRegistry.AllocateId();
            Register(body);
            Physics.SyncTransforms();

            float rigMultiplier = rig.GetOutgoingDamageMultiplier(rig.ActiveWeapon);
            Check(!rig.Status.HasStatus(StatusKind.Empowered), "the rig is not empowered before the cry");
            float mana1 = rig.Mana.CurrentMana;
            Check(caster.TryCastSlot(1), "Battle Cry accepted through SkillCaster.TryCastSlot -> authority (" + _auth.LastSkillRejectReason + ")");
            Check(Mathf.Abs(body.Taken - _cry.flatDamage * rigMultiplier) < 0.01f, $"the real body took the flat {_cry.flatDamage} x the rig's own outgoing multiplier {rigMultiplier:F4} = {_cry.flatDamage * rigMultiplier:F2}, measured before the cry's own buff ({body.Taken:F2})");
            Check(body.Statuses.Count == 1 && body.Statuses[0].Kind == _cry.targetStatus, "and the asset's slow");
            Check(Mathf.Abs((mana1 - rig.Mana.CurrentMana) - _cry.EffectiveManaCost) < 0.6f, "Battle Cry spent its mana exactly once");
            Check(rig.Status.HasStatus(_cry.selfStatus), "the rig carries the Empowered buff the authority applied");

            // Ranged weapon attacks: ammo validated and consumed by the authority, never twice.
            rig.ApplyClass(archerClass, cfg);
            RangedCombat ranged = rig.GetComponent<RangedCombat>();
            Check(ranged != null && ranged.enabled && rig.ActiveWeapon != null && rig.ActiveWeapon.IsRanged, "the archer holds a ranged weapon with an active ranged component");
            WeaponData realBow = rig.ActiveWeapon;
            if (ranged == null) return;

            body.Taken = 0f;
            body.transform.position = rig.transform.position + rig.transform.forward * 12f;
            Physics.SyncTransforms();
            AttackIntent bowIntent(Vector3 point) => new AttackIntent(rig.CombatId, rig.Kind, realBow, 1f, rig.CombatCenter, point - rig.CombatCenter, realBow.rangedRange, CombatRegistry.NextSequence(rig.CombatId), 0f);

            int magazine = ranged.Magazine;
            Check(magazine > 1, $"the archer starts with a magazine ({magazine})");
            AdvanceClock(10f);
            bool shot = _auth.SubmitAttack(bowIntent(body.CombatCenter), new HitCandidate(body.CombatId, body.CombatCenter, false), out string shotReason);
            Check(shot && ranged.Magazine == magazine - 1, $"an accepted bow attack spent exactly one round, by the authority ({magazine} -> {ranged.Magazine}; {shotReason})");

            Check(ranged.TryConsumeAmmo(ranged.Magazine) && ranged.Magazine == 0, "magazine emptied");
            AdvanceClock(10f);
            float before = body.Taken;
            shot = _auth.SubmitAttack(bowIntent(body.CombatCenter), new HitCandidate(body.CombatId, body.CombatCenter, false), out shotReason);
            Check(!shot && Has(shotReason, "ammunition") && body.Taken == before && ranged.Magazine == 0, "a ranged attack with an empty magazine is rejected, deals no damage and spends nothing (" + shotReason + ")");

            ranged.RefundAmmo(4);
            int beforeFire = ranged.Magazine;
            // A real body on the aim ray, so the shot is one the authority accepts.
            body.Taken = 0f;
            body.transform.position = rig.GetMuzzlePosition() + rig.AimDirection * 10f - Vector3.up * 1.1f;
            Physics.SyncTransforms();
            int acceptedBeforeFire = _auth.AcceptedCount;
            AdvanceClock(10f);
            ranged.Fire(1f);
            Check(_auth.AcceptedCount == acceptedBeforeFire + 1 && body.Taken > 0f,
                $"RangedCombat.Fire hit the body and the authority accepted the shot ({acceptedBeforeFire} -> {_auth.AcceptedCount}, damage {body.Taken:F1})");
            Check(ranged.Magazine == beforeFire - 1, $"an accepted Fire costs exactly one round, spent by the authority, not twice ({beforeFire} -> {ranged.Magazine})");
        }
    }
}
