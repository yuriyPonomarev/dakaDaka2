using System.Runtime.CompilerServices;
using System.Collections.Generic;
using UnityEngine;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Player;

[assembly: InternalsVisibleTo("Assembly-CSharp-Editor")]
[assembly: InternalsVisibleTo("Emberdelve.Editor")]

namespace Emberdelve.Combat
{
    /// <summary>
    /// The local stand-in for an authoritative server. It receives intent, validates it against the
    /// authority's own state, computes damage from weapon data, and only then applies it.
    ///
    /// Networking seam: when a real server is added, this component is moved to (or mirrored by) the
    /// server and clients keep calling SubmitAttack locally only in single player. Clients never send
    /// damage numbers, only AttackIntent + HitCandidate, and the authority re-resolves the target from
    /// CombatRegistry instead of trusting the supplied reference.
    /// </summary>
    [DefaultExecutionOrder(1000)]
    public sealed partial class LocalCombatAuthority : MonoBehaviour, ICombatAuthority
    {
        [Header("Wiring")]
        [SerializeField] private GameConfig config;

        [Header("Validation")]
        [SerializeField] private float rangeTolerance = 0.8f;
        [SerializeField] private float cooldownGrace = 0.8f;
        [SerializeField] private float maxOriginOffsetFromInstigator = 4f;
        [SerializeField] private bool logRejections = true;

        [Header("Weapon attack geometry")]
        [Tooltip("A claimed hit point further than this from the target's own centre is not on the target and is rejected.")]
        [SerializeField] private float hitPointTolerance = DefaultHitPointTolerance;

        /// <summary>Largest distance between a claimed hit point and the target's authoritative centre.</summary>
        public const float DefaultHitPointTolerance = 1.5f;

        /// <summary>
        /// Radius of the sphere around a target's centre the authority treats as its body when it measures
        /// distance. A claimed point is pulled inside it, so a client cannot shorten the distance (and skip
        /// range falloff) by claiming a point in front of the target.
        /// </summary>
        public const float TargetBodyRadius = 0.5f;

        /// <summary>A claimed head hit only counts when the claimed point lies on the target's head collider.</summary>
        public const float HeadSurfaceEpsilon = 0.05f;

        private System.Func<float> _clock;

        private readonly Dictionary<int, float> _lastAttackTime = new Dictionary<int, float>();
        private readonly Dictionary<int, int> _lastSequence = new Dictionary<int, int>();
        [Header("Skill activations")]
        [Tooltip("How long (authority clock) after a cast opens its hits are still accepted.")]
        [SerializeField] private float skillActivationWindow = 1.5f;

        /// <summary>
        /// One open skill cast. Created only by <see cref="TryBeginSkillCast"/>; everything a hit needs
        /// (skill, weapon, effect scale, hit budget) was resolved by the authority at that moment.
        /// </summary>
        private sealed class SkillActivation
        {
            public int Id;
            public int CasterId;
            public SkillData Skill;
            public WeaponData Weapon;
            public float Scale;
            public float OpenedAt;
            public int Budget;
            public int Hits;
            public readonly HashSet<int> Targets = new HashSet<int>();
            public bool SelfStatusApplied;
            public bool Completed;
        }

        private struct SkillStamp
        {
            public float Time;
            public int Epoch;
        }

        private readonly Dictionary<int, SkillActivation> _activations = new Dictionary<int, SkillActivation>();
        private readonly Dictionary<int, Dictionary<string, SkillStamp>> _skillStamps = new Dictionary<int, Dictionary<string, SkillStamp>>();
        private int _nextActivationId = 1;

        /// <summary>Small slack so a locally legal cast is not rejected for float rounding.</summary>
        private const float SkillClockTolerance = 0.05f;

        /// <summary>Reason the most recent skill cast or skill hit was rejected (empty after an accepted one).</summary>
        public string LastSkillRejectReason { get; private set; } = string.Empty;

        /// <summary>Activations currently open (observability for tools and tests).</summary>
        public int OpenActivationCount => _activations.Count;

        /// <summary>
        /// The authority's own time. Cooldowns are measured on this clock and never on a timestamp carried by
        /// an intent. Defaults to Time.time; <see cref="SetClock"/> injects a controllable one for tests (a
        /// server would use its own tick time).
        /// </summary>
        public float Now => _clock != null ? _clock() : Time.time;

