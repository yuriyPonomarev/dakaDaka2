using System;
using System.Collections.Generic;
using System.Text;
using Emberdelve.Data;

namespace Emberdelve.Items
{
    /// <summary>
    /// R9: the bag and the equipment of one player as ONE persistence unit. Restored atomically across both
    /// containers: both are fully validated first (each against the catalog, plus uid uniqueness across the two and
    /// against any other containers of the profile), and only then are both committed. A bad bag or a bad equipment
    /// section refuses the whole loadout and neither container changes.
    ///
    /// Same rules as <see cref="ContainerSnapshot"/>: schemaVersion, strict fields (unknown/missing/repeated are
    /// refused), canonical JSON, and a checksum over the canonical body (which includes both container documents and
    /// their own checksums). The checksum is tamper evidence, not security; server-owned storage plus validation is
    /// the real defence (U4C).
    /// </summary>
    public sealed class LoadoutSnapshot
    {
        public int schemaVersion = ContainerSnapshot.CurrentSchemaVersion;
        public ContainerSnapshot bag;
        public ContainerSnapshot equipment;
        public string checksum = "";

        /// <summary>Captures and seals the current bag and equipment. Read-only.</summary>
        public static LoadoutSnapshot Capture(Inventory bag, Equipment equipment)
        {
            LoadoutSnapshot snapshot = new LoadoutSnapshot
            {
                bag = bag != null ? bag.ToContainerSnapshot() : null,
                equipment = equipment != null ? equipment.ToContainerSnapshot() : null
            };
            return snapshot.Seal();
        }

        public LoadoutSnapshot Clone()
        {
            return new LoadoutSnapshot
            {
                schemaVersion = schemaVersion,
                bag = bag != null ? bag.Clone() : null,
                equipment = equipment != null ? equipment.Clone() : null,
                checksum = checksum
            };
        }

        /// <summary>Seals both containers (canonical order + their checksums), then this loadout's checksum.</summary>
        public LoadoutSnapshot Seal()
        {
            if (bag != null) bag.Seal();
            if (equipment != null) equipment.Seal();
            checksum = ComputeChecksum();
            return this;
        }

        public string ComputeChecksum()
        {
            return ContainerSnapshot.ChecksumOf(BuildBody(true));
        }

        public bool ChecksumMatches()
        {
            return !string.IsNullOrEmpty(checksum) && string.Equals(checksum, ComputeChecksum(), StringComparison.Ordinal);
        }

        private string BuildBody(bool forChecksum)
        {
            StringBuilder sb = new StringBuilder(512);
            sb.Append("{\"schemaVersion\":");
            StrictJson.AppendInt(sb, schemaVersion);
            sb.Append(",\"bag\":");
            if (bag != null) bag.AppendJson(sb, forChecksum); else sb.Append("null");
            sb.Append(",\"equipment\":");
            if (equipment != null) equipment.AppendJson(sb, forChecksum); else sb.Append("null");
            return sb.ToString();
        }

