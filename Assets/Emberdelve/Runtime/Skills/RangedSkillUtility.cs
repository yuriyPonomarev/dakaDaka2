using System.Collections.Generic;
using UnityEngine;
using Emberdelve.Combat;
using Emberdelve.Core;
using Emberdelve.Data;

namespace Emberdelve.Skills
{
    /// <summary>
    /// Shared resolution for the archer's projectile skills. One call fires one projectile along one
    /// direction: it asks HitScanner what that projectile can legitimately reach (nearest first, one
    /// entry per entity, stopped by walls), submits one SkillIntent per valid target through the combat
    /// authority, and spawns the cosmetic arrow.
    ///
    /// The per-cast <see cref="SkillContext.AlreadyHit"/> list is what makes "no duplicate hits" true:
    /// a volley cannot delete one enemy by landing three arrows on the same body, and a piercing shot
    /// cannot hit the same body twice on the way through.
    /// </summary>
    internal static class RangedSkillUtility
    {
        public static bool CanFireRangedSkill(SkillContext context, int shotsNeeded, out string reason)
        {
            WeaponData weapon = context.ActiveWeapon;

            if (weapon == null || !weapon.IsRanged)
            {
                reason = "needs a ranged weapon in hand";
                return false;
            }

            if (context.Ranged == null || !context.Ranged.enabled)
            {
                reason = "ranged combat is not active";
                return false;
            }

            if (context.Authority == null)
            {
                reason = "no combat authority in the scene";
                return false;
            }

            if (shotsNeeded > 0 && !context.Ranged.HasAmmo(shotsNeeded))
            {
                string ammoName = string.IsNullOrEmpty(weapon.ammoDisplayName) ? "ammo" : weapon.ammoDisplayName;
                reason = $"needs {shotsNeeded} {ammoName}";
                return false;
            }

            reason = null;
            return true;
        }

        /// <summary>
        /// Fires one projectile. Returns the number of authority-accepted hits, which may be zero: a shot
        /// into empty air is still a shot, and the arrow visual proves it happened.
        /// </summary>
        public static int FireOne(SkillContext context, Vector3 origin, Vector3 aim)
        {
            SkillData skill = context.Skill;
            WeaponData weapon = context.ActiveWeapon;
            if (weapon == null) return 0;

            float range = Mathf.Max(1f, weapon.rangedRange);

            context.Hits.Clear();
            int found = HitScanner.CollectRangedHits(origin, aim, range, context.Rig.CombatId,
                skill.projectileMaxTargets, context.Hits, context.HitMask, context.LevelMask);

            Vector3 endpoint = origin + aim * range;
            int applied = 0;

            for (int i = 0; i < found; i++)
            {
                HitCandidate hit = context.Hits[i];

                if (!CombatRegistry.TryGet(hit.TargetCombatId, out ICombatEntity target)) continue;
                if (target.IsDead) continue;
                if (!CombatTeams.AreHostile(context.Rig, target)) continue;

                // The arrow still sticks where it physically stopped, even when this cast already hit
                // that entity with another projectile.
                endpoint = hit.HitPoint;

                if (context.AlreadyHit.Contains(hit.TargetCombatId)) continue;

                int sequence = CombatRegistry.NextSequence(context.Rig.CombatId);
                // The intent names the activation the authority opened and nothing it could be trusted for:
                // the authority reads the CHA scale from the caster itself and applies it exactly once per
                // accepted hit. Range, spread, projectile count, charge and distance falloff are the weapon's
                // and are deliberately not scaled here or anywhere else in this path.
                SkillIntent intent = new SkillIntent(context.Rig.CombatId, context.Rig.Kind, skill, weapon,
                    origin, aim, range, sequence, Time.time, default, context.ActivationId);

                if (context.Authority.SubmitSkill(intent, hit))
                {
                    applied++;
                    context.AlreadyHit.Add(hit.TargetCombatId);
                }
            }

            ProjectileVisual.SpawnArrow(origin, endpoint, weapon);
            return applied;
        }

        /// <summary>Spread direction for shot <paramref name="index"/> of <paramref name="count"/>, centered on the aim.</summary>
        public static Vector3 SpreadDirection(Vector3 aim, int index, int count, float totalSpreadDegrees)
        {
            if (count <= 1 || totalSpreadDegrees <= 0.01f) return aim;

            float t = index / (count - 1f) - 0.5f;
            float angle = t * Mathf.Clamp(totalSpreadDegrees, 0f, 170f);
            return Quaternion.AngleAxis(angle, Vector3.up) * aim;
        }
    }
}
