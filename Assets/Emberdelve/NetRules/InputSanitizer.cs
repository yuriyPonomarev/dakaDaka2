using System;

namespace Emberdelve.Net.Rules
{
    /// <summary>What the sanitizer decided about one submitted frame.</summary>
    public enum SanitizeVerdict
    {
        /// <summary>New, valid and within the rate budget: the cleaned frame is queued for the simulation.</summary>
        Accepted = 0,
        /// <summary>Not new: a redundancy copy (older slot of a batch) or a duplicate/reordered frame close behind the last accepted one. Ignored silently, not a counter.</summary>
        Redundant = 1,
        /// <summary>The newest frame of a batch is an old sequence number, further behind than redundancy explains.</summary>
        DroppedReplay = 2,
        /// <summary>Valid and new, but this sender is over its frame budget.</summary>
        DroppedFlood = 3,
        /// <summary>Malformed (see <see cref="InputSanitizer.LastInvalidReason"/>).</summary>
        DroppedInvalid = 4
    }

    public enum InvalidReason
    {
        None = 0,
        /// <summary>NaN or Infinity in an axis or angle.</summary>
        NonFinite = 1,
        /// <summary>|move| far above 1 (a little overshoot is clamped, not rejected).</summary>
        MoveMagnitude = 2,
        /// <summary>The sequence number jumped forward by more than the window (or the first one is above it).</summary>
        SeqWindow = 3,
        /// <summary>Sequence 0 is reserved for "no frame".</summary>
        SeqZero = 4,
        /// <summary>A flag bit outside the whitelist.</summary>
        Flags = 5
    }

    /// <summary>Numbers of the movement-input sanitizer (plan section 2.3).</summary>
    public static class MoveSanitizeLimits
    {
        /// <summary>A new frame may be at most this far ahead of the last accepted one.</summary>
        public const uint MaxSeqJump = 64u;
        /// <summary>A frame at most this far (exclusive) behind the last accepted one is redundancy / reordering, not a replay.</summary>
        public const uint RedundancyWindow = 4u;
        /// <summary>Above this the frame is rejected; between 1 and this it is scaled down to length 1 (a diagonal of two full axes is 1.414).</summary>
        public const float MaxMoveMagnitude = 1.5f;
        /// <summary>Overshoot up to this is quantization noise and is not counted as clamped.</summary>
        public const float MoveNoiseTolerance = 1.05f;
        /// <summary>Accepted new frames per second per sender: twice the tick rate (an honest client sends one per tick).</summary>
        public const float FramesPerSecond = 2f * NetTuning.TickRate;
    }

    /// <summary>
    /// Pure, per-sender gate between the wire and the movement simulation. The server owns one per player; nothing a client
    /// sends reaches PlayerMovement.Step without passing through <see cref="Submit"/>.
    ///
    /// Order of checks: finite numbers, flag whitelist, sequence (zero / not new / forward window), |move|, frame budget.
    /// A rejected frame leaves NO state behind except its counter, so a hostile frame cannot desynchronize the honest stream behind
    /// it. The one deliberate exception is a flood-dropped frame: it is otherwise valid, so it still consumes its sequence number
    /// (the content is lost, the stream stays aligned and an honest client recovers the moment its rate is back under the cap
    /// instead of running into the forward-jump window). Accepted frames come back cleaned: |move| at most 1,
    /// yaw wrapped into [-180, 180), pitch clamped. Time is injected (seconds, monotonic, any epoch) so tests and the tick-driven
    /// server are deterministic.
    ///
    /// Batches: the client resends its last three frames every tick. Submit the frames oldest to newest and pass
    /// <c>primary = true</c> only for the newest; an old frame in an older slot is recovered if it is new and silently
    /// ignored otherwise. Sequence numbers are 32 bit and not expected to wrap in a session (a year of 30 Hz).
    /// </summary>
    public sealed class InputSanitizer
    {
        private readonly float _minPitch;
        private readonly float _maxPitch;
        private readonly double _ratePerSecond;
        private readonly double _burst;

        private bool _hasSeq;
        private uint _lastSeq;
        private double _tokens;
        private double _lastRefill;
        private bool _bucketStarted;

        public int Accepted { get; private set; }
        public int Redundant { get; private set; }
        public int DroppedReplay { get; private set; }
        public int DroppedFlood { get; private set; }
        public int DroppedInvalid { get; private set; }
        /// <summary>Accepted frames whose |move| was scaled down to 1 (informational).</summary>
        public int Clamped { get; private set; }

        public int InvalidNonFinite { get; private set; }
        public int InvalidMagnitude { get; private set; }
        public int InvalidSeqWindow { get; private set; }
        public int InvalidSeqZero { get; private set; }
        public int InvalidFlags { get; private set; }
        public InvalidReason LastInvalidReason { get; private set; }

        public bool HasSeq => _hasSeq;
        public uint LastSeq => _lastSeq;

