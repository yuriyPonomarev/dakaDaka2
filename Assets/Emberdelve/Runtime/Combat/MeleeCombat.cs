using System;
using UnityEngine;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.InputLayer;
using Emberdelve.Player;

namespace Emberdelve.Combat
{
    /// <summary>
    /// Thin melee client (D1a/D1b). On the primary button it asks the combat authority to begin a swept swing
    /// (<see cref="ICombatAuthority.TryBeginMeleeSwing"/>) and plays the matching first-person weapon motion.
    /// It does not scan, overlap, measure an arc or line of sight and it hands the authority no hit point,
    /// range, timestamp or head flag: the authority sweeps the pattern's weapon segment through the hit volumes
    /// on its own tick. The local windup/active/recovery timers only drive the animation, the swinging flag the
    /// guard rule and the HUD cooldown readout; when the authority reports that level geometry stopped the
    /// swing, the client enters its recoil.
    ///
    /// D1b: the combo chain is the authority's. The client asks it which step comes next
    /// (<see cref="ICombatAuthority.TryGetExpectedComboStep"/>), requests that step with the chain's pattern and plays
    /// the visual for the pattern and timing the authority accepted. A press during the recovery is remembered for a
    /// moment so the next step can start the instant the swing is over.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(PlayerRig))]
    public sealed class MeleeCombat : MonoBehaviour
    {
        private enum Phase { Idle, Windup, Active, Recovery }

        /// <summary>How long a primary press during the recovery stays queued (seconds, real time).</summary>
        private const float PressBuffer = 0.50f;

        [Header("Wiring")]
        [SerializeField] private PlayerRig rig;
        [SerializeField] private GameConfig config;

        private WeaponData _weapon;
        private SwingPatternData _pattern;
        private SwingTiming _timing;
        private int _comboStep;
        private float _bufferedUntil = -1f;
        private Phase _phase = Phase.Idle;
        private float _phaseTimer;
        private float _readyTime;
        private float _swingStartedAt;
        private int _swingId;
        private bool _recoilStarted;

        /// <summary>Raised when the authority accepted a swing; carries the plain-data request that was sent.</summary>
        public event Action<MeleeSwingIntent> AttackStarted;

        public WeaponData Weapon => _weapon;
        public bool IsSwinging => _phase != Phase.Idle;

        /// <summary>The pattern of the swing in progress (or the last one).</summary>
        public SwingPatternData ActivePattern => _pattern;

        /// <summary>The combo step of the swing in progress (or the last one accepted).</summary>
        public int ComboStep => _comboStep;

        /// <summary>The resolved timing (chain step scales, finisher extra recovery) of the swing in progress.</summary>
        public SwingTiming ActiveTiming => _timing;

        /// <summary>Seconds a primary press made during the recovery stays queued (0 when nothing is buffered).</summary>
        public float BufferedPressRemaining => Mathf.Max(0f, _bufferedUntil - Time.time);

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
                float total = _timing.Total;
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
            _timing = default;
            _comboStep = 0;
            _bufferedUntil = -1f;
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

            // A press while the swing is still recovering is queued, so chaining the next step feels immediate.
            if (_phase == Phase.Recovery && input.PrimaryPressed) _bufferedUntil = Time.time + PressBuffer;

