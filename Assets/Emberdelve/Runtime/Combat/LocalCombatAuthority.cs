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

        private readonly Dictionary<int, float> _lastAttackTime = new Dictionary<int, float>();
        private readonly Dictionary<int, int> _lastSequence = new Dictionary<int, int>();
        private readonly Dictionary<int, Dictionary<string, float>> _lastSkillTime = new Dictionary<int, Dictionary<string, float>>();
        private readonly Dictionary<int, Dictionary<string, int>> _lastSkillActivation = new Dictionary<int, Dictionary<string, int>>();

        /// <summary>Small slack so a locally legal cast is not rejected for float rounding.</summary>
        private const float SkillClockTolerance = 0.05f;

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
            return Mathf.Max(0f, last + Mathf.Max(0.1f, weapon.cooldown) * cooldownGrace - Time.time);
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

        public bool SubmitAttack(AttackIntent intent, HitCandidate hit)
        {
            if (intent.Weapon == null) return Reject(intent, "weapon data missing");
            if (!CombatRegistry.TryGet(intent.InstigatorCombatId, out ICombatEntity instigator)) return Reject(intent, "unknown instigator id");
            if (instigator.IsDead) return Reject(intent, "instigator is dead");
            if (!CombatRegistry.TryGet(hit.TargetCombatId, out ICombatEntity target)) return Reject(intent, "unknown target id");
            if (target.IsDead) return false;
            if (target == instigator) return Reject(intent, "self hit");
            if (!CombatTeams.AreHostile(instigator, target)) return Reject(intent, "target is not hostile to the instigator");

            if (_lastSequence.TryGetValue(intent.InstigatorCombatId, out int lastSequence) && intent.Sequence <= lastSequence)
            {
                return Reject(intent, "stale or repeated command sequence");
            }
            _lastSequence[intent.InstigatorCombatId] = intent.Sequence;

            float cooldown = Mathf.Max(0.1f, intent.Weapon.cooldown);
            if (_lastAttackTime.TryGetValue(intent.InstigatorCombatId, out float lastAttack))
            {
                if (intent.Timestamp - lastAttack < cooldown * cooldownGrace)
                {
                    return Reject(intent, "attack faster than weapon cooldown");
                }
            }
            _lastAttackTime[intent.InstigatorCombatId] = intent.Timestamp;

            if (Vector3.Distance(instigator.CombatCenter, intent.Origin) > maxOriginOffsetFromInstigator)
            {
                return Reject(intent, "attack origin is not attached to the instigator");
            }

            float distance = Vector3.Distance(intent.Origin, hit.HitPoint);
            if (distance > intent.MaxRange + rangeTolerance)
            {
                return Reject(intent, $"hit claimed at {distance:F1}m, weapon reaches {intent.MaxRange:F1}m");
            }

            LayerMask levelMask = config != null ? config.levelMask : Physics.DefaultRaycastLayers;
            if (!HitScanner.HasLineOfSight(intent.Origin, target.CombatCenter, levelMask, 0.35f))
            {
                return Reject(intent, "line of sight is blocked");
            }

            DamageInfo damage = DamageResolver.Resolve(intent.Weapon, intent, hit, target, OutgoingMultiplier(instigator, intent.Weapon));
            if (!damage.IsValid) return Reject(intent, "resolver produced no damage");

            target.ApplyDamage(damage);

            AcceptedCount++;

            HitFeedback feedback = new HitFeedback(hit.HitPoint, damage.Amount, damage.Blocked, damage.Headshot,
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

            DamageInfo damage = DamageResolver.ResolveSkill(intent, hit, target, OutgoingMultiplier(instigator, intent.Weapon));
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

        private bool RejectSkill(SkillIntent intent, string reason)
        {
            RejectedCount++;
            if (logRejections)
            {
                Debug.Log($"[CombatAuthority] Rejected skill '{intent.SkillId}' from #{intent.InstigatorCombatId}: {reason}", this);
            }
            return false;
        }

        private bool Reject(AttackIntent intent, string reason)
        {
            RejectedCount++;
            if (logRejections)
            {
                Debug.Log($"[CombatAuthority] Rejected attack from #{intent.InstigatorCombatId} ({intent.WeaponId}): {reason}", this);
            }
            return false;
        }
    }
}
