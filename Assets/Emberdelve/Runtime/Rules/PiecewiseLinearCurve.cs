using System;
using System.Collections.Generic;

namespace Emberdelve.Stats
{
    /// <summary>Serializable authoring point. A curve copies these values; it never retains its input list.</summary>
    [Serializable]
    public struct CurvePoint
    {
        public float x;
        public float value;
        public CurvePoint(float x, float value) { this.x = x; this.value = value; }
    }

    /// <summary>Immutable, engine-free linear interpolation with held endpoints.</summary>
    public sealed class PiecewiseLinearCurve
    {
        private readonly CurvePoint[] _points;
        public int Count => _points.Length;
        public CurvePoint GetPoint(int index) => _points[index];

        public PiecewiseLinearCurve(IReadOnlyList<CurvePoint> points)
        {
            if (points == null) throw new ArgumentNullException(nameof(points));
            if (points.Count < 2) throw new ArgumentException("A curve needs at least two points.", nameof(points));
            _points = new CurvePoint[points.Count];
            for (int i = 0; i < points.Count; i++)
            {
                CurvePoint point = points[i];
                if (!IsFinite(point.x) || !IsFinite(point.value))
                    throw new ArgumentException("Curve coordinates must be finite.", nameof(points));
                if (i > 0 && point.x <= _points[i - 1].x)
                    throw new ArgumentException("Curve x coordinates must strictly increase.", nameof(points));
                _points[i] = point;
            }
        }

        public static bool TryCreate(IReadOnlyList<CurvePoint> points,
            out PiecewiseLinearCurve curve, out string error)
        {
            try { curve = new PiecewiseLinearCurve(points); error = null; return true; }
            catch (ArgumentException exception) { curve = null; error = exception.Message; return false; }
        }

        /// <summary>NaN safely selects the first endpoint. Infinities select the corresponding endpoint.</summary>
        public float Evaluate(float x)
        {
            if (float.IsNaN(x) || x <= _points[0].x) return _points[0].value;
            int last = _points.Length - 1;
            if (x >= _points[last].x) return _points[last].value;
            for (int i = 1; i <= last; i++)
            {
                CurvePoint right = _points[i];
                if (x > right.x) continue;
                CurvePoint left = _points[i - 1];
                // Double intermediates avoid overflow even for finite float extrema.
                double t = ((double)x - left.x) / ((double)right.x - left.x);
                return (float)(left.value + ((double)right.value - left.value) * t);
            }
            return _points[last].value;
        }

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }

    /// <summary>The D4a fallback and fresh-asset authoring table. No mutable array is shared.</summary>
    public static class MovementCurveDefaults
    {
        public static PiecewiseLinearCurve Curve { get; } = new PiecewiseLinearCurve(CreatePoints());
        public static CurvePoint[] CreatePoints() => new[]
        {
            new CurvePoint(1f, .875f), new CurvePoint(6f, .950f),
            new CurvePoint(10f, 1f), new CurvePoint(16f, 1.075f),
            new CurvePoint(22f, 1.135f), new CurvePoint(30f, 1.175f)
        };
    }
}
