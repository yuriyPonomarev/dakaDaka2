using Emberdelve.Net;
using NUnit.Framework;
using Unity.Collections;
using Unity.Netcode;

namespace Emberdelve.Tests.NetInventory
{
    public sealed class WorldLightRequestWireTests
    {
        [TestCase(0)]
        [TestCase(8)]
        public void RequestAcceptsPayloadAtCurrentReaderPosition(int prefixBytes)
        {
            using (var writer = new FastBufferWriter(25, Allocator.Temp))
            {
                for (int i = 0; i < prefixBytes; i++) writer.WriteValueSafe((byte)42);
                writer.WriteValueSafe(123); writer.WriteValueSafe(456u); writer.WriteValueSafe(789UL); writer.WriteValueSafe((byte)1);
                using (var reader = new FastBufferReader(writer, Allocator.Temp))
                {
                    reader.Seek(prefixBytes);
                    Assert.IsTrue(NetWorldLights.TryReadRequest(reader, out int seed, out uint hash, out ulong id, out bool lit));
                    Assert.AreEqual(123, seed); Assert.AreEqual(456u, hash); Assert.AreEqual(789UL, id); Assert.IsTrue(lit);
                }
            }
        }
        [TestCase(0, 2)]
        [TestCase(1, 1)]
        public void RequestRejectsInvalidBooleanOrTrailingBytes(int trailingBytes, byte state)
        {
            using (var writer = new FastBufferWriter(18, Allocator.Temp))
            {
                writer.WriteValueSafe(123); writer.WriteValueSafe(456u); writer.WriteValueSafe(789UL); writer.WriteValueSafe(state);
                for (int i = 0; i < trailingBytes; i++) writer.WriteValueSafe((byte)42);
                using (var reader = new FastBufferReader(writer, Allocator.Temp))
                    Assert.IsFalse(NetWorldLights.TryReadRequest(reader, out _, out _, out _, out _));
            }
        }
    }
}
