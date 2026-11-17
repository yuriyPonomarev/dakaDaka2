using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using Emberdelve.Player;

namespace Emberdelve.EditorTools
{
    /// <summary>Bakes the finger muscles of a free authored grip pose (Kevin Iglesias, Human@ObjectGripHands01) into Resources.</summary>
    public static class EmberdelveFpGripBake
    {
        const string ClipPath = "Assets/Kevin Iglesias/Human Animations/Animations/Masked Poses/Human@ObjectGripHands01.fbx";
        const string AssetPath = "Assets/Emberdelve/Resources/Emberdelve/FpGripPose.asset";

        [MenuItem("Emberdelve/Visual/Bake FP Grip Pose")]
        public static void Bake()
        {
            AnimationClip clip = null;
            foreach (Object asset in AssetDatabase.LoadAllAssetsAtPath(ClipPath))
                if (asset is AnimationClip c && !c.name.StartsWith("__preview__")) { clip = c; break; }
            if (clip == null) { Debug.LogError("[FpGrip] clip missing: " + ClipPath); return; }

            var curves = new Dictionary<string, EditorCurveBinding>();
            foreach (EditorCurveBinding binding in AnimationUtility.GetCurveBindings(clip)) curves[binding.propertyName] = binding;

            var muscles = new List<int>();
            var values = new List<float>();
            string[] names = HumanTrait.MuscleName;
            for (int m = 0; m < names.Length; m++)
            {
                string name = names[m];
                bool left = name.StartsWith("Left "), right = name.StartsWith("Right ");
                if (!left && !right) continue;
                string rest = name.Substring(left ? 5 : 6);
                string finger = null;
                foreach (string f in new[] { "Thumb", "Index", "Middle", "Ring", "Little" })
                    if (rest.StartsWith(f + " ")) { finger = f; break; }
                if (finger == null) continue;
                string tail = rest.Substring(finger.Length + 1);
                string property = (left ? "LeftHand." : "RightHand.") + finger + "." + tail;
                if (!curves.TryGetValue(property, out EditorCurveBinding found)) continue;
                AnimationCurve curve = AnimationUtility.GetEditorCurve(clip, found);
                if (curve == null) continue;
                muscles.Add(m);
                values.Add(curve.Evaluate(0f));
            }
            if (muscles.Count == 0)
            {
                var sample = new List<string>();
                foreach (var key in curves.Keys) { sample.Add(key); if (sample.Count > 12) break; }
                Debug.LogError("[FpGrip] no finger curves matched; clip properties: " + string.Join(", ", sample));
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(AssetPath));
            var pose = AssetDatabase.LoadAssetAtPath<FpGripPose>(AssetPath);
            if (pose == null) { pose = ScriptableObject.CreateInstance<FpGripPose>(); AssetDatabase.CreateAsset(pose, AssetPath); }
            pose.muscles = muscles.ToArray();
            pose.values = values.ToArray();
            EditorUtility.SetDirty(pose);
            AssetDatabase.SaveAssets();
            Debug.Log("[FpGrip] baked " + muscles.Count + " finger muscles from " + clip.name);
        }
    }
}
