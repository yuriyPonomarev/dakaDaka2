using System;
using Emberdelve.Net.Rules;
using Unity.Netcode;

namespace Emberdelve.Net
{
    /// <summary>
    /// One movement input frame on the wire (21 bytes). The floats are the client's quantized values when the client is honest
    /// (MoveInputFrame.Quantized), but the format deliberately carries full floats and the full flag byte: the SERVER does not
    /// trust the encoder, so NaN, huge axes and unknown flag bits must be representable to be rejected and counted by
    /// <see cref="InputSanitizer"/>. There is no player, combat or party id in here (or anywhere on the movement RPCs).
    /// </summary>
    public struct MoveFrameWire : INetworkSerializable
    {
        public uint Seq;
        public float MoveX;
        public float MoveY;
        public float Yaw;
        public float Pitch;
        public byte Flags;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref Seq);
            serializer.SerializeValue(ref MoveX);
            serializer.SerializeValue(ref MoveY);
            serializer.SerializeValue(ref Yaw);
            serializer.SerializeValue(ref Pitch);
            serializer.SerializeValue(ref Flags);
        }

        public static MoveFrameWire From(in MoveInputFrame frame)
        {
            return new MoveFrameWire
            {
                Seq = frame.Seq,
                MoveX = frame.MoveX,
                MoveY = frame.MoveY,
                Yaw = frame.Yaw,
                Pitch = frame.Pitch,
                Flags = frame.Flags
            };
        }

        public MoveInputFrame ToFrame()
        {
            return new MoveInputFrame { Seq = Seq, MoveX = MoveX, MoveY = MoveY, Yaw = Yaw, Pitch = Pitch, Flags = Flags };
        }
    }

    /// <summary>
    /// The last up to three frames of a client, oldest first, in ONE unreliable message per network tick (loss redundancy; the
    /// server dedupes by sequence). Fixed layout so it needs no array support from the RPC layer.
    /// </summary>
    public struct MoveBatchWire : INetworkSerializable
    {
        public const int MaxFrames = 3;

        public byte Count;
        public MoveFrameWire F0;
        public MoveFrameWire F1;
        public MoveFrameWire F2;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref Count);
            F0.NetworkSerialize(serializer);
            F1.NetworkSerialize(serializer);
            F2.NetworkSerialize(serializer);
        }

        public MoveFrameWire Get(int index)
        {
            switch (index)
            {
                case 0: return F0;
                case 1: return F1;
                default: return F2;
            }
        }

        public static MoveBatchWire From(MoveInputFrame[] frames, int count)
        {
            if (count > MaxFrames) count = MaxFrames;
            if (count < 0) count = 0;

            MoveBatchWire batch = new MoveBatchWire { Count = (byte)count };
            if (count > 0) batch.F0 = MoveFrameWire.From(in frames[0]);
            if (count > 1) batch.F1 = MoveFrameWire.From(in frames[1]);
            if (count > 2) batch.F2 = MoveFrameWire.From(in frames[2]);
            return batch;
        }
    }

    /// <summary>
    /// U3A4b, server to owner (unreliable, every second network tick): the state the server reached after the newest input frame it
    /// processed, so the owner can compare it with what it predicted for that sequence number. Position, vertical velocity and the
    /// planar velocity of the ramp are the spec; jump cooldown, capsule height and the crouch bit complete the Step state so a snap
    /// can replay exactly. No identity of any kind; it only ever goes to the object's owner.
    /// </summary>
    public struct MoveAckWire : INetworkSerializable
    {
        public uint LastProcessedSeq;
        public uint ServerTick;
        public float X;
        public float Y;
        public float Z;
        public float VerticalVelocity;
        public float PlanarVx;
        public float PlanarVz;
        public float JumpCooldown;
        public float CapsuleHeight;
        public byte StateFlags;

        public const byte FlagCrouched = 1 << 0;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref LastProcessedSeq);
            serializer.SerializeValue(ref ServerTick);
            serializer.SerializeValue(ref X);
            serializer.SerializeValue(ref Y);
            serializer.SerializeValue(ref Z);
            serializer.SerializeValue(ref VerticalVelocity);
            serializer.SerializeValue(ref PlanarVx);
            serializer.SerializeValue(ref PlanarVz);
            serializer.SerializeValue(ref JumpCooldown);
            serializer.SerializeValue(ref CapsuleHeight);
            serializer.SerializeValue(ref StateFlags);
        }

        public static MoveAckWire From(uint seq, uint serverTick, in PredictedState state)
        {
            return new MoveAckWire
            {
                LastProcessedSeq = seq,
                ServerTick = serverTick,
                X = state.X,
                Y = state.Y,
                Z = state.Z,
                VerticalVelocity = state.VerticalVelocity,
                PlanarVx = state.PlanarVx,
                PlanarVz = state.PlanarVz,
                JumpCooldown = state.JumpCooldown,
                CapsuleHeight = state.CapsuleHeight,
                StateFlags = (byte)(state.Crouched ? FlagCrouched : 0)
            };
        }

        public PredictedState ToState()
        {
            return new PredictedState
            {
                X = X,
                Y = Y,
                Z = Z,
                VerticalVelocity = VerticalVelocity,
                PlanarVx = PlanarVx,
                PlanarVz = PlanarVz,
                JumpCooldown = JumpCooldown,
                CapsuleHeight = CapsuleHeight,
                Crouched = (StateFlags & FlagCrouched) != 0
            };
        }
    }

    /// <summary>
    /// U3A4b: the server-resolved movement numbers (owner-read NetworkVariable payload; see <see cref="MoveParamsData"/>). The
    /// IEquatable implementation lets the variable's change detection compare values without touching the bytes.
    /// </summary>
    public struct MoveParamsWire : INetworkSerializable, IEquatable<MoveParamsWire>
    {
        public float Walk;
        public float Sprint;
        public float Crouch;
        public float StandHeight;
        public float CrouchHeight;
        public float JumpHeight;
        public int GroundMask;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref Walk);
            serializer.SerializeValue(ref Sprint);
            serializer.SerializeValue(ref Crouch);
            serializer.SerializeValue(ref StandHeight);
            serializer.SerializeValue(ref CrouchHeight);
            serializer.SerializeValue(ref JumpHeight);
            serializer.SerializeValue(ref GroundMask);
        }

        public static MoveParamsWire From(in MoveParamsData data)
        {
            return new MoveParamsWire
            {
                Walk = data.Walk,
                Sprint = data.Sprint,
                Crouch = data.Crouch,
                StandHeight = data.StandHeight,
                CrouchHeight = data.CrouchHeight,
                JumpHeight = data.JumpHeight,
                GroundMask = data.GroundMask
            };
        }

        public MoveParamsData ToData()
        {
            return new MoveParamsData
            {
                Walk = Walk,
                Sprint = Sprint,
                Crouch = Crouch,
                StandHeight = StandHeight,
                CrouchHeight = CrouchHeight,
                JumpHeight = JumpHeight,
                GroundMask = GroundMask
            };
        }

        public bool Equals(MoveParamsWire other) { return ToData().Equals(other.ToData()); }

        public override bool Equals(object obj) { return obj is MoveParamsWire other && Equals(other); }

        public override int GetHashCode() { return ToData().GetHashCode(); }
    }
}
