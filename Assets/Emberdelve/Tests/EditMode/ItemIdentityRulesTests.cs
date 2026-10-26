using System;
using Emberdelve.Items;
using NUnit.Framework;

namespace Emberdelve.Rules.Tests
{
    public sealed class ItemIdentityRulesTests
    {
        [TestCase("ember_sword", true)]
        [TestCase("item_2", true)]
        [TestCase("a", true)]
        [TestCase("", false)]
        [TestCase(" Ember_sword", false)]
        [TestCase("Ember_sword", false)]
        [TestCase("_sword", false)]
        [TestCase("sword-name", false)]
        [TestCase("sword.name", false)]
        public void IsStableItemId_AcceptsOnlyCanonicalLiteralIds(string itemId, bool expected)
        {
            bool valid = ItemIdentityRules.IsStableItemId(itemId, out string reason);

            Assert.That(valid, Is.EqualTo(expected));
            Assert.That(valid ? reason == null : !string.IsNullOrEmpty(reason), Is.True);
        }

        [Test]
        public void IsStableItemId_AcceptsThe64CharacterBoundary()
        {
            string maxLength = new string('a', ItemIdentityRules.MaxItemIdLength);

            Assert.That(maxLength.Length, Is.EqualTo(64));
            Assert.That(ItemIdentityRules.IsStableItemId(maxLength, out string reason), Is.True);
            Assert.That(reason, Is.Null);
        }

        [Test]
        public void IsStableItemId_RejectsNullWithLiteralReason()
        {
            Assert.That(ItemIdentityRules.IsStableItemId(null, out string reason), Is.False);
            Assert.That(reason, Is.EqualTo("item definition id is empty"));
        }
        [Test]
        public void IsStableItemId_RejectsOverlongAndWhitespaceIdsWithLiteralReasons()
        {
            string overlong = new string('a', 65);

            Assert.That(ItemIdentityRules.IsStableItemId(overlong, out string longReason), Is.False);
            Assert.That(longReason, Is.EqualTo("item definition id '" + overlong + "' is longer than 64 characters"));
            Assert.That(ItemIdentityRules.IsStableItemId("sword ", out string whitespaceReason), Is.False);
            Assert.That(whitespaceReason, Is.EqualTo("item definition id 'sword ' has surrounding whitespace"));
        }

        [Test]
        public void CanonicalUid_UsesLowercaseGuidNAndEmptyGuidHasNoIdentity()
        {
            Guid uid = Guid.Parse("07387a15-8fa4-4daa-9573-529b34a1d093");

            Assert.That(ItemIdentityRules.CanonicalUid(uid), Is.EqualTo("07387a158fa44daa9573529b34a1d093"));
            Assert.That(ItemIdentityRules.CanonicalUid(Guid.Empty), Is.EqualTo(string.Empty));
            Assert.That(ItemIdentityRules.UidCanonicalLength, Is.EqualTo(32));
        }

        [TestCase("07387a158fa44daa9573529b34a1d093", true)]
        [TestCase("00000000000000000000000000000000", false)]
        [TestCase("07387A158FA44DAA9573529B34A1D093", false)]
        [TestCase("07387a15-8fa4-4daa-9573-529b34a1d093", false)]
        [TestCase("07387a158fa44daa9573529b34a1d09", false)]
        [TestCase(" 07387a158fa44daa9573529b34a1d093", false)]
        [TestCase(null, false)]
        public void TryReadUid_RequiresExactLowercaseGuidN(string text, bool expected)
        {
            bool valid = ItemIdentityRules.TryReadUid(text, out Guid uid, out string reason);

            Assert.That(valid, Is.EqualTo(expected));
            if (valid)
            {
                Assert.That(uid.ToString("N"), Is.EqualTo("07387a158fa44daa9573529b34a1d093"));
                Assert.That(reason, Is.Null);
            }
            else
            {
                Assert.That(uid, Is.EqualTo(Guid.Empty));
                Assert.That(reason, Is.Not.Null.And.Not.Empty);
            }
        }

        [Test]
        public void TryReadUid_RefusesEmptyIdentityAndMalformedLengthWithLiteralReasons()
        {
            Assert.That(ItemIdentityRules.TryReadUid("00000000000000000000000000000000", out _, out string emptyReason), Is.False);
            Assert.That(emptyReason, Is.EqualTo("item uid is the all-zero identity, which means no item at all"));
            Assert.That(ItemIdentityRules.TryReadUid("bad", out _, out string lengthReason), Is.False);
            Assert.That(lengthReason, Is.EqualTo("item uid 'bad' is not the canonical 32-character Guid 'N' form"));
        }
    }
}
