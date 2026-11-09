using System;

namespace Emberdelve.Net.Rules
{
    public enum ChestHoldVerdict
    {
        Valid = 0,
        TooEarly = 1,
        Expired = 2,
        WrongChest = 3,
        ChestChanged = 4,
    }

    /// <summary>Server-clock lease for one uninterrupted hold on one chest state.</summary>
    public readonly struct ChestHoldWindow
    {
        public const float MaxDurationSeconds = 10f;

        public readonly string ChestId;
        public readonly double StartedAt;
        public readonly float Duration;
        public readonly uint StateRevision;

        private ChestHoldWindow(string chestId, double startedAt, float duration, uint stateRevision)
        {
            ChestId = chestId;
            StartedAt = startedAt;
            Duration = duration;
            StateRevision = stateRevision;
        }

        public static bool TryStart(string chestId, double startedAt, float duration, uint stateRevision,
            out ChestHoldWindow window)
        {
            window = default;
            if (string.IsNullOrEmpty(chestId) || chestId.Length > 64 || double.IsNaN(startedAt) || double.IsInfinity(startedAt) ||
                float.IsNaN(duration) || float.IsInfinity(duration) || duration <= 0f || duration > MaxDurationSeconds) return false;
            window = new ChestHoldWindow(chestId, startedAt, duration, stateRevision);
            return true;
        }

        public ChestHoldVerdict Evaluate(string chestId, uint stateRevision, double now)
        {
            if (!string.Equals(ChestId, chestId, StringComparison.Ordinal)) return ChestHoldVerdict.WrongChest;
            if (StateRevision != stateRevision) return ChestHoldVerdict.ChestChanged;
            if (double.IsNaN(now) || double.IsInfinity(now) || now < StartedAt || now - StartedAt > Duration + 2.0)
                return ChestHoldVerdict.Expired;
            return now - StartedAt < Duration ? ChestHoldVerdict.TooEarly : ChestHoldVerdict.Valid;
        }

        public static bool IsHeartbeatFresh(double lastHeartbeatAt, double now, double maxSilenceSeconds)
        {
            return !double.IsNaN(lastHeartbeatAt) && !double.IsInfinity(lastHeartbeatAt) &&
                !double.IsNaN(now) && !double.IsInfinity(now) && now >= lastHeartbeatAt &&
                !double.IsNaN(maxSilenceSeconds) && !double.IsInfinity(maxSilenceSeconds) &&
                maxSilenceSeconds > 0.0 && now - lastHeartbeatAt <= maxSilenceSeconds;
        }
    }
}
