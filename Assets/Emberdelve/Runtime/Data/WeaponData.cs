using System;
using UnityEngine;
using Emberdelve.Ballistics;
using Emberdelve.Core;
using Emberdelve.Stats;

namespace Emberdelve.Data
{
    public enum WeaponKind { Melee, Ranged }

    /// <summary>
    /// One step of a weapon's combo chain (D1b). The chain is DATA on the weapon: step k swings
    /// <see cref="WeaponData.swingPatterns"/>[patternIndex] with this step's damage multiplier and optional timing
    /// tweaks. The authority, not the client, decides which step comes next.
    /// </summary>
    [Serializable]
    public struct ComboStep
    {
        [Tooltip("Index into WeaponData.swingPatterns that this step swings.")]
        public int patternIndex;
        [Tooltip("Damage multiplier of this step (reference shape: 1.00 / 1.05 / 1.10).")]
        [Range(0.1f, 3f)] public float damageMultiplier;
        [Tooltip("Scales the pattern's windup for this step (1 = as authored, below 1 = a quicker follow-up).")]
        [Range(0.2f, 2f)] public float windupScale;
        [Tooltip("Scales the pattern's recovery for this step.")]
        [Range(0.2f, 2f)] public float recoveryScale;

        public ComboStep(int patternIndex, float damageMultiplier, float windupScale = 1f, float recoveryScale = 1f)
        {
            this.patternIndex = patternIndex;
            this.damageMultiplier = damageMultiplier;
            this.windupScale = windupScale;
            this.recoveryScale = recoveryScale;
        }
    }

    /// <summary>
    /// Impact-zone band (D1b): from <see cref="from"/> (0 = the hilt end of the damage segment, 1 = the tip) up to the
    /// next band, a hit does <see cref="multiplier"/> of its damage.
    /// </summary>
    [Serializable]
    public struct ImpactZone
    {
        [Range(0f, 1f)] public float from;
        [Range(0.1f, 1.5f)] public float multiplier;

        public ImpactZone(float from, float multiplier)
        {
            this.from = from;
            this.multiplier = multiplier;
        }
    }

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

        [Header("Combo chain (D1b)")]
        [Tooltip("Ordered chain steps. Empty = a single-pattern weapon (step 0 only). The authority accepts step k only when step k-1 of this chain ended within the combo window.")]
        public ComboStep[] comboSteps = new ComboStep[0];
        [Tooltip("Seconds after the RECOVERY of a step begins within which the next primary press continues the chain; later it starts again at step 0.")]
        [Range(0.1f, 3f)] public float comboWindow = 0.70f;
        [Tooltip("Extra recovery (seconds) added after the LAST step of the chain, which then resets.")]
        [Range(0f, 2f)] public float comboFinisherRecovery = 0.35f;

        [Header("Impact zones (D1b)")]
        [Tooltip("Damage by where on the damage segment the contact happens (parameter 0 = hilt end, 1 = tip). Bands ascend by From; empty = every contact does full damage.")]
        public ImpactZone[] impactZones = new ImpactZone[0];

        [Header("Hit slow (D1b)")]
        [Tooltip("Move-speed multiplier a hit puts on its target (1 = none, 0.9 = -10 percent). The status uses the existing authority-owned Slow rules (refresh, strongest wins).")]
        [Range(0.1f, 1f)] public float hitSlowMultiplier = 1f;
        [Tooltip("Seconds the hit slow lasts (0 = none).")]
        [Range(0f, 5f)] public float hitSlowDuration = 0f;

        [Header("Move penalty and impact (D1b)")]
        [Tooltip("Held-weapon move-speed penalty in reference points on a 300 base (10 = -3.3 percent of the final ground speed). Applied by PlayerMovement.ResolveGroundSpeed while the weapon is held (an off-hand shield adds its own).")]
        [Range(0f, 100f)] public float heldMoveSpeedPenalty = 0f;
        [Tooltip("Impact power of this weapon's hits. A hit whose power (x the pattern scale) reaches the defender's impact resist breaks the guard.")]
        [Range(0f, 100f)] public float impactPower = 10f;
        [Tooltip("Impact resist of this item when it blocks: a hit with power >= this breaks the block (reduced mitigation and a guard stagger).")]
        [Range(0f, 100f)] public float impactResist = 15f;

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
        [Tooltip("Legacy RANGE falloff (metres from the muzzle). A projectile uses it only when the weapon has no air-time table below, or when the authority runs with ballistics off.")]
        public float falloffStartRange = 16f;
        public float falloffEndRange = 42f;
        [Range(0.1f, 1f)] public float falloffMinScale = 0.45f;

        [Header("Ballistics (D2)")]
        [Tooltip("Gravity on this weapon's projectile in m/s^2, straight down (muzzle speed is Projectile Speed above). 0 = a flat, drop-free flight, which is also what every weapon asset without this field gets.")]
        [Range(0f, 40f)] public float projectileGravity = 0f;
        [Tooltip("Longest time in the air in seconds before the projectile is removed. 0 = no time limit beyond Ranged Range (the path-length cap).")]
        [Range(0f, 10f)] public float projectileMaxFlightTime = 0f;
        [Tooltip("Damage scale by AIR TIME (seconds since launch, not distance): keys ascend by time, linear between them, held at the first/last key outside. Applied after location and the defender's guard/armor. Empty = no air-time falloff (the legacy range falloff above applies). Reference: a recurve bow does about 0.7x after 0.5 s.")]
        public ProjectileFalloffKey[] projectileFalloff = new ProjectileFalloffKey[0];

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

