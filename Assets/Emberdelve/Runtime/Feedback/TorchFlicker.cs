using UnityEngine;

namespace Emberdelve.Feedback
{
    /// <summary>
    /// Simple torch flicker for point lights (level sconces and the player's hand light).
    /// Uses layered sine noise instead of Random so the look stays steady and cheap.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class TorchFlicker : MonoBehaviour
    {
        [SerializeField] private Light lightSource;
        [SerializeField] private float baseIntensity = 14f;
        [SerializeField] private float flickerAmount = 0.22f;
        [SerializeField] private float speed = 6f;
        [SerializeField] private bool affectRange = true;
        [SerializeField] private float rangeWobble = 0.06f;

        private float _seed;
        private float _baseRange;
        private Camera _handCamera;
        private float _nearScale = 1f;

        /// <summary>Intensity multiplier of the hand torch right now (1 = nothing close). Exposed for the gate.</summary>
        public float NearScale => _nearScale;

        // The hand torch sits 0.25 m from the camera. A point light that close to a surface the player is facing
        // (an enemy at contact, a wall) is inverse-square blown out to white and fills the screen (P4a 21/22,
        // P4c). Within NearDistance of what the camera faces, the intensity is scaled so the lit surface never
        // receives more light than it would at NearDistance. Farther away nothing changes.
        private const float NearDistance = 2.5f;
        private const float TorchForwardOffset = 0.25f;

        private void Awake()
        {
            if (lightSource == null) lightSource = GetComponent<Light>();
            if (lightSource == null)
            {
                enabled = false;
                return;
            }

            _seed = Random.value * 100f;
            _baseRange = lightSource.range;
            baseIntensity = lightSource.intensity;
            _handCamera = transform.parent != null ? transform.parent.GetComponent<Camera>() : null;
        }

        private void Start()
        {
            // The level builder drops each sconce at its cell centre, i.e. floating in the middle of a corridor, where
            // the first-person camera walks through it and the near plane turns its emissive box into screen-filling
            // quads (P4a/P4c). Move the visible sconce onto the nearest wall; the light stays where it is, so the
            // lighting is unchanged. The sconce has no collider, so no combat or movement query is affected.
            Transform sconce = transform.Find("Sconce");
            if (sconce != null) MountOnNearestWall(sconce);
        }

        private void MountOnNearestWall(Transform sconce)
        {
            Vector3[] directions = { Vector3.forward, Vector3.back, Vector3.left, Vector3.right };
            float best = MaxWallDistance;
            int levelMask = LayerMask.GetMask(Emberdelve.Core.GameConfig.LevelLayerName);
            Vector3 bestPoint = Vector3.zero;
            Vector3 bestDirection = Vector3.zero;
            bool found = false;

            for (int i = 0; i < directions.Length; i++)
            {
                if (!Physics.Raycast(transform.position, directions[i], out RaycastHit hit, best, levelMask, QueryTriggerInteraction.Ignore)) continue;
                if (hit.distance >= best) continue;

                best = hit.distance;
                bestPoint = hit.point;
                bestDirection = directions[i];
                found = true;
            }

            if (!found) return;
            sconce.position = bestPoint - bestDirection * (WallInset);
        }


        private void UpdateNearScale()
        {
            Transform cam = _handCamera.transform;
            float target = 1f;
            if (Physics.SphereCast(cam.position, 0.3f, cam.forward, out RaycastHit hit, NearDistance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            {
                float d = hit.distance + 0.3f + TorchForwardOffset;
                float reference = NearDistance + TorchForwardOffset;
                target = Mathf.Clamp((d * d) / (reference * reference), 0.04f, 1f);
            }
            _nearScale = Mathf.Lerp(_nearScale, target, 1f - Mathf.Exp(-12f * Time.unscaledDeltaTime));
        }
        private const float WallInset = 0.12f;
        // Level geometry only (never enemies, chests or props); a torch with no wall this close keeps its sconce where it is.
        private const float MaxWallDistance = 6f;

        private void Update()
        {
            if (lightSource == null) return;

            float t = Time.unscaledTime * speed + _seed;
            float noise = 0.6f * Mathf.Sin(t) + 0.3f * Mathf.Sin(t * 2.7f) + 0.15f * Mathf.Sin(t * 5.3f);

            if (_handCamera != null) UpdateNearScale();
            lightSource.intensity = baseIntensity * (1f + noise * flickerAmount) * _nearScale;

            if (affectRange) lightSource.range = _baseRange * (1f + noise * rangeWobble);
        }
    }
}
