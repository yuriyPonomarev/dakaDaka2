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

    [DisallowMultipleComponent]
    public sealed class WorldFootsteps : MonoBehaviour
    {
        public bool Audible { get; set; } = true;
        public bool? ReplicatedCrouched { get; set; }
        public bool IsCrouched => ReplicatedCrouched ?? (_player != null && _player.Movement != null && _player.Movement.IsCrouched);
        public float StepGain => IsCrouched ? .13f : .5f;
        public float StepStride => IsCrouched ? 1.05f : 1.45f;
        public int StepRequests => _step;
        public float LastRequestedGain { get; private set; }
        private PlayerRig _player;
        private EnemyAgent _enemy;
        private FootstepCadence _cadence;
        private int _mask;
        private int _step;
        private void Awake()
        {
            _player = GetComponent<PlayerRig>();
            _enemy = GetComponent<EnemyAgent>();
            _mask = LayerMask.GetMask(GameConfig.LevelLayerName);
        }
        private void LateUpdate()
        {
            bool alive = Audible && (_player == null || !_player.IsDead) &&
                (_enemy == null || (!_enemy.IsDead && !_enemy.IsDormant && !_enemy.IsWaking));
            bool grounded = Physics.Raycast(transform.position + Vector3.up * .25f, Vector3.down,
                .55f, _mask, QueryTriggerInteraction.Ignore);
            if (!_cadence.Advance(transform.position, Time.deltaTime, grounded, alive, StepStride)) return;
            _step++;
            LastRequestedGain = StepGain;
            WorldAudio.Play(WorldSound.Step, transform.position + Vector3.up * .12f,
                LastRequestedGain, .94f + (_step % 5) * .025f);
        }
        private void OnDisable() { _cadence = default; ReplicatedCrouched = null; }
    }
}