        /// <summary>True when this weapon carries an authored air-time falloff table (D2).</summary>
        public bool HasAirTimeFalloff => projectileFalloff != null && projectileFalloff.Length > 0;

        /// <summary>
        /// D2: the damage scale of one of this weapon's projectiles that has been airborne <paramref name="airTime"/> seconds,
        /// read from the authored table by the shared pure rule (<see cref="ProjectileBallistics.FalloffScale"/>). 1 with no table.
        /// </summary>
        public float AirTimeFalloffScale(float airTime) => ProjectileBallistics.FalloffScale(projectileFalloff, airTime);

        /// <summary>True when a player/entity swings this weapon through the authority's swept-segment melee.</summary>
        public bool UsesSweptMelee => !IsRanged && swingPatterns != null && swingPatterns.Length > 0;

        /// <summary>
        /// D1c5: the timings a card, tooltip or HUD should SHOW. The pattern is authoritative for a swept melee weapon (its first
        /// pattern, the one step 0 swings), so the display can never drift from what the authority runs. The serialized
        /// <c>windupTime</c> / <c>activeWindow</c> / <c>recoveryTime</c> fields stay as the data of weapons without patterns
        /// (ranged, legacy) and as the build's authoring seed; no reader outside this type should use them for a melee weapon.
        /// </summary>
        public SwingTiming DisplayTiming
        {
            get
            {
                if (UsesSweptMelee && swingPatterns[0] != null) return swingPatterns[0].Timing;
                return new SwingTiming(windupTime, activeWindow, recoveryTime, 0f);
            }
        }

        public float DisplayWindup => DisplayTiming.Windup;
        public float DisplayActive => DisplayTiming.Active;
        public float DisplayRecovery => DisplayTiming.Recovery;

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

        /// <summary>True when this weapon has an authored combo chain (D1b).</summary>
        public bool HasComboChain => UsesSweptMelee && comboSteps != null && comboSteps.Length > 0;

        public int ComboLength => HasComboChain ? comboSteps.Length : 1;

        /// <summary>
        /// Resolves chain step <paramref name="step"/> to its data and pattern. False (with the reason) when the weapon has
        /// no such step or the claimed pattern index is not the one the chain assigns to it. A weapon without a chain
        /// only has step 0 (the D1a rule).
        /// </summary>
        public bool TryResolveComboStep(int step, int patternIndex, out ComboStep data, out SwingPatternData pattern, out string reason)
        {
            data = new ComboStep(patternIndex, 1f);
            pattern = null;
            reason = string.Empty;

            if (!HasComboChain)
            {
                if (!TryResolveSwingPattern(step, patternIndex, out pattern))
                {
                    reason = $"pattern {patternIndex} at combo step {step} is not allowed for the equipped weapon";
                    return false;
                }
                return true;
            }

            if (step < 0 || step >= comboSteps.Length)
            {
                reason = $"combo step {step} does not exist in the weapon's {comboSteps.Length}-step chain";
                return false;
            }

            data = comboSteps[step];
            if (patternIndex != data.patternIndex)
            {
                reason = $"pattern {patternIndex} is not the chain's pattern {data.patternIndex} for combo step {step}";
                return false;
            }

            if (data.patternIndex < 0 || swingPatterns == null || data.patternIndex >= swingPatterns.Length || swingPatterns[data.patternIndex] == null)
            {
                reason = $"combo step {step} names a missing swing pattern";
                return false;
            }

            pattern = swingPatterns[data.patternIndex];
            return true;
        }

        /// <summary>Timing of combo step <paramref name="step"/>: the pattern's, scaled by the step, plus the finisher's extra recovery on the last step.</summary>
        public SwingTiming ComboTiming(int step, SwingPatternData pattern)
        {
            if (pattern == null) return default;
            if (!HasComboChain || step < 0 || step >= comboSteps.Length) return pattern.Timing;

            ComboStep data = comboSteps[step];
            float extra = step == comboSteps.Length - 1 ? comboFinisherRecovery : 0f;
            return pattern.TimingFor(data.windupScale <= 0f ? 1f : data.windupScale, data.recoveryScale <= 0f ? 1f : data.recoveryScale, extra);
        }

        /// <summary>
        /// Damage multiplier of a contact <paramref name="t"/> along the damage segment (0 = hilt end, 1 = tip): the band
        /// with the greatest <c>from</c> not above t. With no bands every contact is full damage.
        /// </summary>
        public float ImpactMultiplier(float t)
        {
            if (impactZones == null || impactZones.Length == 0) return 1f;
            t = float.IsNaN(t) ? 0f : Mathf.Clamp01(t);

            float result = impactZones[0].multiplier;
            float bestFrom = float.NegativeInfinity;
            for (int i = 0; i < impactZones.Length; i++)
            {
                if (impactZones[i].from <= t + 1e-6f && impactZones[i].from >= bestFrom)
                {
                    bestFrom = impactZones[i].from;
                    result = impactZones[i].multiplier;
                }
            }

            return Mathf.Max(0.05f, result);
        }

        /// <summary>The status a hit with this weapon puts on its target (none when the weapon has no hit slow).</summary>
        public StatusSpec HitSlowSpec => hitSlowDuration > 0f && hitSlowMultiplier < 1f
            ? new StatusSpec(StatusKind.Slow, hitSlowDuration, hitSlowMultiplier)
            : StatusSpec.None;

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
