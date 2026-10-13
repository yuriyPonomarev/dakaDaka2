using UnityEngine;
using Emberdelve.Core;
using Emberdelve.InputLayer;
using Emberdelve.Player;

namespace Emberdelve.Interaction
{
    /// <summary>
    /// The player-side half of interaction: it decides what the local player is aiming at and
    /// whether the interact key was held long enough, then calls the interactable. It never performs
    /// the effect itself, so the same interactable can later be driven by a server-validated request.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(PlayerRig))]
    public sealed class Interactor : MonoBehaviour
    {
        private const int MaxProbeResults = 12;
        private static readonly Collider[] _probeBuffer = new Collider[MaxProbeResults];

        [Header("Wiring")]
        [SerializeField] private PlayerRig rig;
        [SerializeField] private GameConfig config;

        private IInteractable _current;
        private MonoBehaviour _currentHost;
        private float _holdProgress;
        private string _lastPrompt = string.Empty;

        public IInteractable CurrentTarget => _current;
        public float HoldProgress => _holdProgress;

        private void Awake()
        {
            if (rig == null) rig = GetComponent<PlayerRig>();
        }

        private void Update()
        {
            if (rig == null || config == null) return;

            EmberdelveInput input = rig.Input;
            if (input == null) return;

            if (rig.IsDead)
            {
                ClearTarget();
                return;
            }

            IInteractable target = ResolveTarget();

            if (!ReferenceEquals(target, _current))
            {
                _current = target;
                _currentHost = target as MonoBehaviour;
                _holdProgress = 0f;
                GameEvents.RaiseInteractionHold(0f);
                GameEvents.RaiseInteractionPrompt(target == null ? string.Empty : BuildPrompt(target, out _));
            }

            if (_current == null) return;

            if (_current.RequiresHold)
            {
                float duration = Mathf.Max(0.1f, _current.HoldDuration);

                if (input.InteractHeld)
                {
                    _holdProgress += Time.deltaTime / duration;
                    GameEvents.RaiseInteractionHold(_holdProgress);

                    if (_holdProgress >= 1f)
                    {
                        _holdProgress = 0f;
                        GameEvents.RaiseInteractionHold(0f);
                        _current.OnHoldComplete();
                    }
                }
                else if (_holdProgress > 0f)
                {
                    _holdProgress = Mathf.Max(0f, _holdProgress - Time.deltaTime * 1.6f);
                    GameEvents.RaiseInteractionHold(_holdProgress);
                }
            }
            else if (input.InteractPressed)
            {
                _current.OnInteractInstant();
            }
        }

        private string BuildPrompt(IInteractable target, out string reason)
        {
            string label = target.PromptText;

            if (!target.CanInteract(out reason))
            {
                string blocked = string.IsNullOrEmpty(reason) ? "Cannot interact" : reason;
                return label + "  -  " + blocked;
            }

            return target.RequiresHold ? label + "  (hold)" : label;
        }

        private IInteractable ResolveTarget()
        {
            if (_current != null && _currentHost == null) ClearTarget();

            float range = Mathf.Max(0.5f, config.interactionRange);
            float probeRadius = Mathf.Max(0.2f, config.interactSphereRadius) + 0.9f;

            // Probe from chest height, not eye height: loot sits low on the floor and the gate trigger
            // spans the full body height. The aim direction still decides what counts as "in front of you".
            Vector3 origin = rig.transform.position + Vector3.up * 1.0f;
            Vector3 direction = rig.AimDirection;
            Vector3 probe = origin + direction * (range * 0.5f);

            int count = Physics.OverlapSphereNonAlloc(probe, probeRadius, _probeBuffer, config.interactMask, QueryTriggerInteraction.Collide);

            IInteractable best = null;
            float bestDistance = float.MaxValue;

            for (int i = 0; i < count; i++)
            {
                Collider collider = _probeBuffer[i];
                if (collider == null) continue;

                MonoBehaviour host = collider.GetComponentInParent<MonoBehaviour>();
                if (host == null) continue;

                IInteractable candidate = host as IInteractable;
                if (candidate == null) continue;

                Vector3 center = collider.bounds.center;
                Vector3 toTarget = center - origin;
                float distance = toTarget.magnitude;
                if (distance > range) continue;

                if (distance > 0.3f && Vector3.Angle(direction, toTarget) > 80f) continue;

                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = candidate;
                }
            }

            return best;
        }

        private void ClearTarget()
        {
            if (_current != null)
            {
                _current = null;
                _currentHost = null;
                _holdProgress = 0f;
                GameEvents.RaiseInteractionHold(0f);
                GameEvents.RaiseInteractionPrompt(string.Empty);
            }
        }

        private void OnDisable()
        {
            ClearTarget();
        }
    }
}
