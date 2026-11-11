using Emberdelve.Core;
using Emberdelve.Items;
using UnityEngine;

namespace Emberdelve.Player
{
    /// <summary>Read-only shared drink clock for owner hands and observers.</summary>
    public sealed class PotionDrinkPresentation : MonoBehaviour
    {
        private PlayerRig _rig;
        private ConsumableChannel _channel;
        private bool _networkDrinking;
        private float _progress, _duration, _sampledAt;
        public static PotionDrinkPresentation Ensure(PlayerRig rig)
        {
            if (rig == null) return null;
            var view = rig.GetComponent<PotionDrinkPresentation>();
            if (view == null) view = rig.gameObject.AddComponent<PotionDrinkPresentation>();
            view._rig = rig;
            return view;
        }
        private ConsumableChannel Channel
        {
            get { if (_channel == null) _channel = GetComponent<ConsumableChannel>(); return _channel; }
        }
        public bool IsDrinking => _rig != null && !_rig.IsDead && !_rig.IsExtracted &&
            (NetRole.IsClientOnly ? _networkDrinking : Channel != null && Channel.IsChanneling);
        public float Progress => !IsDrinking ? 0f : NetRole.IsClientOnly
            ? Mathf.Clamp01(_progress + Mathf.Min(.15f, Time.unscaledTime - _sampledAt) / Mathf.Max(.01f, _duration))
            : Channel.ChannelProgress;
        public void ApplyNetwork(bool drinking, float progress, float duration)
        {
            if (_networkDrinking == drinking && _progress == progress && _duration == duration) return;
            _networkDrinking = drinking;
            _progress = Mathf.Clamp01(progress); _duration = duration; _sampledAt = Time.unscaledTime;
        }
        // Uncork, raise, sip, lower: the item is consumed only after the full authority channel.
        public static float Raise(float progress) => Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(.16f, .38f, progress)) *
            (1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(.80f, 1f, progress)));
    }
}
