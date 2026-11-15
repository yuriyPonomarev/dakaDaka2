using Emberdelve.Net;
using NUnit.Framework;
using Unity.Collections;
using Unity.Netcode;

namespace Emberdelve.Tests
{
    public sealed class AvatarArmorWireTests
    {
        [Test] public void AllSixSlotsSurviveSerializationAndEmptyStateClearsThem()
        {
            var state = new AvatarArmorWire { Head = "iron_hood", Chest = "boiled_cuirass", Legs = "leather_trousers",
                Hands = "graspwrap_gloves", Feet = "footpad_boots", Back = "ashen_cloak" };
            Assert.True(state.IsValid);
            Assert.True(RoundTrip(state).Equals(state));
            var cleared = RoundTrip(default);
            Assert.True(cleared.IsValid);
            Assert.True(cleared.Equals(default));
            Assert.False(cleared.Equals(state));
        }
        [TestCase(0)] [TestCase(1)] [TestCase(2)] [TestCase(3)] [TestCase(4)] [TestCase(5)]
        public void EachSlotParticipatesInChangeDetection(int slot)
        {
            var state = new AvatarArmorWire();
            switch (slot)
            {
                case 0: state.Head = "new_item"; break;
                case 1: state.Chest = "new_item"; break;
                case 2: state.Legs = "new_item"; break;
                case 3: state.Hands = "new_item"; break;
                case 4: state.Feet = "new_item"; break;
                case 5: state.Back = "new_item"; break;
            }
            Assert.False(state.Equals(default));
            Assert.True(RoundTrip(state).Equals(state));
        }
        [Test] public void InvalidAuthoringNeverThrowsAndForgedWireIdIsRejected()
        {
            foreach (var id in new[] { (string)null, "", new string('a', 62), "bad/item", "bad\nitem", "броня" })
                Assert.That(AvatarArmorWire.SafeId(id).Length, Is.Zero);
            string boundary = new string('a', AvatarArmorWire.MaxIdLength);
            Assert.That(AvatarArmorWire.SafeId(boundary).ToString(), Is.EqualTo(boundary));
            var forged = new AvatarArmorWire { Chest = "bad/item" };
            Assert.False(forged.IsValid);
            Assert.False(RoundTrip(forged).IsValid);
        }
        [Test] public void MissingAuthoritativeEquipmentProducesEmptyAppearance()
        {
            Assert.True(AvatarArmorWire.Capture(null).Equals(default));
        }
        private static AvatarArmorWire RoundTrip(AvatarArmorWire source)
        {
            using (var writer = new FastBufferWriter(512, Allocator.Temp))
            {
                writer.WriteValueSafe(source);
                using (var reader = new FastBufferReader(writer, Allocator.Temp))
                { reader.ReadValueSafe(out AvatarArmorWire result); return result; }
            }
        }
    }
}
