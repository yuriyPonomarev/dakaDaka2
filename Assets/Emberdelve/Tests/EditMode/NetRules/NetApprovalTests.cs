using System.Collections.Generic;
using System.Text;
using Emberdelve.Net.Rules;
using NUnit.Framework;

namespace Emberdelve.NetRules.Tests
{
    public sealed class NetApprovalTests
    {
        private const int Proto = NetTuning.ProtocolVersion;
        private static readonly HashSet<string> Classes = new HashSet<string> { "warrior", "archer" };

        private static bool KnownClass(string id) { return Classes.Contains((id ?? "").ToLowerInvariant()); }

        private static NetApprovalResult Eval(byte[] payload, int connected, int max, params string[] takenSlots)
        {
            HashSet<string> taken = new HashSet<string>(takenSlots);
            return NetApproval.Evaluate(payload, Proto, connected, max, s => taken.Contains(s), KnownClass);
        }

        private static byte[] Payload(string slot, string cls, int protocol = Proto)
        {
            return NetConnectPayload.Encode(protocol, slot, cls);
        }

        [Test]
        public void Valid_IsApproved_WithSlotAndClass()
        {
            NetApprovalResult r = Eval(Payload("a", "Warrior"), 0, 2);
            Assert.That(r.Approved, Is.True);
            Assert.That(r.Reason, Is.EqualTo(""));
            Assert.That(r.Slot, Is.EqualTo("a"));
            Assert.That(r.ClassId, Is.EqualTo("Warrior"));
        }

        [Test]
        public void LastFreeSeat_IsApproved_NextIsFull()
        {
            Assert.That(Eval(Payload("b", "archer"), 1, 2).Approved, Is.True);
            NetApprovalResult full = Eval(Payload("c", "archer"), 2, 2);
            Assert.That(full.Approved, Is.False);
            Assert.That(full.Reason, Is.EqualTo("server full"));
        }

        [Test]
        public void ProtocolMismatch_KeepsTheU3A1Wording()
        {
            NetApprovalResult r = Eval(Payload("a", "warrior", 999), 0, 2);
            Assert.That(r.Approved, Is.False);
            Assert.That(r.Reason, Does.StartWith("protocol mismatch"));
            Assert.That(r.Reason, Is.EqualTo("protocol mismatch client=999 server=" + Proto));
        }

        [Test]
        public void DuplicateSlot_IsRefused()
        {
            NetApprovalResult r = Eval(Payload("a", "warrior"), 1, 3, "a");
            Assert.That(r.Approved, Is.False);
            Assert.That(r.Reason, Is.EqualTo("duplicate slot"));
        }

        [Test]
        public void UnknownClass_IsRefused_AndEmptyClassToo()
        {
            Assert.That(Eval(Payload("a", "necromancer"), 0, 3).Reason, Is.EqualTo("unknown class"));
            Assert.That(Eval(Payload("a", ""), 0, 3).Reason, Is.EqualTo("unknown class"));
        }

        [Test]
        public void RequestFaults_WinOverAFullServer()
        {
            // The server is full AND the slot is a duplicate AND the class is unknown: the request-level reason is reported.
            Assert.That(Eval(Payload("a", "warrior"), 2, 2, "a").Reason, Is.EqualTo("duplicate slot"));
            Assert.That(Eval(Payload("z", "bogus"), 2, 2).Reason, Is.EqualTo("unknown class"));
            Assert.That(Eval(Payload("z", "warrior"), 2, 2).Reason, Is.EqualTo("server full"));
        }

        [Test]
        public void ProtocolMismatch_WinsOverEverythingElse()
        {
            NetApprovalResult r = Eval(Payload("a", "bogus", 7), 5, 2, "a");
            Assert.That(r.Reason, Does.StartWith("protocol mismatch"));
        }

        [Test]
        public void NullEmptyOversizeAndMalformed_AreBadPayload()
        {
            Assert.That(Eval(null, 0, 2).Reason, Is.EqualTo("bad payload"));
            Assert.That(Eval(new byte[0], 0, 2).Reason, Is.EqualTo("bad payload"));
            Assert.That(Eval(new byte[NetConnectPayload.MaxBytes + 1], 0, 2).Reason, Is.EqualTo("bad payload"));
            Assert.That(Eval(new byte[] { 1, 2, 3 }, 0, 2).Reason, Is.EqualTo("bad payload"));
            Assert.That(Eval(Encoding.UTF8.GetBytes("EMB|x|a|warrior"), 0, 2).Reason, Is.EqualTo("bad payload"));
            Assert.That(Eval(Encoding.UTF8.GetBytes("EMB|1|a"), 0, 2).Reason, Is.EqualTo("bad payload"));
            Assert.That(Eval(Encoding.UTF8.GetBytes("XYZ|1|a|warrior"), 0, 2).Reason, Is.EqualTo("bad payload"));
        }

        [Test]
        public void MaxLengthSlot_IsAccepted()
        {
            string slot = new string('s', NetConnectPayload.MaxField);
            string cls = "warrior";
            byte[] ok = Encoding.UTF8.GetBytes("EMB|" + Proto + "|" + slot + "|" + cls);
            Assert.That(ok.Length, Is.LessThanOrEqualTo(NetConnectPayload.MaxBytes));
            Assert.That(Eval(ok, 0, 2).Approved, Is.True);
        }

