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
            Assert.That(CombatNetLimits.KindMax, Is.EqualTo((byte)CombatIntentKind.SwapWeapon));
            Assert.That(CombatNetLimits.ClientMinIntervalSeconds * CombatIntentGate.RequestsPerSecond, Is.GreaterThan(1.0),
                "an honest sender at the throttle stays under the gate's rate");
        }
    }
}
