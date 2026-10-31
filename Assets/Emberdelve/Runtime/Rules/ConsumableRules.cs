using System;

namespace Emberdelve.Items
{
    /// <summary>
    /// D3c: the pure arithmetic of timed consumables (a drink channel, then a heal delivered over time). No scene access, no engine
    /// types: ConsumableChannel owns the state, this owns the numbers, so the numbers can be asserted without a rig.
    /// </summary>
    public static class ConsumableRules
    {
        /// <summary>
        /// A drink is interrupted when the drinker moves faster than this fraction of its walking speed
        /// (PlayerMovement.SpeedMultiplier is already speed / walk speed): standing, and a slow crouch-walk, are fine; walking
        /// at pace and sprinting are not.
        /// </summary>
        public const float MoveCancelRatio = 0.6f;

        public static bool IsMovingTooFast(float speedRatio)
        {
            return !float.IsNaN(speedRatio) && speedRatio > MoveCancelRatio;
        }

        /// <summary>Fraction of a channel done after <paramref name="elapsed"/> seconds of <paramref name="duration"/> (1 for a zero-length channel).</summary>
        public static float Progress(float elapsed, float duration)
        {
            if (!(duration > 0f)) return 1f;
            if (!(elapsed > 0f)) return 0f;
            return elapsed >= duration ? 1f : elapsed / duration;
        }

        /// <summary>
        /// How much of a heal of <paramref name="total"/> points has been delivered after <paramref name="elapsed"/> seconds of
        /// <paramref name="duration"/>: linear, and EXACTLY <paramref name="total"/> once the duration is reached (or for a zero
        /// duration), so a heal over time pays out its amount once in total - never more, never less.
        /// </summary>
        public static float HealDelivered(float total, float duration, float elapsed)
        {
            if (!(total > 0f)) return 0f;
            if (!(duration > 0f) || elapsed >= duration) return total;
            if (!(elapsed > 0f)) return 0f;
            return total * (elapsed / duration);
        }
    }
}
