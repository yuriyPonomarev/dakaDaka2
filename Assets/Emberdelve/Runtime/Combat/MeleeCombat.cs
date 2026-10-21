using System;
using System.Collections.Generic;
using UnityEngine;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.InputLayer;
using Emberdelve.Player;

namespace Emberdelve.Combat
{
    /// <summary>
    /// Warrior swing state machine: windup -> active -> recovery -> cooldown.
    /// It only produces intent. During the active window it asks HitScanner which targets are
    /// actually reachable, then hands each candidate to the combat authority, which decides the
    /// damage. Nothing here writes a health value.
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
        private Phase _phase = Phase.Idle;
        private float _phaseTimer;
        private float _readyTime;
        private AttackIntent _pendingIntent;
        private readonly List<HitCandidate> _hits = new List<HitCandidate>(8);

        public event Action<AttackIntent> AttackStarted;
        public event Action<HitCandidate> SwingResolved;

        public WeaponData Weapon => _weapon;
        public bool IsSwinging => _phase != Phase.Idle;

        /// <summary>Seconds until the swing state machine would start another swing (0 when ready).</summary>
        public float CooldownRemaining => Mathf.Max(0f, _readyTime - Time.time);
        public float SwingProgress
        {
            get
            {
                if (_weapon == null || _phase == Phase.Idle) return 0f;
                float total = _weapon.windupTime + _weapon.activeWindow + _weapon.recoveryTime;
                if (total <= 0f) return 0f;
                float elapsed = _weapon.windupTime + _weapon.activeWindow + _weapon.recoveryTime - _phaseTimer;
                return Mathf.Clamp01(elapsed / total);
            }
        }

        private void Awake()
        {
            if (rig == null) rig = GetComponent<PlayerRig>();
        }

        public void Initialize(WeaponData weapon)
        {
            _weapon = weapon;
            _phase = Phase.Idle;
            _phaseTimer = 0f;
            _readyTime = 0f;

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
                    if (input.PrimaryPressed && !input.SecondaryHeld && Time.time >= _readyTime) BeginSwing();
                    break;

                case Phase.Windup:
                    _phaseTimer -= dt;
                    if (_phaseTimer <= 0f) EnterActive();
                    break;

                case Phase.Active:
                    _phaseTimer -= dt;
                    if (_phaseTimer <= 0f)
                    {
                        _phase = Phase.Recovery;
                        _phaseTimer = Mathf.Max(0.02f, _weapon.recoveryTime);
                    }
                    break;

                case Phase.Recovery:
                    _phaseTimer -= dt;
                    if (_phaseTimer <= 0f) _phase = Phase.Idle;
                    break;
            }
        }

        /// <summary>
        /// Starts a swing. Public because input, tools and (later) a networked input source all funnel
        /// through the same entry point; damage itself is still only applied by the combat authority.
        /// </summary>
        public void BeginSwing()
        {
            // Sequence comes from the combat registry, keyed by combat id: the same player can swing a
            // sword and shoot a crossbow, and a private counter here would make one weapon's first attack
            // look like a replay of the other's.
            int sequence = CombatRegistry.NextSequence(rig.CombatId);

            Vector3 origin = rig.AimOrigin;
            Vector3 direction = rig.AimDirection;
            float maxRange = _weapon.reach + _weapon.hitSphereRadius;

            _pendingIntent = new AttackIntent(rig.CombatId, rig.Kind, _weapon, 1f, origin, direction,
                maxRange, sequence, Time.time);

            _phase = Phase.Windup;
            _phaseTimer = Mathf.Max(0.02f, _weapon.windupTime);

            rig.PlaySwingVisual(_weapon);
            AttackStarted?.Invoke(_pendingIntent);
        }

        private void EnterActive()
        {
            LayerMask hitMask = config != null ? config.hitMask : LayerMask.GetMask("Default");
            LayerMask levelMask = config != null ? config.levelMask : Physics.DefaultRaycastLayers;

            Vector3 origin = rig.AimOrigin;
            Vector3 direction = rig.AimDirection;

            int count = HitScanner.CollectMeleeHits(origin, direction, _weapon, rig.CombatId, hitMask, levelMask, _hits, 3);

            if (!GameServices.TryGet<ICombatAuthority>(out ICombatAuthority authority))
            {
                Debug.LogError("[MeleeCombat] No ICombatAuthority in the scene; swings cannot deal damage.", this);
                return;
            }

            // One intent resolves one hit. The authority ties damage to a single command sequence, so a
            // swing cannot be replayed into extra damage; the closest valid target in the arc wins.
            HitCandidate best = default;
            bool found = false;
            float bestDistance = float.MaxValue;

            for (int i = 0; i < count; i++)
            {
                if (!CombatRegistry.TryGet(_hits[i].TargetCombatId, out ICombatEntity entity)) continue;

                float distance = (entity.CombatCenter - origin).sqrMagnitude;
                if (distance >= bestDistance) continue;

                bestDistance = distance;
                best = _hits[i];
                found = true;
            }

            if (found)
            {
                bool accepted = authority.SubmitAttack(_pendingIntent, best);
                CombatDiagnostics.Log("melee-swing",
                    $"[MeleeCombat] swing #{_pendingIntent.Sequence} ({_weapon.weaponId}): {count} candidate(s), submitted to #{best.TargetCombatId}, accepted={accepted}", this);
                SwingResolved?.Invoke(best);
            }
            else if (CombatDiagnostics.ShouldLog("melee-swing"))
            {
                string nearest = CombatRegistry.TryGetNearest(origin, EntityKind.Enemy, 8f, out ICombatEntity near)
                    ? $"nearest enemy #{near.CombatId} centre {Vector3.Distance(origin, near.CombatCenter):F2}m from the eye, {Vector3.Angle(direction, near.CombatCenter - origin):F0} deg off the aim (reach {_weapon.reach + _weapon.hitSphereRadius:F2}m, arc {_weapon.arcAngle:F0})"
                    : "no live enemy within 8m";
                Debug.Log($"[MeleeCombat] swing #{_pendingIntent.Sequence} ({_weapon.weaponId}): no target in the arc; {nearest}", this);
            }

            _phase = Phase.Active;
            _phaseTimer = Mathf.Max(0.02f, _weapon.activeWindow);
            _readyTime = Time.time + Mathf.Max(0.1f, _weapon.cooldown);
        }

        private void OnDisable()
        {
            if (rig != null) rig.SetGuardHeld(false);
            _phase = Phase.Idle;
        }
    }
}
