using UnityEngine;
using Emberdelve.Core;
using Emberdelve.Combat;
using Emberdelve.Data;
using Emberdelve.InputLayer;
using Emberdelve.Skills;
using Emberdelve.Stats;

namespace Emberdelve.Movement
{
    /// <summary>
    /// CharacterController locomotion only: walk, sprint, crouch, jump, gravity.
    /// No class numbers live here; they arrive through ApplyClass(CharacterClassData).
    /// DesiredMoveIntent is the value a future client would send to the server.
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public sealed class PlayerMovement : MonoBehaviour
    {
        [Header("Wiring")]
        [SerializeField] private EmberdelveInput input;
        [SerializeField] private CharacterController controller;
        [SerializeField] private Transform viewTransform;

        [Header("Physics")]
        [SerializeField] private float gravity = -19f;
        [SerializeField] private float speedChangeRate = 11f;
        [SerializeField] private float groundedOffset = -0.22f;
        [SerializeField] private float groundedRadius = 0.32f;
        [SerializeField] private LayerMask groundMask;

        [Header("Crouch")]
        [SerializeField] private float crouchTransitionSpeed = 9f;

        private CharacterClassData _class;
        private CharacterStatStack _stats;
        private StatsComponent _attributes;
        private StatusComponent _status;
        private float _horizontalSpeed;
        private float _verticalVelocity;
        private float _terminalVelocity = 55f;
        private float _currentHeight;
        private bool _crouched;
        private bool _guardActive;
        private float _speedPenalty = 1f;
        private IMoveFlowSource _flow;
        private float _jumpCooldown;

        // Dash state. The dash is a movement mode, not a teleport: it goes through the same
        // CharacterController.Move call as walking, so walls stop it and the grounded check still applies.
        private Vector3 _dashDirection;
        private float _dashSpeed;
        private float _dashTimeRemaining;
        private bool _dashBlocked;

        public bool IsCrouched => _crouched;
        public bool IsGrounded { get; private set; }
        public bool IsMoving { get; private set; }
        public bool IsDashing => _dashTimeRemaining > 0f;

        /// <summary>True when the current or most recent dash was stopped by geometry instead of finishing.</summary>
        public bool DashWasBlocked => _dashBlocked;
        public float DashProgress => _dashTimeRemaining > 0f ? 1f - Mathf.Clamp01(_dashTimeRemaining / Mathf.Max(0.01f, _dashDurationHint)) : 0f;
        public Vector3 Velocity => controller.velocity;
        public Vector3 DesiredMoveIntent { get; private set; }
        public float SpeedMultiplier { get; private set; } = 1f;

        /// <summary>Seconds of dash the last cast asked for, kept for progress reporting only.</summary>
        private float _dashDurationHint = 0.22f;

        private void Awake()
        {
            if (controller == null) controller = GetComponent<CharacterController>();
            if (input == null) input = GetComponent<EmberdelveInput>();
            if (viewTransform == null)
            {
                // WASD follows the active camera. On the stock rig the camera is a pitch-only child of the
                // body, so its flattened forward equals the body yaw; resolving it here also covers a rig
                // whose camera is driven independently.
                Camera cam = GetComponentInChildren<Camera>();
                if (cam != null) viewTransform = cam.transform;
            }
            _currentHeight = controller.height;
            ConfigureMoveThreshold();
        }

        public void ApplyClass(CharacterClassData data, LayerMask ground)
        {
            _class = data;
            groundMask = ground;
            _currentHeight = data.standHeight;
            controller.height = _currentHeight;
            controller.radius = 0.34f;
            controller.center = new Vector3(0f, _currentHeight * 0.5f, 0f);
            groundedRadius = controller.radius;
            ConfigureMoveThreshold();
        }

        /// <summary>
        /// Unity discards a CharacterController.Move whose length is below minMoveDistance (default 0.001 m),
        /// and its own CharacterController.minMoveDistance documentation recommends 0. At high frame rates a
        /// class-speed step is already sub-millimetre (3.5 m/s * ~0.0008 s = 0.0028 m, and the Lerp below only
        /// reaches that after several frames), so every discarded step is fed back through controller.velocity
        /// as zero and the acceleration ramp restarts forever: the player crawls instead of reaching class speed.
        ///
        /// Only this threshold is changed here. skinWidth/radius/height/center/stepOffset/slopeLimit, the
        /// collision masks, gravity and every class number stay as authored, and locomotion still goes
        /// through controller.Move, so walls, ledges and the grounded check remain the authority.
        /// Set from Awake as well as ApplyClass so a rig authored before this change (serialized default
        /// 0.001) is corrected before its first Move, and re-asserted whenever class configuration is applied.
        /// </summary>
        private void ConfigureMoveThreshold()
        {
            if (controller == null) return;
            controller.minMoveDistance = 0f;
        }

        /// <summary>Passive movement modifiers from the class kit. Null clears them.</summary>
        public void ApplyStatStack(CharacterStatStack stats) => _stats = stats;

        /// <summary>
        /// The live six-attribute owner (Docs/Design/ATTRIBUTES.md). It supplies the DEX term of the
        /// locomotion rule; the stack above still supplies the passive/gear terms. Null falls back to the
        /// pre-amendment resolution, so a movement component that was never given a class still moves.
        /// </summary>
        public void ApplyAttributeStats(StatsComponent attributes) => _attributes = attributes;

        /// <summary>
        /// The ground speed this character is allowed to produce for a given input state, with the DEX
        /// locomotion rule, passives, gear, guard and any temporary slow already folded in.
        ///
        /// Public and side-effect free on purpose: it is the number a future server compares a client's
        /// reported movement against, and it is the number tools can assert without needing input devices.
        /// It is also the only place these speeds are resolved - Move() below clamps its request to this
        /// result, and the attribute query it uses is the same call, so DEX is applied exactly once.
        /// </summary>
        public float ResolveGroundSpeed(bool sprintHeld, bool crouched)
        {
            if (_class == null) return 0f;

            float speed;
            if (crouched)
            {
                speed = ResolveAuthoredSpeed(_class.crouchSpeed, SkillStat.CrouchSpeed);
            }
            else if (sprintHeld)
            {
                speed = ResolveAuthoredSpeed(_class.sprintSpeed, SkillStat.SprintSpeed);
            }
            else
            {
                speed = ResolveWalkSpeed();
            }

            if (_guardActive) speed *= _class.guardSpeedMultiplier;
            speed *= _speedPenalty;
            speed *= StatusSpeedMultiplier();

            // D1b: fighting penalties (held weapon, swing phase, guard stagger) - authority-side state, no stamina.
            if (_flow != null) speed *= Mathf.Clamp(_flow.GetMoveFlowFactor(), 0.05f, 1f);

            return Mathf.Max(0f, speed);
        }

        /// <summary>
        /// Docs/Design/ATTRIBUTES.md walking rule, in the amendment's exact order:
        /// clamp(authoredMoveSpeed * max(0.5, 1 + 0.025 * DEX modifier) * passive/gear multiplier
        ///       + passive/gear flat, 2, 8).
        ///
        /// The multiplier and the flat are read separately from the stack on purpose: CharacterStatStack.Get
        /// computes (base + flat) * multiplier, which is NOT the amendment's order. Sprint and crouch keep
        /// their own authored bases (see ResolveAuthoredSpeed); guard, aim, status, collision, acceleration
        /// and the requested-speed cap below are unchanged.
        /// </summary>
        public float ResolveWalkSpeed()
        {
            if (_class == null) return 0f;

            float multiplier = _stats != null ? _stats.Multiplier(SkillStat.MoveSpeed) : 1f;
            float flat = _stats != null ? _stats.Flat(SkillStat.MoveSpeed) : 0f;

            if (_attributes != null && _attributes.IsReady) return _attributes.ResolveMoveSpeed(multiplier, flat);
            return _stats != null ? _stats.Get(SkillStat.MoveSpeed, _class.moveSpeed) : _class.moveSpeed;
        }

        /// <summary>
        /// Sprint/crouch: the authored base for that mode, the same DEX factor, and that mode's own
        /// passive/gear terms. The authored base is retained rather than clamped into the walking band.
        /// </summary>
        private float ResolveAuthoredSpeed(float authoredBaseSpeed, SkillStat stat)
        {
            if (_class == null) return 0f;

            float multiplier = _stats != null ? _stats.Multiplier(stat) : 1f;
            float flat = _stats != null ? _stats.Flat(stat) : 0f;

            if (_attributes != null && _attributes.IsReady) return _attributes.ResolveAuthoredSpeed(authoredBaseSpeed, multiplier, flat);
            return _stats != null ? _stats.Get(stat, authoredBaseSpeed) : authoredBaseSpeed;
        }

        private float StatusSpeedMultiplier()
        {
            if (_status == null) _status = GetComponent<StatusComponent>();
            return _status != null ? _status.SpeedMultiplier : 1f;
        }

        /// <summary>
        /// Starts a dash. The direction is flattened to the ground plane, the speed is distance / duration,
        /// and nothing here moves the transform: Move() feeds the dash through the controller, which is what
        /// makes walls and ledges real obstacles instead of suggestions.
        /// </summary>
        public bool TryStartDash(Vector3 direction, float distance, float duration)
        {
            if (controller == null || _class == null) return false;

            Vector3 flat = new Vector3(direction.x, 0f, direction.z);
            if (flat.sqrMagnitude < 0.0001f) flat = FlatForward(transform.forward);
            flat.Normalize();

            _dashDurationHint = Mathf.Clamp(duration, 0.05f, 2f);
            float seconds = _dashDurationHint;
            float meters = Mathf.Clamp(distance, 0.5f, 20f);

            // Capped so a dash cannot out-run the physics step that has to resolve its collisions.
            _dashSpeed = Mathf.Min(meters / seconds, 24f);
            _dashDirection = flat;
            _dashTimeRemaining = seconds;
            _dashBlocked = false;
            return true;
        }

        /// <summary>True when a dash can start at all (a controller and a class are present).</summary>
        public bool CanDash => controller != null && _class != null;

        public void CancelDash()
        {
            _dashTimeRemaining = 0f;
        }

        private static Vector3 FlatForward(Vector3 forward)
        {
            Vector3 flat = new Vector3(forward.x, 0f, forward.z);
            return flat.sqrMagnitude < 0.0001f ? Vector3.forward : flat.normalized;
        }

        public void SetGuardActive(bool active) => _guardActive = active;

        /// <summary>D1b: where the combat-flow factor (held-weapon penalty, swing phase, guard stagger) comes from. Null = none.</summary>
        public void SetFlowSource(IMoveFlowSource source) => _flow = source;

        /// <summary>Slows locomotion while a class is aiming, drawing or otherwise committed.</summary>
        public void SetSpeedPenalty(float multiplier) => _speedPenalty = Mathf.Clamp(multiplier, 0.1f, 1f);

        public void SetCrouched(bool value) => _crouched = value;

        private void Update()
        {
            if (_class == null || input == null) return;

            float dt = Time.deltaTime;
            if (input.CrouchPressed) _crouched = !_crouched;

            UpdateCapsuleHeight(dt);
            GroundedCheck();
            JumpAndGravity(dt);
            Move(dt);
        }

        private void UpdateCapsuleHeight(float dt)
        {
            float target = _crouched ? _class.crouchHeight : _class.standHeight;
            _currentHeight = Mathf.MoveTowards(_currentHeight, target, dt * crouchTransitionSpeed);
            controller.height = _currentHeight;
            controller.center = new Vector3(0f, _currentHeight * 0.5f, 0f);
        }

        private void GroundedCheck()
        {
            Vector3 sphere = new Vector3(transform.position.x, transform.position.y + groundedOffset, transform.position.z);
            IsGrounded = Physics.CheckSphere(sphere, groundedRadius, groundMask, QueryTriggerInteraction.Ignore);
        }

        private void JumpAndGravity(float dt)
        {
            if (IsGrounded)
            {
                if (_verticalVelocity < 0f) _verticalVelocity = -2f;
                _jumpCooldown -= dt;

                if (input.JumpPressed && !_crouched && _jumpCooldown <= 0f)
                {
                    _verticalVelocity = Mathf.Sqrt(_class.jumpHeight * -2f * gravity);
                    _jumpCooldown = 0.2f;
                }
            }

            if (_verticalVelocity < _terminalVelocity)
            {
                _verticalVelocity += gravity * dt;
            }
        }

        private void Move(float dt)
        {
            if (IsDashing)
            {
                MoveDash(dt);
                return;
            }

            Vector2 raw = input.Move;
            if (raw.sqrMagnitude > 1f) raw = raw.normalized;

            float targetSpeed = 0f;
            if (raw.sqrMagnitude > 0.01f)
            {
                targetSpeed = ResolveGroundSpeed(input.SprintHeld, _crouched);
            }

            float currentHorizontal = new Vector3(controller.velocity.x, 0f, controller.velocity.z).magnitude;
            if (Mathf.Abs(currentHorizontal - targetSpeed) > 0.05f)
            {
                _horizontalSpeed = Mathf.Lerp(currentHorizontal, targetSpeed, dt * speedChangeRate);
            }
            else
            {
                _horizontalSpeed = targetSpeed;
            }

            // Bound the *request* to the speed this character is currently allowed to produce.
            // The Lerp above seeds from controller.velocity, and CharacterController.velocity also
            // contains contact / depenetration displacement from the previous Move (wall, enemy body,
            // chest collider). A contact spike therefore re-enters the requested speed and the
            // speedChangeRate decay keeps asking for several times the legal class/passive speed for
            // many frames (measured: intent 34.663 m/s against a resolved 5.824 m/s sprint cap).
            // Only the requested magnitude is clamped, to [0, ResolveGroundSpeed target]. The physical
            // response is left to the controller: no transform write, no velocity write, no collision
            // mask change, so walls and contacts remain the authority on where the body ends up.
            _horizontalSpeed = Mathf.Clamp(_horizontalSpeed, 0f, targetSpeed);

            IsMoving = targetSpeed > 0.05f;
            SpeedMultiplier = BaseMoveSpeed > 0f ? _horizontalSpeed / BaseMoveSpeed : 0f;

            Vector3 direction = ViewRelativeDirection(raw, viewTransform != null ? viewTransform : transform);
            Vector3 desired = direction * _horizontalSpeed;
            DesiredMoveIntent = desired;

            Vector3 motion = desired * dt + new Vector3(0f, _verticalVelocity, 0f) * dt;
            if (motion.sqrMagnitude > 0f) controller.Move(motion);
        }

        /// <summary>
        /// Dash movement. Same controller call, same gravity, same grounded check as walking, so a dash into
        /// a wall is stopped by the wall and reports it. The dash ends early on a side collision rather than
        /// pushing through or sliding along an unintended path.
        /// </summary>
        private void MoveDash(float dt)
        {
            _dashTimeRemaining -= dt;

            Vector3 desired = _dashDirection * _dashSpeed;
            DesiredMoveIntent = desired;
            IsMoving = true;
            _horizontalSpeed = _dashSpeed;
            SpeedMultiplier = BaseMoveSpeed > 0f ? _dashSpeed / BaseMoveSpeed : 0f;

            CollisionFlags flags = controller.Move(desired * dt + new Vector3(0f, _verticalVelocity, 0f) * dt);

            if ((flags & CollisionFlags.Sides) != 0)
            {
                _dashBlocked = true;
                _dashTimeRemaining = 0f;
                _horizontalSpeed = 0f;
            }
        }

        /// <summary>
        /// The character's own resolved ordinary walking speed, so speed ratios (dash, sprint) stay
        /// comparable across classes and gear instead of using a second, differently ordered formula.
        /// </summary>
        private float BaseMoveSpeed => ResolveWalkSpeed();

        /// <summary>
        /// Maps raw move input (x = strafe, y = forward) into a world-space direction relative to the
        /// view. WASD has to follow where the player is looking; treating raw.x/raw.y as world X/Z made
        /// W always run towards world north no matter which way the camera faced.
        ///
        /// Public and static so the mapping is verifiable without input devices, and so a future server
        /// validating a client's DesiredMoveIntent applies the identical rule.
        /// </summary>
        public static Vector3 ViewRelativeDirection(Vector2 raw, Transform view)
        {
            if (view == null || raw.sqrMagnitude < 0.0001f) return Vector3.zero;

            // Flatten: pitch must not shorten ground movement, and a straight-up view still walks.
            Vector3 forward = new Vector3(view.forward.x, 0f, view.forward.z);
            Vector3 right = new Vector3(view.right.x, 0f, view.right.z);

            if (forward.sqrMagnitude < 0.0001f) forward = Vector3.forward;
            if (right.sqrMagnitude < 0.0001f) right = Vector3.right;

            forward.Normalize();
            right.Normalize();

            Vector3 direction = right * raw.x + forward * raw.y;
            return direction.sqrMagnitude > 0.0001f ? direction.normalized : Vector3.zero;
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = IsGrounded ? new Color(0.2f, 0.9f, 0.3f, 0.4f) : new Color(0.9f, 0.2f, 0.2f, 0.4f);
            Gizmos.DrawWireSphere(new Vector3(transform.position.x, transform.position.y + groundedOffset, transform.position.z), groundedRadius);
        }
    }
}
