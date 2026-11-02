using System;

namespace Emberdelve.Stats
{
    /// <summary>Pure local chest-hold timing. It does not authorize a server interaction.</summary>
    public static class InteractionSpeedRules
    {
        public static float Duration(float authoredSeconds, float speedFactor)
        {
            if (float.IsNaN(authoredSeconds) || float.IsInfinity(authoredSeconds)) return .1f;
            if (authoredSeconds <= 0f) return 0f;
            float duration = authoredSeconds / ActionSpeedRules.SanitizeFactor(speedFactor);
            if (float.IsNaN(duration) || float.IsInfinity(duration)) duration = authoredSeconds;
            return Math.Max(.1f, duration);
        }
    }

    public static class InteractionCurveDefaults
    {
        public static PiecewiseLinearCurve Curve { get; } = new PiecewiseLinearCurve(CreatePoints());
        public static CurvePoint[] CreatePoints() => new[]
        {
            new CurvePoint(1f, .75f), new CurvePoint(6f, .90f), new CurvePoint(10f, 1f),
            new CurvePoint(16f, 1.12f), new CurvePoint(22f, 1.20f), new CurvePoint(30f, 1.28f)
        };
    }
}