        public string ToJson()
        {
            StringBuilder sb = new StringBuilder(512);
            sb.Append(BuildBody(false));
            sb.Append(",\"checksum\":");
            StrictJson.AppendString(sb, checksum ?? "");
            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>Strict structural parse, canonical-only input and the same migration rules as <see cref="ContainerSnapshot.TryFromJson"/>; content validation happens in <see cref="TryRestore"/>.</summary>
        public static bool TryFromJson(string json, out LoadoutSnapshot snapshot, out string reason)
        {
            snapshot = null;
            if (!StrictJson.TryParse(json, out JsonNode root, out reason)) return false;
            if (!ContainerSchema.TryReadVersionAndUpgrade(root, "loadout snapshot", out bool migrated, out reason)) return false;

            if (!StrictJson.TryExpectObject(root, "loadout snapshot", new[] { "schemaVersion", "bag", "equipment", "checksum" },
                    out Dictionary<string, JsonNode> map, out reason)) return false;

            LoadoutSnapshot result = new LoadoutSnapshot();
            if (!StrictJson.TryInt(map["schemaVersion"], "loadout schemaVersion", out result.schemaVersion, out reason)) return false;
            if (!ContainerSnapshot.TryRead(map["bag"], "loadout bag", out result.bag, out bool bagMigrated, out reason)) return false;
            if (!ContainerSnapshot.TryRead(map["equipment"], "loadout equipment", out result.equipment, out bool gearMigrated, out reason)) return false;
            if (!StrictJson.TryString(map["checksum"], "loadout checksum", out result.checksum, out reason)) return false;

            if (!migrated && (bagMigrated || gearMigrated))
            {
                reason = "loadout snapshot is current but one of its containers is from an older schema version: the wrapper and its containers share one version";
                return false;
            }

            if (migrated)
            {
                result.Seal(); // the original loadout's integrity was verified before migration
            }
            else if (!string.Equals(result.ToJson(), json, StringComparison.Ordinal))
            {
                reason = "loadout snapshot is not in canonical form (its re-serialization differs from the text): " +
                         "whitespace, escapes, member order, number spelling or -0 are not accepted";
                return false;
            }

            snapshot = result;
            return true;
        }

        /// <summary>
        /// Restores bag and equipment as one unit, all or nothing, and as one step for observers: both containers are
        /// validated first, both are applied with no event raised, and only then is anyone notified, so no observer
        /// ever sees a uid both worn and carried. <paramref name="others"/> is the profile's uid ledger; it may still
        /// hold the OLD 'bag' and 'equipment' holdings (a uid that moved between them is not a conflict), is checked
        /// as if those two owners were already released, and is updated atomically with the restore. Null = no ledger.
        /// </summary>
        public static bool TryRestore(LoadoutSnapshot snapshot, Inventory bag, Equipment equipment, ItemUidRegistry others,
            out string reason)
        {
            if (snapshot == null)
            {
                reason = "no loadout snapshot";
                return false;
            }

            if (bag == null || equipment == null)
            {
                reason = "a loadout restore needs both a bag and an equipment component";
                return false;
            }

            if (!ReferenceEquals(equipment.BagPartner(), bag))
            {
                reason = "this equipment does not belong to this bag";
                return false;
            }

            if (!ContainerSchema.IsCurrent(snapshot.schemaVersion, "loadout snapshot", out reason)) return false;

            if (snapshot.bag == null || snapshot.equipment == null)
            {
                reason = "loadout snapshot is missing its bag or equipment section";
                return false;
            }

            if (string.IsNullOrEmpty(snapshot.checksum))
            {
                reason = "loadout snapshot has no checksum";
                return false;
            }

            if (!ContainerSnapshot.IsChecksumShape(snapshot.checksum))
            {
                reason = "loadout checksum is malformed (expected 'sha256:' and 64 lowercase hex digits)";
                return false;
            }

            if (!snapshot.ChecksumMatches())
            {
                reason = "loadout checksum does not match its content: the snapshot was changed after it was written";
                return false;
            }

            // Validate BOTH halves before anything is committed. The partner checks are skipped on purpose: both
            // containers are being replaced, so the uids they hold today are about to be released.
            ItemUidRegistry view = others != null ? others.WithoutOwners(Inventory.SnapshotContainerId, Equipment.SnapshotContainerId) : null;

            if (!bag.TryPrepareRestore(snapshot.bag, view, false, out ValidatedContainer validatedBag, out string bagReason))
            {
                reason = "bag section refused: " + bagReason;
                return false;
            }

            if (!equipment.TryPrepareRestore(snapshot.equipment, view, false, out ValidatedContainer validatedGear, out string gearReason))
            {
                reason = "equipment section refused: " + gearReason;
                return false;
            }

            HashSet<Guid> bagUids = new HashSet<Guid>(validatedBag.Uids);
            for (int i = 0; i < validatedGear.Uids.Count; i++)
            {
                if (bagUids.Contains(validatedGear.Uids[i]))
                {
                    reason = $"uid {validatedGear.Uids[i]:N} is in both the bag and the equipment sections: one uid is one item";
                    return false;
                }
            }

            if (!bag.CanCommitRevision(out reason)) return false;

            // Both validated and disjoint. The ledger moves both owners in one atomic step (it was checked above, so
            // this cannot refuse; if it ever did, nothing has been applied yet).
            if (others != null)
            {
                Dictionary<string, IReadOnlyCollection<Guid>> holdings = new Dictionary<string, IReadOnlyCollection<Guid>>
                {
                    { Inventory.SnapshotContainerId, validatedBag.Uids },
                    { Equipment.SnapshotContainerId, validatedGear.Uids }
                };
                if (!others.TryReplaceOwners(holdings, out reason)) return false;
            }

            // Bag first (silent), then the equipment: its slots are replaced before any hand or pool change it causes
            // can run a subscriber, so by then both containers are final. Then notify once.
            bag.ApplyRestore(validatedBag);
            List<ItemData> removed = equipment.ApplyRestore(validatedGear);
            bag.CommitRevision();
            equipment.NotifyRestore(removed, validatedGear); // raises InventoryChanged once, then the equipment events
            reason = null;
            return true;
        }

        /// <summary>Parse then restore, for a store that holds the loadout as JSON text.</summary>
        public static bool TryRestoreJson(string json, Inventory bag, Equipment equipment, ItemUidRegistry others, out string reason)
        {
            if (!TryFromJson(json, out LoadoutSnapshot snapshot, out reason)) return false;
            return TryRestore(snapshot, bag, equipment, others, out reason);
        }
    }
}
