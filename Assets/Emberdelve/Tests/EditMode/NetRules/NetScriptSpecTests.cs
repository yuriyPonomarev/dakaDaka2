using Emberdelve.Net.Rules;
using NUnit.Framework;

namespace Emberdelve.NetRules.Tests
{
    public sealed class NetScriptSpecTests
    {
        private static NetScriptSpec Parse(string text)
        {
            Assert.That(NetScriptSpec.TryParse(text, out NetScriptSpec spec, out string error), Is.True, text + " -> " + error);
            return spec;
        }

        [Test]
        public void SingleStep_UsesTheDefaultSeconds()
        {
            Assert.That(Parse("walk-forward").Steps[0].Seconds, Is.EqualTo(3f));
            Assert.That(Parse("sprint-forward").Steps[0].Seconds, Is.EqualTo(3f));
            Assert.That(Parse("walk-square").Steps[0].Seconds, Is.EqualTo(1f));
            Assert.That(Parse("walk-square").Steps[0].Duration, Is.EqualTo(4f), "four sides");
            Assert.That(Parse("jump").Steps[0].Kind, Is.EqualTo(NetScriptKind.Jump));
        }

        [Test]
        public void Seconds_AndYaw_AreParsed_CaseInsensitive_InvariantCulture()
        {
            NetScriptSpec spec = Parse(" Walk-Forward:2.5@-90 ");
            Assert.That(spec.Steps.Count, Is.EqualTo(1));
            Assert.That(spec.Steps[0].Kind, Is.EqualTo(NetScriptKind.WalkForward));
            Assert.That(spec.Steps[0].Seconds, Is.EqualTo(2.5f));
            Assert.That(spec.Steps[0].HasYaw, Is.True);
            Assert.That(spec.Steps[0].Yaw, Is.EqualTo(-90f).Within(1e-3f));
            Assert.That(spec.Text, Is.EqualTo("walk-forward:2.5@-90"));
        }

        [Test]
        public void Chain_IsParsedInOrder_WithAGapBetweenSteps()
        {
            NetScriptSpec spec = Parse("walk-forward:3@90,sprint-forward:1.5@270,jump");
            Assert.That(spec.Steps.Count, Is.EqualTo(3));
            Assert.That(spec.Steps[1].Kind, Is.EqualTo(NetScriptKind.SprintForward));
            Assert.That(spec.Steps[2].HasYaw, Is.False);
            Assert.That(spec.TotalSeconds, Is.EqualTo(3f + 1.5f + 1.5f + 3f * NetScriptSpec.GapSeconds).Within(1e-4f));
        }

        [Test]
        public void EveryHostileKind_IsKnown()
        {
            string[] names = { "flood-input", "flood-run", "replay-seq", "nan-input", "big-move", "seq-jump", "forge-move" };
            foreach (string name in names)
            {
                NetScriptSpec spec = Parse(name);
                Assert.That(spec.Steps[0].IsHostile, Is.True, name);
                Assert.That(NetScriptSpec.NameOf(spec.Steps[0].Kind), Is.EqualTo(name));
            }

            Assert.That(Parse("walk-forward").Steps[0].IsHostile, Is.False);
        }

        [Test]
        public void ForgeOwner_IsAScriptOfItsOwn()
        {
            Assert.That(Parse("forge-owner").IsForgeOwnerOnly, Is.True);
            Assert.That(NetScriptSpec.TryParse("forge-owner,jump", out _, out string chained), Is.False);
            Assert.That(chained, Does.Contain("chained"));
            Assert.That(NetScriptSpec.TryParse("forge-owner:3", out _, out _), Is.False);
        }

        [Test]
        public void InventoryProcessProbes_AreIsolatedDevelopmentScripts()
        {
            NetScriptSpec owner = Parse("inventory-owner");
            NetScriptSpec forge = Parse("inventory-forge");
            Assert.That(owner.IsProbeOnly, Is.True);
            Assert.That(owner.IsInventoryOwnerOnly, Is.True);
            Assert.That(forge.IsProbeOnly, Is.True);
            Assert.That(forge.IsInventoryForgeOnly, Is.True);
            Assert.That(NetScriptSpec.IsDevelopmentOnly(owner.Steps[0].Kind), Is.True);
            Assert.That(NetScriptSpec.IsDevelopmentOnly(forge.Steps[0].Kind), Is.True);
            Assert.That(NetScriptSpec.TryParse("inventory-owner,jump", out _, out string chained), Is.False);
            Assert.That(chained, Does.Contain("chained"));
        }

