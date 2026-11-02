using System;
using System.Collections.Generic;
using Emberdelve.Combat;
using Emberdelve.Player;
using Emberdelve.Net.Rules;
using Unity.Netcode;

namespace Emberdelve.Net
{
    /// <summary>One admitted connection as the server knows it.</summary>
    public sealed class NetIdentity
    {
        public ulong ClientId;
        /// <summary>Dev-scoped identity from the connect payload (NOT authentication; never a profile key).</summary>
        public string Slot;
        public string ClassId;
        /// <summary>The server-issued player id (= the rig's combat id).</summary>
        public int PlayerId;
        public int PartyId;
        public PlayerRig Rig;
        public NetworkObject Object;
#if DEVELOPMENT_BUILD || UNITY_EDITOR
        // One gate per admitted connection; the row is discarded on disconnect.
        internal readonly SkillIntentGate SkillIntents = new SkillIntentGate();
#endif
    }

    /// <summary>
    /// Server only. clientId (the transport connection) &lt;-&gt; devSlot &lt;-&gt; server PlayerId, in one place. The sender of any
    /// sender-bound command comes from <see cref="ResolveSender"/> and from nowhere else: no RPC parameter ever carries a player or
    /// combat id (plan section 2.2, NETWORK_SPIKE). An unknown or disconnected client resolves to CombatSenderId.None, which every
    /// sender-bound authority command already refuses. Plain data structure with no engine calls except the types it stores.
    /// </summary>
    public sealed class NetIdentityTable
    {
        /// <summary>The live table of this (server) process; null on a client or offline.</summary>
        public static NetIdentityTable Current { get; internal set; }

        private readonly Dictionary<ulong, NetIdentity> _byClient = new Dictionary<ulong, NetIdentity>();
        private readonly Dictionary<int, NetIdentity> _byPlayer = new Dictionary<int, NetIdentity>();

        public int Count => _byClient.Count;
        public IEnumerable<NetIdentity> All => _byClient.Values;

        /// <summary>Adds a row; false (nothing changed) when the client, the player id or the slot is already present.</summary>
        public bool Add(NetIdentity identity)
        {
            if (identity == null || identity.PlayerId <= 0) return false;
            if (_byClient.ContainsKey(identity.ClientId) || _byPlayer.ContainsKey(identity.PlayerId)) return false;
            if (IsSlotUsed(identity.Slot)) return false;

            _byClient[identity.ClientId] = identity;
            _byPlayer[identity.PlayerId] = identity;
            return true;
        }

        public bool TryGetByClient(ulong clientId, out NetIdentity identity) => _byClient.TryGetValue(clientId, out identity);
        public bool TryGetByPlayer(int playerId, out NetIdentity identity) => _byPlayer.TryGetValue(playerId, out identity);

        public bool IsSlotUsed(string slot)
        {
            if (string.IsNullOrEmpty(slot)) return false;
            foreach (NetIdentity row in _byClient.Values)
                if (string.Equals(row.Slot, slot, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public bool RemoveByClient(ulong clientId, out NetIdentity removed)
        {
            if (!_byClient.TryGetValue(clientId, out removed)) return false;
            _byClient.Remove(clientId);
            _byPlayer.Remove(removed.PlayerId);
            return true;
        }

        /// <summary>
        /// The command sender for an RPC: derived from the connection that delivered it (RpcParams.Receive.SenderClientId), never from
        /// a payload field. CombatSenderId.None when the connection has no admitted player.
        /// </summary>
        public CombatSenderId ResolveSender(ulong senderClientId)
        {
            return _byClient.TryGetValue(senderClientId, out NetIdentity row)
                ? CombatSenderId.ForPlayer(row.PlayerId)
                : CombatSenderId.None;
        }

        public void Clear()
        {
            _byClient.Clear();
            _byPlayer.Clear();
        }
    }
}
