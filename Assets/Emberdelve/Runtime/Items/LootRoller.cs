using System;
using System.Collections.Generic;
using Emberdelve.Core;
using Emberdelve.Data;

namespace Emberdelve.Items
{
    /// <summary>
    /// The seeded weighted affix roller from Docs/Design/ITEM_AFFIXES.md: given a trusted item definition,
    /// a quantity, a known rarity, the trusted catalog, a caller-supplied <see cref="ILootRng"/> (the project's own generator, never System.Random) and
    /// the existing <see cref="ItemIdentityIssuer"/>, it produces the final affix records and then hands
    /// them to the shared validated issuer path to build one <see cref="ItemInstance"/>.
    ///
    /// What "seeded" means here, exactly: the roller owns no RNG of its own. It never calls
    /// UnityEngine.Random, never reads a global seed, never reseeds from the clock and has no fallback
    /// generator for the case where the caller forgot to pass one - a missing generator is a refusal, not
    /// a surprise. Every number it consumes comes from the supplied Random, in a fixed order that depends
    /// only on the catalog contents and the requested rarity: for each rolled affix it takes one weighted
    /// pick and then one grid draw. So the same seed plus the same catalog plus the same request gives the
    /// same affix ids, in the same order, with the same values. The UID is the one part that is supposed to
    /// differ: it comes from the issuer, once per successful roll, so two rolls of the same archetype with
    /// the same seed are the same roll of two different items.
    ///
    /// Order independence is deliberate. The eligible definitions are copied out of the catalog and sorted
    /// by stable ordinal id before anything is sampled, so re-ordering the catalog asset (or registering
    /// the same affixes in a different order) does not silently change what a seed produces. The catalog
    /// itself is never sorted, mutated, reordered or written to.
    ///
    /// This is a rolling helper, not a network authority. It rolls from the catalog this process trusts,
    /// which is what an authoritative server would do with its own catalog and its own seed; it is not
    /// proof of server-side minting, ownership, transfer or replication, and no inventory, equipment,
    /// snapshot or UI path calls it. Copy, quantity change, export and restore never reroll anything: they
    /// carry the records that were already accepted.
    /// </summary>
    public static class LootRoller
    {
        // ------------------------------------------------------------------------ public entry

        /// <summary>
        /// Roll one item and build it. Everything that can be decided without randomness is decided first:
        /// the trusted-definition identity gate (the supplied object must be the very object the catalog
        /// owns for that id), the rarity and its required affix count, the carrying/quantity rule, the
        /// trusted gear category, and the eligibility, weight and grid validity of every affix definition
        /// in the catalog. Only then is randomness consumed, and only then is the identity minted - through
        /// <see cref="ItemIdentityIssuer.TryCreateInstance"/>, which re-validates the whole item and mints
        /// nothing unless that succeeds.
        ///
        /// Any failure returns false with one reason and a null instance, and it mints nothing: an invalid
        /// request never leaves a half-built item and never spends an identity.
        /// </summary>
        public static bool TryRoll(ItemData definition, int quantity, AffixRarity rarity,
            ItemInstanceAuthority authority, ILootRng random, ItemIdentityIssuer issuer,
            out ItemInstance instance, out string reason)
        {
            instance = null;

            if (random == null)
            {
                reason = "no caller-supplied ILootRng: this roller has no fallback generator and is " +
                         "never seeded from a global generator or from the clock";
                return false;
            }

            if (issuer == null)
            {
                reason = "no item identity issuer: the roller does not mint identities itself";
                return false;
            }

            if (!TryPrepare(definition, rarity, authority, out ItemData trusted,
                out AffixCategory category, out int expected, out reason))
            {
                return false;
            }

            // Quantity before any randomness: an uncarryable request is refused without consuming a roll.
            if (!TryValidateCarrying(trusted, quantity, out reason)) return false;

            AffixRecord[] records = null;
            if (expected > 0)
            {
                if (!TryBuildEligiblePool(trusted, rarity, authority, category, expected,
                    out List<Candidate> pool, out reason))
                {
                    return false;
                }

                if (!TrySample(pool, expected, category, random, out records, out reason))
                {
                    records = null;
                    return false;
                }
            }

            // Final construction is the accepted shared path, not a shortcut around it: the issuer re-checks
            // the trusted definition, the quantity, the category, the rarity/affix count and every
            // id/key/value triple against the catalog, and mints exactly one UID only if all of that holds.
            if (!issuer.TryCreateInstance(trusted, quantity, rarity, records, authority,
                out instance, out reason))
            {
                instance = null;
                return false;
            }

            return true;
        }

