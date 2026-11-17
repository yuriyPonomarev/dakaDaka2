using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Emberdelve.Combat;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.EditorTools;
using Emberdelve.Player;

namespace Emberdelve.Tests.Visual
{
    /// <summary>
    /// V3d: the first-person hands follow the real swing trajectory. Each test poses the arms exactly the way the game does (idle base pose,
    /// then <see cref="FirstPersonArmsDriver.Pose"/> on the pose of the pattern) and compares with the SwingPatternData itself: the blade
    /// direction is the pattern's, the sweep goes the pattern's way, the hand stays inside the camera frustum. Expectations are read from the
    /// data assets and the camera, never from source text.
    /// </summary>
    public sealed class FirstPersonSwingTests
    {
        private const string SwordId = "ashen_short_sword";
        private const string ShieldId = "emberbulwark_shield";

        [Test]
        public void WristAlignedElbowPreservesTheGripAndReducesWristBend()
        {
            var root = new GameObject("WristAlignmentTest");
            try
            {
                var upper = new GameObject("Upper").transform;
                upper.SetParent(root.transform, false);
                var lower = new GameObject("Lower").transform;
                lower.SetParent(upper, false);
                lower.localPosition = Vector3.right * .3f;
                var hand = new GameObject("Hand").transform;
                hand.SetParent(lower, false);
                hand.localPosition = Vector3.right * .3f;
                var grip = new GameObject("Grip").transform;
                grip.SetParent(hand, false);
                grip.localPosition = Vector3.forward * .08f;
                Vector3 target = new Vector3(.4f, .15f, .08f);
                FirstPersonArmsDriver.SolveArm(upper, lower, hand, grip, target,
                    Quaternion.identity, Vector3.down, Vector3.forward);
                Assert.That(Vector3.Distance(grip.position, target), Is.LessThan(.0001f));
                Assert.That(Quaternion.Angle(grip.rotation, Quaternion.identity), Is.LessThan(.01f));
                Assert.That(Vector3.Angle(hand.position - lower.position, hand.forward), Is.LessThan(60f));
                Assert.That(Vector3.Distance(upper.position, lower.position), Is.EqualTo(.3f).Within(.0001f));
                Assert.That(Vector3.Distance(lower.position, hand.position), Is.EqualTo(.3f).Within(.0001f));
            }
            finally { Object.DestroyImmediate(root); }
        }

        [TestCaseSource(nameof(PatternNames))]
        public void NetworkRecoilStartsAtAcceptedContactAndReturnsToReady(string patternName)
        {
            var pattern=Pattern(patternName);
            const float stop=.37f;
            SwingPose contact=pattern.ActivePose(stop);
            WeaponVisualRig.SampleRecoil(pattern,stop,0f,false,out var start,out var startRotation);
            Assert.That(Vector3.Distance(start,contact.Base),Is.LessThan(.0001f));
            Assert.That(Quaternion.Angle(startRotation,contact.LocalRotation),Is.LessThan(.05f));
            WeaponVisualRig.SampleRecoil(pattern,stop,1f,false,out var end,out var endRotation);
            Assert.That(Vector3.Distance(end,GuardBladePose.RootRestPosition),Is.LessThan(.0001f));
            Assert.That(Quaternion.Angle(endRotation,WeaponVisualRig.ReadyRotation),Is.LessThan(.05f));
            WeaponVisualRig.SampleRecoil(pattern,stop,.5f,false,out var middle,out _);
            Assert.That(Vector3.Distance(middle,(start+end)*.5f),Is.LessThan(.0001f));
        }

        [TestCase(1f)]
        [TestCase(FirstPersonArmsDriver.ModelScale)]
        [TestCase(0.75f)]
        public void ArmSolver_PlacesScaledPalmGripOnTarget(float scale)
        {
            GameObject root = new GameObject("ScaledArmTest");
            try
            {
                root.transform.localScale = Vector3.one * scale;
                Transform upper = new GameObject("Upper").transform;
                upper.SetParent(root.transform, false);
                Transform lower = new GameObject("Lower").transform;
                lower.SetParent(upper, false);
                lower.localPosition = Vector3.right * 0.4f;
                Transform hand = new GameObject("Hand").transform;
                hand.SetParent(lower, false);
                hand.localPosition = Vector3.right * 0.4f;
                Transform grip = new GameObject("Grip").transform;
                grip.SetParent(hand, false);
                grip.localPosition = new Vector3(0.08f, -0.03f, 0.04f);
                grip.localRotation = Quaternion.Euler(10f, 20f, 30f);
                Vector3 target = new Vector3(0.2f, -0.3f, 0.3f) * scale;
                Quaternion rotation = Quaternion.Euler(40f, -35f, 15f);

                FirstPersonArmsDriver.SolveArm(upper, lower, hand, grip, target, rotation, Vector3.down);

                Assert.That(Vector3.Distance(grip.position, target), Is.LessThan(0.0001f));
                Assert.That(Quaternion.Angle(grip.rotation, rotation), Is.LessThan(0.05f));
                Assert.That(Vector3.Distance(upper.position, lower.position), Is.EqualTo(0.4f * scale).Within(0.0001f));
                Assert.That(Vector3.Distance(lower.position, hand.position), Is.EqualTo(0.4f * scale).Within(0.0001f));
            }
            finally { Object.DestroyImmediate(root); }
        }

