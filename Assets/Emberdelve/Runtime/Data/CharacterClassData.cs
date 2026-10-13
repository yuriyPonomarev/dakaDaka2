using UnityEngine;

namespace Emberdelve.Data
{
    /// <summary>
    /// Class stats and kit. The player controller never hard-codes class behaviour: it applies this
    /// asset through ClassLoadout, so new classes only need a new asset plus a weapon asset.
    /// </summary>
    [CreateAssetMenu(menuName = "Emberdelve/02 Character Class", fileName = "CharacterClassData")]
    public class CharacterClassData : ScriptableObject
    {
        [Header("Identity")]
        public string classId = "class";
        public string displayName = "Class";
        [TextArea(3, 6)] public string description = "";
        public Color accentColor = Color.white;

        [Header("Vitals")]
        public int maxHealth = 120;
        [Tooltip("Passive regeneration per second once out of danger.")]
        public float healthRegenPerSecond = 0f;
        [Tooltip("Seconds without damage before regeneration starts.")]
        public float regenDelay = 6f;

        [Header("Movement")]
        public float moveSpeed = 3.6f;
        public float sprintSpeed = 5.4f;
        public float crouchSpeed = 1.7f;
        public float jumpHeight = 1.15f;
        public float standHeight = 1.8f;
        public float crouchHeight = 1.15f;
        public float standEyeHeight = 1.68f;
        public float crouchEyeHeight = 1.05f;

        [Header("Kit")]
        public WeaponData primaryWeapon;
        [Tooltip("Optional off-hand shield. A shield can block but can never parry; see DefenseProfile.")]
        public WeaponData offhandShield;
        [Tooltip("Off-hand / secondary weapon the class can switch to at runtime (Warrior crossbow, Archer rapier).")]
        public WeaponData secondaryWeapon;
        public ItemData[] startingItems;

        [Header("Ranged reserve (ignored by classes without a ranged weapon)")]
        public int startingReserveAmmo = 0;
        public int maxReserveAmmo = 0;

        [Header("Guard (melee classes)")]
        [Tooltip("0 disables the block ability for this class.")]
        [Range(0f, 0.95f)] public float blockReduction = 0.65f;
        [Tooltip("Maximum angle (degrees) between facing and the incoming hit for the guard to hold.")]
        [Range(30f, 180f)] public float blockArc = 130f;
        [Tooltip("Movement speed multiplier while guarding.")]
        [Range(0.1f, 1f)] public float guardSpeedMultiplier = 0.45f;

        public bool CanGuard => blockReduction > 0f;
    }
}
