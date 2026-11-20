using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Emberdelve.Data;
using Emberdelve.Player;

namespace Emberdelve.EditorTools
{
    /// <summary>
    /// Edit-mode motion review of the sword-and-shield viewmodel: a block press/release and a full combo chain rendered over time,
    /// with the same blend rates the game uses (FirstPersonArmsDriver), at the game's field of view.
    /// </summary>
    public static class EmberdelveShieldMotionReview
    {
        public const string Folder = "Logs/review/shield-motion";
        private const float Fov = 68f;
        private const int W = 480, H = 270;

        private struct Frame
        {
            public float Time, Guard, Swing, Strike, Lower, Trail;
            public Vector3 Base;
            public Quaternion Rotation;
        }

        [MenuItem("Emberdelve/Debug/Render Shield Motion Review (Edit Mode)")]
        public static void Render()
        {
            if (EditorApplication.isPlaying) { Debug.LogError("[ShieldMotionReview] leave Play mode first"); return; }
            WeaponData sword = EmberdelveViewmodelPreview.FindWeapon("ashen_short_sword");
            WeaponData shield = EmberdelveViewmodelPreview.FindWeapon("emberbulwark_shield");
            RenderSheet("block", sword, shield, BlockTimeline(), 5);
            RenderSheet("combo", sword, shield, ComboTimeline(sword), 5);
            Debug.Log("[ShieldMotionReview] rendered to " + Folder);
        }

        [MenuItem("Emberdelve/Debug/Render Two-Handed Motion Review (Edit Mode)")]
        public static void RenderTwoHanded()
        {
            if (EditorApplication.isPlaying) { Debug.LogError("[ShieldMotionReview] leave Play mode first"); return; }
            WeaponData great = EmberdelveViewmodelPreview.FindWeapon("two_handed_sword");
            WeaponData sword = EmberdelveViewmodelPreview.FindWeapon("ashen_short_sword");
            RenderSheet("2h-guard", great, null, BlockTimeline(blade: true, twoHanded: true), 5);
            RenderSheet("2h-combo", great, null, ComboTimeline(great), 5);
            RenderSheet("1h-guard", sword, null, BlockTimeline(blade: true), 5);
            RenderSheet("1h-riposte", sword, null, RiposteTimeline(sword), 5);
            RenderSheet("2h-riposte", great, null, RiposteTimeline(great), 5);
            Debug.Log("[ShieldMotionReview] two-handed rendered to " + Folder);
        }

