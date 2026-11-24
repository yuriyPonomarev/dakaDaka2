using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Emberdelve.AI;
using Emberdelve.Data;

namespace Emberdelve.EditorTools
{
    /// <summary>
    /// Gives an animator controller speed-matched locomotion (<see cref="LocomotionGait"/>): a float <see cref="LocomotionGait.ScaleParameter"/>
    /// (default 1) that the Walk / Run / Sprint states multiply their playback by. Idempotent. Applied by the enemy visual factory and the class
    /// avatar builders, and in place to the existing controllers by the menu below (no prefab rebuild).
    /// </summary>
    public static class EmberdelveLocomotionControllers
    {
        private static readonly string[] LocomotionStates = { "Walk", "Run", "Sprint" };

        /// <summary>True when the controller changed.</summary>
        public static bool Ensure(AnimatorController controller)
        {
            if (controller == null) return false;
            bool changed = false;
            bool has = false;
            foreach (AnimatorControllerParameter p in controller.parameters) if (p.name == LocomotionGait.ScaleParameter) { has = true; break; }
            if (!has)
            {
                controller.AddParameter(new AnimatorControllerParameter
                { name = LocomotionGait.ScaleParameter, type = AnimatorControllerParameterType.Float, defaultFloat = 1f });
                changed = true;
            }

            if (controller.layers.Length == 0) return changed;
            foreach (ChildAnimatorState child in controller.layers[0].stateMachine.states)
            {
                AnimatorState state = child.state;
                if (System.Array.IndexOf(LocomotionStates, state.name) < 0) continue;
                if (!state.speedParameterActive || state.speedParameter != LocomotionGait.ScaleParameter || !Mathf.Approximately(state.speed, 1f))
                {
                    state.speedParameterActive = true;
                    state.speedParameter = LocomotionGait.ScaleParameter;
                    state.speed = 1f;   // the rate comes from the measured ground speed, never a fixed multiplier
                    changed = true;
                }
            }
            if (changed) EditorUtility.SetDirty(controller);
            return changed;
        }

