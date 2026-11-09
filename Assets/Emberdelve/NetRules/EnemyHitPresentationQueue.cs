namespace Emberdelve.Net.Rules
{
    /// <summary>
    /// Retains authoritative hit edges until the enemy can show a flinch without covering its attack or death. Multiple hits while one
    /// attack owns the animator intentionally coalesce into one queued reaction; one Animator trigger cannot represent overlapping flinches.
    /// </summary>
    public sealed class EnemyHitPresentationQueue
    {
        private uint _lastSequence;
        private bool _initialized;
        private bool _pending;

        public bool HasPending => _pending;

        public void Discard() { _pending = false; }

        /// <summary>Seeds a joiner's baseline so historical hits do not replay as new flinches.</summary>
        public void Reset(uint sequence)
        {
            _lastSequence = sequence;
            _initialized = true;
            _pending = false;
        }

        public void Observe(uint sequence, bool dead)
        {
            if (!_initialized)
            {
                Reset(sequence);
                if (dead) _pending = false;
                return;
            }

            if (dead)
            {
                _pending = false;
                _lastSequence = sequence;
                return;
            }

            if (sequence != _lastSequence) _pending = true;
            _lastSequence = sequence;
        }

        /// <summary>Consumes one pending flinch only when no higher-priority animation currently owns the animator.</summary>
        public bool TryConsume(bool dead, bool windingUp, bool swingStarted, bool attackPlaybackActive)
        {
            if (dead)
            {
                _pending = false;
                return false;
            }

            if (!_pending || windingUp || swingStarted || attackPlaybackActive) return false;
            _pending = false;
            return true;
        }
    }
}
