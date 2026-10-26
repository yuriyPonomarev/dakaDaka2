using Emberdelve.Stats;
using NUnit.Framework;

namespace Emberdelve.Rules.Tests
{
    public sealed class AttributeRulesTests
    {
        [TestCase(0, 1)]
        [TestCase(1, 1)]
        [TestCase(30, 30)]
        [TestCase(31, 30)]
        public void ClampScore_UsesInclusiveOneToThirtyBounds(int input, int expected)
        {
            Assert.That(AttributeRules.ClampScore(input), Is.EqualTo(expected));
        }

        [TestCase(1, -5)]
        [TestCase(9, -1)]
        [TestCase(10, 0)]
        [TestCase(11, 0)]
        [TestCase(16, 3)]
        [TestCase(30, 10)]
        public void Modifier_UsesFloorForNegativeOddValues(int score, int expected)
        {
            Assert.That(AttributeRules.Modifier(score), Is.EqualTo(expected));
        }

        [Test]
        public void DefaultScores_ReadAsTheLegalMinimum()
        {
            AttributeScores scores = default;
            Assert.That(scores.ToArray(), Is.EqualTo(new[] { 1, 1, 1, 1, 1, 1 }));
        }

        [Test]
        public void SnapshotBonuses_AreDerivedFromThePristineBaseAndCanBeReplaced()
        {
            AttributeScores baseScores = new AttributeScores(10, 14, 8, 12, 16, 10);
            AttributeSnapshot baseSnapshot = new AttributeSnapshot(baseScores);
            AttributeSnapshot equipped = baseSnapshot.WithBonuses(new AttributeBonuses(2, 0, 3, 0, 0, 0));
            AttributeSnapshot replaced = equipped.WithBonuses(new AttributeBonuses(1, 0, 1, 0, 0, 0));

            Assert.That(equipped.Effective.Strength, Is.EqualTo(12));
            Assert.That(equipped.Effective.Constitution, Is.EqualTo(11));
            Assert.That(replaced.Effective.Strength, Is.EqualTo(11));
            Assert.That(replaced.Effective.Constitution, Is.EqualTo(9));
            Assert.That(baseSnapshot.Effective.Strength, Is.EqualTo(10));
            Assert.That(baseSnapshot.Effective.Constitution, Is.EqualTo(8));
        }

        [Test]
        public void Derive_AppliesLiteralPoolRegenAndSpeedValues()
        {
            AttributeScores scores = new AttributeScores(10, 14, 8, 12, 16, 10);
            DerivedStatBases bases = new DerivedStatBases(100, 50, 80, 3f, 2f, 4f);
            DerivedStats result = AttributeRules.Derive(scores, bases);

            Assert.That(result.MaxHealth, Is.EqualTo(90));
            Assert.That(result.MaxMana, Is.EqualTo(55));
            Assert.That(result.MaxStamina, Is.EqualTo(75));
            Assert.That(result.ManaRegen, Is.EqualTo(3.75f).Within(0.0001f));
            Assert.That(result.StaminaRegen, Is.EqualTo(1.5f).Within(0.0001f));
            Assert.That(result.MoveSpeed, Is.EqualTo(4.2f).Within(0.0001f));
        }

        [Test]
        public void KeyParser_AcceptsOnlySixAttributeNames()
        {
            Assert.That(AttributeRules.TryParseKey(" Dexterity ", out SixAttribute parsed), Is.True);
            Assert.That(parsed, Is.EqualTo(SixAttribute.Dexterity));
            Assert.That(AttributeRules.TryParseKey("armor", out parsed), Is.False);
            Assert.That(AttributeRules.TryParseKey(null, out parsed), Is.False);
        }
    }
}
