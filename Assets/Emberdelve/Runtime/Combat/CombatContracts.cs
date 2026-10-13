using System.Collections.Generic;
using UnityEngine;
using Emberdelve.Core;
using Emberdelve.Data;

namespace Emberdelve.Combat
{
    /// <summary>
    /// Player/AI intent only. It carries no damage value on purpose: damage is computed by the
    /// authority from weapon data plus validated geometry. A future server receives this same struct
    /// over the network and runs the identical validation.
    /// </summary>
    public readonly struct AttackIntent
    {
        public readonly int InstigatorCombatId;
        public readonly EntityKind InstigatorKind;
        public readonly string WeaponId;
        public readonly WeaponData Weapon;
        public readonly float Charge;
        public readonly Vector3 Origin;
        public readonly Vector3 Direction;
        public readonly float MaxRange;
        public readonly int Sequence;
        public readonly float Timestamp;

        public AttackIntent(int instigatorCombatId, EntityKind instigatorKind, WeaponData weapon, float charge,
            Vector3 origin, Vector3 direction, float maxRange, int sequence, float timestamp)
        {
            InstigatorCombatId = instigatorCombatId;
            InstigatorKind = instigatorKind;
            Weapon = weapon;
            WeaponId = weapon != null ? weapon.weaponId : string.Empty;
            Charge = Mathf.Clamp01(charge);
            Origin = origin;
            Direction = direction.normalized;
            MaxRange = maxRange;
            Sequence = sequence;
            Timestamp = timestamp;
        }
    }

    /// <summary>Where the attacker claims the swing/shot connected. The authority re-resolves the target from its own registry.</summary>
    public readonly struct HitCandidate
    {
        public readonly int TargetCombatId;
        public readonly Vector3 HitPoint;
        public readonly bool Headshot;

        public HitCandidate(int targetCombatId, Vector3 hitPoint, bool headshot)
        {
            TargetCombatId = targetCombatId;
            HitPoint = hitPoint;
            Headshot = headshot;
        }
    }

    public interface ICombatAuthority
    {
        /// <summary>Returns true when the authority accepted the intent and applied damage.</summary>
        bool SubmitAttack(AttackIntent intent, HitCandidate hit);
    }

    /// <summary>
    /// Local stand-in for the entity table an authoritative server would own: combat id -> entity.
    /// Ids are allocated here so nothing trusts a scene reference handed over by a client.
    /// </summary>
    public static class CombatRegistry
    {
        private static readonly Dictionary<int, ICombatEntity> _entities = new Dictionary<int, ICombatEntity>();
        private static readonly List<ICombatEntity> _ordered = new List<ICombatEntity>();
        private static int _nextId = 1;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatic()
        {
            _entities.Clear();
            _ordered.Clear();
            _nextId = 1;
        }

        public static int AllocateId() => _nextId++;

        public static void Register(int id, ICombatEntity entity)
        {
            if (entity == null) return;
            _entities[id] = entity;
            if (!_ordered.Contains(entity)) _ordered.Add(entity);
        }

        public static void Unregister(int id, ICombatEntity entity)
        {
            _entities.Remove(id);
            if (entity != null) _ordered.Remove(entity);
        }

        public static bool TryGet(int id, out ICombatEntity entity) => _entities.TryGetValue(id, out entity);

        public static IReadOnlyList<ICombatEntity> All => _ordered;

        public static bool TryGetNearest(Vector3 position, EntityKind kind, float maxDistance, out ICombatEntity nearest)
        {
            nearest = null;
            float best = maxDistance * maxDistance;
            for (int i = 0; i < _ordered.Count; i++)
            {
                ICombatEntity candidate = _ordered[i];
                if (candidate == null || candidate.IsDead) continue;
                if (candidate.Kind != kind) continue;
                float sqr = (candidate.CombatCenter - position).sqrMagnitude;
                if (sqr <= best)
                {
                    best = sqr;
                    nearest = candidate;
                }
            }
            return nearest != null;
        }
    }
}
