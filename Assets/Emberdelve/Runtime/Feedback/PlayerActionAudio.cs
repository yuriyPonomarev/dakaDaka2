using Emberdelve.Combat;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Flow;
using Emberdelve.Items;
using Emberdelve.Player;
using UnityEngine;

namespace Emberdelve.Feedback
{
    /// <summary>Local presentation of observed action edges. Never starts or completes a gameplay action.</summary>
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

        public void Bind(PlayerRig rig)
        {
            if (_rig == rig && _subscribed) return;
            Unsubscribe();
            _rig = rig;
            _ranged = rig != null ? rig.GetComponent<RangedCombat>() : null;
            _channel = rig != null ? rig.GetComponent<ConsumableChannel>() : null;
            _drink = rig != null ? rig.GetComponent<PotionDrinkPresentation>() : null;
            _seeded = false;
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

        private void LateUpdate()
        {
            if (_rig == null || !_rig.IsLocallyControlled || _rig.IsDead || _rig.IsExtracted || Time.timeScale <= 0f)
            { _seeded = false; return; }
            if (_ranged == null) _ranged = _rig.GetComponent<RangedCombat>();
            if (_channel == null) _channel = _rig.GetComponent<ConsumableChannel>();
            if (_drink == null) _drink = _rig.GetComponent<PotionDrinkPresentation>();
            WeaponData weapon = _rig.ActiveWeapon;
            bool ranged = _ranged != null && weapon != null && weapon.IsRanged && !_rig.WeaponInputSuppressed;
            bool reloading = ranged && _ranged.IsReloading;
            // A client owner's ranged simulation is disabled. Its view draw clock is fed by the server snapshot.
            bool drawing = ranged && !reloading && !_rig.IsGuarding && (NetRole.IsClientOnly
                ? _rig.Loadout != null && _rig.Loadout.Visual != null && _rig.Loadout.Visual.DrawProgress > 0f
                : _ranged.IsDrawing);
            bool consuming = (_drink != null && _drink.IsDrinking) || (_channel != null && _channel.IsChanneling);
            int magazine = _ranged != null ? _ranged.Magazine : 0;
            bool floorChanged = _floorChanged || _floorGeneration != _rig.FloorMovementGeneration || _inTransit != _rig.IsInFloorTransit;
            if (_seeded && floorChanged)
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
            if (!_seeded || floorChanged || _weapon != weapon || _rig.IsInFloorTransit)
            {
                Capture(weapon, drawing, reloading, consuming, magazine);
                return;
            }

            if (drawing && !_drawing) WorldAudio.PlayLocal(WorldSound.Draw, .55f);
            if (reloading && !_reloading)
            {
                _reloadStartMagazine = magazine;
                WorldAudio.PlayLocal(WorldSound.Reload, .6f);
            }
            // Cancellation also clears IsReloading. Only an observed ammunition commit earns the latch sound.
            if (_reloading && !reloading && ranged && magazine > _reloadStartMagazine)
                WorldAudio.PlayLocal(WorldSound.Equip, .4f);
            if (consuming && !_consuming)
            {
                ItemData item = _channel != null && _channel.IsChanneling ? _channel.ChannelItem : _rig.Inventory?.HeldItem;
                // A late snapshot without item identity is silent rather than calling a bandage a potion.
                if (item != null && item.useChannelSeconds > 0f)
                    WorldAudio.PlayLocal(item.IsBandage ? WorldSound.Bandage : WorldSound.Potion, .55f);
            }
            if (CanRequestDryFire(weapon, ranged, drawing, reloading, consuming) && Time.unscaledTime >= _nextDryFire)
            {
                _nextDryFire = Time.unscaledTime + .2f;
                WorldAudio.PlayLocal(WorldSound.DryFire, .5f);
            }
            _drawing = drawing;
            _reloading = reloading;
            _consuming = consuming;
            SampleZoneWarning();
        }

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