        /// <summary>
        /// Injects the authority clock (null restores Time.time). TEST AND REPLAY SEAM ONLY: it is internal on
        /// purpose, so gameplay code (Runtime assembly) cannot call it to rewind the cooldown clock; the Editor
        /// assembly sees it through InternalsVisibleTo. Runtime source files in the root assembly could technically
        /// still call it; nothing does, and a grep gate belongs with the U3
        /// server split.
        /// </summary>
        internal void SetClock(System.Func<float> clock) => _clock = clock;

        /// <summary>Reason the most recent weapon attack was rejected (empty after an accepted one).</summary>
        public string LastAttackRejectReason { get; private set; } = string.Empty;

        public int AcceptedCount { get; private set; }
        public int RejectedCount { get; private set; }

        /// <summary>
        /// Read-only view of the anti-replay state for one combat id (-1 when that entity has never had a
        /// sequence accepted). The rule itself stays private: this only lets tools and tests observe which
        /// sequence the authority has actually consumed instead of guessing it from counters.
        /// </summary>
        public int LastSequenceFor(int combatId) => _lastSequence.TryGetValue(combatId, out int last) ? last : -1;

        /// <summary>
        /// When this authority last accepted an attack from a combat id. Observability only: it lets the HUD
        /// (and the smoke harness) show the same cooldown clock the authority enforces instead of guessing.
        /// Returns negative infinity when the entity has never had an accepted attack.
        /// </summary>
        public float LastAttackTimeFor(int combatId) =>
            _lastAttackTime.TryGetValue(combatId, out float last) ? last : float.NegativeInfinity;

        /// <summary>Seconds until this authority would accept another attack from that entity with that weapon.</summary>
        public float WeaponCooldownRemaining(int combatId, WeaponData weapon)
        {
            if (weapon == null) return 0f;
            float last = LastAttackTimeFor(combatId);
            if (float.IsNegativeInfinity(last)) return 0f;
            return Mathf.Max(0f, last + Mathf.Max(0.1f, weapon.cooldown) * cooldownGrace - Now);
        }

        private void Awake()
        {
            if (config != null) config.EnsureValid();
            GameServices.Register<ICombatAuthority>(this);
        }

        // PlayerLook updates the camera in LateUpdate. Sample the same live aim/guard pose the user sees,
        // after the default-order look and weapon visual components have applied this frame's input.
        private void LateUpdate() => AdvanceProjectiles(Time.deltaTime);

        public bool SubmitAttack(AttackIntent intent, HitCandidate hit) => SubmitAttack(intent, hit, out _);