        /// <summary>
        /// One still of the full blade guard at 960x540, the authority's guard box drawn over it (translucent blue, like GuardZoneVisual), and a
        /// report of where the held model's blade (cross-guard to tip) and the box ends are, in metres and in viewport coordinates.
        /// </summary>
        public static string RenderGuardStill(string name, WeaponData weapon, bool twoHanded)
        {
            var report = new System.Text.StringBuilder();
            string path = Folder + "/" + name + "-still.png";
            EmberdelveViewmodelPreview.Capture(path, 960, 540, Fov, true, (root, camera) =>
            {
                camera.transform.SetPositionAndRotation(EmberdelveViewmodelPreview.Origin + new Vector3(0f, 1.66f, 0f), Quaternion.identity);
                GameObject back = GameObject.CreatePrimitive(PrimitiveType.Cube);
                back.transform.SetParent(root, false);
                back.transform.localPosition = new Vector3(0f, 1.5f, 3.5f);
                back.transform.localScale = new Vector3(8f, 3f, 0.3f);
                Object.DestroyImmediate(back.GetComponent<Collider>());

                WeaponViewModelSet set = WeaponViewModelSet.Load();
                GameObject arms = (GameObject)PrefabUtility.InstantiatePrefab(set.armsPrefab);
                arms.transform.SetParent(camera.transform, false);
                FirstPersonArmsDriver driver = arms.GetComponent<FirstPersonArmsDriver>();
                driver.Bind(null, camera, set);
                driver.SetWeapons(weapon, null);
                driver.SetBlock(true);
                EmberdelveFirstPersonArms.PoseInstance(arms, AssetDatabase.LoadAssetAtPath<AnimationClip>(EmberdelveFirstPersonArms.ClipPath(FirstPersonArmsDriver.IdleState)), 0.3f);
                Quaternion aim = Quaternion.LookRotation(camera.transform.forward, Vector3.up);
                GuardBladePose.ResolveRoot(Vector3.zero, Quaternion.identity, true, out Vector3 guardBase, out Quaternion guardRotation, twoHanded);
                driver.Pose(camera.transform, camera, guardBase, guardRotation, 1f, Vector3.zero, Vector3.zero);

                GuardBladeBox box = GuardBladePose.Resolve(camera.transform.position, aim, true, twoHanded);
                GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                Object.DestroyImmediate(cube.GetComponent<Collider>());
                cube.transform.SetParent(root, true);
                cube.transform.SetPositionAndRotation(box.Center, box.Rotation);
                cube.transform.localScale = box.HalfExtents * 2f;
                Shader unlit = Shader.Find("Universal Render Pipeline/Unlit");
                var material = new Material(unlit);
                material.SetFloat("_Surface", 1f);
                material.SetFloat("_Blend", 0f);
                material.SetOverrideTag("RenderType", "Transparent");
                material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                material.SetInt("_ZWrite", 0);
                material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                material.renderQueue = 3000;
                material.SetColor("_BaseColor", new Color(0.2f, 0.45f, 1f, 0.45f));
                cube.GetComponent<Renderer>().sharedMaterial = material;

                Vector3 boxNear = box.Center - box.Rotation * Vector3.forward * box.HalfExtents.z;
                Vector3 boxFar = box.Center + box.Rotation * Vector3.forward * box.HalfExtents.z;
                Transform held = null;
                foreach (Transform t in arms.GetComponentsInChildren<Transform>(true)) if (t.name == "Held_" + weapon.weaponId) { held = t; break; }
                report.AppendLine($"{name}: box {box.HalfExtents.z * 2f:F2} m, near {Vp(camera, boxNear)} far {Vp(camera, boxFar)}");
                if (held != null)
                {
                    float minY = float.PositiveInfinity, maxY = float.NegativeInfinity;
                    foreach (MeshFilter f in held.GetComponentsInChildren<MeshFilter>(true))
                    {
                        if (f.sharedMesh == null) continue;
                        foreach (Vector3 v in f.sharedMesh.vertices)
                        {
                            float y = held.InverseTransformPoint(f.transform.TransformPoint(v)).y;
                            minY = Mathf.Min(minY, y); maxY = Mathf.Max(maxY, y);
                        }
                    }
                    Vector3 tip = held.TransformPoint(new Vector3(0f, maxY, 0f));
                    Vector3 guard = held.TransformPoint(new Vector3(0f, 0.17f, 0f));
                    Vector3 pommel = held.TransformPoint(new Vector3(0f, minY, 0f));
                    report.AppendLine($"  model blade {(tip - guard).magnitude:F2} m (whole {(tip - pommel).magnitude:F2}), guard {Vp(camera, guard)} tip {Vp(camera, tip)}");
                    report.AppendLine($"  guard->box near {(guard - boxNear).magnitude:F3} m, tip->box far {(tip - boxFar).magnitude:F3} m, " +
                        $"tip off the box axis {Vector3.Cross(box.Rotation * Vector3.forward, tip - boxNear).magnitude:F3} m");
                    report.AppendLine($"  guard (aim frame) {camera.transform.InverseTransformPoint(guard):F3} tip (aim frame) {camera.transform.InverseTransformPoint(tip):F3}");
                    GuardBladePose.ResolveRoot(camera.transform.position, aim, true, out Vector3 rootWorld, out Quaternion rootRotation, twoHanded);
                    Quaternion toRoot = Quaternion.Inverse(rootRotation);
                    report.AppendLine($"  guard (root frame) {toRoot * (guard - rootWorld):F3} tip (root frame) {toRoot * (tip - rootWorld):F3}");
                }
            }, 0.05f);
            Debug.Log("[GuardStill] " + report);
            return report.ToString();
        }

        private static string Vp(Camera camera, Vector3 world)
        {
            Vector3 v = camera.WorldToViewportPoint(world);
            return $"({v.x:F2},{v.y:F2},{v.z:F2})";
        }

        /// <summary>Batch entry for the CI mirror only: guard stills of the short sword and the two-handed sword.</summary>
        public static void RenderGuardBatch()
        {
            UnityEditor.SceneManagement.EditorSceneManager.OpenScene("Assets/Emberdelve/Scenes/Emberdelve_NetBoot.unity");
            try
            {
                string text = RenderGuardStill("2h", EmberdelveViewmodelPreview.FindWeapon("two_handed_sword"), true)
                    + RenderGuardStill("1h", EmberdelveViewmodelPreview.FindWeapon("ashen_short_sword"), false);
                System.IO.File.WriteAllText(Folder + "/guard-report.txt", text);
            }
            catch (System.Exception e) { Debug.LogException(e); EditorApplication.Exit(1); return; }
            EditorApplication.Exit(0);
        }

