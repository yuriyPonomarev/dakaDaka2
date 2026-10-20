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
    ///
    /// Docs/Design/ATTRIBUTES.md attaches CHA to beneficial magnitudes and durations, and this slice's
    /// mapping puts the cry's CHA term in the two statuses it applies: the durations scale, and each
    /// magnitude scales as its DELTA from neutral 1 (see ScaleStatus). The flat damage is not rescaled
    /// here, and no effect scale is carried into the intent for it.
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

            // One snapshot for the whole cast: every victim of this burst and the caster's own buff are
            // resolved against the same scale, so a gear change halfway through the loop cannot split one
            // cast into two different effects.
            float scale = context.EffectScale;
            StatusSpec targetStatus = ScaleStatus(skill.TargetStatusSpec, scale);
            StatusSpec selfStatus = ScaleStatus(skill.SelfStatusSpec, scale);

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

                // Radius, target cap, hostility and the authority's own range/line-of-sight/cooldown rules
                // are untouched. The intent carries the scaled status it is allowed to apply, and no damage
                // magnitude of any kind: the authority still derives the flat 14 from the skill asset.
                SkillIntent intent = new SkillIntent(rig.CombatId, rig.Kind, skill, context.ActiveWeapon,
                    center, outward, skill.radius, sequence, Time.time, targetStatus, context.ActivationId);

                if (context.Authority.SubmitSkill(intent, hit)) resolved++;
            }

            // The self-buff is a separate authority request, so it is checked by the same rules a server
            // would apply to it, and it lands even when the cry hit nothing at all.
            if (skill.affectsSelf && !selfStatus.IsNone)
            {
                context.Authority.ApplyStatus(rig.CombatId, rig.CombatId, selfStatus);
            }

            return true;
        }

        /// <summary>
        /// CHA scales a status's duration and the DELTA its magnitude means from neutral 1, never the
        /// authored magnitude wholesale: a 1.25 buff becomes 1 + 0.25 * scale and a 0.55 slow becomes
        /// 1 - 0.45 * scale, so a stronger personal force makes the effect bigger (or the debuff deeper)
        /// against the neutral baseline instead of re-reading the asset number. The scale itself is already
        /// clamped to 0.6..1.4 by AttributeRules, StatusSpec clamps duration and magnitude to non-negative,
        /// and StatusComponent still clamps the speed and damage multipliers those magnitudes become, so
        /// no bound is bypassed and the shared skill asset is only ever read.
        /// </summary>
        private static StatusSpec ScaleStatus(StatusSpec spec, float scale)
        {
            if (spec.IsNone) return StatusSpec.None;

            return new StatusSpec(spec.Kind, spec.Duration * scale, 1f + (spec.Magnitude - 1f) * scale);
        }
    }
}