        /// <summary>
        /// Weapon attack. The intent is a REQUEST. What is applied is decided here:
        /// - the weapon is the one the instigator entity actually holds (a different claim is rejected);
        /// - reach, falloff and cooldown come from that weapon, measured on the authority's own clock;
        /// - distance and line of sight are measured between the two entities' own positions, and a claimed hit
        ///   point is only accepted when it lies on the target and is then pulled inside the target's body;
        /// - headshot comes from the target's head collider at that point, never from the claimed flag;
        /// - the damage direction is derived from the entities' positions.
        /// Returns accept/reject with a reason so callers (and tests) can attribute a rejection to a rule.
        /// </summary>
        public bool SubmitAttack(AttackIntent intent, HitCandidate hit, out string reason)
        {
            reason = string.Empty;
            LastAttackRejectReason = string.Empty;

            if (!CombatRegistry.TryGet(intent.InstigatorCombatId, out ICombatEntity instigator)) return Reject(intent, "unknown instigator id", out reason);
            if (instigator.IsDead) return Reject(intent, "instigator is dead", out reason);

            // The weapon is resolved from the instigator, never from the intent.
            if (!(instigator is IActiveWeaponProvider holder)) return Reject(intent, "instigator has no authoritative weapon", out reason);
            WeaponData weapon = holder.ActiveWeapon;
            if (weapon == null) return Reject(intent, "instigator holds no weapon", out reason);
            if (intent.WeaponId != weapon.weaponId || (intent.Weapon != null && intent.Weapon != weapon))
            {
                return Reject(intent, $"claimed weapon '{intent.WeaponId}' is not the equipped weapon '{weapon.weaponId}'", out reason);
            }
            if (weapon.IsRanged) return Reject(intent, "ranged attacks must launch an authority-stepped projectile", out reason);

            // D1a: an entity with an authoritative swing pose (the player) hits with a melee weapon only through
            // the swept swing (TryBeginMeleeSwing). This arc-less path accepted a claimed hit point out to ~4 m
            // with no arc and no walls. Enemies have no swing pose and keep their committed-wedge claw attack.
            if (!weapon.IsRanged && instigator is IAimPoseProvider && !AllowLegacyMeleeAttacks)
            {
                return Reject(intent, "melee attacks from this instigator must begin a swept swing", out reason);
            }

            if (!CombatRegistry.TryGet(hit.TargetCombatId, out ICombatEntity target)) return Reject(intent, "unknown target id", out reason);
            if (target.IsDead)
            {
                reason = "target is dead";
                LastAttackRejectReason = reason;
                return false;
            }
            if (target == instigator) return Reject(intent, "self hit", out reason);
            if (!CombatTeams.AreHostile(instigator, target)) return Reject(intent, "target is not hostile to the instigator", out reason);

            if (_lastSequence.TryGetValue(intent.InstigatorCombatId, out int lastSequence) && intent.Sequence <= lastSequence)
            {
                return Reject(intent, "stale or repeated command sequence", out reason);
            }
            _lastSequence[intent.InstigatorCombatId] = intent.Sequence;

            // Cooldown on the authority's clock. intent.Timestamp is ignored on purpose.
            float now = Now;
            float cooldown = Mathf.Max(0.1f, weapon.cooldown);
            if (_lastAttackTime.TryGetValue(intent.InstigatorCombatId, out float lastAttack) && now - lastAttack < cooldown * cooldownGrace)
            {
                return Reject(intent, "attack faster than weapon cooldown", out reason);
            }

            // Authoritative reach of the weapon actually held. A claim of more than that is a forgery.
            float reach = weapon.IsRanged ? Mathf.Max(1f, weapon.rangedRange) : weapon.reach + weapon.hitSphereRadius;

            if (!TryResolveHit(instigator, target, reach, false, intent.MaxRange, intent.Origin, hit.HitPoint, true,
                    out Vector3 authoritativePoint, out bool headshot, out string geometryReason))
            {
                return Reject(intent, geometryReason, out reason);
            }

            // Ammunition: a ranged attack from an entity that owns a magazine needs a round in it. The
            // authority reads it through IAmmoSource and spends it itself on an accepted attack (below);
            // RangedCombat.Fire only spends the rounds of shots the authority did not accept.
            IAmmoSource ammo = weapon.IsRanged ? instigator as IAmmoSource : null;
            if (ammo != null && !ammo.HasAmmo(1)) return Reject(intent, "no ammunition in the magazine", out reason);

            Vector3 origin = instigator.CombatCenter;
            Vector3 direction = authoritativePoint - origin;
            if (direction.sqrMagnitude < 0.0001f) direction = intent.Direction;

            AttackIntent resolved = new AttackIntent(instigator.CombatId, instigator.Kind, weapon, intent.Charge,
                origin, direction, reach, intent.Sequence, now);
            HitCandidate resolvedHit = new HitCandidate(target.CombatId, authoritativePoint, headshot);

            // Both instigator factors are read from the authority's own validated entity, never from the
            // intent: the multiplier channel and the separate flat weapon-base channel.
            DamageInfo damage = DamageResolver.Resolve(weapon, resolved, resolvedHit, target,
                OutgoingMultiplier(instigator, weapon), FlatWeaponBonus(instigator, weapon), FlowTuning);
            if (!damage.IsValid) return Reject(intent, "resolver produced no damage", out reason);

            if (ammo != null && !ammo.TryConsumeAmmo(1)) return Reject(intent, "no ammunition in the magazine", out reason);

            // Only an accepted attack spends the cooldown.
            _lastAttackTime[intent.InstigatorCombatId] = now;

            target.ApplyDamage(damage);

            AcceptedCount++;
            ApplyHitEffects(target, damage, weapon);

            HitFeedback feedback = new HitFeedback(authoritativePoint, damage.Amount, damage.Blocked, damage.Headshot,
                target.IsDead, target.Kind);

            PublishCombatFeedback(instigator, target, feedback, damage);

            return true;
        }

