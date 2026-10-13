using UnityEngine;
using Emberdelve.Data;

namespace Emberdelve.Skills
{
    /// <summary>
    /// Multi-shot: several arrows at once, fanned out across the weapon's own spread angle.
    ///
    /// It is a real volley, not a damage multiplier: each arrow resolves its own ray, claims its own
    /// command sequence, and is accepted or rejected by the authority on its own merits. Ammunition is
    /// spent from the bow's magazine through RangedCombat, so the skill respects the same ammo rules as
    /// a normal shot instead of printing free arrows.
    /// </summary>
    public sealed class MultiShotBehavior : ISkillBehavior
    {
        public bool CanCast(SkillContext context, out string reason)
        {
            SkillData skill = context.Skill;
            int shots = Mathf.Max(1, skill.projectileCount);
            int ammoNeeded = skill.consumesAmmoPerProjectile ? shots : 0;

            return RangedSkillUtility.CanFireRangedSkill(context, ammoNeeded, out reason);
        }

        public bool Cast(SkillContext context)
        {
            SkillData skill = context.Skill;
            int shots = Mathf.Max(1, skill.projectileCount);

            if (skill.consumesAmmoPerProjectile)
            {
                if (!context.Ranged.TryConsumeAmmo(shots)) return false;
            }

            context.Ranged.CancelDraw();
            context.ResetScratch();

            Vector3 origin = context.Rig.GetMuzzlePosition();
            Vector3 aim = context.Rig.AimDirection;

            for (int i = 0; i < shots; i++)
            {
                Vector3 direction = RangedSkillUtility.SpreadDirection(aim, i, shots, skill.spreadDegrees);
                RangedSkillUtility.FireOne(context, origin, direction);
            }

            return true;
        }
    }
}
