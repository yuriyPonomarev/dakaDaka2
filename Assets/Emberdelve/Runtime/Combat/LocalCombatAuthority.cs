using System.Collections.Generic;
using UnityEngine;
using Emberdelve.Core;

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

        public int AcceptedCount { get; private set; }
        public int RejectedCount { get; private set; }

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

            DamageInfo damage = DamageResolver.Resolve(intent.Weapon, intent, hit, target);
            target.ApplyDamage(damage);

            AcceptedCount++;

            HitFeedback feedback = new HitFeedback(hit.HitPoint, damage.Amount, damage.Blocked, damage.Headshot,
                target.IsDead, target.Kind);

            if (instigator.Kind == EntityKind.LocalPlayer) GameEvents.RaiseDamageDealt(feedback);
            if (target.Kind == EntityKind.LocalPlayer) GameEvents.RaisePlayerDamaged(damage);

            return true;
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
