using Emberdelve.Net.Rules;
using NUnit.Framework;

namespace Emberdelve.NetRules.Tests
{
    public sealed class PredictionReconcilerTests
    {
        private static PredictionEntry Entry(uint seq, float x)
        {
            return new PredictionEntry { Seq = seq, State = new PredictedState { X = x, CapsuleHeight = 1.8f } };
        }

        private static PredictedState Server(float x)
        {
            return new PredictedState { X = x, CapsuleHeight = 1.8f };
        }

        private static PredictionReconciler Filled(int count)
        {
            PredictionReconciler r = new PredictionReconciler();
            for (int i = 1; i <= count; i++) r.Record(Entry((uint)i, i * 0.2f));
            return r;
        }

        [Test]
        public void HonestAck_WithinTheDeadZone_IsIgnored_AndTrimsTheRing()
        {
            PredictionReconciler r = Filled(6);
            ReconcileOutcome o = r.OnAck(3, Server(3 * 0.2f + 0.04f));

            Assert.That(o.Verdict, Is.EqualTo(ReconcileVerdict.Ignore));
            Assert.That(o.Error, Is.EqualTo(0.04f).Within(1e-5f));
            Assert.That(r.Stats.Acks, Is.EqualTo(1));
            Assert.That(r.Stats.Blends + r.Stats.Snaps, Is.EqualTo(0));
            Assert.That(r.Ring.Count, Is.EqualTo(3), "entries 1..3 are processed by the server");
            Assert.That(r.Ring[0].Seq, Is.EqualTo(4u));
            Assert.That(r.Blend.Active, Is.False);
            Assert.That(r.LastAckedSeq, Is.EqualTo(3u));
        }

        [Test]
        public void MediumError_Blends_AndShiftsTheUnackedEntries()
        {
            PredictionReconciler r = Filled(6);
            float predicted = 3 * 0.2f;
            ReconcileOutcome o = r.OnAck(3, Server(predicted + 0.4f));

            Assert.That(o.Verdict, Is.EqualTo(ReconcileVerdict.Blend));
            Assert.That(r.Stats.Blends, Is.EqualTo(1));
            Assert.That(r.Stats.Snaps, Is.EqualTo(0));
            Assert.That(r.Blend.Active, Is.True);
            Assert.That(r.Blend.RemainingX, Is.EqualTo(0.4f).Within(1e-5f));
            Assert.That(r.Ring[0].State.X, Is.EqualTo(4 * 0.2f + 0.4f).Within(1e-5f), "the unacked trajectory is the same one, translated");
            Assert.That(r.Ring[2].State.X, Is.EqualTo(6 * 0.2f + 0.4f).Within(1e-5f));

            // The next Ack of the corrected trajectory now agrees: nothing more to do.
            ReconcileOutcome next = r.OnAck(5, Server(5 * 0.2f + 0.4f));
            Assert.That(next.Verdict, Is.EqualTo(ReconcileVerdict.Ignore));
        }

        [Test]
        public void LargeError_Snaps_CountsTheReplay_AndLeavesOnlyUnackedFramesInTheRing()
        {
            PredictionReconciler r = Filled(8);
            r.Blend.Add(0.2f, 0f, 0f); // an owed blend is dropped by a snap
            ReconcileOutcome o = r.OnAck(4, Server(4 * 0.2f + 2.0f));

            Assert.That(o.Verdict, Is.EqualTo(ReconcileVerdict.Snap));
            Assert.That(o.Server.X, Is.EqualTo(4 * 0.2f + 2.0f).Within(1e-5f));
            Assert.That(r.Stats.Snaps, Is.EqualTo(1));
            Assert.That(r.Stats.Replays, Is.EqualTo(1));
            Assert.That(r.Stats.ReplayedFrames, Is.EqualTo(4));
            Assert.That(r.Blend.Active, Is.False);
            Assert.That(r.Ring.Count, Is.EqualTo(4));
            Assert.That(r.Ring[0].Seq, Is.EqualTo(5u), "the frames to replay start right after the acked one");
            Assert.That(r.Ring[3].Seq, Is.EqualTo(8u));
        }

        [Test]
        public void UnknownSequence_IsMissing_AndCountsNoError()
        {
            PredictionReconciler r = Filled(4);
            ReconcileOutcome o = r.OnAck(50, Server(999f));

            Assert.That(o.Verdict, Is.EqualTo(ReconcileVerdict.Missing));
            Assert.That(r.Stats.Missing, Is.EqualTo(1));
            Assert.That(r.Stats.Acks, Is.EqualTo(0));
            Assert.That(r.Stats.MaxError, Is.EqualTo(0f));
            Assert.That(r.Ring.Count, Is.EqualTo(0), "everything older than the Ack is processed");
        }

        [Test]
        public void UnknownSequence_KeepsNewerEntries()
        {
            PredictionReconciler r = new PredictionReconciler();
            r.Record(Entry(10, 1f));
            r.Record(Entry(12, 2f)); // 11 was a frame the client did not predict (a flood extra)
            ReconcileOutcome o = r.OnAck(11, Server(0f));

            Assert.That(o.Verdict, Is.EqualTo(ReconcileVerdict.Missing));
            Assert.That(r.Ring.Count, Is.EqualTo(1));
            Assert.That(r.Ring[0].Seq, Is.EqualTo(12u));
        }

