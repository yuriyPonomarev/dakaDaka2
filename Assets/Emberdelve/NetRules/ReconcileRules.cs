using System;
using System.Globalization;

namespace Emberdelve.Net.Rules
{
    public enum ReconcileVerdict
    {
        /// <summary>The Ack names a sequence the client has no entry for (it predates prediction, or the ring lost it): nothing to compare.</summary>
        Missing = 0,
        /// <summary>The Ack is not newer than one already handled (unreliable delivery reordered it).</summary>
        Stale = 1,
        /// <summary>Prediction and server agree within the dead zone: do nothing.</summary>
        Ignore = 2,
        /// <summary>Small disagreement: pull the predicted position to the server's over <see cref="ReconcileRules.BlendSeconds"/>.</summary>
        Blend = 3,
        /// <summary>Large disagreement: teleport to the server state and replay the unacknowledged frames.</summary>
        Snap = 4
    }

    /// <summary>Numbers of the owner reconciliation (plan section 2.3).</summary>
    public static class ReconcileRules
    {
        /// <summary>Error up to and including this is ignored.</summary>
        public const float IgnoreMeters = 0.10f;
        /// <summary>Error above <see cref="IgnoreMeters"/> up to and including this is blended; above it is a snap.</summary>
        public const float SnapMeters = 1.0f;
        public const float BlendSeconds = 0.1f;
        /// <summary>The server sends an Ack every this many network ticks.</summary>
        public const int AckEveryTicks = 2;

        public static ReconcileVerdict Decide(float errorMeters)
        {
            if (float.IsNaN(errorMeters) || float.IsInfinity(errorMeters)) return ReconcileVerdict.Snap;
            if (errorMeters <= IgnoreMeters) return ReconcileVerdict.Ignore;
            if (errorMeters <= SnapMeters) return ReconcileVerdict.Blend;
            return ReconcileVerdict.Snap;
        }
    }

    /// <summary>
    /// Spreads a position correction over <see cref="ReconcileRules.BlendSeconds"/>: <see cref="Add"/> sets the offset still owed
    /// and restarts the clock, each tick <see cref="Take"/> hands out a linear share of it (the whole remainder on the last one).
    /// </summary>
    public sealed class BlendCorrector
    {
        private float _x;
        private float _y;
        private float _z;
        private float _timeLeft;

        public bool Active => _timeLeft > 0f;
        public float RemainingX => _x;
        public float RemainingY => _y;
        public float RemainingZ => _z;

        public void Add(float dx, float dy, float dz)
        {
            _x += dx;
            _y += dy;
            _z += dz;
            _timeLeft = ReconcileRules.BlendSeconds;
        }

        /// <summary>The part of the owed offset to apply for a step of <paramref name="dt"/> seconds; false when nothing is owed.</summary>
        public bool Take(float dt, out float sx, out float sy, out float sz)
        {
            sx = 0f;
            sy = 0f;
            sz = 0f;
            if (_timeLeft <= 0f) return false;

            float fraction = (dt <= 0f) ? 0f : (dt >= _timeLeft - 0.0001f ? 1f : dt / _timeLeft);
            sx = _x * fraction;
            sy = _y * fraction;
            sz = _z * fraction;
            _x -= sx;
            _y -= sy;
            _z -= sz;
            _timeLeft -= dt;
            if (fraction >= 1f || _timeLeft <= 0f)
            {
                sx += _x;
                sy += _y;
                sz += _z;
                Clear();
            }

            return true;
        }

        public void Clear()
        {
            _x = 0f;
            _y = 0f;
            _z = 0f;
            _timeLeft = 0f;
        }
    }

    /// <summary>Non-mutating cumulative snapshot for measuring a reconciliation interval.</summary>
    public readonly struct ReconcileStatsSnapshot
    {
        public readonly int Acks, Snaps, Replays, Fights;
        public readonly double SumError;

        public ReconcileStatsSnapshot(int acks, double sumError, int snaps, int replays, int fights)
        {
            Acks = acks; SumError = sumError; Snaps = snaps; Replays = replays; Fights = fights;
        }

        /// <summary>Returns false for an empty interval or a counter reset, never silently accepting missing samples.</summary>
        public bool TryInterval(ReconcileStatsSnapshot end, out double meanError, out int acks,
            out int snaps, out int replays, out int fights)
        {
            meanError = 0d;
            acks = end.Acks - Acks; snaps = end.Snaps - Snaps;
            replays = end.Replays - Replays; fights = end.Fights - Fights;
            double sum = end.SumError - SumError;
            if (acks <= 0 || snaps < 0 || replays < 0 || fights < 0 ||
                double.IsNaN(sum) || double.IsInfinity(sum) || sum < 0d) return false;
            meanError = sum / acks;
            return true;
        }
    }

    /// <summary>What the owner reports about its own prediction (the line the gate reads).</summary>
    public sealed class ReconcileStats
    {
        /// <summary>Acks that were compared with a stored prediction.</summary>
        public int Acks { get; private set; }
        public float MaxError { get; private set; }
        public double SumError { get; private set; }
        public int Blends;
        public int Snaps;
        public int Replays;
        public int ReplayedFrames;
        public int Missing;
        public int Stale;
        /// <summary>Times something else (the transform sync) moved the owner between two ticks and the predictor put it back.</summary>
        public int Fights;

        public float MeanError => Acks > 0 ? (float)(SumError / Acks) : 0f;

        public ReconcileStatsSnapshot Capture() => new ReconcileStatsSnapshot(Acks, SumError, Snaps, Replays, Fights);

