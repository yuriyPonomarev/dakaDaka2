using UnityEngine;

namespace Emberdelve.AI
{
    /// <summary>
    /// Speed-matched locomotion for every animated body (monsters on the server, monster proxies on clients, other players and rival bots).
    /// The imported Quaternius loops play in place, so a body moved by gameplay at a speed its loop was not authored for slides ("ice
    /// skating"). Here the loop's playback rate follows the measured ground speed, a backward move plays the loop in reverse, and a sideways
    /// move turns the body (not the aim) toward the motion, folded so the turn never exceeds <see cref="MaxBodyTurn"/>.
    /// Presentation only: nothing here moves anything or touches combat.
    /// </summary>
    public static class LocomotionGait
    {
        /// <summary>Animator float every locomotion state (Walk, Run) multiplies its playback by; negative plays the loop backward.</summary>
        public const string ScaleParameter = "LocoScale";

        // Fallback ground speeds (m/s) of the Quaternius walk / jog loops on the 1.8 m skeleton body when a model carries no measured
        // LocomotionStrides (every built body does: the strides depend on the body's leg length, so they are measured per model).
        public const float WalkStride = 0.8f;
        public const float JogStride = 4.2f;

        /// <summary>Ground speed (m/s) at which a body changes from its walk to its run loop.</summary>
        public const float RunFrom = 1.9f;

        /// <summary>Clamp of the playback rate (beyond it a loop looks wrong in either direction; a little slide is better).</summary>
        public const float MinRate = 0.5f, MaxRate = 2.4f;

        /// <summary>Largest turn (degrees) of the body toward a sideways move; the aim/facing itself is never changed.</summary>
        public const float MaxBodyTurn = 50f;

        /// <summary>Below this ground speed (m/s) the body is standing.</summary>
        public const float StandBelow = 0.15f;

        /// <summary>
        /// The playback for a body moving with <paramref name="planarVelocity"/> while facing <paramref name="facing"/>: the signed rate
        /// (negative = the loop backward, for a retreat) for a loop of <paramref name="stride"/> m/s scaled by <paramref name="bodyScale"/>, and the
        /// body's yaw offset (degrees, toward the motion, folded into +-90 so a backward move turns toward the reverse direction, then clamped).
        /// </summary>
        public static void Resolve(Vector3 planarVelocity, Vector3 facing, float stride, float bodyScale, out float rate, out float bodyTurn)
        {
            planarVelocity.y = 0f;
            facing.y = 0f;
            float speed = planarVelocity.magnitude;
            if (speed < StandBelow || facing.sqrMagnitude < 1e-6f) { rate = 1f; bodyTurn = 0f; return; }

            float angle = Vector3.SignedAngle(facing, planarVelocity, Vector3.up);   // 0 = forward, +-180 = backward
            bool backward = Mathf.Abs(angle) > 100f;
            float folded = backward ? angle - Mathf.Sign(angle) * 180f : angle;
            bodyTurn = Mathf.Clamp(folded, -MaxBodyTurn, MaxBodyTurn);

            float scaled = Mathf.Max(0.1f, stride) * Mathf.Max(0.1f, bodyScale);
            float magnitude = Mathf.Clamp(speed / scaled, MinRate, MaxRate);
            rate = backward ? -magnitude : magnitude;
        }
    }
}
