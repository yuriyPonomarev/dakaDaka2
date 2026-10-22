using UnityEngine;
using Emberdelve.Core;
using Emberdelve.Stats;

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
        [Tooltip("Finesse marker (Docs/Design/ATTRIBUTES.md): this weapon scales off the better of the STR and DEX " +
                 "modifiers instead of STR alone. It is authored data on the definition - the runtime never looks at " +
                 "a class name, an item name or a weapon id to decide the scale. Ranged weapons are DEX-scaled.")]
        public bool finesse = false;

        [Header("Timing (seconds)")]
        [Range(0f, 2f)] public float windupTime = 0.26f;
        [Range(0.02f, 1f)] public float activeWindow = 0.14f;
        [Range(0f, 2f)] public float recoveryTime = 0.28f;
        [Range(0.1f, 4f)] public float cooldown = 0.95f;

        [Header("Melee reach")]
        public float reach = 2.2f;
        [Range(30f, 180f)] public float arcAngle = 110f;
        public float hitSphereRadius = 0.6f;

        [Header("Swept melee (D1a)")]
        [Tooltip("The swing patterns of this weapon at combo step 0 (D1b adds chained steps as more data). Empty = the legacy arc/overlap melee, which only enemies still use.")]
        public SwingPatternData[] swingPatterns = new SwingPatternData[0];

        [Header("Hands")]
        [Tooltip("A two-handed weapon occupies both hands, so an off-hand shield cannot contribute its block while it is held.")]
        public bool twoHanded = false;

        [Header("Ranged")]
        [Tooltip("Name used by the HUD ammo readout, e.g. Arrows or Bolts.")]
        public string ammoDisplayName = "Ammo";
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

        [Header("Defense capability")]
        [Tooltip("Shield role hard-forbids Parry: a shield can only ever block.")]
        public DefenseRole defenseRole = DefenseRole.Weapon;
        [Tooltip("Block reduces incoming damage behind an arc. Parry is reserved for the future parry system and is not consumed by the runtime yet.")]
        public DefenseCapability capabilities = DefenseCapability.None;
        [Tooltip("-1 means use the class value.")]
        [Range(-1f, 0.95f)] public float blockReductionOverride = -1f;
        [Tooltip("-1 means use the class value.")]
        [Range(-1f, 360f)] public float blockArcOverride = -1f;

        [Header("Blockout visuals (replace with real weapon models later)")]
        public Material shaftMaterial;
        public Material edgeMaterial;
        public Material gripMaterial;

        public bool IsRanged => kind == WeaponKind.Ranged;

        /// <summary>True when a player/entity swings this weapon through the authority's swept-segment melee.</summary>
        public bool UsesSweptMelee => !IsRanged && swingPatterns != null && swingPatterns.Length > 0;

        /// <summary>
        /// Resolves a (combat step, pattern index) pair to the pattern the weapon allows. The combo step is the
        /// seam D1b needs: today only step 0 exists, so any other step is refused; D1b adds data (a chain per
        /// step) behind this one function instead of touching the authority or the client.
        /// </summary>
        public bool TryResolveSwingPattern(int comboStep, int patternIndex, out SwingPatternData pattern)
        {
            pattern = null;
            if (comboStep != 0) return false;
            if (swingPatterns == null || patternIndex < 0 || patternIndex >= swingPatterns.Length) return false;
            pattern = swingPatterns[patternIndex];
            return pattern != null;
        }

        /// <summary>First pattern of a kind at combo step 0, or -1.</summary>
        public int FindSwingPattern(SwingKind kind)
        {
            if (swingPatterns == null) return -1;
            for (int i = 0; i < swingPatterns.Length; i++)
            {
                if (swingPatterns[i] != null && swingPatterns[i].kind == kind) return i;
            }
            return -1;
        }

        /// <summary>
        /// Which ability score this weapon's damage scales off, decided by the definition's own data:
        /// the finesse marker first, then ranged/melee. Every consumer (StatsComponent query and the
        /// authority's outgoing multiplier) reads this one property, so a query and the resolved damage
        /// cannot disagree and no caller has to classify the weapon by hand.
        /// </summary>
        public WeaponDamageScale DamageScale => finesse
            ? WeaponDamageScale.Finesse
            : (IsRanged ? WeaponDamageScale.Ranged : WeaponDamageScale.Melee);

        public bool IsShield => defenseRole == DefenseRole.Shield;

        /// <summary>Capabilities this item actually has, with the shield rule already applied.</summary>
        public DefenseCapability EffectiveCapabilities => DefenseProfile.Sanitize(capabilities, IsShield);
        public bool CanBlock => (EffectiveCapabilities & DefenseCapability.Block) != 0;
        public bool CanParry => (EffectiveCapabilities & DefenseCapability.Parry) != 0;

        private void OnValidate()
        {
            // Keeps the serialized asset consistent with the rule even if someone ticks Parry on a shield.
            capabilities = DefenseProfile.Sanitize(capabilities, IsShield);
        }

        /// <summary>
        /// The weapon's own damage at that distance: the authored base, optionally boosted by a FLAT amount
        /// (U2B7 rolled melee_damage / projectile_damage) that joins the base BEFORE the falloff rule. The
        /// existing curve is reused unchanged on the boosted base - the bonus is never added after falloff,
        /// charge, a multiplier or a headshot - so a bonus of 0 reproduces the previous number exactly and a
        /// weapon inside its falloff start still returns its (boosted) base.
        ///
        /// The asset is never written: <see cref="baseDamage">baseDamage</see> stays the authored value and the
        /// boost is a local. A NaN, ±Infinity, negative or overflow-sized claim contributes 0 rather than
        /// poisoning the resolved hit (see <see cref="SanitizeBaseDamageBonus"/>).
        /// </summary>
        public float DamageAtRange(float distance, float baseDamageBonus = 0f)
        {
            float boosted = BoostedBaseDamage(baseDamageBonus);

            if (kind != WeaponKind.Ranged || falloffEndRange <= falloffStartRange) return boosted;
            if (distance <= falloffStartRange) return boosted;
            float t = Mathf.Clamp01((distance - falloffStartRange) / (falloffEndRange - falloffStartRange));
            return Mathf.Lerp(boosted, boosted * falloffMinScale, t);
        }

        /// <summary>
        /// The one sanitization rule for a flat base-damage claim, applied wherever such a claim enters the
        /// weapon path: non-finite and negative claims are 0. Public because the provider, the authority and
        /// the resolver all hand a claim through this same gate instead of each inventing its own.
        /// </summary>
        public static float SanitizeBaseDamageBonus(float bonus)
        {
            if (float.IsNaN(bonus) || float.IsInfinity(bonus) || bonus < 0f) return 0f;
            return bonus;
        }

        /// <summary>
        /// Authored base + sanitized flat bonus. A claim large enough to overflow the sum falls back to the
        /// authored base alone, so the falloff rule always scales a finite number.
        /// </summary>
        public float BoostedBaseDamage(float baseDamageBonus = 0f)
        {
            float bonus = SanitizeBaseDamageBonus(baseDamageBonus);
            float boosted = baseDamage + bonus;
            if (float.IsNaN(boosted) || float.IsInfinity(boosted)) return baseDamage;
            return boosted;
        }
    }
}
