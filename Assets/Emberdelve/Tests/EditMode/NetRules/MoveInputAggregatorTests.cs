using Emberdelve.Net.Rules;
using NUnit.Framework;

namespace Emberdelve.NetRules.Tests
{
    public sealed class MoveInputAggregatorTests
    {
        [Test]
        public void OneFramePerFlush_SequenceStartsAtOne_AndIncreases()
        {
            MoveInputAggregator a = new MoveInputAggregator();
            Assert.That(a.LastSeq, Is.EqualTo(0u));
            a.Sample(0f, 1f, false, false, false, 10f, 5f);
            MoveInputFrame f1 = a.Flush();
            MoveInputFrame f2 = a.Flush();
            Assert.That(f1.Seq, Is.EqualTo(1u));
            Assert.That(f2.Seq, Is.EqualTo(2u));
            Assert.That(a.LastSeq, Is.EqualTo(2u));
        }

        [Test]
        public void Move_IsTheLastHeldValue_ViewIsCurrent()
        {
            MoveInputAggregator a = new MoveInputAggregator();
            a.Sample(1f, 0f, false, false, false, 0f, 0f);
            a.Sample(0f, 1f, true, false, false, 45f, 10f);
            a.Sample(-1f, 0f, false, false, false, 90f, 20f);
            MoveInputFrame f = a.Flush();

            Assert.That(f.MoveX, Is.EqualTo(-1f));
            Assert.That(f.MoveY, Is.EqualTo(0f));
            Assert.That(f.Sprint, Is.False, "sprint is a level: the last sample wins");
            Assert.That(f.Yaw, Is.EqualTo(90f).Within(0.01f));
            Assert.That(f.Pitch, Is.EqualTo(20f).Within(0.01f));
        }

        [Test]
        public void Edges_AreOrOverSamples_AndClearedByTheFlush()
        {
            MoveInputAggregator a = new MoveInputAggregator();
            a.Sample(0f, 0f, false, true, false, 0f, 0f);
            a.Sample(0f, 0f, false, false, true, 0f, 0f);
            a.Sample(0f, 0f, false, false, false, 0f, 0f);
            MoveInputFrame f1 = a.Flush();
            Assert.That(f1.Jump, Is.True, "a press between ticks is not lost");
            Assert.That(f1.CrouchToggle, Is.True);

            a.Sample(0f, 0f, false, false, false, 0f, 0f);
            MoveInputFrame f2 = a.Flush();
            Assert.That(f2.Jump, Is.False, "and is not doubled");
            Assert.That(f2.CrouchToggle, Is.False);
        }

        [Test]
        public void Frames_AreInTheQuantizedWireForm_AndHonestNaNBecomesZero()
        {
            MoveInputAggregator a = new MoveInputAggregator();
            a.Sample(0.3f, float.NaN, false, false, false, 33.3333f, 12.34f);
            MoveInputFrame f = a.Flush();
            Assert.That(f.MoveX, Is.EqualTo(MoveInputFrame.DequantizeAxis(MoveInputFrame.QuantizeAxis(0.3f))));
            Assert.That(f.MoveY, Is.EqualTo(0f));
            Assert.That(f.IsWellFormed, Is.True);
        }

        [Test]
        public void Batch_HoldsTheLastThree_OldestFirst()
        {
            MoveInputAggregator a = new MoveInputAggregator();
            MoveInputFrame[] batch = new MoveInputFrame[MoveInputAggregator.BatchSize];

            a.Flush();
            Assert.That(a.FillBatch(batch), Is.EqualTo(1));
            Assert.That(batch[0].Seq, Is.EqualTo(1u));

            a.Flush();
            a.Flush();
            a.Flush();
            a.Flush();
            int count = a.FillBatch(batch);
            Assert.That(count, Is.EqualTo(3));
            Assert.That(batch[0].Seq, Is.EqualTo(3u));
            Assert.That(batch[1].Seq, Is.EqualTo(4u));
            Assert.That(batch[2].Seq, Is.EqualTo(5u));
        }

        [Test]
        public void HonestStream_PassesTheSanitizer_EveryFrameOnce()
        {
            MoveInputAggregator a = new MoveInputAggregator();
            InputSanitizer s = new InputSanitizer();
            MoveInputFrame[] batch = new MoveInputFrame[MoveInputAggregator.BatchSize];

            for (int tick = 1; tick <= 120; tick++)
            {
                a.Sample(0.7071f, 0.7071f, tick % 40 == 0, tick % 50 == 0, false, tick * 0.5f, 10f);
                a.Flush();
                int n = a.FillBatch(batch);
                for (int i = 0; i < n; i++) s.Submit(batch[i], i == n - 1, tick / 30.0, out _);
            }

            Assert.That(s.Accepted, Is.EqualTo(120));
            Assert.That(s.DroppedReplay + s.DroppedInvalid + s.DroppedFlood, Is.EqualTo(0));
            Assert.That(s.Clamped, Is.EqualTo(0), "a keyboard diagonal after quantization is noise, not a clamp");
        }
    }
}
