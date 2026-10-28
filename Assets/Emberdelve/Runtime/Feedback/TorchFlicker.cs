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

        // V1 (level sconces only; all off for the hand torch): once the sconce is mounted on a wall the light moves to the
        // flame, and lightJitter (metres) wobbles it a little so the soft shadows breathe with the flame.
        [SerializeField] private Transform flameAnchor;
        [SerializeField] private bool lightFollowsFlame;
        [SerializeField] private float lightJitter;

        private float _seed;
        private float _baseRange;
        private Camera _handCamera;
        private float _nearScale = 1f;
        private Transform _mountedSconce;
        private Vector3 _mountedSconcePosition;
        private Vector3 _lightBasePosition;

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
            // quads (P4a/P4c). Move the visible sconce onto the nearest wall. Plain torches (and the lobby/hand torch)
            // keep their light where it is; V1 level sconces opt in to lightFollowsFlame and the light moves to the flame
            // (editor-time positions are unchanged). The sconce has no collider, so no combat or movement query is affected.
            Transform sconce = transform.Find("Sconce");
            if (sconce == null) return;

            if (MountOnNearestWall(sconce) && lightFollowsFlame && flameAnchor != null)
            {
                // Move the light to the flame, keep the sconce where it was just mounted (it is a child of this object).
                Vector3 sconcePosition = sconce.position;
                Quaternion sconceRotation = sconce.rotation;
                transform.position = flameAnchor.position;
                sconce.SetPositionAndRotation(sconcePosition, sconceRotation);

                _mountedSconce = sconce;
                _mountedSconcePosition = sconcePosition;
                _lightBasePosition = transform.position;
            }
        }

        private bool MountOnNearestWall(Transform sconce)
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

            if (!found) return false;
            sconce.position = bestPoint - bestDirection * (WallInset);
            // Local +Z of the sconce points away from the wall into the room.
            sconce.rotation = Quaternion.LookRotation(-bestDirection, Vector3.up);
            return true;
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

            if (_mountedSconce != null && lightJitter > 0f)
            {
                Vector3 offset = new Vector3(Mathf.Sin(t * 1.7f), 0.6f * Mathf.Sin(t * 2.3f + 1.1f), Mathf.Sin(t * 1.3f + 2.2f)) * lightJitter;
                transform.position = _lightBasePosition + offset;
                _mountedSconce.position = _mountedSconcePosition; // the sconce is our child: pin it back
            }
        }
    }
}
