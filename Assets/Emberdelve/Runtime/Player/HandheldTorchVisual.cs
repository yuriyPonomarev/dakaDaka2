using Emberdelve.Feedback;
using UnityEngine;

namespace Emberdelve.Player
{
    /// <summary>The flame belongs to the equipped item, never to the camera or the bag.</summary>
    public sealed class HandheldTorchVisual : MonoBehaviour
    {
        public const string WeaponId = "pitch_torch";
        public Light source;
        public Transform flame;
        private PlayerRig _rig;
        private Vector3 _flameScale;
        private GameObject _haze;
        private void Awake()
        {
            _rig = GetComponentInParent<PlayerRig>();
            if (flame != null) _flameScale = flame.localScale;
            if (source == null) return;
            WorldLightAppearance.Configure(source);
            var haze = source.transform.Find("LocalLightHaze");
            if (haze != null) _haze = haze.gameObject;
        }
        private void LateUpdate()
        {
            bool lit = _rig == null || (!_rig.IsDead && !_rig.IsExtracted);
            if (source != null)
            {
                source.enabled = lit;
                source.intensity = 1.5f * (1f + .07f * Mathf.Sin(Time.time * 9f) + .035f * Mathf.Sin(Time.time * 23f));
            }
            if (_haze != null) _haze.SetActive(lit);
            if (flame != null)
            {
                flame.gameObject.SetActive(lit);
                flame.localScale = Vector3.Scale(_flameScale, new Vector3(1f, 1f + .07f * Mathf.Sin(Time.time * 13f), 1f));
            }
        }
    }
}
