using Emberdelve.Core;
using Emberdelve.Interaction;
using Emberdelve.Player;
using Unity.Netcode;
using UnityEngine;

namespace Emberdelve.Net
{
    /// <summary>Owner bridge that turns the local hold lifecycle into server requests.</summary>
    public sealed class NetExtractionClient : MonoBehaviour, IExtractionInteractionAuthority, IExtractionFloorAuthority
    {
        private const float HeartbeatSeconds = 0.25f;

        private NetPlayerRig _owner;
        private PlayerRig _rig;
        private uint _nextSequence;
        private uint _activeSequence;
        private uint _pendingSequence;
        private double _nextHeartbeatAt;
        private bool _requiresRelease;

        public event System.Action<uint, bool, string> HoldResultReceived;

        public void Configure(NetPlayerRig owner)
        {
            _owner = owner;
            _rig = owner != null ? owner.Rig : null;
        }

        private void Update()
        {
            if (_requiresRelease && _rig != null && _rig.Input != null && !_rig.Input.InteractHeld)
                _requiresRelease = false;
            uint sequence = _activeSequence != 0 ? _activeSequence : _pendingSequence;
            if (sequence == 0 || !CanSend()) return;
            double now = Time.realtimeSinceStartupAsDouble;
            if (now < _nextHeartbeatAt) return;
            _nextHeartbeatAt = now + HeartbeatSeconds;
            SendHeartbeat(sequence);
        }

        public bool AcceptsExtractionFloor(int floor)
        {
            if (_owner == null || !_owner.IsSpawned || _rig == null || !_rig.IsInPlay || _rig.IsDead) return false;
            var clock = _owner.RaidClock.Value;
            return clock.IsValid && !clock.Waiting && !clock.Expired && clock.Floor == floor
                && (floor == 0 || (floor == 3 && clock.Enabled && clock.MovementGeneration == _rig.FloorMovementGeneration));
        }

        public bool BeginExtractionHold()
        {
            if (_requiresRelease || !CanSend() || _activeSequence != 0 || _pendingSequence != 0) return false;
            if (_nextSequence == uint.MaxValue) return false;
            uint sequence = ++_nextSequence;
            _activeSequence = sequence;
            _nextHeartbeatAt = Time.realtimeSinceStartupAsDouble + HeartbeatSeconds;
            if (IsHost)
            {
                bool started = NetExtractionServer.Current != null &&
                    NetExtractionServer.Current.BeginHold(NetworkManager.ServerClientId, _owner.NetworkObject, sequence);
                if (!started && _activeSequence == sequence) _activeSequence = 0;
                return started;
            }
            _owner.ExtractionHoldBeginRpc(sequence);
            return true;
        }

        public void ContinueExtractionHold()
        {
            // Update sends the server lease heartbeat while active or awaiting a completion answer.
        }

        public void CancelExtractionHold()
        {
            if (_activeSequence == 0 || !CanSend()) return;
            uint sequence = _activeSequence;
            _activeSequence = 0;
            SendCancel(sequence);
        }

        public bool CompleteExtractionHold()
        {
            if (_activeSequence == 0 || !CanSend()) return false;
            uint sequence = _activeSequence;
            _activeSequence = 0;
            _pendingSequence = sequence;
            _nextHeartbeatAt = Time.realtimeSinceStartupAsDouble;
            SendComplete(sequence);
            return true;
        }

        public void OnResult(uint sequence, bool accepted, string reason)
        {
            bool active = sequence != 0 && sequence == _activeSequence;
            bool pending = sequence != 0 && sequence == _pendingSequence;
            if (!active && !pending) return;

            if (active) _activeSequence = 0;
            if (pending) _pendingSequence = 0;
            if (!accepted) _requiresRelease = true;

            HoldResultReceived?.Invoke(sequence, accepted, reason ?? "");

            if (accepted)
            {
                // The separate server RunResult RPC carries the exact terminal result and drives owner presentation.
                return;
            }

            if (_rig != null)
            {
                // A refused attempt must not restart every frame while the same key is held.
                // Wait for a real release before allowing a fresh server request.
                _rig.GetComponent<Interactor>()?.CancelActiveHold();
                GameEvents.RaiseToast(_rig.PlayerId, string.IsNullOrEmpty(reason) ? "Extraction interrupted" : reason);
            }
        }

        private bool CanSend() => _owner != null && _owner.IsSpawned && _rig != null;

        private bool IsHost => NetRole.Current == NetRoleKind.Host;

        private void SendHeartbeat(uint sequence)
        {
            if (IsHost) NetExtractionServer.Current?.Heartbeat(NetworkManager.ServerClientId, _owner.NetworkObject, sequence);
            else _owner.ExtractionHoldHeartbeatRpc(sequence);
        }

        private void SendCancel(uint sequence)
        {
            if (IsHost) NetExtractionServer.Current?.CancelHold(NetworkManager.ServerClientId, _owner.NetworkObject, sequence);
            else _owner.ExtractionHoldCancelRpc(sequence);
        }

        private void SendComplete(uint sequence)
        {
            if (IsHost) NetExtractionServer.Current?.CompleteHold(NetworkManager.ServerClientId, _owner.NetworkObject, sequence);
            else _owner.ExtractionHoldCompleteRpc(sequence);
        }
    }
}
