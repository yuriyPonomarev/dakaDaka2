using UnityEngine;
using Emberdelve.Combat;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Player;

namespace Emberdelve.Skills
{
    /// <summary>
    /// Battle Cry: a self-centred burst that damages and slows every hostile entity inside its radius,
    /// and empowers the caster.
    ///
    /// Both halves go through the combat authority. Each victim gets its own SkillIntent with its own
    /// sequence from the shared per-entity allocator, so the burst is one command stream a server could
    /// replay, not a loop that writes health. The self-buff goes through authority ApplyStatus for the
    /// same reason: even a buff on yourself is something a server has to be able to confirm.
    ///
    /// The damage is the skill's own flat number (see SkillData.flatDamage), so the cry works whatever
    /// is held, including a crossbow or nothing at all.
    /// </summary>
    public sealed class BattleCryBehavior : ISkillBehavior
    {
        public bool CanCast(SkillContext context, out string reason)
        {
            if (context.Rig == null)
            {
                reason = "no caster";
                return false;
            }

            if (context.Authority == null)
            {
                reason = "no combat authority in the scene";
                return false;
            }

            reason = null;
            return true;
        }

        public bool Cast(SkillContext context)
        {
            SkillData skill = context.Skill;
            PlayerRig rig = context.Rig;
            Vector3 center = rig.CombatCenter;

            context.ResetScratch();

            int found = HitScanner.CollectRadialHits(center, skill.radius, rig.CombatId, skill.maxTargets,
                context.Hits, context.HitMask, context.LevelMask);

            int resolved = 0;

            for (int i = 0; i < found; i++)
            {
                HitCandidate hit = context.Hits[i];

                if (!CombatRegistry.TryGet(hit.TargetCombatId, out ICombatEntity target)) continue;
                if (target.IsDead) continue;
                if (!CombatTeams.AreHostile(rig, target)) continue;

                Vector3 outward = target.CombatCenter - center;
                outward.y = 0f;
                if (outward.sqrMagnitude < 0.0001f) outward = rig.Facing;

                int sequence = CombatRegistry.NextSequence(rig.CombatId);

                SkillIntent intent = new SkillIntent(rig.CombatId, rig.Kind, skill, context.ActiveWeapon,
                    center, outward, skill.radius, sequence, Time.time, skill.TargetStatusSpec, context.ActivationId);

                if (context.Authority.SubmitSkill(intent, hit)) resolved++;
            }

            // The self-buff is a separate authority request, so it is checked by the same rules a server
            // would apply to it, and it lands even when the cry hit nothing at all.
            if (skill.affectsSelf && !skill.SelfStatusSpec.IsNone)
            {
                context.Authority.ApplyStatus(rig.CombatId, rig.CombatId, skill.SelfStatusSpec);
            }

            return true;
        }
    }
}
