using System.Collections.Generic;
using UnityEngine;
using Emberdelve.Combat;
using Emberdelve.Core;
using Emberdelve.Data;

namespace Emberdelve.Skills
{
    /// <summary>
    /// Shared launch path for archer projectile skills. The authority advances each arrow and discovers
    /// body, blade and wall contacts in order; client-side target scans never decide a hit.
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
        /// Fires one projectile. Returns one when the authority accepted the launch, including a shot into air.
        /// </summary>
        public static int FireOne(SkillContext context, Vector3 origin, Vector3 aim, int projectileIndex = 0)
        {
            SkillData skill = context.Skill;
            WeaponData weapon = context.ActiveWeapon;
            if (weapon == null) return 0;

            float range = Mathf.Max(1f, weapon.rangedRange);

            context.Hits.Clear();
            int sequence = CombatRegistry.NextSequence(context.Rig.CombatId);
            SkillIntent intent = new SkillIntent(context.Rig.CombatId, context.Rig.Kind, skill, weapon,
                origin, aim, range, sequence, Time.time, default, context.ActivationId);
            return context.Authority.LaunchSkillProjectile(intent, projectileIndex) ? 1 : 0;
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
