using System;

namespace Emberdelve.Net.Rules
{
    /// <summary>
    /// U3A4b: the numbers the owning client needs to run the same movement step as the server, resolved BY THE SERVER (DEX, gear,
    /// passives, status and flow factors already folded in; the client has none of that data). Plain data: replicated to the owner
    /// only, applied through PlayerMovement.ApplyPredictionParams, never authoritative on the client (a client that changes them
    /// only mispredicts and is corrected).
    /// </summary>
    public struct MoveParamsData : IEquatable<MoveParamsData>
    {
        public float Walk;
        public float Sprint;
        public float Crouch;
        public float StandHeight;
        public float CrouchHeight;
        public float JumpHeight;
        /// <summary>LayerMask value of the ground the grounded check looks for.</summary>
        public int GroundMask;

        public bool IsValid =>
            MoveInputFrame.IsFinite(Walk) && MoveInputFrame.IsFinite(Sprint) && MoveInputFrame.IsFinite(Crouch) &&
            MoveInputFrame.IsFinite(StandHeight) && MoveInputFrame.IsFinite(CrouchHeight) && MoveInputFrame.IsFinite(JumpHeight) &&
            Walk > 0f && StandHeight > 0f && CrouchHeight > 0f;

        public bool Equals(MoveParamsData other)
        {
            return Walk == other.Walk && Sprint == other.Sprint && Crouch == other.Crouch &&
                   StandHeight == other.StandHeight && CrouchHeight == other.CrouchHeight &&
                   JumpHeight == other.JumpHeight && GroundMask == other.GroundMask;
        }

        public override bool Equals(object obj) { return obj is MoveParamsData other && Equals(other); }

        public override int GetHashCode()
        {
            unchecked
            {
                int h = Walk.GetHashCode();
                h = h * 31 + Sprint.GetHashCode();
                h = h * 31 + Crouch.GetHashCode();
                h = h * 31 + StandHeight.GetHashCode();
                h = h * 31 + CrouchHeight.GetHashCode();
                h = h * 31 + JumpHeight.GetHashCode();
                h = h * 31 + GroundMask;
                return h;
            }
        }
    }
}
