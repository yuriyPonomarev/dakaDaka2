using System;
using System.Collections.Generic;

namespace Emberdelve.Items
{
    /// <summary>
    /// R9: the uniqueness ledger for item UIDs across every container of one profile (bag, equipment, stash, ...).
    /// One uid is one item, so a uid may be held by exactly one container at a time. The future stash/profile store
    /// (U4C) keeps one registry per profile, hands it to <see cref="ContainerSnapshotValidator.TryValidate"/> as the
    /// "held elsewhere" set, and commits a restored container through <see cref="TryReplaceOwner"/>.
    ///
    /// An owner is a container id (<see cref="ContainerSnapshot.containerId"/>); container ids must therefore be
    /// unique within a profile. Every mutating method is atomic: on a refusal the ledger is unchanged. This is an
    /// in-memory check for this process, not a server-side ownership service.
    /// </summary>
    public sealed class ItemUidRegistry
    {
        private readonly Dictionary<Guid, string> _owners = new Dictionary<Guid, string>();

        public int Count => _owners.Count;

        public bool Contains(Guid uid) => _owners.ContainsKey(uid);

        public bool TryGetOwner(Guid uid, out string owner) => _owners.TryGetValue(uid, out owner);

        /// <summary>
        /// Pure check: could <paramref name="ownerId"/> hold exactly these uids? Refused when the list repeats a uid, or
        /// when any uid is currently held by a DIFFERENT owner. Holdings the same owner already has are fine (a restore
        /// replaces them).
        /// </summary>
        public bool CanHold(string ownerId, IEnumerable<Guid> uids, out string reason)
        {
            if (string.IsNullOrEmpty(ownerId))
            {
                reason = "a uid registry owner needs a container id";
                return false;
            }

            if (uids == null)
            {
                reason = null;
                return true;
            }

            HashSet<Guid> seen = new HashSet<Guid>();
            foreach (Guid uid in uids)
            {
                if (uid == Guid.Empty)
                {
                    reason = "the all-zero uid cannot be registered";
                    return false;
                }

                if (!seen.Add(uid))
                {
                    reason = $"uid {uid:N} appears twice in '{ownerId}': one uid is one item";
                    return false;
                }

                if (_owners.TryGetValue(uid, out string other) && !string.Equals(other, ownerId, StringComparison.Ordinal))
                {
                    reason = $"uid {uid:N} is already held by container '{other}', so '{ownerId}' cannot also hold it: one uid is one item";
                    return false;
                }
            }

            reason = null;
            return true;
        }

        /// <summary>
        /// Atomically makes <paramref name="ownerId"/> hold exactly <paramref name="uids"/>: whatever it held before is
        /// released, the new set is registered. Refused (ledger untouched) under the same rules as <see cref="CanHold"/>.
        /// </summary>
        public bool TryReplaceOwner(string ownerId, IReadOnlyCollection<Guid> uids, out string reason)
        {
            if (!CanHold(ownerId, uids, out reason)) return false;

            List<Guid> mine = new List<Guid>();
            foreach (KeyValuePair<Guid, string> pair in _owners)
            {
                if (string.Equals(pair.Value, ownerId, StringComparison.Ordinal)) mine.Add(pair.Key);
            }

            for (int i = 0; i < mine.Count; i++) _owners.Remove(mine[i]);
            if (uids != null)
            {
                foreach (Guid uid in uids) _owners[uid] = ownerId;
            }

            return true;
        }

        /// <summary>
        /// A copy of this ledger without the named owners' holdings: the "held elsewhere" view for a restore that is
        /// about to replace those owners (a loadout restore replaces both 'bag' and 'equipment', so a uid that moved
        /// between them since the ledger was filled is not a conflict). The original is untouched.
        /// </summary>
        public ItemUidRegistry WithoutOwners(params string[] ownerIds)
        {
            ItemUidRegistry view = new ItemUidRegistry();
            foreach (KeyValuePair<Guid, string> pair in _owners)
            {
                if (Array.IndexOf(ownerIds, pair.Value) < 0) view._owners[pair.Key] = pair.Value;
            }
            return view;
        }

        /// <summary>
        /// Atomically replaces SEVERAL owners at once (a loadout restore moves uids between bag and equipment in one
        /// step). Refused, ledger untouched, when a uid is listed twice across the new holdings or is held by an owner
        /// that is not being replaced.
        /// </summary>
        public bool TryReplaceOwners(IReadOnlyDictionary<string, IReadOnlyCollection<Guid>> holdings, out string reason)
        {
            ItemUidRegistry others = WithoutOwners(new List<string>(holdings.Keys).ToArray());
            HashSet<Guid> seen = new HashSet<Guid>();
            foreach (KeyValuePair<string, IReadOnlyCollection<Guid>> pair in holdings)
            {
                if (!others.CanHold(pair.Key, pair.Value, out reason)) return false;
                foreach (Guid uid in pair.Value)
                {
                    if (!seen.Add(uid))
                    {
                        reason = $"uid {uid:N} would be held by two containers of the same restore: one uid is one item";
                        return false;
                    }
                }
            }

            foreach (string owner in holdings.Keys) ReleaseOwner(owner);
            foreach (KeyValuePair<string, IReadOnlyCollection<Guid>> pair in holdings)
            {
                foreach (Guid uid in pair.Value) _owners[uid] = pair.Key;
            }

            reason = null;
            return true;
        }

        public void ReleaseOwner(string ownerId)
        {
            List<Guid> mine = new List<Guid>();
            foreach (KeyValuePair<Guid, string> pair in _owners)
            {
                if (string.Equals(pair.Value, ownerId, StringComparison.Ordinal)) mine.Add(pair.Key);
            }
            for (int i = 0; i < mine.Count; i++) _owners.Remove(mine[i]);
        }

        /// <summary>
        /// Convenience for a store: fully validate the snapshot against the catalog with THIS registry as the set of
        /// uids held elsewhere, and only if it is legal register its uids under its container id. Nothing is registered
        /// on a refusal.
        /// </summary>
        public bool TryRegisterSnapshot(ContainerSnapshot snapshot, ItemCatalog catalog, out string reason)
        {
            if (!ContainerSnapshotValidator.TryValidate(snapshot, catalog, this, null, out ValidatedContainer validated, out reason))
                return false;

            return TryReplaceOwner(validated.ContainerId, validated.Uids, out reason);
        }
    }
}
