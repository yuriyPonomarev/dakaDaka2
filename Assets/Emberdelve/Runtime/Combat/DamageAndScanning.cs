using System.Collections.Generic;
using UnityEngine;
using Emberdelve.Core;
using Emberdelve.Data;

namespace Emberdelve.Combat
{
    /// <summary>Turns an accepted intent + validated hit into the damage that is actually applied.</summary>
    public static class DamageResolver
    {
        public const float HeadshotMultiplier = 1.8f;

        public static DamageInfo Resolve(WeaponData weapon, in AttackIntent intent, in HitCandidate hit, ICombatEntity target)
        {
            float distance = Vector3.Distance(intent.Origin, hit.HitPoint);
            float amount = weapon.DamageAtRange(distance);

            if (weapon.IsRanged)
            {
                amount *= Mathf.Lerp(weapon.minChargeDamageScale, 1f, intent.Charge);
            }

            if (hit.Headshot) amount *= HeadshotMultiplier;

            DamageInfo info = new DamageInfo(amount, hit.HitPoint, intent.Direction, intent.Origin,
                intent.InstigatorCombatId, intent.InstigatorKind, weapon.damageType, false, hit.Headshot, 0f);

            if (target is IDefenseProvider defender && defender.TryGetGuardReduction(info, out float reduction))
            {
                info = info.WithReduction(reduction, out _);
            }

            return info;
        }
    }

    /// <summary>Geometry queries used by melee swings and line-of-sight checks.</summary>
    public static class HitScanner
    {
        private const int MaxQueryResults = 24;
        private static readonly Collider[] _overlapBuffer = new Collider[MaxQueryResults];
        private static readonly RaycastHit[] _rayBuffer = new RaycastHit[MaxQueryResults];

        public static bool HasLineOfSight(Vector3 from, Vector3 to, LayerMask levelMask, float tolerance = 0.25f)
        {
            Vector3 delta = to - from;
            float distance = delta.magnitude;
            if (distance <= tolerance) return true;

            int hits = Physics.RaycastNonAlloc(from, delta / distance, _rayBuffer, distance - tolerance, levelMask, QueryTriggerInteraction.Ignore);
            return hits == 0;
        }

        public static int CollectMeleeHits(Vector3 origin, Vector3 facing, WeaponData weapon, int instigatorId,
            LayerMask hitMask, LayerMask levelMask, List<HitCandidate> output, int maxHits)
        {
            output.Clear();
            if (weapon == null) return 0;

            float probeDistance = weapon.reach * 0.55f;
            float probeRadius = weapon.reach * 0.55f + weapon.hitSphereRadius;
            Vector3 probeCenter = origin + facing * probeDistance;

            int count = Physics.OverlapSphereNonAlloc(probeCenter, probeRadius, _overlapBuffer, hitMask, QueryTriggerInteraction.Ignore);
            float halfArc = weapon.arcAngle * 0.5f;
            int accepted = 0;

            for (int i = 0; i < count && accepted < maxHits; i++)
            {
                Collider collider = _overlapBuffer[i];
                if (collider == null) continue;

                ICombatEntity entity = collider.GetComponentInParent<ICombatEntity>();
                if (entity == null || entity.IsDead) continue;
                if (entity.CombatId == instigatorId) continue;

                Vector3 center = entity.CombatCenter;
                Vector3 toTarget = center - origin;
                float distance = toTarget.magnitude;
                if (distance > weapon.reach + weapon.hitSphereRadius) continue;

                if (distance > 0.4f)
                {
                    float angle = Vector3.Angle(facing, toTarget);
                    if (angle > halfArc) continue;
                    if (!HasLineOfSight(origin, center, levelMask)) continue;
                }

                Vector3 hitPoint = collider.ClosestPoint(origin);
                bool headshot = entity.HeadCollider != null && collider == entity.HeadCollider;

                output.Add(new HitCandidate(entity.CombatId, hitPoint, headshot));
                accepted++;
            }

            return accepted;
        }
    }
}