        private sealed class Scene : System.IDisposable
        {
            public GameObject Root;
            public Camera Camera;
            public GameObject Arms;
            public FirstPersonArmsDriver Driver;

            public static Scene Create(string weaponId, string shieldId)
            {
                Scene s = new Scene();
                s.Root = new GameObject("V3dTestCamera");
                s.Root.transform.position = new Vector3(4000f, 1.66f, 4000f);
                s.Camera = s.Root.AddComponent<Camera>();
                s.Camera.fieldOfView = 70f;
                s.Camera.aspect = 16f / 9f;
                s.Camera.nearClipPlane = 0.05f;
                WeaponViewModelSet set = WeaponViewModelSet.Load();
                Assert.IsNotNull(set);
                s.Arms = (GameObject)PrefabUtility.InstantiatePrefab(set.armsPrefab);
                s.Arms.transform.SetParent(s.Root.transform, false);
                s.Driver = s.Arms.GetComponent<FirstPersonArmsDriver>();
                s.Driver.Bind(null, s.Camera, set);
                s.Driver.SetWeapons(EmberdelveViewmodelPreview.FindWeapon(weaponId), string.IsNullOrEmpty(shieldId) ? null : EmberdelveViewmodelPreview.FindWeapon(shieldId));
                return s;
            }

            public void Dispose()
            {
                if (Root != null) Object.DestroyImmediate(Root);
            }

            public void Pose(SwingPatternData pattern, float time, float guard = 0f)
            {
                EmberdelveViewmodelPreview.PoseArms(Arms, Driver, Camera, pattern, time, guard);
            }

            /// <summary>Blade direction of the held model in the camera frame (the grip's +Y is the tip).</summary>
            public Vector3 TipDirection() => Camera.transform.InverseTransformDirection(Driver.GripRight.up);

            public Vector3 GripLocal() => Camera.transform.InverseTransformPoint(Driver.GripRight.position);

            public bool InFrustum(Vector3 world)
            {
                Plane[] planes = GeometryUtility.CalculateFrustumPlanes(Camera);
                for (int i = 0; i < planes.Length; i++) if (planes[i].GetDistanceToPoint(world) < 0f) return false;
                return true;
            }
        }

        private static IEnumerable<SwingPatternData> MeleePatterns()
        {
            string[] guids = AssetDatabase.FindAssets("t:SwingPatternData");
            for (int i = 0; i < guids.Length; i++)
            {
                SwingPatternData pattern = AssetDatabase.LoadAssetAtPath<SwingPatternData>(AssetDatabase.GUIDToAssetPath(guids[i]));
                if (pattern != null && pattern.keys != null && pattern.keys.Length >= 2) yield return pattern;
            }
        }

        private static IEnumerable<string> PatternNames()
        {
            foreach (SwingPatternData pattern in MeleePatterns()) yield return pattern.name;
        }

        private static SwingPatternData Pattern(string name)
        {
            foreach (SwingPatternData pattern in MeleePatterns()) if (pattern.name == name) return pattern;
            Assert.Fail("pattern " + name + " exists");
            return null;
        }

        private static float YawOf(Vector3 direction) => Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;
        private static float PitchDownOf(Vector3 direction) => -Mathf.Asin(Mathf.Clamp(direction.normalized.y, -1f, 1f)) * Mathf.Rad2Deg;

