using Emberdelve.Net.Rules;
using NUnit.Framework;

namespace Emberdelve.NetRules.Tests
{
    public sealed class NetDevShoveTests
    {
        [Test]
        public void Parse_SlotYaw_DefaultsToTwoMeters()
        {
            Assert.That(NetDevShove.TryParse("mv-a:180", out NetDevShove s, out string error), Is.True, error);
            Assert.That(s.Slot, Is.EqualTo("mv-a"));
            Assert.That(s.YawDegrees, Is.EqualTo(-180f), "wrapped into [-180, 180)");
            Assert.That(s.Meters, Is.EqualTo(2f));
        }

        [Test]
        public void Parse_WithMeters_AndDirection()
        {
            Assert.That(NetDevShove.TryParse("x:90.0:3.5", out NetDevShove s, out _), Is.True);
            Assert.That(s.Meters, Is.EqualTo(3.5f));
            Assert.That(s.DirectionX, Is.EqualTo(1f).Within(1e-5f));
            Assert.That(s.DirectionZ, Is.EqualTo(0f).Within(1e-5f));

            Assert.That(NetDevShove.TryParse("x:0", out NetDevShove north, out _), Is.True);
            Assert.That(north.DirectionZ, Is.EqualTo(1f).Within(1e-5f));
        }

        [TestCase("")]
        [TestCase("   ")]
        [TestCase("slot")]
        [TestCase(":90")]
        [TestCase("s:abc")]
        [TestCase("s:NaN")]
        [TestCase("s:90:0")]
        [TestCase("s:90:-1")]
        [TestCase("s:90:11")]
        [TestCase("s:90:2:3")]
        public void Parse_Malformed_IsRefused(string text)
        {
            Assert.That(NetDevShove.TryParse(text, out NetDevShove s, out string error), Is.False);
            Assert.That(s, Is.Null);
            Assert.That(error, Is.Not.Empty);
        }

        [Test]
        public void LaunchArgs_ReadTheShoveSwitches()
        {
            NetLaunchArgs args = NetLaunchArgs.Parse(new[]
            {
                "-ember-net", "server", "-ember-net-dev-shove", "mv-a:270.0:2.0", "-ember-net-dev-shove-file", "C:\\run\\shove.flag"
            });

            Assert.That(args.IsValid, Is.True, string.Join(";", args.Errors));
            Assert.That(args.DevShove, Is.Not.Null);
            Assert.That(args.DevShove.Slot, Is.EqualTo("mv-a"));
            Assert.That(args.DevShove.Meters, Is.EqualTo(2f));
            Assert.That(args.DevShoveFile, Is.EqualTo("C:\\run\\shove.flag"));
        }

        [Test]
        public void LaunchArgs_DefaultsHaveNoShove_AndBadValuesAreErrors()
        {
            NetLaunchArgs none = NetLaunchArgs.Parse(new[] { "-ember-net", "server" });
            Assert.That(none.DevShove, Is.Null);
            Assert.That(none.DevShoveFile, Is.EqualTo(""));

            NetLaunchArgs bad = NetLaunchArgs.Parse(new[] { "-ember-net", "server", "-ember-net-dev-shove", "nonsense" });
            Assert.That(bad.IsValid, Is.False);
            Assert.That(bad.DevShove, Is.Null);
        }
    }
}
