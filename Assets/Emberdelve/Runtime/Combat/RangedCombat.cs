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
    /// Archer draw-and-release state machine plus ammunition bookkeeping. It produces a launch intent;
    /// the authority owns projectile travel, ordered collision, and impact damage.
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
        internal bool CanAcceptAmmo(int amount) => _weapon != null && amount > 0 &&
            (_maxReserve <= 0 || _reserve < _maxReserve);
        public bool IsDrawing => _drawing;
        public bool IsReloading => _reloading;
        public float Charge => _drawing ? Mathf.Clamp01((Time.time - _drawStartedAt) / Mathf.Max(0.05f, _weapon.drawTime)) : 0f;
        public float ReloadProgress => _reloading ? 1f - Mathf.Clamp01(_reloadTimer / Mathf.Max(0.05f, _weapon.reloadTime)) : 0f;

        /// <summary>R7: the player this component belongs to; every event it raises carries it (0 = no player, e.g. a bare bag in a tool).</summary>
        private int OwnerId
        {
            get
            {
                if (rig == null) rig = GetComponent<PlayerRig>();
                return rig != null ? rig.PlayerId : GameEvents.NoPlayer;
            }
        }

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
                // B1: a reload or draw that was running does not survive the weapon leaving the hand (nothing is refunded or
                // granted: the magazine was already stored by the loadout, an unfinished reload adds no rounds).
                _drawing = false;
                _reloading = false;
                enabled = false;
                return;
            }

            int capacity = Mathf.Clamp(weapon.magazineSize, 1, 99);
            _magazine = startingMagazine < 0 ? capacity : Mathf.Clamp(startingMagazine, 0, capacity);
            _maxReserve = Mathf.Max(0, maxReserve);
            _reserve = Mathf.Clamp(startingReserve, 0, _maxReserve);
            _drawing = false;
            _reloading = false;

            GameEvents.RaiseAmmo(OwnerId, _magazine, _reserve);
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
                    else GameEvents.RaiseToast(OwnerId, "Shot released too early");
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
            GameEvents.RaiseToast(OwnerId, "Reloading...");
        }

        private void FinishReload()
        {
            _reloading = false;
            int need = _weapon.magazineSize - _magazine;
            int taken = Mathf.Min(need, _reserve);
            _magazine += taken;
            _reserve -= taken;
            GameEvents.RaiseAmmo(OwnerId, _magazine, _reserve);
        }

        public void AddAmmo(int amount)
        {
            if (_weapon == null || amount <= 0) return;
            _reserve = _maxReserve > 0 ? Mathf.Min(_reserve + amount, _maxReserve) : _reserve + amount;
            GameEvents.RaiseAmmo(OwnerId, _magazine, _reserve);
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
            GameEvents.RaiseAmmo(OwnerId, _magazine, _reserve);
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
            GameEvents.RaiseAmmo(OwnerId, _magazine, _reserve);
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
            AttackIntent intent = new AttackIntent(rig.CombatId, rig.Kind, _weapon, charge, muzzle, direction,
                range, sequence, Time.time);
            bool accepted = GameServices.TryGet<ICombatAuthority>(out ICombatAuthority authority)
                            && authority.LaunchProjectile(rig.CombatSender, intent, out _);

            CombatDiagnostics.Log("ranged-shot",
                $"[RangedCombat] projectile #{sequence} ({_weapon.weaponId}) charge {charge:F2}, accepted={accepted}", this);

            // A rejected launch still consumes the fired round locally, matching the old miss/rejection path.
            // Accepted launches spend it once on the authority.
            if (!accepted) _magazine = Mathf.Max(0, _magazine - 1);
            GameEvents.RaiseAmmo(OwnerId, _magazine, _reserve);

            AttackStarted?.Invoke(intent);
            ShotFired?.Invoke();

            if (_magazine == 0) GameEvents.RaiseToast(OwnerId, $"Out of {(_weapon != null && !string.IsNullOrEmpty(_weapon.ammoDisplayName) ? _weapon.ammoDisplayName : "ammo")} - press R to reload");
        }

        private void OnDisable()
        {
            CancelDraw();
            if (rig != null) rig.SetAimingPenalty(1f);
        }
    }
}