        [TestCaseSource(nameof(PatternNames))]
        public void Blade_FollowsDamageDirectionDuringActive_AndEasedPresentationOutside(string patternName)
        {
            SwingPatternData pattern = Pattern(patternName);
            SwingTiming timing = pattern.Timing;
            using (Scene scene = Scene.Create(SwordId, ShieldId))
            {
                float[] times = { 0f, timing.Windup * 0.5f, timing.Windup, timing.Windup + timing.Active * 0.5f, timing.ActiveEnd, timing.ActiveEnd + timing.Recovery * 0.5f, timing.Total };
                for (int i = 0; i < times.Length; i++)
                {
                    scene.Pose(pattern, times[i]);
                    // Active samples must remain exact damage geometry; windup/recovery use the accepted ready-pose blend.
                    WeaponVisualRig.SamplePresentation(pattern, timing, times[i], FirstPersonArmsDriver.PatternRest,
                        WeaponVisualRig.ReadyRotation, false, out _, out Quaternion presented);
                    bool active = times[i] >= timing.Windup && times[i] <= timing.ActiveEnd;
                    Vector3 expected = active ? pattern.Evaluate(times[i], timing).LocalDirection : presented * Vector3.forward;
                    float angle = Vector3.Angle(expected, scene.TipDirection());
                    Assert.Less(angle, 3f, patternName + " t=" + times[i] + ": the model's blade points along the pattern's blade direction");
                }
            }
        }

        [TestCaseSource(nameof(PatternNames))]
        public void Sweep_GoesTheWayOfThePattern(string patternName)
        {
            SwingPatternData pattern = Pattern(patternName);
            SwingTiming timing = pattern.Timing;
            SwingPose start = pattern.Evaluate(timing.Windup, timing);
            SwingPose end = pattern.Evaluate(timing.ActiveEnd, timing);
            using (Scene scene = Scene.Create(SwordId, ShieldId))
            {
                scene.Pose(pattern, timing.Windup);
                Vector3 tipStart = scene.TipDirection();
                Vector3 handStart = scene.GripLocal();
                scene.Pose(pattern, timing.Windup + timing.Active * 0.5f);
                Vector3 tipMid = scene.TipDirection();
                scene.Pose(pattern, timing.ActiveEnd);
                Vector3 tipEnd = scene.TipDirection();
                Vector3 handEnd = scene.GripLocal();

                float yawData = Mathf.DeltaAngle(YawOf(start.LocalDirection), YawOf(end.LocalDirection));
                float yawMidData = Mathf.DeltaAngle(YawOf(start.LocalDirection), YawOf(pattern.Evaluate(timing.Windup + timing.Active * .5f, timing).LocalDirection));
                float pitchData = end.Pitch - start.Pitch;
                float forwardData = end.Base.z - start.Base.z;

                if (Mathf.Abs(yawData) > 5f)
                {
                    Assert.AreEqual(Mathf.Sign(yawData), Mathf.Sign(Mathf.DeltaAngle(YawOf(tipStart), YawOf(tipEnd))), patternName + ": the blade sweeps to the side the pattern's yaw does");
                    Assert.AreEqual(Mathf.Sign(yawMidData), Mathf.Sign(Mathf.DeltaAngle(YawOf(tipStart), YawOf(tipMid))), patternName + ": and already at the middle of the active phase");
                }

                if (Mathf.Abs(pitchData) > 5f)
                {
                    Assert.AreEqual(Mathf.Sign(pitchData), Mathf.Sign(PitchDownOf(tipEnd) - PitchDownOf(tipStart)), patternName + ": the blade moves up or down as the pattern's pitch does (overhead = top-down)");
                }

                if (forwardData > 0.3f)
                {
                    Assert.Greater(handEnd.z - handStart.z, 0.03f, patternName + ": a thrust brings the hand forward");
                }

                if (pattern.kind == SwingKind.HorizontalLeftToRight) Assert.Greater(handEnd.x, handStart.x, patternName + ": left-to-right moves the hand to the right");
                if (pattern.kind == SwingKind.HorizontalRightToLeft) Assert.Less(handEnd.x, handStart.x, patternName + ": right-to-left moves the hand to the left");
                if (pattern.kind == SwingKind.Overhead) Assert.Greater(PitchDownOf(tipEnd), PitchDownOf(tipStart) + 20f, patternName + ": the overhead comes down");
            }
        }

