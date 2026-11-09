namespace Emberdelve.Net.Rules
{
    /// <summary>
    /// Pure client-side half of the movement stream. The client samples its input every RENDER frame
    /// (<see cref="Sample"/>) and turns the samples into exactly ONE <see cref="MoveInputFrame"/> per network tick
    /// (<see cref="Flush"/>): the move axes and sprint are the LAST held values, the jump / crouch edges are OR-ed over every
    /// sample since the previous tick (a press between two ticks is never lost, and never doubled), yaw and pitch are the
    /// current view. Each tick it also hands back the last <see cref="BatchSize"/> frames (<see cref="FillBatch"/>), which the
    /// sender puts in one unreliable message: a lost packet is covered by the next one, the server dedupes by sequence.
    ///
    /// Frames are stored in the form the server will see them (<see cref="MoveInputFrame.Quantized"/>), so a predicting client
    /// steps with the same numbers. Sequence numbers start at 1 and only ever increase.
    /// </summary>
    public sealed class MoveInputAggregator
    {
        public const int BatchSize = 3;

        private uint _nextSeq = 1u;
        private float _moveX;
        private float _moveY;
        private float _yaw;
        private float _pitch;
        private bool _sprint;
        private bool _jumpEdge;
        private bool _crouchEdge;
        private readonly MoveInputFrame[] _history = new MoveInputFrame[BatchSize];
        private int _historyCount;

        /// <summary>The sequence number of the most recent frame produced (0 before the first).</summary>
        public uint LastSeq => _nextSeq - 1u;

        /// <summary>Discard unsent input when a modal screen takes control, without resetting sequence history.</summary>
        public void ClearPendingInput()
        {
            _moveX = 0f; _moveY = 0f; _sprint = false;
            _jumpEdge = false; _crouchEdge = false;
            // Old frames are retransmitted with the same sequence number. Rewriting their payload
            // would make duplicates depend on arrival order, so discard the history and let the
            // next neutral frame advance the sequence normally.
            _historyCount = 0;
        }

        public void Sample(float moveX, float moveY, bool sprintHeld, bool jumpPressed, bool crouchPressed, float yaw, float pitch)
        {
            _moveX = moveX;
            _moveY = moveY;
            _sprint = sprintHeld;
            _yaw = yaw;
            _pitch = pitch;
            if (jumpPressed) _jumpEdge = true;
            if (crouchPressed) _crouchEdge = true;
        }

        /// <summary>Ends the tick: builds the frame from what was sampled since the last flush, clears the edges, records it for redundancy.</summary>
        public MoveInputFrame Flush()
        {
            MoveInputFrame frame = MoveInputFrame.Create(_nextSeq, _moveX, _moveY, _yaw, _pitch, _sprint, _jumpEdge, _crouchEdge).Quantized();
            _nextSeq++;
            _jumpEdge = false;
            _crouchEdge = false;

            if (_historyCount == BatchSize)
            {
                for (int i = 1; i < BatchSize; i++) _history[i - 1] = _history[i];
                _history[BatchSize - 1] = frame;
            }
            else
            {
                _history[_historyCount++] = frame;
            }

            return frame;
        }

        /// <summary>Copies the recent frames, oldest first, into <paramref name="destination"/> (length at least <see cref="BatchSize"/>); returns how many.</summary>
        public int FillBatch(MoveInputFrame[] destination)
        {
            for (int i = 0; i < _historyCount; i++) destination[i] = _history[i];
            return _historyCount;
        }
    }
}
