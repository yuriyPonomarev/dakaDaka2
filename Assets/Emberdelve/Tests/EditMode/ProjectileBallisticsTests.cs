using Emberdelve.Ballistics;
using NUnit.Framework;

namespace Emberdelve.Rules.Tests
{
    public sealed class ProjectileBallisticsTests
    {
        private const float Bow = 42f;
        private const float BowGravity = 6f;

        private static BallisticState Launch(float speed, float pitchRadians)
        {
            return new BallisticState(0f, 0f, 0f,
                0f, speed * (float)System.Math.Sin(pitchRadians), speed * (float)System.Math.Cos(pitchRadians));
        }

        private static BallisticState Run(BallisticState s, float gravity, int steps)
        {
            for (int i = 0; i < steps; i++) s = ProjectileBallistics.Advance(s, gravity, ProjectileBallistics.StepSeconds);
            return s;
        }

        [Test]
        public void Advance_WithoutGravity_IsAStraightLine()
        {
            BallisticState s = Run(new BallisticState(1f, 2f, 3f, 0f, 0f, 30f), 0f, 60);
            Assert.That(s.PX, Is.EqualTo(1f).Within(1e-4f));
            Assert.That(s.PY, Is.EqualTo(2f).Within(1e-4f));
            Assert.That(s.PZ, Is.EqualTo(33f).Within(1e-3f));
            Assert.That(s.VY, Is.EqualTo(0f));
        }

        [Test]
        public void Advance_MatchesTheClosedFormParabola()
        {
            BallisticState start = Launch(Bow, 0.05f);
            BallisticState stepped = Run(start, BowGravity, 60);
            BallisticState closed = ProjectileBallistics.PositionAt(start, BowGravity, 60 * ProjectileBallistics.StepSeconds);
            Assert.That(stepped.PY, Is.EqualTo(closed.PY).Within(2e-3f));
            Assert.That(stepped.PZ, Is.EqualTo(closed.PZ).Within(2e-3f));
            Assert.That(stepped.VY, Is.EqualTo(closed.VY).Within(2e-3f));
        }

        [Test]
        public void Advance_TheStepSizeDoesNotChangeTheArc()
        {
            BallisticState start = Launch(Bow, 0.02f);
            BallisticState fine = Run(start, BowGravity, 60);
            BallisticState coarse = start;
            for (int i = 0; i < 30; i++) coarse = ProjectileBallistics.Advance(coarse, BowGravity, 2f * ProjectileBallistics.StepSeconds);
            Assert.That(coarse.PY, Is.EqualTo(fine.PY).Within(2e-3f));
            Assert.That(coarse.PZ, Is.EqualTo(fine.PZ).Within(2e-3f));
        }

        [Test]
        public void Advance_TwoRunsAreBitIdentical()
        {
            BallisticState a = Run(Launch(Bow, 0.03f), BowGravity, 97);
            BallisticState b = Run(Launch(Bow, 0.03f), BowGravity, 97);
            Assert.That(a.PX, Is.EqualTo(b.PX));
            Assert.That(a.PY, Is.EqualTo(b.PY));
            Assert.That(a.PZ, Is.EqualTo(b.PZ));
            Assert.That(a.VY, Is.EqualTo(b.VY));
        }

        [Test]
        public void DropBelowAimLine_AtFortyMetres_IsGravityTimesTimeSquaredOverTwo()
        {
            float expected = 0.5f * BowGravity * (40f / Bow) * (40f / Bow);
            Assert.That(ProjectileBallistics.DropBelowAimLine(Bow, BowGravity, 40f), Is.EqualTo(expected).Within(1e-5f));
            Assert.That(expected, Is.EqualTo(2.7211f).Within(1e-3f));
        }

        [Test]
        public void SteppedFlight_DropsBelowTheAimLineByThePredictedAmount()
        {
            // Level shot: after n steps the arrow has gone Bow*t along z, so the aim line is at y = 0.
            int steps = 57;
            BallisticState s = Run(new BallisticState(0f, 0f, 0f, 0f, 0f, Bow), BowGravity, steps);
            float aimDistance = Bow * ProjectileBallistics.AirTime(steps);
            Assert.That(s.PZ, Is.EqualTo(aimDistance).Within(1e-3f));
            Assert.That(-s.PY, Is.EqualTo(ProjectileBallistics.DropBelowAimLine(Bow, BowGravity, aimDistance)).Within(2e-3f));
        }

        [Test]
        public void Crossbow_IsFlatterThanTheBow()
        {
            float bowDrop = ProjectileBallistics.DropBelowAimLine(42f, 6f, 30f);
            float crossbowDrop = ProjectileBallistics.DropBelowAimLine(54f, 3f, 30f);
            Assert.That(crossbowDrop, Is.LessThan(bowDrop * 0.5f));
        }

