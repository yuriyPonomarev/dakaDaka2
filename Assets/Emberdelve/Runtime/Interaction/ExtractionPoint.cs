using UnityEngine;
using Emberdelve.Core;
using Emberdelve.Data;

namespace Emberdelve.Interaction
{
    /// <summary>
    /// What an extraction point needs from the run layer. RunDirector implements it, so the
    /// interactable never reaches into flow code directly.
    /// </summary>
    public interface IExtractionSink
    {
        bool ExtractionAllowed(out string reason);
        void BeginExtraction();
    }

    /// <summary>
    /// The Ember Gate. Standing in its volume and holding the interact key for the configured
    /// duration asks the run layer to finish the run; the run layer decides the outcome.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ExtractionPoint : MonoBehaviour, IInteractable
    {
        [Header("Wiring")]
        [SerializeField] private GameConfig config;
        [Tooltip("Assigned to the RunDirector by the level builder; left empty the gate searches its parents.")]
        [SerializeField] private MonoBehaviour sinkBehaviour;
        [SerializeField] private Light glowLight;
        [SerializeField] private Renderer portalRenderer;

        [Header("Presentation")]
        [SerializeField] private float pulseSpeed = 1.4f;
        [SerializeField] private float minIntensity = 1.6f;
        [SerializeField] private float maxIntensity = 5.5f;

        private IExtractionSink _sink;
        private float _pulse;

        public string PromptText => "Escape through the Ember Gate";
        public bool RequiresHold => true;
        public float HoldDuration => config != null ? config.extractionHoldDuration : 3f;

        private void Awake()
        {
            _sink = sinkBehaviour as IExtractionSink;
            if (_sink == null) _sink = GetComponentInParent<IExtractionSink>();
            if (glowLight == null) glowLight = GetComponentInChildren<Light>();
            if (portalRenderer == null) portalRenderer = GetComponentInChildren<Renderer>();
        }

        public bool CanInteract(out string reason)
        {
            if (_sink == null)
            {
                reason = "No run director in this scene";
                return false;
            }

            return _sink.ExtractionAllowed(out reason);
        }

        public void OnHoldComplete() => _sink?.BeginExtraction();

        public void OnInteractInstant()
        {
            if (CanInteract(out string reason)) GameEvents.RaiseToast(reason ?? "Hold to escape");
        }

        private void Update()
        {
            if (glowLight == null) return;
            _pulse += Time.deltaTime * pulseSpeed;
            glowLight.intensity = Mathf.Lerp(minIntensity, maxIntensity, 0.5f + 0.5f * Mathf.Sin(_pulse));
        }

        private void OnValidate()
        {
            if (glowLight == null) glowLight = GetComponentInChildren<Light>();
        }
    }
}
