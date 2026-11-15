using System;
using Emberdelve.Flow;
using Emberdelve.Player;
using Unity.Netcode;
using UnityEngine;

namespace Emberdelve.Net
{
    /// <summary>Authority sample for one owner's floor. Presentation never applies zone damage.</summary>
    public struct RoyaleZoneWire : INetworkSerializable, IEquatable<RoyaleZoneWire>
    {
        public byte Floor;
        public uint Generation;
        public bool Active, Shrinking, HasFinalExit;
        public Vector3 ExitPosition;
        public float ExitHoldDuration;
        public float X, Z, Radius, DamagePerSecond, SecondsToBoundary;
        public bool IsValid => (!HasFinalExit || (Floor == 3 && float.IsFinite(ExitHoldDuration) && ExitHoldDuration > 0 && ExitHoldDuration <= 120
            && float.IsFinite(ExitPosition.x) && Mathf.Abs(ExitPosition.x) <= 100000
            && float.IsFinite(ExitPosition.y) && Mathf.Abs(ExitPosition.y) <= 10000
            && float.IsFinite(ExitPosition.z) && Mathf.Abs(ExitPosition.z) <= 100000)) && Floor <= 3 && (!Active || Floor > 0)
            && float.IsFinite(X) && Mathf.Abs(X) <= 100000
            && float.IsFinite(Z) && Mathf.Abs(Z) <= 100000
            && float.IsFinite(Radius) && Radius >= 0 && Radius <= 100000
            && float.IsFinite(DamagePerSecond) && DamagePerSecond >= 0 && DamagePerSecond <= 10000
            && float.IsFinite(SecondsToBoundary) && SecondsToBoundary >= 0 && SecondsToBoundary <= 86400;
        public bool VisibleFor(uint generation, bool inPlay, bool dead) => IsValid && Active && inPlay && !dead && Generation == generation;
        public float DistanceOutside(Vector3 position) => Mathf.Max(0, Vector2.Distance(new Vector2(position.x, position.z), new Vector2(X, Z)) - Radius);
        public static RoyaleZoneWire Capture(RunDirector director, PlayerRig actor)
        {
            var session = director?.SessionOf(actor);
            if (session == null || actor == null || !director.RoyaleMode.HasValue) return default;
            var encounter = director.FloorCoordinator.EncounterFor(session.Floor);
            if (encounter == null || !encounter.TrySample(out var sample)) return default;
            return new RoyaleZoneWire { Floor = (byte)session.Floor, Generation = actor.FloorMovementGeneration,
                Active = director.IsRoyaleFloorOpen(session.Floor) && actor.IsInPlay && !actor.IsDead,
                X = (float)sample.X, Z = (float)sample.Z, Radius = (float)sample.Radius,
                DamagePerSecond = (float)sample.DamagePerSecond, SecondsToBoundary = (float)sample.SecondsToBoundary,
                Shrinking = sample.Shrinking, HasFinalExit = encounter.FinalExit != null,
                ExitPosition = encounter.FinalExit != null ? encounter.FinalExit.transform.position : Vector3.zero,
                ExitHoldDuration = encounter.FinalExit != null ? encounter.FinalExit.HoldDuration : 0 };
        }
        public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
        {
            s.SerializeValue(ref Floor); s.SerializeValue(ref Generation); s.SerializeValue(ref Active); s.SerializeValue(ref Shrinking);
            s.SerializeValue(ref HasFinalExit); s.SerializeValue(ref ExitPosition); s.SerializeValue(ref ExitHoldDuration);
            s.SerializeValue(ref X); s.SerializeValue(ref Z); s.SerializeValue(ref Radius);
            s.SerializeValue(ref DamagePerSecond); s.SerializeValue(ref SecondsToBoundary);
        }
        public bool Equals(RoyaleZoneWire o) => Floor == o.Floor && Generation == o.Generation && Active == o.Active
            && HasFinalExit == o.HasFinalExit && ExitPosition.Equals(o.ExitPosition) && ExitHoldDuration.Equals(o.ExitHoldDuration)
            && Shrinking == o.Shrinking && X.Equals(o.X) && Z.Equals(o.Z) && Radius.Equals(o.Radius)
            && DamagePerSecond.Equals(o.DamagePerSecond) && SecondsToBoundary.Equals(o.SecondsToBoundary);
        public override bool Equals(object o) => o is RoyaleZoneWire other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(Floor, Generation, Active, X, Z, Radius);
    }
}
