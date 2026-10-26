using System;
using UnityEngine;

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
