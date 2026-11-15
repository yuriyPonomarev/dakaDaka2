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
        Use = 5,
        /// <summary>
        /// D3b: exchange the two ring slots as one step. DestinationSlot is the ring slot that receives the ring from its
        /// partner (the other ring slot); ExpectedSourceGearUid is the uid worn in the PARTNER slot and ExpectedTargetGearUid
        /// the uid worn in DestinationSlot (Guid.Empty for an empty slot). Appended after Use: the integers are stable.
        /// </summary>
        SwapEquipment = 6,
        /// <summary>
        /// D3c: switch between the two weapon sets (main hand + off hand each). Carries no stack, cell or slot: the only inputs
        /// are the preconditions, ExpectedSourceGearUid = the uid worn in the main hand of the set that is in hand now and
        /// ExpectedTargetGearUid = the uid worn in the main hand of the set it will switch to (Guid.Empty = the class asset's
        /// weapon is in that hand). Appended after SwapEquipment: the integers are stable.
        /// </summary>
        SwapWeaponSet = 7,
        /// <summary>
        /// U5A: lay an item on the ground. Either a bag item (SourceStackId = its stack, DestinationSlot None) or a worn item (SourceStackId 0,
        /// DestinationSlot = the slot it is worn in); ExpectedSourceGearUid is the uid that must be there (never empty: only gear instances can be
        /// dropped). The server checks the identity, removes the item from the model and spawns the ground item; one uid is in exactly one place.
        /// Appended after SwapWeaponSet: the integers are stable.
        /// </summary>
        Drop = 8,
        HoldQuickItem = 9,
        SelectWeaponSet = 10,
        // Definition-only bag stacks. Quantity is resolved from authoritative state, never supplied by the client.
        DropStack = 11,
        DropOne = 12,
        Split = 13
    }

    /// <summary>A client intent containing identities and preconditions, never item data or effect amounts.</summary>
    public readonly struct InventoryCommand
    {
        public readonly InventoryCommandKind Kind;
        public readonly int Amount;
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
            EquipmentSlot destinationSlot, Guid expectedSourceGearUid, Guid expectedTargetGearUid, int amount = 0)
        {
            Kind = kind;
            Amount = amount;
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

        public bool IsStructurallyValid(out string reason)
        {
            if (OwnerPlayerId == GameEvents.NoPlayer || Sequence == 0)
            {
                reason = "inventory command has no owner or sequence";
                return false;
            }

            if (Kind != InventoryCommandKind.Split && Amount != 0)
            { reason = "amount is only valid for a split command"; return false; }

            bool cellIsZero = DestinationCell == Vector2Int.zero;
            bool slotIsNone = DestinationSlot == EquipmentSlot.None;
            switch (Kind)
            {
                case InventoryCommandKind.Split:
                    if (Amount > 0 && SourceStackId != 0 && TargetStackId == 0 &&
                        DestinationCell.x >= 0 && DestinationCell.y >= 0 && slotIsNone &&
                        ExpectedSourceGearUid == Guid.Empty && ExpectedTargetGearUid == Guid.Empty)
                    { reason = null; return true; }
                    break;
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
                case InventoryCommandKind.SwapEquipment:
                    if (SourceStackId == 0 && TargetStackId == 0 && cellIsZero && EquipmentSlots.IsRing(DestinationSlot))
                    {
                        reason = null;
                        return true;
                    }
                    break;
                case InventoryCommandKind.SwapWeaponSet:
                    if (SourceStackId == 0 && TargetStackId == 0 && cellIsZero && slotIsNone)
                    {
                        reason = null;
                        return true;
                    }
                    break;
                case InventoryCommandKind.SelectWeaponSet:
                    if (SourceStackId == 0 && TargetStackId == 0 && slotIsNone && DestinationCell.y == 0 &&
                        DestinationCell.x >= 0 && DestinationCell.x < 2 && ExpectedSourceGearUid == Guid.Empty && ExpectedTargetGearUid == Guid.Empty)
                    { reason = null; return true; }
                    break;
                case InventoryCommandKind.Drop:
                    if (TargetStackId == 0 && cellIsZero && ExpectedSourceGearUid != Guid.Empty && ExpectedTargetGearUid == Guid.Empty &&
                        ((SourceStackId != 0 && slotIsNone) || (SourceStackId == 0 && IsEquipmentSlot(DestinationSlot))))
                    {
                        reason = null;
                        return true;
                    }
                    break;
                case InventoryCommandKind.DropStack:
                case InventoryCommandKind.DropOne:
                    if (SourceStackId != 0 && TargetStackId == 0 && cellIsZero && slotIsNone &&
                        ExpectedSourceGearUid == Guid.Empty && ExpectedTargetGearUid == Guid.Empty)
                    { reason = null; return true; }
                    break;
                case InventoryCommandKind.HoldQuickItem:
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
            Guid expectedTargetGearUid, out InventoryCommand command, out string reason, int amount = 0);

        InventoryCommandResult Submit(PlayerRig requester, InventoryCommand command);
    }
}
