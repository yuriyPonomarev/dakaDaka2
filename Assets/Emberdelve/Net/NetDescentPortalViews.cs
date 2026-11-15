using System.Collections.Generic;
using Emberdelve.Core;
using Emberdelve.Interaction;
using UnityEngine;

namespace Emberdelve.Net
{
    public sealed class NetDescentPortalViews : MonoBehaviour
    {
        private NetPlayerRig _owner;
        private DescentPortalWire _state;
        private GameObject _root;
        private readonly List<ReplicatedDescentPortalSlot> _slots = new List<ReplicatedDescentPortalSlot>();
        public IReadOnlyList<ReplicatedDescentPortalSlot> Slots => _slots;
        public void Configure(NetPlayerRig owner) { _owner = owner; }
        public void Apply(DescentPortalWire state) { _state = state.IsValid ? state : default; Refresh(); }
        private void Update() => Refresh();
        private void Refresh()
        {
            bool visible = _owner != null && _owner.Role == NetPlayerRole.Owner && _owner.Rig != null
                && _owner.Rig.IsInPlay && !_owner.Rig.IsDead && _state.Count > 0
                && _state.Generation == _owner.Rig.FloorMovementGeneration;
            if (!visible) { if (_root != null) _root.SetActive(false); return; }
            if (_root == null)
            {
                Material material = null;
                foreach (var renderer in FindObjectsByType<MeshRenderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                    if (renderer.name == "Gate_Portal") { material = renderer.sharedMaterial; break; }
                if (material == null) return;
                _root = new GameObject("Client_DescentPortals");
                int layer = LayerMask.NameToLayer(GameConfig.InteractLayerName);
                for (int i = 0; i < 9; i++)
                {
                    var go = DescentPortalVisual.Create(_root.transform, "DescentSlot_" + i, material, layer);
                    _slots.Add(go.AddComponent<ReplicatedDescentPortalSlot>());
                }
            }
            _root.SetActive(true); int size = _state.Count / 3;
            for (int i = 0; i < _slots.Count; i++)
            {
                var slot = _slots[i]; slot.gameObject.SetActive(_state.IsAvailable(i));
                if (i >= _state.Count) continue;
                slot.transform.position = _state.Position(i); slot.Configure(_owner, _owner.Rig, _state.Floor, i / size, i % size);
            }
        }
        private void OnDestroy() { if (_root != null) { if (Application.isPlaying) Destroy(_root); else DestroyImmediate(_root); } }
    }
}