        /// <summary>
        /// The geometry rules shared by weapon attacks and skill hits (R1a, reused unchanged for skills):
        /// a claimed range above the authoritative reach is a forgery; the claimed origin must be attached to
        /// the instigator; the claimed hit point must lie on the target and is then pulled inside the target's
        /// body sphere, so it can never shorten the measured distance by more than the body radius; distance is
        /// measured from the instigator's own centre; line of sight is checked between the entity centres; and
        /// a headshot is the target's own head collider at the claimed point, never the claimed flag, and only
        /// where the attack can headshot at all.
        /// </summary>
        private bool TryResolveHit(ICombatEntity instigator, ICombatEntity target, float reach, bool isSkill,
            float claimedMaxRange, Vector3 claimedOrigin, Vector3 claimedPoint, bool allowHeadshot,
            out Vector3 authoritativePoint, out bool headshot, out string reason)
        {
            authoritativePoint = default;
            headshot = false;
            reason = string.Empty;
            string noun = isSkill ? "skill" : "weapon";

            if (!(claimedMaxRange <= reach + rangeTolerance))
            {
                reason = $"claimed range {claimedMaxRange:F1}m exceeds the {noun}'s {reach:F1}m";
                return false;
            }

            Vector3 origin = instigator.CombatCenter;
            if (!(Vector3.Distance(origin, claimedOrigin) <= maxOriginOffsetFromInstigator))
            {
                reason = isSkill ? "skill origin is not attached to the instigator" : "attack origin is not attached to the instigator";
                return false;
            }

            Vector3 targetCenter = target.CombatCenter;
            if (!(Vector3.Distance(claimedPoint, targetCenter) <= hitPointTolerance))
            {
                reason = "claimed hit point is not on the target";
                return false;
            }

            // The point the authority measures with: the claim, pulled inside the target's body sphere.
            authoritativePoint = targetCenter + Vector3.ClampMagnitude(claimedPoint - targetCenter, TargetBodyRadius);
            float distance = Vector3.Distance(origin, authoritativePoint);
            if (distance > reach + rangeTolerance)
            {
                reason = $"target at {distance:F1}m, {noun} reaches {reach:F1}m";
                return false;
            }

            LayerMask levelMask = config != null ? config.levelMask : Physics.DefaultRaycastLayers;
            if (!HitScanner.HasLineOfSight(origin, targetCenter, levelMask, 0.35f))
            {
                reason = "line of sight is blocked";
                return false;
            }

            Collider head = target.HeadCollider;
            headshot = allowHeadshot && head != null && head.enabled
                       && (head.ClosestPoint(claimedPoint) - claimedPoint).sqrMagnitude <= HeadSurfaceEpsilon * HeadSurfaceEpsilon;
            return true;
        }

        // ------------------------------------------------------------------------------------------------
        // Skills
        // ------------------------------------------------------------------------------------------------

