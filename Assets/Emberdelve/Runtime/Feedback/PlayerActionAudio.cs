using Emberdelve.Combat;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Flow;
using Emberdelve.Items;
using Emberdelve.Player;
using UnityEngine;

namespace Emberdelve.Feedback
{
    /// <summary>Actor foley from observed action edges. Never starts or completes a gameplay action.</summary>
    [DisallowMultipleComponent]
    public sealed class PlayerActionAudio : MonoBehaviour
    {
        private PlayerRig _rig;
        private RangedCombat _ranged;
        private ConsumableChannel _channel;
        private PotionDrinkPresentation _drink;
        private WeaponData _weapon;
        private bool _subscribed, _seeded, _drawing, _reloading, _consuming, _inTransit, _floorChanged;
        private bool _sawTransitEntry;
        private uint _floorGeneration;
        private int _reloadStartMagazine;
        private float _nextDryFire, _nextPortal;
        private RoyaleZoneBoundary _zoneBoundary;
        private bool _zoneSeeded;
        private float _nextZoneSample, _nextZoneWarning;
        private bool _hasReplicatedActions, _replicatedDrawing, _replicatedReloading, _replicatedConsuming;
        private string _replicatedWeaponId, _replicatedUtilityId, _sampledWeaponId;

        public void Bind(PlayerRig rig)
        {
            if (_rig == rig && _subscribed) return;
            Unsubscribe();
            _rig = rig;
            _ranged = rig != null ? rig.GetComponent<RangedCombat>() : null;
            _channel = rig != null ? rig.GetComponent<ConsumableChannel>() : null;
            _drink = rig != null ? rig.GetComponent<PotionDrinkPresentation>() : null;
            _seeded = false;
            _hasReplicatedActions = false;
            if (_rig != null && isActiveAndEnabled)
            {
                _rig.FloorMovementChanged += OnFloorMovementChanged;
                _subscribed = true;
            }
        }

        private void OnEnable() => Bind(_rig != null ? _rig : GetComponent<PlayerRig>());
        private void OnDisable() { Unsubscribe(); _seeded = false; }

        private void Unsubscribe()
        {
            if (_rig != null && _subscribed) _rig.FloorMovementChanged -= OnFloorMovementChanged;
            _subscribed = false;
            _floorChanged = false;
            _sawTransitEntry = false;
        }

        private void OnFloorMovementChanged(PlayerRig rig)
        {
            _floorChanged = true;
            _sawTransitEntry |= rig.IsInFloorTransit;
        }

        /// <summary>Client presentation only. Owners and observers share one sampler, so neither also emits from its local simulation.</summary>
        public void ApplyReplicatedActionState(bool valid, string weaponId, string utilityId,
            bool drawing, bool reloading, bool consuming)
        {
            if (!NetRole.IsClientOnly) return;
            _hasReplicatedActions = valid;
            if (!valid) { _seeded = false; return; }
            _replicatedWeaponId = weaponId;
            _replicatedUtilityId = utilityId;
            _replicatedDrawing = drawing;
            _replicatedReloading = reloading;
            _replicatedConsuming = consuming;
        }

        private void LateUpdate()
        {
            if (_rig == null || _rig.IsDead || _rig.IsExtracted || Time.timeScale <= 0f ||
                (NetRole.IsClientOnly && !_hasReplicatedActions))
            { _seeded = false; return; }
            if (_ranged == null) _ranged = _rig.GetComponent<RangedCombat>();
            if (_channel == null) _channel = _rig.GetComponent<ConsumableChannel>();
            if (_drink == null) _drink = _rig.GetComponent<PotionDrinkPresentation>();
            WeaponData weapon = _rig.ActiveWeapon;
            bool ranged = _ranged != null && weapon != null && weapon.IsRanged && !_rig.WeaponInputSuppressed;
            // Remote rigs need no private inventory or first-person weapon model to make foley.
            bool reloading = NetRole.IsClientOnly ? _replicatedReloading : ranged && _ranged.IsReloading;
            bool drawing = NetRole.IsClientOnly ? _replicatedDrawing : ranged && !reloading && !_rig.IsGuarding && _ranged.IsDrawing;
            bool consuming = NetRole.IsClientOnly ? _replicatedConsuming :
                (_drink != null && _drink.IsDrinking) || (_channel != null && _channel.IsChanneling);
            int magazine = _ranged != null ? _ranged.Magazine : 0;
            bool floorChanged = _floorChanged || _floorGeneration != _rig.FloorMovementGeneration || _inTransit != _rig.IsInFloorTransit;
            if (_rig.IsLocallyControlled && _seeded && floorChanged)
            {
                bool entered = _sawTransitEntry || (!_inTransit && _rig.IsInFloorTransit);
                bool transferBetweenSamples = !_inTransit && !_rig.IsInFloorTransit && _floorGeneration != _rig.FloorMovementGeneration;
                // One cue at entry. Arrival does not repeat it; a whole transfer between snapshots is still audible.
                if ((entered || transferBetweenSamples) && Time.unscaledTime >= _nextPortal)
                {
                    _nextPortal = Time.unscaledTime + 1f;
                    WorldAudio.PlayLocal(WorldSound.Portal, .6f);
                }
            }
            bool weaponChanged = NetRole.IsClientOnly ? _sampledWeaponId != _replicatedWeaponId : _weapon != weapon;
            if (_seeded && weaponChanged && !floorChanged && !_rig.IsInFloorTransit && !_rig.IsLocallyControlled)
                PlayActor(WorldSound.Equip, .5f);
            if (!_seeded || floorChanged || weaponChanged || _rig.IsInFloorTransit)
            {
                Capture(weapon, drawing, reloading, consuming, magazine);
                return;
            }

            if (drawing && !_drawing) PlayActor(WorldSound.Draw, .55f);
            if (reloading && !_reloading)
            {
                _reloadStartMagazine = magazine;
                PlayActor(WorldSound.Reload, .6f);
            }
            // Cancellation also clears IsReloading. Only an observed ammunition commit earns the latch sound.
            if (_reloading && !reloading && ranged && magazine > _reloadStartMagazine &&
                (!NetRole.IsClientOnly || _rig.IsLocallyControlled))
                PlayActor(WorldSound.Equip, .4f);
            if (consuming && !_consuming)
            {
                ItemData item = NetRole.IsClientOnly ? HandheldUtilityVisual.Find(_replicatedUtilityId) :
                    _channel != null && _channel.IsChanneling ? _channel.ChannelItem : _rig.Inventory?.HeldItem;
                // A late snapshot without item identity is silent rather than calling a bandage a potion.
                if (item != null && item.useChannelSeconds > 0f)
                    PlayActor(item.IsBandage ? WorldSound.Bandage : WorldSound.Potion, .55f);
            }
            if (_rig.IsLocallyControlled && CanRequestDryFire(weapon, ranged, drawing, reloading, consuming) && Time.unscaledTime >= _nextDryFire)
            {
                _nextDryFire = Time.unscaledTime + .2f;
                PlayActor(WorldSound.DryFire, .5f);
            }
            _drawing = drawing;
            _reloading = reloading;
            _consuming = consuming;
            if (_rig.IsLocallyControlled) SampleZoneWarning();
        }

