using Emberdelve.Net.Rules;
using NUnit.Framework;

namespace Emberdelve.NetRules.Tests
{
    public sealed class CombatVitalsDataTests
    {
        [Test]
        public void NonFiniteAndNegativeNumbersBecomeZero()
        {
            var v = new CombatVitalsData(float.NaN, -5f, false);
            Assert.That(v.Health, Is.EqualTo(0f));
            Assert.That(v.MaxHealth, Is.EqualTo(0f));
            var w = new CombatVitalsData(float.PositiveInfinity, 100f, false);
            Assert.That(w.Health, Is.EqualTo(0f));
        }

        [Test]
        public void HealthNeverExceedsTheMaximum()
        {
            var v = new CombatVitalsData(150f, 100f, false);
            Assert.That(v.Health, Is.EqualTo(100f));
        }

        [Test]
        public void QuantizingRoundsToTenthsSoRegenerationDoesNotChurnTheVariable()
        {
            var a = new CombatVitalsData(57.01f, 100f, false).Quantized();
            var b = new CombatVitalsData(56.96f, 100f, false).Quantized();
            Assert.That(a.Equals(b), Is.True);
            var c = new CombatVitalsData(57.2f, 100f, false).Quantized();
            Assert.That(a.Equals(c), Is.False);
            Assert.That(a.GetHashCode(), Is.EqualTo(b.GetHashCode()));
        }

        [Test]
        public void DeadFlagParticipatesInEquality()
        {
            var alive = new CombatVitalsData(0f, 100f, false);
            var dead = new CombatVitalsData(0f, 100f, true);
            Assert.That(alive.Equals(dead), Is.False);
        }
    }
}
