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
    /// Archer draw-and-release state machine plus ammunition bookkeeping.
    /// Like MeleeCombat it only produces intent: the shot is resolved by a ray the component runs
    /// from the muzzle, and the resulting HitCandidate (an id, not a reference) is handed to the
    /// combat authority. A future authoritative server can run the same ray from its own replicated
    /// positions and ignore the client's claim entirely.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(PlayerRig))]
    public sealed class RangedCombat : MonoBehaviour
    {
        private const int MaxRayResults = 16;
        private static readonly RaycastHit[] _rayBuffer = new RaycastHit[MaxRayResults];

        [Header("Wiring")]
        [SerializeField] private PlayerRig rig;
        [SerializeField] private GameConfig config;

        private WeaponData _weapon;
        private int _magazine;
        private int _reserve;
        private int _maxReserve;
        private bool _drawing;
        private float _drawStartedAt;
        private bool _reloading;
        private float _reloadTimer;

        public event Action<AttackIntent> AttackStarted;
        public event Action ShotFired;

        public WeaponData Weapon => _weapon;
        public int Magazine => _magazine;
        public int Reserve => _reserve;
        public bool IsDrawing => _drawing;
        public bool IsReloading => _reloading;
        public float Charge => _drawing ? Mathf.Clamp01((Time.time - _drawStartedAt) / Mathf.Max(0.05f, _weapon.drawTime)) : 0f;
        public float ReloadProgress => _reloading ? 1f - Mathf.Clamp01(_reloadTimer / Mathf.Max(0.05f, _weapon.reloadTime)) : 0f;

        private void Awake()
        {
            if (rig == null) rig = GetComponent<PlayerRig>();
        }

        /// <summary>
        /// startingMagazine = -1 means "full magazine". A non-negative value restores a partially spent
        /// magazine, which is what ClassLoadout needs so ammo survives a weapon swap instead of refilling
        /// for free.
        /// </summary>
        public void Initialize(WeaponData weapon, int startingReserve = 0, int maxReserve = 0, int startingMagazine = -1)
        {
            _weapon = weapon;

            if (weapon == null || !weapon.IsRanged)
            {
                enabled = false;
                return;
            }

            int capacity = Mathf.Clamp(weapon.magazineSize, 1, 99);
            _magazine = startingMagazine < 0 ? capacity : Mathf.Clamp(startingMagazine, 0, capacity);
            _maxReserve = Mathf.Max(0, maxReserve);
            _reserve = Mathf.Clamp(startingReserve, 0, _maxReserve);
            _drawing = false;
            _reloading = false;

            GameEvents.RaiseAmmo(_magazine, _reserve);
        }

        private void Update()
        {
            if (_weapon == null || rig == null) return;

            EmberdelveInput input = rig.Input;
            if (input == null) return;

            if (rig.IsDead)
            {
                CancelDraw();
                return;
            }

            float dt = Time.deltaTime;

            if (_reloading)
            {
                _reloadTimer -= dt;
                if (_reloadTimer <= 0f) FinishReload();
            }
            else if (input.ReloadPressed && CanReload)
            {
                StartReload();
            }

            if (input.PrimaryPressed && !_reloading && _magazine > 0)
            {
                _drawing = true;
                _drawStartedAt = Time.time;
            }

            if (_drawing)
            {
                rig.SetDrawVisual(Charge);
                rig.SetAimingPenalty(0.55f);

                if (input.PrimaryReleased)
                {
                    float charge = Charge;
                    CancelDraw();

                    if (charge >= _weapon.minChargeToFire) Fire(charge);
                    else GameEvents.RaiseToast("Shot released too early");
                }
            }
            else
            {
                rig.SetDrawVisual(0f);
                rig.SetAimingPenalty(1f);
            }
        }

        private bool CanReload => _magazine < _weapon.magazineSize && _reserve > 0;

        private void StartReload()
        {
            _reloading = true;
            _reloadTimer = Mathf.Max(0.1f, _weapon.reloadTime);
            CancelDraw();
            GameEvents.RaiseToast("Reloading...");
        }

        private void FinishReload()
        {
            _reloading = false;
            int need = _weapon.magazineSize - _magazine;
            int taken = Mathf.Min(need, _reserve);
            _magazine += taken;
            _reserve -= taken;
            GameEvents.RaiseAmmo(_magazine, _reserve);
        }

        public void AddAmmo(int amount)
        {
            if (_weapon == null || amount <= 0) return;
            _reserve = _maxReserve > 0 ? Mathf.Min(_reserve + amount, _maxReserve) : _reserve + amount;
            GameEvents.RaiseAmmo(_magazine, _reserve);
        }

        /// <summary>
        /// Ammunition check for skills and tools: the magazine is the only pool, so a skill that wants N
        /// rounds has to find N rounds in the weapon the player is actually holding.
        /// </summary>
        public bool HasAmmo(int count) => _weapon != null && _weapon.IsRanged && _magazine >= Mathf.Max(0, count);

        /// <summary>
        /// Spends rounds from the magazine without reloading and without refilling anything. Returns false
        /// and changes nothing when the magazine cannot cover the request.
        /// </summary>
        public bool TryConsumeAmmo(int count)
        {
            int needed = Mathf.Max(0, count);
            if (needed == 0) return true;
            if (!HasAmmo(needed)) return false;

            _magazine -= needed;
            GameEvents.RaiseAmmo(_magazine, _reserve);
            return true;
        }

        /// <summary>
        /// Gives rounds back to the magazine (never above its capacity). Used by the combat authority when it
        /// cancels an activation it had already charged ammo for.
        /// </summary>
        internal void RefundAmmo(int count)
        {
            if (_weapon == null || !_weapon.IsRanged || count <= 0) return;
            _magazine = Mathf.Min(Mathf.Clamp(_weapon.magazineSize, 1, 99), _magazine + count);
            GameEvents.RaiseAmmo(_magazine, _reserve);
        }

        /// <summary>Abandons an in-progress draw, e.g. because a skill is about to fire instead.</summary>
        public void CancelDraw()
        {
            if (!_drawing) return;
            _drawing = false;
            if (rig != null) rig.SetDrawVisual(0f);
        }

        /// <summary>
        /// Resolves one shot. Public so input, tools and a future networked input source share one path;
        /// damage is still computed by the combat authority from validated geometry only.
        /// </summary>
        public void Fire(float charge = 1f)
        {
            // Sequence is allocated per combat id, not per component: swapping between a bow and a
            // rapier must not make the second weapon's first attack look like a replayed command.
            int sequence = CombatRegistry.NextSequence(rig.CombatId);

            Vector3 muzzle = rig.GetMuzzlePosition();
            Vector3 direction = rig.AimDirection;
            float range = Mathf.Max(1f, _weapon.rangedRange);

            LayerMask hitMask = config != null ? config.hitMask : Physics.AllLayers;
            LayerMask levelMask = config != null ? config.levelMask : Physics.DefaultRaycastLayers;
            int mask = hitMask.value | levelMask.value;

            int count = Physics.RaycastNonAlloc(muzzle, direction, _rayBuffer, range, mask, QueryTriggerInteraction.Ignore);
            Array.Sort(_rayBuffer, 0, count, Comparer<RaycastHit>.Create((a, b) => a.distance.CompareTo(b.distance)));

            Vector3 endpoint = muzzle + direction * range;
            HitCandidate candidate = default;
            bool hasTarget = false;

            for (int i = 0; i < count; i++)
            {
                Collider collider = _rayBuffer[i].collider;
                if (collider == null) continue;

                endpoint = _rayBuffer[i].point;

                ICombatEntity entity = collider.GetComponentInParent<ICombatEntity>();
                if (entity == null || entity.IsDead || entity.CombatId == rig.CombatId) break;

                candidate = new HitCandidate(entity.CombatId, _rayBuffer[i].point,
                    entity.HeadCollider != null && collider == entity.HeadCollider);
                hasTarget = true;
                break;
            }

            AttackIntent intent = new AttackIntent(rig.CombatId, rig.Kind, _weapon, charge, muzzle, direction,
                range, sequence, Time.time);

            // Ammo: the authority validates (it refuses a ranged attack from an empty magazine) and SPENDS the
            // round for an attack it accepts, because it reads the magazine through IAmmoSource. A shot the
            // authority did not accept (a miss, no target, a rejected intent) is still a fired round, so it is
            // spent here. Exactly one of the two spends per shot: no double spend, no free accepted hit.
            // Note: the draw Charge is still a client claim (clamped 0..1); a server draw timer is U3.
            bool accepted = hasTarget && GameServices.TryGet<ICombatAuthority>(out ICombatAuthority authority)
                            && authority.SubmitAttack(intent, candidate);

            if (!accepted) _magazine = Mathf.Max(0, _magazine - 1);
            GameEvents.RaiseAmmo(_magazine, _reserve);

            ProjectileVisual.SpawnArrow(muzzle, endpoint, _weapon);

            AttackStarted?.Invoke(intent);
            ShotFired?.Invoke();

            if (_magazine == 0) GameEvents.RaiseToast($"Out of {(_weapon != null && !string.IsNullOrEmpty(_weapon.ammoDisplayName) ? _weapon.ammoDisplayName : "ammo")} - press R to reload");
        }

        private void OnDisable()
        {
            CancelDraw();
            if (rig != null) rig.SetAimingPenalty(1f);
        }
    }
}