        private void PlayActor(WorldSound sound, float gain) =>
            WorldAudio.PlayAttached(sound, _rig.transform, Vector3.up * 1.2f, gain);

        private void SampleZoneWarning()
        {
            if (Time.unscaledTime < _nextZoneSample) return;
            _nextZoneSample = Time.unscaledTime + .5f;
            if (_zoneBoundary == null || !_zoneBoundary.isActiveAndEnabled || !_zoneBoundary.IsVisible)
            {
                _zoneBoundary = FindFirstObjectByType<RoyaleZoneBoundary>();
                // Older floor curtains remain as hidden objects; choose the currently presented floor.
                if (_zoneBoundary != null && !_zoneBoundary.IsVisible)
                    foreach (var candidate in FindObjectsByType<RoyaleZoneBoundary>(FindObjectsSortMode.None))
                        if (candidate.isActiveAndEnabled && candidate.IsVisible) { _zoneBoundary = candidate; break; }
            }
            if (_zoneBoundary == null || !_zoneBoundary.isActiveAndEnabled || !_zoneBoundary.IsVisible)
            { _zoneSeeded = false; return; }
            Vector3 center = _zoneBoundary.transform.position;
            Vector3 position = _rig.transform.position;
            float radius = Mathf.Abs(_zoneBoundary.transform.localScale.x);
            bool outside = new Vector2(position.x - center.x, position.z - center.z).sqrMagnitude > radius * radius;
            if (!_zoneSeeded)
            {
                _zoneSeeded = true;
                _nextZoneWarning = Time.unscaledTime + (outside ? 8f : 0f);
                return;
            }
            if (!outside || Time.unscaledTime < _nextZoneWarning) return;
            _nextZoneWarning = Time.unscaledTime + 8f;
            WorldAudio.PlayLocal(WorldSound.ZoneWarning, .6f);
        }

        private bool CanRequestDryFire(WeaponData weapon, bool ranged, bool drawing, bool reloading, bool consuming)
        {
            var input = _rig.Input;
            return ranged && !drawing && !reloading && !consuming && _rig.IsInPlay &&
                _ranged.HasAuthoritativeAmmo && _ranged.Magazine == 0 && !_rig.IsGuarding && !_rig.IsGuardStaggered &&
                (_rig.Skills == null || !_rig.Skills.SuppressPrimary) && input != null && input.GameplayInputEnabled &&
                !input.QuickWheelCaptured && !InventoryInputFocus.OwnsEscape && !input.ReloadPressed &&
                !(weapon.reloadWhileHeld && input.ReloadHeld) && !(weapon.CanRangedGuard && input.SecondaryHeld) && input.PrimaryPressed;
        }

        private void Capture(WeaponData weapon, bool drawing, bool reloading, bool consuming, int magazine)
        {
            _weapon = weapon;
            _sampledWeaponId = _replicatedWeaponId;
            _drawing = drawing;
            _reloading = reloading;
            _consuming = consuming;
            _reloadStartMagazine = magazine;
            _floorGeneration = _rig.FloorMovementGeneration;
            _inTransit = _rig.IsInFloorTransit;
            _floorChanged = false;
            _sawTransitEntry = false;
            _zoneSeeded = false;
            _zoneBoundary = null;
            _seeded = true;
        }
    }
}
