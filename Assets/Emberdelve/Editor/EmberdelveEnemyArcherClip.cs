using UnityEditor;
using UnityEngine;
using Emberdelve.AI;

namespace Emberdelve.EditorTools
{
    /// <summary>
    /// The skeleton archer's shot, a normal Humanoid clip generated from the imported CC0 Quaternius Idle (no new third-party asset). The whole
    /// body keeps the Idle pose (it faces forward); only the arms are solved in muscle space (damped least squares through HumanPoseHandler, the
    /// same technique as the Servitor overhead). The left arm raises the bow forward at shoulder height; the right hand is solved onto the bow's
    /// nock in every frame (the procedural <see cref="Emberdelve.Player.BowPresentation"/> geometry at <see cref="EnemyBowRig.BowScale"/>), so the
    /// string hand and the string stay together through the draw. Timeline (normalized): raise and set the string until
    /// EnemyAttackPhase.ArcherDrawStart, draw to the cheek by ArcherDrawEnd, release (the hand flicks back) by ArcherReleaseEnd, hold, lower the bow
    /// by ArcherRecoveryEnd. The bow's grip pose on the left hand is derived from the full-draw frame so the bow points straight forward there.
    /// </summary>
    public static class EmberdelveEnemyArcherClip
    {
        public const float Length = 1.6f;
        private const float FrameRate = 60f;

        // Shoulder-relative wrist of the bow arm at full draw: forward along the aim, a little in toward the body line and up.
        private const float BowArmReach = 0.93f;    // fraction of the arm length
        private const float BowArmInward = 0.10f;
        private const float BowArmUp = 0.05f;
        private const float HoldEnd = EnemyAttackPhase.ArcherHoldEnd;

        /// <summary>Grip pose of the bow on the left hand bone (local to the bone) found by the last build, and its carry rotation (upright at the side in Idle).</summary>
        public static Vector3 LastGripLocalPosition;
        public static Quaternion LastGripLocalRotation = Quaternion.identity;
        public static Quaternion LastCarryLocalRotation = Quaternion.identity;
        public static float LastWorstResidual;
        public static string LastDiagnostics = string.Empty;

        private static float Smooth01(float x) { x = Mathf.Clamp01(x); return x * x * (3f - 2f * x); }

        // BowPresentation's nock in bow space (see BowPresentation.Nock): rest .024/.015/-.16, full draw further back and out.
        private static Vector3 NockLocal(float draw) => new Vector3(.024f + .15f * draw, .015f, -.16f - .22f * draw);

        public static AnimationClip CreateAsset(EmberdelveEnemyVisualFactory.Variant variant)
        {
            AnimationClip clip = Build(variant);
            if (clip == null) return null;
            string path = EmberdelveEnemyVisualFactory.AttackClipPath(variant.Name);
            if (AssetDatabase.LoadAssetAtPath<AnimationClip>(path) != null) AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(clip, path);
            return clip;
        }

