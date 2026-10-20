using System;
using System.Collections.Generic;

namespace Emberdelve.Data
{
    /// <summary>
    /// The catalog-boundary check over a set of affix definitions: every definition must validate on its
    /// own, stable ids must be unique, and one stat key may be authored by only one definition in the
    /// catalog. All problems are collected and reported; nothing is dropped, renamed, merged, deduplicated
    /// or clamped, and no definition is modified.
    ///
    /// Key uniqueness is a deliberate authoring rule at this boundary: an instance record is validated as
    /// an id/key/value triple, so an id must map to exactly one key and a key must not have two competing
    /// prototype bands in the same catalog. A later slice that genuinely wants two bands for one key has
    /// to change this rule on purpose, not discover it at runtime.
    /// </summary>
    public static class AffixCatalog
    {
        /// <summary>Single-reason form: the first problem found, or null when the catalog is valid.</summary>
        public static bool TryValidate(IReadOnlyList<AffixDefinition> definitions, out string reason)
        {
            List<string> reasons;
            bool valid = TryValidate(definitions, out reasons);
            reason = valid ? null : string.Join("; ", reasons);
            return valid;
        }

        /// <summary>All problems in the catalog, in authoring order. Empty list means valid.</summary>
        public static bool TryValidate(IReadOnlyList<AffixDefinition> definitions, out List<string> reasons)
        {
            reasons = new List<string>();

            if (definitions == null)
            {
                reasons.Add("affix catalog is null");
                return false;
            }

            if (definitions.Count == 0)
            {
                reasons.Add("affix catalog is empty");
                return false;
            }

            HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
            HashSet<AffixStatKey> keys = new HashSet<AffixStatKey>();

            for (int i = 0; i < definitions.Count; i++)
            {
                AffixDefinition definition = definitions[i];
                if (definition == null)
                {
                    reasons.Add($"affix catalog entry {i} is null");
                    continue;
                }

                if (!definition.TryValidate(out string definitionReason))
                {
                    reasons.Add($"affix catalog entry {i}: {definitionReason}");
                    continue;
                }

                if (!ids.Add(definition.id))
                {
                    reasons.Add($"affix catalog entry {i}: duplicate affix id '{definition.id}'");
                }

                if (!keys.Add(definition.statKey))
                {
                    reasons.Add($"affix catalog entry {i}: duplicate affix stat key " +
                                $"'{AffixStatKeys.Key(definition.statKey)}'");
                }
            }

            return reasons.Count == 0;
        }
    }
}
