using System;

namespace Emberdelve.Net.Rules
{
    /// <summary>Per-connection reliable inventory channel admission, evaluated after ownership validation.</summary>
    public sealed class InventoryIntentGate
    {
        public const ulong MaxSequenceJump = 64;
        public const ulong SequenceCeiling = ulong.MaxValue - MaxSequenceJump;
        public const double Burst = 10;
        public const double RequestsPerSecond = 10;
        private ulong _last;
        private double _tokens = Burst;
        private double _time;
        private bool _started;
        public ulong LastSequence => _last;
        public bool Exhausted => _last >= SequenceCeiling;

        public SkillIntentVerdict Submit(ulong sequence, double now)
        {
            if (sequence == 0 || sequence > SequenceCeiling || double.IsNaN(now) || double.IsInfinity(now) || now < 0 || now > 1e9)
                return SkillIntentVerdict.Invalid;
            if (sequence <= _last) return SkillIntentVerdict.Replay;
            if (sequence - _last > MaxSequenceJump) return SkillIntentVerdict.Invalid;
            if (!_started) { _started = true; _time = now; }
            else if (now > _time) { _tokens = Math.Min(Burst, _tokens + (now - _time) * RequestsPerSecond); _time = now; }
            _last = sequence;
            if (_tokens < 1) return SkillIntentVerdict.Flood;
            _tokens -= 1;
            return SkillIntentVerdict.Accepted;
        }
    }
}
