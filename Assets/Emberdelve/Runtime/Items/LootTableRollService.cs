using System.Collections.Generic;
using UnityEngine;
using Emberdelve.Core;
using Emberdelve.Items;

namespace Emberdelve.Data
{
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

    /// <summary>Seeded loot orchestration kept separate from the LootTable authoring asset.</summary>
    public static class LootTableRollService
    {
        private const int NothingPick = -2;

        public static void Roll(LootTable table, int seed, List<LootDrop> output, ItemCatalog catalog)
        {
            if (TryRoll(table, seed, output, catalog, out string reason)) return;
            Debug.LogError($"[Emberdelve][LootTable] {table.name}: seeded roll refused: {reason}", table);
        }

        public static bool TryRoll(LootTable table, int seed, List<LootDrop> output, ItemCatalog catalog, out string reason)
        {
            return TryRoll(table, seed, output, catalog, ItemIdentityIssuer.CreateLocalTrusted(), out reason);
        }

        /// <summary>
        /// Legacy 32-bit seed entry. The int is zero-extended to the 64-bit seed, so every int seed is a
        /// distinct, stable table seed.
        /// </summary>
        public static bool TryRoll(LootTable table, int seed, List<LootDrop> output, ItemCatalog catalog, ItemIdentityIssuer issuer,
            out string reason)
        {
            return TryRollSeeded(table, unchecked((uint)seed), output, catalog, issuer, out reason);
        }

