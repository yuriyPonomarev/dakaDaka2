namespace Emberdelve.Net.Rules
{
    /// <summary>
    /// U3A4b: fixed-capacity ring of the owning client's predicted frames, oldest first. One entry per frame the client sent and
    /// predicted; <see cref="IndexOf"/> finds the entry an Ack names, <see cref="DiscardThrough"/> drops what the server has
    /// processed, <see cref="TranslateAfter"/> shifts the still-unacked entries by a correction. A full ring overwrites its oldest
    /// entry (counted), so an Ack that never arrives cannot grow memory.
    /// </summary>
    public sealed class PredictionRing
    {
        public const int DefaultCapacity = 128;

        private readonly PredictionEntry[] _items;
        private int _head;
        private int _count;

        public PredictionRing() : this(DefaultCapacity) { }

        public PredictionRing(int capacity)
        {
            _items = new PredictionEntry[capacity < 1 ? 1 : capacity];
        }

        public int Capacity => _items.Length;
        public int Count => _count;
        /// <summary>Entries lost because the ring was full.</summary>
        public int Overwritten { get; private set; }

        /// <summary>Entry <paramref name="index"/>, 0 = oldest.</summary>
        public PredictionEntry this[int index] => _items[(_head + index) % _items.Length];

        public void Push(in PredictionEntry entry)
        {
            if (_count == _items.Length)
            {
                _head = (_head + 1) % _items.Length;
                _count--;
                Overwritten++;
            }

            _items[(_head + _count) % _items.Length] = entry;
            _count++;
        }

        public void Set(int index, in PredictionEntry entry)
        {
            if (index < 0 || index >= _count) return;
            _items[(_head + index) % _items.Length] = entry;
        }

        /// <summary>Index of the entry with this sequence number (newest match first), or -1.</summary>
        public int IndexOf(uint seq)
        {
            for (int i = _count - 1; i >= 0; i--)
                if (_items[(_head + i) % _items.Length].Seq == seq) return i;
            return -1;
        }

        /// <summary>Drops entry <paramref name="index"/> and every older one.</summary>
        public void DiscardThrough(int index)
        {
            if (index < 0) return;
            int drop = index + 1;
            if (drop > _count) drop = _count;
            _head = (_head + drop) % _items.Length;
            _count -= drop;
        }

        /// <summary>Adds an offset to the position of every entry newer than <paramref name="index"/>.</summary>
        public void TranslateAfter(int index, float dx, float dy, float dz)
        {
            for (int i = index + 1; i < _count; i++)
            {
                int slot = (_head + i) % _items.Length;
                PredictionEntry e = _items[slot];
                e.State.X += dx;
                e.State.Y += dy;
                e.State.Z += dz;
                _items[slot] = e;
            }
        }

        public void Clear()
        {
            _head = 0;
            _count = 0;
        }
    }
}
