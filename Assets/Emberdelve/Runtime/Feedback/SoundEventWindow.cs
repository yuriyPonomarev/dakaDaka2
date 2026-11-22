using System.Collections.Generic;

namespace Emberdelve.Feedback
{
    /// <summary>Bounded presentation deduplication, independent of blood/damage eligibility.</summary>
    public sealed class SoundEventWindow
    {
        private readonly HashSet<ulong> _seen = new HashSet<ulong>();
        private readonly Queue<ulong> _order = new Queue<ulong>();
        public bool Accept(ulong id)
        {
            if (id == 0) return true; // Legacy unnumbered presentation is not a shared identity.
            if (!_seen.Add(id)) return false;
            _order.Enqueue(id);
            if (_order.Count > 256) _seen.Remove(_order.Dequeue());
            return true;
        }
        public void Clear() { _seen.Clear(); _order.Clear(); }
    }
}
