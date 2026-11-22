using Emberdelve.Core;
using UnityEngine;

namespace Emberdelve.Interaction
{
    public sealed class DescentPortalSlot : MonoBehaviour, IInteractable, IInteractableHoldLifecycle
    {
        public DescentPortalGroup Group { get; private set; }
        public int Slot { get; private set; }
        public string PromptText => Group != null ? Group.PromptText : "Descending portal";
        public bool RequiresHold => Group != null && Group.RequiresHold;
        public float HoldDuration => Group != null ? Group.HoldDuration : 0;
        public void Configure(DescentPortalGroup group, int slot)
        { Group = group; Slot = slot; group.RegisterSlot(slot, this); }
        public bool CanInteract(IInteractionActor actor, out string reason)
        {
            reason = "Portal slot is unavailable";
            return Group != null && !Group.SlotUsed(Slot) && Group.CanInteractAt(actor, this, out reason);
        }
        public void OnInteractInstant(IInteractionActor actor)
        {
            if (Group != null && !RequiresHold && !Group.TryEnter(actor, Slot, out string reason))
                GameEvents.RaiseToast(actor != null ? actor.PlayerId : -1, reason);
        }
        public bool OnHoldStarted(IInteractionActor actor) => Group != null && Group.BeginHold(actor, Slot, out _);
        public void OnHoldProgress(IInteractionActor actor) { }
        public void OnHoldCanceled(IInteractionActor actor) => Group?.CancelHold(actor, Slot);
        public void OnHoldComplete(IInteractionActor actor)
        {
            if (Group != null && !Group.CompleteHold(actor, Slot, out string reason))
                GameEvents.RaiseToast(actor != null ? actor.PlayerId : -1, reason);
        }
    }
}
