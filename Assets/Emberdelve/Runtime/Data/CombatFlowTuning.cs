using System;
using UnityEngine;
using Emberdelve.BladeDefense;

namespace Emberdelve.Data
{
    /// <summary>
    /// D1b combat-flow tunables that belong to no single weapon: what a broken guard costs. Our own numbers
    /// (Docs/Tasks/D1b.md), data on <see cref="Emberdelve.Core.GameConfig.flow"/>; a config that predates this slice
    /// deserializes to these defaults. Every consumer reads them from the combat authority's config, never from
    /// an intent.
    /// </summary>
    [Serializable]
    public sealed class CombatFlowTuning
    {
        public const float DefaultGuardBreakReductionScale = 0.30f;
        public const float DefaultGuardStaggerSeconds = 0.80f;
        public const float DefaultGuardStaggerMoveMultiplier = 0.60f;

        /// <summary>D1c2: the review's example anti-cheat ceiling, degrees per second of yaw and of pitch.</summary>
        public const float DefaultSwingTurnCeilingDegPerSec = 720f;

        [Tooltip("A BROKEN guard still removes this fraction of its normal reduction (0.3 = only 30 percent of the usual mitigation).")]
        [Range(0f, 1f)] public float guardBreakReductionScale = DefaultGuardBreakReductionScale;
        [Tooltip("After a guard break the defender cannot raise the guard for this long (authority clock).")]
        [Range(0f, 3f)] public float guardStaggerSeconds = DefaultGuardStaggerSeconds;
        [Tooltip("Move-speed multiplier while the guard stagger lasts.")]
        [Range(0.1f, 1f)] public float guardStaggerMoveMultiplier = DefaultGuardStaggerMoveMultiplier;

        [Tooltip("D1c2 anti-cheat ceiling: while a swing runs (windup, active, recovery) the aim pose the authority sweeps follows the reported pose at no more than this many degrees per second, yaw and pitch each. Excess is discarded, not banked. Convention: 0 (an asset saved before D1c2) = the default 720; a NEGATIVE value = unlimited (test seam / mutation checks); a positive value is used as is.")]
        public float swingTurnCeilingDegPerSec = DefaultSwingTurnCeilingDegPerSec;

        [Header("U3C directional height defense (data-driven; Docs/Tasks/U3C.md)")]
        [Tooltip("Height band an attack of each trajectory arrives in. Horizontal and thrust arrive at Mid, an overhead at High.")]
        public BladeHeight horizontalHeight = BladeHeight.Mid;
        public BladeHeight overheadHeight = BladeHeight.High;
        public BladeHeight thrustHeight = BladeHeight.Mid;
        [Tooltip("A blade guard covers attack bands up to this many bands away from its own (1 = Mid covers everything, Low cannot stop an overhead).")]
        [Range(0, 2)] public int bladeHeightTolerance = 1;
        [Tooltip("A shield is large: it covers attack bands up to this many bands away (2 = every band).")]
        [Range(0, 2)] public int shieldHeightTolerance = 2;
        [Tooltip("The defender's view pitch (degrees) at which the guard is raised to High (looking up) ...")]
        [Range(5f, 80f)] public float guardHighPitchDegrees = BladeGuardRules.DefaultHighPitchDegrees;
        [Tooltip("... and lowered to Low (looking down). Between the two it is Mid.")]
        [Range(5f, 80f)] public float guardLowPitchDegrees = BladeGuardRules.DefaultLowPitchDegrees;

        [Header("U3C guard endurance (0 = off: the D1b/C2 decision, blocking is free)")]
        [Tooltip("Size of the guard endurance pool. 0 or less = blocking costs nothing and cannot run dry (the shipped default). Above 0 every held hit pays its cost and an empty pool cannot block.")]
        public float guardEnduranceMax = 0f;
        [Tooltip("Endurance regained per second once the regen delay has passed.")]
        public float guardEnduranceRegenPerSecond = 20f;
        [Tooltip("Seconds after the last held hit before regeneration starts.")]
        public float guardEnduranceRegenDelay = 1f;
        [Tooltip("Cost of one held hit before the impact term.")]
        public float guardBlockCostBase = 10f;
        [Tooltip("Extra cost per point of the attacker's impact power.")]
        public float guardBlockCostPerImpact = 0.5f;

        [Header("U3C committed swing")]
        [Tooltip("true = a swing's sweep keeps the aim it had when it began (the warning is then exact for the whole swing). false (default) = D1c2 behaviour: the sweep follows the live aim at the turn ceiling.")]
        public bool commitSwingAim = false;

        /// <summary>True when blocking costs endurance (a finite pool above zero).</summary>
        public bool GuardEnduranceEnabled => guardEnduranceMax > 0f && !float.IsNaN(guardEnduranceMax) && !float.IsInfinity(guardEnduranceMax);

        /// <summary>The height band an attack of <paramref name="trajectory"/> arrives in, from this tuning's table.</summary>
        public BladeHeight HeightFor(BladeTrajectory trajectory) =>
            BladeGuardRules.HeightOf(trajectory, horizontalHeight, overheadHeight, thrustHeight);

        /// <summary>
        /// The ceiling the authority enforces, in degrees per second: the value itself when positive, the default when
        /// the field is 0 (old asset, never authored) or not a finite number, <see cref="float.PositiveInfinity"/> when it is
        /// negative (unlimited).
        /// </summary>
        public float ResolvedSwingTurnCeiling
        {
            get
            {
                float v = swingTurnCeilingDegPerSec;
                if (float.IsNaN(v) || float.IsPositiveInfinity(v)) return DefaultSwingTurnCeilingDegPerSec;
                if (v < 0f) return float.PositiveInfinity;
                return v > 0f ? v : DefaultSwingTurnCeilingDegPerSec;
            }
        }

        /// <summary>The finite ceiling the sample grid is sized for: the resolved ceiling, or the default when the turn is unlimited.</summary>
        public float GridSwingTurnCeiling
        {
            get
            {
                float c = ResolvedSwingTurnCeiling;
                return float.IsInfinity(c) ? DefaultSwingTurnCeilingDegPerSec : c;
            }
        }

        public static CombatFlowTuning Default => new CombatFlowTuning();
    }
}
