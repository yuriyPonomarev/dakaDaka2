using System.Text;
using Emberdelve.Net.Rules;
using NUnit.Framework;

namespace Emberdelve.NetRules.Tests
{
    /// <summary>The skill kit rides on the connection as an optional fifth field. It is data the server validates, never a number it trusts.</summary>
    public sealed class NetConnectKitTests
    {
        private const int Proto = NetTuning.ProtocolVersion;

        [Test]
        public void KitRoundTrips_ThroughEncodeAndDecode()
        {
            byte[] payload = NetConnectPayload.Encode(Proto, "slot1", "warrior", "skill_earthshaker,warrior_battle_cry;warrior_ashen_might,skill_iron_constitution");
            Assert.That(NetConnectPayload.TryDecode(payload, out int protocol, out string slot, out string classId, out string kit), Is.True);
            Assert.That(protocol, Is.EqualTo(Proto));
            Assert.That(slot, Is.EqualTo("slot1"));
            Assert.That(classId, Is.EqualTo("warrior"));
            Assert.That(kit, Is.EqualTo("skill_earthshaker,warrior_battle_cry;warrior_ashen_might,skill_iron_constitution"));
        }

        [Test]
        public void AnOlderClient_WithNoKit_StillDecodesAndIsApproved()
        {
            byte[] legacy = Encoding.UTF8.GetBytes("EMB|" + Proto + "|slot1|warrior");
            Assert.That(NetConnectPayload.TryDecode(legacy, out _, out _, out string classId, out string kit), Is.True);
            Assert.That(classId, Is.EqualTo("warrior"));
            Assert.That(kit, Is.Empty);
            NetApprovalResult r = NetApproval.Evaluate(legacy, Proto, 0, 4, null, id => id == "warrior");
            Assert.That(r.Approved, Is.True);
            Assert.That(r.Kit, Is.Empty);
        }

        [Test]
        public void ApprovalCarriesTheKit_ToTheServer()
        {
            byte[] payload = NetConnectPayload.Encode(Proto, "a", "warrior", "t_a1,t_a2;t_p1,t_p2");
            NetApprovalResult r = NetApproval.Evaluate(payload, Proto, 0, 4, null, id => id == "warrior");
            Assert.That(r.Approved, Is.True);
            Assert.That(r.Kit, Is.EqualTo("t_a1,t_a2;t_p1,t_p2"));
        }

        [TestCase("Dash<script>")]
        [TestCase("has space")]
        [TestCase("UPPER,CASE;X")]
        [TestCase("pipe|inside")]
        public void AHostileKit_IsDropped_NotTrusted(string hostile)
        {
            Assert.That(NetConnectPayload.CleanKit(hostile), Is.Empty);
            byte[] payload = NetConnectPayload.Encode(Proto, "a", "warrior", hostile);
            Assert.That(NetConnectPayload.TryDecode(payload, out _, out _, out _, out string kit), Is.True);
            Assert.That(kit, Is.Empty);
        }

        [Test]
        public void AnOversizedKit_IsDropped()
        {
            string tooLong = new string('a', NetConnectPayload.MaxKit + 1);
            Assert.That(NetConnectPayload.CleanKit(tooLong), Is.Empty);
        }

        [Test]
        public void ARawFifthFieldWithBadCharacters_IsSanitisedOnDecode()
        {
            byte[] raw = Encoding.UTF8.GetBytes("EMB|" + Proto + "|a|warrior|Bad Kit!!");
            Assert.That(NetConnectPayload.TryDecode(raw, out _, out _, out _, out string kit), Is.True);
            Assert.That(kit, Is.Empty);
        }

        [Test]
        public void MoreThanFiveFields_IsABadPayload()
        {
            byte[] raw = Encoding.UTF8.GetBytes("EMB|" + Proto + "|a|warrior|x|y");
            Assert.That(NetConnectPayload.TryDecode(raw, out _, out _, out _, out _), Is.False);
        }
    }
}
