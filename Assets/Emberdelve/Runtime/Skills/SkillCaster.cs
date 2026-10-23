using System.Collections.Generic;
using UnityEngine;
using Emberdelve.Combat;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.InputLayer;
using Emberdelve.Player;
using Emberdelve.Stats;

namespace Emberdelve.Skills
{
    /// <summary>
    /// Owns one player's skill kit: which two actives are on which keys, their cooldown clocks, the
    /// mana and requirement checks that refuse a cast, and the two passives resolved into a
    /// <see cref="CharacterStatStack"/>.
    ///
    /// Design rules that matter for the future server:
    /// - TryCastSlot is the single entry point. Input, tools and a networked input source all call it, so
    ///   the same rules apply no matter where the request came from.
    /// - A refused cast spends nothing: no mana, no cooldown, no sequence.
    /// - A cast that happens claims its command sequences from CombatRegistry inside the behavior, and the
    ///   damage itself is only ever applied by the combat authority.
    /// - Cooldowns here are the local prediction. The authority independently validates the same cooldown
    ///   per skill, so a client cannot shorten it.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SkillCaster : MonoBehaviour
    {
        public const int ActiveSlotCapacity = 2;
        public const int PassiveSlotCapacity = 2;

        [Header("Wiring")]
        [SerializeField] private PlayerRig rig;

        private ClassLoadout loadout;
        private ManaComponent mana;
        private GameConfig config;
        private ICombatAuthority authority;

        private readonly SkillData[] _active = new SkillData[ActiveSlotCapacity];
        private readonly SkillData[] _passive = new SkillData[PassiveSlotCapacity];
        private readonly float[] _cooldownRemaining = new float[ActiveSlotCapacity];
        private readonly bool[] _castableCache = new bool[ActiveSlotCapacity];
        private readonly bool[] _readyCache = new bool[ActiveSlotCapacity];

        private readonly CharacterStatStack _stats = new CharacterStatStack();
        private readonly SkillContext _context = new SkillContext();
        private readonly List<string> _kitProblems = new List<string>();

        private CharacterClassData appliedClass;
        private bool kitValid;

        public CharacterStatStack Stats => _stats;
        public IReadOnlyList<SkillData> ActiveSkills => _active;
        public IReadOnlyList<SkillData> PassiveSkills => _passive;
        public IReadOnlyList<string> KitProblems => _kitProblems;
        public bool KitValid => kitValid;
        public CharacterClassData AppliedClass => appliedClass;

        /// <summary>
        /// Changes whenever the kit is re-applied, cleared by death or restored. The combat authority stamps its
        /// own cooldown records with it, so a respawn or a new run starts with fresh cooldowns on both sides
        /// without the caster ever telling the authority to forget anything.
        /// </summary>
        public int KitEpoch { get; private set; }

        /// <summary>R7: the player this component belongs to; every event it raises carries it (0 = no player, e.g. a bare bag in a tool).</summary>
        private int OwnerId
        {
            get
            {
                if (rig == null) rig = GetComponent<PlayerRig>();
                return rig != null ? rig.PlayerId : GameEvents.NoPlayer;
            }
        }

        private void Awake()
        {
            if (rig == null) rig = GetComponent<PlayerRig>();
            if (loadout == null) loadout = GetComponent<ClassLoadout>();
            if (mana == null) mana = ManaComponent.Ensure(gameObject);
        }

        /// <summary>Applies the class kit. Called by ClassLoadout, which owns class application.</summary>
        public void ApplyClass(CharacterClassData data, GameConfig gameConfig)
        {
            appliedClass = data;
            config = gameConfig;
            KitEpoch++;

            if (authority == null && GameServices.TryGet<ICombatAuthority>(out ICombatAuthority resolved)) authority = resolved;
            if (mana == null) mana = ManaComponent.Ensure(gameObject);

            _kitProblems.Clear();
            _stats.Build(data);

            for (int i = 0; i < ActiveSlotCapacity; i++)
            {
                _active[i] = null;
                _cooldownRemaining[i] = 0f;
                _castableCache[i] = false;
                _readyCache[i] = true;
            }

            for (int i = 0; i < PassiveSlotCapacity; i++) _passive[i] = null;

            if (data != null) ValidateKit(data);

            kitValid = _kitProblems.Count == 0;

            for (int i = 0; i < ActiveSlotCapacity; i++)
            {
                _castableCache[i] = CanCastSlot(i, out _);
                _readyCache[i] = IsReady(i);
                GameEvents.RaiseSkillState(OwnerId, Snapshot(i));
            }
        }

        /// <summary>
        /// The kit contract, enforced rather than documented: exactly two active skills and exactly two
        /// passives, all present, all in the right slot, all with a behavior or a modifier behind them.
        /// A new class only has to satisfy it, and nothing in the runtime needs to change.
        /// </summary>
        private void ValidateKit(CharacterClassData data)
        {
            CharacterClassData.ValidateKit(data, _active, _passive, ActiveSlotCapacity, PassiveSlotCapacity, _kitProblems);

            for (int i = 0; i < ActiveSlotCapacity; i++)
            {
                SkillData skill = _active[i];
                if (skill == null) continue;

                if (SkillBehaviorCatalog.Resolve(skill.effect) == null)
                {
                    _kitProblems.Add($"active skill '{skill.displayName}' [{skill.skillId}] declares effect '{skill.effect}' with no behavior registered in SkillBehaviorCatalog");
                }
            }

            for (int i = 0; i < PassiveSlotCapacity; i++)
            {
                SkillData passive = _passive[i];
                if (passive == null) continue;

                if (passive.modifiers == null || passive.modifiers.Length == 0)
                {
                    _kitProblems.Add($"passive skill '{passive.displayName}' [{passive.skillId}] declares no stat modifier, so it is not a meaningful passive");
                }
            }

            for (int i = 0; i < _kitProblems.Count; i++) Debug.LogError("[Emberdelve][SkillKit] " + _kitProblems[i], this);
        }

        private void Update()
        {
            if (rig == null) return;

            float dt = Time.deltaTime;

            for (int i = 0; i < ActiveSlotCapacity; i++)
            {
                if (_cooldownRemaining[i] > 0f)
                {
                    _cooldownRemaining[i] = Mathf.Max(0f, _cooldownRemaining[i] - dt);
                }

                bool ready = IsReady(i);
                bool castable = CanCastSlot(i, out _);

                if (ready != _readyCache[i] || castable != _castableCache[i])
                {
                    _readyCache[i] = ready;
                    _castableCache[i] = castable;
                    GameEvents.RaiseSkillState(OwnerId, Snapshot(i));
                }
            }

            if (rig.IsDead) return;

            EmberdelveInput input = rig.Input;
            if (input == null) return;

            if (input.Skill1Pressed) TryCastSlot(0);
            if (input.Skill2Pressed) TryCastSlot(1);
        }

        public bool IsReady(int slot) => slot >= 0 && slot < ActiveSlotCapacity && _cooldownRemaining[slot] <= 0f;
        public float CooldownRemaining(int slot) => slot >= 0 && slot < ActiveSlotCapacity ? Mathf.Max(0f, _cooldownRemaining[slot]) : 0f;
        public SkillData ActiveSkill(int slot) => slot >= 0 && slot < ActiveSlotCapacity ? _active[slot] : null;

        /// <summary>
        /// Every rule that decides whether a cast is allowed right now, with the reason it was refused.
        /// Order is deliberate: the cheapest and most absolute checks first, so the reason a player sees
        /// is the reason that actually blocked them.
        /// </summary>
        public bool CanCastSlot(int slot, out string reason)
        {
            reason = null;

            if (slot < 0 || slot >= ActiveSlotCapacity)
            {
                reason = "no such skill slot";
                return false;
            }

            SkillData skill = _active[slot];
            if (skill == null)
            {
                reason = "no skill equipped there";
                return false;
            }

            if (rig == null)
            {
                reason = "no caster";
                return false;
            }

            if (rig.IsDead)
            {
                reason = "dead";
                return false;
            }

            if (_cooldownRemaining[slot] > 0f)
            {
                reason = $"on cooldown ({_cooldownRemaining[slot]:F1}s)";
                return false;
            }

            if (mana == null)
            {
                reason = "no mana pool";
                return false;
            }

            if (!mana.HasAmount(skill.EffectiveManaCost))
            {
                reason = $"not enough mana (needs {skill.EffectiveManaCost})";
                return false;
            }

            ISkillBehavior behavior = SkillBehaviorCatalog.Resolve(skill.effect);
            if (behavior == null)
            {
                reason = $"no behavior for effect '{skill.effect}'";
                return false;
            }

            if (authority == null && GameServices.TryGet<ICombatAuthority>(out ICombatAuthority resolved)) authority = resolved;

            if (authority == null)
            {
                reason = "no combat authority in the scene";
                return false;
            }

            BuildContext(skill);

            if (skill.requiresRangedWeapon && !_context.HasRangedInHand)
            {
                reason = "needs a ranged weapon in hand";
                return false;
            }

            if (skill.requiresMeleeWeapon && !_context.HasMeleeInHand)
            {
                reason = "needs a melee weapon in hand";
                return false;
            }

            return behavior.CanCast(_context, out reason);
        }

        /// <summary>
        /// The one entry point a cast goes through. Returns true only when the cast happened, and only
        /// then does it spend mana and start the cooldown.
        /// </summary>
        public bool TryCastSlot(int slot)
        {
            if (!CanCastSlot(slot, out string reason))
            {
                Reject(slot, reason);
                return false;
            }

            SkillData skill = _active[slot];
            ISkillBehavior behavior = SkillBehaviorCatalog.Resolve(skill.effect);
            BuildContext(skill);

            // The authority decides: it re-checks kit, weapon, cooldown (its own clock), mana and ammo and
            // spends mana and ammo itself. Nothing is spent here, and the activation id the hits must carry
            // is the one the authority hands back.
            if (!authority.TryBeginSkillCast(rig.CombatId, skill.skillId, out int activationId, out string authorityReason))
            {
                Reject(slot, authorityReason);
                return false;
            }

            _context.ActivationId = activationId;

            bool performed = behavior.Cast(_context);

            // The authority closes the activation either way and never refunds: mana, ammo and cooldown were
            // spent when it opened the cast. Feasibility (e.g. a dash needs a controller) is checked in the
            // behavior's CanCast BEFORE the cast opens, so a behavior failing here is a bug, not a refund.
            authority.CompleteSkillCast(rig.CombatId, activationId);

            if (!performed)
            {
                Debug.LogError($"[SkillCaster] '{skill.skillId}' passed CanCast but its effect did not run; the cast stays spent.", this);
                _cooldownRemaining[slot] = skill.EffectiveCooldown;
                _castableCache[slot] = false;
                _readyCache[slot] = false;
                Reject(slot, "the cast could not be performed");
                GameEvents.RaiseSkillState(OwnerId, Snapshot(slot));
                return false;
            }

            _cooldownRemaining[slot] = skill.EffectiveCooldown;
            _castableCache[slot] = false;
            _readyCache[slot] = false;

            GameEvents.RaiseSkillState(OwnerId, Snapshot(slot));
            return true;
        }

        public bool TryCast(string skillId)
        {
            for (int i = 0; i < ActiveSlotCapacity; i++)
            {
                if (_active[i] != null && _active[i].skillId == skillId) return TryCastSlot(i);
            }

            Reject(-1, $"no equipped skill called '{skillId}'");
            return false;
        }

        public SkillSnapshot Snapshot(int slot)
        {
            SkillData skill = ActiveSkill(slot);
            if (skill == null) return SkillSnapshot.Empty(slot);

            bool ready = IsReady(slot);
            bool castable = CanCastSlot(slot, out _);

            return new SkillSnapshot(slot, true, skill.skillId, skill.displayName, skill.description,
                skill.ShortSummary, skill.EffectiveManaCost, skill.EffectiveCooldown, CooldownRemaining(slot),
                ready, castable, KeyLabel(slot), skill.iconColor);
        }

        public string KeyLabel(int slot)
        {
            EmberdelveInput input = rig != null ? rig.Input : GetComponent<EmberdelveInput>();
            return input != null ? input.SkillKeyLabel(slot) : (slot == 0 ? "F" : "G");
        }

        /// <summary>Death clears the kit: cooldowns, temporary statuses and the dash, and mana stops regenerating.</summary>
        public void HandleDeath()
        {
            KitEpoch++;
            for (int i = 0; i < ActiveSlotCapacity; i++)
            {
                _cooldownRemaining[i] = 0f;
                _castableCache[i] = false;
                _readyCache[i] = true;
                GameEvents.RaiseSkillState(OwnerId, Snapshot(i));
            }

            if (rig != null)
            {
                if (rig.Movement != null) rig.Movement.CancelDash();
                if (rig.Status != null) rig.Status.ClearStatuses();
            }

            if (mana != null) mana.SetRegenerationActive(false);
        }

        /// <summary>Re-enables the kit after a respawn or a fresh run.</summary>
        public void HandleKitRestored()
        {
            KitEpoch++;
            if (mana != null) mana.SetRegenerationActive(true);
            for (int i = 0; i < ActiveSlotCapacity; i++)
            {
                _cooldownRemaining[i] = 0f;
                GameEvents.RaiseSkillState(OwnerId, Snapshot(i));
            }
        }

        private void BuildContext(SkillData skill)
        {
            if (loadout == null) loadout = GetComponent<ClassLoadout>();
            if (mana == null) mana = ManaComponent.Ensure(gameObject);

            _context.Rig = rig;
            _context.Loadout = loadout;
            _context.Melee = GetComponent<MeleeCombat>();
            _context.Ranged = GetComponent<RangedCombat>();
            _context.Mana = mana;
            _context.Config = config;
            _context.Authority = authority;
            _context.Skill = skill;
            _context.Stats = _stats;
            _context.SnapshotEffectScale(ResolveEffectScale());
            _context.ActivationId = 0;
            _context.ResetScratch();
        }

        /// <summary>
        /// The caster's own ability effect scale for the cast about to run (Docs/Design/ATTRIBUTES.md),
        /// from the one definition on the rig. Used here only for LOCAL movement effects (the dash length);
        /// statuses and damage use the scale the combat authority reads from the same rig itself, and no
        /// scale is ever sent to the authority.
        /// </summary>
        private float ResolveEffectScale() => rig != null ? rig.AbilityEffectScale : 1f;

        private void Reject(int slot, string reason)
        {
            SkillSnapshot snapshot = slot >= 0 ? Snapshot(slot) : SkillSnapshot.Empty(-1);
            GameEvents.RaiseSkillRejected(OwnerId, snapshot, reason);

            if (!string.IsNullOrEmpty(snapshot.DisplayName))
            {
                GameEvents.RaiseToast(OwnerId, $"{snapshot.DisplayName}: {reason}");
            }
            else
            {
                GameEvents.RaiseToast(OwnerId, reason);
            }
        }
    }
}