        [Test]
        public void FalloffScale_FollowsTheTable()
        {
            ProjectileFalloffKey[] bow =
            {
                new ProjectileFalloffKey(0f, 1f), new ProjectileFalloffKey(0.3f, 1f),
                new ProjectileFalloffKey(0.5f, 0.7f), new ProjectileFalloffKey(1.1f, 0.45f)
            };
            Assert.That(ProjectileBallistics.FalloffScale(bow, 0f), Is.EqualTo(1f));
            Assert.That(ProjectileBallistics.FalloffScale(bow, 0.3f), Is.EqualTo(1f).Within(1e-6f));
            Assert.That(ProjectileBallistics.FalloffScale(bow, 0.5f), Is.EqualTo(0.7f).Within(1e-6f));
            Assert.That(ProjectileBallistics.FalloffScale(bow, 0.4f), Is.EqualTo(0.85f).Within(1e-5f));
            Assert.That(ProjectileBallistics.FalloffScale(bow, 0.8f), Is.EqualTo(0.575f).Within(1e-5f));
            Assert.That(ProjectileBallistics.FalloffScale(bow, 9f), Is.EqualTo(0.45f));
            Assert.That(ProjectileBallistics.FalloffScale(bow, -1f), Is.EqualTo(1f));
            Assert.That(ProjectileBallistics.FalloffScale(bow, float.NaN), Is.EqualTo(1f));
        }

        [Test]
        public void FalloffScale_EmptyTableMeansNoFalloff()
        {
            Assert.That(ProjectileBallistics.FalloffScale(null, 5f), Is.EqualTo(1f));
            Assert.That(ProjectileBallistics.FalloffScale(new ProjectileFalloffKey[0], 5f), Is.EqualTo(1f));
        }

        [Test]
        public void IsValidTable_RejectsDescendingAndNonFiniteKeys()
        {
            Assert.That(ProjectileBallistics.IsValidTable(new[] { new ProjectileFalloffKey(0f, 1f), new ProjectileFalloffKey(1f, 0.5f) }), Is.True);
            Assert.That(ProjectileBallistics.IsValidTable(new[] { new ProjectileFalloffKey(1f, 1f), new ProjectileFalloffKey(0.5f, 0.5f) }), Is.False);
            Assert.That(ProjectileBallistics.IsValidTable(new[] { new ProjectileFalloffKey(0f, float.NaN) }), Is.False);
            Assert.That(ProjectileBallistics.IsValidTable(new[] { new ProjectileFalloffKey(0f, -0.1f) }), Is.False);
        }

        [Test]
        public void ConsumeSteps_OneStepDeltaRunsOneStep()
        {
            float acc = ProjectileBallistics.StepSeconds;
            Assert.That(ProjectileBallistics.ConsumeSteps(ref acc, 600), Is.EqualTo(1));
            Assert.That(acc, Is.EqualTo(0f).Within(1e-6f));
        }

        [Test]
        public void ConsumeSteps_OneSecondIsSixtySteps()
        {
            float acc = 1f;
            Assert.That(ProjectileBallistics.ConsumeSteps(ref acc, 600), Is.EqualTo(60));
        }

        [Test]
        public void ConsumeSteps_CarriesTheRemainderAndIgnoresHowTimeWasChopped()
        {
            float acc = 0f;
            int total = 0;
            for (int i = 0; i < 100; i++)
            {
                acc += 0.01f;
                total += ProjectileBallistics.ConsumeSteps(ref acc, 600);
            }
            Assert.That(total, Is.EqualTo(60));
        }

        [Test]
        public void ConsumeSteps_NaNZeroAndNegativeRunNothingAndTheBacklogIsBounded()
        {
            float acc = float.NaN;
            Assert.That(ProjectileBallistics.ConsumeSteps(ref acc, 600), Is.EqualTo(0));
            Assert.That(acc, Is.EqualTo(0f));
            acc = -3f;
            Assert.That(ProjectileBallistics.ConsumeSteps(ref acc, 600), Is.EqualTo(0));
            acc = 100f;
            Assert.That(ProjectileBallistics.ConsumeSteps(ref acc, 600), Is.EqualTo(600));
            Assert.That(acc, Is.EqualTo(0f));
        }

        [Test]
        public void SolveLowPitch_AimsAtTheTargetAndTheSteppedArrowArrivesThere()
        {
            Assert.That(ProjectileBallistics.SolveLowPitch(Bow, BowGravity, 30f, 0.6f, out float pitch), Is.True);
            Assert.That(pitch, Is.GreaterThan(0f));
            float t = ProjectileBallistics.TimeToHorizontalDistance(Bow, pitch, 30f);
            BallisticState at = ProjectileBallistics.PositionAt(Launch(Bow, pitch), BowGravity, t);
            Assert.That(at.PZ, Is.EqualTo(30f).Within(1e-3f));
            Assert.That(at.PY, Is.EqualTo(0.6f).Within(2e-3f));
        }

        [Test]
        public void SolveLowPitch_OutOfReachIsRefused()
        {
            Assert.That(ProjectileBallistics.SolveLowPitch(10f, 9.81f, 500f, 0f, out _), Is.False);
            Assert.That(ProjectileBallistics.SolveLowPitch(0f, 9.81f, 10f, 0f, out _), Is.False);
        }

        [Test]
        public void SolveLowPitch_WithoutGravityIsTheStraightAngle()
        {
            Assert.That(ProjectileBallistics.SolveLowPitch(40f, 0f, 10f, 10f, out float pitch), Is.True);
            Assert.That(pitch, Is.EqualTo((float)System.Math.PI / 4f).Within(1e-5f));
        }
    }
}