        public bool TryBeginSkillCast(int casterCombatId, string skillId, out int activationId, out string reason)
        {
            activationId = 0;
            reason = string.Empty;
            LastSkillRejectReason = string.Empty;
            PurgeExpiredActivations();

            if (!CombatRegistry.TryGet(casterCombatId, out ICombatEntity caster)) return RejectCast(casterCombatId, skillId, "unknown instigator id", out reason);
            if (caster.IsDead) return RejectCast(casterCombatId, skillId, "instigator is dead", out reason);
            if (!(caster is ISkillCasterState state)) return RejectCast(casterCombatId, skillId, "instigator has no skill kit", out reason);
            if (!(caster is IActiveWeaponProvider holder)) return RejectCast(casterCombatId, skillId, "instigator has no authoritative weapon", out reason);
            if (string.IsNullOrEmpty(skillId)) return RejectCast(casterCombatId, skillId, "skill id missing", out reason);

            // The skill is resolved from the caster's own kit. A skill that is not one of its active skills
            // (unknown, foreign, a passive, a different class's) cannot be cast, whatever the caller holds.
            SkillData skill = null;
            IReadOnlyList<SkillData> kit = state.ActiveSkills;
            if (kit != null)
            {
                for (int i = 0; i < kit.Count; i++)
                {
                    SkillData candidate = kit[i];
                    if (candidate != null && candidate.IsActive && candidate.skillId == skillId)
                    {
                        skill = candidate;
                        break;
                    }
                }
            }
            if (skill == null) return RejectCast(casterCombatId, skillId, $"skill '{skillId}' is not in the caster's kit", out reason);

            // Weapon requirement against the weapon the authority knows is in hand.
            WeaponData weapon = holder.ActiveWeapon;
            if ((skill.requiresRangedWeapon || skill.IsProjectileSkill) && (weapon == null || !weapon.IsRanged))
            {
                return RejectCast(casterCombatId, skillId, "needs a ranged weapon in hand", out reason);
            }
            if (skill.requiresMeleeWeapon && (weapon == null || weapon.IsRanged))
            {
                return RejectCast(casterCombatId, skillId, "needs a melee weapon in hand", out reason);
            }
            if (!skill.UsesFlatDamage && skill.HitBudget > 0 && weapon == null)
            {
                return RejectCast(casterCombatId, skillId, "needs a weapon in hand", out reason);
            }

            // Cooldown on the authority clock. A stamp from an older kit epoch (a death, a respawn, a new
            // class application) no longer counts, exactly like the caster's own cleared cooldowns.
            float now = Now;
            float cooldown = skill.EffectiveCooldown;
            Dictionary<string, SkillStamp> stamps = SkillStampsFor(casterCombatId);
            bool hadStamp = stamps.TryGetValue(skillId, out SkillStamp previous);
            if (hadStamp && previous.Epoch == state.KitEpoch && cooldown > 0f && now - previous.Time < cooldown - SkillClockTolerance)
            {
                return RejectCast(casterCombatId, skillId, $"{skillId} cast faster than its {cooldown:F1}s cooldown", out reason);
            }

            // Mana and ammo are validated against the caster's own components, then spent here.
            int manaCost = skill.EffectiveManaCost;
            ManaComponent mana = state.Mana;
            if (manaCost > 0)
            {
                if (mana == null) return RejectCast(casterCombatId, skillId, "no mana pool", out reason);
                if (!mana.HasAmount(manaCost)) return RejectCast(casterCombatId, skillId, $"not enough mana (needs {manaCost})", out reason);
            }

            int ammoNeeded = skill.AmmoPerCast;
            IAmmoSource ammo = caster as IAmmoSource;
            if (ammoNeeded > 0 && (ammo == null || !ammo.HasAmmo(ammoNeeded)))
            {
                return RejectCast(casterCombatId, skillId, $"not enough ammunition (needs {ammoNeeded})", out reason);
            }

            if (manaCost > 0 && !mana.TrySpend(manaCost)) return RejectCast(casterCombatId, skillId, $"not enough mana (needs {manaCost})", out reason);
            if (ammoNeeded > 0 && !ammo.TryConsumeAmmo(ammoNeeded))
            {
                if (manaCost > 0) mana.Restore(manaCost);
                return RejectCast(casterCombatId, skillId, $"not enough ammunition (needs {ammoNeeded})", out reason);
            }

            // The effect scale is the caster's own, read here once per cast and kept with the activation.
            float scale = DamageResolver.ClampEffectScale(state.AbilityEffectScale);

            SkillActivation activation = new SkillActivation
            {
                Id = _nextActivationId++,
                CasterId = casterCombatId,
                Skill = skill,
                Weapon = weapon,
                Scale = scale,
                OpenedAt = now,
                Budget = skill.HitBudget,
            };
            _activations[activation.Id] = activation;
            stamps[skillId] = new SkillStamp { Time = now, Epoch = state.KitEpoch };

            activationId = activation.Id;
            return true;
        }

