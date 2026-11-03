using Emberdelve.Net.Rules;
using NUnit.Framework;

namespace Emberdelve.NetRules.Tests
{
    /// <summary>U3B2: the replicated skill picture and the client view model (replicated values only, no local simulation).</summary>
    public sealed class SkillStateDataTests
    {
        [Test]
        public void State_SanitizesNonFiniteAndNegativeValues()
        {
            var s = new SkillStateData(float.NaN, float.PositiveInfinity, -1f, 2f, float.NegativeInfinity, 7f);
            Assert.That(s.Mana, Is.EqualTo(0f));
            Assert.That(s.MaxMana, Is.EqualTo(0f));
            Assert.That(s.Cooldown0, Is.EqualTo(0f));
            Assert.That(s.Cooldown1, Is.EqualTo(2f));
            Assert.That(s.Duration0, Is.EqualTo(0f));
            Assert.That(s.Duration1, Is.EqualTo(7f));
            Assert.That(s.Cooldown(5), Is.EqualTo(0f));
            Assert.That(s.Duration(-1), Is.EqualTo(0f));
        }

        [Test]
        public void State_SlotAccessorsAndEquality()
        {
            var a = new SkillStateData(38f, 70f, 6.5f, 0f, 7f, 14f);
            var b = new SkillStateData(38f, 70f, 6.5f, 0f, 7f, 14f);
            var c = new SkillStateData(38f, 70f, 6.4f, 0f, 7f, 14f);
            Assert.That(a.Cooldown(0), Is.EqualTo(6.5f));
            Assert.That(a.Cooldown(1), Is.EqualTo(0f));
            Assert.That(a.Duration(1), Is.EqualTo(14f));
            Assert.That(a.Equals(b), Is.True);
            Assert.That(a.GetHashCode(), Is.EqualTo(b.GetHashCode()));
            Assert.That(a.Equals(c), Is.False);
            Assert.That(a.Equals((object)b), Is.True);
        }

        [Test]
        public void State_QuantizedChangesOnlyWhenSomethingVisibleChanged()
        {
            var a = new SkillStateData(38.04f, 70f, 6.52f, 0f, 7f, 14f).Quantized();
            var b = new SkillStateData(38.01f, 70f, 6.54f, 0f, 7f, 14f).Quantized();
            var c = new SkillStateData(38.3f, 70f, 6.54f, 0f, 7f, 14f).Quantized();
            Assert.That(a.Equals(b), Is.True);
            Assert.That(a.Equals(c), Is.False);
            Assert.That(a.Mana, Is.EqualTo(38f).Within(1e-4f));
        }

        [Test]
        public void ViewModel_StartsEmptyAndKeepsExactlyWhatTheServerSent()
        {
            var view = new SkillViewModel();
            Assert.That(view.HasState, Is.False);
            Assert.That(view.CooldownRemaining(0, 5.0), Is.EqualTo(0f));
            Assert.That(view.CooldownFraction(0, 5.0), Is.EqualTo(0f));

            view.Apply(new SkillStateData(38f, 70f, 7f, 14f, 7f, 14f), 100.0);
            Assert.That(view.HasState, Is.True);
            Assert.That(view.Updates, Is.EqualTo(1));
            Assert.That(view.Mana, Is.EqualTo(38f));
            Assert.That(view.MaxMana, Is.EqualTo(70f));
        }

        [Test]
        public void ViewModel_ManaIsNeverSimulated_CooldownCountsDownBetweenUpdatesOnly()
        {
            var view = new SkillViewModel();
            view.Apply(new SkillStateData(38f, 70f, 7f, 14f, 7f, 14f), 100.0);

            // Ten seconds later with no update: mana is still the server's number (no local regeneration).
            Assert.That(view.Mana, Is.EqualTo(38f));
            Assert.That(view.CooldownRemaining(0, 103.5), Is.EqualTo(3.5f).Within(1e-4f));
            Assert.That(view.CooldownRemaining(1, 103.5), Is.EqualTo(10.5f).Within(1e-4f));
            Assert.That(view.CooldownRemaining(0, 110.0), Is.EqualTo(0f), "never negative");
            Assert.That(view.CooldownFraction(1, 107.0), Is.EqualTo(0.5f).Within(1e-4f));
            Assert.That(view.CooldownRemaining(0, 90.0), Is.EqualTo(7f), "a clock that went backwards does not extend a cooldown");

            // A new update re-anchors everything.
            view.Apply(new SkillStateData(44f, 70f, 0f, 9f, 7f, 14f), 120.0);
            Assert.That(view.Mana, Is.EqualTo(44f));
            Assert.That(view.CooldownRemaining(1, 122.0), Is.EqualTo(7f).Within(1e-4f));
            Assert.That(view.Updates, Is.EqualTo(2));
        }

        [Test]
        public void ViewModel_FractionWithoutADurationIsZero()
        {
            var view = new SkillViewModel();
            view.Apply(new SkillStateData(1f, 1f, 5f, 5f, 0f, 0f), 0.0);
            Assert.That(view.CooldownFraction(0, 0.0), Is.EqualTo(0f));
        }

        [Test]
        public void RefusalAndOutcomeCodes_AreStableWireValues()
        {
            Assert.That((byte)SkillRefusalCode.None, Is.EqualTo(0));
            Assert.That((byte)SkillRefusalCode.Cooldown, Is.EqualTo(5));
            Assert.That((byte)SkillRefusalCode.Mana, Is.EqualTo(6));
            Assert.That((byte)SkillRefusalCode.ClientRole, Is.EqualTo(14));
            Assert.That((byte)SkillCastOutcome.Cast, Is.EqualTo(0));
            Assert.That((byte)SkillCastOutcome.Refused, Is.EqualTo(1));
        }
    }
}
