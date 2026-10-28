using Emberdelve.Net.Rules;
using NUnit.Framework;

namespace Emberdelve.NetRules.Tests
{
    public sealed class ReconcileRulesTests
    {
        [Test]
        public void Thresholds_AreTheSpecifiedOnes()
        {
            Assert.That(ReconcileRules.IgnoreMeters, Is.EqualTo(0.10f));
            Assert.That(ReconcileRules.SnapMeters, Is.EqualTo(1.0f));
            Assert.That(ReconcileRules.BlendSeconds, Is.EqualTo(0.1f).Within(1e-6f));
            Assert.That(ReconcileRules.AckEveryTicks, Is.EqualTo(2));
        }

        [TestCase(0f, ReconcileVerdict.Ignore)]
        [TestCase(0.05f, ReconcileVerdict.Ignore)]
        [TestCase(0.10f, ReconcileVerdict.Ignore)]
        [TestCase(0.1001f, ReconcileVerdict.Blend)]
        [TestCase(0.5f, ReconcileVerdict.Blend)]
        [TestCase(1.0f, ReconcileVerdict.Blend)]
        [TestCase(1.0001f, ReconcileVerdict.Snap)]
        [TestCase(25f, ReconcileVerdict.Snap)]
        public void Decide_UsesTheBands(float error, ReconcileVerdict expected)
        {
            Assert.That(ReconcileRules.Decide(error), Is.EqualTo(expected));
        }

        [Test]
        public void Decide_NonFiniteError_IsASnap()
        {
            Assert.That(ReconcileRules.Decide(float.NaN), Is.EqualTo(ReconcileVerdict.Snap));
            Assert.That(ReconcileRules.Decide(float.PositiveInfinity), Is.EqualTo(ReconcileVerdict.Snap));
        }

        [Test]
        public void Blend_PaysTheWholeOffsetOverAboutOneHundredMilliseconds()
        {
            BlendCorrector blend = new BlendCorrector();
            Assert.That(blend.Active, Is.False);
            blend.Add(0.3f, 0f, -0.6f);
            Assert.That(blend.Active, Is.True);

            float sumX = 0f, sumY = 0f, sumZ = 0f;
            int ticks = 0;
            const float dt = 1f / 30f;
            while (blend.Take(dt, out float sx, out float sy, out float sz) && ticks < 20)
            {
                sumX += sx; sumY += sy; sumZ += sz;
                ticks++;
            }

            Assert.That(sumX, Is.EqualTo(0.3f).Within(1e-5f));
            Assert.That(sumY, Is.EqualTo(0f).Within(1e-5f));
            Assert.That(sumZ, Is.EqualTo(-0.6f).Within(1e-5f));
            Assert.That(ticks, Is.InRange(3, 4), "100 ms at 30 Hz is three ticks (a fourth only from float rounding)");
            Assert.That(blend.Active, Is.False);
            Assert.That(blend.RemainingX, Is.EqualTo(0f));
        }

        [Test]
        public void Blend_IsLinear_FirstShareIsAThird()
        {
            BlendCorrector blend = new BlendCorrector();
            blend.Add(0.9f, 0f, 0f);
            blend.Take(1f / 30f, out float sx, out _, out _);
            Assert.That(sx, Is.EqualTo(0.3f).Within(1e-4f));
            Assert.That(blend.RemainingX, Is.EqualTo(0.6f).Within(1e-4f));
        }

        [Test]
        public void Blend_AddWhileActive_SumsAndRestartsTheClock()
        {
            BlendCorrector blend = new BlendCorrector();
            blend.Add(0.3f, 0f, 0f);
            blend.Take(1f / 30f, out _, out _, out _);
            blend.Add(0.3f, 0f, 0f);

            float total = 0f;
            int guard = 0;
            while (blend.Take(1f / 30f, out float sx, out _, out _) && guard++ < 20) total += sx;
            Assert.That(total, Is.EqualTo(0.5f).Within(1e-4f), "0.2 still owed + 0.3 new");
        }

        [Test]
        public void Blend_TakeWithNothingOwed_ReturnsFalse_AndClearDropsTheDebt()
        {
            BlendCorrector blend = new BlendCorrector();
            Assert.That(blend.Take(0.033f, out float sx, out float sy, out float sz), Is.False);
            Assert.That(sx + sy + sz, Is.EqualTo(0f));

            blend.Add(1f, 1f, 1f);
            blend.Clear();
            Assert.That(blend.Active, Is.False);
            Assert.That(blend.Take(0.033f, out _, out _, out _), Is.False);
        }

        [Test]
        public void Stats_MaxMeanAndLogLine()
        {
            ReconcileStats stats = new ReconcileStats();
            Assert.That(stats.MeanError, Is.EqualTo(0f));
            stats.RecordAck(0.02f);
            stats.RecordAck(0.06f);
            stats.RecordAck(0.01f);
            stats.Blends = 1;
            stats.Snaps = 2;
            stats.Replays = 2;

            Assert.That(stats.Acks, Is.EqualTo(3));
            Assert.That(stats.MaxError, Is.EqualTo(0.06f).Within(1e-6f));
            Assert.That(stats.MeanError, Is.EqualTo(0.03f).Within(1e-6f));

            string line = stats.ToLogLine("final");
            Assert.That(line, Does.StartWith("reconcile acks=3 maxErr=0.060 meanErr=0.030 blends=1 snaps=2 replays=2"));
            Assert.That(line, Does.EndWith(" at=final"));
            Assert.That(stats.ToLogLine(null), Does.Not.Contain(" at="));
        }
    }
}
