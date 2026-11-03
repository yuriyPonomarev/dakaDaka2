using System;

namespace Emberdelve.BladeDefense
{
    /// <summary>
    /// U3C: the committed motion class of a melee swing. It comes from the authored swing pattern (SwingKind) and is fixed when the
    /// swing begins. Values are stable (they travel on the wire as a byte). Engine-free: this file lives in the Rules assembly.
    /// </summary>
    public enum BladeTrajectory
    {
        None = 0,
        Horizontal = 1,
        Overhead = 2,
        Thrust = 3
    }

    /// <summary>Height band of an attack or of a guard. Ordered Low &lt; Mid &lt; High; Unspecified is "no height" (skills, arrows, legacy hits).</summary>
    public enum BladeHeight
    {
        Unspecified = 0,
        Low = 1,
        Mid = 2,
        High = 3
    }

    /// <summary>Why a guard did or did not hold against one hit.</summary>
    public enum GuardOutcome
    {
        NotGuarding = 0,
        OutsideArc = 1,
        WrongHeight = 2,
        Exhausted = 3,
        Blocked = 4,
        Broken = 5
    }

    /// <summary>Everything the pure guard rule needs, as plain numbers (the caller reads the defender; nothing here touches a scene).</summary>
    public struct GuardInput
    {
        /// <summary>The guard is up, the kit can block, the defender is alive, not staggered and not swinging (the caller's decision).</summary>
        public bool Raised;
        public float BlockReduction;
        /// <summary>Total block arc in degrees (the guard holds inside +-arc/2 of the defender's facing).</summary>
        public float ArcDegrees;
        /// <summary>Angle in degrees between the defender's facing and the direction to the attacker (0 = straight ahead, 180 = behind).</summary>
        public float BearingDegrees;
        public bool HasShield;
        public BladeHeight GuardHeight;
        public BladeHeight AttackHeight;
        /// <summary>How many height bands apart the guard may be and still cover the attack (0 = same band only).</summary>
        public int HeightTolerance;
        public float ImpactPower;
        public float ImpactResist;
        /// <summary>Fraction of the normal mitigation a BROKEN guard keeps.</summary>
        public float BreakReductionScale;
        /// <summary>False = blocking costs nothing and cannot run dry (the D1b behaviour).</summary>
        public bool EnduranceEnabled;
        public float EnduranceCurrent;
        public float EnduranceCost;
    }

    /// <summary>The result of <see cref="BladeGuardRules.Resolve"/>. There is deliberately no parry/riposte member: no guard outcome produces one.</summary>
    public readonly struct GuardVerdict
    {
        public readonly GuardOutcome Outcome;
        /// <summary>Fraction of the damage removed (0 unless the guard held).</summary>
        public readonly float Reduction;
        /// <summary>Endurance the caller must spend, exactly once for this hit (0 unless the guard held).</summary>
        public readonly float EnduranceSpent;

        public GuardVerdict(GuardOutcome outcome, float reduction, float enduranceSpent)
        {
            Outcome = outcome;
            Reduction = reduction;
            EnduranceSpent = enduranceSpent;
        }

        public bool Mitigated => Outcome == GuardOutcome.Blocked || Outcome == GuardOutcome.Broken;
        public bool Broken => Outcome == GuardOutcome.Broken;
    }

    /// <summary>
    /// U3C: the pure directional guard rule. One call decides one hit, in this order:
    ///   1. guard not up                              -> NotGuarding (full damage)
    ///   2. attacker outside the front arc (behind)   -> OutsideArc  (full damage, costs nothing)
    ///   3. attack height not covered by guard height -> WrongHeight (full damage, costs nothing)
    ///   4. endurance enabled and the pool cannot pay -> Exhausted   (full damage, costs nothing)
    ///   5. impact power reaches impact resist        -> Broken      (reduction x break scale, pays the cost)
    ///   6. otherwise                                 -> Blocked     (full reduction, pays the cost)
    /// Shields and blades share the rule; a shield only differs by the height tolerance the caller passes. Nothing here parries.
    /// </summary>
    public static class BladeGuardRules
    {
        public const float DefaultHighPitchDegrees = 25f;
        public const float DefaultLowPitchDegrees = 25f;

