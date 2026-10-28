using Emberdelve.Net.Rules;
using NUnit.Framework;

namespace Emberdelve.NetRules.Tests
{
    public sealed class NetLaunchArgsTests
    {
        [Test]
        public void NoArguments_IsOffline()
        {
            NetLaunchArgs a = NetLaunchArgs.Parse(new[] { "Emberdelve.exe", "-batchmode" });
            Assert.That(a.Mode, Is.EqualTo(NetLaunchMode.Offline));
            Assert.That(a.IsValid, Is.True);
        }

        [Test]
        public void Null_IsOffline()
        {
            Assert.That(NetLaunchArgs.Parse(null).Mode, Is.EqualTo(NetLaunchMode.Offline));
        }

        [Test]
        public void SpaceAndEqualsForms_Parse()
        {
            NetLaunchArgs a = NetLaunchArgs.Parse(new[]
            {
                "-ember-net", "client", "-ember-port", "7800", "--ember-address=10.0.0.5",
                "-ember-slot", "a", "-ember-net-autoquit", "2.5"
            });
            Assert.That(a.IsValid, Is.True);
            Assert.That(a.Mode, Is.EqualTo(NetLaunchMode.Client));
            Assert.That(a.Port, Is.EqualTo(7800));
            Assert.That(a.Address, Is.EqualTo("10.0.0.5"));
            Assert.That(a.Slot, Is.EqualTo("a"));
            Assert.That(a.AutoQuitSeconds, Is.EqualTo(2.5f));
        }

        [Test]
        public void ServerDefaults_AreLoopbackAndNine()
        {
            NetLaunchArgs a = NetLaunchArgs.Parse(new[] { "-ember-net", "server" });
            Assert.That(a.Mode, Is.EqualTo(NetLaunchMode.Server));
            Assert.That(a.ListenAddress, Is.EqualTo("127.0.0.1"));
            Assert.That(a.MaxPlayers, Is.EqualTo(9));
            Assert.That(a.Port, Is.EqualTo(7777));
        }

        [TestCase("0")]
        [TestCase("70000")]
        [TestCase("abc")]
        public void BadPort_IsAnError(string port)
        {
            NetLaunchArgs a = NetLaunchArgs.Parse(new[] { "-ember-net", "server", "-ember-port", port });
            Assert.That(a.IsValid, Is.False);
        }

        [Test]
        public void UnknownRole_IsAnError_NotSilentOffline()
        {
            NetLaunchArgs a = NetLaunchArgs.Parse(new[] { "-ember-net", "bogus" });
            Assert.That(a.IsValid, Is.False);
        }

        [Test]
        public void MissingValue_IsAnError()
        {
            NetLaunchArgs a = NetLaunchArgs.Parse(new[] { "-ember-net" });
            Assert.That(a.IsValid, Is.False);
        }

        [Test]
        public void Approval_AcceptsMatchingProtocol()
        {
            byte[] p = NetConnectPayload.Encode(NetTuning.ProtocolVersion, "s1", "Warrior");
            NetApprovalResult r = NetApproval.Evaluate(p, NetTuning.ProtocolVersion, 0, 9);
            Assert.That(r.Approved, Is.True);
            Assert.That(r.Slot, Is.EqualTo("s1"));
            Assert.That(r.ClassId, Is.EqualTo("Warrior"));
        }

        [Test]
        public void Approval_RejectsProtocolMismatchFullMalformedAndOversize()
        {
            byte[] bad = NetConnectPayload.Encode(NetTuning.ProtocolVersion + 1, "s", "c");
            Assert.That(NetApproval.Evaluate(bad, NetTuning.ProtocolVersion, 0, 9).Approved, Is.False);

            byte[] ok = NetConnectPayload.Encode(NetTuning.ProtocolVersion, "s", "c");
            NetApprovalResult full = NetApproval.Evaluate(ok, NetTuning.ProtocolVersion, 2, 2);
            Assert.That(full.Approved, Is.False);
            Assert.That(full.Reason, Is.EqualTo("server full"));

            Assert.That(NetApproval.Evaluate(new byte[] { 1, 2, 3 }, 1, 0, 9).Approved, Is.False);
            Assert.That(NetApproval.Evaluate(null, 1, 0, 9).Approved, Is.False);
            Assert.That(NetApproval.Evaluate(new byte[NetConnectPayload.MaxBytes + 1], 1, 0, 9).Approved, Is.False);
        }

        [Test]
        public void Payload_SanitizesSeparatorsAndLength()
        {
            byte[] p = NetConnectPayload.Encode(1, "a|b", new string('x', 100));
            Assert.That(NetConnectPayload.TryDecode(p, out int v, out string slot, out string cls), Is.True);
            Assert.That(v, Is.EqualTo(1));
            Assert.That(slot, Is.EqualTo("a_b"));
            Assert.That(cls.Length, Is.EqualTo(32));
        }
    }
}
