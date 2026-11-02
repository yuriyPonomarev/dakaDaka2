using UnityEngine;
using Emberdelve.Combat;
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
        private float _holdDuration;
        private string _lastPrompt = string.Empty;
        private HealthComponent _health;
        private bool _runEnded;

        public IInteractable CurrentTarget => _current;
        public float HoldProgress => _holdProgress;
        public float CapturedHoldDuration => _holdDuration;

        private void Awake()
        {
            if (rig == null) rig = GetComponent<PlayerRig>();
        }

        private void Update()
        {
            if (rig == null || config == null) return;

            EmberdelveInput input = rig.Input;
            if (input == null) return;

            if (rig.IsDead || _runEnded)
            {
                ClearTarget();
                return;
            }

            IInteractable target = ResolveTarget();

            if (!ReferenceEquals(target, _current))
            {
                _current = target;
                _currentHost = target as MonoBehaviour;
                ResetHold();
                _lastPrompt = target == null ? string.Empty : BuildPrompt(rig, target, out _);
                GameEvents.RaiseInteractionPrompt(rig.PlayerId, _lastPrompt);
            }

            if (_current == null) return;

            // The prompt text can change while the same target stays aimed at (a chest that just emptied, a
            // rule that now blocks the gate): re-read it every frame and only raise it when it differs, so a
            // line that was true when the target was first seen cannot outlive the truth.
            string prompt = BuildPrompt(rig, _current, out _);
            if (prompt != _lastPrompt)
            {
                _lastPrompt = prompt;
                GameEvents.RaiseInteractionPrompt(rig.PlayerId, prompt);
            }

            if (_current.RequiresHold)
            {
                if (input.InteractHeld)
                {
                    float duration;
                    if (_current is IActorHoldDuration timed)
                    {
                        // Keep the capture while partial progress decays, so repress/regear cannot retime it.
                        if (_holdDuration <= 0f) _holdDuration = Mathf.Max(0.1f, timed.ResolveHoldDuration(rig));
                        duration = _holdDuration;
                    }
                    else
                    {
                        // Extraction and other non-opted-in holds retain their original timing path.
                        duration = Mathf.Max(0.1f, _current.HoldDuration);
                    }
                    _holdProgress += Time.deltaTime / duration;
                    GameEvents.RaiseInteractionHold(rig.PlayerId, _holdProgress);

                    if (_holdProgress >= 1f)
                    {
                        ResetHold();

                        // The target may have become invalid during the hold (rule changed, run ended):
                        // ask again instead of trusting the answer from when the hold began.
                        if (_current.CanInteract(rig, out string blockedReason))
                        {
                            _current.OnHoldComplete(rig);
                        }
                        else
                        {
                            GameEvents.RaiseToast(rig.PlayerId, string.IsNullOrEmpty(blockedReason) ? "Cannot interact" : blockedReason);
                        }
                    }
                }
                else if (_holdProgress > 0f)
                {
                    _holdProgress = Mathf.Max(0f, _holdProgress - Time.deltaTime * 1.6f);
                    if (_holdProgress <= 0f) _holdDuration = 0f;
                    GameEvents.RaiseInteractionHold(rig.PlayerId, _holdProgress);
                }
            }
            else if (input.InteractPressed)
            {
                _current.OnInteractInstant(rig);
            }
        }

        /// <summary>The line the HUD shows for a target (public so a gate can assert on the exact text).</summary>
        public static string PromptFor(IInteractionActor actor, IInteractable target) { return BuildPrompt(actor, target, out _); }

        private static string BuildPrompt(IInteractionActor actor, IInteractable target, out string reason)
        {
            string label = target.PromptText;

            if (!target.CanInteract(actor, out reason))
            {
                string blocked = string.IsNullOrEmpty(reason) ? "Cannot interact" : reason;
                return label + "  -  " + blocked;
            }

            // A prompt that already says "Hold ..." must not be told it is a hold twice.
            bool saysHold = label != null && label.IndexOf("hold", System.StringComparison.OrdinalIgnoreCase) >= 0;
            return target.RequiresHold && !saysHold ? label + "  (hold)" : label;
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

        private void ResetHold()
        {
            _holdProgress = 0f;
            _holdDuration = 0f;
            GameEvents.RaiseInteractionHold(rig.PlayerId, 0f);
        }

        private void ClearTarget()
        {
            if (_current != null)
            {
                _current = null;
                _currentHost = null;
                ResetHold();
                _lastPrompt = string.Empty;
                GameEvents.RaiseInteractionPrompt(rig.PlayerId, string.Empty);
            }
        }

        private void OnEnable()
        {
            _health = GetComponent<HealthComponent>();
            if (_health != null) _health.Damaged += OnDamaged;
            GameEvents.PlayerRunStateChanged += OnRunState;
        }

        // A finished run has nothing left to interact with: drop the target and its prompt at once.
        private void OnRunState(int playerId, RunState state)
        {
            if (rig == null || playerId != rig.PlayerId) return;
            _runEnded = state == RunState.Dead || state == RunState.Extracted;
            if (_runEnded) ClearTarget();
        }

        // Any damage event interrupts a hold in progress: the player has to start over.
        private void OnDamaged(DamageInfo info)
        {
            if (_holdProgress <= 0f && _holdDuration <= 0f) return;
            ResetHold();
        }

        private void OnDisable()
        {
            if (_health != null) _health.Damaged -= OnDamaged;
            GameEvents.PlayerRunStateChanged -= OnRunState;
            _health = null;
            ClearTarget();
        }
    }
}
