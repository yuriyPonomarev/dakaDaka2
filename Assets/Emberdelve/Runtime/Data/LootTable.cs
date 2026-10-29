using System.Collections.Generic;
using UnityEngine;
using Emberdelve.Core;
using Emberdelve.Items;

namespace Emberdelve.Data
{
    /// <summary>One possible drop: an item, how many of it, how likely it is, how many times it may
    /// appear in a single chest so one chest cannot hand out six identical breastplates, and - new in U2B9 -
    /// the rarity it is rolled at. Every field keeps its old meaning. What U2B9-R1 corrected is <em>which</em>
    /// drops come back as concrete items: that is decided by the item's own category (worn/held gear versus
    /// carried goods), not by the rarity. An entry authored before any of this existed still names the same
    /// item at the same amount and is still drawn by the same seeded picks - a Common breastplate is simply
    /// one breastplate with its own identity now, instead of a definition plus a count.</summary>
    [System.Serializable]
    public class LootEntry
    {
        public ItemData item;
        [Min(1)] public int minAmount = 1;
        [Min(1)] public int maxAmount = 1;
        [Min(0)] public int weight = 1;
        [Min(1)] public int maxCopies = 1;

        [Tooltip("The rarity this entry is rolled at. Poor (6) and Common (0) roll no affixes and consume no affix " +
                 "RNG and keep the item's own fixed stats. Uncommon (1), Rare (2), Epic (3), Legendary (4) and Unique " +
                 "(5) roll 1/2/3/4/5 accepted affixes from this table's catalog. When the table declares a rarity " +
                 "ladder, a worn-gear unit takes its rarity from the ladder instead and this field is only a baseline. " +
                 "Rarity says nothing about whether the drop is a concrete " +
                 "item: worn gear is one concrete item with its own UID at every rarity, Common included, and " +
                 "carried goods stay a definition plus the amount the table drew.")]
        public AffixRarity affixRarity = AffixRarity.Common;

        public int EffectiveMin => Mathf.Max(1, minAmount);
        public int EffectiveMax => Mathf.Max(EffectiveMin, maxAmount);
        public int EffectiveWeight => Mathf.Max(0, weight);
        public int EffectiveCopies => Mathf.Max(1, maxCopies);

        /// <summary>The affix question, and only the affix question: true when this entry declares a tier that
        /// rolls at least one affix (Uncommon and above) from the table's catalog. Poor and Common roll none, and an
        /// unknown value is not "rolled gear" (the validators refuse it separately). It deliberately does NOT
        /// say whether the drop is a concrete item - Poor/Common gear is a concrete item too, and carried goods
        /// never roll affixes at any rarity.</summary>
        public bool RequestsRolledGear => AffixRarities.RollsAffixes(affixRarity);

        /// <summary>The identity question: true when every unit this entry drops is produced as one concrete
        /// <see cref="Emberdelve.Items.ItemInstance"/> with its own UID, because the item is worn or held gear.
        /// Worn gear is one item per unit at Common, Uncommon and Rare alike; carried goods (currency,
        /// consumables, relics) stay a definition plus the amount the table drew.</summary>
        public bool RequestsGearInstance => item != null && item.IsEquippable;
    }

    /// <summary>D3a: one rung of a table's rarity ladder - a tier and its relative weight.</summary>
    [System.Serializable]
    public class RarityWeight
    {
        public AffixRarity rarity = AffixRarity.Common;
        [Min(0)] public int weight = 1;

        public RarityWeight()
        {
        }

        public RarityWeight(AffixRarity rarity, int weight)
        {
            this.rarity = rarity;
            this.weight = weight;
        }
    }

