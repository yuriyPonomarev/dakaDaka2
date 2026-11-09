using System;

namespace Emberdelve.Net.Rules
{
    /// <summary>Per-player pickup intent admission: strictly ordered, bounded and rate limited.</summary>
    public sealed class FloorLootIntentGate
    {
        public const uint MaxSequence = uint.MaxValue - 64;
        public const double IntervalSeconds = 0.1;
        private uint _lastSequence;
        private double _lastAcceptedAt = double.NegativeInfinity;

        public uint LastSequence => _lastSequence;

        public bool TryAccept(uint sequence, double now, out string reason)
        {
            if (sequence == 0 || sequence > MaxSequence || double.IsNaN(now) || double.IsInfinity(now) || now < 0 || now > 1e9)
            { reason = "invalid pickup sequence or clock"; return false; }
            if (_lastSequence >= MaxSequence || sequence != _lastSequence + 1)
            { reason = "stale or skipped pickup sequence"; return false; }

            // Consume an ordered sequence even when rate limited, preventing one packet from being retried repeatedly.
            _lastSequence = sequence;
            if (now - _lastAcceptedAt < IntervalSeconds)
            { reason = "pickup request rate limited"; return false; }
            _lastAcceptedAt = now;
            reason = null;
            return true;
        }
    }
}
