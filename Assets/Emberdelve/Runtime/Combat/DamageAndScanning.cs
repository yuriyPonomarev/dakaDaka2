using System.Collections.Generic;
using UnityEngine;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Stats;

namespace Emberdelve.Combat
{
    /// <summary>Turns an accepted intent + validated hit into the damage that is actually applied.</summary>
    public static class DamageResolver
    {
        public const float HeadshotMultiplier = 1.8f;

        /// <summary>
        /// Ordinary weapon damage. <paramref name="weaponBaseDamageBonus"/> is the authority-read flat bonus
        /// on the weapon's OWN base (U2B7 rolled melee_damage / projectile_damage): it enters through
        /// <see cref="WeaponData.DamageAtRange(float, float)"/> before falloff, so charge, the instigator
        /// multiplier and headshot all still multiply the already-boosted base exactly as they multiplied the
        /// authored one. 0 - every entity that is not a flat-bonus provider, and every weapon with no matching
        /// affix - resolves bit-for-bit to the previous number.
        /// </summary>
        public static DamageInfo Resolve(WeaponData weapon, in AttackIntent intent, in HitCandidate hit,
            ICombatEntity target, float instigatorMultiplier = 1f, float weaponBaseDamageBonus = 0f,
            CombatFlowTuning tuning = null)
        {
            float distance = Vector3.Distance(intent.Origin, hit.HitPoint);
            float amount = weapon.DamageAtRange(distance, weaponBaseDamageBonus);

            if (weapon.IsRanged)
            {
                amount *= Mathf.Lerp(weapon.minChargeDamageScale, 1f, intent.Charge);
            }

            amount *= Mathf.Max(0f, instigatorMultiplier);

            if (hit.Headshot) amount *= HeadshotMultiplier;

            return Finalize(amount, weapon, intent.Direction, intent.Origin, intent.InstigatorCombatId,
                intent.InstigatorKind, hit, target, null, HitLocation.Unspecified,
                weapon != null ? weapon.impactPower : 0f, tuning);
        }

        /// <summary>
        /// D1a swept melee damage. Same chain as <see cref="Resolve"/> (flat base bonus joins the weapon base,
        /// then the instigator multiplier, then guard and armour in Finalize), with two differences: the pattern's
        /// own damage multiplier applies, and the hit-location multiplier replaces the binary headshot factor.
        /// <paramref name="locationMultiplier"/> comes from the authority's own location table and the volume the
        /// sweep touched; a melee hit has no range falloff. DamageInfo.Headshot is true exactly for Head.
        /// </summary>
        public static DamageInfo ResolveSwing(WeaponData weapon, SwingPatternData pattern, in AttackIntent intent,
            in HitCandidate hit, HitLocation location, float locationMultiplier, ICombatEntity target,
            float instigatorMultiplier = 1f, float weaponBaseDamageBonus = 0f,
            float stepMultiplier = 1f, float zoneMultiplier = 1f, CombatFlowTuning tuning = null)
        {
            float amount = weapon.DamageAtRange(0f, weaponBaseDamageBonus);
            amount *= pattern != null ? Mathf.Max(0.05f, pattern.damageMultiplier) : 1f;
            amount *= Mathf.Max(0.05f, stepMultiplier);
            amount *= Mathf.Max(0f, instigatorMultiplier);
            amount *= Mathf.Max(0f, locationMultiplier);
            amount *= Mathf.Max(0f, zoneMultiplier);

            float impact = weapon.impactPower * (pattern != null ? Mathf.Max(0.01f, pattern.impactPowerScale) : 1f);
            return Finalize(amount, weapon, intent.Direction, intent.Origin, intent.InstigatorCombatId,
                intent.InstigatorKind, hit, target, null, location, impact, tuning);
        }

        /// <summary>
        /// Skill damage. A skill either brings its own number (flatDamage) or scales the weapon actually
        /// held, which is what keeps Multi-shot and Piercing Shot honest about the bow in the player's
        /// hands: range falloff, damage type and headshots all still come from the weapon asset.
        ///
        /// effectScale is the caster's per-activation ability scale (Docs/Design/ATTRIBUTES.md CHA rule).
        /// It is applied here and nowhere else in this path, once per accepted hit, on top of the weapon's
        /// own falloff and the instigator multiplier the authority read from the instigator - so the STR/DEX
        /// weapon factor, the passive/gear/status factors, charge and falloff each still count exactly once
        /// and a skill cannot be scaled by both its behavior and its resolver.
        ///
        /// weaponBaseDamageBonus is the same authority-read flat the ordinary attack path gets, and it is
        /// applied in the WEAPON branch only: a weapon-based skill scales the boosted base with its own
        /// damageMultiplier, while a skill that declares its own flat number (BattleCry) keeps that number
        /// untouched and gains nothing from the weapon in hand.
        /// </summary>
        public static DamageInfo ResolveSkill(in SkillIntent intent, in HitCandidate hit, ICombatEntity target,
            float instigatorMultiplier = 1f, float effectScale = 1f, float weaponBaseDamageBonus = 0f)
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

                amount = weapon.DamageAtRange(distance, weaponBaseDamageBonus);
                if (weapon.IsRanged) amount *= Mathf.Lerp(weapon.minChargeDamageScale, 1f, 1f);
                amount *= Mathf.Max(0.05f, skill.damageMultiplier);
            }

            amount *= Mathf.Max(0f, instigatorMultiplier);
            amount *= ClampEffectScale(effectScale);

            if (hit.Headshot) amount *= HeadshotMultiplier;

