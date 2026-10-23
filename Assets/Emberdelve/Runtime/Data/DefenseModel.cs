using System;
using UnityEngine;

namespace Emberdelve.Data
{
    /// <summary>
    /// What a piece of kit is allowed to do defensively. Block and Parry are deliberately separate
    /// flags: a shield may only ever Block, while a weapon such as a later sword or axe can carry
    /// Parry for the parry system. Nothing in the runtime consumes Parry yet, which is the point -
    /// the capability exists in data long before the timing window does.
    /// </summary>
    [Flags]
    public enum DefenseCapability
    {
        None = 0,
        Block = 1 << 0,
        Parry = 1 << 1,
    }

    /// <summary>How the item is held. Shield is the one role that hard-forbids Parry.</summary>
    public enum DefenseRole
    {
        None = 0,
        Weapon = 1,
        Shield = 2,
    }

    /// <summary>
    /// The defensive kit resolved from data assets for one entity: which capabilities are available,
    /// how much a block removes, and how wide the block arc is. Built only from ScriptableObjects, so
    /// a new class, weapon or shield is a new asset rather than a new code path.
    /// </summary>
    public readonly struct DefenseProfile
    {
        public readonly DefenseCapability Capabilities;
        public readonly float BlockReduction;
        public readonly float BlockArcDegrees;
        public readonly bool HasShield;
        public readonly bool CanBlock;
        public readonly bool CanParry;

        /// <summary>D1b: impact resist of what is blocking (the better of a contributing shield and the held weapon). A hit with impact power at or above it breaks the guard.</summary>
        public readonly float ImpactResist;

        public DefenseProfile(DefenseCapability capabilities, float reduction, float arc, bool hasShield, float impactResist = 0f)
        {
            ImpactResist = Mathf.Max(0f, impactResist);
            Capabilities = Sanitize(capabilities, hasShield);
            BlockReduction = Mathf.Clamp01(reduction);
            BlockArcDegrees = Mathf.Clamp(arc, 30f, 360f);
            HasShield = hasShield;
            CanBlock = (Capabilities & DefenseCapability.Block) != 0 && BlockReduction > 0f;
            CanParry = (Capabilities & DefenseCapability.Parry) != 0;
        }

        /// <summary>
        /// The single rule that keeps shields honest: a shield can block, and can never parry, no
        /// matter what gets ticked in the Inspector. Everything else is free to declare Parry.
        /// </summary>
        public static DefenseCapability Sanitize(DefenseCapability capabilities, bool isShield)
        {
            return isShield ? capabilities & ~DefenseCapability.Parry : capabilities;
        }

        public static DefenseProfile Resolve(CharacterClassData data, WeaponData primary, WeaponData shield,
            float passiveBlockBonus = 0f)
        {
            // A two-handed main hand occupies both hands, so an off-hand shield contributes nothing for as
            // long as that weapon is held. This is why a Warrior on the crossbow cannot block, and why the
            // same Warrior on the sword can.
            bool shieldContributes = shield != null && !(primary != null && primary.twoHanded);

            DefenseCapability capabilities = DefenseCapability.None;
            if (primary != null) capabilities |= primary.EffectiveCapabilities;
            if (shieldContributes) capabilities |= shield.EffectiveCapabilities;

            bool hasShield = shield != null && shield.IsShield;
            float reduction = data != null ? data.blockReduction : 0f;
            float arc = data != null ? data.blockArc : 120f;

            // An off-hand shield overrides the class numbers only when it defines its own and is actually
            // being carried in a free hand.
            if (shieldContributes && shield.blockReductionOverride >= 0f) reduction = shield.blockReductionOverride;
            if (shieldContributes && shield.blockArcOverride >= 0f) arc = shield.blockArcOverride;

            // Passive training is applied last so it improves whatever the kit actually resolved, class
            // numbers or shield numbers, instead of being tied to one of them.
            reduction += Mathf.Max(0f, passiveBlockBonus);

            // Impact resist comes from the item that actually blocks: a weapon that can block brings its own, a
            // contributing shield brings its own, and the better one holds the guard.
            float impactResist = 0f;
            if (primary != null && primary.CanBlock) impactResist = Mathf.Max(impactResist, primary.impactResist);
            if (shieldContributes && shield.CanBlock) impactResist = Mathf.Max(impactResist, shield.impactResist);

            return new DefenseProfile(capabilities, reduction, arc, hasShield, impactResist);
        }

        public static DefenseProfile Empty => new DefenseProfile(DefenseCapability.None, 0f, 0f, false);

        public override string ToString()
        {
            return $"block={CanBlock} parry={CanParry} shield={HasShield} reduction={BlockReduction:F2} arc={BlockArcDegrees:F0} resist={ImpactResist:F0}";
        }
    }
}
