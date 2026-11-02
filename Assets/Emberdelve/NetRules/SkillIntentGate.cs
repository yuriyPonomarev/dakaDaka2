using System;

namespace Emberdelve.Net.Rules
{
    public enum SkillIntentVerdict { Accepted, Invalid, Replay, Flood }

    /// <summary>
    /// Per-connection admission for reliable skill intents. This is only a wire-input gate:
    /// caller must first validate connection/object ownership and then let the authoritative
    /// skill caster decide whether a valid request can actually be performed.
    /// </summary>
    public sealed class SkillIntentGate
    {
        public const uint MaxSequenceJump = 64;
        public const double RequestsPerSecond = 4.0;
        public const double Burst = 4.0;
        private uint _lastSequence;
        private double _tokens = Burst;
        private double _lastTime;
        private bool _clockStarted;

        public uint LastSequence => _lastSequence;

        // Time comes from the server's monotonic clock, never the request. A gate belongs
        // to one admitted connection and is discarded when that connection is removed.
        public SkillIntentVerdict Submit(int slot, uint sequence, double now)
        {
            if (slot < 0 || slot >= 2 || sequence == 0 || double.IsNaN(now) || double.IsInfinity(now))
                return SkillIntentVerdict.Invalid;
            if (sequence <= _lastSequence) return SkillIntentVerdict.Replay;
            if (sequence - _lastSequence > MaxSequenceJump) return SkillIntentVerdict.Invalid;

            if (!_clockStarted)
            {
                _clockStarted = true;
                _lastTime = now;
            }
            else if (now > _lastTime)
            {
                _tokens = Math.Min(Burst, _tokens + (now - _lastTime) * RequestsPerSecond);
                _lastTime = now;
            }

            // A well-formed flooded command still consumes its sequence so the next honest
            // command can recover without exceeding the forward window.
            _lastSequence = sequence;
            if (_tokens < 1.0) return SkillIntentVerdict.Flood;
            _tokens -= 1.0;
            return SkillIntentVerdict.Accepted;
        }
    }
}
