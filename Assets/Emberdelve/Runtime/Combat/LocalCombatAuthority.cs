using System.Collections.Generic;
using UnityEngine;
using Emberdelve.Core;
using Emberdelve.Data;

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
    public sealed class LocalCombatAuthority : MonoBehaviour, ICombatAuthority
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
        private readonly Dictionary<int, Dictionary<string, float>> _lastSkillTime = new Dictionary<int, Dictionary<string, float>>();
        private readonly Dictionary<int, Dictionary<string, int>> _lastSkillActivation = new Dictionary<int, Dictionary<string, int>>();

        /// <summary>Small slack so a locally legal cast is not rejected for float rounding.</summary>
        private const float SkillClockTolerance = 0.05f;

        /// <summary>
        /// The authority's own time. Cooldowns are measured on this clock and never on a timestamp carried by
        /// an intent. Defaults to Time.time; <see cref="SetClock"/> injects a controllable one for tests (a
        /// server would use its own tick time).
        /// </summary>
        public float Now => _clock != null ? _clock() : Time.time;

        /// <summary>Injects the authority clock (null restores Time.time). Test and replay seam.</summary>
        public void SetClock(System.Func<float> clock) => _clock = clock;

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

        private void OnDestroy()
        {
            GameServices.Unregister<ICombatAuthority>(this);
        }

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
            if (!(intent.MaxRange <= reach + rangeTolerance))
            {
                return Reject(intent, $"claimed range {intent.MaxRange:F1}m exceeds the weapon's {reach:F1}m", out reason);
            }

            Vector3 origin = instigator.CombatCenter;
            if (!(Vector3.Distance(origin, intent.Origin) <= maxOriginOffsetFromInstigator))
            {
                return Reject(intent, "attack origin is not attached to the instigator", out reason);
            }

            Vector3 targetCenter = target.CombatCenter;
            if (!(Vector3.Distance(hit.HitPoint, targetCenter) <= hitPointTolerance))
            {
                return Reject(intent, "claimed hit point is not on the target", out reason);
            }

            // The point the authority measures with: the claim, pulled inside the target's body sphere.
            Vector3 authoritativePoint = targetCenter + Vector3.ClampMagnitude(hit.HitPoint - targetCenter, TargetBodyRadius);
            float distance = Vector3.Distance(origin, authoritativePoint);
            if (distance > reach + rangeTolerance)
            {
                return Reject(intent, $"target at {distance:F1}m, weapon reaches {reach:F1}m", out reason);
            }

            LayerMask levelMask = config != null ? config.levelMask : Physics.DefaultRaycastLayers;
            if (!HitScanner.HasLineOfSight(origin, targetCenter, levelMask, 0.35f))
            {
                return Reject(intent, "line of sight is blocked", out reason);
            }

            // Headshot is the authority's reading of the target's own head collider, not the claimed flag.
            Collider head = target.HeadCollider;
            bool headshot = head != null && head.enabled
                            && (head.ClosestPoint(hit.HitPoint) - hit.HitPoint).sqrMagnitude <= HeadSurfaceEpsilon * HeadSurfaceEpsilon;

            Vector3 direction = authoritativePoint - origin;
            if (direction.sqrMagnitude < 0.0001f) direction = intent.Direction;

            AttackIntent resolved = new AttackIntent(instigator.CombatId, instigator.Kind, weapon, intent.Charge,
                origin, direction, reach, intent.Sequence, now);
            HitCandidate resolvedHit = new HitCandidate(target.CombatId, authoritativePoint, headshot);

            // Both instigator factors are read from the authority's own validated entity, never from the
            // intent: the multiplier channel and the separate flat weapon-base channel.
            DamageInfo damage = DamageResolver.Resolve(weapon, resolved, resolvedHit, target,
                OutgoingMultiplier(instigator, weapon), FlatWeaponBonus(instigator, weapon));
            if (!damage.IsValid) return Reject(intent, "resolver produced no damage", out reason);

            // Only an accepted attack spends the cooldown.
            _lastAttackTime[intent.InstigatorCombatId] = now;

            target.ApplyDamage(damage);

            AcceptedCount++;

            HitFeedback feedback = new HitFeedback(authoritativePoint, damage.Amount, damage.Blocked, damage.Headshot,
                target.IsDead, target.Kind);

            if (instigator.Kind == EntityKind.LocalPlayer) GameEvents.RaiseDamageDealt(feedback);
            if (target.Kind == EntityKind.LocalPlayer) GameEvents.RaisePlayerDamaged(damage);

            return true;
        }

        public bool SubmitSkill(SkillIntent intent, HitCandidate hit)
        {
            if (intent.Skill == null) return RejectSkill(intent, "skill data missing");
            if (!CombatRegistry.TryGet(intent.InstigatorCombatId, out ICombatEntity instigator)) return RejectSkill(intent, "unknown instigator id");
            if (instigator.IsDead) return RejectSkill(intent, "instigator is dead");
            if (!CombatRegistry.TryGet(hit.TargetCombatId, out ICombatEntity target)) return RejectSkill(intent, "unknown target id");
            if (target.IsDead) return false;
            if (target == instigator) return RejectSkill(intent, "self hit");
            if (!CombatTeams.AreHostile(instigator, target)) return RejectSkill(intent, "target is not hostile to the instigator");

            // Skills share the entity-wide sequence with weapon attacks, so one entity cannot get two
            // different command streams that a server would have to reconcile.
            if (_lastSequence.TryGetValue(intent.InstigatorCombatId, out int lastSequence) && intent.Sequence <= lastSequence)
            {
                return RejectSkill(intent, "stale or repeated command sequence");
            }
            _lastSequence[intent.InstigatorCombatId] = intent.Sequence;

            // A skill is validated against its own cooldown, not against the weapon cooldown clock, so a
            // Warrior may battle cry one frame after a sword swing without that being spam.
            //
            // One activation can legitimately submit several intents - a three arrow volley, or one arrow
            // that pierces two bodies - so the cooldown rule is applied per activation id, not per intent.
            // A genuinely repeated cast carries a new activation id and is still refused.
            Dictionary<string, int> activations = SkillActivationFor(intent.InstigatorCombatId);
            bool sameActivation = intent.ActivationId > 0
                                  && activations.TryGetValue(intent.SkillId, out int lastActivation)
                                  && lastActivation == intent.ActivationId;

            if (!sameActivation)
            {
                float cooldown = intent.Skill.EffectiveCooldown;
                Dictionary<string, float> clock = SkillClockFor(intent.InstigatorCombatId);
                bool hasCastBefore = clock.TryGetValue(intent.SkillId, out float lastCast);

                if (hasCastBefore && cooldown > 0f && intent.Timestamp - lastCast < cooldown - SkillClockTolerance)
                {
                    return RejectSkill(intent, $"{intent.SkillId} cast faster than its {cooldown:F1}s cooldown");
                }
            }

            // The cooldown clock is only stamped once a hit of this activation is accepted, so a shot that
            // failed on range or line of sight cannot spend the skill's cooldown.
            bool startsCooldown = !sameActivation;

            if (Vector3.Distance(instigator.CombatCenter, intent.Origin) > maxOriginOffsetFromInstigator)
            {
                return RejectSkill(intent, "skill origin is not attached to the instigator");
            }

            float distance = Vector3.Distance(intent.Origin, hit.HitPoint);
            if (distance > intent.MaxRange + rangeTolerance)
            {
                return RejectSkill(intent, $"skill hit claimed at {distance:F1}m, {intent.SkillId} reaches {intent.MaxRange:F1}m");
            }

            LayerMask levelMask = config != null ? config.levelMask : Physics.DefaultRaycastLayers;
            if (!HitScanner.HasLineOfSight(intent.Origin, target.CombatCenter, levelMask, 0.35f))
            {
                return RejectSkill(intent, "line of sight is blocked");
            }

            // The instigator's multipliers are read from the instigator; the ability scale is the bounded
            // per-activation value this cast carried; the flat weapon-base bonus is read from the instigator
            // on its own channel and only reaches a skill that scales the weapon held (DamageResolver applies
            // each of them exactly once, and a skill with its own flat damage gets no weapon bonus at all).
            DamageInfo damage = DamageResolver.ResolveSkill(intent, hit, target,
                OutgoingMultiplier(instigator, intent.Weapon), intent.EffectScale,
                FlatWeaponBonus(instigator, intent.Weapon));
            if (!damage.IsValid) return RejectSkill(intent, "resolver produced no damage");

            target.ApplyDamage(damage);

            if (startsCooldown)
            {
                Dictionary<string, float> acceptedClock = SkillClockFor(intent.InstigatorCombatId);
                acceptedClock[intent.SkillId] = intent.Timestamp;
                if (intent.ActivationId > 0) activations[intent.SkillId] = intent.ActivationId;
            }

            if (!intent.TargetStatus.IsNone && target is IStatusTarget statusTarget)
            {
                statusTarget.ApplyStatus(intent.TargetStatus, intent.InstigatorCombatId);
            }

            AcceptedCount++;

            HitFeedback feedback = new HitFeedback(hit.HitPoint, damage.Amount, damage.Blocked, damage.Headshot,
                target.IsDead, target.Kind);

            if (instigator.Kind == EntityKind.LocalPlayer) GameEvents.RaiseDamageDealt(feedback);
            if (target.Kind == EntityKind.LocalPlayer) GameEvents.RaisePlayerDamaged(damage);

            return true;
        }

        public bool ApplyStatus(int targetCombatId, int sourceCombatId, StatusSpec spec)
        {
            if (spec.IsNone) return false;
            if (!CombatRegistry.TryGet(targetCombatId, out ICombatEntity target)) return false;
            if (target.IsDead) return false;
            if (!(target is IStatusTarget statusTarget)) return false;

            bool isSelf = targetCombatId == sourceCombatId;
            if (!isSelf)
            {
                if (!CombatRegistry.TryGet(sourceCombatId, out ICombatEntity source)) return false;
                if (!CombatTeams.AreHostile(source, target)) return false;
            }

            statusTarget.ApplyStatus(spec, sourceCombatId);
            return true;
        }

        private Dictionary<string, int> SkillActivationFor(int combatId)
        {
            if (!_lastSkillActivation.TryGetValue(combatId, out Dictionary<string, int> activations))
            {
                activations = new Dictionary<string, int>();
                _lastSkillActivation[combatId] = activations;
            }
            return activations;
        }

        private Dictionary<string, float> SkillClockFor(int combatId)
        {
            if (!_lastSkillTime.TryGetValue(combatId, out Dictionary<string, float> clock))
            {
                clock = new Dictionary<string, float>();
                _lastSkillTime[combatId] = clock;
            }
            return clock;
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

        private bool RejectSkill(SkillIntent intent, string reason)
        {
            RejectedCount++;
            if (logRejections)
            {
                Debug.Log($"[CombatAuthority] Rejected skill '{intent.SkillId}' from #{intent.InstigatorCombatId}: {reason}", this);
            }
            return false;
        }

        private bool Reject(AttackIntent intent, string reason, out string reasonOut)
        {
            reasonOut = reason;
            LastAttackRejectReason = reason;
            RejectedCount++;
            if (logRejections)
            {
                Debug.Log($"[CombatAuthority] Rejected attack from #{intent.InstigatorCombatId} ({intent.WeaponId}): {reason}", this);
            }
            return false;
        }
    }
}
