using Emberdelve.Flow;
using UnityEngine;
namespace Emberdelve.Net
{
    public sealed class NetRoyaleZoneBoundary : MonoBehaviour
    {
        private NetPlayerRig _owner;
        private RoyaleZoneBoundary _view;
        public void Configure(NetPlayerRig owner) { _owner = owner; }
        private void LateUpdate()
        {
            if (Application.isBatchMode) return;
            var rig = _owner != null ? _owner.Rig : null;
            var zone = _owner != null ? _owner.RoyaleZone.Value : default;
            if (_owner == null || !_owner.IsSpawned || _owner.Role != NetPlayerRole.Owner || rig == null
                || !zone.VisibleFor(rig.FloorMovementGeneration, rig.IsInPlay, rig.IsDead))
            { if (_view != null) _view.Hide(); return; }
            if (_view == null) _view = new GameObject("Owner_ZoneBoundary").AddComponent<RoyaleZoneBoundary>();
            _view.Apply(new Vector2(zone.X, zone.Z), zone.Radius, rig.transform.position.y);
        }
        private void OnDisable() { if (_view != null) _view.Hide(); }
        private void OnDestroy() { if (_view != null) Destroy(_view.gameObject); }
    }
}
