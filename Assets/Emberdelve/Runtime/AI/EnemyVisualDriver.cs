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
        [SerializeField] private ParticleSystem bloodEffect;
        [Tooltip("Seconds the measured speed takes to settle (smoothing of the finite difference of the root position).")]
        [SerializeField] private float speedSmoothing = 0.08f;


        private EnemyAgent _agent;
        private byte _soundPhase;
        private HealthComponent _health;
        private Vector3 _lastPosition;
        private float _speed;
        private bool _phaseDriven;
        private bool _restDriven;
        private bool _releasing;
        private float _blendInUntil;
        private float _lastTarget;
        private const float AttackBlendIn = 0.12f;
        private const float RecoveryHoldFraction = 0.35f;
        private float _attackPlayedAt = -10f;
        private float _attackPlaybackDuration;
        private float _attackClipLength = 1f;
        private float _overheadClipLength = 1f;
        private bool _hasOverhead;
        private int _phaseVariant;
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
            if (_agent != null && _agent.GetComponent<Emberdelve.Feedback.WorldFootsteps>() == null)
                _agent.gameObject.AddComponent<Emberdelve.Feedback.WorldFootsteps>();

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
                _hasOverhead = animator.runtimeAnimatorController != null && animator.HasState(0, Animator.StringToHash(EnemyAttackPhase.OverheadState));
            }
        }

        private void Start()
        {
            if (_agent == null) return;

            _lastPosition = _agent.transform.position;
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
                if (clips[i] == null) continue;
                if (clips[i].name.IndexOf(EnemyAttackPhase.OverheadState, System.StringComparison.OrdinalIgnoreCase) >= 0)
                    _overheadClipLength = Mathf.Max(0.2f, clips[i].length);
                else if (clips[i].name.IndexOf("Attack", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    _attackClipLength = Mathf.Max(0.2f, clips[i].length);
            }
        }

        private void LateUpdate()
        {
            if (animator == null || !_hasParameters || _agent == null) return;

            float dt = Time.deltaTime;
            if (dt <= 0f) return;

            EnemyDormantPose.BlendShieldArm(animator, !_agent.IsDead && !_agent.IsDormant && !_agent.IsWaking, dt);
            if (_agent.IsDead)
            {
                EnemyDormantPose.FinishDead(animator, ref _restDriven);
                if (!_dead)
                {
                    _dead = true;
                    Emberdelve.Feedback.WorldAudio.PlayAttached(Emberdelve.Feedback.WorldSound.EnemyDeath, _agent.transform, Vector3.up, .65f);
                    animator.ResetTrigger(_attackHash);
                    animator.ResetTrigger(_hitHash);
                    animator.SetBool(_deadHash, true);
                }

                animator.SetFloat(_speedHash, 0f);
                return;
            }

            if (EnemyDormantPose.Apply(animator, _agent.IsDormant, _agent.IsWaking,
                _agent.Brain != null ? _agent.Brain.WakeElapsed : 0f, ref _restDriven))
            {
                _speed = 0f;
                _lastPosition = _agent.transform.position;
                _agent.RefreshDormantHitPose(animator);
                return;
            }
            _agent.RefreshDormantHitPose(animator);

            // Locomotion: the agent moves the root transform, so the animator speed is the measured ground speed.
            Vector3 position = _agent.transform.position;
            Vector3 delta = position - _lastPosition;
            delta.y = 0f;
            _lastPosition = position;

            float raw = delta.magnitude / dt;
            float blend = 1f - Mathf.Exp(-dt / Mathf.Max(0.01f, speedSmoothing));
            _speed = Mathf.Lerp(_speed, raw, blend);
            animator.SetFloat(_speedHash, _speed);

            EnemyAttackPhase phase = _agent.AttackPresentation;
            if (phase.Phase == 2 && _soundPhase != 2)
                Emberdelve.Feedback.WorldAudio.PlayAttached(_agent.UsesCinderClub ? Emberdelve.Feedback.WorldSound.BluntSwing : Emberdelve.Feedback.WorldSound.Swing, _agent.transform, Vector3.up, .8f);
            _soundPhase = phase.Phase;
            if (phase.Phase != 0)
            {
                // Recovery hands the body back to idle with a long blend instead of holding the lunge and snapping at its end.
                bool releaseNow = phase.Phase == 3 && phase.Duration > 0.05f && phase.Elapsed >= phase.Duration * RecoveryHoldFraction;
                if (_releasing || releaseNow)
                {
                    if (!_releasing)
                    {
                        _releasing = true;
                        _phaseDriven = false;
                        animator.SetFloat(_attackSpeedHash, 0.35f);
                        animator.CrossFadeInFixedTime("Idle", Mathf.Clamp(phase.Duration - phase.Elapsed + 0.12f, 0.18f, 0.45f));
                    }
                    if (phase.Phase == 3) return;
                    _releasing = false;
                }

                // The attack the server brain chose for this blow (the overhead has its own clip and state).
                int variant = !_agent.UsesCinderClub && _hasOverhead && _agent.AttackVariantIndex == EnemyAttackPhase.OverheadVariant
                    ? EnemyAttackPhase.OverheadVariant : EnemyAttackPhase.DiagonalVariant;
                if (_phaseDriven) variant = _phaseVariant;
                string stateName = EnemyAttackPhase.StateName(variant);
                float clipLength = variant == EnemyAttackPhase.OverheadVariant ? _overheadClipLength : _attackClipLength;
                float target = phase.NormalizedTime(club: _agent.UsesCinderClub, variant: variant);
                if (!_phaseDriven)
                {
                    _phaseVariant = variant;
                    // Enter the attack with a short blend from whatever locomotion pose the body is in (no first-frame pop).
                    _phaseDriven = true;
                    _blendInUntil = Time.time + AttackBlendIn;
                    animator.ResetTrigger(_attackHash);
                    animator.ResetTrigger(_hitHash);
                    animator.SetFloat(_attackSpeedHash, 0f);
                    animator.CrossFadeInFixedTime(stateName, AttackBlendIn, 0, target * clipLength);
                    _lastTarget = target;
                    return;
                }
                if (Time.time < _blendInUntil)
                {
                    // Keep the blend alive; advance the attack state at the phase rate instead of re-playing it.
                    float rate = (target - _lastTarget) / dt;
                    animator.SetFloat(_attackSpeedHash, Mathf.Max(0f, rate * clipLength));
                    _lastTarget = target;
                    return;
                }
                animator.ResetTrigger(_attackHash);
                animator.ResetTrigger(_hitHash);
                animator.SetFloat(_attackSpeedHash, 0f);
                // Smooth the scrub target slightly so frame hitches never jump the pose.
                float smoothed = Mathf.Abs(target - _lastTarget) > 0.2f ? target : Mathf.Lerp(_lastTarget, target, 1f - Mathf.Exp(-dt / 0.03f));
                _lastTarget = smoothed;
                animator.Play(stateName, 0, smoothed);
                animator.Update(0f);
            }
            else
            {
                if (_phaseDriven)
                {
                    animator.SetFloat(_attackSpeedHash, 1f);
                    animator.CrossFadeInFixedTime("Idle", 0.22f);
                }
                else if (_releasing)
                {
                    animator.SetFloat(_attackSpeedHash, 1f);
                }
                _phaseDriven = false;
                _releasing = false;
            }
        }

        public static float AttackPlaybackSeconds(bool fromWindup, float telegraphSeconds, float swingSeconds) =>
            Mathf.Max(0.02f, (fromWindup ? Mathf.Max(0f, telegraphSeconds) : 0f) + Mathf.Max(0f, swingSeconds));

        private void PlayAttack(bool fromWindup)
        {
            float planned = AttackPlaybackSeconds(fromWindup, _agent != null ? _agent.WindupSeconds : 0f,
                _agent != null ? _agent.AttackSwingSeconds : _attackClipLength);
            float playbackSpeed = _attackClipLength / planned;
            animator.SetFloat(_attackSpeedHash, playbackSpeed);
            _attackPlaybackDuration = _attackClipLength / playbackSpeed;
            // A hit queued earlier in this frame must not interrupt the telegraph on the
            // next Animator evaluation after Attack has consumed its own trigger.
            animator.ResetTrigger(_hitHash);
            animator.SetTrigger(_attackHash);
            _attackPlayedAt = Time.time;
        }

        private void HandleDamaged(DamageInfo damage)
        {
            if (_agent != null && !_agent.IsDead && damage.Amount > 0f)
                Emberdelve.Feedback.WorldAudio.PlayAttached(Emberdelve.Feedback.WorldSound.EnemyHurt, _agent.transform, Vector3.up, .5f);
            if (bloodEffect != null && _agent != null && damage.Amount > 0f)
            {
                Vector3 point = damage.Point;
                if (!float.IsFinite(point.x) || !float.IsFinite(point.y) || !float.IsFinite(point.z) ||
                    (point - _agent.transform.position).sqrMagnitude > 16f)
                    point = _agent.transform.position + Vector3.up;
                bloodEffect.transform.position = point;
                Vector3 direction = damage.Direction;
                if (float.IsFinite(direction.x) && float.IsFinite(direction.y) && float.IsFinite(direction.z) && direction.sqrMagnitude > 0.001f)
                    // DamageInfo.Direction points from the attacker into the target. Blood must travel back
                    // out of the impact surface or the target mesh can occlude the whole spray.
                    bloodEffect.transform.rotation = Quaternion.LookRotation(-direction.normalized);
                if (!bloodEffect.isPlaying) bloodEffect.Play();
                bloodEffect.Emit(18);
            }
            if (animator == null || !_hasParameters || _agent == null || _agent.IsDead) return;

            // A flinch must never hide the telegraph: no Hit clip while the windup is running or a swing was just played.
            if (_agent.IsDormant || _agent.IsWaking || _agent.AttackPresentation.Phase != 0 || Time.time - _attackPlayedAt < _attackPlaybackDuration) return;
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
