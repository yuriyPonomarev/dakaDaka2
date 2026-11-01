using System;
using System.Collections.Generic;
using UnityEngine;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.InputLayer;
using Emberdelve.Player;
using Emberdelve.Stats;

namespace Emberdelve.Combat
{
    /// <summary>
    /// Archer draw-and-release state machine plus ammunition bookkeeping. It produces a launch intent;
    /// the authority owns projectile travel, ordered collision, and impact damage.
    /// Ordinary draw charging is LOCAL and the existing launch path still accepts its charge. U3B must own
    /// server-clock begin/cancel records before this can count as network combat acceptance.
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
        private WeaponData _drawWeapon;
        private float _drawAuthoredTime;
        private float _drawDuration;
        private bool _reloading;
        private float _reloadTimer;
        private float _reloadDuration;
        private bool _viewBound;

        public event Action<AttackIntent> AttackStarted;
        public event Action ShotFired;

        public WeaponData Weapon => _weapon;
        public int Magazine => _magazine;
        public int Reserve => _reserve;
        internal int MaxReserve => _maxReserve;
        /// <summary>R1c2: true once the combat authority pushes its ammunition record here; the numbers are then a display.</summary>
        public bool IsAuthorityView => _viewBound;
        internal bool CanAcceptAmmo(int amount) => _weapon != null && amount > 0 &&
            (_maxReserve <= 0 || _reserve < _maxReserve);
        public bool IsDrawing => _drawing;
        public bool IsReloading => _reloading;
        /// <summary>Local accepted press capture; U3B must own equivalent server-clock begin/cancel state.</summary>
        public float ActiveDrawDuration => _drawing ? _drawDuration : 0f;
        public float ActiveAuthoredDrawTime => _drawing ? _drawAuthoredTime : 0f;
        public float Charge => _drawing && _drawDuration > 0f
            ? Mathf.Clamp01((Time.time - _drawStartedAt) / _drawDuration) : 0f;
        public float ReloadDuration => _reloadDuration;
        public float ReloadProgress => _reloading && _reloadDuration > 0f
            ? 1f - Mathf.Clamp01(_reloadTimer / _reloadDuration) : 0f;

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
            InitializeLocal(weapon, startingReserve, maxReserve, startingMagazine);

            // R1c2: the hand changed, so the authority's record learns it. Pools it already holds win over what is passed
            // here (a swap is not a refill); only a weapon it has never seen is adopted.
            if (rig == null) rig = GetComponent<PlayerRig>();
            if (rig != null) rig.SeedKit(KitSeedReason.Weapon);
        }

        private void InitializeLocal(WeaponData weapon, int startingReserve, int maxReserve, int startingMagazine)
        {
            CancelDraw();
            _weapon = weapon;
            _reloadTimer = _reloadDuration = 0f;

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
            if (_weapon == null || !_weapon.IsRanged || rig == null) { CancelDraw(); return; }

            EmberdelveInput input = rig.Input;
            if (input == null || !input.GameplayInputEnabled) { CancelDraw(); return; }

            if (rig.IsDead)
            {
                CancelDraw();
                return;
            }

            if (_drawing && _drawWeapon != _weapon) CancelDraw();

            float dt = Time.deltaTime;

            if (_reloading)
            {
                // R1c2: with a kit record the authority runs the timer and moves the rounds.
                if (!_viewBound)
                {
                    _reloadTimer -= dt;
                    if (_reloadTimer <= 0f) FinishReload();
                }
            }
            else if (input.ReloadPressed && CanReload)
            {
                if (_viewBound) RequestReload();
                else StartReload();
            }

            if (input.PrimaryPressed && !_drawing && !_reloading && _magazine > 0)
            {
                _drawWeapon = _weapon;
                _drawAuthoredTime = _weapon.drawTime;
                _drawDuration = ActionSpeedRules.DrawDuration(_drawAuthoredTime, rig.ActionSpeedFactor);
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
                    WeaponData drawnWeapon = _drawWeapon;
                    CancelDraw();

                    if (charge >= drawnWeapon.minChargeToFire) Fire(charge);
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
            _reloadDuration = ActionSpeedRules.ReloadDuration(_weapon.reloadTime, rig != null ? rig.ActionSpeedFactor : 1f);
            _reloadTimer = _reloadDuration;
            CancelDraw();
            GameEvents.RaiseToast(OwnerId, "Reloading...");
        }

        private void RequestReload()
        {
            if (!GameServices.TryGet<ICombatAuthority>(out ICombatAuthority authority)) return;
            if (!authority.TryStartReload(rig.CombatSender, rig.CombatId, out _)) return;

            // The authority has started it (and pushed _reloading/_reloadTimer back into this view).
            CancelDraw();
            GameEvents.RaiseToast(OwnerId, "Reloading...");
        }

        /// <summary>R1c2: the authority's ammunition record for the weapon in hand. This component only displays it.</summary>
        internal void ApplyKitView(in KitView view)
        {
            _viewBound = true;
            if (_weapon == null || !_weapon.IsRanged || !view.HasActiveAmmo || view.ActiveWeaponId != _weapon.weaponId) return;

            bool changed = _magazine != view.Magazine || _reserve != view.Reserve;
            _magazine = view.Magazine;
            _reserve = view.Reserve;
            _maxReserve = view.MaxReserve;
            _reloading = view.Reloading;
            _reloadTimer = view.ReloadRemaining;
            _reloadDuration = view.ReloadDuration;
            if (_reloading) CancelDraw();
            if (changed) GameEvents.RaiseAmmo(OwnerId, _magazine, _reserve);
        }

        private void FinishReload()
        {
            _reloading = false;
            _reloadTimer = _reloadDuration = 0f;
            int need = _weapon.magazineSize - _magazine;
            int taken = Mathf.Min(need, _reserve);
            _magazine += taken;
            _reserve -= taken;
            GameEvents.RaiseAmmo(OwnerId, _magazine, _reserve);
        }

        internal void AddAmmo(int amount)
        {
            if (_weapon == null || amount <= 0) return;

            // R1c2: a reserve grant (inventory use, pickup) is an authority operation once a kit record exists.
            if (_viewBound && rig != null && GameServices.TryGet<ICombatAuthority>(out ICombatAuthority granting)
                && granting is LocalCombatAuthority local && local.HostGrantReserve(rig.CombatId, amount))
            {
                return;
            }

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
            _drawing = false;
            _drawWeapon = null;
            _drawStartedAt = _drawAuthoredTime = _drawDuration = 0f;
            if (rig != null)
            {
                rig.SetDrawVisual(0f);
                rig.SetAimingPenalty(1f);
            }
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
            if (!accepted)
            {
                if (_viewBound && GameServices.TryGet<ICombatAuthority>(out ICombatAuthority forfeiting)
                    && forfeiting.TryForfeitRound(rig.CombatSender, rig.CombatId))
                {
                    // the record lost the round and pushed it back into this view
                }
                else _magazine = Mathf.Max(0, _magazine - 1);
            }
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
