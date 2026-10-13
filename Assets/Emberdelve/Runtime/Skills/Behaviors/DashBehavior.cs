using UnityEngine;

namespace Emberdelve.Skills
{
    /// <summary>
    /// Dash: a short, collision-respecting burst of ground movement.
    ///
    /// The dash is handed to PlayerMovement, which owns the CharacterController, gravity and the
    /// grounded check. That is deliberate: a dash that teleports the transform would pass through walls,
    /// and it would not be the same movement a server validates. Here the dash is only an intent to move,
    /// and the controller decides how far it actually gets.
    ///
    /// Direction follows the movement intent the player is already producing, so a strafe dash strafes,
    /// and falls back to facing when standing still. That is the same flattened view-relative vector
    /// Milestone 1 established.
    /// </summary>
    public sealed class DashBehavior : ISkillBehavior
    {
        public bool CanCast(SkillContext context, out string reason)
        {
            if (context.Rig == null || context.Rig.Movement == null)
            {
                reason = "no movement component";
                return false;
            }

            if (context.Rig.Movement.IsDashing)
            {
                reason = "already dashing";
                return false;
            }

            reason = null;
            return true;
        }

        public bool Cast(SkillContext context)
        {
            Vector3 intent = context.Rig.Movement.DesiredMoveIntent;
            Vector3 direction = intent.sqrMagnitude > 0.01f ? intent : context.Rig.Facing;

            return context.Rig.Movement.TryStartDash(direction, context.Skill.dashDistance, context.Skill.dashDuration);
        }
    }
}
