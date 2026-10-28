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
}
