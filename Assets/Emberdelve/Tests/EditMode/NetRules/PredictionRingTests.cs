using Emberdelve.Net.Rules;
using NUnit.Framework;

namespace Emberdelve.NetRules.Tests
{
    public sealed class PredictionRingTests
    {
        private static PredictionEntry Entry(uint seq, float x)
        {
            return new PredictionEntry { Seq = seq, State = new PredictedState { X = x } };
        }

        [Test]
        public void Push_KeepsOldestFirst_AndIndexOfFindsBySequence()
        {
            PredictionRing ring = new PredictionRing(8);
            for (uint s = 1; s <= 5; s++) ring.Push(Entry(s, s));

            Assert.That(ring.Count, Is.EqualTo(5));
            Assert.That(ring[0].Seq, Is.EqualTo(1u));
            Assert.That(ring[4].Seq, Is.EqualTo(5u));
            Assert.That(ring.IndexOf(3u), Is.EqualTo(2));
            Assert.That(ring.IndexOf(99u), Is.EqualTo(-1));
        }

        [Test]
        public void Full_OverwritesTheOldest_AndCountsIt()
        {
            PredictionRing ring = new PredictionRing(4);
            for (uint s = 1; s <= 6; s++) ring.Push(Entry(s, s));

            Assert.That(ring.Count, Is.EqualTo(4));
            Assert.That(ring.Overwritten, Is.EqualTo(2));
            Assert.That(ring[0].Seq, Is.EqualTo(3u));
            Assert.That(ring[3].Seq, Is.EqualTo(6u));
            Assert.That(ring.IndexOf(1u), Is.EqualTo(-1));
        }

        [Test]
        public void DiscardThrough_DropsTheNamedEntryAndEveryOlderOne()
        {
            PredictionRing ring = new PredictionRing(8);
            for (uint s = 1; s <= 6; s++) ring.Push(Entry(s, s));

            ring.DiscardThrough(ring.IndexOf(3u));
            Assert.That(ring.Count, Is.EqualTo(3));
            Assert.That(ring[0].Seq, Is.EqualTo(4u));

            ring.DiscardThrough(-1);
            Assert.That(ring.Count, Is.EqualTo(3), "a negative index discards nothing");

            ring.DiscardThrough(50);
            Assert.That(ring.Count, Is.EqualTo(0), "an index past the end discards everything");
        }

        [Test]
        public void WrapAround_StillIndexesCorrectly()
        {
            PredictionRing ring = new PredictionRing(4);
            for (uint s = 1; s <= 3; s++) ring.Push(Entry(s, s));
            ring.DiscardThrough(1); // keeps 3
            for (uint s = 4; s <= 6; s++) ring.Push(Entry(s, s));

            Assert.That(ring.Count, Is.EqualTo(4));
            Assert.That(ring[0].Seq, Is.EqualTo(3u));
            Assert.That(ring[3].Seq, Is.EqualTo(6u));
            Assert.That(ring.IndexOf(5u), Is.EqualTo(2));
        }

        [Test]
        public void TranslateAfter_ShiftsOnlyNewerEntries()
        {
            PredictionRing ring = new PredictionRing(8);
            for (uint s = 1; s <= 4; s++) ring.Push(Entry(s, 10f * s));

            ring.TranslateAfter(1, 0.5f, 1f, -1f);
            Assert.That(ring[0].State.X, Is.EqualTo(10f));
            Assert.That(ring[1].State.X, Is.EqualTo(20f));
            Assert.That(ring[2].State.X, Is.EqualTo(30.5f).Within(1e-5f));
            Assert.That(ring[3].State.X, Is.EqualTo(40.5f).Within(1e-5f));
            Assert.That(ring[3].State.Y, Is.EqualTo(1f).Within(1e-5f));
            Assert.That(ring[3].State.Z, Is.EqualTo(-1f).Within(1e-5f));
        }

        [Test]
        public void Set_ReplacesAnEntryInPlace_AndIgnoresBadIndexes()
        {
            PredictionRing ring = new PredictionRing(4);
            ring.Push(Entry(1, 1f));
            ring.Push(Entry(2, 2f));

            ring.Set(1, Entry(2, 9f));
            ring.Set(5, Entry(7, 7f));
            ring.Set(-1, Entry(7, 7f));

            Assert.That(ring.Count, Is.EqualTo(2));
            Assert.That(ring[1].State.X, Is.EqualTo(9f));
        }

        [Test]
        public void Clear_EmptiesTheRing()
        {
            PredictionRing ring = new PredictionRing(4);
            ring.Push(Entry(1, 1f));
            ring.Clear();
            Assert.That(ring.Count, Is.EqualTo(0));
            Assert.That(ring.IndexOf(1u), Is.EqualTo(-1));
        }
    }
}