        public bool CompleteSkillCast(int casterCombatId, int activationId)
        {
            if (!_activations.TryGetValue(activationId, out SkillActivation activation)) return false;
            if (activation.CasterId != casterCombatId || activation.Completed) return false;
            if (!CombatRegistry.TryGet(casterCombatId, out ICombatEntity caster) || caster.IsDead) return false;

            // Closed: no further hit can ride on this activation.
            activation.Completed = true;

            // The self buff comes from the skill asset, scaled by the caster's own effect scale (the one read
            // when the cast opened). It lands even when the burst hit nothing, and it lands AFTER the hits,
            // so a cry does not empower its own damage - the same order the behavior always had.
            SkillData skill = activation.Skill;
            if (skill.affectsSelf && caster is IStatusTarget self)
            {
                StatusSpec selfStatus = ScaleStatus(skill.SelfStatusSpec, activation.Scale);
                if (!selfStatus.IsNone)
                {
                    self.ApplyStatus(selfStatus, casterCombatId);
                    activation.SelfStatusApplied = true;
                }
            }

            return true;
        }

        public bool SubmitSkill(SkillIntent intent, HitCandidate hit) => SubmitSkill(intent, hit, out _);

        /// <summary>
        /// A skill hit. The intent is a REQUEST for one hit of an activation this authority opened
        /// (<see cref="TryBeginSkillCast"/>): the skill, weapon, effect scale and hit budget are the
        /// activation's, not the intent's. A claimed weapon or skill that differs is rejected, a claimed range
        /// beyond the skill's authoritative reach is rejected, the hit point/headshot/range rules are the same
        /// as for weapon attacks (<see cref="TryResolveHit"/>), and Timestamp, TargetStatus and EffectScale are
        /// ignored. The cooldown was stamped when the cast opened, on the authority clock.
        /// </summary>
        public bool SubmitSkill(SkillIntent intent, HitCandidate hit, out string reason)
        {
            reason = string.Empty;
            LastSkillRejectReason = string.Empty;

            if (!CombatRegistry.TryGet(intent.InstigatorCombatId, out ICombatEntity instigator)) return RejectSkill(intent, "unknown instigator id", out reason);
            if (instigator.IsDead) return RejectSkill(intent, "instigator is dead", out reason);
            if (!(instigator is IActiveWeaponProvider holder)) return RejectSkill(intent, "instigator has no authoritative weapon", out reason);
            if (!CombatRegistry.TryGet(hit.TargetCombatId, out ICombatEntity target)) return RejectSkill(intent, "unknown target id", out reason);
            if (target.IsDead)
            {
                reason = "target is dead";
                LastSkillRejectReason = reason;
                return false;
            }
            if (target == instigator) return RejectSkill(intent, "self hit", out reason);
            if (!CombatTeams.AreHostile(instigator, target)) return RejectSkill(intent, "target is not hostile to the instigator", out reason);

            // The activation is the authority's own record. An id it did not hand to this entity is a forgery.
            if (intent.ActivationId <= 0 || !_activations.TryGetValue(intent.ActivationId, out SkillActivation activation))
            {
                return RejectSkill(intent, $"no open activation {intent.ActivationId}", out reason);
            }
            if (activation.CasterId != instigator.CombatId) return RejectSkill(intent, "activation belongs to another entity", out reason);

            float now = Now;
            if (now - activation.OpenedAt > skillActivationWindow)
            {
                _activations.Remove(activation.Id);
                return RejectSkill(intent, "activation window expired", out reason);
            }
            if (intent.SkillId != activation.Skill.skillId || (intent.Skill != null && intent.Skill != activation.Skill))
            {
                return RejectSkill(intent, $"claimed skill '{intent.SkillId}' is not the skill activation {activation.Id} was opened for ('{activation.Skill.skillId}')", out reason);
            }
            if (activation.Completed) return RejectSkill(intent, $"activation {activation.Id} is closed (the cast finished)", out reason);
            if (activation.Hits >= activation.Budget)
            {
                return RejectSkill(intent, $"activation {activation.Id} is past its hit budget ({activation.Budget})", out reason);
            }
            if (activation.Targets.Contains(target.CombatId)) return RejectSkill(intent, "target already hit by this activation", out reason);

            // Skills share the entity-wide sequence with weapon attacks, so one entity cannot get two
            // different command streams that a server would have to reconcile.
            if (_lastSequence.TryGetValue(intent.InstigatorCombatId, out int lastSequence) && intent.Sequence <= lastSequence)
            {
                return RejectSkill(intent, "stale or repeated command sequence", out reason);
            }
            _lastSequence[intent.InstigatorCombatId] = intent.Sequence;

            WeaponData weapon = activation.Weapon;
            if (holder.ActiveWeapon != weapon) return RejectSkill(intent, "the equipped weapon changed since the cast opened", out reason);
            if (intent.Weapon != weapon)
            {
                string claimed = intent.Weapon != null ? intent.Weapon.weaponId : "none";
                string held = weapon != null ? weapon.weaponId : "none";
                return RejectSkill(intent, $"claimed weapon '{claimed}' is not the equipped weapon '{held}'", out reason);
            }

            SkillData skill = activation.Skill;
            if (skill.IsProjectileSkill)
                return RejectSkill(intent, "projectile skills must launch an authority-stepped projectile", out reason);
            float reach = skill.IsProjectileSkill ? Mathf.Max(1f, weapon.rangedRange) : skill.radius;

            if (!TryResolveHit(instigator, target, reach, true, intent.MaxRange, intent.Origin, hit.HitPoint,
                    skill.IsProjectileSkill, out Vector3 authoritativePoint, out bool headshot, out string geometryReason))
            {
                return RejectSkill(intent, geometryReason, out reason);
            }

            Vector3 origin = instigator.CombatCenter;
            Vector3 direction = authoritativePoint - origin;
            if (direction.sqrMagnitude < 0.0001f) direction = intent.Direction;

            // CHA scale: the caster's own value from the activation. A skill with its own flat number keeps it
            // (the cry's CHA term lives in its two statuses, ScaleStatus), a weapon-based skill is scaled once.
            float damageScale = skill.UsesFlatDamage ? 1f : activation.Scale;

            SkillIntent resolved = new SkillIntent(instigator.CombatId, instigator.Kind, skill, weapon, origin, direction,
                reach, intent.Sequence, now, default, activation.Id, damageScale);
            HitCandidate resolvedHit = new HitCandidate(target.CombatId, authoritativePoint, headshot);

            // The instigator's multipliers and the flat weapon-base bonus are read from the instigator.
            DamageInfo damage = DamageResolver.ResolveSkill(resolved, resolvedHit, target,
                OutgoingMultiplier(instigator, weapon), damageScale, FlatWeaponBonus(instigator, weapon));
            if (!damage.IsValid) return RejectSkill(intent, "resolver produced no damage", out reason);

            activation.Hits++;
            activation.Targets.Add(target.CombatId);

            target.ApplyDamage(damage);

            StatusSpec targetStatus = ScaleStatus(skill.TargetStatusSpec, activation.Scale);
            if (!targetStatus.IsNone && target is IStatusTarget statusTarget)
            {
                statusTarget.ApplyStatus(targetStatus, intent.InstigatorCombatId);
            }

            AcceptedCount++;

            HitFeedback feedback = new HitFeedback(authoritativePoint, damage.Amount, damage.Blocked, damage.Headshot,
                target.IsDead, target.Kind);

            PublishCombatFeedback(instigator, target, feedback, damage);

            return true;
        }

