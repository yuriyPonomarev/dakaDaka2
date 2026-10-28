namespace Emberdelve.Net.Rules
{
    /// <summary>Where the frame the server steps this tick came from.</summary>
    public enum MoveFrameSource
    {
        /// <summary>A frame the client sent (oldest queued one).</summary>
        Queued = 0,
        /// <summary>No frame arrived: the last move is repeated (edges cleared), for at most <see cref="ServerMoveInputBuffer.MaxRepeatTicks"/> ticks.</summary>
        Repeated = 1,
        /// <summary>Nothing for too long: no movement, view kept.</summary>
        Neutral = 2
    }

    /// <summary>
    /// Pure per-player queue between the sanitizer and the movement step. The server pops exactly ONE frame per player per
    /// network tick (<see cref="Next"/>); a missing frame repeats the last move for at most three ticks and then goes neutral, so
    /// a stalled or silent client never keeps running (no extrapolated runaway) and a fast sender never gets extra steps.
    ///
    /// Capacity is six frames. Beyond <see cref="TrimDepth"/> the oldest frame is dropped (its jump / crouch edges are folded
    /// into the next frame so a press is not lost), which also bounds the added latency after a burst.
    /// </summary>
    public sealed class ServerMoveInputBuffer
    {
        public const int Capacity = 6;
        public const int TrimDepth = 4;
        public const int MaxRepeatTicks = 3;

        private const byte EdgeMask = MoveInputFrame.FlagJump | MoveInputFrame.FlagCrouchToggle;

        private readonly MoveInputFrame[] _ring = new MoveInputFrame[Capacity];
        private int _head;
        private int _count;
        private bool _hasLast;
        private MoveInputFrame _last;
        private int _missing;

        public int Depth => _count;
        /// <summary>Frames discarded because the queue was too deep.</summary>
        public int Overflowed { get; private set; }
        public int RepeatedTicks { get; private set; }
        public int NeutralTicks { get; private set; }
        public int QueuedTicks { get; private set; }

        public void Push(in MoveInputFrame frame)
        {
            if (_count == Capacity) DropOldest();
            _ring[(_head + _count) % Capacity] = frame;
            _count++;
            while (_count > TrimDepth) DropOldest();
        }

        /// <summary>The frame to step this tick. Fallback view values are used only before any frame was ever stepped.</summary>
        public MoveFrameSource Next(float fallbackYaw, float fallbackPitch, out MoveInputFrame frame)
        {
            if (_count > 0)
            {
                frame = _ring[_head];
                _head = (_head + 1) % Capacity;
                _count--;
                _last = frame;
                _hasLast = true;
                _missing = 0;
                QueuedTicks++;
                return MoveFrameSource.Queued;
            }

            if (_hasLast && _missing < MaxRepeatTicks)
            {
                _missing++;
                RepeatedTicks++;
                frame = _last;
                frame.Flags = (byte)(frame.Flags & (MoveInputFrame.FlagSprint | MoveInputFrame.FlagGuardHeld));
                return MoveFrameSource.Repeated;
            }

            frame = default(MoveInputFrame);
            frame.Yaw = _hasLast ? _last.Yaw : fallbackYaw;
            frame.Pitch = _hasLast ? _last.Pitch : fallbackPitch;
            NeutralTicks++;
            return MoveFrameSource.Neutral;
        }

        public void Clear()
        {
            _head = 0;
            _count = 0;
            _hasLast = false;
            _missing = 0;
        }

        private void DropOldest()
        {
            MoveInputFrame dropped = _ring[_head];
            _head = (_head + 1) % Capacity;
            _count--;
            Overflowed++;
            if (_count > 0) _ring[_head].Flags |= (byte)(dropped.Flags & EdgeMask);
        }
    }
}