        /// <summary>Batch entry for the CI mirror only: runs the data library build so new authored assets get created, then quits.</summary>
        public static void BuildLibraryBatch()
        {
            try { EmberdelveAssetLibrary.BuildAll(); AssetDatabase.SaveAssets(); }
            catch (System.Exception e) { Debug.LogException(e); EditorApplication.Exit(1); return; }
            EditorApplication.Exit(0);
        }

        /// <summary>Batch entry for the CI mirror: renders every review sheet, then quits.</summary>
        public static void RenderAllBatch()
        {
            // Batch Unity starts in an unsaved untitled scene, which cannot host the preview's additive scene.
            UnityEditor.SceneManagement.EditorSceneManager.OpenScene("Assets/Emberdelve/Scenes/Emberdelve_NetBoot.unity");
            try { RenderTwoHanded(); }
            catch (System.Exception e) { Debug.LogException(e); EditorApplication.Exit(1); return; }
            EditorApplication.Exit(0);
        }

        private static void GuardPose(out Vector3 position, out Quaternion rotation, bool twoHanded = false)
        {
            Vector3 eye = Vector3.zero;
            GuardBladePose.ResolveRoot(eye, Quaternion.identity, true, out Vector3 world, out Quaternion worldRotation, twoHanded);
            position = world - eye;
            rotation = worldRotation;
        }

        private static List<Frame> BlockTimeline(bool blade = false, bool twoHanded = false)
        {
            GuardPose(out Vector3 guardBase, out Quaternion guardRotation, twoHanded);
            var frames = new List<Frame>();
            float guard = 0f;
            const float dt = 1f / 60f;
            for (int i = 0; i <= 96; i++)
            {
                float t = i * dt;
                bool held = t >= 0.1f && t < 0.8f;
                if (i > 0) guard = Mathf.MoveTowards(guard, held ? 1f : 0f, (held ? FirstPersonArmsDriver.GuardRaiseRate : FirstPersonArmsDriver.GuardLowerRate) * dt);
                // A blade guard: the weapon rig snaps to the guard pose while held (the arms ease onto it); a shield leaves the rig at rest.
                bool atGuard = blade && held;
                if (i % 4 == 0) frames.Add(new Frame { Time = t, Guard = guard,
                    Base = atGuard ? guardBase : FirstPersonArmsDriver.PatternRest, Rotation = atGuard ? guardRotation : WeaponVisualRig.ReadyRotation });
            }
            return frames;
        }

        private static List<Frame> RiposteTimeline(WeaponData weapon)
        {
            var steps = new List<(SwingPatternData pattern, SwingTiming timing, float start)>();
            float cursor = 0.2f;
            for (int i = 0; i < weapon.RiposteStrikeCount; i++)
            {
                SwingPatternData pattern = weapon.swingPatterns[weapon.riposteSwingPatterns[i]];
                steps.Add((pattern, pattern.Timing, cursor));
                cursor += pattern.Timing.Total + weapon.riposteStrikeGap;
            }
            return StepsTimeline(steps, cursor);
        }

        private static List<Frame> ComboTimeline(WeaponData weapon)
        {
            var steps = new List<(SwingPatternData pattern, SwingTiming timing, float start)>();
            float cursor = 0.2f;
            for (int i = 0; i < weapon.comboSteps.Length; i++)
            {
                ComboStep cs = weapon.comboSteps[i];
                SwingPatternData pattern = weapon.swingPatterns[cs.patternIndex];
                SwingTiming timing = pattern.TimingFor(cs.windupScale, cs.recoveryScale, i == weapon.comboSteps.Length - 1 ? weapon.comboFinisherRecovery : 0f);
                steps.Add((pattern, timing, cursor));
                cursor += timing.Total;
            }
            return StepsTimeline(steps, cursor);
        }

