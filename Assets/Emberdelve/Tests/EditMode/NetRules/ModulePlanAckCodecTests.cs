using Emberdelve.Net.Rules;
using NUnit.Framework;

namespace Emberdelve.Net.Rules.Tests
{
    public sealed class ModulePlanAckCodecTests
    {
        [TestCase(0xA5AACDF7u, true)]
        [TestCase(0x00000000u, false)]
        [TestCase(0xFFFFFFFFu, true)]
        public void AckRoundTripsExactHashAndDecision(uint hash, bool accepted)
        {
            byte[] payload = ModulePlanAckCodec.Encode(hash, accepted);

            Assert.That(payload, Has.Length.EqualTo(ModulePlanAckCodec.PayloadBytes));
            Assert.That(ModulePlanAckCodec.TryDecode(payload, out uint decodedHash, out bool decodedAccepted, out string reason), Is.True, reason);
            Assert.That(decodedHash, Is.EqualTo(hash));
            Assert.That(decodedAccepted, Is.EqualTo(accepted));
        }

        [Test]
        public void AckRejectsTruncatedOverlongAndInvalidDecisionPayloads()
        {
            byte[] valid = ModulePlanAckCodec.Encode(0xA5AACDF7u, true);
            byte[] truncated = new byte[valid.Length - 1];
            byte[] overlong = new byte[valid.Length + 1];
            valid.CopyTo(overlong, 0);
            byte[] invalidDecision = (byte[])valid.Clone();
            invalidDecision[4] = 2;

            Assert.That(ModulePlanAckCodec.TryDecode(truncated, out _, out _, out _), Is.False);
            Assert.That(ModulePlanAckCodec.TryDecode(overlong, out _, out _, out _), Is.False);
            Assert.That(ModulePlanAckCodec.TryDecode(invalidDecision, out _, out _, out _), Is.False);
        }
    }
}