    /// <summary>
    /// What a container can hold, as data.
    ///
    /// Rolls are seeded rather than free-running: the same table with the same seed always produces the same
    /// loot, which is what makes a chest reproducible for a player who reloads a run, and testable without a
    /// random-number harness. A future server rolls the same table from a server-owned seed and the client
    /// only displays the result.
    ///
    /// U2B9 added the second half of what a table can drop; U2B9-R1 finished it. The split is by what the item
    /// <em>is</em>, not by how lucky it got:
    /// <list type="bullet">
    /// <item>a carried entry (currency, consumables, relics) is still a definition plus the amount the table
    /// drew, on the counted partial path, exactly as every table authored before this slice;</item>
    /// <item>an equippable entry is one concrete <see cref="Emberdelve.Items.ItemInstance"/> per unit, Common
    /// included: the table builds each unit once through the accepted seeded
    /// <see cref="Emberdelve.Items.LootRoller"/> and issuer, against the run's one item catalog (R3, the table only restricts what it may roll from it),
    /// at the rarity that entry declared. A Common gear unit mints one UID, carries zero affix records, keeps
    /// the definition's fixed stats and consumes no affix RNG at all; an Uncommon or Rare unit additionally
    /// rolls that many accepted affixes, which is what this table's <see cref="affixCatalog"/> is for.</item>
    /// </list>
    /// An amount N of gear is N quantity-1 instances with N distinct UIDs - never one UID pasted onto an
    /// aggregated stack, and never N units quietly reduced to one. Whether a table is allowed to make the items
    /// it selected is decided before anything is produced, and a table that is not refuses the whole roll with
    /// a reason instead of dropping an approximation of it.
    /// </summary>
    [CreateAssetMenu(menuName = "Emberdelve/08 Loot Table", fileName = "LootTable")]
    public class LootTable : ScriptableObject
    {
        [Header("Identity")]
        public string tableId = "loot_table";

        [Header("Contents")]
        public LootEntry[] entries;

        [Header("Rolled gear catalog (U2B9)")]
        [Tooltip("The affix definitions this table is allowed to roll onto gear. Empty (the default) means no " +
                 "affixes may be rolled here: carried goods still drop normally and Common gear still gets its " +
                 "own UID, it just carries none of these. Entries that ask for Uncommon or Rare gear are " +
                 "refused when this catalog is empty - never downgraded to Common.")]
        public AffixDefinition[] affixCatalog;

        [Header("Rarity ladder (D3a)")]
        [Tooltip("Optional. Empty (the default, and every table authored before D3a) means each gear entry rolls at " +
                 "the rarity it declares and nothing here is read. When set, every worn-gear unit this table drops " +
                 "draws its rarity from these weights, one draw per unit on its own seeded stream (RarityStream), " +
                 "so the shape and affix streams - and therefore which items drop and what an Uncommon/Rare unit " +
                 "rolls - are untouched. One rung per tier, weights are relative, a weight of 0 disables a tier.")]
        public RarityWeight[] rarityLadder;

        [Header("Roll shape")]
        [Tooltip("How many separate drops this table may produce for one container.")]
        [Min(1)] public int maxRolls = 3;
        [Tooltip("The first entry is always handed out once, so a container can be guaranteed to contain something specific.")]
        public bool guaranteeFirstEntry = false;
        [Tooltip("R4: weight of an 'empty' pick. Each roll draws from the entry weights plus this one; landing on it " +
                 "uses up that roll and drops nothing. 0 (the default) means every roll that can drop does. The miss " +
                 "chance is relative to the weight still available, so once an entry reaches maxCopies the " +
                 "miss chance rises: this is not a constant per-roll chance.")]
        [Min(0)] public int nothingWeight = 0;

        /// <summary>Stream ids of the table roll (<see cref="RngMix.Combine"/> of the table seed and the id).</summary>
        public const ulong ShapeStream = 1UL;
        public const ulong AffixStream = 2UL;
        /// <summary>D3a: the rarity-ladder draws. A separate stream, so a table without a ladder consumes exactly what it always did.</summary>
        public const ulong RarityStream = 4UL;

        /// <summary>Largest weight one ladder rung may carry (keeps the total far inside an int).</summary>
        public const int MaxLadderWeight = 1000000;

        public int EntryCount => entries != null ? entries.Length : 0;

