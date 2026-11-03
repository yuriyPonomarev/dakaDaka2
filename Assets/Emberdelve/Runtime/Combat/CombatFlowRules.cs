using UnityEngine;
using Emberdelve.Data;

namespace Emberdelve.Combat
{
    /// <summary>
    /// What the combat authority knows about an entity's combat FLOW (D1b) that movement and the guard need:
    /// the move-speed factor of its current swing phase / guard stagger and whether its guard is staggered. Implemented by
    /// <see cref="LocalCombatAuthority"/>; a server would answer from the same replicated swing state, which is what
    /// lets it validate a client's reported speed.
    /// </summary>
    public interface ICombatFlowState
    {
        /// <summary>Move-speed multiplier from the entity's swing phase (windup / active / recovery) and guard stagger. 1 when it is doing neither.</summary>
        float GetMoveFactor(int combatId);

        /// <summary>True while the entity's guard is broken (it cannot raise the guard) on the authority clock.</summary>
        bool IsGuardStaggered(int combatId);

        /// <summary>True while a melee swing of this entity is running (windup, active or recovery) on the authority.</summary>
        bool IsSwingInProgress(int combatId);

        /// <summary>Seconds of guard stagger left (0 when none).</summary>
        float GuardStaggerRemaining(int combatId);
    }

    /// <summary>Supplies the combat-flow move-speed factor to <see cref="Emberdelve.Movement.PlayerMovement.ResolveGroundSpeed"/>.</summary>
    public interface IMoveFlowSource
    {
        float GetMoveFlowFactor();
    }

    /// <summary>Pure rules of the D1b combat flow: guard break and the held-weapon move penalty. No scene access.</summary>
    public static class CombatFlowRules
    {
        /// <summary>Reference scale of the held-weapon penalty points (a base move speed of 300 in the reference genre).</summary>
        public const float PenaltyPointScale = 300f;

        /// <summary>The held penalties together never take more than this fraction of the speed.</summary>
        public const float MinHeldFactor = 0.5f;

        /// <summary>
        /// A hit breaks a guard when it carries impact power (above zero) and that power reaches the defender's impact
        /// resist. A source without impact power (a skill's flat damage) never breaks a guard.
        /// </summary>
        public static bool IsGuardBroken(float impactPower, float impactResist)
        {
            // U3C: one rule for the pure layer and the runtime (Emberdelve.BladeDefense.BladeGuardRules).
            return Emberdelve.BladeDefense.BladeGuardRules.IsGuardBroken(impactPower, impactResist);
        }

        /// <summary>Move-speed factor of held gear: 1 - (main-hand penalty + a contributing off-hand shield's) / 300, floored.</summary>
        public static float HeldFactor(WeaponData mainHand, WeaponData offHand, bool offHandContributes)
        {
            return HeldFactor(mainHand, offHand, offHandContributes, 0f);
        }

        /// <summary>
        /// D3c: the same factor with the penalty of the WORN gear (ItemData.moveSpeedPenalty summed over the live pieces, in the same
        /// reference points) added to the held weapons' points before the one division and the one floor: 1 - (held + worn) / 300,
        /// never below <see cref="MinHeldFactor"/>. The worn gear therefore reaches ResolveGroundSpeed through this single factor,
        /// exactly once. Negative or NaN worn points count as 0.
        /// </summary>
        public static float HeldFactor(WeaponData mainHand, WeaponData offHand, bool offHandContributes, float wornPenaltyPoints)
        {
            float points = 0f;
            if (mainHand != null) points += Mathf.Max(0f, mainHand.heldMoveSpeedPenalty);
            if (offHand != null && offHandContributes) points += Mathf.Max(0f, offHand.heldMoveSpeedPenalty);
            if (wornPenaltyPoints > 0f) points += wornPenaltyPoints;
            return Mathf.Clamp(1f - points / PenaltyPointScale, MinHeldFactor, 1f);
        }

        /// <summary>Move multiplier of a swing at <paramref name="elapsed"/> seconds (windup / active / recovery); 1 once it is over.</summary>
        public static float SwingPhaseFactor(SwingPatternData pattern, in SwingTiming timing, float elapsed, bool stopped)
        {
            if (pattern == null) return 1f;
            if (elapsed < 0f) return 1f;
            if (stopped) return pattern.recoveryMoveMultiplier;
            if (elapsed < timing.Windup) return pattern.windupMoveMultiplier;
            if (elapsed <= timing.ActiveEnd) return pattern.activeMoveMultiplier;
            if (elapsed < timing.Total) return pattern.recoveryMoveMultiplier;
            return 1f;
        }
    }
}
