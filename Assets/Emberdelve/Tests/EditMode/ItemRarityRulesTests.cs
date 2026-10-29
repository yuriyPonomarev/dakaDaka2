using System;
using System.Collections.Generic;
using Emberdelve.Items;
using NUnit.Framework;

namespace Emberdelve.Rules.Tests
{
    /// <summary>D3a: the rarity ladder as pure rules over the stored int. Expected values are literals, not read back from the code under test.</summary>
    public sealed class ItemRarityRulesTests
    {
        // stored int, ordinal (Poor 0 .. Unique 6), rolled affixes, name
        private static readonly object[][] Ladder =
        {
            new object[] { 6, 0, 0, "Poor" },
            new object[] { 0, 1, 0, "Common" },
            new object[] { 1, 2, 1, "Uncommon" },
            new object[] { 2, 3, 2, "Rare" },
            new object[] { 3, 4, 3, "Epic" },
            new object[] { 4, 5, 4, "Legendary" },
            new object[] { 5, 6, 5, "Unique" },
        };

        [Test]
        public void TheSevenStoredIntsAreStable()
        {
            Assert.That(ItemRarityRules.StoredCommon, Is.EqualTo(0));
            Assert.That(ItemRarityRules.StoredUncommon, Is.EqualTo(1));
            Assert.That(ItemRarityRules.StoredRare, Is.EqualTo(2));
            Assert.That(ItemRarityRules.StoredEpic, Is.EqualTo(3));
            Assert.That(ItemRarityRules.StoredLegendary, Is.EqualTo(4));
            Assert.That(ItemRarityRules.StoredUnique, Is.EqualTo(5));
            Assert.That(ItemRarityRules.StoredPoor, Is.EqualTo(6));
            Assert.That(ItemRarityRules.TierCount, Is.EqualTo(7));
            Assert.That(ItemRarityRules.MaxAffixCount, Is.EqualTo(5));
        }

        [Test]
        public void EveryTierHasItsOrdinalCountAndName()
        {
            foreach (object[] row in Ladder)
            {
                int stored = (int)row[0];
                Assert.That(ItemRarityRules.TryOrdinal(stored, out int ordinal), Is.True, "stored " + stored);
                Assert.That(ordinal, Is.EqualTo((int)row[1]), "ordinal of " + row[3]);
                Assert.That(ItemRarityRules.TryExpectedAffixCount(stored, out int count), Is.True);
                Assert.That(count, Is.EqualTo((int)row[2]), "affixes of " + row[3]);
                Assert.That(ItemRarityRules.Name(stored), Is.EqualTo((string)row[3]));
                Assert.That(ItemRarityRules.TryStoredAtOrdinal(ordinal, out int back), Is.True);
                Assert.That(back, Is.EqualTo(stored), "round trip through the ordinal");
                Assert.That(ItemRarityRules.IsKnownStored(stored), Is.True);
            }
        }

        [TestCase(7)]
        [TestCase(8)]
        [TestCase(99)]
        [TestCase(-1)]
        [TestCase(int.MaxValue)]
        [TestCase(int.MinValue)]
        public void AnUnknownStoredIntIsRefusedNotClamped(int stored)
        {
            Assert.That(ItemRarityRules.IsKnownStored(stored), Is.False);
            Assert.That(ItemRarityRules.TryOrdinal(stored, out int ordinal), Is.False);
            Assert.That(ordinal, Is.EqualTo(-1));
            Assert.That(ItemRarityRules.TryExpectedAffixCount(stored, out int count), Is.False);
            Assert.That(count, Is.EqualTo(-1), "an unknown tier never reads as 0 affixes");
            Assert.That(ItemRarityRules.Name(stored), Is.EqualTo("Unknown"));
            Assert.That(ItemRarityRules.TryCompare(stored, ItemRarityRules.StoredCommon, out int comparison), Is.False);
            Assert.That(comparison, Is.EqualTo(0));
            Assert.That(ItemRarityRules.IsAtLeast(stored, ItemRarityRules.StoredPoor), Is.False);
        }

