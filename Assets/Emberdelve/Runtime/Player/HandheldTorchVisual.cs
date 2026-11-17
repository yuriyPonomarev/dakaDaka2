using Emberdelve.Feedback;
using UnityEngine;

namespace Emberdelve.Player
{
    /// <summary>The flame belongs to the equipped item, never to the camera or the bag.</summary>
    // Both first-person and remote hand poses run at100; orient the flame after those bones move.
    [DefaultExecutionOrder(200)]
    public sealed class HandheldTorchVisual : MonoBehaviour
    {
        public const string WeaponId = "pitch_torch";
        public const float LightIntensity = 4.5f;
        public const float LightRange = 12f;
        public Light source;
        public Transform flame;
        private Transform _volume;
        private PlayerRig _rig;
        private Vector3 _flameScale;
        private Vector3 _flameLocal;
        /// <summary>World-up lift of the whole fire so it burns at the very end of the head.</summary>
        public const float FlameLift = .085f;
        private GameObject _haze;
        private void Awake()
        {
            _rig = GetComponentInParent<PlayerRig>();
            if (flame != null) { _flameScale = flame.localScale; _flameLocal = flame.localPosition; }
            if (source == null) return;
            WorldLightAppearance.Configure(source);
            source.range = LightRange;
            var haze = source.transform.Find("LocalLightHaze");
            if (haze != null) { _haze = haze.gameObject; _haze.SetActive(false); }
        }
        public void AlignFlame()
        {
            if(flame==null)return;
            flame.rotation=Quaternion.identity;
            if(_flameLocal==Vector3.zero)_flameLocal=flame.localPosition;
            flame.position=transform.TransformPoint(_flameLocal)+Vector3.up*FlameLift;
            if(_volume==null)_volume=flame.Find("Flame/FlameVolume");
            // Keep the volume centred on the actual combustible head after the hand pose is applied.
            if(_volume!=null)_volume.SetPositionAndRotation(transform.TransformPoint(new Vector3(0,.502f,0))+Vector3.up*(.13f+FlameLift),Quaternion.identity);
        }
        private void LateUpdate()
        {
            bool lit = _rig == null || (!_rig.IsDead && !_rig.IsExtracted);
            if (source != null)
            {
                source.enabled = lit;
                source.intensity = LightIntensity * (1f + .07f * Mathf.Sin(Time.time * 9f) + .035f * Mathf.Sin(Time.time * 23f));
            }
            // The room-light haze is too large at arm's length and reads as an orange ball.
            if (_haze != null) _haze.SetActive(false);
            if (flame != null)
            {
                flame.gameObject.SetActive(lit);
                // Heat rises vertically even while the wooden shaft is tilted in a hand.
                AlignFlame();
                flame.localScale = Vector3.Scale(_flameScale, new Vector3(1f, 1f + .07f * Mathf.Sin(Time.time * 13f), 1f));
            }
        }
    }
}
