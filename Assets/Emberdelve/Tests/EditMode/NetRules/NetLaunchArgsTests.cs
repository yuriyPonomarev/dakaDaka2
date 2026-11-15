using Emberdelve.Net.Rules;
using Emberdelve.Core;
using NUnit.Framework;

namespace Emberdelve.NetRules.Tests
{
    public sealed class NetLaunchArgsTests
    {
        [TestCase("solo", 1)] [TestCase("duo", 2)] [TestCase("trio", 3)]
        public void RoyaleSelectionIsExplicitAndBounded(string mode, int size)
        {
            var args = NetLaunchArgs.Parse(new[] { "game", "-ember-net", "host", "-ember-royale-mode", mode });
            Assert.True(args.IsValid); Assert.That(args.RoyalePartySize, Is.EqualTo(size));
        }
        [TestCase("squad")] [TestCase("0")] [TestCase("")]
        public void InvalidRoyaleModeDoesNotSilentlyChooseAnotherMode(string mode)
        {
            Assert.False(NetLaunchArgs.Parse(new[] { "game", "-ember-net", "host", "-ember-royale-mode", mode }).IsValid);
        }

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

        [Test]
        public void ServerHasNoLegacyOrOptionalMapSwitch()
        {
            Assert.That(GameConfig.DefaultDungeonSceneName, Is.EqualTo(NetTuning.ModularDungeonSceneName),
                "The product default and network authority must target the same required map.");
            NetLaunchArgs args = NetLaunchArgs.Parse(new[] { "-ember-net", "server" });
            Assert.That(args.IsValid, Is.True);

            NetLaunchArgs legacyOptOut = NetLaunchArgs.Parse(new[] { "-ember-net", "server", "-ember-net-modular-map", "false" });
            Assert.That(legacyOptOut.IsValid, Is.False, "An obsolete opt-out flag must fail argument validation instead of selecting Level01.");
        }

        [Test]
        public void CorpseDeathProofArguments_ParseOnlyAsExplicitServerFixture()
        {
            NetLaunchArgs args = NetLaunchArgs.Parse(new[]
            {
                "-ember-net", "server", "-ember-net-corpse-death-proof-server",
                "-ember-net-corpse-death-proof-action-file", "death-action.txt"
            });
            Assert.That(args.IsValid, Is.True);
            Assert.That(args.CorpseDeathProofServer, Is.True);
            Assert.That(args.CorpseDeathProofActionFile, Is.EqualTo("death-action.txt"));
            Assert.That(NetLaunchArgs.Parse(new[] { "-ember-net", "server" }).CorpseDeathProofServer, Is.False);
        }

        [Test]
        public void TimeoutProofArgument_IsAnExplicitDevelopmentServerFixture()
        {
            NetLaunchArgs args = NetLaunchArgs.Parse(new[] { "-ember-net", "server", "-ember-net-timeout-proof-server" });
            Assert.That(args.IsValid, Is.True);
            Assert.That(args.TimeoutProofServer, Is.True);
            Assert.That(NetLaunchArgs.Parse(new[] { "-ember-net", "server" }).TimeoutProofServer, Is.False);
        }

        [Test]
        public void DoorProofArgumentsRequireKnownRolesAndPreserveActionFiles()
        {
            NetLaunchArgs server = NetLaunchArgs.Parse(new[]
            {
                "-ember-net", "server", "-ember-net-door-proof-server",
                "-ember-net-door-proof-server-action-file", "server-stage.txt"
            });
            Assert.That(server.IsValid, Is.True);
            Assert.That(server.DoorProofServer, Is.True);
            Assert.That(server.DoorProofServerActionFile, Is.EqualTo("server-stage.txt"));

            NetLaunchArgs client = NetLaunchArgs.Parse(new[]
            {
                "-ember-net", "client", "-ember-net-door-proof-role", "late",
                "-ember-net-door-proof-action-file", "late-command.txt"
            });
            Assert.That(client.IsValid, Is.True);
            Assert.That(client.DoorProofRole, Is.EqualTo("late"));
            Assert.That(client.DoorProofActionFile, Is.EqualTo("late-command.txt"));
            Assert.That(NetLaunchArgs.Parse(new[] { "-ember-net", "client", "-ember-net-door-proof-role", "unknown" }).IsValid, Is.False);
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

        [Test]
        public void UtpResetMitigations_DefaultOn_AndParse()
        {
            NetLaunchArgs d = NetLaunchArgs.Parse(new[] { "-ember-net", "server" });
            Assert.That(d.UdpResetFix, Is.True);
            Assert.That(d.ClientLingerMs, Is.EqualTo(NetTuning.DefaultClientLingerMs));

            NetLaunchArgs off = NetLaunchArgs.Parse(new[] { "-ember-net", "client", "-ember-net-udp-reset-fix", "false", "-ember-net-client-linger-ms", "0" });
            Assert.That(off.IsValid, Is.True);
            Assert.That(off.UdpResetFix, Is.False);
            Assert.That(off.ClientLingerMs, Is.EqualTo(0));

            // a bare switch means true and must not swallow the next option
            NetLaunchArgs bare = NetLaunchArgs.Parse(new[] { "-ember-net", "server", "-ember-net-udp-reset-fix", "-ember-port", "7801" });
            Assert.That(bare.IsValid, Is.True);
            Assert.That(bare.UdpResetFix, Is.True);
            Assert.That(bare.Port, Is.EqualTo(7801));
        }

        [Test]
        public void UdpPath_DefaultScan_ParsesAndWinsOverLegacyFlag()
        {
            Assert.That(NetLaunchArgs.Parse(new[] { "-ember-net", "server" }).UdpPath, Is.EqualTo(NetUdpPath.Scan));
            Assert.That(NetLaunchArgs.Parse(new[] { "-ember-net", "server", "-ember-net-udp-path", "managed" }).UdpPath, Is.EqualTo(NetUdpPath.Managed));
            Assert.That(NetLaunchArgs.Parse(new[] { "-ember-net", "client", "-ember-net-udp-path", "ws" }).UdpPath, Is.EqualTo(NetUdpPath.WebSockets));
            Assert.That(NetLaunchArgs.Parse(new[] { "-ember-net", "client", "-ember-net-udp-path", "stock" }).UdpResetFix, Is.False);
            // legacy false = stock, in either order; an explicit path wins
            Assert.That(NetLaunchArgs.Parse(new[] { "-ember-net", "server", "-ember-net-udp-reset-fix", "false" }).UdpPath, Is.EqualTo(NetUdpPath.Stock));
            Assert.That(NetLaunchArgs.Parse(new[] { "-ember-net", "server", "-ember-net-udp-reset-fix", "false", "-ember-net-udp-path", "ws" }).UdpPath, Is.EqualTo(NetUdpPath.WebSockets));
            Assert.That(NetLaunchArgs.Parse(new[] { "-ember-net", "server", "-ember-net-udp-path", "ws", "-ember-net-udp-reset-fix", "false" }).UdpPath, Is.EqualTo(NetUdpPath.WebSockets));
            Assert.That(NetLaunchArgs.Parse(new[] { "-ember-net", "server", "-ember-net-udp-path", "tcp" }).IsValid, Is.False);
        }

        [TestCase("-1")]
        [TestCase("5001")]
        [TestCase("abc")]
        public void BadClientLinger_IsAnError(string value)
        {
            Assert.That(NetLaunchArgs.Parse(new[] { "-ember-net", "client", "-ember-net-client-linger-ms", value }).IsValid, Is.False);
        }
    }
}
