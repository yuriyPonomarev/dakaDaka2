using UnityEngine;

namespace Emberdelve.Player
{
    /// <summary>
    /// Authored closed-hand grip (finger muscles) baked from a humanoid pose clip by the editor
    /// (Emberdelve/Visual/Bake FP Grip Pose). The first-person arms apply it to the fingers holding a handle.
    /// </summary>
    public sealed class FpGripPose : ScriptableObject
    {
        public const string ResourcePath = "Emberdelve/FpGripPose";

        [Tooltip("Humanoid muscle indices (HumanTrait.MuscleName order).")]
        public int[] muscles = new int[0];
        public float[] values = new float[0];

        private static FpGripPose _cached;
        private static bool _loaded;

        public static FpGripPose Load()
        {
            if (!_loaded) { _cached = Resources.Load<FpGripPose>(ResourcePath); _loaded = true; }
            return _cached;
        }

        public bool TryGet(int muscle, out float value)
        {
            for (int i = 0; i < muscles.Length && i < values.Length; i++)
                if (muscles[i] == muscle) { value = values[i]; return true; }
            value = 0f;
            return false;
        }
    }
}
