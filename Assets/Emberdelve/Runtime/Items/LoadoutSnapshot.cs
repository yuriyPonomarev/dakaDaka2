using System;
using System.Collections.Generic;
using System.Text;
using Emberdelve.Data;
using Emberdelve.Core;

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
    ///
    /// D3c (schemaVersion 4): the wrapper also carries the live weapon set (<see cref="weaponSet"/>, 0 = Weapon + OffHand,
    /// 1 = AltWeapon + AltOffHand) and the utility belt (<see cref="belt"/>: four identities, "" or "i:" + item id or "u:" + uid).
    /// A model whose schemaVersion is below 4 is written WITHOUT them, byte for byte as the older versions wrote the wrapper
    /// (that is how an old document is rebuilt for its integrity check), and a version 4 document must carry both.
    /// </summary>
    public sealed class LoadoutSnapshot
    {
        /// <summary>D3c: the belt has six entries split between the 3/4 quick wheels.</summary>
        public const int BeltSlotCount = HotbarBindings.SlotCount;

        public int schemaVersion = ContainerSnapshot.CurrentSchemaVersion;
        public ContainerSnapshot bag;
        public ContainerSnapshot equipment;
        /// <summary>D3c (version 4): the weapon set in hand, 0 or 1.</summary>
        public int weaponSet;
        /// <summary>D3c (version 4): the utility belt, one text per key: "" (empty), "i:" + itemId (a carried item) or "u:" + 32 hex digits (an item with an identity).</summary>
        public string[] belt = new string[BeltSlotCount] { "", "", "", "", "", "" };
        public string checksum = "";

        /// <summary>Captures and seals the current bag and equipment. Read-only.</summary>
        public static LoadoutSnapshot Capture(Inventory bag, Equipment equipment)
        {
            LoadoutSnapshot snapshot = new LoadoutSnapshot
            {
                bag = bag != null ? bag.ToContainerSnapshot() : null,
                equipment = equipment != null ? equipment.ToContainerSnapshot() : null,
                weaponSet = equipment != null ? equipment.ActiveWeaponSet : 0
            };

            if (bag != null)
            {
                BeltIdentity[] identities = bag.Hotbar.CaptureIdentities();
                for (int i = 0; i < BeltSlotCount; i++) snapshot.belt[i] = identities[i].ToSnapshotText();
            }

            return snapshot.Seal();
        }

        public LoadoutSnapshot Clone()
        {
            return new LoadoutSnapshot
            {
                schemaVersion = schemaVersion,
                bag = bag != null ? bag.Clone() : null,
                equipment = equipment != null ? equipment.Clone() : null,
                weaponSet = weaponSet,
                belt = belt != null ? (string[])belt.Clone() : null,
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

            if (schemaVersion >= 4)
            {
                sb.Append(",\"weaponSet\":");
                StrictJson.AppendInt(sb, weaponSet);
                sb.Append(",\"belt\":[");
                if (belt != null)
                {
                    for (int i = 0; i < belt.Length; i++)
                    {
                        if (i > 0) sb.Append(',');
                        StrictJson.AppendString(sb, belt[i] ?? "");
                    }
                }
                sb.Append(']');
            }

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

        /// <summary>
        /// Structure-only read of a loadout tree with no version handling and no migration, for an older version's
        /// integrity check (the wrapper checksum covers both containers' text, so the model is rebuilt to recompute it).
        /// </summary>
        internal static bool TryReadFieldsForVerification(JsonNode root, out LoadoutSnapshot result, out string reason)
        {
            result = null;

            if (!StrictJson.TryInt(ContainerSchema.Member(root, "schemaVersion"), "loadout schemaVersion", out int version, out reason)) return false;
            string[] fields = version >= 4
                ? new[] { "schemaVersion", "bag", "equipment", "weaponSet", "belt", "checksum" }
                : new[] { "schemaVersion", "bag", "equipment", "checksum" };
            if (!StrictJson.TryExpectObject(root, "loadout snapshot", fields,
                    out Dictionary<string, JsonNode> map, out reason)) return false;

            LoadoutSnapshot parsed = new LoadoutSnapshot();
            if (!StrictJson.TryInt(map["schemaVersion"], "loadout schemaVersion", out parsed.schemaVersion, out reason)) return false;
            if (!ContainerSnapshot.TryReadFields(map["bag"], "loadout bag", out parsed.bag, out reason)) return false;
            if (!ContainerSnapshot.TryReadFields(map["equipment"], "loadout equipment", out parsed.equipment, out reason)) return false;
            if (!StrictJson.TryString(map["checksum"], "loadout checksum", out parsed.checksum, out reason)) return false;

            if (version >= 4)
            {
                if (!StrictJson.TryInt(map["weaponSet"], "loadout weaponSet", out parsed.weaponSet, out reason)) return false;
                if (parsed.weaponSet < 0 || parsed.weaponSet > 1) { reason = "invalid legacy weapon set"; return false; }
                if (!StrictJson.TryArray(map["belt"], "legacy belt", 4, out List<JsonNode> entries, out reason)) return false;
                if (entries.Count != 4) { reason = "a version 4 belt has exactly four entries"; return false; }
                parsed.belt = new string[4];
                for (int i = 0; i < 4; i++)
                    if (!StrictJson.TryString(entries[i], "legacy belt entry", out parsed.belt[i], out reason)
                        || !TryParseBeltText(parsed.belt[i], null, out _, out reason)) return false;
            }

            result = parsed;
            return true;
        }

        /// <summary>Strict structural parse, canonical-only input and the same migration rules as <see cref="ContainerSnapshot.TryFromJson"/>; content validation happens in <see cref="TryRestore"/>.</summary>
        public static bool TryFromJson(string json, out LoadoutSnapshot snapshot, out string reason)
        {
            snapshot = null;
            if (!StrictJson.TryParse(json, out JsonNode root, out reason)) return false;
            if (!ContainerSchema.TryReadVersionAndUpgrade(root, "loadout snapshot", out bool migrated, out reason)) return false;

            if (!StrictJson.TryExpectObject(root, "loadout snapshot", new[] { "schemaVersion", "bag", "equipment", "weaponSet", "belt", "checksum" },
                    out Dictionary<string, JsonNode> map, out reason)) return false;

            LoadoutSnapshot result = new LoadoutSnapshot();
            if (!StrictJson.TryInt(map["schemaVersion"], "loadout schemaVersion", out result.schemaVersion, out reason)) return false;
            if (!ContainerSnapshot.TryRead(map["bag"], "loadout bag", out result.bag, out bool bagMigrated, out reason)) return false;
            if (!ContainerSnapshot.TryRead(map["equipment"], "loadout equipment", out result.equipment, out bool gearMigrated, out reason)) return false;
            if (!StrictJson.TryInt(map["weaponSet"], "loadout weaponSet", out result.weaponSet, out reason)) return false;
            if (!StrictJson.TryArray(map["belt"], "loadout belt", BeltSlotCount, out List<JsonNode> beltNodes, out reason)) return false;
            result.belt = new string[beltNodes.Count];
            for (int i = 0; i < beltNodes.Count; i++)
            {
                if (!StrictJson.TryString(beltNodes[i], $"loadout belt key {i + 1}", out result.belt[i], out reason)) return false;
            }
            if (!TryCheckWeaponSetAndBelt(result.weaponSet, result.belt, out reason)) return false;
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
        /// D3c: the shape of the version 4 members, with no catalog needed: weaponSet is 0 or 1; the belt has exactly one text per
        /// key, each "" or "i:" + a non-empty item id or "u:" + 32 lowercase hex digits. Whether an item id exists is the restore's job.
        /// </summary>
        internal static bool TryCheckWeaponSetAndBelt(int weaponSet, string[] belt, out string reason)
        {
            if (weaponSet < 0 || weaponSet >= EquipmentSlots.WeaponSetCount)
            {
                reason = $"loadout weaponSet {weaponSet} is not a weapon set (0 or 1)";
                return false;
            }

            if (belt == null || belt.Length != BeltSlotCount)
            {
                reason = $"loadout belt must hold exactly {BeltSlotCount} keys (it holds {(belt == null ? 0 : belt.Length)})";
                return false;
            }

            for (int i = 0; i < belt.Length; i++)
            {
                if (!TryParseBeltText(belt[i], null, out _, out string why))
                {
                    reason = $"loadout belt key {i + 1}: {why}";
                    return false;
                }
            }

            reason = null;
            return true;
        }

        /// <summary>
        /// One belt text to an identity. "" is empty; "u:" + uid is an identity by uid; "i:" + itemId is an identity by definition and,
        /// when a catalog is given, must name an item of it. Pure apart from the catalog lookup.
        /// </summary>
        internal static bool TryParseBeltText(string text, ItemCatalog catalog, out BeltIdentity identity, out string reason)
        {
            identity = default;
            if (text == null)
            {
                reason = "a belt key may not be null";
                return false;
            }

            if (text.Length == 0)
            {
                reason = null;
                return true;
            }

            if (text.StartsWith("u:", StringComparison.Ordinal))
            {
                string hex = text.Substring(2);
                bool shape = hex.Length == 32;
                for (int i = 0; shape && i < hex.Length; i++)
                {
                    char c = hex[i];
                    shape = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
                }

                if (!shape || !Guid.TryParseExact(hex, "N", out Guid uid) || uid == Guid.Empty)
                {
                    reason = $"'{text}' is not 'u:' and 32 lowercase hex digits";
                    return false;
                }

                identity = new BeltIdentity(null, uid);
                reason = null;
                return true;
            }

            if (text.StartsWith("i:", StringComparison.Ordinal) && text.Length > 2)
            {
                string itemId = text.Substring(2);
                if (catalog != null)
                {
                    if (!catalog.TryResolveItem(itemId, out ItemData definition, out string why) || definition == null)
                    {
                        reason = $"names the item '{itemId}', which the catalog does not have ({why})";
                        return false;
                    }
                    identity = new BeltIdentity(definition, Guid.Empty);
                }

                reason = null;
                return true;
            }

            reason = $"'{text}' is neither empty, 'i:' + an item id, nor 'u:' + a uid";
            return false;
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

            if (!TryCheckWeaponSetAndBelt(snapshot.weaponSet, snapshot.belt, out reason)) return false;

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

            // D3c: the weapon set in hand must have a weapon to hold once the equipment is restored, and every belt key must read.
            if (!equipment.WouldHoldWeapon(validatedGear, snapshot.weaponSet))
            {
                reason = $"weapon set {snapshot.weaponSet + 1} would hold no weapon after this restore";
                return false;
            }

            BeltIdentity[] belt = new BeltIdentity[BeltSlotCount];
            for (int i = 0; i < BeltSlotCount; i++)
            {
                if (!TryParseBeltText(snapshot.belt[i], bag.Catalog, out belt[i], out string beltReason))
                {
                    reason = $"loadout belt key {i + 1}: {beltReason}";
                    return false;
                }
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
            using (GameEventTransaction.Defer())
            {
                bag.ApplyRestore(validatedBag);
                bag.Hotbar.RebindFromIdentities(belt, allowEmpty: true); // Full loadout snapshots preserve even an explicitly empty belt.
                List<ItemData> removed = equipment.ApplyRestore(validatedGear);
                equipment.ApplyRestoredWeaponSet(snapshot.weaponSet);
                bag.CommitRevision();
                equipment.NotifyRestore(removed, validatedGear); // raises InventoryChanged once, then the equipment events
            }
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
