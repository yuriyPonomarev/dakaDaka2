using UnityEngine;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.InputLayer;

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

        [Header("Physics")]
        [SerializeField] private float gravity = -19f;
        [SerializeField] private float speedChangeRate = 11f;
        [SerializeField] private float groundedOffset = -0.22f;
        [SerializeField] private float groundedRadius = 0.32f;
        [SerializeField] private LayerMask groundMask;

        [Header("Crouch")]
        [SerializeField] private float crouchTransitionSpeed = 9f;

        private CharacterClassData _class;
        private float _horizontalSpeed;
        private float _verticalVelocity;
        private float _terminalVelocity = 55f;
        private float _currentHeight;
        private bool _crouched;
        private bool _guardActive;
        private float _speedPenalty = 1f;
        private float _jumpCooldown;

        public bool IsCrouched => _crouched;
        public bool IsGrounded { get; private set; }
        public bool IsMoving { get; private set; }
        public Vector3 Velocity => controller.velocity;
        public Vector3 DesiredMoveIntent { get; private set; }
        public float SpeedMultiplier { get; private set; } = 1f;

        private void Awake()
        {
            if (controller == null) controller = GetComponent<CharacterController>();
            if (input == null) input = GetComponent<EmberdelveInput>();
            _currentHeight = controller.height;
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
        }

        public void SetGuardActive(bool active) => _guardActive = active;

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
            Vector2 raw = input.Move;
            if (raw.sqrMagnitude > 1f) raw = raw.normalized;

            float targetSpeed = 0f;
            if (raw.sqrMagnitude > 0.01f)
            {
                if (_crouched) targetSpeed = _class.crouchSpeed;
                else if (input.SprintHeld) targetSpeed = _class.sprintSpeed;
                else targetSpeed = _class.moveSpeed;

                if (_guardActive) targetSpeed *= _class.guardSpeedMultiplier;
                targetSpeed *= _speedPenalty;
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

            IsMoving = targetSpeed > 0.05f;
            SpeedMultiplier = _class.moveSpeed > 0f ? _horizontalSpeed / _class.moveSpeed : 0f;

            Vector3 direction = new Vector3(raw.x, 0f, raw.y).normalized;
            Vector3 desired = direction * _horizontalSpeed;
            DesiredMoveIntent = desired;

            Vector3 motion = desired * dt + new Vector3(0f, _verticalVelocity, 0f) * dt;
            if (motion.sqrMagnitude > 0f) controller.Move(motion);
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = IsGrounded ? new Color(0.2f, 0.9f, 0.3f, 0.4f) : new Color(0.9f, 0.2f, 0.2f, 0.4f);
            Gizmos.DrawWireSphere(new Vector3(transform.position.x, transform.position.y + groundedOffset, transform.position.z), groundedRadius);
        }
    }
}
