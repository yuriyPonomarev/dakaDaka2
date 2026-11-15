using UnityEngine;

namespace Emberdelve.Feedback
{
    /// <summary>Crackle follows the same replicated light switch as the visible flame.</summary>
    [DisallowMultipleComponent]
    public sealed class FlameAudio : MonoBehaviour
    {
        private Light _light;
        private float _next;
        private int _pulse;
        private void Awake() { _light = GetComponent<Light>(); }
        private void Update()
        {
            if (_light == null || !_light.enabled || Time.time < _next || !WorldAudio.CanHear(transform.position, 9f)) return;
            _pulse++;
            _next = Time.time + .35f + (_pulse % 5) * .07f;
            WorldAudio.Play(WorldSound.Fire, transform.position, .13f, .92f + (_pulse % 7) * .025f);
        }
    }
}
