using System;
using System.Collections.Generic;

namespace Emberdelve.Rules
{
    public enum RoyaleParticipantState { Alive, Eliminated, Escaped }
    public enum RoyalePartyMode { Solo = 1, Duo = 2, Trio = 3 }

    /// <summary>Authority-owned match identities. Disconnect is not death or a new admission.
    /// Last surviving party is information, not an automatic escape or reward.</summary>
    public sealed class RoyaleRoster
    {
        private sealed class Entry
        {
            public int Party;
            public bool Bot;
            public bool Connected = true;
            public RoyaleParticipantState State;
        }

        private readonly Dictionary<int, Entry> _entries = new Dictionary<int, Entry>();
        public RoyalePartyMode Mode { get; }
        public int PartySize => (int)Mode;
        public int Capacity => 16 / PartySize * PartySize;
        public int PartyCapacity => Capacity / PartySize;

        public RoyaleRoster(RoyalePartyMode mode = RoyalePartyMode.Solo)
        {
            if (mode < RoyalePartyMode.Solo || mode > RoyalePartyMode.Trio)
                throw new ArgumentOutOfRangeException(nameof(mode));
            Mode = mode;
        }

        public bool Started { get; private set; }
        public int Count => _entries.Count;

        public bool TryAdmit(int actor, int party, bool bot)
        {
            if (Started || actor < 0 || party < 0 || _entries.ContainsKey(actor) || Count >= Capacity) return false;
            int members = 0;
            var parties = new HashSet<int>();
            foreach (var entry in _entries.Values)
            {
                parties.Add(entry.Party);
                if (entry.Party == party) members++;
            }
            if (members >= PartySize || (members == 0 && parties.Count >= PartyCapacity)) return false;
            _entries.Add(actor, new Entry { Party = party, Bot = bot });
            return true;
        }

        public bool TryStart()
        {
            if (Started || AliveParties < 2) return false;
            Started = true;
            return true;
        }

        public int AliveParties
        {
            get
            {
                var parties = new HashSet<int>();
                foreach (var entry in _entries.Values)
                    if (entry.State == RoyaleParticipantState.Alive) parties.Add(entry.Party);
                return parties.Count;
            }
        }

        public bool TryGet(int actor, out int party, out bool bot, out bool connected,
            out RoyaleParticipantState state)
        {
            party = -1; bot = connected = false; state = default;
            if (!_entries.TryGetValue(actor, out var entry)) return false;
            party = entry.Party; bot = entry.Bot; connected = entry.Connected; state = entry.State;
            return true;
        }

        public bool SetConnected(int actor, bool connected)
        {
            if (!_entries.TryGetValue(actor, out var entry) || entry.Bot) return false;
            entry.Connected = connected;
            return true;
        }

        public bool TryEliminate(int actor) => TryFinish(actor, RoyaleParticipantState.Eliminated);

        // The caller must first validate the final-floor portal and commit item custody.
        public bool TryEscape(int actor) => TryFinish(actor, RoyaleParticipantState.Escaped);

        private bool TryFinish(int actor, RoyaleParticipantState state)
        {
            if (!Started || !_entries.TryGetValue(actor, out var entry)
                || entry.State != RoyaleParticipantState.Alive) return false;
            entry.State = state;
            return true;
        }

        public bool TryGetLastAliveParty(out int party)
        {
            party = -1;
            if (!Started) return false;
            foreach (var entry in _entries.Values)
            {
                if (entry.State != RoyaleParticipantState.Alive) continue;
                if (party >= 0 && party != entry.Party) { party = -1; return false; }
                party = entry.Party;
            }
            return party >= 0;
        }
    }
}
