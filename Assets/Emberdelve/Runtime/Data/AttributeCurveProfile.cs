using UnityEngine;
using Emberdelve.Stats;

namespace Emberdelve.Data
{
    /// <summary>Authored attribute curves for locomotion, action, interaction, cooldown and skill power. Captured once when a class is initialized.</summary>
    [CreateAssetMenu(menuName = "Emberdelve/Attribute Curve Profile", fileName = "AttributeCurveProfile")]
    public sealed class AttributeCurveProfile : ScriptableObject
    {
        [Tooltip("Strictly increasing score coordinates and positive finite factors. Linear interpolation; held endpoints.")]
        public CurvePoint[] movement = MovementCurveDefaults.CreatePoints();

        [Tooltip("DEX score to swept-melee, reload and local ordinary draw rate. Positive finite factors; no skill scaling.")]
        public CurvePoint[] action = ActionSpeedCurveDefaults.CreatePoints();

        [Tooltip("CHA score to chest-opening rate. Positive finite factors; extraction and other holds are unchanged.")]
        public CurvePoint[] interaction = InteractionCurveDefaults.CreatePoints();

        [Tooltip("CHA score to skill cooldown duration multiplier. Positive finite values; minimum.4 enforces at most60% reduction.")]
        public CurvePoint[] skillCooldown = SkillCooldownCurveDefaults.CreatePoints();

        [Tooltip("WIS score to beneficial skill magnitude/duration. Positive finite factors, bounded.6..1.4 at runtime.")]
        public CurvePoint[] skillPower = SkillPowerCurveDefaults.CreatePoints();

        public bool TryCaptureMovement(out PiecewiseLinearCurve curve, out string error)
        {
            if (!PiecewiseLinearCurve.TryCreate(movement, out curve, out error)) return false;
            for (int i = 0; i < curve.Count; i++)
            {
                if (curve.GetPoint(i).value > 0f) continue;
                curve = null;
                error = "Movement factors must be positive.";
                return false;
            }
            return true;
        }

        public bool TryCaptureAction(out PiecewiseLinearCurve curve, out string error)
        {
            if (!PiecewiseLinearCurve.TryCreate(action, out curve, out error)) return false;
            for (int i = 0; i < curve.Count; i++)
            {
                if (curve.GetPoint(i).value > 0f) continue;
                curve = null;
                error = "Action factors must be positive.";
                return false;
            }
            return true;
        }

        public static PiecewiseLinearCurve CaptureAction(AttributeCurveProfile profile)
        {
            if (profile == null) return ActionSpeedCurveDefaults.Curve;
            if (profile.TryCaptureAction(out PiecewiseLinearCurve curve, out string error)) return curve;
            Debug.LogWarning("[Emberdelve][AttributeCurve] Invalid action profile '" + profile.name +
                "': " + error + " Using D4b1 defaults.", profile);
            return ActionSpeedCurveDefaults.Curve;
        }

        /// <summary>Null and invalid assets fall back to the exact same immutable default table.</summary>
        public static PiecewiseLinearCurve CaptureMovement(AttributeCurveProfile profile)
        {
            if (profile == null) return MovementCurveDefaults.Curve;
            if (profile.TryCaptureMovement(out PiecewiseLinearCurve curve, out string error)) return curve;
            Debug.LogWarning("[Emberdelve][AttributeCurve] Invalid movement profile '" + profile.name +
                "': " + error + " Using D4a defaults.", profile);
            return MovementCurveDefaults.Curve;
        }

        public bool TryCaptureInteraction(out PiecewiseLinearCurve curve, out string error)
        {
            if (!PiecewiseLinearCurve.TryCreate(interaction, out curve, out error)) return false;
            for (int i = 0; i < curve.Count; i++)
            {
                if (curve.GetPoint(i).value > 0f) continue;
                curve = null;
                error = "Interaction factors must be positive.";
                return false;
            }
            return true;
        }

        public static PiecewiseLinearCurve CaptureInteraction(AttributeCurveProfile profile)
        {
            if (profile == null) return InteractionCurveDefaults.Curve;
            if (profile.TryCaptureInteraction(out PiecewiseLinearCurve curve, out string error)) return curve;
            Debug.LogWarning("[Emberdelve][AttributeCurve] Invalid interaction profile '" + profile.name +
                "': " + error + " Using D4c1 defaults.", profile);
            return InteractionCurveDefaults.Curve;
        }

        public bool TryCaptureSkillCooldown(out PiecewiseLinearCurve curve, out string error)
        {
            if (!PiecewiseLinearCurve.TryCreate(skillCooldown, out curve, out error)) return false;
            for (int i = 0; i < curve.Count; i++)
            {
                if (curve.GetPoint(i).value > 0f) continue;
                curve = null; error = "Skill cooldown multipliers must be positive."; return false;
            }
            return true;
        }

        public static PiecewiseLinearCurve CaptureSkillCooldown(AttributeCurveProfile profile)
        {
            if (profile == null) return SkillCooldownCurveDefaults.Curve;
            if (profile.TryCaptureSkillCooldown(out PiecewiseLinearCurve curve, out string error)) return curve;
            Debug.LogWarning("[Emberdelve][AttributeCurve] Invalid skill cooldown profile '" + profile.name +
                "': " + error + " Using D4c2 defaults.", profile);
            return SkillCooldownCurveDefaults.Curve;
        }

        public bool TryCaptureSkillPower(out PiecewiseLinearCurve curve, out string error)
        {
            if (!PiecewiseLinearCurve.TryCreate(skillPower, out curve, out error)) return false;
            for (int i = 0; i < curve.Count; i++)
            {
                if (curve.GetPoint(i).value > 0f) continue;
                curve = null; error = "Skill power factors must be positive."; return false;
            }
            return true;
        }

        public static PiecewiseLinearCurve CaptureSkillPower(AttributeCurveProfile profile)
        {
            if (profile == null) return SkillPowerCurveDefaults.Curve;
            if (profile.TryCaptureSkillPower(out PiecewiseLinearCurve curve, out string error)) return curve;
            Debug.LogWarning("[Emberdelve][AttributeCurve] Invalid skill power profile '" + profile.name +
                "': " + error + " Using D4d1 defaults.", profile);
            return SkillPowerCurveDefaults.Curve;
        }

    }
}