        [Test]
        public void OrdinalsOutsideTheLadderMapToNothing()
        {
            Assert.That(ItemRarityRules.TryStoredAtOrdinal(-1, out int stored), Is.False);
            Assert.That(stored, Is.EqualTo(-1));
            Assert.That(ItemRarityRules.TryStoredAtOrdinal(7, out _), Is.False);
        }

        [Test]
        public void PoorIsBelowCommonEvenThoughItsStoredIntIsHigher()
        {
            Assert.That(ItemRarityRules.StoredPoor > ItemRarityRules.StoredCommon, Is.True, "premise: Poor is stored last");
            Assert.That(ItemRarityRules.TryCompare(ItemRarityRules.StoredPoor, ItemRarityRules.StoredCommon, out int comparison), Is.True);
            Assert.That(comparison, Is.LessThan(0));
            Assert.That(ItemRarityRules.IsAtLeast(ItemRarityRules.StoredPoor, ItemRarityRules.StoredCommon), Is.False);
            Assert.That(ItemRarityRules.IsAtLeast(ItemRarityRules.StoredCommon, ItemRarityRules.StoredPoor), Is.True);
        }

        [Test]
        public void SortingByOrdinalGivesTheLadderOrderAndSortingByStoredIntDoesNot()
        {
            int[] stored = { 3, 6, 5, 0, 2, 4, 1 };

            int[] byStored = (int[])stored.Clone();
            Array.Sort(byStored);
            Assert.That(byStored, Is.EqualTo(new[] { 0, 1, 2, 3, 4, 5, 6 }), "raw ints sort Poor (6) last");

            int[] byTier = (int[])stored.Clone();
            Array.Sort(byTier, (a, b) =>
            {
                Assert.That(ItemRarityRules.TryCompare(a, b, out int c), Is.True);
                return c;
            });
            Assert.That(byTier, Is.EqualTo(new[] { 6, 0, 1, 2, 3, 4, 5 }), "Poor, Common, Uncommon, Rare, Epic, Legendary, Unique");
        }

        [Test]
        public void CompareAndIsAtLeastAgreeWithTheOrdinalForEveryPair()
        {
            for (int i = 0; i < ItemRarityRules.TierCount; i++)
            {
                for (int k = 0; k < ItemRarityRules.TierCount; k++)
                {
                    ItemRarityRules.TryStoredAtOrdinal(i, out int a);
                    ItemRarityRules.TryStoredAtOrdinal(k, out int b);
                    Assert.That(ItemRarityRules.TryCompare(a, b, out int comparison), Is.True);
                    Assert.That(Math.Sign(comparison), Is.EqualTo(Math.Sign(i - k)), i + " vs " + k);
                    Assert.That(ItemRarityRules.IsAtLeast(a, b), Is.EqualTo(i >= k), i + " at least " + k);
                }
            }
        }

        [Test]
        public void AffixCountsAreMonotonicByTierAndStopAtFive()
        {
            int previous = -1;
            for (int ordinal = 0; ordinal < ItemRarityRules.TierCount; ordinal++)
            {
                ItemRarityRules.TryStoredAtOrdinal(ordinal, out int stored);
                ItemRarityRules.TryExpectedAffixCount(stored, out int count);
                Assert.That(count, Is.GreaterThanOrEqualTo(previous));
                Assert.That(count, Is.LessThanOrEqualTo(ItemRarityRules.MaxAffixCount));
                previous = count;
            }
            Assert.That(previous, Is.EqualTo(5));
        }
    }

    /// <summary>D3a: gear score arithmetic. Literals only.</summary>
    public sealed class GearScoreRulesTests
    {
        private static readonly int[] Weights = { 2, 1, 1, 1, 1, 1 }; // Weapon, OffHand, Head, Chest, Legs, Back
        private const int E = GearScoreRules.EmptySlot;

