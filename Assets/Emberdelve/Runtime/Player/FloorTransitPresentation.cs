using System.Collections.Generic;
using UnityEngine;

namespace Emberdelve.Player
{
    /// <summary>Temporarily removes the same actor from the source floor's rendering and physics.
    /// Original per-component flags are restored; normally hidden owner meshes stay hidden.</summary>
    [DefaultExecutionOrder(20000)]
    [DisallowMultipleComponent]
    public sealed class FloorTransitPresentation : MonoBehaviour
    {
        private PlayerRig _rig;
        private PortalSpectatorCamera _spectator;
        private readonly Dictionary<Collider, bool> _colliders = new Dictionary<Collider, bool>();
        private readonly Dictionary<Renderer, bool> _renderers = new Dictionary<Renderer, bool>();

        public void Refresh(PlayerRig rig)
        {
            _rig = rig;
            if (rig != null && rig.IsLocallyControlled && rig.IsPortalSpectating && _spectator == null)
                _spectator = gameObject.AddComponent<PortalSpectatorCamera>();
            if (_spectator != null) _spectator.Refresh(rig);
            if (rig == null || !rig.IsInFloorTransit) { Restore(); return; }
            foreach (var collider in GetComponentsInChildren<Collider>(true))
            {
                if (!_colliders.ContainsKey(collider)) _colliders.Add(collider, collider.enabled);
                collider.enabled = false;
            }
            foreach (var renderer in GetComponentsInChildren<Renderer>(true))
            {
                if (!_renderers.ContainsKey(renderer)) _renderers.Add(renderer, renderer.forceRenderingOff);
                renderer.forceRenderingOff = true;
            }
        }

        private void LateUpdate()
        {
            if (_rig != null && _rig.IsInFloorTransit) Refresh(_rig);
            else if (_colliders.Count != 0 || _renderers.Count != 0) Restore();
        }

        private void Restore()
        {
            foreach (var entry in _colliders) if (entry.Key != null) entry.Key.enabled = entry.Value;
            foreach (var entry in _renderers) if (entry.Key != null) entry.Key.forceRenderingOff = entry.Value;
            _colliders.Clear(); _renderers.Clear();
        }
    }
}