        /// <summary>True when a rarity ladder is authored (at least one rung). Null and empty are the same: no ladder.</summary>
        public bool UsesRarityLadder => rarityLadder != null && rarityLadder.Length > 0;

        /// <summary>Sum of the ladder weights (0 without a ladder or with only null/zero rungs).</summary>
        public long RarityLadderTotalWeight
        {
            get
            {
                if (rarityLadder == null) return 0;
                long total = 0;
                for (int i = 0; i < rarityLadder.Length; i++)
                {
                    if (rarityLadder[i] != null && rarityLadder[i].weight > 0) total += rarityLadder[i].weight;
                }
                return total;
            }
        }

        /// <summary>
        /// The ladder weight of one tier. Rungs are looked up by tier, never by position, so the order they were
        /// authored in does not change what a seed produces.
        /// </summary>
        public int LadderWeightOf(AffixRarity rarity)
        {
            if (rarityLadder == null) return 0;
            for (int i = 0; i < rarityLadder.Length; i++)
            {
                if (rarityLadder[i] != null && rarityLadder[i].rarity == rarity) return Mathf.Max(0, rarityLadder[i].weight);
            }
            return 0;
        }

        /// <summary>
        /// One ladder draw: a tier chosen with the ladder weights, walking the tiers in rank order (Poor first). Exactly
        /// one <c>Next(total)</c> is consumed. False (tier Common) when there is no usable ladder.
        /// </summary>
        public bool TryPickLadderRarity(ILootRng random, out AffixRarity picked)
        {
            picked = AffixRarity.Common;
            long total = RarityLadderTotalWeight;
            if (random == null || total <= 0 || total > int.MaxValue) return false;

            int ticket = random.Next((int)total);
            int running = 0;
            for (int i = 0; i < AffixRarities.ByRank.Length; i++)
            {
                AffixRarity tier = AffixRarities.ByRank[i];
                running += LadderWeightOf(tier);
                if (ticket >= running) continue;
                picked = tier;
                return true;
            }
            return false;
        }

        /// <summary>How many affix definitions this table authored. Null and empty are the same thing: no catalog.</summary>
        public int AffixCatalogCount => affixCatalog != null ? affixCatalog.Length : 0;

        /// <summary>True when at least one entry declares a rarity above Common, i.e. needs the affix catalog
        /// to roll random affixes with. The affix question only - see <see cref="RequestsGearInstances"/>.</summary>
        public bool RequestsRolledGear
        {
            get
            {
                if (entries != null)
                {
                    for (int i = 0; i < entries.Length; i++)
                    {
                        if (entries[i] != null && entries[i].RequestsRolledGear) return true;
                    }
                }

                for (int i = 0; i < AffixRarities.ByRank.Length; i++)
                {
                    AffixRarity tier = AffixRarities.ByRank[i];
                    if (AffixRarities.RollsAffixes(tier) && LadderWeightOf(tier) > 0) return true;
                }
                return false;
            }
        }

        /// <summary>True when at least one entry drops worn or held gear, i.e. at least one unit of this roll
        /// is produced as a concrete instance with its own UID - Common gear included. This is the question
        /// that decides whether the table has to build a trusted item catalog; it is deliberately not the same
        /// question as "has random affixes".</summary>
        public bool RequestsGearInstances
        {
            get
            {
                if (entries == null) return false;
                for (int i = 0; i < entries.Length; i++)
                {
                    if (entries[i] != null && entries[i].RequestsGearInstance) return true;
                }
                return false;
            }
        }

        public int TotalWeight
        {
            get
            {
                if (entries == null) return 0;
                int total = 0;
                for (int i = 0; i < entries.Length; i++)
                {
                    if (entries[i] == null || entries[i].item == null) continue;
                    total += entries[i].EffectiveWeight;
                }
                return total;
            }
        }

        /// <summary>Every item this table can ever produce, for validation and for the asset library.</summary>
        public bool Contains(ItemData item)
        {
            if (item == null || entries == null) return false;
            for (int i = 0; i < entries.Length; i++)
            {
                if (entries[i] != null && entries[i].item == item) return true;
            }
            return false;
        }