        public void RecordAck(float error)
        {
            Acks++;
            if (float.IsNaN(error) || float.IsInfinity(error)) error = 1000f;
            if (error > MaxError) MaxError = error;
            SumError += error;
        }

        public string ToLogLine(string tag)
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            return "reconcile acks=" + Acks + " maxErr=" + MaxError.ToString("F3", c) + " meanErr=" + MeanError.ToString("F3", c) +
                   " blends=" + Blends + " snaps=" + Snaps + " replays=" + Replays +
                   " replayFrames=" + ReplayedFrames + " missing=" + Missing + " stale=" + Stale + " fights=" + Fights +
                   (string.IsNullOrEmpty(tag) ? "" : " at=" + tag);
        }
    }

    /// <summary>What <see cref="PredictionReconciler.OnAck"/> decided; the engine layer carries it out.</summary>
    public struct ReconcileOutcome
    {
        public ReconcileVerdict Verdict;
        public float Error;
        /// <summary>Snap only: the server state to teleport to. The entries still in the ring are the unacknowledged frames to replay.</summary>
        public PredictedState Server;
    }

    /// <summary>
    /// U3A4b: the pure core of the owner's prediction loop. The engine layer calls <see cref="Record"/> after stepping each frame
    /// it sends and <see cref="OnAck"/> when the server's Ack arrives; this class owns the ring, the stats and the blend offset, and
    /// tells the caller what to do (nothing, wait for the blend, or teleport and replay what is left in <see cref="Ring"/>).
    /// A Blend is applied logically at once: the unacked entries are shifted by the correction (the same trajectory, translated),
    /// and the engine layer feeds the correction to the body over <see cref="BlendCorrector"/> ticks.
    /// </summary>
    public sealed class PredictionReconciler
    {
        public readonly PredictionRing Ring = new PredictionRing();
        public readonly ReconcileStats Stats = new ReconcileStats();
        public readonly BlendCorrector Blend = new BlendCorrector();

        public uint LastAckedSeq { get; private set; }

        /// <summary>Start a new authority-issued sequence window; discard pre-teleport corrections and predictions.</summary>
        public void ResetForTeleport()
        {
            Ring.Clear(); Blend.Clear(); LastAckedSeq = 0;
        }

        public void Record(in PredictionEntry entry)
        {
            Ring.Push(in entry);
        }

        public ReconcileOutcome OnAck(uint seq, in PredictedState server)
        {
            ReconcileOutcome outcome = new ReconcileOutcome { Server = server };

            if (seq <= LastAckedSeq)
            {
                Stats.Stale++;
                outcome.Verdict = ReconcileVerdict.Stale;
                return outcome;
            }

            int index = Ring.IndexOf(seq);
            if (index < 0)
            {
                // Not a frame we predicted (before prediction started, a flood frame, or lost from the ring). Everything older than
                // the Ack is processed anyway, so the entries behind it go.
                Stats.Missing++;
                LastAckedSeq = seq;
                int older = 0;
                while (older < Ring.Count && Ring[older].Seq < seq) older++;
                Ring.DiscardThrough(older - 1);
                outcome.Verdict = ReconcileVerdict.Missing;
                return outcome;
            }

            PredictionEntry entry = Ring[index];
            float error = server.IsFinite ? entry.State.DistanceTo(in server) : float.NaN;
            Stats.RecordAck(error);
            LastAckedSeq = seq;
            outcome.Error = error;
            outcome.Verdict = ReconcileRules.Decide(error);

            switch (outcome.Verdict)
            {
                case ReconcileVerdict.Blend:
                    {
                        float dx = server.X - entry.State.X;
                        float dy = server.Y - entry.State.Y;
                        float dz = server.Z - entry.State.Z;
                        Blend.Add(dx, dy, dz);
                        Ring.TranslateAfter(index, dx, dy, dz);
                        Stats.Blends++;
                    }

                    break;

                case ReconcileVerdict.Snap:
                    Blend.Clear();
                    Stats.Snaps++;
                    Stats.Replays++;
                    Stats.ReplayedFrames += Ring.Count - (index + 1);
                    break;
            }

            Ring.DiscardThrough(index);
            return outcome;
        }
    }

    /// <summary>The frame as the server will step it, so the predicting client runs the same numbers.</summary>
    public static class PredictionRules
    {
        /// <summary>
        /// Quantized axes and angles, flags masked, then the same |move| scaling the server's InputSanitizer applies to an accepted
        /// frame (a length above 1 is divided down to 1), yaw wrapped, pitch clamped. The expression order mirrors
        /// <see cref="InputSanitizer.Submit"/> on purpose; the tests compare the two.
        /// </summary>
        public static MoveInputFrame ServerView(in MoveInputFrame frame)
        {
            MoveInputFrame q = frame.Quantized();
            float moveX = q.MoveX;
            float moveY = q.MoveY;
            float magnitudeSquared = moveX * moveX + moveY * moveY;
            if (magnitudeSquared > 1f)
            {
                float magnitude = (float)Math.Sqrt(magnitudeSquared);
                moveX /= magnitude;
                moveY /= magnitude;
            }

            return new MoveInputFrame
            {
                Seq = q.Seq,
                MoveX = moveX,
                MoveY = moveY,
                Yaw = MoveInputFrame.WrapDegrees(q.Yaw),
                Pitch = MoveInputFrame.ClampPitch(q.Pitch),
                Flags = q.Flags
            };
        }
    }
}
