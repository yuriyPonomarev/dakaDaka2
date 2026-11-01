using System;

namespace Emberdelve.Stats
{
    /// <summary>Immutable accepted action timing; all durations are seconds.</summary>
    public readonly struct MeleeActionTiming
    {
        public readonly float Factor, Windup, Active, Recovery, WallRecoil, RepeatInterval;
        public float Total => Windup + Active + Recovery;
        internal MeleeActionTiming(float factor, float windup, float active, float recovery, float wallRecoil, float repeat)
        {
            Factor = factor; Windup = windup; Active = active; Recovery = recovery;
            WallRecoil = wallRecoil; RepeatInterval = repeat;
        }
    }

    /// <summary>Engine-free DEX action timing math. The registered actor supplies the factor, never an intent.</summary>
    public static class ActionSpeedRules
    {
        public static float SanitizeFactor(float factor) =>
            float.IsNaN(factor) || float.IsInfinity(factor) || factor <= 0f ? 1f : factor;

        /// <summary>D4b2: one accepted reload duration. Draw and skill timings are deliberately unrelated.</summary>
        public static float ReloadDuration(float authoredSeconds, float actionFactor)
        {
            float authored = Finite(authoredSeconds) ? Math.Max(0f, authoredSeconds) : .1f;
            float duration = authored / SanitizeFactor(actionFactor);
            // Preserve finite positive authoring unless its arithmetic cannot be represented safely.
            if (!Finite(duration)) duration = authored;
            return Math.Max(.1f, duration);
        }

        /// <summary>D4b3 LOCAL ordinary draw duration. Server-clock begin/cancel authority remains U3B work.</summary>
        public static float DrawDuration(float authoredSeconds, float actionFactor)
        {
            float authored = Finite(authoredSeconds) ? Math.Max(0f, authoredSeconds) : .05f;
            float duration = authored / SanitizeFactor(actionFactor);
            if (!Finite(duration)) duration = authored;
            return Math.Max(.05f, duration);
        }

        public static MeleeActionTiming Resolve(float windup, float active, float recovery, float wallRecoil,
            float weaponCooldown, float actionFactor, float cooldownGrace)
        {
            float factor = SanitizeFactor(actionFactor);
            MeleeActionTiming timing = Calculate(windup, active, recovery, wallRecoil, weaponCooldown, factor, cooldownGrace);
            // A finite positive authored factor remains tunable, unless its arithmetic cannot fit finite seconds.
            if (Finite(timing.Windup) && Finite(timing.Active) && Finite(timing.Recovery) && Finite(timing.WallRecoil)
                && Finite(timing.Total) && Finite(timing.RepeatInterval)) return timing;
            return Calculate(windup, active, recovery, wallRecoil, weaponCooldown, 1f, cooldownGrace);
        }

        private static MeleeActionTiming Calculate(float windup, float active, float recovery, float wallRecoil,
            float cooldown, float factor, float grace)
        {
            float w = Math.Max(0f, windup / factor);
            float a = Math.Max(.02f, active / factor);
            float r = Math.Max(0f, recovery / factor);
            float recoil = Math.Max(0f, wallRecoil / factor);
            // Preserve the existing .1-second cooldown floor followed by the authority's grace policy.
            float repeat = Math.Max(Math.Max(.1f, cooldown / factor) * grace, w + a + r);
            return new MeleeActionTiming(factor, w, a, r, recoil, repeat);
        }
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }

    public static class ActionSpeedCurveDefaults
    {
        public static PiecewiseLinearCurve Curve { get; } = new PiecewiseLinearCurve(CreatePoints());
        public static CurvePoint[] CreatePoints() => new[]
        {
            new CurvePoint(1f, .82f), new CurvePoint(6f, .92f), new CurvePoint(10f, 1f),
            new CurvePoint(16f, 1.12f), new CurvePoint(22f, 1.20f), new CurvePoint(30f, 1.28f)
        };
    }
}
