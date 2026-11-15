using Emberdelve.Net.Rules;
using NUnit.Framework;

namespace Emberdelve.NetRules.Tests
{
    /// <summary>U3B3: the combat client scripts, the party-size proof switch and the intent / refusal vocabulary.</summary>
    public sealed class CombatNetScriptAndArgsTests
    {
        private static NetScriptSpec Parse(string text)
        {
            Assert.That(NetScriptSpec.TryParse(text, out NetScriptSpec spec, out string error), Is.True, error);
            return spec;
        }

        [TestCase("melee-duel", NetScriptKind.MeleeDuel)]
        [TestCase("melee-parry", NetScriptKind.MeleeParry)]
        [TestCase("ranged-shot", NetScriptKind.RangedShot)]
        [TestCase("combat-abuse", NetScriptKind.CombatAbuse)]
        public void CombatScripts_AreIsolatedProbesWithTheirOwnName(string text, NetScriptKind kind)
        {
            NetScriptSpec spec = Parse(text);
            Assert.That(spec.IsCombatScript, Is.True);
            Assert.That(spec.IsProbeOnly, Is.True);
            Assert.That(spec.IsSkillScript, Is.False);
            Assert.That(spec.Steps[0].Kind, Is.EqualTo(kind));
            Assert.That(NetScriptSpec.NameOf(kind), Is.EqualTo(text));
        }

        [Test]
        public void OnlyTheHostileCombatScriptIsDevelopmentOnly()
        {
            Assert.That(NetScriptSpec.IsDevelopmentOnly(NetScriptKind.CombatAbuse), Is.True);
            Assert.That(NetScriptSpec.IsDevelopmentOnly(NetScriptKind.MeleeDuel), Is.False);
            Assert.That(NetScriptSpec.IsDevelopmentOnly(NetScriptKind.RangedShot), Is.False);
        }

        [TestCase("melee-duel,jump")]
        [TestCase("jump,ranged-shot")]
        [TestCase("combat-abuse,combat-abuse")]
        [TestCase("melee-duel:3")]
        [TestCase("ranged-shot:1.5")]
        [TestCase("melee-duel,skill-cast")]
        public void CombatScripts_CannotBeChainedOrGivenADuration(string text)
        {
            Assert.That(NetScriptSpec.TryParse(text, out NetScriptSpec spec, out string error), Is.False, text);
            Assert.That(spec, Is.Null);
            Assert.That(error, Is.Not.Empty);
        }

        [Test]
        public void ScriptArgumentAcceptsTheCombatScripts()
        {
            NetLaunchArgs a = NetLaunchArgs.Parse(new[] { "-ember-net", "client", "-ember-net-script", "melee-duel" });
            Assert.That(a.IsValid, Is.True, string.Join(";", a.Errors));
            Assert.That(a.ScriptSpec.IsMeleeDuelOnly, Is.True);
        }

        [Test]
        public void InventoryContestHarnessArguments_AreExplicitAndValidated()
        {
            NetLaunchArgs server = NetLaunchArgs.Parse(new[] { "-ember-net", "server", "-ember-net-inventory-contest-server" });
            Assert.That(server.IsValid, Is.True, string.Join(";", server.Errors));
            Assert.That(server.InventoryContestServer, Is.True);

            NetLaunchArgs client = NetLaunchArgs.Parse(new[] { "-ember-net", "client", "-ember-net-inventory-contest-role", "a" });
            Assert.That(client.IsValid, Is.True, string.Join(";", client.Errors));
            Assert.That(client.InventoryContestRole, Is.EqualTo("a"));

            NetLaunchArgs invalid = NetLaunchArgs.Parse(new[] { "-ember-net", "client", "-ember-net-inventory-contest-role", "owner" });
            Assert.That(invalid.IsValid, Is.False);
        }

        [Test]
        public void InventoryDropProofArguments_AreKnownAndPreserveFixturePaths()
        {
            NetLaunchArgs server = NetLaunchArgs.Parse(new[]
            {
                "-ember-net", "server", "-ember-net-inventory-drop-proof-server",
                "-ember-net-inventory-drop-proof-action-file", "client action.txt",
                "-ember-net-inventory-drop-proof-expected-file", "expected item.json"
            });
            Assert.That(server.IsValid, Is.True, string.Join(";", server.Errors));
            Assert.That(server.InventoryDropProofServer, Is.True, "bare fixture switch must be recognized as a flag");
            Assert.That(server.InventoryDropProofActionFile, Is.EqualTo("client action.txt"));
            Assert.That(server.InventoryDropProofExpectedFile, Is.EqualTo("expected item.json"));
            Assert.That(NetLaunchArgs.Parse(new[] { "-ember-net", "server" }).InventoryDropProofServer, Is.False);

            foreach (string role in new[] { "owner", "peer", "late" })
            {
                NetLaunchArgs client = NetLaunchArgs.Parse(new[]
                {
                    "-ember-net", "client", "-ember-net-inventory-drop-proof-role", role,
                    "-ember-net-inventory-drop-proof-action-file", "client action.txt",
                    "-ember-net-inventory-drop-proof-expected-file", "expected item.json"
                });
                Assert.That(client.IsValid, Is.True, string.Join(";", client.Errors));
                Assert.That(client.InventoryDropProofRole, Is.EqualTo(role));
                Assert.That(client.InventoryDropProofActionFile, Is.EqualTo("client action.txt"));
                Assert.That(client.InventoryDropProofExpectedFile, Is.EqualTo("expected item.json"));
            }

            NetLaunchArgs invalid = NetLaunchArgs.Parse(new[]
                { "-ember-net", "client", "-ember-net-inventory-drop-proof-role", "unknown" });
            Assert.That(invalid.IsValid, Is.False);
        }