        public InputSanitizer()
            : this(MoveInputFrame.DefaultMinPitch, MoveInputFrame.DefaultMaxPitch,
                   MoveSanitizeLimits.FramesPerSecond, MoveSanitizeLimits.FramesPerSecond)
        {
        }

        /// <param name="ratePerSecond">Sustained accepted frames per second.</param>
        /// <param name="burst">Frames that may be accepted back to back before the rate applies.</param>
        public InputSanitizer(float minPitch, float maxPitch, double ratePerSecond, double burst)
        {
            _minPitch = minPitch;
            _maxPitch = maxPitch;
            _ratePerSecond = ratePerSecond < 0.0 ? 0.0 : ratePerSecond;
            _burst = burst < 1.0 ? 1.0 : burst;
        }

        /// <summary>Only the authority may open a new generation. Keep rate budget and lifetime rejection counters.</summary>
        public void ResetGeneration()
        {
            _hasSeq = false; _lastSeq = 0;
        }

        public SanitizeVerdict Submit(in MoveInputFrame frame, bool primary, double nowSeconds, out MoveInputFrame clean)
        {
            clean = default(MoveInputFrame);

            if (!MoveInputFrame.IsFinite(frame.MoveX) || !MoveInputFrame.IsFinite(frame.MoveY) ||
                !MoveInputFrame.IsFinite(frame.Yaw) || !MoveInputFrame.IsFinite(frame.Pitch))
                return Invalid(InvalidReason.NonFinite);

            if ((frame.Flags & ~MoveInputFrame.KnownFlags) != 0) return Invalid(InvalidReason.Flags);

            if (frame.Seq == 0u) return Invalid(InvalidReason.SeqZero);

            if (_hasSeq)
            {
                if (frame.Seq <= _lastSeq)
                {
                    uint behind = _lastSeq - frame.Seq;
                    if (!primary || behind < MoveSanitizeLimits.RedundancyWindow)
                    {
                        Redundant++;
                        return SanitizeVerdict.Redundant;
                    }

                    DroppedReplay++;
                    return SanitizeVerdict.DroppedReplay;
                }

                if (frame.Seq - _lastSeq > MoveSanitizeLimits.MaxSeqJump) return Invalid(InvalidReason.SeqWindow);
            }
            else if (frame.Seq > MoveSanitizeLimits.MaxSeqJump)
            {
                return Invalid(InvalidReason.SeqWindow);
            }

            float magnitudeSquared = frame.MoveX * frame.MoveX + frame.MoveY * frame.MoveY;
            if (!(magnitudeSquared <= MoveSanitizeLimits.MaxMoveMagnitude * MoveSanitizeLimits.MaxMoveMagnitude))
                return Invalid(InvalidReason.MoveMagnitude);

            if (!TakeToken(nowSeconds))
            {
                DroppedFlood++;
                _lastSeq = frame.Seq;
                _hasSeq = true;
                return SanitizeVerdict.DroppedFlood;
            }

            float moveX = frame.MoveX;
            float moveY = frame.MoveY;
            if (magnitudeSquared > 1f)
            {
                float magnitude = (float)Math.Sqrt(magnitudeSquared);
                moveX /= magnitude;
                moveY /= magnitude;
                if (magnitude > MoveSanitizeLimits.MoveNoiseTolerance) Clamped++;
            }

            clean = new MoveInputFrame
            {
                Seq = frame.Seq,
                MoveX = moveX,
                MoveY = moveY,
                Yaw = MoveInputFrame.WrapDegrees(frame.Yaw),
                Pitch = MoveInputFrame.ClampPitch(frame.Pitch, _minPitch, _maxPitch),
                Flags = frame.Flags
            };

            _lastSeq = frame.Seq;
            _hasSeq = true;
            Accepted++;
            return SanitizeVerdict.Accepted;
        }

        private SanitizeVerdict Invalid(InvalidReason reason)
        {
            DroppedInvalid++;
            LastInvalidReason = reason;
            switch (reason)
            {
                case InvalidReason.NonFinite: InvalidNonFinite++; break;
                case InvalidReason.MoveMagnitude: InvalidMagnitude++; break;
                case InvalidReason.SeqWindow: InvalidSeqWindow++; break;
                case InvalidReason.SeqZero: InvalidSeqZero++; break;
                case InvalidReason.Flags: InvalidFlags++; break;
            }

            return SanitizeVerdict.DroppedInvalid;
        }

        private bool TakeToken(double now)
        {
            if (!_bucketStarted)
            {
                _bucketStarted = true;
                _tokens = _burst;
                _lastRefill = now;
            }
            else if (now > _lastRefill)
            {
                _tokens = Math.Min(_burst, _tokens + (now - _lastRefill) * _ratePerSecond);
                _lastRefill = now;
            }

            if (_tokens < 1.0) return false;
            _tokens -= 1.0;
            return true;
        }
    }
}
