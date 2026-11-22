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
        /// <summary>R2: the rule is evaluated for THIS actor (their state, their carried value).</summary>
        bool ExtractionAllowed(IInteractionActor actor, out string reason);
        bool BeginExtraction(IInteractionActor actor, out string reason);
    }

    public interface IExtractionFloorSource
    {
        int ExtractionFloorFor(IInteractionActor actor);
    }
    public interface IExtractionFloorAuthority
    {
        bool AcceptsExtractionFloor(int floor);
    }

    /// <summary>Owner-side bridge for a server-authorized extraction hold.</summary>
    public interface IExtractionInteractionAuthority
    {
        bool BeginExtractionHold();
        void ContinueExtractionHold();
        void CancelExtractionHold();
        bool CompleteExtractionHold();
    }

    /// <summary>
    /// The Ember Gate. Standing in its volume and holding the interact key for the configured
    /// duration asks the run layer to finish the run; the run layer decides the outcome.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ExtractionPoint : MonoBehaviour, IInteractable, IInteractableHoldLifecycle
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
        public int Floor { get; private set; }
        private float _finalHoldDuration = 3f;
        public void BindFinalFloor(IExtractionSink sink)
        {
            Floor = 3;
            _sink = sink;
        }
        public static ExtractionPoint CreateFinalView(Transform parent, Vector3 position, float holdDuration, IExtractionSink sink)
        {
            if (!float.IsFinite(holdDuration) || holdDuration <= 0 || holdDuration > 120) return null;
            Material material = null;
            foreach (var view in FindObjectsByType<MeshRenderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (view.name == "Gate_Portal") { material = view.sharedMaterial; break; }
            if (material == null) return null;
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube); go.name = "Final_ExtractionPortal";
            go.transform.SetParent(parent, false); go.transform.position = position;
            go.transform.localScale = new Vector3(1.1f, 2.1f, .18f);
            go.GetComponent<BoxCollider>().isTrigger = true;
            int layer = LayerMask.NameToLayer(GameConfig.InteractLayerName); if (layer >= 0) go.layer = layer;
            go.GetComponent<MeshRenderer>().sharedMaterial = material;
            var point = go.AddComponent<ExtractionPoint>(); point.BindFinalFloor(sink); point._finalHoldDuration = holdDuration;
            return point;
        }
        public static ExtractionPoint FindForFloor(int floor)
        {
            if (floor != 0 && floor != 3) return null;
            ExtractionPoint found = null;
            foreach (var point in FindObjectsByType<ExtractionPoint>(FindObjectsSortMode.None))
            {
                if (!point.isActiveAndEnabled || point.Floor != floor) continue;
                if (found != null) return null; // Ambiguous authority target must never depend on Unity object ordering.
                found = point;
            }
            return found;
        }
        private float _pulse;

        public string PromptText => "Escape through the Ember Gate";
        public bool RequiresHold => true;
        public float HoldDuration => Floor == 3 ? _finalHoldDuration : config != null ? config.extractionHoldDuration : 3f;

        private void Awake()
        {
            if (_sink == null) _sink = sinkBehaviour as IExtractionSink;
            if (_sink == null) _sink = GetComponentInParent<IExtractionSink>();
            if (glowLight == null) glowLight = GetComponentInChildren<Light>();
            if (portalRenderer == null) portalRenderer = GetComponentInChildren<Renderer>();
        }

        public bool CanInteract(IInteractionActor actor, out string reason)
        {
            if (!isActiveAndEnabled) { reason = "Exit is unavailable"; return false; }
            if (NetRole.IsClientOnly)
            {
                if (!TryGetNetworkAuthority(actor, out var authority)
                    || !(authority is IExtractionFloorAuthority floors) || !floors.AcceptsExtractionFloor(Floor))
                {
                    reason = "Exit state is syncing";
                    return false;
                }
                return InteractionRules.Validate(actor, this, InteractionRules.ReachOf(_sink), out reason);
            }

            if (_sink == null)
            {
                reason = "No run director in this scene";
                return false;
            }

            if (_sink is IExtractionFloorSource floorsSource && floorsSource.ExtractionFloorFor(actor) != Floor)
            { reason = "Exit belongs to another floor"; return false; }
            if (!InteractionRules.Validate(actor, this, InteractionRules.ReachOf(_sink), _sink, out reason)) return false;

            return _sink.ExtractionAllowed(actor, out reason);
        }

        public void OnHoldComplete(IInteractionActor actor)
        {
            if (NetRole.IsNetworked)
            {
                if (TryGetNetworkAuthority(actor, out IExtractionInteractionAuthority authority))
                    authority.CompleteExtractionHold();
                return;
            }

            if (_sink == null) return;
            if (!CanInteract(actor, out string reason))
            {
                GameEvents.RaiseToast(actor != null ? actor.PlayerId : GameEvents.NoPlayer, reason ?? "Cannot escape yet");
                return;
            }

            _sink.BeginExtraction(actor, out _);
        }

        public bool OnHoldStarted(IInteractionActor actor)
        {
            if (!NetRole.IsNetworked) return Floor != 3 || CanInteract(actor, out _);
            return TryGetNetworkAuthority(actor, out IExtractionInteractionAuthority authority) &&
                authority.BeginExtractionHold();
        }

        public void OnHoldProgress(IInteractionActor actor)
        {
            if (NetRole.IsNetworked && TryGetNetworkAuthority(actor, out IExtractionInteractionAuthority authority))
                authority.ContinueExtractionHold();
        }

        public void OnHoldCanceled(IInteractionActor actor)
        {
            if (NetRole.IsNetworked && TryGetNetworkAuthority(actor, out IExtractionInteractionAuthority authority))
                authority.CancelExtractionHold();
        }

        public void OnInteractInstant(IInteractionActor actor)
        {
            if (CanInteract(actor, out string reason)) GameEvents.RaiseToast(actor.PlayerId, reason ?? "Hold to escape");
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

        private static bool TryGetNetworkAuthority(IInteractionActor actor, out IExtractionInteractionAuthority authority)
        {
            authority = null;
            Component component = actor as Component;
            if (component == null) return false;
            MonoBehaviour[] behaviours = component.GetComponents<MonoBehaviour>();
            for (int i = 0; i < behaviours.Length; i++)
            {
                authority = behaviours[i] as IExtractionInteractionAuthority;
                if (authority != null) return true;
            }
            return false;
        }
    }
}
