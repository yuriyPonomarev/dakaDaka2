using System;
using UnityEngine;

namespace Emberdelve.Data
{
    /// <summary>
    /// The numeric rules an affix band must satisfy, shared by authoring validation and rolled-value
    /// validation. Pure and total: every method is a function of its arguments, reads nothing from an
    /// asset and writes nothing anywhere, so checking a proposed roll can never normalize, clamp, round
    /// or otherwise touch the authored definition. Every failure comes back with a reason.
    ///
    /// Epsilon is a numeric-representation tolerance only (float 0.03f is not exactly 0.03), never a
    /// gameplay tolerance. It is used for value-space comparisons (band range, prototype containment,
    /// integrality of small authored numbers) where a relative budget is the right scale.
    ///
    /// Grid alignment is NOT checked that way: a relative tolerance on the index (span / step) grows with
    /// the index, so a fine step would swallow its own fractional offsets (index 500000.5 looked aligned at
    /// a tolerance of 0.5). Alignment is therefore measured in whole fractions of one step, bounded by the
    /// representation error of the inputs actually supplied and capped at MaxAlignmentSteps of a step, so
    /// no grid index - however large - can absorb a fractional step. A grid whose own representation error
    /// already exceeds that cap is reported as unvalidatable instead of quietly accepting every offset.
    /// </summary>
    public static class AffixBandRules
    {
        /// <summary>Relative representation tolerance used for range/prototype/integrality comparisons only.</summary>
        public const double Epsilon = 1e-6;

        /// <summary>
        /// Representation budget for an input that carries float precision (an Inspector float widened to
        /// double: 24 significant bits, so fl(x) is within 2^-24 = 5.96e-8 of the authored decimal), with
        /// headroom for the add/subtract/multiply/divide the validators perform on it.
        /// </summary>
        public const double FloatRepresentationRelative = 1e-6;

        /// <summary>
        /// Representation budget for an input supplied as a full double (2^-53 = 1.11e-16) plus headroom.
        /// A pure double API call gets this tighter budget, never the float one: it must not claim that a
        /// grid finer than the input precision is aligned.
        /// </summary>
        public const double DoubleRepresentationRelative = 8e-15;

        /// <summary>
        /// Hard ceiling on an accepted alignment residual, in fractions of one grid step, independent of
        /// the size of the grid index. Far below a half step, so a fractional step is never accepted.
        /// </summary>
        public const double MaxAlignmentSteps = 1e-2;

        /// <summary>2^52: above this a double can no longer hold consecutive whole indices exactly.</summary>
        public const double MaxValidGridIndex = 4.5e15;

        public static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        public static bool NearlyEqual(double a, double b)
        {
            if (!IsFinite(a) || !IsFinite(b)) return false;
            double scale = Math.Max(1.0, Math.Max(Math.Abs(a), Math.Abs(b)));
            return Math.Abs(a - b) <= Epsilon * scale;
        }

        /// <summary>True when the double is exactly one float value, i.e. it came from float authoring.</summary>
        public static bool IsFloatPrecision(double value)
        {
            if (!IsFinite(value)) return false;
            return (double)(float)value == value;
        }

        /// <summary>Representation budget of one input: float budget for a float-precision value, else double.</summary>
        public static double RepresentationRelative(double value)
        {
            return IsFloatPrecision(value) ? FloatRepresentationRelative : DoubleRepresentationRelative;
        }

        /// <summary>
        /// The largest alignment residual the inputs of one grid can honestly support, expressed in steps:
        /// the worst-case representation error of base/upper/step (and of the division that produces the
        /// index) divided by the step. Inputs take the loosest budget among them, because a float-authored
        /// band stays float-sloppy no matter how the value was produced.
        /// </summary>
        public static double AlignmentAllowanceInSteps(double baseValue, double upper, double step)
        {
            if (!IsFinite(baseValue) || !IsFinite(upper) || !IsFinite(step) || step == 0.0) return double.PositiveInfinity;

            double relative = RepresentationRelative(baseValue);
            relative = Math.Max(relative, RepresentationRelative(upper));
            relative = Math.Max(relative, RepresentationRelative(step));

            double magnitude = Math.Abs(baseValue) + Math.Abs(upper) + Math.Abs(upper - baseValue);
            return relative * magnitude / Math.Abs(step);
        }

