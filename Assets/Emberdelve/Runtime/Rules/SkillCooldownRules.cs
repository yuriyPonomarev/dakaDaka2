using System;
namespace Emberdelve.Stats
{
    /// <summary>Pure accepted skill cooldown duration, capped at60% reduction.</summary>
    public static class SkillCooldownRules
    {
        public const float MinimumMultiplier = .4f;
        public static float SanitizeMultiplier(float multiplier)
        {
            if (float.IsNaN(multiplier) || float.IsInfinity(multiplier) || multiplier <= 0f) return 1f;
            return Math.Max(MinimumMultiplier, multiplier);
        }
        public static float Duration(float authoredSeconds, float multiplier)
        {
            if (float.IsNaN(authoredSeconds) || float.IsInfinity(authoredSeconds) || authoredSeconds <= 0f) return 0f;
            float duration = authoredSeconds * SanitizeMultiplier(multiplier);
            return float.IsNaN(duration) || float.IsInfinity(duration) ? authoredSeconds : duration;
        }
    }
    public static class SkillCooldownCurveDefaults
    {
        public static PiecewiseLinearCurve Curve { get; } = new PiecewiseLinearCurve(CreatePoints());
        public static CurvePoint[] CreatePoints() => new[]
        {
            new CurvePoint(1, 1.20f), new CurvePoint(6, 1.06f), new CurvePoint(10, 1f),
            new CurvePoint(16, .88f), new CurvePoint(22, .70f), new CurvePoint(30, .40f)
        };
    }
}
