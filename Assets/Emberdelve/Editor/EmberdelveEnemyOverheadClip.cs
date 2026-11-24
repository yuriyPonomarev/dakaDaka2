using UnityEditor;
using UnityEngine;
using Emberdelve.AI;

namespace Emberdelve.EditorTools
{
    /// <summary>
    /// The derived vertical-overhead chop of the Servitor (second melee attack, pattern index 1), a normal Humanoid clip generated from the
    /// imported CC0 Quaternius Idle (no new third-party asset). The whole body is the Idle pose in every frame (it keeps facing forward); only the
    /// right arm is solved (muscle-space IK) and the spine pitches forward in the strike. Windup: the guard rises directly to a raised pose (blade
    /// over the head). Strike (EnemyAttackPhase.OverheadSweepStart..End): the hand and blade come down in the sagittal plane to low in front.
    /// Recovery: straight back to the Idle guard. Every frame is written as muscle + root curves, so the driver, the client proxy and TryBakeSwing
    /// play it like any library clip.
    /// </summary>
    public static class EmberdelveEnemyOverheadClip
    {
        public const float Length = 1.2f;

        /// <summary>Muscles whose solved value left the avatar range (-1..1) in the last build: "name max".</summary>
        public static string LastDiagnostics = string.Empty;
        public static float[][] LastMuscleFrames;
        public static float LastWorstResidual;
        private const float FrameRate = 120f;
        private static readonly Vector3 RaisedWrist = new Vector3(0.06f, 0.30f, 0.12f);   // shoulder-relative wrist at the top of the windup
        private const float RaisedPitch = -70f;  // blade pitch (degrees, positive down) at the top of the windup
        private const float StrikeLean = 0.90f;  // spine forward-pitch muscle added at the end of the strike
        private const float RecoveryBladeLag = 0.5f; // 0 = the blade turns back linearly with the hand, 1 = late (keeps the tip low)
        private const float EndPitch = 60f;      // blade pitch (degrees, positive down) at the end of the chop
        private const float EndArmAngle = 30f;   // straight arm, degrees below horizontal-forward
        private const float RecoveryStart = 0.62f;
        private const float RecoveryEnd = EnemyAttackPhase.OverheadRecoveryEnd;

        /// <summary>Muscle curve property name of HumanTrait muscle <paramref name="i"/> (finger muscles are "LeftHand.Thumb.1 Stretched").</summary>
        public static string MuscleCurveName(int i)
        {
            string name = HumanTrait.MuscleName[i];
            string[] fingers = { "Thumb", "Index", "Middle", "Ring", "Little" };
            foreach (string side in new[] { "Left", "Right" })
            {
                foreach (string finger in fingers)
                {
                    string prefix = side + " " + finger + " ";
                    if (name.StartsWith(prefix, System.StringComparison.Ordinal))
                        return side + "Hand." + finger + "." + name.Substring(prefix.Length);
                }
            }
            return name;
        }

        private static int Muscle(string name) => System.Array.IndexOf(HumanTrait.MuscleName, name);

        private static float Smooth01(float x) { x = Mathf.Clamp01(x); return x * x * (3f - 2f * x); }

        private static void CopyPose(in HumanPose from, ref HumanPose to)
        {
            if (to.muscles == null || to.muscles.Length != from.muscles.Length) to.muscles = new float[from.muscles.Length];
            System.Array.Copy(from.muscles, to.muscles, from.muscles.Length);
            to.bodyPosition = from.bodyPosition;
            to.bodyRotation = from.bodyRotation;
        }

        private static void LerpPose(in HumanPose a, in HumanPose b, float k, ref HumanPose result)
        {
            if (result.muscles == null || result.muscles.Length != a.muscles.Length) result.muscles = new float[a.muscles.Length];
            for (int i = 0; i < a.muscles.Length; i++) result.muscles[i] = Mathf.Lerp(a.muscles[i], b.muscles[i], k);
            result.bodyPosition = Vector3.Lerp(a.bodyPosition, b.bodyPosition, k);
            result.bodyRotation = Quaternion.Slerp(a.bodyRotation, b.bodyRotation, k);
        }