        [Test]
        public void StaleOrDuplicateAck_IsIgnored()
        {
            PredictionReconciler r = Filled(6);
            r.OnAck(4, Server(4 * 0.2f));
            ReconcileOutcome again = r.OnAck(4, Server(100f));
            ReconcileOutcome older = r.OnAck(2, Server(100f));

            Assert.That(again.Verdict, Is.EqualTo(ReconcileVerdict.Stale));
            Assert.That(older.Verdict, Is.EqualTo(ReconcileVerdict.Stale));
            Assert.That(r.Stats.Stale, Is.EqualTo(2));
            Assert.That(r.Stats.Acks, Is.EqualTo(1));
            Assert.That(r.Stats.Snaps, Is.EqualTo(0));
        }

        [Test]
        public void NonFiniteServerState_IsASnapNotACrash()
        {
            PredictionReconciler r = Filled(3);
            PredictedState bad = Server(float.NaN);
            ReconcileOutcome o = r.OnAck(2, bad);
            Assert.That(o.Verdict, Is.EqualTo(ReconcileVerdict.Snap));
            Assert.That(r.Stats.MaxError, Is.EqualTo(1000f));
        }

        [Test]
        public void HonestStream_ManyAcks_NoSnaps_MeanErrorStaysTiny()
        {
            PredictionReconciler r = new PredictionReconciler();
            uint seq = 0;
            for (int tick = 0; tick < 300; tick++)
            {
                seq++;
                r.Record(Entry(seq, seq * 0.17f));
                if (tick % 2 == 1 && seq > 3) r.OnAck(seq - 3, Server((seq - 3) * 0.17f + 0.001f));
            }

            Assert.That(r.Stats.Snaps, Is.EqualTo(0));
            Assert.That(r.Stats.Blends, Is.EqualTo(0));
            Assert.That(r.Stats.Acks, Is.GreaterThan(100));
            Assert.That(r.Stats.MeanError, Is.LessThan(0.05f));
            Assert.That(r.Ring.Count, Is.LessThan(8), "acked entries are discarded as the Acks arrive");
        }

        [Test]
        public void ServerView_MatchesWhatTheSanitizerHandsTheSimulation()
        {
            float[] axes = { 0f, 1f, -1f, 0.7071f, -0.7071f, 0.5f, 0.31f, 1f / 3f };
            InputSanitizer sanitizer = new InputSanitizer(-85f, 85f, 1000000.0, 1000000.0);
            uint seq = 0;
            for (int i = 0; i < axes.Length; i++)
            {
                for (int j = 0; j < axes.Length; j++)
                {
                    seq++;
                    MoveInputFrame raw = MoveInputFrame.Create(seq, axes[i], axes[j], 37.3f * i - 120f, 12f * j - 40f, (i + j) % 2 == 0, j == 1, i == 2).Quantized();
                    MoveInputFrame predicted = PredictionRules.ServerView(raw);
                    SanitizeVerdict verdict = sanitizer.Submit(raw, true, 1.0, out MoveInputFrame server);

                    Assert.That(verdict, Is.EqualTo(SanitizeVerdict.Accepted));
                    Assert.That(predicted.MoveX, Is.EqualTo(server.MoveX), "x " + axes[i] + "," + axes[j]);
                    Assert.That(predicted.MoveY, Is.EqualTo(server.MoveY), "y " + axes[i] + "," + axes[j]);
                    Assert.That(predicted.Yaw, Is.EqualTo(server.Yaw));
                    Assert.That(predicted.Pitch, Is.EqualTo(server.Pitch));
                    Assert.That(predicted.Flags, Is.EqualTo(server.Flags));
                    Assert.That(predicted.Seq, Is.EqualTo(server.Seq));
                }
            }
        }

        [Test]
        public void ServerView_QuantizesLikeTheWire()
        {
            // Inside the unit circle: ServerView adds nothing beyond the wire quantization.
            MoveInputFrame raw = MoveInputFrame.Create(5, 0.3333f, 0.6f, 12.3456f, 80f, true, false, false);
            MoveInputFrame view = PredictionRules.ServerView(raw);
            MoveInputFrame wire = raw.Quantized();
            Assert.That(view.MoveX, Is.EqualTo(wire.MoveX).Within(1e-6f));
            Assert.That(view.MoveY, Is.EqualTo(wire.MoveY).Within(1e-6f));
            Assert.That(view.Yaw, Is.EqualTo(wire.Yaw));
            Assert.That(view.Flags, Is.EqualTo(wire.Flags));

            // Outside it (0.3333, 0.9999 quantize to 42/127, 127/127: length > 1): the server, like ServerView, divides
            // the quantized pair down to length 1 after the wire, so the expectation is the NORMALIZED quantized pair.
            // (This was the failing expectation: it compared against the un-normalized wire value.)
            MoveInputFrame rawLong = MoveInputFrame.Create(6, 0.3333f, 0.9999f, 12.3456f, 80f, true, false, false);
            MoveInputFrame viewLong = PredictionRules.ServerView(rawLong);
            MoveInputFrame wireLong = rawLong.Quantized();
            float len = (float)System.Math.Sqrt(wireLong.MoveX * wireLong.MoveX + wireLong.MoveY * wireLong.MoveY);
            Assert.That(len, Is.GreaterThan(1f));
            Assert.That(viewLong.MoveX, Is.EqualTo(wireLong.MoveX / len).Within(1e-6f));
            Assert.That(viewLong.MoveY, Is.EqualTo(wireLong.MoveY / len).Within(1e-6f));
        }
    }
}