        [Test]
        public void ExtractionProcessProbes_AreIsolatedDevelopmentScripts()
        {
            NetScriptSpec owner = Parse("extraction-owner");
            NetScriptSpec forge = Parse("extraction-forge");
            Assert.That(owner.IsProbeOnly && owner.IsExtractionOwnerOnly, Is.True);
            Assert.That(forge.IsProbeOnly && forge.IsExtractionForgeOnly, Is.True);
            Assert.That(NetScriptSpec.IsDevelopmentOnly(owner.Steps[0].Kind), Is.True);
            Assert.That(NetScriptSpec.IsDevelopmentOnly(forge.Steps[0].Kind), Is.True);
            Assert.That(NetScriptSpec.TryParse("extraction-owner,jump", out _, out _), Is.False);
            NetScriptSpec interrupt = Parse("extraction-interrupt");
            Assert.That(interrupt.IsProbeOnly && interrupt.IsExtractionInterruptOnly, Is.True);
            Assert.That(NetScriptSpec.IsDevelopmentOnly(interrupt.Steps[0].Kind), Is.True);
        }

        [Test]
        public void SkillProof_IsAnIsolatedProbe()
        {
            NetScriptSpec spec = Parse("skill-proof");
            Assert.That(spec.IsSkillProofOnly, Is.True);
            Assert.That(spec.IsForgeOwnerOnly, Is.False);
            Assert.That(NetScriptSpec.NameOf(spec.Steps[0].Kind), Is.EqualTo("skill-proof"));
            Assert.That(NetScriptSpec.TryParse("skill-proof,jump", out _, out _), Is.False);
            Assert.That(NetScriptSpec.TryParse("skill-proof:3", out _, out _), Is.False);
        }

        [Test]
        public void EnemyHit_IsAnIsolatedDevelopmentCombatProbe()
        {
            NetScriptSpec spec = Parse("enemy-hit");
            Assert.That(spec.IsEnemyHitOnly, Is.True);
            Assert.That(spec.IsCombatScript, Is.True);
            Assert.That(NetScriptSpec.IsDevelopmentOnly(spec.Steps[0].Kind), Is.True);
            Assert.That(NetScriptSpec.TryParse("enemy-hit,jump", out _, out _), Is.False);
            Assert.That(NetScriptSpec.TryParse("enemy-hit:3", out _, out _), Is.False);
        }

        [TestCase("")]
        [TestCase("dance")]
        [TestCase("walk-forward:")]
        [TestCase("walk-forward:abc")]
        [TestCase("walk-forward:0")]
        [TestCase("walk-forward:-1")]
        [TestCase("walk-forward:NaN")]
        [TestCase("walk-forward:100000")]
        [TestCase("walk-forward@")]
        [TestCase("walk-forward@x")]
        [TestCase("walk-forward@Infinity")]
        [TestCase("walk-forward,,jump")]
        [TestCase("walk-forward,")]
        public void Malformed_IsRejected(string text)
        {
            Assert.That(NetScriptSpec.TryParse(text, out NetScriptSpec spec, out string error), Is.False, text);
            Assert.That(spec, Is.Null);
            Assert.That(error, Is.Not.Empty);
        }

        [Test]
        public void LaunchArgs_AcceptMovementScripts_AndTheNewOptions()
        {
            NetLaunchArgs args = NetLaunchArgs.Parse(new[]
            {
                "-ember-net", "client", "-ember-net-script", "Walk-Forward:3.0@90,jump",
                "-ember-net-script-start-file", "C:\\x\\go.txt", "--ember-net-script-yaw=-45.5"
            });
            Assert.That(args.IsValid, Is.True, string.Join(";", args.Errors));
            Assert.That(args.Script, Is.EqualTo("walk-forward:3.0@90,jump"));
            Assert.That(args.ScriptSpec.Steps.Count, Is.EqualTo(2));
            Assert.That(args.ScriptStartFile, Is.EqualTo("C:\\x\\go.txt"));
            Assert.That(args.HasScriptYaw, Is.True);
            Assert.That(args.ScriptYaw, Is.EqualTo(-45.5f).Within(1e-3f));

            NetLaunchArgs none = NetLaunchArgs.Parse(new[] { "-ember-net", "client" });
            Assert.That(none.ScriptSpec, Is.Null);
            Assert.That(none.HasScriptYaw, Is.False);
            Assert.That(none.ScriptStartFile, Is.EqualTo(""));
        }

        [Test]
        public void LaunchArgs_RejectBadScriptsAndYaw()
        {
            Assert.That(NetLaunchArgs.Parse(new[] { "-ember-net", "client", "-ember-net-script", "walk-forward:abc" }).IsValid, Is.False);
            Assert.That(NetLaunchArgs.Parse(new[] { "-ember-net", "client", "-ember-net-script-yaw", "north" }).IsValid, Is.False);
            Assert.That(NetLaunchArgs.Parse(new[] { "-ember-net", "client", "-ember-net-script-yaw", "NaN" }).IsValid, Is.False);
            Assert.That(NetLaunchArgs.Parse(new[] { "-ember-net", "client", "--ember-net-script=forge-owner" }).Script,
                Is.EqualTo(NetLaunchArgs.ScriptForgeOwner));
        }