        [Test]
        public void CombatEnemyProofFixture_IsOptInAndServerScoped()
        {
            Assert.That(NetLaunchArgs.Parse(new[] { "-ember-net", "server" }).CombatEnemyProofServer, Is.False);
            NetLaunchArgs args = NetLaunchArgs.Parse(new[] { "-ember-net", "server", "-ember-net-combat-enemy-proof-server" });
            Assert.That(args.IsValid, Is.True, string.Join(";", args.Errors));
            Assert.That(args.CombatEnemyProofServer, Is.True);
        }

        [Test]
        public void ExtractionProofHarnessFlag_IsExplicitAndClientScriptsParse()
        {
            NetLaunchArgs server = NetLaunchArgs.Parse(new[] { "-ember-net", "server", "-ember-net-extraction-proof-server" });
            Assert.That(server.IsValid, Is.True, string.Join(";", server.Errors));
            Assert.That(server.ExtractionProofServer, Is.True);
            NetLaunchArgs ownerFlag = NetLaunchArgs.Parse(new[] { "-ember-net", "client", "-ember-net-extraction-proof-owner" });
            Assert.That(ownerFlag.IsValid, Is.True, string.Join(";", ownerFlag.Errors));
            Assert.That(ownerFlag.ExtractionProofOwner, Is.True);

            NetLaunchArgs action = NetLaunchArgs.Parse(new[] { "-ember-net", "server", "-ember-net-extraction-proof-action-file", "C:\\proof\\action.txt" });
            Assert.That(action.IsValid, Is.True, string.Join(";", action.Errors));
            Assert.That(action.ExtractionProofActionFile, Is.EqualTo("C:\\proof\\action.txt"));

            NetLaunchArgs owner = NetLaunchArgs.Parse(new[] { "-ember-net", "client", "-ember-net-script", "extraction-owner" });
            Assert.That(owner.IsValid, Is.True, string.Join(";", owner.Errors));
            Assert.That(owner.ScriptSpec.IsExtractionOwnerOnly, Is.True);
            NetLaunchArgs forge = NetLaunchArgs.Parse(new[] { "-ember-net", "client", "-ember-net-script", "extraction-forge" });
            Assert.That(forge.IsValid, Is.True, string.Join(";", forge.Errors));
            Assert.That(forge.ScriptSpec.IsExtractionForgeOnly, Is.True);
            NetLaunchArgs interrupt = NetLaunchArgs.Parse(new[] { "-ember-net", "client", "-ember-net-script", "extraction-interrupt" });
            Assert.That(interrupt.IsValid, Is.True, string.Join(";", interrupt.Errors));
            Assert.That(interrupt.ScriptSpec.IsExtractionInterruptOnly, Is.True);
        }

        [Test]
        public void PartySizeOverride_DefaultsToTheAuthoredValueAndParsesBothForms()
        {
            Assert.That(NetLaunchArgs.Parse(new[] { "-ember-net", "server" }).PartySizeOverride, Is.EqualTo(0));
            NetLaunchArgs a = NetLaunchArgs.Parse(new[] { "-ember-net", "server", "-ember-net-party-size", "1" });
            Assert.That(a.IsValid, Is.True, string.Join(";", a.Errors));
            Assert.That(a.PartySizeOverride, Is.EqualTo(1));
            NetLaunchArgs b = NetLaunchArgs.Parse(new[] { "-ember-net", "server", "--ember-net-party-size=2" });
            Assert.That(b.PartySizeOverride, Is.EqualTo(2));
        }

        [TestCase("0")]
        [TestCase("4")]
        [TestCase("-1")]
        [TestCase("abc")]
        public void PartySizeOverride_InvalidValuesAreErrors(string value)
        {
            NetLaunchArgs a = NetLaunchArgs.Parse(new[] { "-ember-net", "server", "-ember-net-party-size", value });
            Assert.That(a.IsValid, Is.False, value);
            Assert.That(a.PartySizeOverride, Is.EqualTo(0));
        }

        [Test]
        public void IntentKindsAreContiguousAndBounded()
        {
            Assert.That(CombatNetLimits.IsValidKind(0), Is.False);
            for (int k = 1; k <= CombatNetLimits.KindMax; k++) Assert.That(CombatNetLimits.IsValidKind(k), Is.True, "kind " + k);
            Assert.That(CombatNetLimits.IsValidKind(CombatNetLimits.KindMax + 1), Is.False);
            Assert.That(CombatNetLimits.KindMax, Is.EqualTo((byte)CombatIntentKind.DrawCancel));
            Assert.That(CombatNetLimits.ClientMinIntervalSeconds * CombatIntentGate.RequestsPerSecond, Is.GreaterThan(1.0),
                "an honest sender at the throttle stays under the gate's rate");
        }
    }
}
