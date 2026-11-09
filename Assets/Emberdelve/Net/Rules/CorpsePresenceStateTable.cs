using System.Collections.Generic;
using UnityEngine;

namespace Emberdelve.Net.Rules
{
    /// <summary>Client replica ledger that rejects stale presence updates and retains removal tombstones.</summary>
    public sealed class CorpsePresenceStateTable
    {
        public readonly struct State
        {
            public readonly ulong CorpseId;
            public readonly Vector3 Position;
            public readonly ulong Revision;
            public readonly bool Removed;

            public State(ulong corpseId, Vector3 position, ulong revision, bool removed)
            {
                CorpseId = corpseId;
                Position = position;
                Revision = revision;
                Removed = removed;
            }
        }

        private readonly Dictionary<ulong, State> _states = new Dictionary<ulong, State>();

        public bool TryApply(ulong corpseId, Vector3 position, ulong revision, bool removed, out State state)
        {
            state = default;
            if (corpseId == 0 || !IsFinite(position)) return false;
            if (_states.TryGetValue(corpseId, out State previous))
            {
                // IDs are unique for a server session. Once removed, a corpse ID must never be resurrected.
                if (previous.Removed || revision <= previous.Revision) return false;
            }

            state = new State(corpseId, position, revision, removed);
            _states[corpseId] = state;
            return true;
        }

        public bool TryGet(ulong corpseId, out State state) => _states.TryGetValue(corpseId, out state);

        public void Clear() => _states.Clear();

        private static bool IsFinite(Vector3 value) =>
            !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
            !float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
            !float.IsNaN(value.z) && !float.IsInfinity(value.z);
    }
}