        /// <summary>True when the attacker is inside the guard's front arc. NaN is outside.</summary>
        public static bool InsideArc(float bearingDegrees, float arcDegrees)
        {
            if (float.IsNaN(bearingDegrees) || float.IsNaN(arcDegrees)) return false;
            return bearingDegrees <= arcDegrees * 0.5f;
        }

        /// <summary>
        /// The guard band from the defender's view pitch (degrees, positive DOWN, the project convention): looking up by at least
        /// <paramref name="highPitchDegrees"/> raises the guard (High), looking down by at least <paramref name="lowPitchDegrees"/>
        /// lowers it (Low), anything else is Mid. A non-finite pitch is Mid; a non-positive or non-finite threshold uses the default.
        /// </summary>
        public static BladeHeight GuardHeightFromPitch(float pitchDegreesDown, float highPitchDegrees, float lowPitchDegrees)
        {
            if (float.IsNaN(pitchDegreesDown) || float.IsInfinity(pitchDegreesDown)) return BladeHeight.Mid;
            float high = Positive(highPitchDegrees, DefaultHighPitchDegrees);
            float low = Positive(lowPitchDegrees, DefaultLowPitchDegrees);
            if (pitchDegreesDown <= -high) return BladeHeight.High;
            if (pitchDegreesDown >= low) return BladeHeight.Low;
            return BladeHeight.Mid;
        }

        /// <summary>True when a guard in band <paramref name="guard"/> covers an attack in band <paramref name="attack"/>. An unspecified side is always covered.</summary>
        public static bool HeightCovered(BladeHeight guard, BladeHeight attack, int tolerance)
        {
            if (guard == BladeHeight.Unspecified || attack == BladeHeight.Unspecified) return true;
            int distance = Math.Abs((int)guard - (int)attack);
            return distance <= Math.Max(0, tolerance);
        }

        /// <summary>The height band an attack of this trajectory arrives in, from the three authored table entries.</summary>
        public static BladeHeight HeightOf(BladeTrajectory trajectory, BladeHeight horizontal, BladeHeight overhead, BladeHeight thrust)
        {
            switch (trajectory)
            {
                case BladeTrajectory.Horizontal: return horizontal;
                case BladeTrajectory.Overhead: return overhead;
                case BladeTrajectory.Thrust: return thrust;
                default: return BladeHeight.Unspecified;
            }
        }

        /// <summary>A hit breaks a guard when it carries impact power (above zero) and that power reaches the defender's impact resist.</summary>
        public static bool IsGuardBroken(float impactPower, float impactResist)
        {
            if (float.IsNaN(impactPower) || float.IsNaN(impactResist)) return false;
            return impactPower > 0f && impactPower >= impactResist;
        }

        /// <summary>Endurance one held hit costs: base + impactPower x perImpact. Negative, NaN and infinite parts count as 0.</summary>
        public static float BlockCost(float impactPower, float baseCost, float costPerImpact)
        {
            float cost = NonNegativeFinite(baseCost) + NonNegativeFinite(impactPower) * NonNegativeFinite(costPerImpact);
            return float.IsNaN(cost) || float.IsInfinity(cost) ? 0f : cost;
        }

        /// <summary>Shields never parry; a parry-capable weapon does. The one place the rule is written down for the pure layer.</summary>
        public static bool CanParry(bool hasShield, bool parryCapability) => !hasShield && parryCapability;

        public static GuardVerdict Resolve(in GuardInput g)
        {
            if (!g.Raised) return new GuardVerdict(GuardOutcome.NotGuarding, 0f, 0f);
            if (!InsideArc(g.BearingDegrees, g.ArcDegrees)) return new GuardVerdict(GuardOutcome.OutsideArc, 0f, 0f);
            if (!HeightCovered(g.GuardHeight, g.AttackHeight, g.HeightTolerance)) return new GuardVerdict(GuardOutcome.WrongHeight, 0f, 0f);

            float cost = g.EnduranceEnabled ? NonNegativeFinite(g.EnduranceCost) : 0f;
            if (g.EnduranceEnabled)
            {
                bool affordable = g.EnduranceCurrent > 0f && g.EnduranceCurrent >= cost;
                if (!affordable) return new GuardVerdict(GuardOutcome.Exhausted, 0f, 0f);
            }

            float reduction = Clamp01(g.BlockReduction);
            if (IsGuardBroken(g.ImpactPower, g.ImpactResist))
            {
                return new GuardVerdict(GuardOutcome.Broken, reduction * Clamp01(g.BreakReductionScale), cost);
            }

            return new GuardVerdict(GuardOutcome.Blocked, reduction, cost);
        }

