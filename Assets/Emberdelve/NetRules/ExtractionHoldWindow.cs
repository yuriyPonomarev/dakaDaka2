using System;

namespace Emberdelve.Net.Rules
{
    public enum ExtractionHoldVerdict
    {
        Valid = 0,
        TooEarly = 1,
        Expired = 2,
        WrongSequence = 3,
        Moved = 4,
    }

    /// <summary>One-shot seal used immediately before the server commits extraction.</summary>
    public sealed class ExtractionHoldCommitLatch
    {
        private bool _sealed;
        public bool IsSealed => _sealed;

        public bool TrySeal()
        {
            if (_sealed) return false;
            _sealed = true;
            return true;
        }
    }

    /// <summary>Server-clock lease for one uninterrupted extraction hold.</summary>
    public readonly struct ExtractionHoldWindow
    {
        public const float MaxDurationSeconds = 120f;
        public const float MovementToleranceMeters = 0.05f;
        private const double MaxCompletionGraceSeconds = 2.0;

        public readonly uint Sequence;
        public readonly double StartedAt;
        public readonly float Duration;
        public readonly float StartX;
        public readonly float StartY;
        public readonly float StartZ;

        private ExtractionHoldWindow(uint sequence, double startedAt, float duration, float x, float y, float z)
        {
            Sequence = sequence;
            StartedAt = startedAt;
            Duration = duration;
            StartX = x;
            StartY = y;
            StartZ = z;
        }

        public static bool TryStart(uint sequence, double startedAt, float duration, float x, float y, float z,
            out ExtractionHoldWindow window)
        {
            window = default;
            if (sequence == 0 || double.IsNaN(startedAt) || double.IsInfinity(startedAt) ||
                !IsFinite(duration) || duration <= 0f || duration > MaxDurationSeconds ||
                !IsFinite(x) || !IsFinite(y) || !IsFinite(z)) return false;
            window = new ExtractionHoldWindow(sequence, startedAt, duration, x, y, z);
            return true;
        }

        public ExtractionHoldVerdict Evaluate(uint sequence, double now, float x, float y, float z)
        {
            if (sequence != Sequence) return ExtractionHoldVerdict.WrongSequence;
            if (double.IsNaN(now) || double.IsInfinity(now) || now < StartedAt ||
                now - StartedAt >= Duration + MaxCompletionGraceSeconds ||
                !IsFinite(x) || !IsFinite(y) || !IsFinite(z)) return ExtractionHoldVerdict.Expired;

            float dx = x - StartX;
            float dy = y - StartY;
            float dz = z - StartZ;
            float tolerance = MovementToleranceMeters;
            if (dx * dx + dy * dy + dz * dz > tolerance * tolerance) return ExtractionHoldVerdict.Moved;
            return now - StartedAt < Duration ? ExtractionHoldVerdict.TooEarly : ExtractionHoldVerdict.Valid;
        }

        public static bool IsHeartbeatFresh(double lastHeartbeatAt, double now, double maxSilenceSeconds)
        {
            return !double.IsNaN(lastHeartbeatAt) && !double.IsInfinity(lastHeartbeatAt) &&
                !double.IsNaN(now) && !double.IsInfinity(now) && now >= lastHeartbeatAt &&
                !double.IsNaN(maxSilenceSeconds) && !double.IsInfinity(maxSilenceSeconds) &&
                maxSilenceSeconds > 0.0 && now - lastHeartbeatAt <= maxSilenceSeconds;
        }

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
