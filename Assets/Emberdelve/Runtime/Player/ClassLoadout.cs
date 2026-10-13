using System.Collections;
using UnityEngine;
using Emberdelve.Core;
using Emberdelve.Combat;
using Emberdelve.Data;

namespace Emberdelve.Player
{
    /// <summary>
    /// Applies a CharacterClassData to the player rig and builds the temporary weapon visuals.
    /// This is the only place that knows which components a class needs, so adding a third class is
    /// a new asset plus (at most) one new combat component.
    /// </summary>
    public sealed class ClassLoadout : MonoBehaviour
    {
        [Header("Wiring")]
        [SerializeField] private Transform weaponAnchor;
        [SerializeField] private MeleeCombat meleeCombat;
        [SerializeField] private RangedCombat rangedCombat;
        [SerializeField] private Camera targetCamera;

        private WeaponVisualRig _visual;
        private CharacterClassData _appliedClass;

        public bool HasMuzzle => _visual != null && _visual.Muzzle != null;
        public Vector3 MuzzlePosition => _visual != null && _visual.Muzzle != null ? _visual.Muzzle.position : Vector3.zero;
        public CharacterClassData AppliedClass => _appliedClass;

        private void Awake()
        {
            if (meleeCombat == null) meleeCombat = GetComponent<MeleeCombat>();
            if (rangedCombat == null) rangedCombat = GetComponent<RangedCombat>();
            if (targetCamera == null && GetComponentInParent<Camera>() != null) targetCamera = GetComponentInParent<Camera>();
        }

        public void Apply(CharacterClassData data, GameConfig config, PlayerRig rig)
        {
            if (data == null || rig == null) return;
            _appliedClass = data;

            rig.Movement.ApplyClass(data, config.levelMask);
            rig.Look.ApplyClass(data);
            rig.Look.ConfigureSensitivity(config.mouseSensitivity);
            rig.Health.Configure(data.maxHealth, data.healthRegenPerSecond, data.regenDelay);

            if (rig.Inventory != null)
            {
                rig.Inventory.Configure(config.inventoryCapacity);
                rig.Inventory.GiveStartingItems(data.startingItems);
            }

            WeaponData weapon = data.primaryWeapon;
            bool isRanged = weapon != null && weapon.IsRanged;

            if (meleeCombat != null)
            {
                meleeCombat.enabled = !isRanged;
                meleeCombat.Initialize(weapon);
            }

            if (rangedCombat != null)
            {
                rangedCombat.enabled = isRanged;
                rangedCombat.Initialize(weapon, data.startingReserveAmmo, data.maxReserveAmmo);
            }

            RebuildWeaponVisual(weapon);

            if (targetCamera != null) targetCamera.fieldOfView = config.fieldOfView;
        }

        public void PlaySwingVisual(WeaponData weapon)
        {
            if (_visual != null) _visual.PlaySwing(weapon);
        }

        public void SetDrawVisual(float progress)
        {
            if (_visual != null) _visual.SetDrawProgress(progress);
        }

        public void RebuildWeaponVisual(WeaponData weapon)
        {
            if (_visual != null)
            {
                Destroy(_visual.gameObject);
                _visual = null;
            }

            if (weapon == null || weaponAnchor == null) return;
            _visual = WeaponVisualBuilder.Build(weapon, weaponAnchor);
        }
    }
}