            WeaponData source = intent.Weapon;
            DamageType type = source != null ? source.damageType : skill.UsesFlatDamage ? DamageType.Holy : DamageType.Physical;

            return Finalize(amount, source, intent.Direction, intent.Origin, intent.InstigatorCombatId,
                intent.InstigatorKind, hit, target, type);
        }

        /// <summary>
        /// Bounds a claimed ability effect scale to the band AttributeRules can actually produce
        /// (0.6..1.4). A claim outside it is corrected to the nearest legal value rather than obeyed, and a
        /// missing or nonsense claim means "no scale" (1), which is what an entity with no six-attribute
        /// state - an enemy, an intent built before this amendment - resolves to.
        /// </summary>
        public static float ClampEffectScale(float scale)
        {
            if (!(scale > 0f) || float.IsNaN(scale) || float.IsInfinity(scale)) return 1f;
            if (scale < AttributeRules.MinEffectScale) return AttributeRules.MinEffectScale;
            if (scale > AttributeRules.MaxEffectScale) return AttributeRules.MaxEffectScale;
            return scale;
        }

        private static DamageInfo Finalize(float amount, WeaponData source, Vector3 direction, Vector3 origin,
            int instigatorCombatId, EntityKind instigatorKind, in HitCandidate hit, ICombatEntity target,
            DamageType? overrideType = null, HitLocation location = HitLocation.Unspecified,
            float impactPower = 0f, CombatFlowTuning tuning = null)
        {
            DamageType type = overrideType ?? (source != null ? source.damageType : DamageType.Physical);

            DamageInfo info = new DamageInfo(Mathf.Max(1f, amount), hit.HitPoint, direction, origin,
                instigatorCombatId, instigatorKind, type, false, hit.Headshot, 0f, location, impactPower);

            // D1b: a defender whose guard can break answers with the reduction AND whether the hit's impact power
            // reached its impact resist (reduced mitigation). Other defenders keep the plain directional guard.
            if (target is IGuardBreakDefender breakable)
            {
                if (breakable.TryResolveGuard(info, tuning ?? CombatFlowTuning.Default, out GuardResult guard))
                {
                    info = info.WithReduction(guard.Reduction, out _, guard.Broken);
                }
            }
            else if (target is IDefenseProvider defender && defender.TryGetGuardReduction(info, out float reduction))
            {
                info = info.WithReduction(reduction, out _);
            }

            // U2B8: armor is a defender-side PHYSICAL mitigation, applied after the directional guard and
            // independently of it. It applies from every direction (a hit from behind still meets the
            // breastplate, it simply does not meet the shield arc), it touches DamageType.Physical only, and it
            // is NOT run through WithReduction, because that would flag the hit as blocked and rewrite
            // BlockReduction. A target that is not an armor provider - an enemy, anything wearing nothing - is
            // never asked and contributes a 0 rating, which is the exact no-op below.
            if (info.Type == DamageType.Physical && target is IArmorRatingProvider armorProvider)
            {
                info = ApplyArmorRating(info, armorProvider.GetArmorRating());
            }

            return info;
        }

        /// <summary>
        /// Maps a claimed armor rating to the rating this resolver is willing to use: NaN, plus or minus
        /// Infinity and a negative claim all become 0, which is the exact no-op. A legal claim is returned
        /// unchanged, so a huge finite rating stays huge and is handled by the ratio below instead of being
        /// clamped away.
        /// </summary>
        public static float SanitizeArmorRating(float claimedRating)
        {
            if (float.IsNaN(claimedRating) || float.IsInfinity(claimedRating) || claimedRating < 0f) return 0f;
            return claimedRating;
        }

        /// <summary>
        /// The prototype armor rule (Docs/Design/ARMOR_RULES.md) applied to an already resolved, already
        /// guard-reduced hit: final = max(1, postGuardAmount * 100 / (100 + rating)). A 0 rating returns the
        /// same value untouched, so every zero-armor number this project already balances is unchanged. The
        /// ratio is evaluated in double: 100 + a rating near float.MaxValue cannot overflow there, and because
        /// the ratio is never greater than 1 the mitigated amount can never exceed the amount it started from,
        /// so the cast back to float cannot overflow either. An amount that is already NaN or infinite is
        /// returned untouched rather than repaired into a valid hit: armor mitigates damage, it does not
        /// validate it, and an invalid hit still fails DamageInfo.IsValid in the authority exactly as before.
        /// Blocked, Headshot, BlockReduction, point, direction, source position, source combat id, source kind
        /// and type are carried through unchanged, and the returned Amount is the number the authority hands
        /// to IDamageable.ApplyDamage, so the reported damage and the health actually lost are one number.
        /// </summary>
        public static DamageInfo ApplyArmorRating(in DamageInfo info, float claimedRating)
        {
            float rating = SanitizeArmorRating(claimedRating);
            if (rating <= 0f) return info;

            float amount = info.Amount;
            if (float.IsNaN(amount) || float.IsInfinity(amount)) return info;

            double mitigated = (double)amount * (100.0 / (100.0 + (double)rating));
            float final = mitigated >= 1.0 ? (float)mitigated : 1f;
            if (float.IsNaN(final) || float.IsInfinity(final)) final = 1f;

            return new DamageInfo(final, info.Point, info.Direction, info.SourcePosition, info.SourceCombatId,
                info.SourceKind, info.Type, info.Blocked, info.Headshot, info.BlockReduction, info.Location,
                info.ImpactPower, info.GuardBroken);
        }
    }

    /// <summary>Geometry queries used by ranged shots, area skills and line-of-sight checks. Melee no longer scans here: D1a swept melee lives in LocalCombatAuthority.</summary>
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
