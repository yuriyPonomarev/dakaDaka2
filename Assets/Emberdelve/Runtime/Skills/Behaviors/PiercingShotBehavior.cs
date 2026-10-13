using UnityEngine;
using Emberdelve.Data;

namespace Emberdelve.Skills
{
    /// <summary>
    /// Piercing Shot: one heavy arrow that keeps going through up to <see cref="SkillData.projectileMaxTargets"/>
    /// valid enemies.
    ///
    /// Penetration is resolved by HitScanner in one pass: nearest first, one entry per combat entity,
    /// stopped by level geometry, never by the shooter. Because the scan already deduplicates per
    /// entity, and the per-cast list deduplicates across the whole cast, an enemy can only be hit once
    /// by this shot no matter how many of its colliders the arrow crosses.
    /// </summary>
    public sealed class PiercingShotBehavior : ISkillBehavior
    {
        public bool CanCast(SkillContext context, out string reason)
        {
            int ammoNeeded = context.Skill.consumesAmmoPerProjectile ? 1 : 0;
            return RangedSkillUtility.CanFireRangedSkill(context, ammoNeeded, out reason);
        }

        public bool Cast(SkillContext context)
        {
            SkillData skill = context.Skill;

            if (skill.consumesAmmoPerProjectile)
            {
                if (!context.Ranged.TryConsumeAmmo(1)) return false;
            }

            context.Ranged.CancelDraw();
            context.ResetScratch();

            Vector3 origin = context.Rig.GetMuzzlePosition();
            Vector3 aim = context.Rig.AimDirection;

            RangedSkillUtility.FireOne(context, origin, aim);
            return true;
        }
    }
}
