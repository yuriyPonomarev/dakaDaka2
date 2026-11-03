using System;

namespace Emberdelve.Net.Rules
{
    /// <summary>
    /// U3B3: per-connection admission for the reliable, ordered weapon-combat intents (attack press, guard down/up, draw start/release,
    /// weapon swap). The same contract as <see cref="SkillIntentGate"/> (which stays untouched) with the combat numbers:
    ///  - ONE sequence per connection for this channel (1, 2, 3 ...), independent of the skill channel's. Both channels share the
    ///    connection's reject LEDGER (SkillRejectLedger), so abuse on one counts against the other.
    ///  - The channel is reliable and ordered, so a sequence that is not newer than the last one is a Replay (no Redundant verdict).
    ///  - Invalid never moves the sequence; a Flood verdict consumes it. A sequence above <see cref="SequenceCeiling"/> is Invalid and
    ///    <see cref="Exhausted"/> turns true once the space is used up.
    ///  - `now` is the SERVER's monotonic clock, never request data; it must be finite, non-negative and below <see cref="MaxClockSeconds"/>.
    ///  - A valid kind is 1..<see cref="CombatNetLimits.KindMax"/>.
    /// Budget: <see cref="Burst"/> requests at once, <see cref="RequestsPerSecond"/> sustained: enough for a guard toggle at the key-mash
    /// rate the client allows (<see cref="CombatNetLimits.ClientMinIntervalSeconds"/>), nowhere near a flood. Pure.
    /// </summary>
    public sealed class CombatIntentGate
    {
        public const uint MaxSequenceJump = 64;
        public const uint SequenceCeiling = uint.MaxValue - MaxSequenceJump;
        public const double RequestsPerSecond = 10.0;
        public const double Burst = 10.0;
        public const double MaxClockSeconds = 1.0e9;

        private uint _lastSequence;
        private double _tokens = Burst;
        private double _lastTime;
        private bool _clockStarted;

        public CombatIntentGate()
        {
        }

        /// <summary>Probation: starts with <paramref name="startTokens"/> (clamped to 0..Burst) at server time <paramref name="startTime"/>.</summary>
        public CombatIntentGate(double startTokens, double startTime)
        {
            if (double.IsNaN(startTokens) || double.IsInfinity(startTokens)) startTokens = Burst;
            _tokens = Math.Max(0.0, Math.Min(Burst, startTokens));
            if (!double.IsNaN(startTime) && !double.IsInfinity(startTime) && startTime >= 0.0 && startTime <= MaxClockSeconds)
            {
                _clockStarted = true;
                _lastTime = startTime;
            }
        }

        public uint LastSequence => _lastSequence;
        public bool Exhausted => _lastSequence >= SequenceCeiling;
        public double Tokens => _tokens;
        public int Accepted { get; private set; }
        public int Invalid { get; private set; }
        public int Replay { get; private set; }
        public int Flood { get; private set; }

        public SkillIntentVerdict Submit(int kind, uint sequence, double now)
        {
            SkillIntentVerdict verdict = Evaluate(kind, sequence, now);
            switch (verdict)
            {
                case SkillIntentVerdict.Accepted: Accepted++; break;
                case SkillIntentVerdict.Invalid: Invalid++; break;
                case SkillIntentVerdict.Replay: Replay++; break;
                default: Flood++; break;
            }

            return verdict;
        }

        private SkillIntentVerdict Evaluate(int kind, uint sequence, double now)
        {
            if (!CombatNetLimits.IsValidKind(kind) || sequence == 0 || sequence > SequenceCeiling ||
                double.IsNaN(now) || double.IsInfinity(now) || now < 0.0 || now > MaxClockSeconds)
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

            _lastSequence = sequence;
            if (_tokens < 1.0) return SkillIntentVerdict.Flood;
            _tokens -= 1.0;
            return SkillIntentVerdict.Accepted;
        }
    }
}
