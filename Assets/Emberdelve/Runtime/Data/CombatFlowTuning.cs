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

        [Tooltip("A BROKEN guard still removes this fraction of its normal reduction (0.3 = only 30 percent of the usual mitigation).")]
        [Range(0f, 1f)] public float guardBreakReductionScale = DefaultGuardBreakReductionScale;
        [Tooltip("After a guard break the defender cannot raise the guard for this long (authority clock).")]
        [Range(0f, 3f)] public float guardStaggerSeconds = DefaultGuardStaggerSeconds;
        [Tooltip("Move-speed multiplier while the guard stagger lasts.")]
        [Range(0.1f, 1f)] public float guardStaggerMoveMultiplier = DefaultGuardStaggerMoveMultiplier;

        public static CombatFlowTuning Default => new CombatFlowTuning();
    }
}
