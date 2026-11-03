using Emberdelve.Net.Rules;
using NUnit.Framework;

namespace Emberdelve.NetRules.Tests
{
    public sealed class CombatScriptParamsTests
    {
        [Test]
        public void EmptyAndBareWordContentGiveDefaults()
        {
            Assert.That(CombatScriptParams.TryParse("", out CombatScriptParams a, out string ea), Is.True);
            Assert.That(ea, Is.Null);
            Assert.That(a.Presses, Is.EqualTo(CombatScriptParams.Default.Presses));
            Assert.That(a.Shots, Is.EqualTo(1));
            Assert.That(CombatScriptParams.TryParse("go", out CombatScriptParams b, out _), Is.True);
            Assert.That(b.HasWalk, Is.False);
            Assert.That(CombatScriptParams.TryParse(null, out _, out _), Is.True);
        }

        [Test]
        public void KeyValuePairsAreReadWithInvariantCulture()
        {
            Assert.That(CombatScriptParams.TryParse("go stop=1.75 interval=0.9;presses=7 hold=1.3 early=0.05 away=14.5 pspeed=40.5 pgrav=9.8 shots=2 aimheight=1.2",
                out CombatScriptParams p, out string error), Is.True, error);
            Assert.That(p.Stop, Is.EqualTo(1.75f));
            Assert.That(p.Interval, Is.EqualTo(0.9f));
            Assert.That(p.Presses, Is.EqualTo(7));
            Assert.That(p.Hold, Is.EqualTo(1.3f));
            Assert.That(p.Early, Is.EqualTo(0.05f));
            Assert.That(p.Away, Is.EqualTo(14.5f));
            Assert.That(p.ProjectileSpeed, Is.EqualTo(40.5f));
            Assert.That(p.ProjectileGravity, Is.EqualTo(9.8f));
            Assert.That(p.Shots, Is.EqualTo(2));
            Assert.That(p.AimHeight, Is.EqualTo(1.2f));
        }

        [Test]
        public void WalkAndPitchSetTheirFlags()
        {
            Assert.That(CombatScriptParams.TryParse("walk=90:2.5 pitch=-4.5", out CombatScriptParams p, out _), Is.True);
            Assert.That(p.HasWalk, Is.True);
            Assert.That(p.WalkYaw, Is.EqualTo(90f));
            Assert.That(p.WalkSeconds, Is.EqualTo(2.5f));
            Assert.That(p.HasPitch, Is.True);
            Assert.That(p.Pitch, Is.EqualTo(-4.5f));
        }

        [TestCase("stop=abc")]
        [TestCase("stop=0")]
        [TestCase("stop=NaN")]
        [TestCase("presses=0")]
        [TestCase("presses=2000")]
        [TestCase("interval=-1")]
        [TestCase("pitch=120")]
        [TestCase("walk=90")]
        [TestCase("walk=x:2")]
        [TestCase("shots=0")]
        public void BadValuesFailWithAMessageAndNeverThrow(string text)
        {
            Assert.That(CombatScriptParams.TryParse(text, out CombatScriptParams p, out string error), Is.False);
            Assert.That(error, Is.Not.Null.And.Not.Empty);
            Assert.That(p, Is.Not.Null);
            Assert.That(p.HasWalk, Is.False);
        }

        [Test]
        public void AwayYawIsOptionalAndNumeric()
        {
            Assert.That(CombatScriptParams.TryParse("away=12", out CombatScriptParams a, out _), Is.True);
            Assert.That(a.HasAwayYaw, Is.False);
            Assert.That(CombatScriptParams.TryParse("away=12 awayyaw=90", out CombatScriptParams b, out _), Is.True);
            Assert.That(b.HasAwayYaw, Is.True);
            Assert.That(b.AwayYaw, Is.EqualTo(90f));
            Assert.That(CombatScriptParams.TryParse("awayyaw=east", out _, out string error), Is.False);
            Assert.That(error, Is.Not.Empty);
        }

        [Test]
        public void UnknownKeysAreIgnored()
        {
            Assert.That(CombatScriptParams.TryParse("future=1 stop=2", out CombatScriptParams p, out _), Is.True);
            Assert.That(p.Stop, Is.EqualTo(2f));
        }
    }
}