        /// <summary>
        /// The same roll without minting or building anything: the accepted affix records for one definition
        /// and rarity under one catalog and generator. It is the deterministic core of <see cref="TryRoll"/>,
        /// so a roll and this helper consume the generator identically and report the same records - which is
        /// how same-seed equality is checked without comparing UIDs, and how a future server can roll now and
        /// mint later. It validates definition identity, rarity, category and eligibility, but not quantity
        /// (nothing here is carried) and it never mints.
        /// </summary>
        public static bool TryRollAffixes(ItemData definition, AffixRarity rarity,
            ItemInstanceAuthority authority, ILootRng random,
            out AffixRecord[] records, out string reason)
        {
            records = null;

            if (random == null)
            {
                reason = "no caller-supplied ILootRng: this roller has no fallback generator and is " +
                         "never seeded from a global generator or from the clock";
                return false;
            }

            if (!TryPrepare(definition, rarity, authority, out ItemData trusted,
                out AffixCategory category, out int expected, out reason))
            {
                return false;
            }

            if (expected == 0)
            {
                // Poor and Common are fixed stats only: no affix pool is consulted and no randomness is consumed.
                records = new AffixRecord[0];
                reason = null;
                return true;
            }

            if (!TryBuildEligiblePool(trusted, rarity, authority, category, expected,
                out List<Candidate> pool, out reason))
            {
                return false;
            }

            return TrySample(pool, expected, category, random, out records, out reason);
        }

        // ------------------------------------------------------------- pre-randomness validation

        /// <summary>
        /// Everything decided before a single number is drawn. Same trusted-definition gate the accepted
        /// creation boundary uses: the id is resolved through the catalog and the object that resolves must
        /// be the object that was handed over, so a hand-made definition that merely copies a catalog id is
        /// refused before its fields are believed.
        /// </summary>
        private static bool TryPrepare(ItemData definition, AffixRarity rarity, ItemInstanceAuthority authority,
            out ItemData trusted, out AffixCategory category, out int expected, out string reason)
        {
            trusted = null;
            category = AffixCategory.None;
            expected = -1;

            if (authority == null)
            {
                reason = "no trusted item/affix catalog was supplied";
                return false;
            }

            if (definition == null)
            {
                reason = "no item definition to roll for";
                return false;
            }

            if (!ItemIdentityRules.IsStableItemId(definition.itemId, out reason)) return false;

            if (!authority.TryResolveItem(definition.itemId, out trusted, out reason)) return false;

            if (!ReferenceEquals(trusted, definition))
            {
                reason = $"the supplied '{definition.itemId}' definition is not the definition the trusted " +
                         $"catalog owns for that item id: a definition that shares a catalog id but is not " +
                         $"the registered object cannot be rolled for";
                return false;
            }

            if (!AffixRarities.TryExpectedAffixCount(rarity, out expected))
            {
                reason = $"unknown rarity value {(int)rarity}: only the ladder members " +
                         $"({ItemRarityRules.StoredLegend}) exist";
                return false;
            }

            if (expected == 0)
            {
                // Poor and Common carry no rolled affixes, so no gear category is needed and none is claimed.
                reason = null;
                return true;
            }

            if (!trusted.IsEquippable)
            {
                reason = $"{trusted.itemId} is not equipment, so it may not carry rolled affixes " +
                         $"(rarity {rarity})";
                return false;
            }

            if (!authority.TryCategory(trusted, out category, out reason))
            {
                category = AffixCategory.None;
                return false;
            }

            if (!AffixCategories.IsSingle(category))
            {
                reason = $"rolled gear needs exactly one known gear category, {trusted.itemId} resolved to " +
                        $"{AffixCategories.Describe(category)}";
                category = AffixCategory.None;
                return false;
            }

            reason = null;
            return true;
        }

