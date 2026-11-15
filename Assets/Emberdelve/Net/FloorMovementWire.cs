using System;
using Unity.Netcode;
using UnityEngine;

namespace Emberdelve.Net
{
    /// <summary>Reliable server-owned transition snapshot, including the owner prediction origin.</summary>
    public struct FloorMovementWire : INetworkSerializable, IEquatable<FloorMovementWire>
    {
        public uint Generation;
        public bool InTransit;
        public Vector3 Position;
        public Quaternion Rotation;
        public bool IsValid => Finite(Position.x) && Finite(Position.y) && Finite(Position.z)
            && Finite(Rotation.x) && Finite(Rotation.y) && Finite(Rotation.z) && Finite(Rotation.w)
            && Quaternion.Dot(Rotation, Rotation) > .000001f;
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref Generation); serializer.SerializeValue(ref InTransit);
            serializer.SerializeValue(ref Position); serializer.SerializeValue(ref Rotation);
        }
        public bool Equals(FloorMovementWire other) => Generation == other.Generation && InTransit == other.InTransit
            && Position.Equals(other.Position) && Rotation.Equals(other.Rotation);
    }
}