        /// <summary>
        /// The producing roll: every profile, catalog, rarity and gear-category claim is decided before a single
        /// unit is built, and a refusal publishes nothing at all - no half-filled output, no Common stand-in for
        /// a rolled entry, no identity minted for an item this table is not allowed to make.
        ///
        /// Determinism, exactly:
        /// <list type="bullet">
        /// <item>the item/amount stream is <c>new DeterministicRng(seed).Fork(ShapeStream)</c> (R4: the project's
        /// own SplitMix64, replacing the platform generator), drawn in a fixed order, so the same seed selects the
        /// same table.entries and the same amounts on every platform; a selected gear unit is turned into one instance
        /// per unit, at every rarity;</item>
        /// <item>the affix stream is a second generator, <c>Fork(AffixStream)</c> of the same seed, consumed once per rolled
        /// unit in selection order through the accepted <see cref="LootRoller"/>, so the same seed plus the same
        /// table plus the same catalog gives the same affix ids, order and values. A Common gear unit consumes
        /// nothing from it - zero affixes is zero draws - so adding Common gear to a table cannot shift the
        /// records another unit of the same roll already produced;</item>
        /// <item>the UID is the one part that deliberately differs per produced unit: the issuer mints once per
        /// accepted instance, which is what makes two copies of one archetype two items - including two Common
        /// boots from one entry that drew amount 2.</item>
        /// </list>
        /// </summary>
        public static bool TryRollSeeded(LootTable table, ulong seed, List<LootDrop> output, ItemCatalog catalog, ItemIdentityIssuer issuer,
            out string reason)
        {
            reason = null;

            if (output == null)
            {
                reason = $"{table.name}: no output list to fill";
                return false;
            }

            output.Clear();

            if (table.entries == null || table.entries.Length == 0)
            {
                reason = $"{table.name}: loot table declares no entries";
                return false;
            }

            if (issuer == null)
            {
                reason = $"{table.name}: no item identity issuer was supplied: this table does not mint identities itself";
                return false;
            }

            // ---- 1. profiles, before anything is drawn or built.
            // Two questions, asked separately on purpose: does this entry roll random affixes (a rarity above
            // Common), and does this entry drop worn gear, which is one concrete instance with its own UID at
            // every rarity - Common included. Confusing the two is exactly the bug this slice fixes.
            bool requestsRolledAffixes = false;
            bool requestsGearInstances = false;

            for (int i = 0; i < table.entries.Length; i++)
            {
                LootEntry entry = table.entries[i];
                if (entry == null) continue;

                if (!AffixRarities.IsKnown(entry.affixRarity))
                {
                    reason = $"{table.name}: entry {i} declares rarity value {(int)entry.affixRarity}, which is not a " +
                             "known rarity (0 Common, 1 Uncommon, 2 Rare); an unknown rarity is refused, never " +
                             "read as Common";
                    return false;
                }

                if (entry.RequestsRolledGear) requestsRolledAffixes = true;

                if (entry.item == null)
                {
                    if (entry.RequestsRolledGear)
                    {
                        reason = $"{table.name}: entry {i} asks for {entry.affixRarity} gear but declares no item";
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
                    reason = $"{table.name}: entry {i} asks for {entry.item.itemId} at rarity {entry.affixRarity}, but " +
                            "that item is carried rather than worn: rolled affixes are never silently downgraded " +
                            "into a Common carried stack";
                    return false;
                }
            }

            // Only a rarity that actually rolls affixes needs an affix catalog. Common gear rolls none, so a
            // table with no catalog authored at all can still mint Common gear identities - it just may not
            // pretend to roll Uncommon or Rare gear.
            if (requestsRolledAffixes && table.AffixCatalogCount == 0)
            {
                reason = $"{table.name}: entries ask for rolled gear but the table declares no affix catalog " +
                         "(LootTable.affixCatalog is empty), so there is nothing to roll with";
                return false;
            }

            // Any worn gear at all needs the run's item catalog (R3), because the accepted roller and issuer only
            // mint for the definition object that catalog owns for that item id. The table never builds a
            // catalog out of its own entries any more: a gear entry that is not a catalog item is a refusal. A
            // table that only ever drops carried definitions still needs no catalog at all. (R4: the streams
            // changed, so a given int seed no longer produces what it produced before the project RNG.)
            ItemInstanceAuthority authority = null;
            if (requestsGearInstances && !TryDeriveAuthority(table, catalog, out authority, out reason)) return false;

            // ---- 2. the shape loop, on its own stream (Fork(ShapeStream) of the table seed; R4 re-baselined the
            // numbers every seed produces, the draw order and the selection rules are the legacy ones).
            DeterministicRng root = new DeterministicRng(seed);
            DeterministicRng random = root.Fork(LootTable.ShapeStream);
            int rolls = Mathf.Max(1, table.maxRolls);
            int[] copies = new int[table.entries.Length];
            List<LootDrop> staged = new List<LootDrop>();
            List<PendingUnit> pending = new List<PendingUnit>();

            if (table.guaranteeFirstEntry && table.entries[0] != null && table.entries[0].item != null)
            {
                LootEntry first = table.entries[0];
                StageDrop(staged, pending, first, RandomInRange(random, first));
                copies[0] = 1;
            }

            for (int roll = 0; roll < rolls; roll++)
            {
                int index = PickWeighted(table, random, copies);
                if (index == NothingPick) continue;
                if (index < 0) break;

                LootEntry entry = table.entries[index];
                StageDrop(staged, pending, entry, RandomInRange(random, entry));
                copies[index]++;
            }

            // ---- 3. validate every rolled unit against the trusted catalog without minting, then produce each
            // one exactly once. The two passes consume their own generators from the same derived seed, so the
            // records that were proven rollable are the records that get built.
            if (pending.Count > 0)
            {
                DeterministicRng affixRoot = root.Fork(LootTable.AffixStream);

                ILootRng validator = affixRoot.Restart();
                for (int i = 0; i < pending.Count; i++)
                {
                    PendingUnit unit = pending[i];
                    if (!LootRoller.TryRollAffixes(unit.Definition, unit.Rarity, authority, validator,
                        out _, out reason))
                    {
                        reason = $"{table.name}: {unit.Definition.itemId} at rarity {unit.Rarity} cannot be rolled from " +
                                 $"this table's catalog: {reason}";
                        return false;
                    }
                }

                ILootRng roller = affixRoot.Restart();
                for (int i = 0; i < pending.Count; i++)
                {
                    PendingUnit unit = pending[i];

                    if (!LootRoller.TryRoll(unit.Definition, 1, unit.Rarity, authority, roller, issuer,
                        out ItemInstance instance, out reason))
                    {
                        reason = $"{table.name}: {unit.Definition.itemId} at rarity {unit.Rarity} was refused by the " +
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
        /// its own entries and the affix subset it authored (possibly none - an empty affix list is a
        /// legal Common-only table). Every member must be the catalog's own object, so a table can restrict what
        /// it rolls but can never introduce an item or affix the catalog does not own. Carried (non-gear)
        /// entries are not minted and are not listed. Repeated references to one definition object are listed
        /// once - the weighted entries themselves are never touched, so this cannot change the loot shape. Two
        /// different definition objects that claim one item id cannot both be catalog entries, so the second is
        /// refused.
        /// </summary>
        private static bool TryDeriveAuthority(LootTable table, ItemCatalog catalog, out ItemInstanceAuthority authority, out string reason)
        {
            authority = null;

            if (catalog == null)
            {
                reason = $"{table.name}: no item catalog was supplied, and this table drops gear: gear identities are " +
                         "only minted for definitions the run's item catalog owns";
                return false;
            }

            List<ItemData> items = new List<ItemData>();

            for (int i = 0; i < table.entries.Length; i++)
            {
                LootEntry entry = table.entries[i];
                if (entry == null || entry.item == null || !entry.item.IsEquippable) continue;
                if (!items.Contains(entry.item)) items.Add(entry.item);
            }

            List<AffixDefinition> affixes = new List<AffixDefinition>();

            if (table.affixCatalog != null)
            {
                for (int i = 0; i < table.affixCatalog.Length; i++)
                {
                    if (table.affixCatalog[i] == null)
                    {
                        reason = $"{table.name}: affix catalog entry {i} is null";
                        return false;
                    }

                    if (!affixes.Contains(table.affixCatalog[i])) affixes.Add(table.affixCatalog[i]);
                }
            }

            if (!catalog.TryDeriveAuthority(items, affixes, out authority, out reason))
            {
                authority = null;
                reason = $"{table.name}: the table is not backed by the item catalog: {reason}";
                return false;
            }

            return true;
        }

        /// <summary>One rolled unit this table selected: where it sits in the staged drops, and what it must be.</summary>
        private struct PendingUnit
        {
            public int StageIndex;
            public ItemData Definition;
            public AffixRarity Rarity;
        }

        private static int PickWeighted(LootTable table, ILootRng random, int[] copies)
        {
            int total = 0;
            for (int i = 0; i < table.entries.Length; i++)
            {
                LootEntry entry = table.entries[i];
                if (entry == null || entry.item == null) continue;
                if (copies[i] >= entry.EffectiveCopies) continue;
                total += entry.EffectiveWeight;
            }

            if (total <= 0) return -1;

            int empty = Mathf.Max(0, table.nothingWeight);
            int ticket = random.Next(total + empty);
            int running = 0;

            for (int i = 0; i < table.entries.Length; i++)
            {
                LootEntry entry = table.entries[i];
                if (entry == null || entry.item == null) continue;
                if (copies[i] >= entry.EffectiveCopies) continue;

                running += entry.EffectiveWeight;
                if (ticket < running) return i;
            }

            return empty > 0 ? NothingPick : -1;
        }

        private static int RandomInRange(ILootRng random, LootEntry entry)
        {
            int min = entry.EffectiveMin;
            int max = entry.EffectiveMax;
            return min == max ? min : random.Next(min, max + 1);
        }

    }


    /// <summary>Source-compatible LootTable rolling entry points backed by LootTableRollService.</summary>
    public static class LootTableRollExtensions
    {
        public static void Roll(this LootTable table, int seed, List<LootDrop> output, ItemCatalog catalog)
        {
            LootTableRollService.Roll(table, seed, output, catalog);
        }

        public static bool TryRoll(this LootTable table, int seed, List<LootDrop> output, ItemCatalog catalog,
            out string reason)
        {
            return LootTableRollService.TryRoll(table, seed, output, catalog, out reason);
        }

        public static bool TryRoll(this LootTable table, int seed, List<LootDrop> output, ItemCatalog catalog,
            ItemIdentityIssuer issuer, out string reason)
        {
            return LootTableRollService.TryRoll(table, seed, output, catalog, issuer, out reason);
        }

        public static bool TryRollSeeded(this LootTable table, ulong seed, List<LootDrop> output,
            ItemCatalog catalog, ItemIdentityIssuer issuer, out string reason)
        {
            return LootTableRollService.TryRollSeeded(table, seed, output, catalog, issuer, out reason);
        }
    }
}
