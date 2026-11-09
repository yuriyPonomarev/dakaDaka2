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

        public bool Warning => Enabled && (Expired || RemainingSeconds <= 60f);

        public bool IsValid => !float.IsNaN(RemainingSeconds) && !float.IsInfinity(RemainingSeconds) && RemainingSeconds >= 0f;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref Enabled);
            serializer.SerializeValue(ref RemainingSeconds);
            serializer.SerializeValue(ref Expired);
        }

        public bool Equals(RaidClockWire other) => Enabled == other.Enabled && Expired == other.Expired &&
            Math.Abs(RemainingSeconds - other.RemainingSeconds) < 0.001f;

        public override bool Equals(object obj) => obj is RaidClockWire other && Equals(other);
        public override int GetHashCode() => (Enabled ? 1 : 0) ^ (Expired ? 2 : 0) ^ RemainingSeconds.GetHashCode();
    }
}
