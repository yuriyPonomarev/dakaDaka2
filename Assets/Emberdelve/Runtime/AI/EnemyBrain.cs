using System.Collections.Generic;
using UnityEngine;
using Emberdelve.Core;
using Emberdelve.Combat;
using Emberdelve.Data;

namespace Emberdelve.AI
{
    public enum EnemyState { Patrol, Investigate, Chase, Attack, Stunned, Dead }

    /// <summary>What the brain decided. EnemyAgent turns it into movement and an attack intent.</summary>
    public readonly struct BrainOrder
    {
        public readonly Vector3 MoveTo;
        public readonly float Speed;
        public readonly bool RequestAttack;
        public readonly Vector3 FaceTowards;

        public BrainOrder(Vector3 moveTo, float speed, bool requestAttack, Vector3 faceTowards)
        {
            MoveTo = moveTo;
            Speed = Mathf.Max(0f, speed);
            RequestAttack = requestAttack;
            FaceTowards = faceTowards;
        }

        public static BrainOrder Hold(Vector3 self) => new BrainOrder(self, 0f, false, self);
    }

    /// <summary>
    /// Enemy decision making as a plain class: perception, memory, patrol, chase, attack timing.
    /// It never applies damage and never moves a transform, which keeps behaviour easy to reason
    /// about and easy to move server-side later.
    /// </summary>
    public sealed class EnemyBrain
    {
        private readonly EnemyData _data;
        private readonly INavigationProvider _nav;
        private readonly LayerMask _levelMask;
        private readonly Vector3 _home;
        private readonly List<Vector3> _patrolPoints = new List<Vector3>(6);

        private EnemyState _state = EnemyState.Patrol;
        private Vector3 _lastKnownPosition;
        private float _lastSeenAt = float.NegativeInfinity;
        private float _investigateUntil;
        private int _patrolIndex;
        private Vector3 _patrolTarget;
        private float _attackCooldownLeft;
        private float _windupLeft;
        private bool _windingUp;
        private bool _attackConsumed;
        private Vector3 _telegraphDirection;
        private int _lastHeardTargetId;
        private float _lastHeardAt = float.NegativeInfinity;

        public EnemyBrain(EnemyData data, INavigationProvider nav, Vector3 home, LayerMask levelMask)
        {
            _data = data;
            _nav = nav;
            _home = home;
            _levelMask = levelMask;
            _lastKnownPosition = home;
            BuildPatrolPoints();
        }

        public EnemyState State => _state;
        public bool IsWindingUp => _windingUp;
        public bool IsStunned => _state == EnemyState.Stunned;
        public float WindupProgress => _windingUp ? 1f - Mathf.Clamp01(_windupLeft / Mathf.Max(0.05f, _data.attackWindup)) : 0f;
        public float WindupRemaining => Mathf.Max(0f, _windupLeft);
        public Vector3 LastKnownPosition => _lastKnownPosition;
        public Vector3 PatrolTarget => _patrolTarget;

        /// <summary>
        /// The direction committed when the windup started, flattened to the ground plane. The swept swing (D1c4) is
        /// aimed along this same vector, so the telegraph the player sees is the hit that arrives -
        /// stepping out of the warned wedge is a real dodge, not a coincidence.
        /// Zero until a windup has happened at least once.
        /// </summary>
        public Vector3 TelegraphDirection => _telegraphDirection;

        private void BuildPatrolPoints()
        {
            _patrolPoints.Clear();
            float radius = Mathf.Max(1f, _data.patrolRadius);

            for (int i = 0; i < 4; i++)
            {
                float angle = (i / 4f) * Mathf.PI * 2f + Random.value * 0.6f;
                Vector3 candidate = _home + new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
                if (_nav == null || _nav.IsWalkableWorld(candidate)) _patrolPoints.Add(candidate);
            }

            if (_patrolPoints.Count == 0) _patrolPoints.Add(_home);
            _patrolTarget = _patrolPoints[0];
        }

        public void NotifyDamaged(DamageInfo damage)
        {
            _lastKnownPosition = damage.SourcePosition;
            _lastSeenAt = Time.time;

            if (_state == EnemyState.Patrol || _state == EnemyState.Investigate) _state = EnemyState.Chase;
        }

