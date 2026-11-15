using Emberdelve.Core;
using UnityEngine;

namespace Emberdelve.Interaction
{
    public interface IDescentPortalAuthority
    {
        bool DescentReady(int floor);
        bool RequestDescent(int floor, int group, int slot);
    }
    /// <summary>Client interaction view. It only sends intent; no local transition or slot consumption.</summary>
    public sealed class ReplicatedDescentPortalSlot : MonoBehaviour, IInteractable
    {
        private IDescentPortalAuthority _authority;
        private IInteractionActor _owner;
        public int Floor { get; private set; }
        public int Group { get; private set; }
        public int Slot { get; private set; }
        public string PromptText => "Descend to floor " + (Floor + 1);
        public bool RequiresHold => false;
        public float HoldDuration => 0;
        public void Configure(IDescentPortalAuthority authority, IInteractionActor owner, int floor, int group, int slot)
        { _authority = authority; _owner = owner; Floor = floor; Group = group; Slot = slot; }
        public bool CanInteract(IInteractionActor actor, out string reason)
        {
            reason = "Portal is unavailable";
            return isActiveAndEnabled && ReferenceEquals(actor, _owner) && _authority != null && _authority.DescentReady(Floor)
                && InteractionRules.Validate(actor, this, InteractionRules.DefaultReach, out reason);
        }
        public void OnInteractInstant(IInteractionActor actor)
        {
            if (CanInteract(actor, out _) && !_authority.RequestDescent(Floor, Group, Slot))
                GameEvents.RaiseToast(actor.PlayerId, "Portal request is unavailable");
        }
        public void OnHoldComplete(IInteractionActor actor) => OnInteractInstant(actor);
    }
}