        /// <summary>Sets a locomotion state's loop (in place).</summary>
        public static bool SetMotion(AnimatorController controller, string stateName, AnimationClip clip)
        {
            if (controller == null || clip == null || controller.layers.Length == 0) return false;
            foreach (ChildAnimatorState child in controller.layers[0].stateMachine.states)
            {
                if (child.state.name != stateName || child.state.motion == clip) continue;
                child.state.motion = clip;
                EditorUtility.SetDirty(controller);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Ground speed (m/s, in <paramref name="root"/>'s own scale) a locomotion loop covers at playback 1 on this body: the speed at which its
        /// planted feet would stand still on the ground. Each foot counts as planted while it is within 4 cm of its lowest height; the stride is
        /// the backward speed that best cancels the planted feet's own fore-aft motion (least squares). Works for walks and runs alike (a run has
        /// no double support, so the feet-apart estimate under-reads it about 2x).
        /// </summary>
        public static bool TryMeasureStride(Animator animator, Transform root, AnimationClip clip, out float stride)
        {
            stride = 0f;
            if (animator == null || root == null || clip == null || !animator.isHuman || clip.length < 0.05f) return false;
            Transform left = animator.GetBoneTransform(HumanBodyBones.LeftFoot), right = animator.GetBoneTransform(HumanBodyBones.RightFoot);
            if (left == null || right == null) return false;
            const int samples = 200;
            var l = new Vector3[samples + 1];
            var r = new Vector3[samples + 1];
            for (int i = 0; i <= samples; i++)
            {
                EmberdelveFirstPersonArms.Evaluate(animator, clip, i * clip.length / samples);
                l[i] = root.InverseTransformPoint(left.position);
                r[i] = root.InverseTransformPoint(right.position);
            }
            float minL = float.MaxValue, minR = float.MaxValue;
            for (int i = 0; i <= samples; i++) { minL = Mathf.Min(minL, l[i].y); minR = Mathf.Min(minR, r[i].y); }
            // The least-squares stride is the mean backward speed of the planted samples.
            float dt = clip.length / samples, sum = 0f;
            int count = 0;
            for (int i = 1; i <= samples; i++)
            {
                if (l[i].y < minL + 0.04f && l[i - 1].y < minL + 0.04f) { sum += -(l[i].z - l[i - 1].z) / dt; count++; }
                if (r[i].y < minR + 0.04f && r[i - 1].y < minR + 0.04f) { sum += -(r[i].z - r[i - 1].z) / dt; count++; }
            }
            if (count < 4) return false;
            stride = Mathf.Round(sum / count * 100f) / 100f;
            return stride > 0.1f;
        }

        /// <summary>The motion of a locomotion state of the controller (null when absent).</summary>
        public static AnimationClip StateClip(AnimatorController controller, string stateName)
        {
            if (controller == null || controller.layers.Length == 0) return null;
            foreach (ChildAnimatorState child in controller.layers[0].stateMachine.states)
                if (child.state.name == stateName) return child.state.motion as AnimationClip;
            return null;
        }

        /// <summary>
        /// Measures the model's Walk / Run strides with its own controller loops and stores them on a <see cref="LocomotionStrides"/> at the
        /// model root (added when missing). The pose is reset to the default afterwards by the caller's save (prefab contents are reloaded).
        /// </summary>
        public static bool MeasureInto(GameObject modelRoot, out string summary)
        {
            summary = string.Empty;
            Animator animator = modelRoot != null ? modelRoot.GetComponentInChildren<Animator>(true) : null;
            var controller = animator != null ? animator.runtimeAnimatorController as AnimatorController : null;
            if (controller == null) { summary = "no humanoid controller"; return false; }
            // Sampling poses the bones: keep the authored pose so a saved prefab is not left mid-stride.
            Transform[] all = modelRoot.GetComponentsInChildren<Transform>(true);
            var pose = new (Vector3 p, Quaternion r, Vector3 s)[all.Length];
            for (int i = 0; i < all.Length; i++) pose[i] = (all[i].localPosition, all[i].localRotation, all[i].localScale);
            bool walkOk, runOk;
            float walk, run;
            try
            {
                walkOk = TryMeasureStride(animator, modelRoot.transform, StateClip(controller, "Walk"), out walk);
                runOk = TryMeasureStride(animator, modelRoot.transform, StateClip(controller, "Run"), out run);
            }
            finally
            {
                for (int i = 0; i < all.Length; i++) { all[i].localPosition = pose[i].p; all[i].localRotation = pose[i].r; all[i].localScale = pose[i].s; }
            }
            if (!walkOk && !runOk) { summary = "no measurable loops"; return false; }
            LocomotionStrides strides = modelRoot.GetComponent<LocomotionStrides>();
            if (strides == null) strides = modelRoot.AddComponent<LocomotionStrides>();
            if (walkOk) strides.walk = walk;
            if (runOk) strides.run = run;
            summary = $"walk {strides.walk:F2} m/s, run {strides.run:F2} m/s";
            return true;
        }

        private static string MeasurePrefab(string path)
        {
            if (string.IsNullOrEmpty(path) || AssetDatabase.LoadAssetAtPath<GameObject>(path) == null) return "missing";
            GameObject contents = PrefabUtility.LoadPrefabContents(path);
            try
            {
                if (!MeasureInto(contents, out string summary)) return summary;
                PrefabUtility.SaveAsPrefabAsset(contents, path);
                return summary;
            }
            finally { PrefabUtility.UnloadPrefabContents(contents); }
        }

        [MenuItem("Emberdelve/Visual/Apply Speed-Matched Locomotion")]
        public static void MenuApply() => Debug.Log("[Emberdelve][Locomotion] " + ApplyToExisting());

        /// <summary>Patches the enemy variant controllers (their Walk/Run loops from the variant table) and the class avatar controllers.</summary>
        public static string ApplyToExisting()
        {
            var report = new System.Text.StringBuilder();
            foreach (EmberdelveEnemyVisualFactory.Variant variant in EmberdelveEnemyVisualFactory.Variants)
            {
                var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(EmberdelveEnemyVisualFactory.ControllerPath(variant.Name));
                if (controller == null) { report.Append(variant.Name).Append(": no controller; "); continue; }
                bool changed = SetMotion(controller, "Walk", EmberdelveEnemyVisualFactory.FindClip(variant.Walk));
                changed |= SetMotion(controller, "Run", EmberdelveEnemyVisualFactory.FindClip(variant.Run));
                changed |= Ensure(controller);
                AssetDatabase.SaveAssets();
                report.Append(variant.Name).Append(changed ? " patched" : " up to date")
                    .Append(" [").Append(MeasurePrefab(EmberdelveEnemyVisualFactory.PrefabPath(variant.Name)))
                    .Append(" | net ").Append(MeasurePrefab(EmberdelveEnemyVisualFactory.NetResourcePath(variant.Name))).Append("]; ");
            }
            foreach (string guid in AssetDatabase.FindAssets("t:CharacterClassData", new[] { EmberdelvePaths.Data }))
            {
                var data = AssetDatabase.LoadAssetAtPath<CharacterClassData>(AssetDatabase.GUIDToAssetPath(guid));
                Animator animator = data != null && data.characterVisual != null ? data.characterVisual.GetComponentInChildren<Animator>(true) : null;
                var controller = animator != null ? animator.runtimeAnimatorController as AnimatorController : null;
                if (controller == null) continue;
                bool changed = Ensure(controller);
                AssetDatabase.SaveAssets();
                report.Append(data.name).Append(changed ? " patched" : " up to date")
                    .Append(" [").Append(MeasurePrefab(AssetDatabase.GetAssetPath(data.characterVisual))).Append("]; ");
            }
            AssetDatabase.SaveAssets();
            return report.ToString();
        }
    }
}
