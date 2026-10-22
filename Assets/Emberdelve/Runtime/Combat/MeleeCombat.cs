using System;
using UnityEngine;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.InputLayer;
using Emberdelve.Player;

namespace Emberdelve.Combat
{
    /// <summary>
    /// Thin melee client (D1a). On the primary button it asks the combat authority to begin a swept swing
    /// (<see cref="ICombatAuthority.TryBeginMeleeSwing"/>) and plays the matching first-person weapon motion.
    /// It does not scan, overlap, measure an arc or line of sight and it hands the authority no hit point,
    /// range, timestamp or head flag: the authority sweeps the pattern's weapon segment through the hit volumes
    /// on its own tick. The local windup/active/recovery timers only drive the animation, the swinging flag the
    /// guard rule and the HUD cooldown readout; when the authority reports that level geometry stopped the
    /// swing, the client enters its recoil.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(PlayerRig))]
    public sealed class MeleeCombat : MonoBehaviour
    {
        private enum Phase { Idle, Windup, Active, Recovery }

        [Header("Wiring")]
        [SerializeField] private PlayerRig rig;
        [SerializeField] private GameConfig config;

        private WeaponData _weapon;
        private SwingPatternData _pattern;
        private Phase _phase = Phase.Idle;
        private float _phaseTimer;
        private float _readyTime;
        private float _swingStartedAt;
        private int _swingId;
        private bool _recoilStarted;
        private bool _slashFromRight;

        /// <summary>Raised when the authority accepted a swing; carries the plain-data request that was sent.</summary>
        public event Action<MeleeSwingIntent> AttackStarted;

        public WeaponData Weapon => _weapon;
        public bool IsSwinging => _phase != Phase.Idle;

        /// <summary>The pattern of the swing in progress (or the last one).</summary>
        public SwingPatternData ActivePattern => _pattern;

        /// <summary>True once the authority reported a wall stop for the current swing and the client entered its recoil.</summary>
        public bool RecoilStarted => _recoilStarted;

        /// <summary>Authority id of the swing in progress (or the last one); 0 before the first accepted swing.</summary>
        public int LastSwingId => _swingId;

        /// <summary>Seconds until the swing state machine would start another swing (0 when ready).</summary>
        public float CooldownRemaining => Mathf.Max(0f, _readyTime - Time.time);

        public float SwingProgress
        {
            get
            {
                if (_pattern == null || _phase == Phase.Idle) return 0f;
                float total = _pattern.TotalTime;
                if (total <= 0f) return 0f;
                return Mathf.Clamp01((Time.time - _swingStartedAt) / total);
            }
        }

        private void Awake()
        {
            if (rig == null) rig = GetComponent<PlayerRig>();
        }

        public void Initialize(WeaponData weapon)
        {
            _weapon = weapon;
            _pattern = null;
            _phase = Phase.Idle;
            _phaseTimer = 0f;
            _readyTime = 0f;
            _swingId = 0;
            _recoilStarted = false;

            if (weapon == null || weapon.IsRanged) enabled = false;
        }

        private void Update()
        {
            if (_weapon == null || rig == null) return;

            EmberdelveInput input = rig.Input;
            if (input == null) return;

            if (rig.IsDead)
            {
                rig.SetGuardHeld(false);
                _phase = Phase.Idle;
                return;
            }

            rig.SetGuardHeld(input.SecondaryHeld && !IsSwinging);

            float dt = Time.deltaTime;

            switch (_phase)
            {
                case Phase.Idle:
                    if (input.PrimaryPressed && !input.SecondaryHeld && Time.time >= _readyTime)
                    {
                        TryBeginSwing(ChoosePatternIndex(input.SprintHeld));
                    }
                    break;

                case Phase.Windup:
                    _phaseTimer -= dt;
                    PollAuthority();
                    if (_phaseTimer <= 0f) EnterActive();
                    break;

                case Phase.Active:
                    _phaseTimer -= dt;
                    PollAuthority();
                    if (_phaseTimer <= 0f && _phase == Phase.Active)
                    {
                        _phase = Phase.Recovery;
                        _phaseTimer = Mathf.Max(0.02f, _pattern.recoveryTime);
                    }
                    break;

                case Phase.Recovery:
                    _phaseTimer -= dt;
                    PollAuthority();
                    if (_phaseTimer <= 0f) _phase = Phase.Idle;
                    break;
            }
        }

        /// <summary>
        /// Starts a swing with the automatically chosen pattern (alternating slashes for a sword, the thrust for
        /// a rapier). Public because input, tools and (later) a networked input source all funnel through the
        /// same entry point; the authority decides whether it is accepted and what it hits.
        /// </summary>
        public void BeginSwing() => TryBeginSwing(ChoosePatternIndex(false));