        /// <summary>PickWeighted result for a roll that landed on <see cref="nothingWeight"/>.</summary>
        private const int NothingPick = -2;

        public string Describe()
        {
            if (entries == null || entries.Length == 0) return "empty table";

            System.Text.StringBuilder builder = new System.Text.StringBuilder();
            for (int i = 0; i < entries.Length; i++)
            {
                LootEntry entry = entries[i];
                if (entry == null || entry.item == null) continue;
                if (builder.Length > 0) builder.Append(", ");
                builder.Append(entry.item.displayName)
                      .Append(" x").Append(entry.EffectiveMin).Append('-').Append(entry.EffectiveMax)
                      .Append(" w").Append(entry.EffectiveWeight);

                // Honest about the new lifecycle: every gear entry is an instance drop, so its rarity is part
                // of what the entry means; a carried entry keeps its old plain "item x min-max w weight" text.
                if (entry.RequestsGearInstance) builder.Append(" ").Append(entry.affixRarity);
            }
            return builder.ToString();
        }

        public List<string> Validate()
        {
            List<string> problems = new List<string>();

            if (string.IsNullOrEmpty(tableId)) problems.Add($"{name}: loot table has no id");
            if (entries == null || entries.Length == 0) problems.Add($"{name}: loot table declares no entries");
            if (TotalWeight <= 0) problems.Add($"{name}: every entry has weight 0, so the table can never drop anything");

            if (entries != null)
            {
                for (int i = 0; i < entries.Length; i++)
                {
                    LootEntry entry = entries[i];
                    if (entry == null) { problems.Add($"{name}: entry {i} is empty"); continue; }
                    if (entry.item == null) problems.Add($"{name}: entry {i} declares no item");

                    if (!AffixRarities.IsKnown(entry.affixRarity))
                    {
                        problems.Add($"{name}: entry {i} declares rarity value {(int)entry.affixRarity}, which is " +
                                     $"not a known rarity ({ItemRarityRules.StoredLegend})");
                        continue;
                    }

                    // Worn gear is minted through the trusted catalog at Common too, so its definition has to
                    // be usable as a catalog entry; a carried entry does not need one.
                    if (entry.item != null && entry.item.IsEquippable &&
                        !ItemIdentityRules.IsStableItemId(entry.item.itemId, out string gearIdReason))
                    {
                        problems.Add($"{name}: entry {i} drops worn gear whose item id cannot be a catalog " +
                                     $"entry: {gearIdReason}");
                    }

                    if (!entry.RequestsRolledGear) continue;

                    if (entry.item != null && !entry.item.IsEquippable)
                    {
                        problems.Add($"{name}: entry {i} asks for {entry.item.itemId} at rarity " +
                                     $"{entry.affixRarity}, but that item is carried rather than worn, so it " +
                                     "cannot carry rolled affixes");
                    }

                    if (AffixCatalogCount == 0)
                    {
                        problems.Add($"{name}: entry {i} asks for {entry.affixRarity} gear but the table " +
                                     "declares no affix catalog");
                    }
                }
            }

            ValidateRarityLadder(problems);

            // A table that drops gear has to be able to build the catalog that minting requires, and one item
            // id naming two different definitions would make a minted identity ambiguous. Repeated references
            // to the same object are only weighting, and stay legal.
            if (entries != null && RequestsGearInstances)
            {
                List<ItemData> listed = new List<ItemData>();
                List<int> listedAt = new List<int>();

                for (int i = 0; i < entries.Length; i++)
                {
                    LootEntry entry = entries[i];
                    if (entry == null || entry.item == null) continue;

                    bool sameObject = false;
                    for (int k = 0; k < listed.Count; k++)
                    {
                        if (!ReferenceEquals(listed[k], entry.item)) continue;
                        sameObject = true;
                        break;
                    }
                    if (sameObject) continue;

                    for (int k = 0; k < listed.Count; k++)
                    {
                        if (listed[k].itemId != entry.item.itemId) continue;

                        problems.Add($"{name}: entry {listedAt[k]} and entry {i} are two different item " +
                                     $"definitions that both claim item id '{entry.item.itemId}': one item id " +
                                     "names exactly one definition, so a minted gear identity would not say " +
                                     "which item it is");
                        break;
                    }

                    listed.Add(entry.item);
                    listedAt.Add(i);
                }
            }

            if (affixCatalog != null && affixCatalog.Length > 0 &&
                !AffixCatalog.TryValidate(affixCatalog, out List<string> catalogProblems))
            {
                for (int i = 0; i < catalogProblems.Count; i++)
                {
                    problems.Add($"{name}: affix catalog: {catalogProblems[i]}");
                }
            }

            return problems;
        }

