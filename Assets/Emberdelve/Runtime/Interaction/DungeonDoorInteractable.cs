using System;
using UnityEngine;
using Emberdelve.Core;

namespace Emberdelve.Interaction
{
    /// <summary>Owner-side request seam for a deterministic modular doorway.</summary>
    public interface IDoorInteractionAuthority
    {
        bool DoorStateReady { get; }
        bool RequestDoorToggle(string doorId);
    }

    /// <summary>Server-owned doorway state operation used by simulated dungeon enemies.</summary>
    public interface IDoorAiAuthority
    {
        bool RequestEnemyDoorOpen(string doorId);
    }

    /// <summary>A server-authoritative hinged doorway. Its stable id is derived from the two adjacent plan cells.</summary>
    [DisallowMultipleComponent]
    public sealed class DungeonDoorInteractable : MonoBehaviour, IInteractable
    {
        [SerializeField] private string doorId;
        [SerializeField] private Transform hinge;
        [SerializeField] private Collider blocker;
        [SerializeField] private float openYaw = 95f;
        [SerializeField] private float interactionReach = 3f;
        private bool _open;

        public string DoorId => doorId;
        public bool IsOpen => _open;
        public bool BlockerEnabled => blocker != null && blocker.enabled;
        public Collider BlockerCollider => blocker;
        public string PromptText => _open ? "Close door" : "Open door";
        public bool RequiresHold => false;
        public float HoldDuration => 0f;

        public void Configure(string id, Transform newHinge, Collider doorBlocker, float swingYaw)
        {
            doorId = id;
            hinge = newHinge;
            blocker = doorBlocker;
            openYaw = swingYaw;
            SetOpen(false);
        }

        public bool CanInteract(IInteractionActor actor, out string reason)
        {
            if (NetRole.IsClientOnly && (!TryGetAuthority(actor, out IDoorInteractionAuthority authority) || !authority.DoorStateReady))
            { reason = "Door state is syncing"; return false; }
            return InteractionRules.Validate(actor, this, interactionReach, out reason);
        }

        public void OnInteractInstant(IInteractionActor actor)
        {
            if (!CanInteract(actor, out string reason))
            {
                GameEvents.RaiseToast(actor != null ? actor.PlayerId : GameEvents.NoPlayer, reason);
                return;
            }
            if (NetRole.IsNetworked)
            {
                if (!TryGetAuthority(actor, out IDoorInteractionAuthority authority) || !authority.RequestDoorToggle(doorId))
                    GameEvents.RaiseToast(actor != null ? actor.PlayerId : GameEvents.NoPlayer, "Waiting for the server");
                return;
            }
            SetOpen(!_open);
        }

        public void OnHoldComplete(IInteractionActor actor) => OnInteractInstant(actor);

        /// <summary>Applies an authoritative state update; validates IDs before changing collision.</summary>
        public bool ApplyReplicatedState(string id, bool open)
        {
            if (!string.Equals(doorId, id, StringComparison.Ordinal) || !IsValidDoorId(id)) return false;
            SetOpen(open);
            return true;
        }

        /// <summary>Called only after the network authority has validated sender, life, reach and line of sight.</summary>
        public void ToggleAuthoritatively() => SetOpen(!_open);

        /// <summary>Opens without toggling; callers must already be the local gameplay authority.</summary>
        public bool OpenAuthoritatively()
        {
            if (_open) return false;
            SetOpen(true);
            return true;
        }

        public static bool IsValidDoorId(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length > 32) return false;
            for (int i = 0; i < id.Length; i++)
            {
                char c = id[i];
                if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '-')) return false;
            }
            return true;
        }

        private void SetOpen(bool open)
        {
            _open = open;
            if (hinge != null) hinge.localRotation = Quaternion.Euler(0f, open ? openYaw : 0f, 0f);
            if (blocker != null) blocker.enabled = !open;
        }

        private static bool TryGetAuthority(IInteractionActor actor, out IDoorInteractionAuthority authority)
        {
            authority = null;
            Component component = actor as Component;
            if (component == null) return false;
            MonoBehaviour[] behaviours = component.GetComponents<MonoBehaviour>();
            for (int i = 0; i < behaviours.Length; i++)
                if ((authority = behaviours[i] as IDoorInteractionAuthority) != null) return true;
            return false;
        }
    }
}
