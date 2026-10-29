using System;

namespace Emberdelve.Items
{
    /// <summary>
    /// D3a: the rarity ladder as pure rules over the STORED integer (what a snapshot, a packet or an asset holds).
    /// Decision C7 (Docs/Design/DND_PARITY.md): Poor, Common, Uncommon, Rare, Epic, Legendary, Unique - Artifact is
    /// skipped. The stored ints are STABLE and append-only: Common=0, Uncommon=1, Rare=2 (the original ladder, never
    /// reinterpreted), then Epic=3, Legendary=4, Unique=5 and finally Poor=6 (Poor sits BELOW Common, so its stored
    /// int is not its rank).
    ///
    /// Because of that, a stored int is an identity and never an order. Everything that sorts, compares or asks "is at
    /// least Rare" goes through <see cref="TryOrdinal"/> (Poor 0, Common 1, Uncommon 2, Rare 3, Epic 4, Legendary 5,
    /// Unique 6) and never compares two raw ints with a relational operator. Membership is a lookup in the declared
    /// table, not a numeric range, so an unknown int (7, 99, -1) is a refusal and is never clamped or coerced.
    ///
    /// No UnityEngine and no other Emberdelve assembly: the Runtime enum <c>AffixRarity</c> is a thin typed view over
    /// these same ints, and the EditMode tests exercise this class directly.
    /// </summary>
    public static class ItemRarityRules
    {
        public const int StoredCommon = 0;
        public const int StoredUncommon = 1;
        public const int StoredRare = 2;
        public const int StoredEpic = 3;
        public const int StoredLegendary = 4;
        public const int StoredUnique = 5;
        public const int StoredPoor = 6;

        /// <summary>Number of tiers on the ladder.</summary>
        public const int TierCount = 7;

        /// <summary>The largest number of rolled affixes any tier carries (Unique).</summary>
        public const int MaxAffixCount = 5;

        // Index = ordinal (low to high). Value = the stored int of that tier.
        private static readonly int[] StoredByOrdinal =
        {
            StoredPoor, StoredCommon, StoredUncommon, StoredRare, StoredEpic, StoredLegendary, StoredUnique
        };

        // Index = ordinal. Poor 0, Common 0, Uncommon 1, Rare 2, Epic 3, Legendary 4, Unique 5 (C7).
        private static readonly int[] AffixCountByOrdinal = { 0, 0, 1, 2, 3, 4, 5 };

        private static readonly string[] NameByOrdinal =
        {
            "Poor", "Common", "Uncommon", "Rare", "Epic", "Legendary", "Unique"
        };

        /// <summary>The stored int of the tier at this rank (0 = Poor .. 6 = Unique). False outside 0..6.</summary>
        public static bool TryStoredAtOrdinal(int ordinal, out int stored)
        {
            if (ordinal < 0 || ordinal >= TierCount)
            {
                stored = -1;
                return false;
            }

            stored = StoredByOrdinal[ordinal];
            return true;
        }

        /// <summary>The rank of a stored int (Poor 0 .. Unique 6). False, with ordinal -1, for an unknown int.</summary>
        public static bool TryOrdinal(int stored, out int ordinal)
        {
            for (int i = 0; i < StoredByOrdinal.Length; i++)
            {
                if (StoredByOrdinal[i] != stored) continue;
                ordinal = i;
                return true;
            }

            ordinal = -1;
            return false;
        }

        /// <summary>True for exactly the seven declared stored ints.</summary>
        public static bool IsKnownStored(int stored)
        {
            return TryOrdinal(stored, out _);
        }

        /// <summary>
        /// Orders two stored rarities by tier: negative when a is the lower tier, 0 when equal, positive when higher.
        /// False (comparison 0) when either is unknown, so an unknown int never sorts anywhere.
        /// </summary>
        public static bool TryCompare(int storedA, int storedB, out int comparison)
        {
            comparison = 0;
            if (!TryOrdinal(storedA, out int a) || !TryOrdinal(storedB, out int b)) return false;
            comparison = a == b ? 0 : (a < b ? -1 : 1);
            return true;
        }

        /// <summary>True when both are known and <paramref name="stored"/> is the same tier as or above <paramref name="minimum"/>.</summary>
        public static bool IsAtLeast(int stored, int minimum)
        {
            return TryCompare(stored, minimum, out int comparison) && comparison >= 0;
        }

