using System.Collections.Generic;
using Emberdelve.Core;
using UnityEngine;

namespace Emberdelve.Items
{
    /// <summary>
    /// Session-only companion to the persistence snapshot: authoritative bag stack identities and revision.
    /// It is not a save format. The sender captures it on the server and the receiving client validates both
    /// its loadout and exact position-to-id mapping before changing anything.
    /// </summary>
    public sealed class NetworkLoadoutState
    {
        public struct StackIdentity
        {
            public int X, Y;
            public ulong Id;
        }

        public LoadoutSnapshot Loadout;
        public ulong Revision;
        public ulong HeldStackId;
        public StackIdentity[] Stacks;

        public static NetworkLoadoutState Capture(Inventory bag, Equipment equipment)
        {
            if (bag == null || equipment == null) return null;
            var state = new NetworkLoadoutState { Loadout = LoadoutSnapshot.Capture(bag, equipment), Revision = bag.Revision, HeldStackId = bag.HeldStackId,
                Stacks = new StackIdentity[bag.Slots.Count] };
            for (int i = 0; i < state.Stacks.Length; i++)
            {
                InventorySlot slot = bag.Slots[i];
                state.Stacks[i] = new StackIdentity { X = slot.Origin.x, Y = slot.Origin.y, Id = slot.StackId };
            }
            return state;
        }

        public bool TryApply(Inventory bag, Equipment equipment, out string reason)
        {
            if (!NetRole.IsClientOnly) { reason = "network loadout is client-only"; return false; }
            if (bag == null || equipment == null || Loadout == null || Loadout.bag == null || Loadout.bag.entries == null || Stacks == null)
            { reason = "network loadout is incomplete"; return false; }
            if (!bag.CanApplyNetworkRevision(Revision)) { reason = "network loadout revision is stale"; return false; }
            if (Stacks.Length != Loadout.bag.entries.Length) { reason = "stack identity count mismatch"; return false; }

            var ids = new Dictionary<Vector2Int, ulong>();
            var uniqueIds = new HashSet<ulong>();
            for (int i = 0; i < Stacks.Length; i++)
            {
                StackIdentity stack = Stacks[i];
                var cell = new Vector2Int(stack.X, stack.Y);
                if (stack.X < 0 || stack.Y < 0 || stack.Id == 0 || !uniqueIds.Add(stack.Id) || ids.ContainsKey(cell))
                { reason = "invalid or duplicate stack identity"; return false; }
                ids.Add(cell, stack.Id);
            }
            foreach (ContainerEntry entry in Loadout.bag.entries)
                if (entry == null || !ids.ContainsKey(new Vector2Int(entry.x, entry.y)))
                { reason = "stack identity position mismatch"; return false; }

            if (HeldStackId != 0)
            {
                if (!uniqueIds.Contains(HeldStackId)) { reason = "held quick item identity is absent"; return false; }
                ContainerEntry heldEntry = null;
                foreach (var entry in Loadout.bag.entries)
                    if (ids[new Vector2Int(entry.x, entry.y)] == HeldStackId) { heldEntry = entry; break; }
                if (heldEntry?.item == null || bag.Catalog == null ||
                    !bag.Catalog.TryResolveItem(heldEntry.item.itemId, out var definition, out _) ||
                    !HotbarBindings.IsUsableDefinition(definition))
                { reason = "held item is not a quick item"; return false; }
            }

            var localBelt = bag.HasNetworkRevision ? bag.Hotbar.CaptureIdentities() : null;
            using (GameEventTransaction.Defer())
            {
                if (!LoadoutSnapshot.TryRestore(Loadout, bag, equipment, null, out reason)) return false;
                bag.ApplyNetworkIdentities(ids, Revision);
                bag.ApplyNetworkHeldStack(HeldStackId);
                if (localBelt != null) bag.Hotbar.RebindFromIdentities(localBelt, allowEmpty: true);
            }
            reason = null;
            return true;
        }
    }
}