        /// <summary>
        /// The accepted carrying rule, applied before randomness so an impossible request is refused
        /// instead of rolled: worn or held gear is exactly one unit, a carried item is 1..its own effective
        /// stack cap. It mirrors <c>ItemInstance</c>'s private quantity validation, and it is not the
        /// authority for it - the issuer path re-checks quantity when the instance is built, so this gate
        /// can only ever refuse earlier, never accept an illegal quantity into an instance.
        /// </summary>
        private static bool TryValidateCarrying(ItemData definition, int quantity, out string reason)
        {
            if (definition.IsEquippable)
            {
                if (quantity != 1)
                {
                    reason = $"{definition.itemId} is worn or held, so its quantity must be exactly 1 " +
                             $"(got {quantity})";
                    return false;
                }

                reason = null;
                return true;
            }

            int limit = Math.Max(1, definition.StackLimitForFootprint);
            if (quantity < 1)
            {
                reason = $"{definition.itemId} quantity {quantity} is not at least one unit";
                return false;
            }

            if (quantity > limit)
            {
                reason = $"{definition.itemId} quantity {quantity} is above its stack limit of {limit}";
                return false;
            }

            reason = null;
            return true;
        }

        /// <summary>
        /// The eligible pool for one gear category, built from the definitions the catalog itself owns (so
        /// no caller can hand in a candidate list, and no stat key is named by a caller).
        ///
        /// Every definition is re-validated at this use boundary with the accepted authoring rules, because
        /// a ScriptableObject can be edited after the authority indexed it: a weight that became 0/NaN, a
        /// band that became reversed or wider than its prototype, or a category set that became empty is a
        /// refusal naming that entry, not a stale value that gets rolled with and not a pool that quietly
        /// shrinks. Category mismatch is the one legitimate exclusion - that is what a profile restriction
        /// means. Duplicate ids and two eligible definitions for one stat key are catalog corruption and are
        /// refused, because "distinct affix ids and distinct stat keys" is a promise this roller makes.
        ///
        /// The pool is a copy. It is sorted by stable ordinal id so seeded behavior does not depend on the
        /// order the catalog happens to be authored in, and the catalog's own order is never touched.
        /// </summary>
        private static bool TryBuildEligiblePool(ItemData trusted, AffixRarity rarity,
            ItemInstanceAuthority authority, AffixCategory category, int expected,
            out List<Candidate> pool, out string reason)
        {
            pool = new List<Candidate>();
            reason = null;

            IReadOnlyList<AffixDefinition> definitions = authority.AffixDefinitions;
            if (definitions == null)
            {
                reason = "the trusted catalog exposes no affix definitions to roll from";
                return false;
            }

            HashSet<string> seenIds = new HashSet<string>(StringComparer.Ordinal);
            HashSet<AffixStatKey> seenKeys = new HashSet<AffixStatKey>();

            for (int i = 0; i < definitions.Count; i++)
            {
                AffixDefinition definition = definitions[i];
                if (definition == null)
                {
                    reason = $"trusted affix entry {i} is null";
                    return false;
                }

                if (!definition.TryValidate(out string definitionReason))
                {
                    reason = $"trusted affix entry '{definition.id}' is not valid for rolling: " +
                             $"{definitionReason}";
                    return false;
                }

                if (!AffixBandRules.IsFinite(definition.weight) || definition.weight <= 0f)
                {
                    reason = $"trusted affix entry '{definition.id}' has weight {definition.weight}, which is " +
                             $"not a positive finite number";
                    return false;
                }

                if (!seenIds.Add(definition.id))
                {
                    reason = $"trusted affix entry {i}: duplicate affix id '{definition.id}'";
                    return false;
                }

                // Not allowed on this gear family: a legitimate profile restriction, not a defect.
                if (!definition.AllowsCategory(category)) continue;

                if (!seenKeys.Add(definition.statKey))
                {
                    reason = $"two affix definitions claim stat key '{definition.CanonicalKey}' and both are " +
                            $"eligible for {AffixCategories.Describe(category)}: one key may have exactly one " +
                            $"definition in a catalog";
                    return false;
                }

                if (!TryReadGrid(definition, out Candidate candidate, out reason)) return false;
                pool.Add(candidate);
            }

            if (pool.Count < expected)
            {
                reason = $"{trusted.itemId} at rarity {rarity} needs {expected} distinct affix definition(s) " +
                         $"eligible for {AffixCategories.Describe(category)}, the trusted catalog has " +
                         $"{pool.Count}: the affix count is never lowered to fit the catalog";
                return false;
            }

            pool.Sort(CompareByStableId);
            return true;
        }

        private static int CompareByStableId(Candidate a, Candidate b)
        {
            return string.Compare(a.Definition.id, b.Definition.id, StringComparison.Ordinal);
        }