        public BrainOrder Tick(float dt, float now, Vector3 self, Vector3 facing, ICombatEntity target)
        {
            if (_state == EnemyState.Dead) return BrainOrder.Hold(self);

            _attackCooldownLeft = Mathf.Max(0f, _attackCooldownLeft - dt);

            if (_state == EnemyState.Stunned) return BrainOrder.Hold(self);

            bool targetAlive = target != null && !target.IsDead;
            Vector3 targetCenter = targetAlive ? target.CombatCenter : _lastKnownPosition;

            bool visible = targetAlive && CanSee(self, facing, targetCenter);
            bool heard = targetAlive && DistanceXZ(self, targetCenter) <= _data.hearingRange;
            if (!heard && target != null && _lastHeardTargetId == target.CombatId) _lastHeardTargetId = 0;
            bool newSound = heard && _lastHeardTargetId != target.CombatId;
            if (newSound)
            {
                _lastHeardTargetId = target.CombatId;
                _lastHeardAt = now;
            }
            bool heardWithinMemory = heard && now - _lastHeardAt <= _data.memoryDuration;

            if (visible)
            {
                _lastKnownPosition = targetCenter;
                _lastSeenAt = now;
            }
            else if (newSound)
            {
                // Hearing supplies one last-known search point even when a closed door blocks sight. Do not
                // refresh it every frame: that would reveal and track an occluded player's exact position indefinitely.
                _lastKnownPosition = targetCenter;
            }

            if (targetAlive && (visible || heardWithinMemory) && _state != EnemyState.Chase && _state != EnemyState.Attack)
            {
                _state = EnemyState.Chase;
            }

            float distanceToTarget = DistanceXZ(self, _lastKnownPosition);

            switch (_state)
            {
                case EnemyState.Patrol:
                    return TickPatrol(self, dt);

                case EnemyState.Investigate:
                    if (now > _investigateUntil)
                    {
                        _state = EnemyState.Patrol;
                        _patrolTarget = PickPatrolPoint();
                    }
                    return TickMoveTo(self, _lastKnownPosition, _data.patrolSpeed);

                case EnemyState.Chase:
                    return TickChase(self, facing, targetAlive ? target : null, targetCenter, visible, dt, now);

                case EnemyState.Attack:
                    return TickAttack(self, facing, targetAlive ? target : null, targetCenter, visible, dt);
            }

            return BrainOrder.Hold(self);
        }

        private BrainOrder TickPatrol(Vector3 self, float dt)
        {
            if (DistanceXZ(self, _patrolTarget) < 1.1f) _patrolTarget = PickPatrolPoint();
            return TickMoveTo(self, _patrolTarget, _data.patrolSpeed);
        }

        private BrainOrder TickChase(Vector3 self, Vector3 facing, ICombatEntity target, Vector3 targetCenter, bool visible, float dt, float now)
        {
            if (target == null)
            {
                _state = EnemyState.Investigate;
                _investigateUntil = now + Mathf.Max(0.5f, _data.investigateTime);
                return TickMoveTo(self, _lastKnownPosition, _data.patrolSpeed);
            }

            float distance = DistanceXZ(self, targetCenter);

            if (distance <= _data.attackRange && visible)
            {
                _state = EnemyState.Attack;
                _windingUp = false;
                _attackConsumed = false;
                _windupLeft = 0f;
            }

            if (!visible && now - Mathf.Max(_lastSeenAt, _lastHeardAt) > _data.memoryDuration)
            {
                _state = EnemyState.Investigate;
                _investigateUntil = now + Mathf.Max(0.5f, _data.investigateTime);
                return TickMoveTo(self, _lastKnownPosition, _data.patrolSpeed);
            }

            Vector3 destination = visible ? targetCenter : _lastKnownPosition;
            float stopDistance = _data.attackRange * 0.75f;

            if (distance <= stopDistance)
            {
                return new BrainOrder(self, 0f, false, targetCenter);
            }

            BrainOrder move = TickMoveTo(self, destination, _data.chaseSpeed);
            return new BrainOrder(move.MoveTo, move.Speed, false, targetCenter);
        }

