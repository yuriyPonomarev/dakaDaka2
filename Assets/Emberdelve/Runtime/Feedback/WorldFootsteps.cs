using System;
using UnityEngine;
using Emberdelve.AI;
using Emberdelve.Core;
using Emberdelve.Player;

namespace Emberdelve.Feedback
{
    /// <summary>Distance-based cadence: standing, airborne movement and teleport corrections cannot produce steps.</summary>
    public struct FootstepCadence
    {
        private Vector3 _previous;
        private bool _seeded;
        private float _distance;
        private float _sinceStep;
        public bool Advance(Vector3 position, float dt, bool grounded, bool audible, float stride = 1.45f)
        {
            Vector3 delta = position - _previous;
            _previous = position;
            if (!_seeded) { _seeded = true; return false; }
            delta.y = 0f;
            float distance = delta.magnitude;
            if (!audible || !grounded || dt <= 0f || dt > .25f || distance > 12f * dt)
            { _distance = 0f; _sinceStep = 0f; return false; }
            _sinceStep += dt;
            _distance += distance;
            if (_distance < Mathf.Max(.4f, stride) || _sinceStep < .15f) return false;
            _distance %= Mathf.Max(.4f, stride);
            _sinceStep = 0f;
            return true;
        }
    }

    /// <summary>Presentation fallback for existing authored surfaces; no physics material or collider is changed.</summary>
    public static class FootstepSurface
    {
        public static WorldSound Classify(string name) => TryClassify(name, out WorldSound sound) ? sound : WorldSound.Step;

        public static WorldSound FromHit(in RaycastHit hit)
        {
            Collider collider = hit.collider;
            if (collider == null) return WorldSound.Step;
            if (collider.sharedMaterial != null && TryClassify(collider.sharedMaterial.name, out WorldSound sound)) return sound;
            var renderer = collider.GetComponent<Renderer>();
            if (renderer == null) renderer = collider.GetComponentInChildren<Renderer>();
            if (renderer != null)
                foreach (Material material in renderer.sharedMaterials)
                    if (material != null && TryClassify(material.name, out sound)) return sound;
            Transform surface = collider.transform;
            for (int depth = 0; surface != null && depth < 3; depth++, surface = surface.parent)
                if (TryClassify(surface.name, out sound)) return sound;
            return WorldSound.Step;
        }

        private static bool TryClassify(string name, out WorldSound sound)
        {
            sound = WorldSound.Step;
            if (string.IsNullOrEmpty(name)) return false;
            // A mossy stone material remains masonry. Unknown surfaces also fall back to stone.
            if (Contains(name, "stone") || Contains(name, "brick") || Contains(name, "marble") || Contains(name, "rock")) return true;
            if (Contains(name, "wood") || Contains(name, "timber") || Contains(name, "plank") || Contains(name, "barrel") || Contains(name, "crate"))
            { sound = WorldSound.StepWood; return true; }
            if (Contains(name, "dirt") || Contains(name, "soil") || Contains(name, "earth") || Contains(name, "mud") ||
                Contains(name, "sand") || Contains(name, "grass") || Contains(name, "gravel"))
            { sound = WorldSound.StepDirt; return true; }
            return false;
        }