        /// <summary>Rolled affixes a tier carries: 0/0/1/2/3/4/5. False with count -1 for an unknown int, never 0.</summary>
        public static bool TryExpectedAffixCount(int stored, out int count)
        {
            if (!TryOrdinal(stored, out int ordinal))
            {
                count = -1;
                return false;
            }

            count = AffixCountByOrdinal[ordinal];
            return true;
        }

        /// <summary>Display name of a tier; "Unknown" for an unknown int.</summary>
        public static string Name(int stored)
        {
            return TryOrdinal(stored, out int ordinal) ? NameByOrdinal[ordinal] : "Unknown";
        }

        /// <summary>Human list of the stored ints for refusal messages (identical wording everywhere).</summary>
        public const string StoredLegend =
            "0 Common, 1 Uncommon, 2 Rare, 3 Epic, 4 Legendary, 5 Unique, 6 Poor";
    }

    /// <summary>
    /// D3a: gear score, computed from item rarity per slot (DND_PARITY.md "Gear score": computed from rarity, no
    /// matchmaking use). Compute and display only - nothing reads it for a rule.
    ///
    /// score = sum over the occupied slots of points(rarity) x slotWeight. Points follow the ladder's rank:
    /// Poor 1, Common 2, Uncommon 3, Rare 5, Epic 8, Legendary 13, Unique 21 (a Fibonacci run, so one better item
    /// is always worth more than a step up on a low-value slot). An empty slot contributes 0, which is why even a
    /// Poor item scores more than nothing. Slot weights belong to the caller (the Runtime maps its equipment slots
    /// to weights) so this class never learns the slot enum and D3b can add slots without touching it.
    /// </summary>
    public static class GearScoreRules
    {
        /// <summary>The value a caller passes for a slot that holds nothing.</summary>
        public const int EmptySlot = -1;

        public const int MinSlotWeight = 1;
        public const int MaxSlotWeight = 10;

        /// <summary>Hard cap on slots in one calculation; keeps the total far from int overflow.</summary>
        public const int MaxSlots = 64;

        // Index = ordinal (Poor .. Unique).
        private static readonly int[] PointsByOrdinal = { 1, 2, 3, 5, 8, 13, 21 };

        /// <summary>Points of one tier. False (points 0) for an unknown stored int.</summary>
        public static bool TryPoints(int storedRarity, out int points)
        {
            if (!ItemRarityRules.TryOrdinal(storedRarity, out int ordinal))
            {
                points = 0;
                return false;
            }

            points = PointsByOrdinal[ordinal];
            return true;
        }

        /// <summary>Score of one worn item: points(rarity) x slotWeight. False for an unknown rarity or a weight outside 1..10.</summary>
        public static bool TryScoreItem(int storedRarity, int slotWeight, out int score)
        {
            score = 0;
            if (slotWeight < MinSlotWeight || slotWeight > MaxSlotWeight) return false;
            if (!TryPoints(storedRarity, out int points)) return false;
            score = points * slotWeight;
            return true;
        }

        /// <summary>
        /// Total over parallel lists: entry i is the stored rarity worn in slot i (or <see cref="EmptySlot"/>) and the
        /// weight of that slot. Atomic: any unknown rarity, bad weight, null list or length mismatch returns false with a
        /// reason and a total of 0 - a partial score is never reported.
        /// </summary>
        public static bool TryTotal(System.Collections.Generic.IReadOnlyList<int> storedRarities,
            System.Collections.Generic.IReadOnlyList<int> slotWeights, out int total, out string reason)
        {
            total = 0;

            if (storedRarities == null || slotWeights == null)
            {
                reason = "gear score needs both a rarity list and a slot weight list";
                return false;
            }

            if (storedRarities.Count != slotWeights.Count)
            {
                reason = $"gear score lists differ in length ({storedRarities.Count} rarities, {slotWeights.Count} weights)";
                return false;
            }

            if (storedRarities.Count > MaxSlots)
            {
                reason = $"gear score covers at most {MaxSlots} slots (got {storedRarities.Count})";
                return false;
            }

            int sum = 0;
            for (int i = 0; i < storedRarities.Count; i++)
            {
                if (storedRarities[i] == EmptySlot) continue;

                if (!TryScoreItem(storedRarities[i], slotWeights[i], out int score))
                {
                    reason = $"slot {i}: rarity {storedRarities[i]} with weight {slotWeights[i]} cannot be scored";
                    return false;
                }

                sum += score;
            }

            total = sum;
            reason = null;
            return true;
        }
    }
}