        [TestCaseSource(nameof(PatternNames))]
        public void Presentation_IsContinuousAtDamageWindowBoundaries(string patternName)
        {
            var pattern = Pattern(patternName);
            var timing = pattern.Timing;
            foreach (float boundary in new[] { timing.Windup, timing.ActiveEnd })
            {
                WeaponVisualRig.SamplePresentation(pattern, timing, boundary - .00001f, FirstPersonArmsDriver.PatternRest,
                    WeaponVisualRig.ReadyRotation, false, out var before, out var beforeRotation);
                WeaponVisualRig.SamplePresentation(pattern, timing, boundary + .00001f, FirstPersonArmsDriver.PatternRest,
                    WeaponVisualRig.ReadyRotation, false, out var after, out var afterRotation);
                Assert.Less(Vector3.Distance(before, after), .002f, patternName + " boundary translation");
                Assert.Less(Quaternion.Angle(beforeRotation, afterRotation), .2f, patternName + " boundary rotation");
            }
        }

        [Test]
        public void LeftToRight_And_RightToLeft_AreDistinctMotions()
        {
            SwingPatternData lr = Pattern("Pattern_SwordSlashLR");
            SwingPatternData rl = Pattern("Pattern_SwordSlashRL");
            using (Scene scene = Scene.Create(SwordId, ShieldId))
            {
                float Sweep(SwingPatternData p)
                {
                    SwingTiming t = p.Timing;
                    scene.Pose(p, t.Windup);
                    float a = YawOf(scene.TipDirection());
                    scene.Pose(p, t.ActiveEnd);
                    return YawOf(scene.TipDirection()) - a;
                }

                float lrSweep = Sweep(lr);
                float rlSweep = Sweep(rl);
                Assert.Greater(lrSweep, 20f, "LR sweeps right");
                Assert.Less(rlSweep, -20f, "RL sweeps left");
            }
        }

        [TestCaseSource(nameof(PatternNames))]
        public void SwordGrip_FollowsTheAcceptedLoweredPresentation_ThroughTheWholeSwing(string patternName)
        {
            SwingPatternData pattern = Pattern(patternName);
            SwingTiming timing = pattern.Timing;
            using (Scene scene = Scene.Create(SwordId, ShieldId))
            {
                int steps = 16;
                for (int i = 0; i <= steps; i++)
                {
                    float time = timing.Total * i / steps;
                    scene.Pose(pattern, time);
                    WeaponVisualRig.SamplePresentation(pattern, timing, time, FirstPersonArmsDriver.PatternRest,
                        WeaponVisualRig.ReadyRotation, false, out Vector3 presentedBase, out _);
                    Vector3 framed = FirstPersonArmsDriver.ClampToView(scene.Camera, FirstPersonArmsDriver.ViewHand(presentedBase));
                    // The accepted sword framing intentionally lowers the hilt by 20 cm below the clamped target.
                    // Validate the pre-offset framing AND the final exact hand target instead of requiring the lowered hilt onscreen.
                    Assert.IsTrue(scene.InFrustum(scene.Camera.transform.TransformPoint(framed)));
                    Vector3 expected = scene.Camera.transform.TransformPoint(framed + Vector3.down * .20f);
                    Assert.Less(Vector3.Distance(expected, scene.Driver.GripRight.position), 0.06f, patternName + " t=" + time.ToString("0.000") + ": the arm reaches the hand target");
                }
            }
        }

        [Test]
        public void ViewHand_NeverMovesTheHandOutsideTheFrustumForAnyAuthoredBase()
        {
            using (Scene scene = Scene.Create(SwordId, ShieldId))
            {
                foreach (SwingPatternData pattern in MeleePatterns())
                {
                    for (int k = 0; k < pattern.keys.Length; k++)
                    {
                        Vector3 local = FirstPersonArmsDriver.ClampToView(scene.Camera, FirstPersonArmsDriver.ViewHand(pattern.keys[k].basePos));
                        Assert.IsTrue(scene.InFrustum(scene.Camera.transform.TransformPoint(local)), pattern.name + " key " + k);
                    }
                }
            }
        }

        [Test]
        public void Block_RaisesTheShield_AndBringsTheBladeToTheGuardPose()
        {
            using (Scene scene = Scene.Create(SwordId, ShieldId))
            {
                scene.Pose(null, -1f, 0f);
                Vector3 restShield = scene.Camera.transform.InverseTransformPoint(scene.Driver.GripLeft.position);
                scene.Pose(null, -1f, 1f);
                Vector3 raisedShield = scene.Camera.transform.InverseTransformPoint(scene.Driver.GripLeft.position);
                Assert.Greater(raisedShield.y, restShield.y + 0.05f, "the shield rises when the block is held");
                Assert.Greater(raisedShield.x, restShield.x + 0.05f, "and comes towards the middle");

                GuardBladePose.ResolveRoot(scene.Camera.transform.position, Quaternion.LookRotation(scene.Camera.transform.forward, Vector3.up), true, out Vector3 guardPosition, out Quaternion guardRotation);
                Vector3 expectedDirection = scene.Camera.transform.InverseTransformDirection(guardRotation * Vector3.forward);
                Assert.Less(Vector3.Angle(expectedDirection, scene.TipDirection()), 3f, "the held blade is the guard blade the projectile defence uses");
                Assert.IsTrue(scene.InFrustum(scene.Driver.GripRight.position));
                Assert.IsTrue(scene.InFrustum(scene.Driver.GripLeft.position));
            }
        }

