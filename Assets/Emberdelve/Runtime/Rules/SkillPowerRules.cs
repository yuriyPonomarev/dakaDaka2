using System;

namespace Emberdelve.Stats
{
    /// <summary>Beneficial active-skill magnitude/duration only; no cost, cooldown or ordinary attack scaling.</summary>
    public static class SkillPowerRules
    {
        public const float MinimumFactor = .6f;
        public const float MaximumFactor = 1.4f;
        public static float SanitizeFactor(float factor)
        {
            if (float.IsNaN(factor) || float.IsInfinity(factor) || factor <= 0f) return 1f;
            return Math.Max(MinimumFactor, Math.Min(MaximumFactor, factor));
        }
    }
    public static class SkillPowerCurveDefaults
    {
        public static PiecewiseLinearCurve Curve { get; } = new PiecewiseLinearCurve(CreatePoints());
        public static CurvePoint[] CreatePoints() => new[]
        {
            new CurvePoint(1f, .70f), new CurvePoint(6f, .90f), new CurvePoint(10f, 1f),
            new CurvePoint(16f, 1.12f), new CurvePoint(22f, 1.24f), new CurvePoint(30f, 1.40f)
        };
    }
}