            switch (_phase)
            {
                case Phase.Idle:
                    if ((input.PrimaryPressed || Time.time <= _bufferedUntil) && !input.SecondaryHeld && Time.time >= _readyTime)
                    {
                        // The buffered press is spent only when the authority accepts the begin: a refusal (its cooldown clock a hair
                        // behind this one) keeps it for the rest of its 0.5 s.
                        if (TryBeginSwing()) _bufferedUntil = -1f;
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
                        _phaseTimer = Mathf.Max(0.02f, _timing.Recovery);
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
        /// Starts the next swing of the chain. Public because input, tools and (later) a networked input source all
        /// funnel through the same entry point; the authority decides whether it is accepted and what it hits.
        /// </summary>
        public void BeginSwing() => TryBeginSwing();

        /// <summary>
        /// Asks the authority which combo step comes next (0 when no chain is running or its window ran out) and
        /// requests that step with the chain's pattern. If the authority refuses a continuation (its clock and the
        /// client's disagree about the window) the client starts a fresh chain instead. Returns whether a swing started.
        /// </summary>
        public bool TryBeginSwing()
        {
            if (_weapon == null || rig == null) return false;

            int step = 0;
            if (GameServices.TryGet<ICombatAuthority>(out ICombatAuthority authority)
                && authority.TryGetExpectedComboStep(rig.CombatId, out int expected))
            {
                step = expected;
            }

            if (TryBeginStep(step)) return true;
            return step > 0 && TryBeginStep(0);
        }

        /// <summary>
        /// Requests combo step 0 with one specific pattern of the weapon. Kept for tools and fixtures that need a given
        /// pattern; the authority only accepts it when it is the pattern its chain assigns to step 0.
        /// </summary>
        public bool TryBeginSwing(int patternIndex) => TryBeginRequest(0, patternIndex);

        private bool TryBeginStep(int step)
        {
            int patternIndex = 0;
            if (_weapon.HasComboChain)
            {
                if (step < 0 || step >= _weapon.comboSteps.Length) return false;
                patternIndex = _weapon.comboSteps[step].patternIndex;
            }

            return TryBeginRequest(step, patternIndex);
        }

        /// <summary>
        /// Sends one begin request (<paramref name="step"/>, <paramref name="patternIndex"/>) and, only when the
        /// authority accepts, plays the visual it resolved. The aim sent is a snapshot the authority compares with its
        /// own copy of the pose, it is not what the sweep uses.
        /// </summary>
        private bool TryBeginRequest(int step, int patternIndex)
        {
            if (_weapon == null || rig == null) return false;

            if (!GameServices.TryGet<ICombatAuthority>(out ICombatAuthority authority))
            {
                Debug.LogError("[MeleeCombat] No ICombatAuthority in the scene; swings cannot deal damage.", this);
                return false;
            }

            // The pattern and timing the visual plays come from the same weapon data the authority resolves (nothing is
            // played unless the authority accepts).
            if (!_weapon.TryResolveComboStep(step, patternIndex, out ComboStep stepData, out SwingPatternData pattern, out string localReason))
            {
                CombatDiagnostics.Log("melee-nopattern", $"[MeleeCombat] weapon '{_weapon.weaponId}' cannot swing step {step} pattern {patternIndex}: {localReason}", this);
                return false;
            }

            if (!rig.TryGetAimPose(out _, out float yaw, out float pitch)) return false;

            // Sequence comes from the combat registry, keyed by combat id: the same player can swing a
            // sword and shoot a crossbow, and a private counter here would make one weapon's first attack
            // look like a replay of the other's.
            int sequence = CombatRegistry.NextSequence(rig.CombatId);
            MeleeSwingIntent intent = new MeleeSwingIntent(rig.CombatId, _weapon.weaponId, patternIndex, step, yaw, pitch, sequence);

            if (!authority.TryBeginMeleeSwing(intent, out int swingId, out string reason))
            {
                CombatDiagnostics.Log("melee-refused", $"[MeleeCombat] swing #{sequence} ({_weapon.weaponId} step {step}) refused by the authority: {reason}", this);
                return false;
            }

            _pattern = pattern;
            _comboStep = step;
            _timing = authority.TryGetMeleeSwingStatus(swingId, out MeleeSwingStatus accepted) ? accepted.Timing : _weapon.ComboTiming(step, pattern);
            _swingId = swingId;
            _swingStartedAt = Time.time;
            _recoilStarted = false;
            _phase = _timing.Windup > 0f ? Phase.Windup : Phase.Active;
            _phaseTimer = Mathf.Max(0.02f, _timing.Windup);
            if (_phase == Phase.Active) _phaseTimer = Mathf.Max(0.02f, _timing.Active);
            _readyTime = Time.time + Mathf.Max(0.1f, _weapon.cooldown, _timing.Total);

            rig.PlaySwingVisual(_weapon, pattern, _timing);
            AttackStarted?.Invoke(intent);

            CombatDiagnostics.Log("melee-swing",
                $"[MeleeCombat] swing #{sequence} ({_weapon.weaponId}, {pattern.patternId}, combo step {step} x{stepData.damageMultiplier:F2}) accepted by the authority as swing {swingId}", this);
            return true;
        }

        private void EnterActive()
        {
            _phase = Phase.Active;
            _phaseTimer = Mathf.Max(0.02f, _timing.Active);
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
            float stopTime = _timing.Windup + _timing.Active * status.WallContactSample / Mathf.Max(1, status.SampleCount);
            _phaseTimer = Mathf.Max(0.05f, stopTime + _timing.WallRecoil - status.Elapsed);
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