        [Test]
        public void ShieldVisual_SurvivesAWeaponSwapSequence()
        {
            WeaponData sword = EmberdelveViewmodelPreview.FindWeapon(SwordId);
            WeaponData shield = EmberdelveViewmodelPreview.FindWeapon(ShieldId);
            WeaponData crossbow = EmberdelveViewmodelPreview.FindWeapon("ember_crossbow");
            Assert.IsNotNull(crossbow);
            using (Scene scene = Scene.Create(SwordId, ShieldId))
            {
                Assert.IsTrue(scene.Driver.ShieldShown, "spawn: sword and shield");

                // The procedural crossbow keeps the same arms alive, then the sword set restores its models.
                for (int round = 0; round < 3; round++)
                {
                    Assert.IsTrue(scene.Driver.SetWeapons(crossbow, shield), "crossbow supports the existing arms");
                    Assert.IsFalse(scene.Driver.ShieldShown, "the two-handed crossbow has no shield");
                    scene.Driver.SetWeapons(sword, shield);
                    scene.Arms.SetActive(true);

                    Assert.IsTrue(scene.Driver.ShieldShown, "round " + round + ": the shield model is back in the left hand");
                    Assert.IsTrue(scene.Driver.HasWeaponModel, "and the sword");
                    scene.Pose(Pattern("Pattern_SwordSlashLR"), -1f, 0f);
                    // The strap-aligned pivot can sit below the frame while the shield surface remains visible.
                    Plane[] planes = GeometryUtility.CalculateFrustumPlanes(scene.Camera);
                    bool shieldVisible = false;
                    foreach (Renderer renderer in scene.Driver.OffHandModel.GetComponentsInChildren<Renderer>(false))
                        shieldVisible |= renderer.enabled && GeometryUtility.TestPlanesAABB(planes, renderer.bounds);
                    Assert.IsTrue(shieldVisible, "the active shield model intersects the camera frustum");
                    Assert.Less(Vector3.Distance(scene.Camera.transform.InverseTransformPoint(scene.Driver.GripLeft.position), FirstPersonArmsDriver.ShieldRest), 0.08f, "the left hand holds it at its rest position");
                    Assert.AreEqual(0, CountEnabledShields(scene.Driver, shield) - 1, "exactly one shield is shown");
                }
            }
        }

        [Test] public void CrossbowLoadedVisual_FollowsAmmoAndReloadWithoutMovingMuzzle()
        {
            using (Scene scene = Scene.Create("ember_crossbow", null))
            {
                var visual = WeaponVisualBuilder.Build(EmberdelveViewmodelPreview.FindWeapon("ember_crossbow"), scene.Camera.transform, scene.Camera.transform);
                var bolt = visual.transform.Find("Crossbow_Bolt");
                var left = visual.transform.Find("Crossbow_String/Left");
                Vector3 muzzle = visual.Muzzle.position;
                visual.SetCrossbowState(true, false, 0f);
                Assert.IsTrue(bolt.gameObject.activeSelf);
                Assert.Less(left.localPosition.z, -.1f);
                Assert.That(bolt.localPosition.z - bolt.localScale.z * .5f, Is.EqualTo(-.22f).Within(.001f), "bolt tail meets cocked string");
                visual.SetDrawProgress(0f);
                Assert.IsTrue(bolt.gameObject.activeSelf, "idle draw reset must not hide loaded bolt");
                visual.SetCrossbowState(false, false, 0f);
                Assert.IsFalse(bolt.gameObject.activeSelf);
                Assert.That(left.localPosition.z, Is.EqualTo(-.025f).Within(.001f));
                visual.SetCrossbowState(false, true, .3f);
                Assert.IsFalse(bolt.gameObject.activeSelf);
                visual.SetCrossbowState(false, true, .65f);
                Assert.IsTrue(bolt.gameObject.activeSelf);
                visual.SetCrossbowState(false, false, 0f);
                Assert.IsFalse(bolt.gameObject.activeSelf, "cancelled reload removes uncommitted bolt");
                Assert.Less(Vector3.Distance(muzzle, visual.Muzzle.position), .0001f);
            }
        }