        /// <summary>
        /// Whole-number test with a scale-independent budget: the residual to the nearest whole number may
        /// be as large as that value's own representation error, never as large as 1e-6 of the value.
        /// </summary>
        public static bool IsIntegral(double value)
        {
            if (!IsFinite(value)) return false;
            double nearest = Math.Round(value, MidpointRounding.AwayFromZero);
            return Math.Abs(value - nearest) <= RepresentationRelative(value) * Math.Max(1.0, Math.Abs(value));
        }

        /// <summary>
        /// Validates one authored band for one key, in this order, failing with the first reason:
        /// 1. the key is one of the allowlisted enum values;
        /// 2. min, max and step are all finite (NaN/Infinity rejected);
        /// 3. step is strictly positive;
        /// 4. min &lt;= max (a reversed range fails; min == max is a single-value band);
        /// 5. min and max both sit inside the key's prototype band from Docs/Design/ITEM_AFFIXES.md -
        ///    a narrower band is allowed, a wider one is not;
        /// 6. for the six attribute keys, min, max and step are all whole numbers and step is at least 1;
        /// 7. max == min + k*step for some integer k &gt;= 0, i.e. the top endpoint is actually reachable
        ///    from the bottom one by whole steps (this also rejects a step wider than the span).
        /// </summary>
        public static bool TryValidateBand(AffixStatKey key, double min, double max, double step, out string reason)
        {
            reason = null;

            if (!AffixStatKeys.IsKnown(key))
            {
                reason = $"unknown affix stat key value {(int)key}";
                return false;
            }

            if (!IsFinite(min) || !IsFinite(max) || !IsFinite(step))
            {
                reason = $"{AffixStatKeys.Key(key)}: min/max/step must all be finite numbers";
                return false;
            }

            if (step <= 0.0)
            {
                reason = $"{AffixStatKeys.Key(key)}: step must be greater than 0 (got {step})";
                return false;
            }

            if (min > max)
            {
                reason = $"{AffixStatKeys.Key(key)}: reversed range, min {min} is above max {max}";
                return false;
            }

            float protoMin, protoMax, protoStep;
            if (!AffixStatKeys.TryPrototypeBand(key, out protoMin, out protoMax, out protoStep))
            {
                reason = $"{AffixStatKeys.Key(key)}: no prototype band for this key";
                return false;
            }

            if ((min < protoMin && !NearlyEqual(min, protoMin)) ||
                (max > protoMax && !NearlyEqual(max, protoMax)))
            {
                reason = $"{AffixStatKeys.Key(key)}: band {min}..{max} is outside the prototype band " +
                         $"{protoMin}..{protoMax}";
                return false;
            }

            if (AffixStatKeys.IsIntegralKey(key))
            {
                if (!IsIntegral(min) || !IsIntegral(max))
                {
                    reason = $"{AffixStatKeys.Key(key)}: attribute bands must be whole numbers, got {min}..{max}";
                    return false;
                }

                if (!IsIntegral(step) || step < 1.0)
                {
                    reason = $"{AffixStatKeys.Key(key)}: attribute step must be a whole number of at least 1, got {step}";
                    return false;
                }
            }

            // The index itself is not needed here, only whether the top endpoint sits on a whole one.
            if (!TryGridIndex(min, max, step, out _, out string bandGridProblem))
            {
                reason = $"{AffixStatKeys.Key(key)}: max {max} is not min {min} + a whole number of steps " +
                         $"of {step} ({bandGridProblem})";
                return false;
            }

            return true;
        }

