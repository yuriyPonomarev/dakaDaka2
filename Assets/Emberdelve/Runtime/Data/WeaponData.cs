using UnityEngine;
using Emberdelve.Core;

namespace Emberdelve.Data
{
    public enum WeaponKind { Melee, Ranged }

    /// <summary>
    /// All numbers that define how a weapon behaves. Combat components read this asset; they never
    /// hard-code damage, ranges or timings.
    /// </summary>
    [CreateAssetMenu(menuName = "Emberdelve/03 Weapon", fileName = "WeaponData")]
    public class WeaponData : ScriptableObject
    {
        [Header("Identity")]
        public string weaponId = "weapon";
        public string displayName = "Weapon";
        public WeaponKind kind = WeaponKind.Melee;

        [Header("Damage")]
        [Range(1f, 200f)] public float baseDamage = 22f;
        public DamageType damageType = DamageType.Physical;

        [Header("Timing (seconds)")]
        [Range(0f, 2f)] public float windupTime = 0.26f;
        [Range(0.02f, 1f)] public float activeWindow = 0.14f;
        [Range(0f, 2f)] public float recoveryTime = 0.28f;
        [Range(0.1f, 4f)] public float cooldown = 0.95f;

        [Header("Melee reach")]
        public float reach = 2.2f;
        [Range(30f, 180f)] public float arcAngle = 110f;
        public float hitSphereRadius = 0.6f;

        [Header("Ranged")]
        public float rangedRange = 42f;
        public float projectileSpeed = 36f;
        public float projectileRadius = 0.07f;
        public float projectileLength = 0.9f;
        [Range(0.1f, 3f)] public float drawTime = 0.8f;
        [Range(0f, 1f)] public float minChargeToFire = 0.18f;
        [Range(0.1f, 1f)] public float minChargeDamageScale = 0.45f;
        public int magazineSize = 7;
        public float reloadTime = 1.7f;
        public float falloffStartRange = 16f;
        public float falloffEndRange = 42f;
        [Range(0.1f, 1f)] public float falloffMinScale = 0.45f;

        [Header("Blockout visuals (replace with real weapon models later)")]
        public Material shaftMaterial;
        public Material edgeMaterial;
        public Material gripMaterial;

        public bool IsRanged => kind == WeaponKind.Ranged;

        public float DamageAtRange(float distance)
        {
            if (kind != WeaponKind.Ranged || falloffEndRange <= falloffStartRange) return baseDamage;
            if (distance <= falloffStartRange) return baseDamage;
            float t = Mathf.Clamp01((distance - falloffStartRange) / (falloffEndRange - falloffStartRange));
            return Mathf.Lerp(baseDamage, baseDamage * falloffMinScale, t);
        }
    }
}
