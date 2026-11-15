using Emberdelve.Core;
using UnityEngine;

namespace Emberdelve.Interaction
{
    public sealed class DescentPortalSlot : MonoBehaviour, IInteractable
    {
        public DescentPortalGroup Group { get; private set; }
        public int Slot { get; private set; }
        public string PromptText => Group != null ? Group.PromptText : "Descending portal";
        public bool RequiresHold => false;
        public float HoldDuration => 0;
        public void Configure(DescentPortalGroup group, int slot)
        { Group = group; Slot = slot; group.RegisterSlot(slot, this); }
        public bool CanInteract(IInteractionActor actor, out string reason)
        {
            reason = "Portal slot is unavailable";
            return Group != null && !Group.SlotUsed(Slot) && Group.CanInteractAt(actor, this, out reason);
        }
        public void OnInteractInstant(IInteractionActor actor)
        {
            if (Group != null && !Group.TryEnter(actor, Slot, out string reason))
                GameEvents.RaiseToast(actor != null ? actor.PlayerId : -1, reason);
        }
        public void OnHoldComplete(IInteractionActor actor) => OnInteractInstant(actor);
    }
}