        [TestCase(0f, false)]
        [TestCase(.3f, true)]
        [TestCase(.65f, true)]
        [TestCase(1f, false)]
        public void CrossbowReload_HandReachesActionFromAbove_WithoutMovingWeapon(float progress, bool above)
        {
            WeaponData crossbow = EmberdelveViewmodelPreview.FindWeapon("ember_crossbow");
            using (Scene scene = Scene.Create("ember_crossbow", null))
            {
                Assert.IsTrue(scene.Driver.SetWeapons(crossbow, null));
                WeaponVisualRig visual = WeaponVisualBuilder.Build(crossbow, scene.Camera.transform, scene.Camera.transform);
                scene.Driver.SetVisual(visual);
                scene.Pose(null, -1f, 0f);
                Vector3 position = visual.transform.position;
                Quaternion rotation = visual.transform.rotation;
                scene.Driver.PoseCrossbow(scene.Camera.transform, visual.transform, true, progress);
                Vector3 grip = FirstPersonArmsDriver.CrossbowLeftGrip(true, progress);
                Assert.Less(Vector3.Distance(scene.Driver.SupportGrip.position, visual.transform.TransformPoint(grip)), .08f);
                Transform wrist = scene.Driver.GetComponentInChildren<Animator>().GetBoneTransform(HumanBodyBones.LeftHand);
                float relativeHeight = visual.transform.InverseTransformPoint(wrist.position).y - grip.y;
                if (above) Assert.Greater(relativeHeight, .02f, "reload wrist stays above the string/bolt rather than hidden behind the stock");
                else Assert.Less(relativeHeight, -.02f, "reload endpoints retain the ordinary support orientation");
                if (above)
                    Assert.Less(scene.Camera.WorldToViewportPoint(wrist.position).y, .6f,
                        "working wrist must not rise into the upper view and obscure the action");
                Assert.Less(Vector3.Distance(position, visual.transform.position), .0001f);
                Assert.Less(Quaternion.Angle(rotation, visual.transform.rotation), .001f);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CrossbowHands_FollowSingleVisibleProceduralWeapon_WithoutMovingGuard(bool raised)
        {
            WeaponData crossbow = EmberdelveViewmodelPreview.FindWeapon("ember_crossbow");
            using (Scene scene = Scene.Create("ember_crossbow", null))
            {
                Assert.IsTrue(scene.Driver.SetWeapons(crossbow, null));
                Assert.IsNull(scene.Driver.MainHandModel, "no duplicate weapon is parented to the hand");
                WeaponVisualRig visual = WeaponVisualBuilder.Build(crossbow, scene.Camera.transform, scene.Camera.transform);
                scene.Driver.SetVisual(visual);
                visual.SetGuardHeld(raised);
                Vector3 before = visual.transform.position;
                Quaternion rotation = visual.transform.rotation;
                scene.Pose(null, -1f, raised ? 1f : 0f);
                Assert.Less(Vector3.Distance(scene.Driver.GripRight.position,
                    visual.transform.TransformPoint(FirstPersonArmsDriver.CrossbowRightGrip)), .08f, "right palm reaches the grip");
                Assert.IsNotNull(scene.Driver.SupportGrip);
                Assert.Less(Vector3.Distance(scene.Driver.SupportGrip.position,
                    visual.transform.TransformPoint(FirstPersonArmsDriver.CrossbowSupportGrip)), .08f, "left palm reaches the stock support");
                Transform supportWrist = scene.Driver.GetComponentInChildren<Animator>().GetBoneTransform(HumanBodyBones.LeftHand);
                if (!raised)
                    Assert.Less(visual.transform.InverseTransformPoint(supportWrist.position).y,
                        FirstPersonArmsDriver.CrossbowSupportGrip.y - .02f,
                        "resting support wrist approaches from below the stock, keeping the forearm out of the sightline");
                Assert.That(Vector3.Distance(before, visual.transform.position), Is.LessThan(.0001f));
                Assert.That(Quaternion.Angle(rotation, visual.transform.rotation), Is.LessThan(.001f));
                Renderer stock = visual.transform.Find("Crossbow_Stock").GetComponent<Renderer>();
                Assert.IsTrue(stock.enabled && stock.gameObject.activeInHierarchy, "the sole procedural weapon stays visible");
                if (raised)
                {
                    CrossbowGuardShape shape = CrossbowGuardPose.Resolve(scene.Camera.transform.position,
                        Quaternion.LookRotation(scene.Camera.transform.forward, Vector3.up));
                    Assert.Less(Vector3.Distance(shape.Position, visual.transform.position), .0001f);
                    Assert.Less(Quaternion.Angle(shape.Rotation, visual.transform.rotation), .001f);
                }
            }
        }

        [Test]
        public void BowGuard_HandsFollowWood_WithoutMovingAuthorityPose()
        {
            WeaponData weapon=EmberdelveViewmodelPreview.FindWeapon("hollow_bow");
            using (Scene scene=Scene.Create("hollow_bow",null))
            {
                WeaponVisualRig visual=WeaponVisualBuilder.Build(weapon,scene.Camera.transform,scene.Camera.transform);
                scene.Driver.SetVisual(visual);
                visual.SetGuardHeld(true);
                Vector3 position=visual.transform.position;
                Quaternion rotation=visual.transform.rotation;
                scene.Pose(null,-1f,1f);
                var bow=visual.GetComponentInChildren<BowPresentation>();
                scene.Driver.PoseBowGuard(scene.Camera.transform,bow);
                Assert.Less(Vector3.Distance(scene.Driver.GripRight.position,bow.transform.TransformPoint(BowGuardPose.RightGrip)),.08f);
                Assert.Less(Vector3.Distance(scene.Driver.SupportGrip.position,bow.transform.TransformPoint(BowGuardPose.LeftGrip)),.08f);
                Assert.Less(Vector3.Distance(position,visual.transform.position),.0001f);
                Assert.Less(Quaternion.Angle(rotation,visual.transform.rotation),.001f);
                Assert.That(bow.Draw,Is.Zero);
                BowGuardShape shape=BowGuardPose.Resolve(scene.Camera.transform.position,scene.Camera.transform.rotation);
                Assert.Less(Vector3.Distance(position,shape.Position),.0001f);
            }
        }

        [Test]
        public void CrossbowReload_MovesSupportHandAndReturns_WithoutMovingWeaponOrRightGrip()
        {
            WeaponData crossbow = EmberdelveViewmodelPreview.FindWeapon("ember_crossbow");
            using (Scene scene = Scene.Create("ember_crossbow", null))
            {
                WeaponVisualRig visual = WeaponVisualBuilder.Build(crossbow, scene.Camera.transform, scene.Camera.transform);
                scene.Driver.SetVisual(visual);
                scene.Pose(null, -1f);
                Vector3 before = visual.transform.position;
                Vector3 right = scene.Driver.GripRight.position;
                Vector3 support = scene.Driver.SupportGrip.position;
                scene.Driver.PoseCrossbow(scene.Camera.transform, visual.transform, true, .5f);
                Assert.Greater(Vector3.Distance(scene.Driver.SupportGrip.position, support), .15f, "reload visibly pulls the string");
                Assert.Less(Vector3.Distance(scene.Driver.GripRight.position, right), .01f, "right hand keeps its grip");
                scene.Driver.PoseCrossbow(scene.Camera.transform, visual.transform, true, 1f);
                Assert.Less(Vector3.Distance(scene.Driver.SupportGrip.position, support), .01f, "reload returns to support");
                Assert.Less(Vector3.Distance(visual.transform.position, before), .0001f, "hands never reposition physical weapon");
            }
        }

        private static int CountEnabledShields(FirstPersonArmsDriver driver, WeaponData shield)
        {
            int count = 0;
            Transform[] all = driver.GripLeft.GetComponentsInChildren<Transform>(false);
            for (int i = 0; i < all.Length; i++) if (all[i].name == "Held_" + shield.weaponId) count++;
            return count;
        }

        [Test]
        public void Defense_KeepsTheShield_WhenTheRangedSetIsHeldAndTheMeleeSetReturns()
        {
            CharacterClassData warrior = null;
            string[] guids = AssetDatabase.FindAssets("t:CharacterClassData");
            for (int i = 0; i < guids.Length && warrior == null; i++)
            {
                CharacterClassData data = AssetDatabase.LoadAssetAtPath<CharacterClassData>(AssetDatabase.GUIDToAssetPath(guids[i]));
                if (data != null && data.offhandShield != null && data.primaryWeapon != null && !data.primaryWeapon.IsRanged
                    && data.secondaryWeapon != null && data.secondaryWeapon.IsRanged) warrior = data;
            }

            Assert.IsNotNull(warrior, "a class with sword + shield and a ranged second set exists");
            DefenseProfile before = DefenseProfile.Resolve(warrior, warrior.primaryWeapon, warrior.offhandShield, 0f);
            DefenseProfile ranged = DefenseProfile.Resolve(warrior, warrior.secondaryWeapon, warrior.offhandShield, 0f);
            DefenseProfile after = DefenseProfile.Resolve(warrior, warrior.primaryWeapon, warrior.offhandShield, 0f);
            Assert.IsTrue(before.HasShield, "spawn: the shield defends");
            Assert.IsTrue(ranged.CanBlock && warrior.secondaryWeapon.CanCrossbowGuard, "crossbow blocks with its own physical shape");
            Assert.IsFalse(ranged.HasShield, "the two-handed crossbow does not borrow the stowed shield");
            Assert.IsTrue(after.HasShield, "after the swap back the shield defends again (gameplay is not what disappeared)");
            Assert.AreEqual(before.BlockReduction, after.BlockReduction, 1e-5f);
        }

        [Test]
        public void ShieldHeld_ShowsTheLeftHandOnTheShieldGrip_NotAnIdleFist()
        {
            using (Scene scene = Scene.Create(SwordId, ShieldId))
            {
                scene.Pose(Pattern("Pattern_SwordSlashLR"), 0.1f, 0f);
                Vector3 expected = FirstPersonArmsDriver.ShieldRest;
                Vector3 actual = scene.Camera.transform.InverseTransformPoint(scene.Driver.GripLeft.position);
                Assert.Less(Vector3.Distance(expected, actual), 0.08f, "the shield stays at its rest place while the right hand swings");
            }
        }

        [Test]
        public void TwoHandedWeapon_IsHeldWithBothHands_LeftHandOnTheSecondGrip_EverywhereInTheSwing()
        {
            int checkedWeapons = 0;
            foreach (string guid in AssetDatabase.FindAssets("t:WeaponData"))
            {
                WeaponData weapon = AssetDatabase.LoadAssetAtPath<WeaponData>(AssetDatabase.GUIDToAssetPath(guid));
                if (weapon == null || weapon.Hands != HandUse.TwoHanded || weapon.IsRanged || !weapon.UsesSweptMelee) continue;
                checkedWeapons++;
                using (Scene scene = Scene.Create(weapon.weaponId, ShieldId))
                {
                    Assert.That(scene.Driver.WeaponScale, Is.GreaterThan(0f));
                    float reach = FirstPersonArmsDriver.SecondGripOffset * scene.Driver.WeaponScale + 0.05f;
                    foreach (SwingPatternData pattern in weapon.swingPatterns)
                    {
                        for (int i = 0; i <= 4; i++)
                        {
                            scene.Pose(pattern, pattern.Timing.Windup + pattern.Timing.Active * (i / 4f), 0f);
                            float apart = Vector3.Distance(scene.Driver.GripLeft.position, scene.Driver.GripRight.position);
                            Assert.That(apart, Is.LessThan(reach + 0.35f), weapon.weaponId + " " + pattern.name + " step " + i + ": the left hand is on the weapon, not at the side");
                        }
                    }
                }
            }

            Assert.That(checkedWeapons, Is.GreaterThanOrEqualTo(1), "a two-handed melee weapon ships");
        }

        [Test]
        public void ModelScale_MatchesTheDamageSegmentWithinTheClamp()
        {
            WeaponViewModelSet set = WeaponViewModelSet.Load();
            foreach (string id in new[] { SwordId, "ember_rapier", "warden_maul" })
            {
                WeaponData weapon = EmberdelveViewmodelPreview.FindWeapon(id);
                WeaponViewModelSet.Entry entry = set.Find(id);
                if (weapon == null || entry == null) continue;
                float damage = FirstPersonArmsDriver.DamageBladeLength(weapon);
                float scale = FirstPersonArmsDriver.ModelScaleFor(damage, entry.length);
                Assert.GreaterOrEqual(scale, FirstPersonArmsDriver.MinModelScale);
                Assert.LessOrEqual(scale, FirstPersonArmsDriver.MaxModelScale);
                if (damage / entry.length >= FirstPersonArmsDriver.MinModelScale && damage / entry.length <= FirstPersonArmsDriver.MaxModelScale)
                    Assert.AreEqual(damage, entry.length * scale, 1e-3f, id + ": the shown blade is the damage segment");
            }
        }
    }
}
