using System;
using Unity.Netcode;

namespace Emberdelve.Net
{
    /// <summary>Server-authored countdown sample replicated to each player's owner.</summary>
    public struct RaidClockWire : INetworkSerializable, IEquatable<RaidClockWire>
    {
        public bool Enabled;
        public float RemainingSeconds;
        public bool Expired;
        public byte Floor;
        public bool Waiting;
        public uint MovementGeneration;
        public static RaidClockWire From(Emberdelve.Flow.PlayerRaidClock sample) => new RaidClockWire
        {
            Enabled = sample.Enabled, RemainingSeconds = sample.RemainingSeconds, Expired = sample.Expired,
            Floor = sample.Floor, Waiting = sample.Waiting, MovementGeneration = sample.MovementGeneration
        };

        public bool Warning => Enabled && (Expired || RemainingSeconds <= 60f);

        public bool IsValid => !float.IsNaN(RemainingSeconds) && !float.IsInfinity(RemainingSeconds) && RemainingSeconds >= 0f
            && Floor <= 3 && (!Waiting || (Floor > 0 && !Enabled && !Expired && RemainingSeconds == 0));

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref Enabled);
            serializer.SerializeValue(ref RemainingSeconds);
            serializer.SerializeValue(ref Expired);
            serializer.SerializeValue(ref Floor);
            serializer.SerializeValue(ref Waiting);
            serializer.SerializeValue(ref MovementGeneration);
        }

        public bool Equals(RaidClockWire other) => Enabled == other.Enabled && Expired == other.Expired &&
            Floor == other.Floor && Waiting == other.Waiting && MovementGeneration == other.MovementGeneration &&
            Math.Abs(RemainingSeconds - other.RemainingSeconds) < 0.001f;

        public override bool Equals(object obj) => obj is RaidClockWire other && Equals(other);
        public override int GetHashCode() => (Enabled ? 1 : 0) ^ (Expired ? 2 : 0) ^ RemainingSeconds.GetHashCode();
    }
}
