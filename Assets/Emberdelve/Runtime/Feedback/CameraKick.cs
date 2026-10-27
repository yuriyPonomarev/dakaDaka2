using UnityEngine;
using UnityEngine.Rendering;

namespace Emberdelve.Feedback
{
    /// <summary>
    /// B2: a purely visual camera impulse (wall-stop jolt). The offset lives in this component and is applied to the
    /// transform ONLY inside the camera's own render window (beginCameraRendering ... endCameraRendering), then the exact
    /// saved local pose is written back. Everything that reads the transform outside rendering (PlayerLook, the combat
    /// authority's aim pose, movement, the weapon rig) therefore never sees the kick, and PlayerLook's yaw / pitch fields
    /// are never touched. Decays exponentially and snaps to exactly zero.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CameraKick : MonoBehaviour
    {
        /// <summary>Pitch kicks never add up past this many degrees (positive tilts the view down).</summary>
        public const float MaxPitchDegrees = 3f;

        /// <summary>The position kick (local space of the transform) never exceeds this many metres.</summary>
        public const float MaxOffsetMetres = 0.08f;

        /// <summary>Exponential decay rate per second (14 per second: 95 percent gone after about 0.21 s).</summary>
        public const float DecayPerSecond = 14f;

        private const float Epsilon = 0.002f;

        private float _pitch;
        private Vector3 _offset;
        private bool _applied;
        private Vector3 _savedPosition;
        private Quaternion _savedRotation;
        private Camera _camera;

        /// <summary>Current pitch kick in degrees (0 at rest).</summary>
        public float PitchDegrees => _pitch;

        /// <summary>Current local position kick in metres (zero at rest).</summary>
        public Vector3 LocalOffset => _offset;

        public bool IsActive => _pitch != 0f || _offset != Vector3.zero;

        /// <summary>True between <see cref="BeginRender"/> and <see cref="EndRender"/>.</summary>
        public bool IsApplied => _applied;

        private void Awake()
        {
            _camera = GetComponent<Camera>();
            if (_camera == null) _camera = GetComponentInChildren<Camera>(true);
        }

        private void OnEnable()
        {
            RenderPipelineManager.beginCameraRendering += OnBeginCamera;
            RenderPipelineManager.endCameraRendering += OnEndCamera;
        }

        private void OnDisable()
        {
            RenderPipelineManager.beginCameraRendering -= OnBeginCamera;
            RenderPipelineManager.endCameraRendering -= OnEndCamera;
            EndRender();
        }

        private void Update()
        {
            Tick(Time.unscaledDeltaTime);
        }

        /// <summary>Adds an impulse (clamped to the maxima). Allocation free.</summary>
        public void Impulse(float pitchDegrees, Vector3 localOffset)
        {
            _pitch = Mathf.Clamp(_pitch + pitchDegrees, -MaxPitchDegrees, MaxPitchDegrees);
            Vector3 sum = _offset + localOffset;
            _offset = sum.sqrMagnitude > MaxOffsetMetres * MaxOffsetMetres ? sum.normalized * MaxOffsetMetres : sum;
        }

        /// <summary>Decays the kick by <paramref name="deltaTime"/> seconds; below a tiny threshold it snaps to exactly zero.</summary>
        public void Tick(float deltaTime)
        {
            if (!IsActive) return;

            float keep = Mathf.Exp(-DecayPerSecond * Mathf.Max(0f, deltaTime));
            _pitch *= keep;
            _offset *= keep;
            if (Mathf.Abs(_pitch) < Epsilon && _offset.sqrMagnitude < 1e-8f)
            {
                _pitch = 0f;
                _offset = Vector3.zero;
            }
        }

        /// <summary>Applies the kick to the transform for rendering. Idempotent until <see cref="EndRender"/>.</summary>
        public void BeginRender()
        {
            if (_applied || !IsActive) return;

            _savedPosition = transform.localPosition;
            _savedRotation = transform.localRotation;
            transform.localRotation = _savedRotation * Quaternion.Euler(_pitch, 0f, 0f);
            transform.localPosition = _savedPosition + _savedRotation * _offset;
            _applied = true;
        }

        /// <summary>Restores the exact local pose saved by <see cref="BeginRender"/>.</summary>
        public void EndRender()
        {
            if (!_applied) return;

            transform.localPosition = _savedPosition;
            transform.localRotation = _savedRotation;
            _applied = false;
        }

        private void OnBeginCamera(ScriptableRenderContext context, Camera camera)
        {
            if (camera != null && camera == _camera) BeginRender();
        }

        private void OnEndCamera(ScriptableRenderContext context, Camera camera)
        {
            if (camera != null && camera == _camera) EndRender();
        }
    }
}