        private BrainOrder TickAttack(Vector3 self, Vector3 facing, ICombatEntity target, Vector3 targetCenter, bool visible, float dt)
        {
            if (target == null)
            {
                _state = EnemyState.Chase;
                return BrainOrder.Hold(self);
            }

            float distance = DistanceXZ(self, targetCenter);

            if (!visible || distance > _data.attackRange * 1.35f)
            {
                _state = EnemyState.Chase;
                _windingUp = false;
                return BrainOrder.Hold(self);
            }

            BrainOrder faceOnly = new BrainOrder(self, 0f, false, targetCenter);

            if (_attackCooldownLeft > 0f)
            {
                if (_windingUp)
                {
                    _windupLeft -= dt;
                    if (_windupLeft <= 0f) _windingUp = false;
                }

                return faceOnly;
            }

            if (!_windingUp)
            {
                _windingUp = true;
                _windupLeft = Mathf.Max(0.05f, _data.attackWindup);
                _attackConsumed = false;

                // Commit the warned direction now, while the player can still read it and react.
                _telegraphDirection = FlattenDirection(targetCenter - self);
                return faceOnly;
            }

            _windupLeft -= dt;

            if (_windupLeft <= 0f)
            {
                _windingUp = false;
                _attackCooldownLeft = Mathf.Max(0.2f, _data.attackCooldown);

                if (_attackConsumed) return faceOnly;
                _attackConsumed = true;

                return new BrainOrder(self, 0f, true, targetCenter);
            }

            return faceOnly;
        }

        private BrainOrder TickMoveTo(Vector3 self, Vector3 destination, float speed)
        {
            if (_nav == null) return new BrainOrder(destination, speed, false, destination);
            return new BrainOrder(destination, speed, false, destination);
        }

        private Vector3 PickPatrolPoint()
        {
            if (_patrolPoints.Count == 0) return _home;
            _patrolIndex = (_patrolIndex + 1) % _patrolPoints.Count;
            return _patrolPoints[_patrolIndex];
        }

        /// <summary>
        /// D1c4: puts the brain into (or takes it out of) the Stunned state. A stunned brain holds still, winds nothing up and
        /// requests no attack; EnemyAgent reports no active weapon while stunned, so the authority ends a swing the stun
        /// interrupted. Nothing in the game stuns an enemy yet (the state existed but was never entered); this is the seam a
        /// stun source will use, and the gates pin the swing cancel through it. Leaving the stun resumes with a chase.
        /// </summary>
        public void SetStunned(bool stunned)
        {
            if (_state == EnemyState.Dead) return;

            if (stunned)
            {
                _state = EnemyState.Stunned;
                _windingUp = false;
                _windupLeft = 0f;
            }
            else if (_state == EnemyState.Stunned)
            {
                _state = EnemyState.Chase;
            }
        }

        public void MarkDead()
        {
            _state = EnemyState.Dead;
            _windingUp = false;
        }

        private bool CanSee(Vector3 self, Vector3 facing, Vector3 targetCenter)
        {
            Vector3 eye = self + Vector3.up * 1.35f;
            Vector3 toTarget = targetCenter - eye;
            float distance = toTarget.magnitude;

            if (distance > Mathf.Max(1f, _data.sightRange)) return false;

            Vector3 flatFacing = new Vector3(facing.x, 0f, facing.z);
            Vector3 flatTo = new Vector3(toTarget.x, 0f, toTarget.z);

            if (flatTo.sqrMagnitude > 0.04f && flatFacing.sqrMagnitude > 0.0001f)
            {
                float angle = Vector3.Angle(flatFacing, flatTo);
                if (angle > Mathf.Max(20f, _data.sightCone * 0.5f) && distance > 3.5f) return false;
            }

            return HitScanner.HasLineOfSight(eye, targetCenter, _levelMask, 0.3f);
        }

        private static float DistanceXZ(Vector3 a, Vector3 b)
        {
            Vector3 delta = a - b;
            delta.y = 0f;
            return delta.magnitude;
        }

        private static Vector3 FlattenDirection(Vector3 direction)
        {
            Vector3 flat = new Vector3(direction.x, 0f, direction.z);
            return flat.sqrMagnitude < 0.0001f ? Vector3.forward : flat.normalized;
        }
    }
}