        /// <summary>
        /// CHA scales a status's duration and the DELTA its magnitude means from neutral 1, never the
        /// authored magnitude wholesale: a 1.25 buff becomes 1 + 0.25 * scale and a 0.55 slow becomes
        /// 1 - 0.45 * scale. The scale is the caster's own (already clamped to 0.6..1.4), StatusSpec clamps
        /// duration and magnitude to non-negative, and StatusComponent still clamps the multipliers those
        /// magnitudes become, so no bound is bypassed and the shared skill asset is only ever read.
        /// </summary>
        public static StatusSpec ScaleStatus(StatusSpec spec, float scale)
        {
            if (spec.IsNone) return StatusSpec.None;

            return new StatusSpec(spec.Kind, spec.Duration * scale, 1f + (spec.Magnitude - 1f) * scale);
        }

        private void PurgeExpiredActivations()
        {
            if (_activations.Count == 0) return;

            float now = Now;
            List<int> expired = null;
            foreach (KeyValuePair<int, SkillActivation> pair in _activations)
            {
                if (now - pair.Value.OpenedAt > skillActivationWindow + 5f)
                {
                    if (expired == null) expired = new List<int>();
                    expired.Add(pair.Key);
                }
            }

            if (expired != null) for (int i = 0; i < expired.Count; i++) _activations.Remove(expired[i]);
        }

