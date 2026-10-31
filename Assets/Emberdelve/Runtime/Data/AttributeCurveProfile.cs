using UnityEngine;
using Emberdelve.Stats;

namespace Emberdelve.Data
{
    /// <summary>Authored DEX score to locomotion factor. Captured once when a class is initialized.</summary>
    [CreateAssetMenu(menuName = "Emberdelve/Attribute Curve Profile", fileName = "AttributeCurveProfile")]
    public sealed class AttributeCurveProfile : ScriptableObject
    {
        [Tooltip("Strictly increasing score coordinates and positive finite factors. Linear interpolation; held endpoints.")]
        public CurvePoint[] movement = MovementCurveDefaults.CreatePoints();

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

        /// <summary>Null and invalid assets fall back to the exact same immutable default table.</summary>
        public static PiecewiseLinearCurve CaptureMovement(AttributeCurveProfile profile)
        {
            if (profile == null) return MovementCurveDefaults.Curve;
            if (profile.TryCaptureMovement(out PiecewiseLinearCurve curve, out string error)) return curve;
            Debug.LogWarning("[Emberdelve][AttributeCurve] Invalid movement profile '" + profile.name +
                "': " + error + " Using D4a defaults.", profile);
            return MovementCurveDefaults.Curve;
        }
    }
}
