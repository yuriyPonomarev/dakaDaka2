using System;
using System.Collections.Generic;

namespace Emberdelve.Net.Rules
{
    /// <summary>Bounded, out-of-order-tolerant deduplication of server-issued blood events.</summary>
    public sealed class BloodHitWindow
    {
        private const ulong Window = 1024;
        private readonly HashSet<ulong> _seen = new HashSet<ulong>();
        private ulong _highest;

        public void Clear() { _seen.Clear(); _highest = 0; }

        public bool Accept(ulong id, bool enemy, float amount, float x, float y, float z)
        {
            if (id == 0 || !enemy || !float.IsFinite(amount) || amount <= 0f ||
                !float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z)) return false;
            if (_highest >= Window && id <= _highest - Window) return false;
            if (!_seen.Add(id)) return false;
            if (id > _highest)
            {
                _highest = id;
                if (_highest >= Window)
                {
                    ulong threshold = _highest - Window;
                    _seen.RemoveWhere(value => value <= threshold);
                }
            }
            return true;
        }
    }
}
