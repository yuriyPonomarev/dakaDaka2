using System.Collections.Generic;
using UnityEngine;
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

        [Tooltip("The rarity this entry is rolled at. Common (0) rolls no affixes and consumes no affix RNG " +
                 "and keeps the item's own fixed stats. Uncommon (1) and Rare (2) roll that many accepted " +
                 "affixes from this table's catalog. Rarity says nothing about whether the drop is a concrete " +
                 "item: worn gear is one concrete item with its own UID at every rarity, Common included, and " +
                 "carried goods stay a definition plus the amount the table drew.")]
        public AffixRarity affixRarity = AffixRarity.Common;

        public int EffectiveMin => Mathf.Max(1, minAmount);
        public int EffectiveMax => Mathf.Max(EffectiveMin, maxAmount);
        public int EffectiveWeight => Mathf.Max(0, weight);
        public int EffectiveCopies => Mathf.Max(1, maxCopies);

        /// <summary>The affix question, and only the affix question: true when this entry declares a rarity
        /// above Common and therefore rolls random affixes from the table's catalog. It deliberately does NOT
        /// say whether the drop is a concrete item - Common gear is a concrete item too, and carried goods
        /// never roll affixes at any rarity.</summary>
        public bool RequestsRolledGear => affixRarity != AffixRarity.Common;

        /// <summary>The identity question: true when every unit this entry drops is produced as one concrete
        /// <see cref="Emberdelve.Items.ItemInstance"/> with its own UID, because the item is worn or held gear.
        /// Worn gear is one item per unit at Common, Uncommon and Rare alike; carried goods (currency,
        /// consumables, relics) stay a definition plus the amount the table drew.</summary>
        public bool RequestsGearInstance => item != null && item.IsEquippable;
    }

    /// <summary>
    /// One drop from a table.
    ///
    /// <see cref="Item"/> and <see cref="Amount"/> keep their exact legacy meaning for every existing caller:
    /// a carried drop is a definition and a number of units. <see cref="Instance"/> (U2B9) is the concrete half:
    /// when it is not null this drop <em>is</em> one item - that UID, that rarity, those accepted affix records,
    /// quantity 1 - and the container that holds the drop holds that very object, so opening, reopening, a
    /// refused transfer or a UI read can never re-roll it or replace it with a lookalike.
    ///
    /// U2B9-R1 made the two halves line up with what the item actually is: every unit of worn gear a table
    /// selects is instance-backed, at Common as much as at Uncommon or Rare (Common simply carries zero affix
    /// records and the definition's own fixed stats). Carried goods stay definition+amount. A container routes
    /// on <see cref="IsRolled"/>, never on the rarity.
    /// </summary>
    public readonly struct LootDrop
    {
        public readonly ItemData Item;
        public readonly int Amount;
        public readonly ItemInstance Instance;

        public LootDrop(ItemData item, int amount) : this(item, amount, null)
        {
        }

        public LootDrop(ItemData item, int amount, ItemInstance instance)
        {
            Item = item;
            Amount = amount;
            Instance = instance;
        }

        /// <summary>True when this drop carries a concrete rolled item instead of a definition+amount claim.</summary>
        public bool IsRolled => Instance != null;

        /// <summary>The rarity the drop carries: the instance's own, or Common for a legacy definition drop.</summary>
        public AffixRarity Rarity => Instance != null ? Instance.Rarity : AffixRarity.Common;

        /// <summary>Canonical Guid "N" text of the identity this drop carries, empty when it carries none.</summary>
        public string UidText => Instance != null ? Instance.UidText : string.Empty;

        public override string ToString()
        {
            if (Item == null) return "none";
            return Instance != null
                ? $"{Item.displayName} x{Amount} [{Instance.Rarity} {Instance.UidText}]"
                : $"{Item.displayName} x{Amount}";
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

        [Header("Roll shape")]
        [Tooltip("How many separate drops this table may produce for one container.")]
        [Min(1)] public int maxRolls = 3;
        [Tooltip("The first entry is always handed out once, so a container can be guaranteed to contain something specific.")]
        public bool guaranteeFirstEntry = false;

        public int EntryCount => entries != null ? entries.Length : 0;

        /// <summary>How many affix definitions this table authored. Null and empty are the same thing: no catalog.</summary>
        public int AffixCatalogCount => affixCatalog != null ? affixCatalog.Length : 0;

        /// <summary>True when at least one entry declares a rarity above Common, i.e. needs the affix catalog
        /// to roll random affixes with. The affix question only - see <see cref="RequestsGearInstances"/>.</summary>
        public bool RequestsRolledGear
        {
            get
            {
                if (entries == null) return false;
                for (int i = 0; i < entries.Length; i++)
                {
                    if (entries[i] != null && entries[i].RequestsRolledGear) return true;
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

        /// <summary>
        /// Roll the table into <paramref name="output"/> against the run's item catalog. It does not hide an
        /// invalid profile: a table whose entries, catalog or rarity claims cannot be produced logs the reason
        /// and leaves the output empty, instead of quietly handing out a Common stand-in for gear it was asked
        /// to roll.
        /// </summary>
        public void Roll(int seed, List<LootDrop> output, ItemCatalog catalog)
        {
            if (TryRoll(seed, output, catalog, out string reason)) return;

            Debug.LogError($"[Emberdelve][LootTable] {name}: seeded roll refused: {reason}", this);
        }

        /// <summary>
        /// Rolls the table for one container and says whether the whole roll was actually produced.
        /// Uses a fresh local trusted issuer for this roll, which is what an offline container does today; a
        /// run or a future server that wants to count or own the minting passes its own issuer to the overload.
        /// </summary>
        public bool TryRoll(int seed, List<LootDrop> output, ItemCatalog catalog, out string reason)
        {
            return TryRoll(seed, output, catalog, ItemIdentityIssuer.CreateLocalTrusted(), out reason);
        }

        /// <summary>
        /// The producing roll: every profile, catalog, rarity and gear-category claim is decided before a single
        /// unit is built, and a refusal publishes nothing at all - no half-filled output, no Common stand-in for
        /// a rolled entry, no identity minted for an item this table is not allowed to make.
        ///
        /// Determinism, exactly:
        /// <list type="bullet">
        /// <item>the item/amount stream is the legacy one, drawn from <c>new System.Random(seed)</c> in the same
        /// order and consuming the same numbers, so the same seed still selects the same entries and the same
        /// amounts; what changed is only what a selected gear unit is turned <em>into</em> (one instance per
        /// unit, at every rarity), never which unit was selected or how many units were selected;</item>
        /// <item>the affix stream is a second generator derived from the same seed, consumed once per rolled
        /// unit in selection order through the accepted <see cref="LootRoller"/>, so the same seed plus the same
        /// table plus the same catalog gives the same affix ids, order and values. A Common gear unit consumes
        /// nothing from it - zero affixes is zero draws - so adding Common gear to a table cannot shift the
        /// records another unit of the same roll already produced;</item>
        /// <item>the UID is the one part that deliberately differs per produced unit: the issuer mints once per
        /// accepted instance, which is what makes two copies of one archetype two items - including two Common
        /// boots from one entry that drew amount 2.</item>
        /// </list>
        /// </summary>
        public bool TryRoll(int seed, List<LootDrop> output, ItemCatalog catalog, ItemIdentityIssuer issuer,
            out string reason)
        {
            reason = null;

            if (output == null)
            {
                reason = $"{name}: no output list to fill";
                return false;
            }

            output.Clear();

            if (entries == null || entries.Length == 0)
            {
                reason = $"{name}: loot table declares no entries";
                return false;
            }

            if (issuer == null)
            {
                reason = $"{name}: no item identity issuer was supplied: this table does not mint identities itself";
                return false;
            }

            // ---- 1. profiles, before anything is drawn or built.
            // Two questions, asked separately on purpose: does this entry roll random affixes (a rarity above
            // Common), and does this entry drop worn gear, which is one concrete instance with its own UID at
            // every rarity - Common included. Confusing the two is exactly the bug this slice fixes.
            bool requestsRolledAffixes = false;
            bool requestsGearInstances = false;

            for (int i = 0; i < entries.Length; i++)
            {
                LootEntry entry = entries[i];
                if (entry == null) continue;

                if (!AffixRarities.IsKnown(entry.affixRarity))
                {
                    reason = $"{name}: entry {i} declares rarity value {(int)entry.affixRarity}, which is not a " +
                             "known rarity (0 Common, 1 Uncommon, 2 Rare); an unknown rarity is refused, never " +
                             "read as Common";
                    return false;
                }

                if (entry.RequestsRolledGear) requestsRolledAffixes = true;

                if (entry.item == null)
                {
                    if (entry.RequestsRolledGear)
                    {
                        reason = $"{name}: entry {i} asks for {entry.affixRarity} gear but declares no item";
                        return false;
                    }

                    // A Common entry with no item is the legacy hole the shape loop already skips: it can never
                    // be staged, so it claims no identity and needs no catalog.
                    continue;
                }

                if (entry.item.IsEquippable)
                {
                    requestsGearInstances = true;
                    continue;
                }

                if (entry.RequestsRolledGear)
                {
                    reason = $"{name}: entry {i} asks for {entry.item.itemId} at rarity {entry.affixRarity}, but " +
                            "that item is carried rather than worn: rolled affixes are never silently downgraded " +
                            "into a Common carried stack";
                    return false;
                }
            }

            // Only a rarity that actually rolls affixes needs an affix catalog. Common gear rolls none, so a
            // table with no catalog authored at all can still mint Common gear identities - it just may not
            // pretend to roll Uncommon or Rare gear.
            if (requestsRolledAffixes && AffixCatalogCount == 0)
            {
                reason = $"{name}: entries ask for rolled gear but the table declares no affix catalog " +
                         "(LootTable.affixCatalog is empty), so there is nothing to roll with";
                return false;
            }

            // Any worn gear at all needs the run's item catalog (R3), because the accepted roller and issuer only
            // mint for the definition object that catalog owns for that item id. The table never builds a
            // catalog out of its own entries any more: a gear entry that is not a catalog item is a refusal. A
            // table that only ever drops carried definitions still needs no catalog at all, which is what keeps
            // every pre-U2B9 table rolling exactly as it did.
            ItemInstanceAuthority authority = null;
            if (requestsGearInstances && !TryDeriveAuthority(catalog, out authority, out reason)) return false;

            // ---- 2. the shape loop, consuming exactly what the legacy loop consumed.
            System.Random random = new System.Random(seed);
            int rolls = Mathf.Max(1, maxRolls);
            int[] copies = new int[entries.Length];
            List<LootDrop> staged = new List<LootDrop>();
            List<PendingUnit> pending = new List<PendingUnit>();

            if (guaranteeFirstEntry && entries[0] != null && entries[0].item != null)
            {
                LootEntry first = entries[0];
                StageDrop(staged, pending, first, RandomInRange(random, first));
                copies[0] = 1;
            }

            for (int roll = 0; roll < rolls; roll++)
            {
                int index = PickWeighted(random, copies);
                if (index < 0) break;

                LootEntry entry = entries[index];
                StageDrop(staged, pending, entry, RandomInRange(random, entry));
                copies[index]++;
            }

            // ---- 3. validate every rolled unit against the trusted catalog without minting, then produce each
            // one exactly once. The two passes consume their own generators from the same derived seed, so the
            // records that were proven rollable are the records that get built.
            if (pending.Count > 0)
            {
                int affixSeed = DeriveAffixSeed(seed);

                System.Random validator = new System.Random(affixSeed);
                for (int i = 0; i < pending.Count; i++)
                {
                    PendingUnit unit = pending[i];
                    if (!LootRoller.TryRollAffixes(unit.Definition, unit.Rarity, authority, validator,
                        out _, out reason))
                    {
                        reason = $"{name}: {unit.Definition.itemId} at rarity {unit.Rarity} cannot be rolled from " +
                                 $"this table's catalog: {reason}";
                        return false;
                    }
                }

                System.Random roller = new System.Random(affixSeed);
                for (int i = 0; i < pending.Count; i++)
                {
                    PendingUnit unit = pending[i];

                    if (!LootRoller.TryRoll(unit.Definition, 1, unit.Rarity, authority, roller, issuer,
                        out ItemInstance instance, out reason))
                    {
                        reason = $"{name}: {unit.Definition.itemId} at rarity {unit.Rarity} was refused by the " +
                                 $"roller: {reason}";
                        return false;
                    }

                    staged[unit.StageIndex] = new LootDrop(unit.Definition, 1, instance);
                }
            }

            // Published only because every unit in it was validated and produced.
            output.AddRange(staged);
            return true;
        }

        /// <summary>
        /// Places one selected entry into the staged roll, by what the item is rather than by the rarity it was
        /// drawn at. A carried entry is the legacy single drop carrying the item and the drawn amount. A worn
        /// gear entry is that many <em>separate</em> units, each staged as one quantity-1 drop waiting for its
        /// own instance, because worn gear is one item and one identity at Common as much as at Rare - a
        /// breastplate is never a stack with a rarity claim pasted on it, and an entry that drew two boots is
        /// two boots with two UIDs, not one boot counted twice.
        /// </summary>
        private static void StageDrop(List<LootDrop> staged, List<PendingUnit> pending, LootEntry entry, int amount)
        {
            if (!entry.RequestsGearInstance)
            {
                staged.Add(new LootDrop(entry.item, amount));
                return;
            }

            int units = Mathf.Max(1, amount);
            for (int unit = 0; unit < units; unit++)
            {
                staged.Add(new LootDrop(entry.item, 1));
                pending.Add(new PendingUnit
                {
                    StageIndex = staged.Count - 1,
                    Definition = entry.item,
                    Rarity = entry.affixRarity
                });
            }
        }

        /// <summary>
        /// The rolling authority for this table, derived from the run's item catalog (R3): the gear definitions
        /// its own entries name and the affix subset it authored (possibly none - an empty affix list is a
        /// legal Common-only table). Every member must be the catalog's own object, so a table can restrict what
        /// it rolls but can never introduce an item or affix the catalog does not own. Carried (non-gear)
        /// entries are not minted and are not listed. Repeated references to one definition object are listed
        /// once - the weighted entries themselves are never touched, so this cannot change the loot shape. Two
        /// different definition objects that claim one item id cannot both be catalog entries, so the second is
        /// refused.
        /// </summary>
        private bool TryDeriveAuthority(ItemCatalog catalog, out ItemInstanceAuthority authority, out string reason)
        {
            authority = null;

            if (catalog == null)
            {
                reason = $"{name}: no item catalog was supplied, and this table drops gear: gear identities are " +
                         "only minted for definitions the run's item catalog owns";
                return false;
            }

            List<ItemData> items = new List<ItemData>();

            for (int i = 0; i < entries.Length; i++)
            {
                LootEntry entry = entries[i];
                if (entry == null || entry.item == null || !entry.item.IsEquippable) continue;
                if (!items.Contains(entry.item)) items.Add(entry.item);
            }

            List<AffixDefinition> affixes = new List<AffixDefinition>();

            if (affixCatalog != null)
            {
                for (int i = 0; i < affixCatalog.Length; i++)
                {
                    if (affixCatalog[i] == null)
                    {
                        reason = $"{name}: affix catalog entry {i} is null";
                        return false;
                    }

                    if (!affixes.Contains(affixCatalog[i])) affixes.Add(affixCatalog[i]);
                }
            }

            if (!catalog.TryDeriveAuthority(items, affixes, out authority, out reason))
            {
                authority = null;
                reason = $"{name}: the table is not backed by the item catalog: {reason}";
                return false;
            }

            return true;
        }

        /// <summary>
        /// The affix stream is a second stream derived from the same table seed, so a table gaining gear cannot
        /// shift the item/amount stream a legacy table already depends on: the shape loop still consumes exactly
        /// the numbers it used to, and the affix draws happen on their own generator. Same seed plus same table
        /// plus same catalog still gives the same affix records. Common gear units draw nothing from this
        /// generator at all, so they cannot shift the records the Uncommon or Rare units of the same roll get.
        /// </summary>
        private static int DeriveAffixSeed(int seed)
        {
            return unchecked((seed * -1640531527) ^ 1540483477);
        }

        /// <summary>One rolled unit this table selected: where it sits in the staged drops, and what it must be.</summary>
        private struct PendingUnit
        {
            public int StageIndex;
            public ItemData Definition;
            public AffixRarity Rarity;
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

        private int PickWeighted(System.Random random, int[] copies)
        {
            int total = 0;
            for (int i = 0; i < entries.Length; i++)
            {
                LootEntry entry = entries[i];
                if (entry == null || entry.item == null) continue;
                if (copies[i] >= entry.EffectiveCopies) continue;
                total += entry.EffectiveWeight;
            }

            if (total <= 0) return -1;

            int ticket = random.Next(total);
            int running = 0;

            for (int i = 0; i < entries.Length; i++)
            {
                LootEntry entry = entries[i];
                if (entry == null || entry.item == null) continue;
                if (copies[i] >= entry.EffectiveCopies) continue;

                running += entry.EffectiveWeight;
                if (ticket < running) return i;
            }

            return -1;
        }

        private static int RandomInRange(System.Random random, LootEntry entry)
        {
            int min = entry.EffectiveMin;
            int max = entry.EffectiveMax;
            return min == max ? min : random.Next(min, max + 1);
        }

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
                                     "not a known rarity (0 Common, 1 Uncommon, 2 Rare)");
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

        private void OnValidate()
        {
            maxRolls = Mathf.Max(1, maxRolls);

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
