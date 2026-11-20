using UnityEngine;

namespace Emberdelve.AI
{
    /// <summary>Same authored collapse pose and reverse rise on host and remote view; no gameplay decisions.</summary>
    public static class EnemyDormantPose
    {
        /// <summary>
        /// The shield arm layer (layer 1 of a shield bearer's controller: left arm masked to the guard) holds the shield up while the body
        /// stands; it lets go (weight to 0) while dead, dormant or waking so the collapse and the rise move the whole body. No-op for a
        /// controller without that layer.
        /// </summary>
        public static void BlendShieldArm(Animator animator, bool held, float dt)
        {
            if (animator == null || animator.layerCount < 2) return;
            float weight = animator.GetLayerWeight(1);
            float target = held ? 1f : 0f;
            animator.SetLayerWeight(1, Mathf.MoveTowards(weight, target, dt / 0.2f));
        }

        public static bool Apply(Animator animator, bool dormant, bool waking, float elapsed, ref bool driven)
        {
            if (animator == null) return false;
            if (!dormant && !waking)
            {
                if (driven)
                {
                    animator.speed = 1f;
                    animator.CrossFadeInFixedTime("Idle", .12f);
                    driven = false;
                }
                return false;
            }
            driven = true;
            // The server needs the posed bones for ground-level hit volumes even without a camera.
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            animator.ResetTrigger("Attack");
            animator.ResetTrigger("Hit");
            animator.SetBool("Dead", false);
            animator.SetFloat("Speed", 0f);
            animator.speed = 0f;
            float progress = dormant ? 0f : Mathf.Clamp01(elapsed / EnemyBrain.WakeSeconds);
            animator.Play("Death", 0, .99f * (1f - Mathf.SmoothStep(0f, 1f, progress)));
            animator.Update(0f);
            return true;
        }

        public static void FinishDead(Animator animator, ref bool driven)
        {
            if (!driven || animator == null) return;
            animator.Play("Death", 0, .99f);
            animator.Update(0f);
            animator.speed = 0f;
            driven = false;
        }
    }
}