        /// <summary>Builds the clip in memory (deterministic) and records the bow grip pose. The caller owns the clip.</summary>
        public static AnimationClip Build(EmberdelveEnemyVisualFactory.Variant variant)
        {
            if (variant == null) return null;
            if (!EmberdelveQuaterniusImport.EnsureConfigured() || !EmberdelveSkeletonAssets.EnsureConfigured()) return null;

            GameObject fbx = AssetDatabase.LoadAssetAtPath<GameObject>(EmberdelveSkeletonAssets.ModelPath);
            SkinnedMeshRenderer source = fbx != null ? fbx.GetComponentInChildren<SkinnedMeshRenderer>(true) : null;
            Material bodyMaterial = EmberdelveSkeletonAssets.MaterialFor(variant.Name);
            AnimationClip idle = EmberdelveEnemyVisualFactory.FindClip(variant.Idle);
            Avatar avatar = EmberdelveEnemyVisualFactory.FindAvatar(EmberdelveSkeletonAssets.ModelPath);
            if (source == null || source.sharedMesh == null || bodyMaterial == null || idle == null || avatar == null || !avatar.isHuman) return null;

            GameObject root = new GameObject("ArcherBake_" + variant.Name);
            try
            {
                GameObject model = EmberdelveEnemyVisualFactory.CreateBakeModel(root.transform, fbx, source.sharedMesh, bodyMaterial, avatar);
                Animator animator = model.GetComponent<Animator>();
                model.transform.localRotation = Quaternion.identity;

                Transform leftUpper = animator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
                Transform leftLower = animator.GetBoneTransform(HumanBodyBones.LeftLowerArm);
                Transform leftHand = animator.GetBoneTransform(HumanBodyBones.LeftHand);
                Transform rightUpper = animator.GetBoneTransform(HumanBodyBones.RightUpperArm);
                Transform rightHand = animator.GetBoneTransform(HumanBodyBones.RightHand);
                if (leftUpper == null || leftLower == null || leftHand == null || rightUpper == null || rightHand == null) return null;

                HumanPoseHandler handler = new HumanPoseHandler(avatar, model.transform);
                HumanPose idlePose = new HumanPose();
                EmberdelveFirstPersonArms.Evaluate(animator, idle, 0.3f * idle.length);
                handler.GetHumanPose(ref idlePose);

                // Carried in Idle: limbs upright along the leg, the back of the bow facing forward.
                LastCarryLocalRotation = Quaternion.Inverse(leftHand.rotation) * Quaternion.LookRotation(Vector3.forward, Vector3.up);

                Vector3 leftShoulder = leftUpper.position;
                Vector3 leftIdle = leftHand.position;
                Vector3 rightIdle = rightHand.position;
                float armLength = (leftLower.position - leftUpper.position).magnitude + (leftHand.position - leftLower.position).magnitude;
                float inward = leftShoulder.x < 0f ? 1f : -1f;   // toward the body line
                Vector3 leftFull = leftShoulder + new Vector3(inward * BowArmInward, BowArmUp, BowArmReach * armLength);

                ArmSolver left = new ArmSolver(handler, "Left", leftHand);
                ArmSolver right = new ArmSolver(handler, "Right", rightHand);
                float[] current = (float[])idlePose.muscles.Clone();
                HumanPose pose = new HumanPose { muscles = new float[idlePose.muscles.Length] };

                // Pre-pass: the bow arm at full draw defines the grip pose (bow pointing straight forward, limbs up).
                for (int k = 0; k < 12; k++)
                {
                    float s = (k + 1) / 12f;
                    left.Solve(ref pose, idlePose, current, Vector3.Lerp(leftIdle, leftFull, s));
                }
                Quaternion bowWorld = Quaternion.LookRotation(Vector3.forward, Vector3.up);
                Vector3 gripWorld = leftHand.position;
                LastGripLocalPosition = leftHand.InverseTransformPoint(gripWorld);
                LastGripLocalRotation = Quaternion.Inverse(leftHand.rotation) * bowWorld;
                // The nock as the runtime rig places the bow (EnemyBowRig: carry..grip blended by the clip's raise).
                Vector3 Nock(float draw, float raise) => leftHand.TransformPoint(LastGripLocalPosition)
                    + leftHand.rotation * EnemyBowRig.BlendRotation(LastCarryLocalRotation, LastGripLocalRotation, raise) * (NockLocal(draw) * EnemyBowRig.BowScale);

                current = (float[])idlePose.muscles.Clone();
                left.Reset(); right.Reset();

                int frames = Mathf.RoundToInt(Length * FrameRate) + 1;
                int muscleCount = HumanTrait.MuscleCount;
                float[][] muscles = new float[muscleCount][];
                for (int m = 0; m < muscleCount; m++) muscles[m] = new float[frames];
                Vector3[] bodyPos = new Vector3[frames];
                Quaternion[] bodyRot = new Quaternion[frames];

                Vector3 releaseFlick = new Vector3(-inward * 0.05f, 0.02f, -0.07f);
                Vector3 rightAtRelease = Vector3.zero;
                for (int f = 0; f < frames; f++)
                {
                    float n = f / (float)(frames - 1);
                    float raise = EnemyAttackPhase.ArcherBowRaise(n);
                    left.Solve(ref pose, idlePose, current, Vector3.Lerp(leftIdle, leftFull, raise));

                    Vector3 rightTarget;
                    if (n <= EnemyAttackPhase.ArcherDrawStart)
                        rightTarget = Vector3.Lerp(rightIdle, Nock(0f, raise), Smooth01(n / EnemyAttackPhase.ArcherDrawStart));
                    else if (n <= EnemyAttackPhase.ArcherDrawEnd)
                        rightTarget = Nock(EnemyAttackPhase.ArcherBowDraw(n), raise);
                    else if (n <= EnemyAttackPhase.ArcherReleaseEnd)
                    {
                        if (rightAtRelease == Vector3.zero) rightAtRelease = Nock(1f, 1f);
                        rightTarget = rightAtRelease + releaseFlick * Smooth01((n - EnemyAttackPhase.ArcherDrawEnd) / (EnemyAttackPhase.ArcherReleaseEnd - EnemyAttackPhase.ArcherDrawEnd));
                    }
                    else if (n <= HoldEnd) rightTarget = rightAtRelease + releaseFlick;
                    else rightTarget = Vector3.Lerp(rightAtRelease + releaseFlick, rightIdle, Smooth01((n - HoldEnd) / (EnemyAttackPhase.ArcherRecoveryEnd - HoldEnd)));
                    right.Solve(ref pose, idlePose, current, rightTarget);

                    for (int m = 0; m < muscleCount; m++) muscles[m][f] = current[m];
                    bodyPos[f] = idlePose.bodyPosition;
                    bodyRot[f] = idlePose.bodyRotation;
                }
                LastWorstResidual = Mathf.Max(left.WorstResidual, right.WorstResidual);

                System.Text.StringBuilder diagnostics = new System.Text.StringBuilder();
                for (int m = 0; m < muscleCount; m++)
                {
                    float worst = 0f;
                    for (int f = 0; f < frames; f++) if (Mathf.Abs(muscles[m][f]) > Mathf.Abs(worst)) worst = muscles[m][f];
                    if (Mathf.Abs(worst) > 1f) diagnostics.Append(HumanTrait.MuscleName[m]).Append(' ').Append(worst.ToString("F2")).Append("; ");
                }
                LastDiagnostics = diagnostics.ToString();

                AnimationClip clip = new AnimationClip { name = "Attack", frameRate = FrameRate };
                for (int m = 0; m < muscleCount; m++)
                {
                    int index = m;
                    clip.SetCurve(string.Empty, typeof(Animator), EmberdelveEnemyOverheadClip.MuscleCurveName(m), Curve(frames, f => muscles[index][f]));
                }
                clip.SetCurve(string.Empty, typeof(Animator), "RootT.x", Curve(frames, f => bodyPos[f].x));
                clip.SetCurve(string.Empty, typeof(Animator), "RootT.y", Curve(frames, f => bodyPos[f].y));
                clip.SetCurve(string.Empty, typeof(Animator), "RootT.z", Curve(frames, f => bodyPos[f].z));
                clip.SetCurve(string.Empty, typeof(Animator), "RootQ.x", Curve(frames, f => bodyRot[f].x));
                clip.SetCurve(string.Empty, typeof(Animator), "RootQ.y", Curve(frames, f => bodyRot[f].y));
                clip.SetCurve(string.Empty, typeof(Animator), "RootQ.z", Curve(frames, f => bodyRot[f].z));
                clip.SetCurve(string.Empty, typeof(Animator), "RootQ.w", Curve(frames, f => bodyRot[f].w));
                AnimationClipSettings settings = AnimationUtility.GetAnimationClipSettings(clip);
                settings.loopTime = false;
                settings.loopBlendOrientation = true;
                settings.loopBlendPositionY = true;
                settings.loopBlendPositionXZ = false;
                AnimationUtility.SetAnimationClipSettings(clip, settings);
                return clip;
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        private static AnimationCurve Curve(int frames, System.Func<int, float> value)
        {
            Keyframe[] keys = new Keyframe[frames];
            for (int f = 0; f < frames; f++) keys[f] = new Keyframe(f / FrameRate, value(f));
            AnimationCurve curve = new AnimationCurve(keys);
            for (int f = 0; f < frames; f++) curve.SmoothTangents(f, 0f);
            return curve;
        }

        /// <summary>
        /// One arm solved in muscle space so its wrist reaches a world point: Levenberg-Marquardt over the shoulder, arm, forearm and hand muscles,
        /// a finite-difference Jacobian through HumanPoseHandler, starting from the previous frame (continuous in time, no twist wrap). The rest of
        /// the body is the given base pose; the other arm keeps whatever <paramref name="current"/> holds.
        /// </summary>
        private sealed class ArmSolver
        {
            private readonly HumanPoseHandler _handler;
            private readonly Transform _hand;
            private readonly int[] _muscles;
            public float WorstResidual { get; private set; }

            public ArmSolver(HumanPoseHandler handler, string side, Transform hand)
            {
                _handler = handler;
                _hand = hand;
                var list = new System.Collections.Generic.List<int>();
                string[] prefixes = { side + " Shoulder", side + " Arm", side + " Forearm", side + " Hand " };
                for (int i = 0; i < HumanTrait.MuscleCount; i++)
                    foreach (string prefix in prefixes)
                        if (HumanTrait.MuscleName[i].StartsWith(prefix, System.StringComparison.Ordinal)) { list.Add(i); break; }
                _muscles = list.ToArray();
            }

            public void Reset() => WorstResidual = 0f;

            private void Apply(ref HumanPose pose, in HumanPose basePose, float[] current, float[] x, Vector3 target, float[] r)
            {
                System.Array.Copy(current, pose.muscles, current.Length);
                for (int i = 0; i < _muscles.Length; i++) pose.muscles[_muscles[i]] = x[i];
                pose.bodyPosition = basePose.bodyPosition;
                pose.bodyRotation = basePose.bodyRotation;
                _handler.SetHumanPose(ref pose);
                Vector3 d = _hand.position - target;
                r[0] = d.x; r[1] = d.y; r[2] = d.z;
                // Mild pull toward the base pose keeps the redundant degrees (twist, hand) natural.
                for (int i = 0; i < _muscles.Length; i++) r[3 + i] = (x[i] - basePose.muscles[_muscles[i]]) * 0.02f;
            }

            private static float Sum(float[] r) { float s = 0f; for (int i = 0; i < r.Length; i++) s += r[i] * r[i]; return s; }

            public void Solve(ref HumanPose pose, in HumanPose basePose, float[] current, Vector3 target)
            {
                int n = _muscles.Length, size = 3 + n;
                float[] x = new float[n], trial = new float[n], r = new float[size], rTrial = new float[size], rJ = new float[size];
                float[,] jac = new float[size, n];
                for (int i = 0; i < n; i++) x[i] = current[_muscles[i]];
                Apply(ref pose, basePose, current, x, target, r);
                float cost = Sum(r);
                float lambda = 1e-2f;
                for (int iteration = 0; iteration < 30 && cost > 1e-7f; iteration++)
                {
                    for (int j = 0; j < n; j++)
                    {
                        float old = x[j];
                        x[j] = old + 1e-3f;
                        Apply(ref pose, basePose, current, x, target, rJ);
                        x[j] = old;
                        for (int k = 0; k < size; k++) jac[k, j] = (rJ[k] - r[k]) / 1e-3f;
                    }
                    bool improved = false;
                    for (int attempt = 0; attempt < 6 && !improved; attempt++)
                    {
                        float[] step = SolveNormalEquations(jac, r, n, lambda);
                        for (int j = 0; j < n; j++) trial[j] = Mathf.Clamp(x[j] + Mathf.Clamp(step[j], -0.5f, 0.5f), -1.6f, 1.6f);
                        Apply(ref pose, basePose, current, trial, target, rTrial);
                        float trialCost = Sum(rTrial);
                        if (trialCost < cost)
                        {
                            System.Array.Copy(trial, x, n);
                            System.Array.Copy(rTrial, r, size);
                            cost = trialCost;
                            lambda = Mathf.Max(1e-5f, lambda * 0.4f);
                            improved = true;
                        }
                        else lambda *= 4f;
                    }
                    if (!improved) break;
                }
                Apply(ref pose, basePose, current, x, target, r);
                WorstResidual = Mathf.Max(WorstResidual, new Vector3(r[0], r[1], r[2]).magnitude);
                for (int i = 0; i < n; i++) current[_muscles[i]] = x[i];
            }

            private static float[] SolveNormalEquations(float[,] jac, float[] r, int n, float lambda)
            {
                float[,] a = new float[n, n + 1];
                int rows = jac.GetLength(0);
                for (int i = 0; i < n; i++)
                {
                    for (int j = 0; j < n; j++)
                    {
                        float s = 0f;
                        for (int k = 0; k < rows; k++) s += jac[k, i] * jac[k, j];
                        a[i, j] = s + (i == j ? lambda : 0f);
                    }
                    float g = 0f;
                    for (int k = 0; k < rows; k++) g += jac[k, i] * r[k];
                    a[i, n] = -g;
                }
                for (int col = 0; col < n; col++)
                {
                    int pivot = col;
                    for (int row = col + 1; row < n; row++) if (Mathf.Abs(a[row, col]) > Mathf.Abs(a[pivot, col])) pivot = row;
                    if (pivot != col) for (int c = 0; c <= n; c++) { float t = a[col, c]; a[col, c] = a[pivot, c]; a[pivot, c] = t; }
                    float d = a[col, col];
                    if (Mathf.Abs(d) < 1e-9f) continue;
                    for (int row = 0; row < n; row++)
                    {
                        if (row == col) continue;
                        float factor = a[row, col] / d;
                        for (int c = col; c <= n; c++) a[row, c] -= factor * a[col, c];
                    }
                }
                float[] result = new float[n];
                for (int i = 0; i < n; i++) result[i] = Mathf.Abs(a[i, i]) < 1e-9f ? 0f : a[i, n] / a[i, i];
                return result;
            }
        }
    }
}
