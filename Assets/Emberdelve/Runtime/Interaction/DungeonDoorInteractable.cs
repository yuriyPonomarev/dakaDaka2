using System;
using UnityEngine;
using Emberdelve.Core;
using Emberdelve.Feedback;

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
        [SerializeField] private bool secret;
        [SerializeField] private string secretLocalKey;
        [SerializeField] private Collider leverProbe;
        [SerializeField] private Vector3 closedPanelPosition;
        [SerializeField] private Vector3 openPanelOffset;
        [SerializeField] private Transform leverHandle;
        [SerializeField] private BoxCollider secretRoomVolume;
        [SerializeField, Min(.05f)] private float swingDuration = .43f;
        private bool _open;
        private int _boundFloor;
        private float _swing;

        public string DoorId => doorId;
        public bool IsOpen => _open;
        public bool BlockerEnabled => blocker != null && blocker.enabled;
        public Collider BlockerCollider => blocker;
        public bool IsSecret => secret;
        public bool AllowsEnemyOpening => !secret;
        public string PromptText => secret ? (_open ? "Activated" : "Pull lever") : (_open ? "Close door" : "Open door");
        public bool RequiresHold => false;
        public float HoldDuration => 0f;

        public void Configure(string id, Transform newHinge, Collider doorBlocker, float swingYaw)
        {
            hinge = newHinge;
            blocker = doorBlocker;
            openYaw = swingYaw;
            Configure(id);
        }

        public void Configure(string id)
        {
            secret = false;
            doorId = id;
            _boundFloor = 0;
            SetOpen(false, immediate: true);
        }

        /// <summary>The interaction root stays at the lever; the descendant panel may be offset to a remote entrance.</summary>
        public void ConfigureSecret(string localKey, Transform slidingPanel, Collider wallBlocker,
            Collider interactionProbe, Vector3 openLocalOffset)
        {
            secret = true;
            secretLocalKey = localKey;
            doorId = string.Empty; // Assigned from the plan cell before network publication.
            _boundFloor = 0;
            hinge = slidingPanel;
            blocker = wallBlocker;
            leverProbe = interactionProbe;
            closedPanelPosition = slidingPanel != null ? slidingPanel.localPosition : Vector3.zero;
            openPanelOffset = openLocalOffset;
            leverHandle = transform.Find("Lever_Handle");
            SetOpen(false, immediate: true);
        }

        public string SecretIdForCell(int x, int y) => "secret_" + x + "_" + y + "_" + secretLocalKey;

        public void ConfigureSecretRoom(BoxCollider volume) => secretRoomVolume = volume;

        /// <summary>Reserves the cache footprint for authored cache content, independent of its height or open state.</summary>
        public bool ContainsSecretRoomPoint(Vector3 worldPoint)
        {
            if (!secret || secretRoomVolume == null) return false;
            Vector3 local = secretRoomVolume.transform.InverseTransformPoint(worldPoint) - secretRoomVolume.center;
            Vector3 half = secretRoomVolume.size * .5f;
            return Mathf.Abs(local.x) <= half.x && Mathf.Abs(local.z) <= half.z;
        }

        public bool BindModuleCell(int x, int y)
        {
            if (!secret || _boundFloor != 0 || x < 0 || y < 0 || !ValidateSecret(out _)) return false;
            string id = SecretIdForCell(x, y);
            if (!IsValidDoorId(id) || (!string.IsNullOrEmpty(doorId) && doorId != id)) return false;
            doorId = id;
            return true;
        }

        public bool ValidateSecret(out string reason)
        {
            reason = "Invalid secret lever or sliding panel";
            if (!secret || !IsValidDoorId(secretLocalKey) || secretLocalKey.Length > 12 ||
                hinge == null || hinge == transform || !hinge.IsChildOf(transform) || blocker == null ||
                blocker.isTrigger || !blocker.transform.IsChildOf(hinge) || leverProbe == null ||
                leverProbe == blocker || !leverProbe.enabled || !leverProbe.gameObject.activeSelf ||
                !leverProbe.transform.IsChildOf(transform) || leverProbe.transform.IsChildOf(hinge) ||
                hinge.IsChildOf(leverProbe.transform) ||
                secretRoomVolume == null || !secretRoomVolume.isTrigger ||
                !secretRoomVolume.transform.IsChildOf(transform) || secretRoomVolume.transform.IsChildOf(hinge) ||
                secretRoomVolume.transform.IsChildOf(leverProbe.transform) ||
                !Finite(secretRoomVolume.center) || !Finite(secretRoomVolume.size) ||
                secretRoomVolume.size.x <= 0f || secretRoomVolume.size.y <= 0f || secretRoomVolume.size.z <= 0f ||
                !Finite(closedPanelPosition) || !Finite(openPanelOffset) || openPanelOffset.sqrMagnitude < 0.01f)
                return false;
            reason = null;
            return true;
        }

        private static bool Finite(Vector3 value) =>
            !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
            !float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
            !float.IsNaN(value.z) && !float.IsInfinity(value.z);

        /// <summary>Bind a freshly staged door once, before publishing any network state.</summary>
        public bool BindStagedFloor(int floor)
        {
            if (floor < 2 || floor > 3) return false;
            if (_boundFloor != 0) return _boundFloor == floor;
            string scoped = "f" + floor + "_" + doorId;
            if (!IsValidDoorId(doorId) || !IsValidDoorId(scoped)) return false;
            doorId = scoped; _boundFloor = floor; return true;
        }

        public bool CanInteract(IInteractionActor actor, out string reason)
        {
            if (NetRole.IsClientOnly && (!TryGetAuthority(actor, out IDoorInteractionAuthority authority) || !authority.DoorStateReady))
            { reason = "Door state is syncing"; return false; }
            if (secret && !ValidateSecret(out reason)) return false;
            if (secret && _open) { reason = "Already activated"; return false; }
            // The remote wall is never an interaction target: reach and LOS terminate at the lever.
            return InteractionRules.Validate(actor, secret ? (Component)leverProbe : this, interactionReach, out reason);
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
            ToggleAuthoritatively();
        }

        public void OnHoldComplete(IInteractionActor actor) => OnInteractInstant(actor);

        /// <summary>Applies an authoritative state update; validates IDs before changing collision.</summary>
        public bool ApplyReplicatedState(string id, bool open, bool immediate = false)
        {
            if (!string.Equals(doorId, id, StringComparison.Ordinal) || !IsValidDoorId(id)) return false;
            SetOpen(open, immediate);
            return true;
        }

        /// <summary>Called only after the network authority has validated sender, life, reach and line of sight.</summary>
        public void ToggleAuthoritatively() => SetOpen(secret || !_open);

        /// <summary>Opens without toggling; callers must already be the local gameplay authority.</summary>
        public bool OpenAuthoritatively()
        {
            if (_open || !AllowsEnemyOpening) return false;
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

        private void SetOpen(bool open, bool immediate = false)
        {
            bool startsOpening = open && !_open;
            _open = open;
            if (blocker != null && blocker.enabled == open) blocker.enabled = !open;
            if (secret)
            {
                // Cache panels retain their authoritative lift; only regular hinged doors swing.
                if (hinge != null) hinge.localPosition = closedPanelPosition + (open ? openPanelOffset : Vector3.zero);
                if (leverHandle != null) leverHandle.localRotation = Quaternion.Euler(open ? 35f : -35f, 0f, 0f);
                return;
            }
            if (immediate || !Application.isPlaying || !isActiveAndEnabled)
            {
                _swing = open ? 1f : 0f;
                PoseHinge();
            }
            else if (startsOpening && _swing < 1f && hinge != null)
                WorldAudio.Play(WorldSound.DoorCreak, transform.position + Vector3.up * 1.05f, .77f);
        }

        private void Update()
        {
            if (secret) return;
            float target = _open ? 1f : 0f;
            if (_swing == target) return;
            _swing = Mathf.MoveTowards(_swing, target, Time.deltaTime / swingDuration);
            PoseHinge();
            if (_swing == 0f && hinge != null)
                WorldAudio.Play(WorldSound.DoorThud, transform.position + Vector3.up * 1.05f, .88f);
        }

        private void PoseHinge()
        {
            if (hinge != null) hinge.localRotation = Quaternion.Euler(0f, Mathf.SmoothStep(0f, openYaw, _swing), 0f);
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
