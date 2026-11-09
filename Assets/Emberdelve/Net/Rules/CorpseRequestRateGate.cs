using System;

namespace Emberdelve.Net.Rules
{
    /// <summary>Per-client monotonic interval gate for corpse RPCs.</summary>
    public sealed class CorpseRequestRateGate
    {
        private readonly double _minimumIntervalSeconds;
        private bool _hasAccepted;
        private double _lastAcceptedAt;

        public CorpseRequestRateGate(double minimumIntervalSeconds)
        {
            if (double.IsNaN(minimumIntervalSeconds) || double.IsInfinity(minimumIntervalSeconds) ||
                minimumIntervalSeconds <= 0.0)
                throw new ArgumentOutOfRangeException(nameof(minimumIntervalSeconds));
            _minimumIntervalSeconds = minimumIntervalSeconds;
        }

        public bool TryAccept(double now, out string reason)
        {
            if (double.IsNaN(now) || double.IsInfinity(now) || now < 0.0)
            {
                reason = "corpse request time is invalid";
                return false;
            }
            if (_hasAccepted && (now < _lastAcceptedAt || now - _lastAcceptedAt < _minimumIntervalSeconds))
            {
                reason = "corpse request rate limit exceeded";
                return false;
            }

            _hasAccepted = true;
            _lastAcceptedAt = now;
            reason = null;
            return true;
        }
    }
}
