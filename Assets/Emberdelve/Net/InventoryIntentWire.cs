using System;
using Emberdelve.Data;
using Emberdelve.Items;
using Unity.Netcode;
using UnityEngine;

namespace Emberdelve.Net
{
    /// <summary>Owner-only answer for an admitted inventory request. State synchronization is a separate step.</summary>
    public struct InventoryResultWire : INetworkSerializable
    {
        public ulong Sequence;
        public bool Accepted;
        public bool Changed;
        public ulong Revision;
        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref Sequence);
            serializer.SerializeValue(ref Accepted);
            serializer.SerializeValue(ref Changed);
            serializer.SerializeValue(ref Revision);
        }
    }

    /// <summary>
    /// Untrusted inventory intent. No player id or item data crosses this boundary; the receiver must derive
    /// playerId from the admitted transport connection and pass the result to the player's server authority.
    /// This type supplies a wire format only and does not itself admit or execute network requests.
    /// </summary>
    public struct InventoryIntentWire : INetworkSerializable
    {
        public int Kind;
        public int Amount;
        public ulong Sequence;
        public ulong ExpectedRevision;
        public ulong SourceStackId;
        public ulong TargetStackId;
        public int CellX;
        public int CellY;
        public int Slot;
        public ulong SourceUidLow;
        public ulong SourceUidHigh;
        public ulong TargetUidLow;
        public ulong TargetUidHigh;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref Kind);
            serializer.SerializeValue(ref Amount);
            serializer.SerializeValue(ref Sequence);
            serializer.SerializeValue(ref ExpectedRevision);
            serializer.SerializeValue(ref SourceStackId);
            serializer.SerializeValue(ref TargetStackId);
            serializer.SerializeValue(ref CellX);
            serializer.SerializeValue(ref CellY);
            serializer.SerializeValue(ref Slot);
            serializer.SerializeValue(ref SourceUidLow);
            serializer.SerializeValue(ref SourceUidHigh);
            serializer.SerializeValue(ref TargetUidLow);
            serializer.SerializeValue(ref TargetUidHigh);
        }

        /// <summary>Copy intent fields for sending; the local command's owner is intentionally omitted.</summary>
        public static InventoryIntentWire FromCommand(in InventoryCommand command)
        {
            var wire = new InventoryIntentWire
            {
                Kind = (int)command.Kind, Amount = command.Amount, Sequence = command.Sequence, ExpectedRevision = command.ExpectedRevision,
                SourceStackId = command.SourceStackId, TargetStackId = command.TargetStackId,
                CellX = command.DestinationCell.x, CellY = command.DestinationCell.y, Slot = (int)command.DestinationSlot
            };
            EncodeGuid(command.ExpectedSourceGearUid, out wire.SourceUidLow, out wire.SourceUidHigh);
            EncodeGuid(command.ExpectedTargetGearUid, out wire.TargetUidLow, out wire.TargetUidHigh);
            return wire;
        }

        /// <summary>Constructs a validated command using only the server-resolved owner, never a payload identity.</summary>
        public bool TryCreateCommand(int serverPlayerId, out InventoryCommand command, out string reason)
        {
            command = default;
            if (serverPlayerId <= 0)
            {
                reason = "connection has no admitted player";
                return false;
            }
            var candidate = new InventoryCommand((InventoryCommandKind)Kind, serverPlayerId, Sequence,
                ExpectedRevision, SourceStackId, TargetStackId, new Vector2Int(CellX, CellY), (EquipmentSlot)Slot,
                DecodeGuid(SourceUidLow, SourceUidHigh), DecodeGuid(TargetUidLow, TargetUidHigh), Amount);
            if (!candidate.IsStructurallyValid(out reason)) return false;
            command = candidate;
            return true;
        }

        private static void EncodeGuid(Guid guid, out ulong low, out ulong high)
        {
            byte[] bytes = guid.ToByteArray();
            low = 0; high = 0;
            for (int i = 0; i < 8; i++) { low |= (ulong)bytes[i] << (i * 8); high |= (ulong)bytes[i + 8] << (i * 8); }
        }

        private static Guid DecodeGuid(ulong low, ulong high)
        {
            byte[] bytes = new byte[16];
            for (int i = 0; i < 8; i++) { bytes[i] = (byte)(low >> (i * 8)); bytes[i + 8] = (byte)(high >> (i * 8)); }
            return new Guid(bytes);
        }
    }
}
