using System;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Player;
using UnityEngine;

namespace Emberdelve.Items
{
    public enum InventoryCommandKind
    {
        Move = 0,
        Swap = 1,
        Merge = 2,
        Equip = 3,
        Unequip = 4,
        Use = 5
    }

    /// <summary>A client intent containing identities and preconditions, never item data or effect amounts.</summary>
    public readonly struct InventoryCommand
    {
        public readonly InventoryCommandKind Kind;
        public readonly int OwnerPlayerId;
        public readonly ulong Sequence;
        public readonly ulong ExpectedRevision;
        public readonly ulong SourceStackId;
        public readonly ulong TargetStackId;
        public readonly Vector2Int DestinationCell;
        public readonly EquipmentSlot DestinationSlot;
        public readonly Guid ExpectedSourceGearUid;
        public readonly Guid ExpectedTargetGearUid;

        public InventoryCommand(InventoryCommandKind kind, int ownerPlayerId, ulong sequence,
            ulong expectedRevision, ulong sourceStackId, ulong targetStackId, Vector2Int destinationCell,
            EquipmentSlot destinationSlot, Guid expectedSourceGearUid, Guid expectedTargetGearUid)
        {
            Kind = kind;
            OwnerPlayerId = ownerPlayerId;
            Sequence = sequence;
            ExpectedRevision = expectedRevision;
            SourceStackId = sourceStackId;
            TargetStackId = targetStackId;
            DestinationCell = destinationCell;
            DestinationSlot = destinationSlot;
            ExpectedSourceGearUid = expectedSourceGearUid;
            ExpectedTargetGearUid = expectedTargetGearUid;
        }

        internal bool IsStructurallyValid(out string reason)
        {
            if (OwnerPlayerId == GameEvents.NoPlayer || Sequence == 0)
            {
                reason = "inventory command has no owner or sequence";
                return false;
            }

            bool cellIsZero = DestinationCell == Vector2Int.zero;
            bool slotIsNone = DestinationSlot == EquipmentSlot.None;
            switch (Kind)
            {
                case InventoryCommandKind.Move:
                    if (SourceStackId != 0 && TargetStackId == 0 && DestinationCell.x >= 0 && DestinationCell.y >= 0 &&
                        slotIsNone && ExpectedTargetGearUid == Guid.Empty)
                    {
                        reason = null;
                        return true;
                    }
                    break;
                case InventoryCommandKind.Swap:
                case InventoryCommandKind.Merge:
                    if (SourceStackId != 0 && TargetStackId != 0 && SourceStackId != TargetStackId &&
                        cellIsZero && slotIsNone)
                    {
                        reason = null;
                        return true;
                    }
                    break;
                case InventoryCommandKind.Equip:
                    if (SourceStackId != 0 && TargetStackId == 0 && cellIsZero &&
                        IsEquipmentSlot(DestinationSlot) && ExpectedSourceGearUid != Guid.Empty)
                    {
                        reason = null;
                        return true;
                    }
                    break;
                case InventoryCommandKind.Unequip:
                    if (SourceStackId == 0 && TargetStackId == 0 && cellIsZero &&
                        IsEquipmentSlot(DestinationSlot) && ExpectedSourceGearUid != Guid.Empty &&
                        ExpectedTargetGearUid == Guid.Empty)
                    {
                        reason = null;
                        return true;
                    }
                    break;
                case InventoryCommandKind.Use:
                    if (SourceStackId != 0 && TargetStackId == 0 && cellIsZero && slotIsNone &&
                        ExpectedTargetGearUid == Guid.Empty)
                    {
                        reason = null;
                        return true;
                    }
                    break;
            }

            reason = "inventory command fields do not match its kind";
            return false;
        }

        private static bool IsEquipmentSlot(EquipmentSlot slot) =>
            slot != EquipmentSlot.None && Enum.IsDefined(typeof(EquipmentSlot), slot);
    }

    public readonly struct InventoryCommandResult
    {
        public readonly bool Accepted;
        public readonly bool Changed;
        public readonly ulong Revision;
        public readonly string Reason;

        public InventoryCommandResult(bool accepted, bool changed, ulong revision, string reason)
        {
            Accepted = accepted;
            Changed = changed;
            Revision = revision;
            Reason = reason;
        }
    }

    public interface IInventoryAuthority
    {
        bool TryCreateLocalCommand(InventoryCommandKind kind, ulong sourceStackId, ulong targetStackId,
            Vector2Int destinationCell, EquipmentSlot destinationSlot, Guid expectedSourceGearUid,
            Guid expectedTargetGearUid, out InventoryCommand command, out string reason);

        InventoryCommandResult Submit(PlayerRig requester, InventoryCommand command);
    }
}