        /// <summary>
        /// Turn one already-valid band into a grid this roller can actually sample: the number of whole
        /// steps between the authored endpoints, measured the same way the accepted band rules measure
        /// alignment (residual in fractions of one step, bounded by these inputs' own representation budget
        /// and by the largest whole index a double can hold). A band whose endpoints cannot be shown to sit
        /// on a whole number of steps is reported as unsampleable instead of being rounded, truncated or
        /// rolled approximately.
        /// </summary>
        private static bool TryReadGrid(AffixDefinition definition, out Candidate candidate, out string reason)
        {
            candidate = new Candidate();

            double min = definition.minValue;
            double max = definition.maxValue;
            double step = definition.step;

            double span = max - min;
            if (!AffixBandRules.IsFinite(span))
            {
                reason = $"affix '{definition.id}': band {min}..{max} does not have a finite span";
                return false;
            }

            double raw = span / step;
            double nearest = Math.Round(raw, MidpointRounding.AwayFromZero);

            if (!AffixBandRules.IsFinite(raw) || Math.Abs(nearest) > AffixBandRules.MaxValidGridIndex)
            {
                reason = $"affix '{definition.id}': band {min}..{max} step {step} spans {raw} steps, outside " +
                         $"the whole-index range a double can hold ({AffixBandRules.MaxValidGridIndex}); this " +
                         $"grid cannot be sampled";
                return false;
            }

            double allowance = AffixBandRules.AlignmentAllowanceInSteps(min, max, step);
            if (allowance > AffixBandRules.MaxAlignmentSteps)
            {
                reason = $"affix '{definition.id}': step {step} is finer than its own {min}..{max} endpoints " +
                         $"can be represented in (up to {allowance} steps of error, more than " +
                         $"{AffixBandRules.MaxAlignmentSteps}); this grid cannot be sampled";
                return false;
            }

            double residualSteps = Math.Abs(span - nearest * step) / Math.Abs(step);
            if (residualSteps > allowance)
            {
                reason = $"affix '{definition.id}': band {min}..{max} does not end on a whole number of steps " +
                         $"of {step} ({residualSteps} steps off the whole index {nearest}); this grid cannot " +
                         $"be sampled";
                return false;
            }

            if (nearest < 0.0)
            {
                reason = $"affix '{definition.id}': band {min}..{max} has a negative step count {nearest}";
                return false;
            }

            // nearest is bounded by MaxValidGridIndex (4.5e15), far below long.MaxValue, so this cast and
            // the +1 for the bottom endpoint cannot overflow.
            long stepCount = (long)nearest;

            candidate.Definition = definition;
            candidate.Key = definition.statKey;
            candidate.Weight = definition.weight;
            candidate.Min = min;
            candidate.Step = step;
            candidate.GridCount = stepCount + 1;

            reason = null;
            return true;
        }

        // ------------------------------------------------------------------------- sampling

        /// <summary>
        /// Weighted sampling without replacement, exactly <paramref name="expected"/> draws, each one from
        /// the entries still unused in this roll. A picked definition is removed from the pool, and so is
        /// its stat key by construction (the pool holds one entry per key), so one item can never carry the
        /// same affix twice or two affixes on the same key. The loop count is fixed by the rarity, so it
        /// cannot spin: an exhausted pool is a refusal, not a retry.
        /// </summary>
        private static bool TrySample(List<Candidate> pool, int expected, AffixCategory category,
            ILootRng random, out AffixRecord[] records, out string reason)
        {
            records = null;

            bool[] used = new bool[pool.Count];
            AffixRecord[] rolled = new AffixRecord[expected];

            for (int draw = 0; draw < expected; draw++)
            {
                if (!TryPickWeighted(pool, used, random, out int chosen, out reason)) return false;

                used[chosen] = true;
                Candidate candidate = pool[chosen];

                if (!TryDrawGridIndex(random, candidate.GridCount, out long index, out reason)) return false;

                // The value is built from the validated endpoints and the validated step, on the grid,
                // inclusive of both endpoints: min + index*step. It is then handed to the accepted
                // rolled-value boundary (band, range, grid and integrality, plus the target category) before
                // it is recorded, so nothing that the instance path would refuse leaves this method.
                double value = candidate.Min + (double)index * candidate.Step;
                if (!AffixBandRules.IsFinite(value))
                {
                    reason = $"affix '{candidate.Definition.id}': grid point {index} of step {candidate.Step} " +
                             $"from {candidate.Min} is not a finite number";
                    return false;
                }

                float accepted = (float)value;

                if (!candidate.Definition.TryValidateRolledValue(accepted, category, out string valueReason))
                {
                    reason = $"affix '{candidate.Definition.id}': grid point {index} produced {accepted}, which " +
                             $"this definition does not accept ({valueReason})";
                    return false;
                }

                rolled[draw] = new AffixRecord(candidate.Definition.id, candidate.Key, accepted);
            }

            records = rolled;
            reason = null;
            return true;
        }

