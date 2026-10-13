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

        public static DamageInfo Resolve(WeaponData weapon, in AttackIntent intent, in HitCandidate hit,
            ICombatEntity target, float instigatorMultiplier = 1f)
        {
            float distance = Vector3.Distance(intent.Origin, hit.HitPoint);
            float amount = weapon.DamageAtRange(distance);

            if (weapon.IsRanged)
            {
                amount *= Mathf.Lerp(weapon.minChargeDamageScale, 1f, intent.Charge);
            }

            amount *= Mathf.Max(0f, instigatorMultiplier);

            if (hit.Headshot) amount *= HeadshotMultiplier;

            return Finalize(amount, intent.Weapon, intent.Direction, intent.Origin, intent.InstigatorCombatId,
                intent.InstigatorKind, hit, target);
        }

        /// <summary>
        /// Skill damage. A skill either brings its own number (flatDamage) or scales the weapon actually
        /// held, which is what keeps Multi-shot and Piercing Shot honest about the bow in the player's
        /// hands: range falloff, damage type and headshots all still come from the weapon asset.
        /// </summary>
        public static DamageInfo ResolveSkill(in SkillIntent intent, in HitCandidate hit, ICombatEntity target,
            float instigatorMultiplier = 1f)
        {
            SkillData skill = intent.Skill;
            float distance = Vector3.Distance(intent.Origin, hit.HitPoint);
            float amount;

            if (skill == null) return DamageInfo.Invalid;

            if (skill.UsesFlatDamage)
            {
                amount = skill.flatDamage;
            }
            else
            {
                WeaponData weapon = intent.Weapon;
                if (weapon == null) return DamageInfo.Invalid;

                amount = weapon.DamageAtRange(distance);
                if (weapon.IsRanged) amount *= Mathf.Lerp(weapon.minChargeDamageScale, 1f, 1f);
                amount *= Mathf.Max(0.05f, skill.damageMultiplier);
            }

            amount *= Mathf.Max(0f, instigatorMultiplier);

            if (hit.Headshot) amount *= HeadshotMultiplier;

            WeaponData source = intent.Weapon;
            DamageType type = source != null ? source.damageType : skill.UsesFlatDamage ? DamageType.Holy : DamageType.Physical;

            return Finalize(amount, source, intent.Direction, intent.Origin, intent.InstigatorCombatId,
                intent.InstigatorKind, hit, target, type);
        }

        private static DamageInfo Finalize(float amount, WeaponData source, Vector3 direction, Vector3 origin,
            int instigatorCombatId, EntityKind instigatorKind, in HitCandidate hit, ICombatEntity target,
            DamageType? overrideType = null)
        {
            DamageType type = overrideType ?? (source != null ? source.damageType : DamageType.Physical);

            DamageInfo info = new DamageInfo(Mathf.Max(1f, amount), hit.HitPoint, direction, origin,
                instigatorCombatId, instigatorKind, type, false, hit.Headshot, 0f);

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

        /// <summary>
        /// Everything one projectile or ray legitimately hits along a direction, nearest first, at most one
        /// entry per combat entity. Level geometry stops the shot; the instigator's own colliders are
        /// skipped rather than stopping it, which is what lets a piercing shot pass through a body without
        /// ever hitting the shooter.
        /// </summary>
        public static int CollectRangedHits(Vector3 origin, Vector3 direction, float maxRange, int instigatorId,
            int maxTargets, List<HitCandidate> output, LayerMask hitMask, LayerMask levelMask)
        {
            output.Clear();
            if (maxTargets <= 0) return 0;

            int mask = hitMask.value | levelMask.value;
            int count = Physics.RaycastNonAlloc(origin, direction, _rayBuffer, Mathf.Max(0.5f, maxRange), mask,
                QueryTriggerInteraction.Ignore);

            System.Array.Sort(_rayBuffer, 0, count, Comparer<RaycastHit>.Create((a, b) => a.distance.CompareTo(b.distance)));

            int accepted = 0;
            for (int i = 0; i < count && accepted < maxTargets; i++)
            {
                Collider collider = _rayBuffer[i].collider;
                if (collider == null) continue;

                ICombatEntity entity = collider.GetComponentInParent<ICombatEntity>();
                if (entity == null) break;                        // wall or prop: the shot stops here
                if (entity.IsDead) continue;
                if (entity.CombatId == instigatorId) continue;     // never shoot yourself
                if (Contains(output, entity.CombatId)) continue;   // one hit per entity per projectile

                bool headshot = entity.HeadCollider != null && collider == entity.HeadCollider;
                output.Add(new HitCandidate(entity.CombatId, _rayBuffer[i].point, headshot));
                accepted++;
            }

            return accepted;
        }

        /// <summary>Hostile entities inside a radius, nearest first, each listed once.</summary>
        public static int CollectRadialHits(Vector3 center, float radius, int instigatorId, int maxTargets,
            List<HitCandidate> output, LayerMask hitMask, LayerMask levelMask)
        {
            output.Clear();
            if (maxTargets <= 0) return 0;

            int count = Physics.OverlapSphereNonAlloc(center, Mathf.Max(0.5f, radius), _overlapBuffer, hitMask,
                QueryTriggerInteraction.Ignore);

            int accepted = 0;
            while (accepted < maxTargets)
            {
                ICombatEntity best = null;
                float bestDistance = float.MaxValue;
                Collider bestCollider = null;

                for (int i = 0; i < count; i++)
                {
                    Collider collider = _overlapBuffer[i];
                    if (collider == null) continue;

                    ICombatEntity entity = collider.GetComponentInParent<ICombatEntity>();
                    if (entity == null || entity.IsDead) continue;
                    if (entity.CombatId == instigatorId) continue;
                    if (Contains(output, entity.CombatId)) continue;

                    float distance = (entity.CombatCenter - center).magnitude;
                    if (distance > radius + 0.35f) continue;
                    if (distance >= bestDistance) continue;

                    bestDistance = distance;
                    best = entity;
                    bestCollider = collider;
                }

                if (best == null) break;

                bool headshot = best.HeadCollider != null && bestCollider == best.HeadCollider;
                output.Add(new HitCandidate(best.CombatId, bestCollider.ClosestPoint(center), headshot));
                accepted++;
            }

            return accepted;
        }

        private static bool Contains(List<HitCandidate> output, int combatId)
        {
            for (int i = 0; i < output.Count; i++)
            {
                if (output[i].TargetCombatId == combatId) return true;
            }
            return false;
        }
    }
}