        /// <summary>
        /// Classifies a swing path from its measured extents, so the authored SwingKind can be checked against the keys it ships with:
        /// a path that is mostly a forward line is a Thrust, one that sweeps more vertically than sideways is an Overhead, otherwise Horizontal.
        /// </summary>
        public static BladeTrajectory ClassifyPath(float yawSpanDegrees, float pitchSpanDegrees, float baseTravelMetres)
        {
            float yaw = NonNegativeFinite(yawSpanDegrees);
            float pitch = NonNegativeFinite(pitchSpanDegrees);
            float travel = NonNegativeFinite(baseTravelMetres);
            const float lineAngleLimit = 15f;
            const float lineMinTravel = 0.3f;
            if (yaw <= lineAngleLimit && pitch <= lineAngleLimit && travel >= lineMinTravel) return BladeTrajectory.Thrust;
            return pitch > yaw ? BladeTrajectory.Overhead : BladeTrajectory.Horizontal;
        }

        private static float Positive(float value, float fallback) =>
            float.IsNaN(value) || float.IsInfinity(value) || value <= 0f ? fallback : value;

        private static float NonNegativeFinite(float value) =>
            float.IsNaN(value) || float.IsInfinity(value) || value < 0f ? 0f : value;

        private static float Clamp01(float value) =>
            float.IsNaN(value) ? 0f : (value < 0f ? 0f : (value > 1f ? 1f : value));
    }

    /// <summary>
    /// U3C: the defender's guard endurance pool. Deterministic and step-size independent: regeneration is computed from the elapsed time
    /// since the last refresh, starting <c>regenDelay</c> seconds after the last spend. Disabled (max &lt;= 0) it holds nothing and never refuses.
    /// The clock is the combat authority's, passed in by the caller.
    /// </summary>
    public sealed class GuardEnduranceState
    {
        private float _current;
        private float _max;
        private float _stamp;
        private float _lastSpend = float.NegativeInfinity;
        private bool _started;

        public float Current => _current;
        public float Max => _max;
        public bool Started => _started;

        public void Refresh(float now, float max, float regenPerSecond, float regenDelay)
        {
            if (float.IsNaN(now) || float.IsInfinity(now)) return;
            if (float.IsNaN(max) || float.IsInfinity(max) || !(max > 0f))
            {
                _max = 0f;
                _current = 0f;
                _started = false;
                return;
            }

            if (!_started)
            {
                _started = true;
                _max = max;
                _current = max;
                _stamp = now;
                _lastSpend = float.NegativeInfinity;
                return;
            }

            if (now < _stamp)
            {
                _stamp = now;
            }
            else
            {
                float regen = float.IsNaN(regenPerSecond) || float.IsInfinity(regenPerSecond) || regenPerSecond < 0f ? 0f : regenPerSecond;
                float delay = float.IsNaN(regenDelay) || float.IsInfinity(regenDelay) || regenDelay < 0f ? 0f : regenDelay;
                float regenFrom = Math.Max(_stamp, _lastSpend + delay);
                float span = now - regenFrom;
                if (span > 0f && regen > 0f) _current = Math.Min(max, _current + span * regen);
                _stamp = now;
            }

            _max = max;
            if (_current > max) _current = max;
        }

        /// <summary>Pays <paramref name="amount"/>. False (nothing changes) when the pool is not started or cannot cover it; a zero amount always succeeds.</summary>
        public bool Spend(float amount, float now)
        {
            if (!(amount > 0f)) return true;
            if (!_started || _current + 1e-5f < amount) return false;
            _current = Math.Max(0f, _current - amount);
            _lastSpend = now;
            return true;
        }

        /// <summary>Refills the pool and forgets the last spend (respawn, test seam).</summary>
        public void Reset()
        {
            _current = _max;
            _lastSpend = float.NegativeInfinity;
        }
    }
}