        [TestCase(6, 1)]
        [TestCase(0, 2)]
        [TestCase(1, 3)]
        [TestCase(2, 5)]
        [TestCase(3, 8)]
        [TestCase(4, 13)]
        [TestCase(5, 21)]
        public void PointsFollowTheLadder(int stored, int expected)
        {
            Assert.That(GearScoreRules.TryPoints(stored, out int points), Is.True);
            Assert.That(points, Is.EqualTo(expected));
        }

        [TestCase(7)]
        [TestCase(-1)]
        [TestCase(99)]
        public void AnUnknownTierHasNoPoints(int stored)
        {
            Assert.That(GearScoreRules.TryPoints(stored, out int points), Is.False);
            Assert.That(points, Is.EqualTo(0));
        }

        [Test]
        public void ItemScoreIsPointsTimesSlotWeight()
        {
            Assert.That(GearScoreRules.TryScoreItem(2, 2, out int score), Is.True);
            Assert.That(score, Is.EqualTo(10));
            Assert.That(GearScoreRules.TryScoreItem(2, 1, out score), Is.True);
            Assert.That(score, Is.EqualTo(5));
            Assert.That(GearScoreRules.TryScoreItem(2, 0, out score), Is.False);
            Assert.That(GearScoreRules.TryScoreItem(2, 11, out score), Is.False);
            Assert.That(GearScoreRules.TryScoreItem(7, 1, out score), Is.False);
        }

        [Test]
        public void TotalsMatchHandComputedLiterals()
        {
            Assert.That(Total(new[] { E, E, E, E, E, E }), Is.EqualTo(0), "nothing worn");
            Assert.That(Total(new[] { 0, 0, 0, 0, 0, 0 }), Is.EqualTo(14), "six Common: 2*2 + 5*2");
            Assert.That(Total(new[] { 6, 6, 6, 6, 6, 6 }), Is.EqualTo(7), "six Poor: 1*2 + 5*1");
            Assert.That(Total(new[] { 5, 5, 5, 5, 5, 5 }), Is.EqualTo(147), "six Unique: 21*2 + 21*5");
            Assert.That(Total(new[] { 2, E, 6, 3, 1, 4 }), Is.EqualTo(35), "Rare 10 + Poor 1 + Epic 8 + Uncommon 3 + Legendary 13");
        }

        [Test]
        public void ABetterTierNeverScoresLessInTheSameSlot()
        {
            int previous = 0;
            for (int ordinal = 0; ordinal < ItemRarityRules.TierCount; ordinal++)
            {
                ItemRarityRules.TryStoredAtOrdinal(ordinal, out int stored);
                Assert.That(GearScoreRules.TryScoreItem(stored, 1, out int score), Is.True);
                Assert.That(score, Is.GreaterThan(previous), "tier " + ordinal);
                previous = score;
            }
        }

        [Test]
        public void ABadInputRefusesTheWholeScore()
        {
            Assert.That(GearScoreRules.TryTotal(new[] { 7, 0, 0, 0, 0, 0 }, Weights, out int total, out string reason), Is.False);
            Assert.That(total, Is.EqualTo(0), "no partial total");
            Assert.That(reason, Is.Not.Null.And.Not.Empty);
            Assert.That(GearScoreRules.TryTotal(new[] { 0, 0 }, new[] { 1 }, out total, out reason), Is.False);
            Assert.That(GearScoreRules.TryTotal(null, Weights, out total, out reason), Is.False);
            Assert.That(GearScoreRules.TryTotal(new[] { 0 }, new[] { 0 }, out total, out reason), Is.False);
            Assert.That(GearScoreRules.TryTotal(new int[GearScoreRules.MaxSlots + 1], new int[GearScoreRules.MaxSlots + 1], out total, out reason), Is.False);
        }

        [Test]
        public void ASkippedEmptySlotDoesNotNeedAValidWeight()
        {
            Assert.That(GearScoreRules.TryTotal(new[] { E, 0 }, new[] { 0, 1 }, out int total, out _), Is.True);
            Assert.That(total, Is.EqualTo(2));
        }

        private static int Total(int[] rarities)
        {
            Assert.That(GearScoreRules.TryTotal(rarities, Weights, out int total, out string reason), Is.True, reason);
            return total;
        }
    }
}
