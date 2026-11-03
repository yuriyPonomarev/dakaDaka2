using System;

namespace Emberdelve.Net.Rules
{
    public enum SkillIntentVerdict { Accepted, Invalid, Replay, Flood }

    /// <summary>
    /// Per-connection admission for reliable skill intents. This is only a wire-input gate:
    /// caller must first validate connection/object ownership and then let the authoritative
    /// skill caster decide whether a valid request can actually be performed.
    ///
    /// U3B2 contract (the local review's F1..F8):
    ///  - ONE sequence per connection, shared by both slots (1, 2, 3, ... whatever slot each request names). A per-slot numbering
    ///    makes the second key's presses look like replays.
    ///  - The channel is reliable and ordered, so a benign resend or an out-of-order arrival cannot happen: a sequence that is not
    ///    newer than the last one is a Replay by definition (a buggy or hostile client), and there is deliberately NO Redundant
    ///    verdict (the movement sanitizer needs one only because its channel is unreliable and its batches repeat frames).
    ///  - Invalid never moves the sequence; a Flood verdict consumes it (the next honest request stays inside the forward window).
    ///  - `now` is the SERVER's monotonic clock (Time.realtimeSinceStartupAsDouble in the server process), never request data. It
    ///    must be finite, non-negative and below <see cref="MaxClockSeconds"/> or the request is Invalid (F1: one absurd stamp can no
    ///    longer pin the refill clock). A gate lives for one admitted connection and is discarded with it; a returning slot gets a
    ///    new gate, and a remembered abusive slot gets one built with <see cref="SkillIntentGate(double,double)"/> (probation).
    ///  - A sequence above <see cref="SequenceCeiling"/> is Invalid and <see cref="Exhausted"/> turns true once the connection has
    ///    used the space up; the server then ends the connection (the client reconnects with a new gate). Not reachable in a
    ///    session by honest play (4 per second would take decades) but a flood consumes up to 64 per request.
    ///  - Counters per verdict give the seam its observability (F4); the gate stays free of locks (single server thread).
    /// </summary>
    public sealed class SkillIntentGate
    {
        public const uint MaxSequenceJump = 64;
        public const uint SequenceCeiling = uint.MaxValue - MaxSequenceJump;
        public const double RequestsPerSecond = 4.0;
        public const double Burst = 4.0;
        /// <summary>Largest clock value accepted (about 31 years of server uptime).</summary>
        public const double MaxClockSeconds = 1.0e9;
        private uint _lastSequence;
        private double _tokens = Burst;
        private double _lastTime;
        private bool _clockStarted;

        public SkillIntentGate()
        {
        }

        /// <summary>
        /// Probation: the gate starts with <paramref name="startTokens"/> (clamped to 0..Burst) at server time
        /// <paramref name="startTime"/>, so tokens refill from the moment of admission instead of from the first request and a
        /// remembered abusive slot does not start with a full burst.
        /// </summary>
        public SkillIntentGate(double startTokens, double startTime)
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

        // Time comes from the server's monotonic clock, never the request. A gate belongs
        // to one admitted connection and is discarded when that connection is removed.
        public SkillIntentVerdict Submit(int slot, uint sequence, double now)
        {
            SkillIntentVerdict verdict = Evaluate(slot, sequence, now);
            switch (verdict)
            {
                case SkillIntentVerdict.Accepted: Accepted++; break;
                case SkillIntentVerdict.Invalid: Invalid++; break;
                case SkillIntentVerdict.Replay: Replay++; break;
                default: Flood++; break;
            }

            return verdict;
        }

        private SkillIntentVerdict Evaluate(int slot, uint sequence, double now)
        {
            if (slot < 0 || slot >= SkillNetLimits.ActiveSlots || sequence == 0 || sequence > SequenceCeiling ||
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

            // A well-formed flooded command still consumes its sequence so the next honest
            // command can recover without exceeding the forward window.
            _lastSequence = sequence;
            if (_tokens < 1.0) return SkillIntentVerdict.Flood;
            _tokens -= 1.0;
            return SkillIntentVerdict.Accepted;
        }
    }
}
