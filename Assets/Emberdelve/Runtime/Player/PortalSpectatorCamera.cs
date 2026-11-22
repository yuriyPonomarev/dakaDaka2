using Emberdelve.Core;
using Emberdelve.Level;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Emberdelve.Player
{
    [DefaultExecutionOrder(20010)]
    [DisallowMultipleComponent]
    public sealed class PortalSpectatorCamera : MonoBehaviour
    {
        private readonly RaycastHit[] _hits = new RaycastHit[24];
        private PlayerRig _owner;
        private Camera _camera;
        private Vector3 _localPosition;
        private Quaternion _localRotation;
        private int _cullingMask;
        private CameraClearFlags _clearFlags;
        private Color _background;
        private bool _tracking;
        private float _nextScan;
        private DungeonModuleSceneAssembler _observedMap;
        private float _nextMapSearch;
        public bool IsDeathSpectating { get; private set; }
        public bool HasLivingAllies { get; private set; }
        public PlayerRig Target { get; private set; }
        public PlayerRig VisibleTarget { get; private set; }
        public int TargetCount { get; private set; }

        public static bool HasLivingAlly(PlayerRig owner)
        {
            if (owner == null || owner.RoyaleFloor == 0) return false;
            foreach (var candidate in FindObjectsByType<PlayerRig>(FindObjectsSortMode.None))
                if (IsLivingAlly(owner, candidate)) return true;
            return false;
        }

        public bool WatchAfterDeath(PlayerRig rig)
        {
            if (rig == null || !rig.IsLocallyControlled || !HasLivingAlly(rig)) return false;
            IsDeathSpectating = true;
            Refresh(rig);
            if (_tracking) { SelectTarget(false); return true; }
            IsDeathSpectating = false;
            return false;
        }

        public void StopWatchingAfterDeath()
        {
            if (!IsDeathSpectating) return;
            IsDeathSpectating = false;
            Refresh(_owner);
        }

        public void Refresh(PlayerRig rig)
        {
            if (rig == null || !rig.IsLocallyControlled || (!rig.IsPortalSpectating && !IsDeathSpectating))
            {
                Restore();
                enabled = false;
                return;
            }
            _owner = rig;
            if (_tracking) return;
            foreach (var camera in rig.GetComponentsInChildren<Camera>())
                if (camera.enabled && camera.CompareTag("MainCamera")) { _camera = camera; break; }
            if (_camera == null) return;
            _localPosition = _camera.transform.localPosition;
            _localRotation = _camera.transform.localRotation;
            _cullingMask = _camera.cullingMask;
            _clearFlags = _camera.clearFlags;
            _background = _camera.backgroundColor;
            _nextScan = 0;
            _nextMapSearch = 0;
            _tracking = true;
            enabled = true;
        }

        private void LateUpdate()
        {
            if (!_tracking || _camera == null || _owner == null || (!_owner.IsPortalSpectating && !IsDeathSpectating))
            { Restore(); enabled = false; return; }
            bool cycle = Keyboard.current?.tabKey.wasPressedThisFrame == true;
            if (cycle || (VisibleTarget != null && !Eligible(VisibleTarget)) || Time.unscaledTime >= _nextScan)
                SelectTarget(cycle);

            bool visible = Eligible(Target) && HasTargetMap(Target);
            VisibleTarget = visible ? Target : null;
            _camera.cullingMask = visible ? _cullingMask : 0;
            _camera.clearFlags = visible ? _clearFlags : CameraClearFlags.SolidColor;
            _camera.backgroundColor = visible ? _background : new Color(.045f, .04f, .035f);
            if (!visible) return;

            Transform followed = Target.transform;
            Vector3 focus = followed.position + Vector3.up * 1.45f;
            Vector3 offset = Vector3.up * .9f - followed.forward * 3.8f + followed.right * .65f;
            Vector3 direction = offset.normalized;
            float distance = offset.magnitude;
            int hits = Physics.SphereCastNonAlloc(focus, .18f, direction, _hits, distance,
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < hits; i++)
                if (_hits[i].collider != null && _hits[i].collider.GetComponentInParent<PlayerRig>() == null)
                    distance = Mathf.Min(distance, Mathf.Max(.12f, _hits[i].distance - .12f));
            _camera.transform.SetPositionAndRotation(focus + direction * distance, Quaternion.LookRotation(-direction));
        }

        private void SelectTarget(bool cycle)
        {
            _nextScan = Time.unscaledTime + .25f;
            PlayerRig first = null, next = null;
            int currentId = Target != null ? Target.PlayerId : -1;
            TargetCount = 0;
            HasLivingAllies = false;
            foreach (var candidate in FindObjectsByType<PlayerRig>(FindObjectsSortMode.None))
            {
                if (!IsLivingAlly(_owner, candidate)) continue;
                HasLivingAllies = true;
                if (!Eligible(candidate)) continue;
                TargetCount++;
                int id = candidate.PlayerId;
                if (first == null || id < first.PlayerId) first = candidate;
                if (id > currentId && (next == null || id < next.PlayerId)) next = candidate;
            }
            if (cycle || !Eligible(Target))
            {
                if (first != null || !IsDeathSpectating || !IsLivingAlly(_owner, Target)) Target = next != null ? next : first;
            }
        }

        private bool Eligible(PlayerRig candidate) => IsLivingAlly(_owner, candidate) && candidate.IsInPlay
            && (IsDeathSpectating || candidate.RoyaleFloor == _owner.RoyaleFloor);

        private static bool IsLivingAlly(PlayerRig owner, PlayerRig candidate) => candidate != null && candidate != owner
            && candidate.PartyId == owner.PartyId && candidate.RoyaleFloor > 0 && !candidate.IsDead && !candidate.IsExtracted;

        private bool HasTargetMap(PlayerRig target)
        {
            if (!IsDeathSpectating || !NetRole.IsClientOnly) return true;
            var position = target.transform.position;
            bool Contains(DungeonModuleSceneAssembler map)
            {
                var layout = map != null && map.RuntimeMapValidationComplete && !map.RuntimeContentRetired ? map.RuntimeLayout : null;
                return layout != null && new Rect(layout.origin.x, layout.origin.z, layout.gridWidth * layout.CellSize,
                    layout.gridHeight * layout.CellSize).Contains(new Vector2(position.x, position.z));
            }
            if (Contains(_observedMap)) return true;
            if (Time.unscaledTime < _nextMapSearch) return false;
            _nextMapSearch = Time.unscaledTime + .25f;
            foreach (var map in FindObjectsByType<DungeonModuleSceneAssembler>(FindObjectsSortMode.None))
                if (Contains(map)) { _observedMap = map; return true; }
            return false;
        }

        private void OnDisable() => Restore();

        private void Restore()
        {
            if (_tracking && _camera != null)
            {
                _camera.transform.SetLocalPositionAndRotation(_localPosition, _localRotation);
                _camera.cullingMask = _cullingMask;
                _camera.clearFlags = _clearFlags;
                _camera.backgroundColor = _background;
            }
            _tracking = false;
            _observedMap = null;
            Target = null;
            VisibleTarget = null;
            TargetCount = 0;
            HasLivingAllies = false;
        }
    }
}