        /// <summary>
        /// Is upper exactly base + n*step for a whole n? The residual is measured in fractions of one step
        /// and compared against the representation budget of these very inputs, so the answer does not get
        /// sloppier as n grows. A grid whose budget already exceeds MaxAlignmentSteps of a step cannot be
        /// aligned at the precision it was authored with: that is reported as a failure, not waved through.
        /// </summary>
        private static bool TryGridIndex(double baseValue, double upper, double step, out double index,
            out string problem)
        {
            index = 0.0;
            problem = null;

            double span = upper - baseValue;
            if (!IsFinite(span))
            {
                problem = $"span {upper} - {baseValue} is not a finite number";
                return false;
            }

            double raw = span / step;
            double nearest = Math.Round(raw, MidpointRounding.AwayFromZero);
            if (!IsFinite(raw) || Math.Abs(nearest) > MaxValidGridIndex)
            {
                problem = $"span / step = {raw} is outside the range a double can hold as a whole index " +
                          $"(max {MaxValidGridIndex}), this grid cannot be validated";
                return false;
            }

            double allowance = AlignmentAllowanceInSteps(baseValue, upper, step);
            if (allowance > MaxAlignmentSteps)
            {
                problem = $"step {step} is finer than the {baseValue}..{upper} endpoints can be represented in " +
                          $"(their own representation error is up to {allowance} steps, more than " +
                          $"{MaxAlignmentSteps} of a step), this grid cannot be validated";
                return false;
            }

            double residualSteps = Math.Abs(span - nearest * step) / Math.Abs(step);
            if (residualSteps > allowance)
            {
                problem = $"span / step = {raw} is {residualSteps} steps away from the whole index {nearest}, " +
                          $"off-grid for this band (representation budget {allowance} steps)";
                return false;
            }

            index = nearest;
            return true;
        }

        /// <summary>
        /// Read-only check of one proposed rolled value against an authored band: the band itself must be
        /// valid, the value must be finite, must lie inside [min, max] (out-of-range is rejected, never
        /// clamped), must sit exactly on min + k*step for an integer k &gt;= 0 (off-step rejected), and must
        /// be a whole number for the six attribute keys. The authored min/max/step are only ever read.
        /// </summary>
        public static bool TryValidateValue(AffixStatKey key, double min, double max, double step,
            double value, out string reason)
        {
            if (!TryValidateBand(key, min, max, step, out reason)) return false;

            if (!IsFinite(value))
            {
                reason = $"{AffixStatKeys.Key(key)}: rolled value is not a finite number";
                return false;
            }

            if ((value < min && !NearlyEqual(value, min)) || (value > max && !NearlyEqual(value, max)))
            {
                reason = $"{AffixStatKeys.Key(key)}: rolled value {value} is outside the band {min}..{max}";
                return false;
            }

            if (!TryGridIndex(min, value, step, out _, out string valueGridProblem))
            {
                reason = $"{AffixStatKeys.Key(key)}: rolled value {value} is off-step for band {min}..{max} " +
                         $"step {step} ({valueGridProblem})";
                return false;
            }

            if (AffixStatKeys.IsIntegralKey(key) && !IsIntegral(value))
            {
                reason = $"{AffixStatKeys.Key(key)}: rolled attribute value {value} is not a whole number";
                return false;
            }

            return true;
        }
    }

    /// <summary>
    /// One authored affix definition (Docs/Design/ITEM_AFFIXES.md): a stable id, a display name, one
    /// allowlisted typed stat key, the band it rolls in, its step, its positive selection weight and the
    /// gear categories it may roll on. It is a reusable archetype - it stores no rolled result, no item
    /// reference, no UID and no rarity, and nothing here randomizes or persists anything.
    ///
    /// Fields are public for Inspector authoring like every other Emberdelve definition, and there is
    /// deliberately no OnValidate: validation reports what is wrong and changes nothing, so a broken asset
    /// stays visibly broken until an author fixes it instead of being quietly rewritten.
    /// </summary>
    [CreateAssetMenu(menuName = "Emberdelve/09 Affix", fileName = "AffixDefinition")]
    public class AffixDefinition : ScriptableObject
    {
        [Header("Identity")]
        [Tooltip("Stable id used by instances: lowercase letters, digits and underscores only, no spaces.")]
        public string id = "affix";
        public string displayName = "Affix";

        [Header("Stat key (allowlisted)")]
        [Tooltip("Typed key, never a free-form string. An unknown value is a validation failure.")]
        public AffixStatKey statKey = AffixStatKey.Strength;

        [Header("Authored roll band")]
        [Tooltip("Narrower than or equal to this key's prototype band. Endpoints must line up with min + k*step.")]
        public float minValue = 1f;
        public float maxValue = 3f;
        [Tooltip("Whole-number step for the six attribute keys. Must be > 0 for every key.")]
        public float step = 1f;

        [Header("Selection")]
        [Tooltip("Relative sampling weight for the future roller. Positive finite only; this slice does not roll.")]
        public float weight = 1f;
        [Tooltip("Gear families this affix may roll on (boots/cloak/armor/weapon). At least one, no unknown bits.")]
        public AffixCategory allowedCategories = AffixCategory.None;