        private static AnimationCurve Curve(int frames, System.Func<int, float> value)
        {
            Keyframe[] keys = new Keyframe[frames];
            for (int f = 0; f < frames; f++) keys[f] = new Keyframe(f / FrameRate, value(f));
            AnimationCurve curve = new AnimationCurve(keys);
            for (int f = 0; f < frames; f++) curve.SmoothTangents(f, 0f);
            return curve;
        }

        /// <summary>The clip as a project asset (named "Overhead": the driver finds its length by that name). Null when the art is missing.</summary>
        public static AnimationClip CreateAsset(EmberdelveEnemyVisualFactory.Variant variant)
        {
            AnimationClip clip = Build(variant);
            if (clip == null) return null;
            string path = EmberdelveEnemyVisualFactory.OverheadClipPath(variant.Name);
            if (AssetDatabase.LoadAssetAtPath<AnimationClip>(path) != null) AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(clip, path);
            return clip;
        }

        /// <summary>Builds the clip in memory (deterministic: the asset, the sweep bake and the gate all get the same motion). The caller destroys it.</summary>
        public static AnimationClip Build(EmberdelveEnemyVisualFactory.Variant variant)
        {
            if (variant == null || string.IsNullOrEmpty(variant.MainHand)) return null;
            if (!EmberdelveQuaterniusImport.EnsureConfigured() || !EmberdelveSkeletonAssets.EnsureConfigured()) return null;
            EmberdelveWeaponModels.EnsureAll(false);
            EmberdelveUndeadMaterials.EnsureAll();

            GameObject fbx = AssetDatabase.LoadAssetAtPath<GameObject>(EmberdelveSkeletonAssets.ModelPath);
            SkinnedMeshRenderer source = fbx != null ? fbx.GetComponentInChildren<SkinnedMeshRenderer>(true) : null;
            Material bodyMaterial = EmberdelveSkeletonAssets.MaterialFor(variant.Name);
            AnimationClip idle = EmberdelveEnemyVisualFactory.FindClip(variant.Idle);
            Avatar avatar = EmberdelveEnemyVisualFactory.FindAvatar(EmberdelveSkeletonAssets.ModelPath);
            if (source == null || source.sharedMesh == null || bodyMaterial == null || idle == null || avatar == null || !avatar.isHuman)
                return null;

            GameObject root = new GameObject("OverheadBake_" + variant.Name);
            try
            {
                GameObject model = EmberdelveEnemyVisualFactory.CreateBakeModel(root.transform, fbx, source.sharedMesh, bodyMaterial, avatar);
                Animator animator = model.GetComponent<Animator>();
                EmberdelveEnemyVisualFactory.AttachMainHandForBake(variant, fbx, source.sharedMesh, bodyMaterial, avatar, animator);
                model.transform.localRotation = Quaternion.identity;

                Transform upper = animator.GetBoneTransform(HumanBodyBones.RightUpperArm);
                Transform lower = animator.GetBoneTransform(HumanBodyBones.RightLowerArm);
                Transform hand = animator.GetBoneTransform(HumanBodyBones.RightHand);
                Transform head = animator.GetBoneTransform(HumanBodyBones.Head);
                Transform weapon = hand != null ? hand.Find(variant.MainHand) : null;
                if (upper == null || lower == null || hand == null || weapon == null || head == null) return null;

                // The whole body is the Idle pose for the whole clip (hips, legs, spine twist, left arm): nothing of a library sword clip's stance or
                // body turn is used, so the skeleton keeps facing forward. Only the right arm (solved) and a forward spine pitch (strike) differ.
                HumanPoseHandler handler = new HumanPoseHandler(avatar, model.transform);
                HumanPose poseA = new HumanPose();
                EmberdelveFirstPersonArms.Evaluate(animator, idle, 0.3f * idle.length);
                handler.GetHumanPose(ref poseA);

                // The idle guard as shoulder-relative targets.
                Vector3 shoulder0 = upper.position;
                Vector3 offs0 = hand.position - shoulder0;
                Vector3 blade0 = weapon.TransformDirection(Vector3.up).normalized;
                Vector3 face0 = weapon.TransformDirection(Vector3.forward).normalized;
                float armLength = (lower.position - upper.position).magnitude + (hand.position - lower.position).magnitude;

                // The raised pose (end of the windup): wrist just above and in front of the shoulder, the blade standing up over the head
                // (tilted slightly forward), its flat facing sideways.
                Vector3 offsR = RaisedWrist;
                float betaR = RaisedPitch;
                Vector3 bladeR = new Vector3(0f, -Mathf.Sin(betaR * Mathf.Deg2Rad), Mathf.Cos(betaR * Mathf.Deg2Rad));
                Vector3 faceR = Vector3.right * (Vector3.Dot(face0, Vector3.right) >= 0f ? 1f : -1f);
                float alphaR = Mathf.Atan2(-offsR.y, offsR.z) * Mathf.Rad2Deg;
                float lengthR = new Vector2(offsR.y, offsR.z).magnitude;
                float lengthEnd = armLength * 0.97f;
                float xEnd = -shoulder0.x * 0.5f;   // hand drawn halfway to the body centre line

                // Which sign of the spine muscle leans the head forward.
                int spineFB = Muscle("Spine Front-Back"), chestFB = Muscle("Chest Front-Back");
                float leanSign = 1f;
                if (spineFB >= 0)
                {
                    HumanPose probe = new HumanPose();
                    CopyPose(poseA, ref probe);
                    handler.SetHumanPose(ref probe);
                    float z0 = head.position.z;
                    probe.muscles[spineFB] += 0.3f;
                    handler.SetHumanPose(ref probe);
                    leanSign = head.position.z > z0 ? 1f : -1f;
                }

                int frames = Mathf.RoundToInt(Length * FrameRate) + 1;
                int muscleCount = HumanTrait.MuscleCount;
                float[][] muscles = new float[muscleCount][];
                for (int m = 0; m < muscleCount; m++) muscles[m] = new float[frames];
                Vector3[] bodyPos = new Vector3[frames];
                Quaternion[] bodyRot = new Quaternion[frames];

                HumanPose frame = new HumanPose();
                frame.muscles = new float[muscleCount];
                float strikeStart = EnemyAttackPhase.OverheadSweepStart, strikeStop = EnemyAttackPhase.OverheadSweepEnd;

                StrikeRig rig = new StrikeRig
                {
                    Handler = handler, PoseA = poseA, Upper = upper, Hand = hand, Weapon = weapon, SpineFB = spineFB, ChestFB = chestFB,
                    LeanSign = leanSign, Previous = (float[])poseA.muscles.Clone(), Scratch = new HumanPose()
                };
                rig.Scratch.muscles = new float[muscleCount];
                rig.FindArmMuscles();

                // Strike targets at progress u (shoulder-relative wrist, blade, face, lean): the sagittal arc from the raised pose.
                void Strike(float u, out Vector3 offs, out Vector3 blade, out Vector3 face, out float lean)
                {
                    float ease = Mathf.Lerp(u * u, Smooth01(u), 0.5f);   // accelerates into the blow, settles at the end
                    float alpha = Mathf.Lerp(alphaR, EndArmAngle, ease) * Mathf.Deg2Rad;
                    float length = Mathf.Lerp(lengthR, lengthEnd, Smooth01(ease * 1.8f));   // the arm is nearly straight before the blade is level, so the tip reaches out
                    offs = new Vector3(Mathf.Lerp(offsR.x, xEnd, ease), -Mathf.Sin(alpha) * length, Mathf.Cos(alpha) * length);
                    float beta = Mathf.Lerp(betaR, EndPitch, ease) * Mathf.Deg2Rad;
                    blade = new Vector3(0f, -Mathf.Sin(beta), Mathf.Cos(beta));
                    face = faceR;
                    lean = ease * StrikeLean * leanSign;
                }

                Strike(1f, out Vector3 offsEnd, out Vector3 bladeEnd, out Vector3 faceEnd, out float leanEnd);

                for (int f = 0; f < frames; f++)
                {
                    float n = Mathf.Clamp01(f / (float)(frames - 1));
                    Vector3 offs, blade, face;
                    float lean;
                    if (n <= strikeStart)
                    {
                        // Windup: the guard rises directly along the right side to the raised pose (no cross-body travel).
                        float t = Mathf.Clamp01(n / EnemyAttackPhase.OverheadRaiseEnd);
                        float s = 1f - (1f - t) * (1f - t) * (1f - 0.5f * t);   // quick to leave the guard, settles at the top
                        float sIn = 1f - (1f - s) * (1f - s) * (1f - s);   // the hand and blade come in over the body line first, then climb
                        offs = Vector3.Lerp(offs0, offsR, s);
                        offs.x = Mathf.Lerp(offs0.x, offsR.x, sIn);
                        blade = Vector3.Slerp(blade0, bladeR, sIn);
                        face = Vector3.Lerp(face0, faceR, s);
                        lean = 0f;
                    }
                    else if (n <= strikeStop) Strike((n - strikeStart) / (strikeStop - strikeStart), out offs, out blade, out face, out lean);
                    else if (n <= RecoveryStart) { offs = offsEnd; blade = bladeEnd; face = faceEnd; lean = leanEnd; }
                    else
                    {
                        // Recovery: straight back to the idle guard.
                        float s = Smooth01((n - RecoveryStart) / (RecoveryEnd - RecoveryStart));
                        offs = Vector3.Lerp(offsEnd, offs0, s);
                        blade = Vector3.Slerp(bladeEnd, blade0, Mathf.Lerp(s, s * s, RecoveryBladeLag));
                        face = Vector3.Lerp(faceEnd, face0, s);
                        lean = Mathf.Lerp(leanEnd, 0f, s);
                    }

                    rig.Pose(ref frame, lean, offs, blade.normalized, face);
                    for (int m = 0; m < muscleCount; m++) muscles[m][f] = frame.muscles[m];
                    bodyPos[f] = frame.bodyPosition;
                    Quaternion q = frame.bodyRotation;
                    if (f > 0 && Quaternion.Dot(q, bodyRot[f - 1]) < 0f) q = new Quaternion(-q.x, -q.y, -q.z, -q.w);
                    bodyRot[f] = q;
                }
                LastWorstResidual = rig.LastResidual;

                System.Text.StringBuilder diagnostics = new System.Text.StringBuilder();
                for (int m = 0; m < muscleCount; m++)
                {
                    float worst = 0f;
                    for (int f = 0; f < frames; f++) if (Mathf.Abs(muscles[m][f]) > Mathf.Abs(worst)) worst = muscles[m][f];
                    if (Mathf.Abs(worst) > 1f) diagnostics.Append(HumanTrait.MuscleName[m]).Append(' ').Append(worst.ToString("F2")).Append("; ");
                }
                LastDiagnostics = diagnostics.ToString();
                LastMuscleFrames = muscles;

                AnimationClip clip = new AnimationClip { name = EnemyAttackPhase.OverheadState, frameRate = FrameRate };
                for (int m = 0; m < muscleCount; m++)
                {
                    int index = m;
                    clip.SetCurve(string.Empty, typeof(Animator), MuscleCurveName(m), Curve(frames, f => muscles[index][f]));
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

        /// <summary>
        /// The right-arm strike solver. One call poses the body from the raised pose (spine pitched forward by the strike progress), then solves the
        /// right arm in MUSCLE space: a damped least-squares fit (Levenberg-Marquardt, finite-difference Jacobian through HumanPoseHandler) of the
        /// shoulder, arm, forearm and hand muscles so that the wrist reaches the arc target and the held blade points along the wanted direction with
        /// the wanted face. It starts from the previous frame's muscles, so the solution is continuous in time and the swing-and-twist decomposition
        /// of the humanoid muscles never wraps between two frames (an analytic two-bone solve did, around the raised-arm singularity).
        /// </summary>
        private sealed class StrikeRig
        {
            public HumanPoseHandler Handler;
            public HumanPose PoseA;
            public HumanPose Scratch;
            public Transform Upper, Hand, Weapon;
            public int SpineFB, ChestFB;
            public float LeanSign;
            public float[] Previous;
            public float LastResidual;
            private int[] _arm;

            public void FindArmMuscles()
            {
                var list = new System.Collections.Generic.List<int>();
                string[] prefixes = { "Right Shoulder", "Right Arm", "Right Forearm", "Right Hand " };
                for (int i = 0; i < HumanTrait.MuscleCount; i++)
                    foreach (string prefix in prefixes)
                        if (HumanTrait.MuscleName[i].StartsWith(prefix, System.StringComparison.Ordinal)) { list.Add(i); break; }
                _arm = list.ToArray();
            }

            private const int ResidualSize = 9;

            private void Residual(ref HumanPose pose, float[] x, Vector3 wristTarget, Vector3 bladeTarget, Vector3 faceTarget, float[] r)
            {
                for (int i = 0; i < _arm.Length; i++) pose.muscles[_arm[i]] = x[i];
                Handler.SetHumanPose(ref pose);
                Vector3 p = Hand.position - wristTarget;
                Vector3 b = (Weapon.TransformDirection(Vector3.up) - bladeTarget) * 0.8f;
                Vector3 f = (Weapon.TransformDirection(Vector3.forward) - faceTarget) * 0.3f;
                r[0] = p.x; r[1] = p.y; r[2] = p.z; r[3] = b.x; r[4] = b.y; r[5] = b.z; r[6] = f.x; r[7] = f.y; r[8] = f.z;
            }

            private static float Sum(float[] r) { float s = 0f; for (int i = 0; i < r.Length; i++) s += r[i] * r[i]; return s; }

            public void Pose(ref HumanPose frame, float lean, Vector3 offs, Vector3 bladeTarget, Vector3 faceTarget)
            {
                CopyPose(PoseA, ref frame);
                if (SpineFB >= 0) frame.muscles[SpineFB] += lean;
                if (ChestFB >= 0) frame.muscles[ChestFB] += lean * 0.6f;
                for (int i = 0; i < _arm.Length; i++) frame.muscles[_arm[i]] = Previous[_arm[i]];
                Handler.SetHumanPose(ref frame);

                Vector3 wristTarget = Upper.position + offs;
                Vector3.OrthoNormalize(ref bladeTarget, ref faceTarget);

                int n = _arm.Length;
                float[] x = new float[n], trial = new float[n], r = new float[ResidualSize], rTrial = new float[ResidualSize], rJ = new float[ResidualSize];
                float[,] jac = new float[ResidualSize, n];
                for (int i = 0; i < n; i++) x[i] = Previous[_arm[i]];
                Residual(ref frame, x, wristTarget, bladeTarget, faceTarget, r);
                float cost = Sum(r);
                float lambda = 1e-2f;

                for (int iteration = 0; iteration < 24 && cost > 1e-6f; iteration++)
                {
                    for (int j = 0; j < n; j++)
                    {
                        float old = x[j];
                        x[j] = old + 1e-3f;
                        Residual(ref frame, x, wristTarget, bladeTarget, faceTarget, rJ);
                        x[j] = old;
                        for (int k = 0; k < ResidualSize; k++) jac[k, j] = (rJ[k] - r[k]) / 1e-3f;
                    }

                    bool improved = false;
                    for (int attempt = 0; attempt < 6 && !improved; attempt++)
                    {
                        float[] step = SolveNormalEquations(jac, r, n, lambda);
                        for (int j = 0; j < n; j++) trial[j] = Mathf.Clamp(x[j] + Mathf.Clamp(step[j], -0.6f, 0.6f), -3f, 3f);
                        Residual(ref frame, trial, wristTarget, bladeTarget, faceTarget, rTrial);
                        float trialCost = Sum(rTrial);
                        if (trialCost < cost)
                        {
                            System.Array.Copy(trial, x, n);
                            System.Array.Copy(rTrial, r, ResidualSize);
                            cost = trialCost;
                            lambda = Mathf.Max(1e-5f, lambda * 0.4f);
                            improved = true;
                        }
                        else lambda *= 4f;
                    }
                    if (!improved) break;
                }

                Residual(ref frame, x, wristTarget, bladeTarget, faceTarget, r);
                LastResidual = Mathf.Max(LastResidual, Mathf.Sqrt(Sum(r)));
                // The muscles are kept as solved (not read back): GetHumanPose would wrap a twist at +-180 degrees (1.93 -> -1.83), which an
                // interpolating clip would play as a full unwind in one frame; the solved value continues past the wrap.
                System.Array.Copy(frame.muscles, Previous, frame.muscles.Length);
            }

            /// <summary>Solves (J^T J + lambda I) d = -J^T r for the step d.</summary>
            private static float[] SolveNormalEquations(float[,] jac, float[] r, int n, float lambda)
            {
                float[,] a = new float[n, n + 1];
                for (int i = 0; i < n; i++)
                {
                    for (int j = 0; j < n; j++)
                    {
                        float s = 0f;
                        for (int k = 0; k < jac.GetLength(0); k++) s += jac[k, i] * jac[k, j];
                        a[i, j] = s + (i == j ? lambda : 0f);
                    }
                    float g = 0f;
                    for (int k = 0; k < jac.GetLength(0); k++) g += jac[k, i] * r[k];
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
