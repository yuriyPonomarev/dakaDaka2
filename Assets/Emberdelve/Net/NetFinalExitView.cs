using Emberdelve.Interaction;
using UnityEngine;
namespace Emberdelve.Net
{
    public sealed class NetFinalExitView : MonoBehaviour
    {
        private NetPlayerRig _owner;
        private ExtractionPoint _point;
        public ExtractionPoint Point => _point;
        public void Configure(NetPlayerRig owner) { _owner = owner; }
        private void Update()
        {
            var rig = _owner != null ? _owner.Rig : null;
            var state = _owner != null ? _owner.RoyaleZone.Value : default;
            if (_owner == null || !_owner.IsSpawned || _owner.Role != NetPlayerRole.Owner || rig == null || !state.HasFinalExit
                || !state.VisibleFor(rig.FloorMovementGeneration, rig.IsInPlay, rig.IsDead))
            { Hide(); return; }
            if (_point == null) _point = ExtractionPoint.CreateFinalView(null, state.ExitPosition, state.ExitHoldDuration, null);
            if (_point != null) { _point.transform.position = state.ExitPosition; _point.gameObject.SetActive(true); }
        }
        public void Hide() { if (_point != null) _point.gameObject.SetActive(false); }
        private void OnDisable() => Hide();
        private void OnDestroy() { if (_point != null) { if (Application.isPlaying) Destroy(_point.gameObject); else DestroyImmediate(_point.gameObject); } }
    }
}