        /// <summary>Canonical allowlisted key text for this definition's typed key.</summary>
        public string CanonicalKey => AffixStatKeys.Key(statKey);

        /// <summary>True when this key's authored and rolled amounts must be whole numbers.</summary>
        public bool RequiresIntegralValues => AffixStatKeys.IsIntegralKey(statKey);

        /// <summary>True when this key is one of the six ability scores.</summary>
        public bool IsAttributeAffix => AffixStatKeys.IsAttribute(statKey);

        /// <summary>
        /// Authoring validation with the first failure reason. Read-only: it never clamps, rounds, trims,
        /// reorders or otherwise rewrites this asset, and it never mutates minValue/maxValue/step/weight.
        /// </summary>
        public bool TryValidate(out string reason)
        {
            reason = null;

            if (!IsStableId(id))
            {
                reason = "affix id must be a non-empty stable id (lowercase letters, digits and underscores, " +
                         "starting with a letter)";
                return false;
            }

            if (string.IsNullOrWhiteSpace(displayName))
            {
                reason = $"affix '{id}': display name is empty";
                return false;
            }

            if (!AffixStatKeys.IsKnown(statKey))
            {
                reason = $"affix '{id}': unknown stat key value {(int)statKey}";
                return false;
            }

            if (!AffixBandRules.TryValidateBand(statKey, minValue, maxValue, step, out string bandReason))
            {
                reason = $"affix '{id}': {bandReason}";
                return false;
            }

            if (!AffixBandRules.IsFinite(weight) || weight <= 0f)
            {
                reason = $"affix '{id}': weight must be a positive finite number (got {weight})";
                return false;
            }

            if (!AffixCategories.IsKnownSet(allowedCategories))
            {
                reason = $"affix '{id}': allowed categories must name at least one known category, got " +
                         $"{AffixCategories.Describe(allowedCategories)}";
                return false;
            }

            return true;
        }

        /// <summary>True when this definition may roll on exactly one legal category bit.</summary>
        public bool AllowsCategory(AffixCategory category)
        {
            if (!AffixCategories.IsSingle(category)) return false;
            return (allowedCategories & category) == category;
        }

        /// <summary>
        /// Read-only validation of a proposed rolled value against this definition's band. Rejects NaN and
        /// Infinity, out-of-range values, off-step values and fractional attribute values, and reports the
        /// definition's own problems first. Nothing here writes to this asset.
        /// </summary>
        public bool TryValidateRolledValue(float value, out string reason)
        {
            if (!TryValidate(out reason)) return false;

            if (!AffixBandRules.TryValidateValue(statKey, minValue, maxValue, step, value, out string valueReason))
            {
                reason = $"affix '{id}': {valueReason}";
                return false;
            }

            return true;
        }

        /// <summary>
        /// Same value rules plus the category claim: the target must be exactly one legal category bit this
        /// definition allows. A multi-category or unknown claim is rejected rather than narrowed.
        /// </summary>
        public bool TryValidateRolledValue(float value, AffixCategory category, out string reason)
        {
            if (!TryValidate(out reason)) return false;

            if (!AffixCategories.IsSingle(category))
            {
                reason = $"affix '{id}': a rolled value needs exactly one target category, got " +
                         $"{AffixCategories.Describe(category)}";
                return false;
            }

            if (!AllowsCategory(category))
            {
                reason = $"affix '{id}' is not allowed on {AffixCategories.Describe(category)} (it allows " +
                         $"{AffixCategories.Describe(allowedCategories)})";
                return false;
            }

            if (!AffixBandRules.TryValidateValue(statKey, minValue, maxValue, step, value, out string valueReason))
            {
                reason = $"affix '{id}': {valueReason}";
                return false;
            }

            return true;
        }

        /// <summary>Stable-id shape: non-empty, starts with a lowercase letter, then lowercase letters, digits or '_'.</summary>
        public static bool IsStableId(string candidate)
        {
            if (string.IsNullOrEmpty(candidate)) return false;
            if (candidate[0] < 'a' || candidate[0] > 'z') return false;

            for (int i = 0; i < candidate.Length; i++)
            {
                char c = candidate[i];
                if (c >= 'a' && c <= 'z') continue;
                if (c >= '0' && c <= '9') continue;
                if (c == '_') continue;
                return false;
            }

            return true;
        }
    }
}
