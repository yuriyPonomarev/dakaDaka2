using System;

namespace Emberdelve.Net.Rules
{
    /// <summary>
    /// U3A4b: everything the movement step carries from one tick to the next, as plain numbers (no engine types, so it lives in
    /// the pure assembly). Position plus the internal state PlayerMovement.Step reads: vertical velocity, the planar velocity the
    /// acceleration ramp seeds from, the jump cooldown, the capsule height and the crouch toggle. The server sends one in every
    /// Ack; the predicting client stores one per predicted frame and restores one when it has to snap and replay.
    /// </summary>
    public struct PredictedState
    {
        public float X;
        public float Y;
        public float Z;
        public float VerticalVelocity;
        public float PlanarVx;
        public float PlanarVz;
        public float JumpCooldown;
        public float CapsuleHeight;
        public bool Crouched;

        /// <summary>Straight-line distance between the two positions (NaN when either is not finite).</summary>
        public float DistanceTo(in PredictedState other)
        {
            float dx = X - other.X;
            float dy = Y - other.Y;
            float dz = Z - other.Z;
            return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        public bool IsFinite =>
            MoveInputFrame.IsFinite(X) && MoveInputFrame.IsFinite(Y) && MoveInputFrame.IsFinite(Z) &&
            MoveInputFrame.IsFinite(VerticalVelocity) && MoveInputFrame.IsFinite(PlanarVx) && MoveInputFrame.IsFinite(PlanarVz) &&
            MoveInputFrame.IsFinite(JumpCooldown) && MoveInputFrame.IsFinite(CapsuleHeight);
    }

    /// <summary>One predicted frame: the input the client stepped (as the server sees it) and the state it ended in.</summary>
    public struct PredictionEntry
    {
        public uint Seq;
        public MoveInputFrame Frame;
        public PredictedState State;
    }
}