        private Dictionary<string, SkillStamp> SkillStampsFor(int combatId)
        {
            if (!_skillStamps.TryGetValue(combatId, out Dictionary<string, SkillStamp> stamps))
            {
                stamps = new Dictionary<string, SkillStamp>();
                _skillStamps[combatId] = stamps;
            }
            return stamps;
        }


        /// <summary>
        /// Read from the instigator, never from the caller: passives and temporary buffs belong to the
        /// entity the authority already validated, exactly like the stats a server would own.
        /// </summary>
        private static float OutgoingMultiplier(ICombatEntity instigator, WeaponData weapon)
        {
            if (instigator is IDamageOutgoingProvider provider) return Mathf.Max(0f, provider.GetOutgoingDamageMultiplier(weapon));
            return 1f;
        }

        /// <summary>
        /// The instigator's FLAT weapon-base bonus, read from the instigator on its own typed provider channel
        /// at the same accepted-hit boundary as the multiplier. AttackIntent and SkillIntent carry no bonus
        /// and cannot be made to: this is authority-side state about an entity the authority already resolved
        /// from its own registry, which is the shape a server would fill from replicated gear. An instigator
        /// that is not a provider (an enemy, an entity with no rolled gear) contributes 0, and a claim that is
        /// NaN, ±Infinity or negative is sanitized to 0 here as well as inside WeaponData, so no provider can
        /// poison a resolved hit. Local plumbing only - it is not proof of real server authority (U3).
        /// </summary>
        private static float FlatWeaponBonus(ICombatEntity instigator, WeaponData weapon)
        {
            if (instigator is IDamageFlatWeaponBonusProvider provider)
            {
                return WeaponData.SanitizeBaseDamageBonus(provider.GetFlatWeaponDamageBonus(weapon));
            }
            return 0f;
        }

        private bool RejectCast(int casterId, string skillId, string reason, out string reasonOut)
        {
            reasonOut = reason;
            LastSkillRejectReason = reason;
            RejectedCount++;
            if (logRejections)
            {
                Debug.Log($"[CombatAuthority] Rejected skill cast '{skillId}' from #{casterId}: {reason}", this);
            }
            return false;
        }

        private bool RejectSkill(SkillIntent intent, string reason, out string reasonOut)
        {
            reasonOut = reason;
            LastSkillRejectReason = reason;
            RejectedCount++;
            if (logRejections)
            {
                Debug.Log($"[CombatAuthority] Rejected skill '{intent.SkillId}' from #{intent.InstigatorCombatId}: {reason}", this);
            }
            return false;
        }

        /// <summary>
        /// R7: hands the resolved result to the entities involved. A player entity raises its own events under its own
        /// player id (<see cref="IPlayerCombatFeedback"/>); the authority raises no HUD event itself and never asks who the
        /// "local" player is, so any number of players, and a server with none, resolve identically.
        /// </summary>
        private static void PublishCombatFeedback(ICombatEntity instigator, ICombatEntity target, in HitFeedback feedback, in DamageInfo damage)
        {
            if (instigator is IPlayerCombatFeedback dealer) dealer.OnHitDealt(feedback);
            if (target is IPlayerCombatFeedback taker) taker.OnDamageTaken(damage);
        }

        private bool Reject(AttackIntent intent, string reason, out string reasonOut)
        {
            reasonOut = reason;
            LastAttackRejectReason = reason;
            RejectedCount++;
            if (logRejections)
            {
                // Once per reason per second per instigator: a held button or an enemy retrying every frame
                // must not flood the log, but the first occurrence of each reason is always visible.
                CombatDiagnostics.Log($"reject:{intent.InstigatorCombatId}:{reason}",
                    $"[CombatAuthority] Rejected attack from #{intent.InstigatorCombatId} ({intent.WeaponId}): {reason}", this);
            }
            return false;
        }
    }
}