        private static List<Frame> StepsTimeline(List<(SwingPatternData pattern, SwingTiming timing, float start)> steps, float cursor)
        {
            float end = cursor + 0.7f;

            var frames = new List<Frame>();
            float swing = 0f, strike = 0f, lower = 0f, trail = 0f, lastX = FirstPersonArmsDriver.PatternRest.x;
            const float dt = 1f / 60f;
            int n = Mathf.CeilToInt(end / dt);
            for (int i = 0; i <= n; i++)
            {
                float t = i * dt;
                Vector3 b = FirstPersonArmsDriver.PatternRest;
                Quaternion r = WeaponVisualRig.ReadyRotation;
                bool swinging = false, active = false;
                Vector3 startBase = FirstPersonArmsDriver.PatternRest;
                Quaternion startRotation = WeaponVisualRig.ReadyRotation;
                for (int s = 0; s < steps.Count; s++)
                {
                    var step = steps[s];
                    float local = t - step.start;
                    bool last = s == steps.Count - 1;
                    if (local >= 0f && local < step.timing.Total)
                    {
                        swinging = true;
                        active = local >= step.timing.Windup && local <= step.timing.ActiveEnd;
                        WeaponVisualRig.SamplePresentation(step.pattern, step.timing, local, startBase, startRotation, false, out b, out r);
                        if (!last && local > step.timing.ActiveEnd)
                        {
                            SwingPose hold = step.pattern.Evaluate(step.timing.ActiveEnd, step.timing);
                            b = hold.Base; r = hold.LocalRotation;
                        }
                    }
                    SwingPose endPose = step.pattern.Evaluate(step.timing.ActiveEnd, step.timing);
                    startBase = endPose.Base; startRotation = endPose.LocalRotation;
                }
                if (i > 0)
                {
                    swing = Mathf.MoveTowards(swing, swinging ? 1f : 0f, (swinging ? 10f : 6f) * dt);
                    strike = Mathf.MoveTowards(strike, active ? 1f : 0f, 14f * dt);
                    lower = Mathf.MoveTowards(lower, swinging ? 1f : 0f, (swinging ? FirstPersonArmsDriver.ShieldDropRate : FirstPersonArmsDriver.ShieldReturnRate) * dt);
                    float target = swinging ? FirstPersonArmsDriver.TrailFromVelocity((b.x - lastX) / dt) : 0f;
                    trail = Mathf.Lerp(trail, target, 1f - Mathf.Exp(-20f * dt));
                }
                lastX = b.x;
                if (i % 4 == 0) frames.Add(new Frame { Time = t, Swing = swing, Strike = strike, Lower = lower, Trail = trail, Base = b, Rotation = r });
            }
            return frames;
        }

        private static void RenderSheet(string name, WeaponData sword, WeaponData shield, List<Frame> frames, int columns)
        {
            var files = new List<string>();
            for (int i = 0; i < frames.Count; i++)
            {
                Frame f = frames[i];
                string path = Folder + "/" + name + "-" + i.ToString("000") + ".png";
                EmberdelveViewmodelPreview.Capture(path, W, H, Fov, true, (root, camera) =>
                {
                    camera.transform.SetPositionAndRotation(EmberdelveViewmodelPreview.Origin + new Vector3(0f, 1.66f, 0f), Quaternion.identity);
                    GameObject back = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    back.transform.SetParent(root, false);
                    back.transform.localPosition = new Vector3(0f, 1.5f, 3.5f);
                    back.transform.localScale = new Vector3(8f, 3f, 0.3f);
                    Object.DestroyImmediate(back.GetComponent<Collider>());
                    Material wall = AssetDatabase.LoadAssetAtPath<Material>("Assets/Emberdelve/Art/Materials/Stone_Wall.mat");
                    if (wall != null) back.GetComponent<Renderer>().sharedMaterial = wall;

                    WeaponViewModelSet set = WeaponViewModelSet.Load();
                    GameObject arms = (GameObject)PrefabUtility.InstantiatePrefab(set.armsPrefab);
                    arms.transform.SetParent(camera.transform, false);
                    FirstPersonArmsDriver driver = arms.GetComponent<FirstPersonArmsDriver>();
                    driver.Bind(null, camera, set);
                    driver.SetWeapons(sword, shield);
                    driver.SetBlock(f.Guard > 0.5f);
                    EmberdelveFirstPersonArms.PoseInstance(arms, AssetDatabase.LoadAssetAtPath<AnimationClip>(EmberdelveFirstPersonArms.ClipPath(FirstPersonArmsDriver.IdleState)), 0.3f);
                    driver.Pose(camera.transform, camera, f.Base, f.Rotation, f.Guard, Vector3.zero, Vector3.zero,
                        swingBlend: f.Swing, strikeBlend: f.Strike, shieldLower: f.Lower, swingTrail: f.Trail);

                    // Screen crosshair marker so framing can be compared with the reference.
                    GameObject dot = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    dot.transform.SetParent(root, false);
                    dot.transform.position = camera.transform.position + camera.transform.forward * 3f;
                    dot.transform.localScale = Vector3.one * 0.02f;
                    Object.DestroyImmediate(dot.GetComponent<Collider>());
                }, 0.05f);
                files.Add(path);
            }
            EmberdelveViewmodelPreview.Sheet(Folder + "/" + name + "-sheet.png", files, columns, W, H);
        }
    }
}
