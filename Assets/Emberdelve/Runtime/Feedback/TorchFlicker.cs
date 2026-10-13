using UnityEngine;

namespace Emberdelve.Feedback
{
    /// <summary>
    /// Simple torch flicker for point lights (level sconces and the player's hand light).
    /// Uses layered sine noise instead of Random so the look stays steady and cheap.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class TorchFlicker : MonoBehaviour
    {
        [SerializeField] private Light lightSource;
        [SerializeField] private float baseIntensity = 14f;
        [SerializeField] private float flickerAmount = 0.22f;
        [SerializeField] private float speed = 6f;
        [SerializeField] private bool affectRange = true;
        [SerializeField] private float rangeWobble = 0.06f;

        private float _seed;
        private float _baseRange;

        private void Awake()
        {
            if (lightSource == null) lightSource = GetComponent<Light>();
            if (lightSource == null)
            {
                enabled = false;
                return;
            }

            _seed = Random.value * 100f;
            _baseRange = lightSource.range;
            baseIntensity = lightSource.intensity;
        }

        private void Update()
        {
            if (lightSource == null) return;

            float t = Time.unscaledTime * speed + _seed;
            float noise = 0.6f * Mathf.Sin(t) + 0.3f * Mathf.Sin(t * 2.7f) + 0.15f * Mathf.Sin(t * 5.3f);

            lightSource.intensity = baseIntensity * (1f + noise * flickerAmount);

            if (affectRange) lightSource.range = _baseRange * (1f + noise * rangeWobble);
        }
    }
}