        /// <summary>
        /// One weighted pick over the entries still available. Totals are accumulated as doubles, in the
        /// same order in both passes, so the running cumulative sum ends exactly where the ticket was drawn
        /// from and a ticket below the total must cross some cumulative sum. Weights are only ever read.
        /// </summary>
        private static bool TryPickWeighted(List<Candidate> pool, bool[] used, ILootRng random,
            out int chosen, out string reason)
        {
            chosen = -1;
            reason = null;

            double total = 0.0;
            int remaining = 0;

            for (int i = 0; i < pool.Count; i++)
            {
                if (used[i]) continue;
                remaining++;
                total += pool[i].Weight;
            }

            if (remaining == 0)
            {
                reason = "the eligible affix pool is exhausted before the requested affix count was filled";
                return false;
            }

            if (!AffixBandRules.IsFinite(total) || total <= 0.0)
            {
                reason = $"the remaining eligible weights do not add up to a positive finite total " +
                         $"(got {total})";
                return false;
            }

            double ticket = random.NextDouble() * total;
            double running = 0.0;
            int lastUsable = -1;

            for (int i = 0; i < pool.Count; i++)
            {
                if (used[i]) continue;
                lastUsable = i;
                running += pool[i].Weight;

                if (ticket < running)
                {
                    chosen = i;
                    return true;
                }
            }

            // Same summation order in both passes means running ends at total and a legal ticket is below
            // total, so this is unreachable for a legal draw; it exists so a draw that somehow did not land
            // still selects a real remaining entry instead of failing or inventing one.
            if (lastUsable >= 0) chosen = lastUsable;

            if (chosen < 0)
            {
                reason = $"no eligible affix was selected from a total weight of {total}";
                return false;
            }

            return true;
        }

        /// <summary>
        /// Which grid point of an eligible band this roll uses: a whole index in [0, GridCount - 1], i.e.
        /// min and max are both reachable. An exact integer draw is used whenever the grid fits an int;
        /// only an absurdly fine grid falls back to a scaled double draw, and that is clamped into range
        /// rather than allowed to produce an out-of-range or fractional index. A band with a single legal
        /// value (min == max) is that value and consumes no draw.
        /// </summary>
        private static bool TryDrawGridIndex(ILootRng random, long gridCount, out long index,
            out string reason)
        {
            index = 0;
            reason = null;

            if (gridCount <= 1) return true;

            if (gridCount <= int.MaxValue)
            {
                long drawn = random.Next((int)gridCount);
                if (drawn < 0 || drawn >= gridCount)
                {
                    reason = $"the supplied generator returned index {drawn}, which is not one of the " +
                             $"{gridCount} grid points of this band";
                    return false;
                }

                index = drawn;
                return true;
            }

            double scaled = random.NextDouble() * (double)gridCount;
            double floored = Math.Floor(scaled);

            if (!AffixBandRules.IsFinite(floored) || floored < 0.0)
            {
                reason = $"the supplied generator produced {scaled}, which is not a grid index";
                return false;
            }

            if (floored > (double)(gridCount - 1)) floored = (double)(gridCount - 1);

            index = (long)floored;
            return true;
        }

        // ---------------------------------------------------------------------------- state

        /// <summary>
        /// One eligible definition as the roller needs it: the catalog's own object (never a copy of it, so
        /// its id/key/band/category/weight are always read from the trusted source), its weight as a double,
        /// and its validated grid. Nothing here caches a rolled result, and nothing here can write back.
        /// </summary>
        private struct Candidate
        {
            public AffixDefinition Definition;
            public AffixStatKey Key;
            public double Weight;
            public double Min;
            public double Step;
            public long GridCount;
        }
    }
}
