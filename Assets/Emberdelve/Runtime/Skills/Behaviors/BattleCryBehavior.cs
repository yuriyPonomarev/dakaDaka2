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
    /// The behavior only PROPOSES hits. Each victim gets its own SkillIntent with its own sequence from the
    /// shared per-entity allocator, carrying the activation id the authority handed out when it opened the
    /// cast. Everything that has an effect is the authority's: the flat damage comes from the skill asset, the
    /// slow on the victims and the empowering self-buff are the asset's statuses scaled by the caster's own
    /// CHA effect scale, applied by the authority when the cast completes (self) and when it accepts a hit
    /// (victim). The behavior neither reads the scale for those nor sends a status or a scale in the intent.
    ///
    /// Docs/Design/ATTRIBUTES.md attaches CHA to beneficial magnitudes and durations, and this slice's
    /// mapping puts the cry's CHA term in the two statuses it applies (LocalCombatAuthority.ScaleStatus).
    /// The flat damage is not rescaled.
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

                // Radius, target cap, hostility, line of sight and the cooldown are the authority's own rules.
                // The intent carries no status and no scale (the authority ignores them either way).
                SkillIntent intent = new SkillIntent(rig.CombatId, rig.Kind, skill, context.ActiveWeapon,
                    center, outward, skill.radius, sequence, Time.time, default, context.ActivationId);

                context.Authority.SubmitSkill(rig.CombatSender, intent, hit, out _);
            }

            return true;
        }
    }
}
