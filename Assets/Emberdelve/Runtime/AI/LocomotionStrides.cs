using UnityEngine;

namespace Emberdelve.AI
{
    /// <summary>
    /// Presentation data on a built character model: the ground speed (m/s, in the model root's own scale) its Walk and Run loops cover at
    /// playback 1, measured from its feet when the model was built (EmberdelveLocomotionControllers.MeasureStrides). The animation drivers scale
    /// by the model's world scale and match the loop's rate to the measured ground speed (LocomotionGait).
    /// </summary>
    public sealed class LocomotionStrides : MonoBehaviour
    {
        public float walk = LocomotionGait.WalkStride;
        public float run = LocomotionGait.JogStride;

        /// <summary>Stride (m/s) of the walk or run loop at this model's current world scale.</summary>
        public float StrideFor(bool running) => (running ? run : walk) * Mathf.Max(0.1f, transform.lossyScale.y);

        /// <summary>The model's strides, or the skeleton fallback scaled by <paramref name="scaleSource"/>'s world scale.</summary>
        public static float StrideOf(Component scaleSource, LocomotionStrides strides, bool running) =>
            strides != null ? strides.StrideFor(running)
                : (running ? LocomotionGait.JogStride : LocomotionGait.WalkStride) * (scaleSource != null ? Mathf.Max(0.1f, scaleSource.transform.lossyScale.y) : 1f);
    }
}