        /// <summary>
        /// D3a: the ladder is data like everything else on a table, so a bad one is a reported problem and never a
        /// silent fallback: unknown tier, duplicate tier, negative or oversized weight, no weight at all, a ladder on a
        /// table that drops no gear, or a tier that rolls more affixes than the table's catalog has definitions.
        /// </summary>
        private void ValidateRarityLadder(List<string> problems)
        {
            int needed = 0;

            if (UsesRarityLadder)
            {
                HashSet<AffixRarity> seen = new HashSet<AffixRarity>();
                for (int i = 0; i < rarityLadder.Length; i++)
                {
                    RarityWeight rung = rarityLadder[i];
                    if (rung == null) { problems.Add($"{name}: rarity ladder rung {i} is empty"); continue; }

                    if (!AffixRarities.IsKnown(rung.rarity))
                    {
                        problems.Add($"{name}: rarity ladder rung {i} declares rarity value {(int)rung.rarity}, which is " +
                                     $"not a known rarity ({ItemRarityRules.StoredLegend})");
                        continue;
                    }

                    if (!seen.Add(rung.rarity))
                        problems.Add($"{name}: rarity ladder lists {rung.rarity} twice (rung {i}): one rung per tier");

                    if (rung.weight < 0 || rung.weight > MaxLadderWeight)
                        problems.Add($"{name}: rarity ladder rung {i} ({rung.rarity}) has weight {rung.weight}, outside 0..{MaxLadderWeight}");
                    else if (rung.weight > 0 && AffixRarities.TryExpectedAffixCount(rung.rarity, out int count) && count > needed)
                        needed = count;
                }

                if (RarityLadderTotalWeight <= 0)
                    problems.Add($"{name}: rarity ladder has no positive weight, so it can never pick a tier");

                if (!RequestsGearInstances)
                    problems.Add($"{name}: declares a rarity ladder but drops no worn gear, so nothing would read it");
            }

            if (entries != null)
            {
                for (int i = 0; i < entries.Length; i++)
                {
                    if (entries[i] != null && AffixRarities.TryExpectedAffixCount(entries[i].affixRarity, out int count) && count > needed)
                        needed = count;
                }
            }

            if (needed > 0 && AffixCatalogCount > 0 && AffixCatalogCount < needed)
            {
                problems.Add($"{name}: a tier here rolls {needed} distinct affixes but the affix catalog lists only " +
                             $"{AffixCatalogCount}: the count is never lowered to fit the catalog");
            }
        }

        private void OnValidate()
        {
            maxRolls = Mathf.Max(1, maxRolls);
            nothingWeight = Mathf.Max(0, nothingWeight);

            if (entries == null) return;

            for (int i = 0; i < entries.Length; i++)
            {
                LootEntry entry = entries[i];
                if (entry == null) continue;

                entry.minAmount = Mathf.Max(1, entry.minAmount);
                entry.maxAmount = Mathf.Max(entry.minAmount, entry.maxAmount);
                entry.weight = Mathf.Max(0, entry.weight);
                entry.maxCopies = Mathf.Max(1, entry.maxCopies);
            }
        }
    }
}
