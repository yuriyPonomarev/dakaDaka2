using UnityEngine;
using Emberdelve.Combat;
using Emberdelve.Core;

namespace Emberdelve.AI
{
    /// <summary>
    /// V2 presentation only: drives the skeleton model's Animator from the state the EnemyAgent already exposes. It READS
    /// EnemyAgent / EnemyBrain / HealthComponent and never writes gameplay state (no damage, no movement, no timers): the
    /// agent still moves the root transform and the brain still owns every timing, so the colliders, hit volumes and the
    /// telegraph are exactly what they were with the blockout cubes.
    ///
    /// Animator contract (built by EmberdelveEnemyVisualFactory): Speed (float, m/s), AttackSpeed (float, playback
    /// multiplier of the Attack state), Attack and Hit (triggers), Dead (bool); states Idle, Walk, Run, Attack, Hit, Death.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class EnemyVisualDriver : MonoBehaviour
    {
        public const string SpeedParameter = "Speed";
        public const string AttackSpeedParameter = "AttackSpeed";
        public const string AttackTrigger = "Attack";
        public const string HitTrigger = "Hit";
        public const string DeadParameter = "Dead";

        [SerializeField] private Animator animator;
        [Tooltip("Seconds the measured speed takes to settle (smoothing of the finite difference of the root position).")]
        [SerializeField] private float speedSmoothing = 0.08f;
        [Tooltip("Extra seconds assumed for the swing that follows the brain's windup when matching the Attack clip length.")]
        [SerializeField] private float swingSeconds = 0.5f;

        private EnemyAgent _agent;
        private HealthComponent _health;
        private Vector3 _lastPosition;
        private float _speed;
        private bool _wasWindingUp;
        private int _lastSwingsBegun;
        private float _attackPlayedAt = -10f;
        private float _attackClipLength = 1f;
        private bool _dead;
        private bool _hasParameters;
        private int _speedHash, _attackSpeedHash, _attackHash, _hitHash, _deadHash;

        public Animator Animator => animator;

        /// <summary>Smoothed horizontal speed (m/s) currently fed to the animator.</summary>
        public float MeasuredSpeed => _speed;

        private void Awake()
        {
            if (animator == null) animator = GetComponentInChildren<Animator>();
            _agent = GetComponentInParent<EnemyAgent>();

            _speedHash = Animator.StringToHash(SpeedParameter);
            _attackSpeedHash = Animator.StringToHash(AttackSpeedParameter);
            _attackHash = Animator.StringToHash(AttackTrigger);
            _hitHash = Animator.StringToHash(HitTrigger);
            _deadHash = Animator.StringToHash(DeadParameter);

            if (animator != null)
            {
                // The agent moves the root; a clip must never move it (or the model away from its colliders).
                animator.applyRootMotion = false;
                _hasParameters = animator.runtimeAnimatorController != null;
                CacheAttackClipLength();
            }
        }

        private void Start()
        {
            if (_agent == null) return;

            _lastPosition = _agent.transform.position;
            _lastSwingsBegun = _agent.SwingsBegun;
            _health = _agent.Health != null ? _agent.Health : _agent.GetComponent<HealthComponent>();
            if (_health != null) _health.Damaged += HandleDamaged;
        }

        private void OnDestroy()
        {
            if (_health != null) _health.Damaged -= HandleDamaged;
        }

        private void CacheAttackClipLength()
        {
            RuntimeAnimatorController controller = animator.runtimeAnimatorController;
            if (controller == null) return;

            AnimationClip[] clips = controller.animationClips;
            for (int i = 0; i < clips.Length; i++)
            {
                if (clips[i] != null && clips[i].name.IndexOf("Attack", System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    _attackClipLength = Mathf.Max(0.2f, clips[i].length);
                    return;
                }
            }
        }

        private void LateUpdate()
        {
            if (animator == null || !_hasParameters || _agent == null) return;

            float dt = Time.deltaTime;
            if (dt <= 0f) return;

            if (_agent.IsDead)
            {
                if (!_dead)
                {
                    _dead = true;
                    animator.SetBool(_deadHash, true);
                }

                animator.SetFloat(_speedHash, 0f);
                return;
            }

            // Locomotion: the agent moves the root transform, so the animator speed is the measured ground speed.
            Vector3 position = _agent.transform.position;
            Vector3 delta = position - _lastPosition;
            delta.y = 0f;
            _lastPosition = position;

            float raw = delta.magnitude / dt;
            float blend = 1f - Mathf.Exp(-dt / Mathf.Max(0.01f, speedSmoothing));
            _speed = Mathf.Lerp(_speed, raw, blend);
            animator.SetFloat(_speedHash, _speed);

            // Attack: the brain's windup is the telegraph, so the clip starts with it. A swing the authority accepted without a
            // windup (a gate driving a frozen agent) still plays the clip.
            bool windingUp = _agent.IsWindingUp;
            int swings = _agent.SwingsBegun;
            // A swing that follows a windup begins on the frame the windup ends (the previous frame was still winding up).
            bool swingWithoutWindup = swings != _lastSwingsBegun && !_wasWindingUp && !windingUp;
            _lastSwingsBegun = swings;

            if ((windingUp && !_wasWindingUp) || swingWithoutWindup) PlayAttack(windingUp);
            _wasWindingUp = windingUp;
        }

        private void PlayAttack(bool fromWindup)
        {
            float windup = _agent != null && _agent.Data != null ? Mathf.Max(0.1f, _agent.Data.attackWindup) : 0.65f;
            float planned = fromWindup ? windup + swingSeconds : swingSeconds + 0.3f;
            animator.SetFloat(_attackSpeedHash, Mathf.Clamp(_attackClipLength / planned, 0.6f, 2.2f));
            animator.SetTrigger(_attackHash);
            _attackPlayedAt = Time.time;
        }

        private void HandleDamaged(DamageInfo damage)
        {
            if (animator == null || !_hasParameters || _agent == null || _agent.IsDead) return;

            // A flinch must never hide the telegraph: no Hit clip while the windup is running or a swing was just played.
            if (_agent.IsWindingUp || Time.time - _attackPlayedAt < _attackClipLength) return;
            animator.SetTrigger(_hitHash);
        }

        // ---- review / tooling seams (presentation only: they touch the animator and nothing else) ----

        /// <summary>Plays the Attack clip once without involving the brain (graphical review).</summary>
        public void PreviewAttack()
        {
            if (animator == null || !_hasParameters) return;
            PlayAttack(true);
        }

        /// <summary>Plays the Hit clip once (graphical review).</summary>
        public void PreviewHit()
        {
            if (animator == null || !_hasParameters) return;
            animator.SetTrigger(_hitHash);
        }
    }
}