        private static bool Contains(string name, string token) => name.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    [DisallowMultipleComponent]
    public sealed class WorldFootsteps : MonoBehaviour
    {
        public bool Audible { get; set; } = true;
        public bool? ReplicatedCrouched { get; set; }
        public bool IsCrouched => ReplicatedCrouched ?? (_player != null && _player.Movement != null && _player.Movement.IsCrouched);
        public float StepGain => IsCrouched ? .13f : .5f;
        public float StepStride => IsCrouched ? 1.05f : 1.45f;
        public int StepRequests => _step;
        public int JumpRequests { get; private set; }
        public int LandingRequests { get; private set; }
        public float LastRequestedGain { get; private set; }
        public WorldSound LastRequestedSurface { get; private set; } = WorldSound.Step;
        private PlayerRig _player;
        private EnemyAgent _enemy;
        private FootstepCadence _cadence;
        private int _mask;
        private int _step;
        private Vector3 _previousPosition;
        private bool _motionSeeded;
        private bool _lifeSeeded, _wasAlive;
        private bool _wasGrounded;
        private bool _suppressUntilGrounded;
        private uint _floorGeneration;
        private float _airTime;
        private float _peakFallSpeed;
        private float _airPeakY;
        private uint _variation;
        private void Awake()
        {
            _player = GetComponent<PlayerRig>();
            if (_player != null)
            {
                var actions = GetComponent<PlayerActionAudio>();
                if (actions == null) actions = gameObject.AddComponent<PlayerActionAudio>();
                actions.Bind(_player);
            }
            _enemy = GetComponent<EnemyAgent>();
            _mask = LayerMask.GetMask(GameConfig.LevelLayerName);
            _variation = unchecked((uint)GetInstanceID()) | 1u;
        }
        private void LateUpdate()
        {
            if (_player != null)
            {
                bool playerAlive = !_player.IsDead;
                // One physical death voice per actor, from either authority health or replicated vitals.
                // A body already dead on the first observation never replays its death.
                if (_lifeSeeded && _wasAlive && !playerAlive && Audible)
                    WorldAudio.PlayAttached(WorldSound.Death, _player.transform, Vector3.up * 1.4f, .8f);
                _wasAlive = playerAlive;
                _lifeSeeded = true;
            }
            Vector3 position = transform.position;
            float dt = Time.deltaTime;
            bool alive = Audible && (_player == null || (!_player.IsDead && _player.IsInPlay)) &&
                (_enemy == null || (!_enemy.IsDead && !_enemy.IsDormant && !_enemy.IsWaking));
            bool grounded = Physics.Raycast(position + Vector3.up * .25f, Vector3.down,
                out RaycastHit ground, .55f, _mask, QueryTriggerInteraction.Ignore) && ground.normal.y >= .55f;
            uint generation = _player != null ? _player.FloorMovementGeneration : 0;
            Vector3 delta = position - _previousPosition;
            float planarDistance = new Vector2(delta.x, delta.z).magnitude;
            bool discontinuity = dt <= 0f || dt > .25f || planarDistance > 12f * dt ||
                delta.y > Mathf.Max(.3f, 18f * dt) || -delta.y > Mathf.Max(.3f, 65f * dt) ||
                (_wasGrounded && -delta.y > Mathf.Max(.25f, 10f * dt));
            if (!alive || !_motionSeeded || generation != _floorGeneration || discontinuity)
            {
                ResetMotion(position, grounded, generation);
                return;
            }

            _previousPosition = position;
            float verticalSpeed = delta.y / dt;
            bool landing = false;
            if (!grounded)
            {
                if (_wasGrounded)
                {
                    _airTime = 0f;
                    _peakFallSpeed = 0f;
                    _airPeakY = Mathf.Max(position.y, position.y - delta.y);
                    // This also works on interpolated remote rigs, whose movement component is disabled.
                    // Walking off a ledge has no upward takeoff and must remain silent.
                    bool upwardTakeoff = verticalSpeed > 1.2f;
                    if (_player != null && _player.Movement != null && (!NetRole.IsClientOnly || _player.IsLocallyControlled))
                    {
                        _player.Movement.CapturePredictionState(out var motion);
                        upwardTakeoff &= motion.VerticalVelocity > 0f;
                    }
                    if (!_suppressUntilGrounded && upwardTakeoff)
                    {
                        JumpRequests++;
                        WorldAudio.PlayAttached(WorldSound.Jump, transform, Vector3.up * .35f, .42f, VaryPitch());
                        WorldAudio.PlayAttached(WorldSound.Cloth, transform, Vector3.up, .18f, VaryPitch());
                    }
                }
                _airTime += dt;
                _peakFallSpeed = Mathf.Max(_peakFallSpeed, -verticalSpeed);
                _airPeakY = Mathf.Max(_airPeakY, position.y);
            }
            else
            {
                if (!_wasGrounded && !_suppressUntilGrounded && _airTime >= .1f &&
                    _peakFallSpeed >= 2.5f && _airPeakY - position.y >= .28f)
                {
                    landing = true;
                    LandingRequests++;
                    bool heavy = _peakFallSpeed >= 7.5f;
                    float gain = Mathf.Lerp(.35f, .9f, Mathf.InverseLerp(2.5f, 12f, _peakFallSpeed));
                    LastRequestedSurface = FootstepSurface.FromHit(in ground);
                    WorldAudio.Play(heavy ? WorldSound.LandHeavy : WorldSound.Land, ground.point + Vector3.up * .12f, gain, VaryPitch());
                    WorldAudio.Play(LastRequestedSurface, ground.point + Vector3.up * .12f, gain * .55f, VaryPitch());
                }
                _suppressUntilGrounded = false;
                _airTime = _peakFallSpeed = 0f;
                _airPeakY = position.y;
            }
            _wasGrounded = grounded;
            float speed = planarDistance / dt;
            float stride = StepStride * Mathf.Lerp(.9f, 1.2f, Mathf.InverseLerp(1.5f, 6f, speed));
            if (!_cadence.Advance(position, dt, grounded && !landing, true, stride)) return;
            _step++;
            LastRequestedGain = StepGain * Mathf.Lerp(.7f, 1.2f, Mathf.InverseLerp(1f, 6f, speed));
            LastRequestedSurface = FootstepSurface.FromHit(in ground);
            WorldAudio.Play(LastRequestedSurface, ground.point + Vector3.up * .12f, LastRequestedGain, VaryPitch());
        }

        private float VaryPitch()
        {
            // Local presentation variation never consumes UnityEngine.Random or the gameplay random stream.
            _variation ^= _variation << 13; _variation ^= _variation >> 17; _variation ^= _variation << 5;
            return .93f + (_variation & 1023u) / 1023f * .14f;
        }

        private void ResetMotion(Vector3 position, bool grounded, uint generation)
        {
            _previousPosition = position;
            _motionSeeded = true;
            _wasGrounded = grounded;
            _floorGeneration = generation;
            _suppressUntilGrounded = !grounded;
            _airTime = _peakFallSpeed = 0f;
            _airPeakY = position.y;
            _cadence = default;
            _cadence.Advance(position, 0f, grounded, false);
        }

        private void OnDisable()
        {
            _cadence = default;
            _motionSeeded = false;
            _lifeSeeded = false;
            ReplicatedCrouched = null;
        }
    }
}
