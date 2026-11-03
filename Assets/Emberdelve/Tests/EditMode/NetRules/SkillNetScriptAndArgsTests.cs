using Emberdelve.Net.Rules;
using NUnit.Framework;

namespace Emberdelve.NetRules.Tests
{
    /// <summary>U3B2: the skill client scripts and the server abuse arguments.</summary>
    public sealed class SkillNetScriptAndArgsTests
    {
        private static NetScriptSpec Parse(string text)
        {
            Assert.That(NetScriptSpec.TryParse(text, out NetScriptSpec spec, out string error), Is.True, error);
            return spec;
        }

        [Test]
        public void SkillCast_IsAnIsolatedScriptOnTheProductionPath()
        {
            NetScriptSpec spec = Parse("skill-cast");
            Assert.That(spec.IsSkillCastOnly, Is.True);
            Assert.That(spec.IsSkillScript, Is.True);
            Assert.That(spec.IsProbeOnly, Is.True);
            Assert.That(spec.IsSkillAbuseOnly, Is.False);
            Assert.That(spec.IsSkillProofOnly, Is.False);
            Assert.That(spec.IsForgeOwnerOnly, Is.False);
            Assert.That(NetScriptSpec.NameOf(spec.Steps[0].Kind), Is.EqualTo("skill-cast"));
            Assert.That(NetScriptSpec.IsDevelopmentOnly(NetScriptKind.SkillCast), Is.False, "a scripted key press exists in release builds");
        }

        [Test]
        public void SkillAbuse_IsADevelopmentOnlyIsolatedProbe()
        {
            NetScriptSpec spec = Parse("skill-abuse");
            Assert.That(spec.IsSkillAbuseOnly, Is.True);
            Assert.That(spec.IsSkillScript, Is.True);
            Assert.That(spec.IsProbeOnly, Is.True);
            Assert.That(NetScriptSpec.NameOf(spec.Steps[0].Kind), Is.EqualTo("skill-abuse"));
            Assert.That(NetScriptSpec.IsDevelopmentOnly(NetScriptKind.SkillAbuse), Is.True);
            Assert.That(NetScriptSpec.IsDevelopmentOnly(NetScriptKind.SkillProof), Is.True);
            Assert.That(NetScriptSpec.IsDevelopmentOnly(NetScriptKind.ForgeOwner), Is.True);
            Assert.That(NetScriptSpec.IsDevelopmentOnly(NetScriptKind.WalkForward), Is.False);
        }

        [TestCase("skill-cast,jump")]
        [TestCase("jump,skill-cast")]
        [TestCase("skill-abuse,skill-abuse")]
        [TestCase("skill-cast:3")]
        [TestCase("skill-abuse:1.5")]
        [TestCase("skill-cast,forge-owner")]
        public void SkillScripts_CannotBeChainedOrGivenADuration(string text)
        {
            Assert.That(NetScriptSpec.TryParse(text, out NetScriptSpec spec, out string error), Is.False, text);
            Assert.That(spec, Is.Null);
            Assert.That(error, Is.Not.Empty);
        }

        [Test]
        public void MovementScriptsAreNotProbes()
        {
            Assert.That(Parse("walk-forward:2,jump").IsProbeOnly, Is.False);
            Assert.That(Parse("jump").IsSkillScript, Is.False);
            Assert.That(Parse("forge-owner").IsProbeOnly, Is.True);
            Assert.That(Parse("skill-proof").IsSkillScript, Is.True);
        }

        [Test]
        public void ArgsDefaults_MatchThePolicyDefaults()
        {
            NetLaunchArgs a = NetLaunchArgs.Parse(new[] { "-ember-net", "server" });
            Assert.That(a.IsValid, Is.True);
            SkillAbusePolicy p = a.SkillAbusePolicy;
            Assert.That(p.RejectBurst, Is.EqualTo(SkillAbusePolicy.DefaultRejectBurst));
            Assert.That(p.RejectRefillPerSecond, Is.EqualTo(SkillAbusePolicy.DefaultRejectRefillPerSecond));
            Assert.That(p.DisconnectExcess, Is.EqualTo(SkillAbusePolicy.DefaultDisconnectExcess));
            Assert.That(p.MemorySeconds, Is.EqualTo(SkillAbusePolicy.DefaultMemorySeconds));
        }

        [Test]
        public void ArgsParse_SpaceAndEqualsForms()
        {
            NetLaunchArgs a = NetLaunchArgs.Parse(new[]
            {
                "-ember-net", "server", "-ember-net-skill-reject-burst", "6", "--ember-net-skill-reject-rate=0",
                "-ember-net-skill-disconnect-after", "5", "-ember-net-skill-memory-seconds", "600.5"
            });
            Assert.That(a.IsValid, Is.True, string.Join(";", a.Errors));
            Assert.That(a.SkillRejectBurst, Is.EqualTo(6.0));
            Assert.That(a.SkillRejectRate, Is.EqualTo(0.0));
            Assert.That(a.SkillDisconnectAfter, Is.EqualTo(5));
            Assert.That(a.SkillMemorySeconds, Is.EqualTo(600.5));
            Assert.That(a.SkillAbusePolicy.ThresholdFor(1), Is.EqualTo(2));
        }

        [TestCase("-ember-net-skill-reject-burst", "0")]
        [TestCase("-ember-net-skill-reject-burst", "abc")]
        [TestCase("-ember-net-skill-reject-burst", "NaN")]
        [TestCase("-ember-net-skill-reject-rate", "-1")]
        [TestCase("-ember-net-skill-reject-rate", "1001")]
        [TestCase("-ember-net-skill-disconnect-after", "0")]
        [TestCase("-ember-net-skill-disconnect-after", "100001")]
        [TestCase("-ember-net-skill-memory-seconds", "-5")]
        [TestCase("-ember-net-skill-memory-seconds", "86401")]
        public void ArgsInvalid_AreErrors(string name, string value)
        {
            NetLaunchArgs a = NetLaunchArgs.Parse(new[] { "-ember-net", "server", name, value });
            Assert.That(a.IsValid, Is.False, name + " " + value);
        }

        [Test]
        public void ScriptArgumentAcceptsTheNewScripts()
        {
            NetLaunchArgs a = NetLaunchArgs.Parse(new[] { "-ember-net", "client", "-ember-net-script", "skill-cast" });
            Assert.That(a.IsValid, Is.True);
            Assert.That(a.Script, Is.EqualTo("skill-cast"));
            Assert.That(a.ScriptSpec.IsSkillCastOnly, Is.True);
        }
    }
}