        [Test]
        public void HostileFields_AreBadPayload()
        {
            // A forged payload bypassing the encoder: overlong slot, control characters (log injection), empty slot.
            Assert.That(Eval(Encoding.UTF8.GetBytes("EMB|1|" + new string('s', 33) + "|warrior"), 0, 2).Reason, Is.EqualTo("bad payload"));
            Assert.That(Eval(Encoding.UTF8.GetBytes("EMB|1|a\nadmitted|warrior"), 0, 2).Reason, Is.EqualTo("bad payload"));
            Assert.That(Eval(Encoding.UTF8.GetBytes("EMB|1||warrior"), 0, 2).Reason, Is.EqualTo("bad payload"));
            Assert.That(Eval(Encoding.UTF8.GetBytes("EMB|1|a|waré"), 0, 2).Reason, Is.EqualTo("bad payload"));
        }

        [Test]
        public void ReasonStrings_AreStable()
        {
            Assert.That(NetApproval.ReasonBadPayload, Is.EqualTo("bad payload"));
            Assert.That(NetApproval.ReasonUnknownClass, Is.EqualTo("unknown class"));
            Assert.That(NetApproval.ReasonDuplicateSlot, Is.EqualTo("duplicate slot"));
            Assert.That(NetApproval.ReasonServerFull, Is.EqualTo("server full"));
            Assert.That(NetApproval.ReasonProtocolMismatch, Is.EqualTo("protocol mismatch"));
        }

        [Test]
        public void FourArgOverload_DoesNotCheckSlotOrClass()
        {
            NetApprovalResult r = NetApproval.Evaluate(Payload("a", "whatever"), Proto, 0, 2);
            Assert.That(r.Approved, Is.True);
        }

        // ---- launch flags added with this slice ----

        [Test]
        public void NoEnemies_BareFlagIsTrue_AndDefaultIsFalse()
        {
            Assert.That(NetLaunchArgs.Parse(new[] { "-ember-net", "server" }).NoEnemies, Is.False);
            NetLaunchArgs bare = NetLaunchArgs.Parse(new[] { "-ember-net", "server", "-ember-net-no-enemies", "-ember-port", "7801" });
            Assert.That(bare.IsValid, Is.True);
            Assert.That(bare.NoEnemies, Is.True);
            Assert.That(bare.Port, Is.EqualTo(7801));
        }

        [Test]
        public void NoEnemies_DoubleDashAndExplicitValues()
        {
            Assert.That(NetLaunchArgs.Parse(new[] { "-ember-net=server", "--ember-net-no-enemies" }).NoEnemies, Is.True);
            Assert.That(NetLaunchArgs.Parse(new[] { "-ember-net=server", "--ember-net-no-enemies=true" }).NoEnemies, Is.True);
            Assert.That(NetLaunchArgs.Parse(new[] { "-ember-net=server", "--ember-net-no-enemies=false" }).NoEnemies, Is.False);
            Assert.That(NetLaunchArgs.Parse(new[] { "-ember-net", "server", "-ember-net-no-enemies", "false" }).NoEnemies, Is.False);
            NetLaunchArgs last = NetLaunchArgs.Parse(new[] { "-ember-net", "server", "-ember-net-no-enemies" });
            Assert.That(last.IsValid, Is.True);
            Assert.That(last.NoEnemies, Is.True);
            Assert.That(NetLaunchArgs.Parse(new[] { "-ember-net", "server", "--ember-net-no-enemies=maybe" }).IsValid, Is.False);
        }

        [Test]
        public void Script_ForgeOwner_ParsesBothSpellings_UnknownIsAnError()
        {
            Assert.That(NetLaunchArgs.Parse(new[] { "-ember-net", "client" }).Script, Is.EqualTo(""));
            Assert.That(NetLaunchArgs.Parse(new[] { "-ember-net", "client", "--ember-net-script=forge-owner" }).Script,
                Is.EqualTo(NetLaunchArgs.ScriptForgeOwner));
            Assert.That(NetLaunchArgs.Parse(new[] { "-ember-net", "client", "-ember-net-script", "Forge-Owner" }).Script,
                Is.EqualTo("forge-owner"));
            Assert.That(NetLaunchArgs.Parse(new[] { "-ember-net", "client", "--ember-net-script=dance" }).IsValid, Is.False);
            Assert.That(NetLaunchArgs.Parse(new[] { "-ember-net", "client", "-ember-net-script" }).IsValid, Is.False);
        }

        [Test]
        public void ClassAndSlotAliases_StillParse_AndOtherFlagsUnaffected()
        {
            NetLaunchArgs a = NetLaunchArgs.Parse(new[]
            {
                "-ember-net", "client", "--ember-net-class=archer", "--ember-net-slot=adm-b", "-ember-net-no-enemies",
                "-ember-net-max-players", "2"
            });
            Assert.That(a.IsValid, Is.True);
            Assert.That(a.ClassId, Is.EqualTo("archer"));
            Assert.That(a.Slot, Is.EqualTo("adm-b"));
            Assert.That(a.MaxPlayers, Is.EqualTo(2));
        }
    }
}
