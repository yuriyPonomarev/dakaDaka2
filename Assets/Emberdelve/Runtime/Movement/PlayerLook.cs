using UnityEngine;
using Emberdelve.Data;
using Emberdelve.InputLayer;

namespace Emberdelve.Movement
{
    /// <summary>
    /// First-person look. Yaw rotates the body transform, pitch rotates the camera pivot only.
    /// Also owns the eye-height transition so crouching moves the camera, not just the collider.
    /// </summary>
    public sealed class PlayerLook : MonoBehaviour
    {
        [Header("Wiring")]
        [SerializeField] private EmberdelveInput input;
        [SerializeField] private PlayerMovement movement;
        [SerializeField] private Transform lookPivot;
        [SerializeField] private Transform cameraTransform;

        [Header("Look")]
        [SerializeField] private float sensitivity = 1.2f;
        [SerializeField] private float minPitch = -85f;
        [SerializeField] private float maxPitch = 85f;
        [SerializeField] private float gamepadTurnSpeed = 70f;

        [Header("Eye height")]
        [SerializeField] private float standEyeHeight = 1.68f;
        [SerializeField] private float crouchEyeHeight = 1.05f;
        [SerializeField] private float eyeLerpSpeed = 9f;

        private float _pitch;
        private float _yaw;

        public Vector3 AimOrigin => cameraTransform != null ? cameraTransform.position : transform.position + Vector3.up * standEyeHeight;
        public Vector3 AimDirection => cameraTransform != null ? cameraTransform.forward : transform.forward;
        public float Pitch => _pitch;

        private void Awake()
        {
            if (input == null) input = GetComponent<EmberdelveInput>();
            if (movement == null) movement = GetComponent<PlayerMovement>();
            if (lookPivot == null) lookPivot = transform;
            if (cameraTransform == null && lookPivot != null) cameraTransform = lookPivot;

            // Start from the transform the scene actually placed, so the first mouse move cannot snap
            // the body back to yaw 0 and make WASD point somewhere the player is not looking.
            _yaw = transform.localEulerAngles.y;
            _pitch = 0f;
            if (lookPivot != null) lookPivot.localRotation = Quaternion.Euler(_pitch, 0f, 0f);
        }

        public void ApplyClass(CharacterClassData data)
        {
            standEyeHeight = data.standEyeHeight;
            crouchEyeHeight = data.crouchEyeHeight;
        }

        public void ConfigureSensitivity(float value) => sensitivity = Mathf.Max(0.05f, value);

        private void LateUpdate()
        {
            if (input == null) return;

            Vector2 look = input.Look;
            if (look.sqrMagnitude > 0.0001f)
            {
                float deltaTimeMultiplier = look.sqrMagnitude > 1f ? 1f : Time.deltaTime * gamepadTurnSpeed;
                float scale = sensitivity * 0.4f;

                _yaw += look.x * scale * deltaTimeMultiplier;
                _pitch -= look.y * scale * deltaTimeMultiplier;
                _pitch = Mathf.Clamp(_pitch, minPitch, maxPitch);

                transform.rotation = Quaternion.Euler(0f, _yaw, 0f);
                if (lookPivot != null) lookPivot.localRotation = Quaternion.Euler(_pitch, 0f, 0f);
            }

            float targetEye = movement != null && movement.IsCrouched ? crouchEyeHeight : standEyeHeight;
            if (lookPivot != null)
            {
                Vector3 local = lookPivot.localPosition;
                local.y = Mathf.Lerp(local.y, targetEye, Time.deltaTime * eyeLerpSpeed);
                lookPivot.localPosition = local;
            }
        }
    }
}