        /// <summary>
        /// Sends a begin request for one of the weapon's patterns (combo step 0). Returns whether the authority
        /// accepted it; only then does the local animation start. The aim sent is a snapshot the authority
        /// compares with its own copy of the pose, it is not what the sweep uses.
        /// </summary>
        public bool TryBeginSwing(int patternIndex)
        {
            if (_weapon == null || rig == null) return false;

            if (!_weapon.TryResolveSwingPattern(0, patternIndex, out SwingPatternData pattern))
            {
                CombatDiagnostics.Log("melee-nopattern", $"[MeleeCombat] weapon '{_weapon.weaponId}' has no swing pattern {patternIndex}; it cannot be swung.", this);
                return false;
            }

            if (!GameServices.TryGet<ICombatAuthority>(out ICombatAuthority authority))
            {
                Debug.LogError("[MeleeCombat] No ICombatAuthority in the scene; swings cannot deal damage.", this);
                return false;
            }

            if (!rig.TryGetAimPose(out _, out float yaw, out float pitch)) return false;

            // Sequence comes from the combat registry, keyed by combat id: the same player can swing a
            // sword and shoot a crossbow, and a private counter here would make one weapon's first attack
            // look like a replay of the other's.
            int sequence = CombatRegistry.NextSequence(rig.CombatId);
            MeleeSwingIntent intent = new MeleeSwingIntent(rig.CombatId, _weapon.weaponId, patternIndex, 0, yaw, pitch, sequence);

            if (!authority.TryBeginMeleeSwing(intent, out int swingId, out string reason))
            {
                CombatDiagnostics.Log("melee-refused", $"[MeleeCombat] swing #{sequence} ({_weapon.weaponId}) refused by the authority: {reason}", this);
                return false;
            }

            _pattern = pattern;
            _swingId = swingId;
            _swingStartedAt = Time.time;
            _recoilStarted = false;
            _phase = pattern.windupTime > 0f ? Phase.Windup : Phase.Active;
            _phaseTimer = Mathf.Max(0.02f, pattern.windupTime);
            if (_phase == Phase.Active) _phaseTimer = Mathf.Max(0.02f, pattern.activeTime);
            _readyTime = Time.time + Mathf.Max(0.1f, _weapon.cooldown);

            if (pattern.kind == SwingKind.HorizontalLeftToRight) _slashFromRight = true;
            else if (pattern.kind == SwingKind.HorizontalRightToLeft) _slashFromRight = false;

            rig.PlaySwingVisual(_weapon, pattern);
            AttackStarted?.Invoke(intent);

            CombatDiagnostics.Log("melee-swing",
                $"[MeleeCombat] swing #{sequence} ({_weapon.weaponId}, {pattern.patternId}) accepted by the authority as swing {swingId}", this);
            return true;
        }

        /// <summary>
        /// Pattern for the next primary press: Overhead while sprinting (when the weapon has one), otherwise the
        /// horizontal slashes alternate left-to-right / right-to-left, otherwise the weapon's thrust. D1b replaces
        /// this with combo chains; the authority only ever checks that the index is one the weapon allows.
        /// </summary>
        public int ChoosePatternIndex(bool overhead)
        {
            if (_weapon == null) return 0;

            if (overhead)
            {
                int over = _weapon.FindSwingPattern(SwingKind.Overhead);
                if (over >= 0) return over;
            }

            int left = _weapon.FindSwingPattern(SwingKind.HorizontalLeftToRight);
            int right = _weapon.FindSwingPattern(SwingKind.HorizontalRightToLeft);
            if (left >= 0 && right >= 0) return _slashFromRight ? right : left;
            if (left >= 0) return left;
            if (right >= 0) return right;

            int thrust = _weapon.FindSwingPattern(SwingKind.Thrust);
            return thrust >= 0 ? thrust : 0;
        }

        private void EnterActive()
        {
            _phase = Phase.Active;
            _phaseTimer = Mathf.Max(0.02f, _pattern.activeTime);
        }

        /// <summary>Reads the authority's state of this swing: a wall stop ends the active phase with a recoil.</summary>
        private void PollAuthority()
        {
            if (_swingId == 0 || _recoilStarted || _pattern == null) return;
            if (!GameServices.TryGet<ICombatAuthority>(out ICombatAuthority authority)) return;
            if (!authority.TryGetMeleeSwingStatus(_swingId, out MeleeSwingStatus status)) return;
            if (!status.WallStopped) return;

            _recoilStarted = true;
            _phase = Phase.Recovery;

            // End the recoil when the authority ends the swing (its stop time + the pattern's recoil time, on the
            // same swing clock), so the client's readout and the authority's state agree about when the swing is over.
            float stopTime = _pattern.windupTime + _pattern.activeTime * status.WallContactSample / Mathf.Max(1, status.SampleCount);
            _phaseTimer = Mathf.Max(0.05f, stopTime + _pattern.wallRecoilTime - status.Elapsed);
            rig.PlaySwingRecoil();
            CombatDiagnostics.Log("melee-wall", $"[MeleeCombat] swing {_swingId} stopped by level geometry at sample {status.WallContactSample}/{status.SampleCount} with {status.Hits.Count} hit(s)", this);
        }

        private void OnDisable()
        {
            if (rig != null) rig.SetGuardHeld(false);
            _phase = Phase.Idle;
        }
    }
}
