using System;
using System.Collections.Generic;
using Emberdelve.Core;

namespace Emberdelve.Combat
{
    /// <summary>
    /// R1c1: WHO is sending a combat command, as the authority sees it. Plain data on purpose (one int), so a
    /// future server builds it from the network connection a packet arrived on and never from the packet body.
    /// <list type="bullet">
    /// <item><see cref="None"/> (default): no identity; every sender-bound command from it is refused.</item>
    /// <item><see cref="ForPlayer"/>: a player session; owns exactly the combat entities the host bound to it.</item>
    /// <item><see cref="TrustedHost"/>: the process that runs the world (AI, fixtures, scripted entities). It acts for
    /// entities NO player owns, and can never act for a player's entity.</item>
    /// </list>
    /// In the local process the player sender id is the player id the run director already admitted; U3A replaces
    /// the source (connection/session id) without changing the contract.
    /// </summary>
    public readonly struct CombatSenderId : IEquatable<CombatSenderId>
    {
        public readonly int Value;

        private CombatSenderId(int value) { Value = value; }

        public static CombatSenderId None => default;
        public static CombatSenderId TrustedHost => new CombatSenderId(-1);
        public static CombatSenderId ForPlayer(int playerId) => new CombatSenderId(playerId > 0 ? playerId : 0);

        public bool IsNone => Value == 0;
        public bool IsTrustedHost => Value == -1;
        public bool IsPlayer => Value > 0;

        public bool Equals(CombatSenderId other) => Value == other.Value;
        public override bool Equals(object obj) => obj is CombatSenderId other && Equals(other);
        public override int GetHashCode() => Value;
        public static bool operator ==(CombatSenderId a, CombatSenderId b) => a.Value == b.Value;
        public static bool operator !=(CombatSenderId a, CombatSenderId b) => a.Value != b.Value;

        public override string ToString() => IsNone ? "none" : IsTrustedHost ? "host" : "player#" + Value;
    }

    /// <summary>
    /// R1c1: which sender owns which combat entity. Written only by the trusted host (the run director binds a rig
    /// when it admits the player; the setters are internal), read by the authority for every sender-bound command.
    /// A process-wide table next to <see cref="CombatRegistry"/>, cleared at the same reset points, and for the same
    /// reason: one match per process until the U3 server owns one per match.
    /// </summary>
    public static class CombatSenders
    {
        private static readonly Dictionary<int, int> _ownerOfCombatId = new Dictionary<int, int>();

        /// <summary>Number of combat entities that currently have an owning sender.</summary>
        public static int BoundCount => _ownerOfCombatId.Count;

        internal static void Reset() => _ownerOfCombatId.Clear();

        /// <summary>
        /// Binds a player sender to the combat entity of that same player. The entity must be registered and must
        /// itself be that player's body (<see cref="IPlayerOwned.PlayerId"/> equals the sender id), and an entity
        /// that already has a different owner is never re-bound. Binding the same pair twice is a no-op success.
        /// </summary>
        internal static bool Bind(CombatSenderId sender, int combatId, out string reason)
        {
            reason = string.Empty;
            if (!sender.IsPlayer) { reason = "only a player sender can own a combat entity"; return false; }
            if (!CombatRegistry.TryGet(combatId, out ICombatEntity entity)) { reason = "unknown combat id " + combatId; return false; }
            if (!(entity is IPlayerOwned owned) || owned.PlayerId != sender.Value)
            {
                reason = "combat entity #" + combatId + " is not the body of " + sender;
                return false;
            }

            if (_ownerOfCombatId.TryGetValue(combatId, out int existing))
            {
                if (existing == sender.Value) return true;
                reason = "combat entity #" + combatId + " is already owned by " + CombatSenderId.ForPlayer(existing);
                return false;
            }

            _ownerOfCombatId[combatId] = sender.Value;
            return true;
        }

        internal static void Unbind(int combatId) => _ownerOfCombatId.Remove(combatId);

        public static bool TryGetOwner(int combatId, out CombatSenderId owner)
        {
            if (_ownerOfCombatId.TryGetValue(combatId, out int value))
            {
                owner = CombatSenderId.ForPlayer(value);
                return true;
            }

            owner = default;
            return false;
        }

        public static bool Owns(CombatSenderId sender, int combatId) =>
            sender.IsPlayer && TryGetOwner(combatId, out CombatSenderId owner) && owner == sender;

        /// <summary>
        /// The one ownership rule. An entity with an owner accepts commands from that owner only. An entity with no
        /// owner (enemies, fixtures) accepts the trusted host only. No sender and every other pairing are refused.
        /// </summary>
        public static bool Authorize(CombatSenderId sender, int combatId, out string reason)
        {
            reason = string.Empty;
            if (sender.IsNone)
            {
                reason = "command carries no sender identity";
                return false;
            }

            bool owned = TryGetOwner(combatId, out CombatSenderId owner);
            if (owned ? owner == sender : sender.IsTrustedHost) return true;

            reason = "sender " + sender + " does not own combat entity #" + combatId;
            return false;
        }
    }
}