        // ---- timeline ----------------------------------------------------------------------------------------

        [Test]
        public void Timeline_WalkForward_MovesForDurationThenIdlesThenIsDone()
        {
            NetScriptTimeline t = new NetScriptTimeline(Parse("walk-forward:3"), 30f);

            Assert.That(t.Evaluate(-1f).MoveY, Is.EqualTo(0f), "before the start");
            Assert.That(t.YawBeforeStart, Is.EqualTo(30f));

            NetScriptIntent mid = t.Evaluate(1.5f);
            Assert.That(mid.ActiveStep, Is.EqualTo(0));
            Assert.That(mid.MoveY, Is.EqualTo(1f));
            Assert.That(mid.Sprint, Is.False);
            Assert.That(mid.Yaw, Is.EqualTo(30f), "no @yaw: the default yaw");

            NetScriptIntent gap = t.Evaluate(3.1f);
            Assert.That(gap.ActiveStep, Is.EqualTo(-1));
            Assert.That(gap.MoveY, Is.EqualTo(0f));
            Assert.That(gap.Done, Is.False);
            Assert.That(t.CompletedSteps(2.9f), Is.EqualTo(0));
            Assert.That(t.CompletedSteps(3.0f), Is.EqualTo(1));

            Assert.That(t.Evaluate(3f + NetScriptSpec.GapSeconds + 0.01f).Done, Is.True);
        }

        [Test]
        public void Timeline_Chain_StartsEachStepAfterTheGap_AndKeepsPerStepYaw()
        {
            NetScriptTimeline t = new NetScriptTimeline(Parse("walk-forward:3@90,sprint-forward:1.5@270"), 0f);
            Assert.That(t.YawBeforeStart, Is.EqualTo(90f).Within(1e-3f), "faces the first step's yaw before it starts");

            NetScriptIntent walk = t.Evaluate(2f);
            Assert.That(walk.MoveY, Is.EqualTo(1f));
            Assert.That(walk.Yaw, Is.EqualTo(90f).Within(1e-3f));

            float secondStart = 3f + NetScriptSpec.GapSeconds;
            Assert.That(t.Evaluate(secondStart - 0.01f).ActiveStep, Is.EqualTo(-1), "still in the gap");
            NetScriptIntent sprint = t.Evaluate(secondStart + 0.1f);
            Assert.That(sprint.ActiveStep, Is.EqualTo(1));
            Assert.That(sprint.Sprint, Is.True);
            Assert.That(sprint.Yaw, Is.EqualTo(-90f).Within(1e-3f), "270 wraps to -90");
            Assert.That(t.CompletedSteps(secondStart + 1.51f), Is.EqualTo(2));
        }

        [Test]
        public void Timeline_Jump_FiresTheEdgeExactlyOnce()
        {
            NetScriptTimeline t = new NetScriptTimeline(Parse("jump"), 0f);
            Assert.That(t.Evaluate(0.0f).Jump, Is.True);
            Assert.That(t.Evaluate(0.1f).Jump, Is.False);
            Assert.That(t.Evaluate(0.5f).Jump, Is.False);
        }

        [Test]
        public void Timeline_WalkSquare_TurnsNinetyDegreesPerSide()
        {
            NetScriptTimeline t = new NetScriptTimeline(Parse("walk-square:2@0"), 0f);
            Assert.That(t.Evaluate(0.5f).Yaw, Is.EqualTo(0f).Within(1e-3f));
            Assert.That(t.Evaluate(2.5f).Yaw, Is.EqualTo(90f).Within(1e-3f));
            Assert.That(t.Evaluate(4.5f).Yaw, Is.EqualTo(-180f).Within(1e-3f));
            Assert.That(t.Evaluate(6.5f).Yaw, Is.EqualTo(-90f).Within(1e-3f));
            Assert.That(t.Evaluate(7.9f).MoveY, Is.EqualTo(1f));
            Assert.That(t.Evaluate(8.1f).MoveY, Is.EqualTo(0f), "four sides = 8 s");
            Assert.That(t.CompletedSteps(8f), Is.EqualTo(1));
        }

        [Test]
        public void Timeline_Hostile_IdleHonestStream_ExceptFloodRunWhichRuns()
        {
            Assert.That(new NetScriptTimeline(Parse("flood-input"), 0f).Evaluate(0.5f).MoveY, Is.EqualTo(0f));
            Assert.That(new NetScriptTimeline(Parse("nan-input"), 0f).Evaluate(0.5f).MoveY, Is.EqualTo(0f));
            Assert.That(new NetScriptTimeline(Parse("flood-run"), 0f).Evaluate(0.5f).MoveY, Is.EqualTo(1f));
            Assert.That(new NetScriptTimeline(Parse("flood-run"), 0f).Evaluate(0.5f).ActiveKind, Is.EqualTo(NetScriptKind.FloodRun));
        }
    }
}
