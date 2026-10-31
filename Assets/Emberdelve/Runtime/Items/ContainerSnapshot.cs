using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Emberdelve.Data;

namespace Emberdelve.Items
{
    /// <summary>The container kinds a snapshot may describe. Plain strings so the format stays readable and extensible.</summary>
    public static class ContainerKinds
    {
        public const string Bag = "bag";
        public const string Equipment = "equipment";
        /// <summary>Reserved for the server stash (U4C). A grid container exactly like the bag.</summary>
        public const string Stash = "stash";

        public static bool IsKnown(string kind) => kind == Bag || kind == Equipment || kind == Stash;
        public static bool IsGrid(string kind) => kind == Bag || kind == Stash;
    }

    /// <summary>
    /// One thing in a container: where it is (a grid origin cell, or an equipment slot name) and what it is
    /// (<see cref="ItemSnapshot"/>). A carried stack (coins, potions, arrows, relics) has an empty uid, rarity 0 and no
    /// affixes - it is an item id and a quantity. A gear instance has its uid, rarity and rolled affixes.
    /// Plain public fields; the persistence JSON is written by <see cref="ContainerSnapshot.ToJson"/>, not JsonUtility.
    /// </summary>
    public sealed class ContainerEntry
    {
        /// <summary>Equipment slot name (an <see cref="EquipmentSlot"/> name) for an equipment entry, "" for a grid entry.</summary>
        public string slot = "";
        /// <summary>Origin (top-left) cell of a grid entry. Zero for an equipment entry.</summary>
        public int x;
        public int y;
        public ItemSnapshot item;

        public ContainerEntry Clone()
        {
            return new ContainerEntry { slot = slot, x = x, y = y, item = item != null ? item.Clone() : null };
        }
    }

    /// <summary>
    /// R9: the versioned, validated, atomic persistence unit for one item container (the bag, the equipment, later
    /// the stash). Plain serializable data plus a canonical JSON form; the rules that decide whether a snapshot may
    /// be restored live in <see cref="ContainerSnapshotValidator"/> and the catalog.
    ///
    /// Format (schemaVersion 4; D3c): kind, containerId, grid width/height (0x0 for equipment), entries (canonically
    /// ordered: grid entries by row then column, equipment entries by slot number) and a checksum. Version 2 is the
    /// same layout as version 1; what it adds is the meaning of the item "rarity" int: the stored ladder is now
    /// Common 0, Uncommon 1, Rare 2, Epic 3, Legendary 4, Unique 5, Poor 6, and 7 or more is refused. A version 1
    /// document is migrated while it is parsed (its checksum is verified under the version 1 rule first, its rarities
    /// must be 0..2 because nothing else existed then, and the result is re-sealed as version 2) - old saves load
    /// with every item, rarity and rolled affix exactly as written.
    ///
    /// Version 3 (D3b) is again the same layout. What it adds is five equipment slot NAMES (Hands, Feet, Necklace,
    /// RingLeft, RingRight; an equipment entry stores the slot name) and one fact about old saves: the Footpad Boots
    /// were worn in the Legs slot and now belong in Feet. A version 2 equipment entry named Legs that holds the item
    /// footpad_boots is rewritten to Feet while the document is parsed (checksum verified under the version 2 rule
    /// first, then re-sealed as version 3); a version 2 document that already names one of the five new slots was not
    /// written by version 2 and is refused. A version 1 document runs both steps, 1 to 2 and then 2 to 3.
    ///
    /// Version 4 (D3c) is again the same CONTAINER layout. What it adds is two equipment slot NAMES (AltWeapon and AltOffHand: the
    /// main hand and off hand of weapon set 2; Weapon and OffHand are set 1) and, on the LOADOUT wrapper only, two members: the
    /// live weapon set (<see cref="LoadoutSnapshot.weaponSet"/>, 0 or 1) and the utility belt (<see cref="LoadoutSnapshot.belt"/>,
    /// four item identities). A version 3 document migrates by adding weaponSet 0 and an empty belt (the Weapon and OffHand
    /// entries it already has ARE set 1; set 2 is empty) and by re-versioning; a version 3 equipment entry naming one of the two
    /// new slots was not written by version 3 and is refused. Chain 1 to 2 to 3 to 4.
    ///
    /// The checksum is TAMPER EVIDENCE, NOT SECURITY. It is an unkeyed SHA-256 over the canonical content (the same layout as the JSON, but floats as exact bit patterns so it does not depend on how a runtime spells a float) of everything
    /// except the checksum field. It catches accidental corruption and casual hand edits (a changed value, a swapped
    /// uid, a duplicated entry, a stale checksum). Anyone who edits the file can recompute it, so it proves nothing
    /// about who wrote the data. The real defence is that the profile/stash file lives on the SERVER, is written only
    /// by server code, and every restore re-validates the content against the trusted item catalog (item ids, affix
    /// ids and keys, bands and steps, category, rarity/affix counts, quantities, placement, uid uniqueness).
    /// Server authentication of a profile and the on-disk store are U4C.
    ///
    /// Unknown fields policy: REJECT. A document with a field this version does not define, a missing field, or a
    /// repeated field is refused (JsonUtility would silently ignore or default them).
    /// </summary>
    public sealed class ContainerSnapshot
    {
        public const int CurrentSchemaVersion = 4;
        public const int MinSupportedSchemaVersion = 1;
        /// <summary>Largest number of entries a snapshot may carry (a 12x12 bag cannot exceed 144; this is a hard sanity cap).</summary>
        public const int MaxEntries = 512;
        public const int MaxGridDimension = 64;
        /// <summary>Parser-level cap on an item's affix array; the rarity rule (0..5 affixes by tier) is enforced by the validator.</summary>
        public const int MaxAffixesPerItemInDocument = 8;
        public const string ChecksumPrefix = "sha256:";

        public int schemaVersion = CurrentSchemaVersion;
        public string kind = ContainerKinds.Bag;
        public string containerId = "";
        public int width;
        public int height;
        public ContainerEntry[] entries = new ContainerEntry[0];
        public string checksum = "";

        public int EntryCount => entries == null ? 0 : entries.Length;

        public ContainerSnapshot Clone()
        {
            ContainerSnapshot copy = new ContainerSnapshot
            {
                schemaVersion = schemaVersion,
                kind = kind,
                containerId = containerId,
                width = width,
                height = height,
                checksum = checksum
            };

            if (entries == null)
            {
                copy.entries = null;
                return copy;
            }

            copy.entries = new ContainerEntry[entries.Length];
            for (int i = 0; i < entries.Length; i++) copy.entries[i] = entries[i] != null ? entries[i].Clone() : null;
            return copy;
        }

        // ------------------------------------------------------------------ canonical form and checksum

        /// <summary>
        /// Puts the entries in canonical order: grid entries by (row, column), equipment entries by slot number
        /// (an unknown slot name sorts after the known ones, by name, so even a bad snapshot has one order).
        /// </summary>
        public void Canonicalize()
        {
            if (entries == null) return;
            bool grid = ContainerKinds.IsGrid(kind);
            Array.Sort(entries, (a, b) => CompareEntries(a, b, grid));
        }

        internal static int CompareEntries(ContainerEntry a, ContainerEntry b, bool grid)
        {
            if (a == null || b == null) return a == null ? (b == null ? 0 : 1) : -1;

            int c;
            if (grid)
            {
                c = a.y.CompareTo(b.y);
                if (c != 0) return c;
                c = a.x.CompareTo(b.x);
                if (c != 0) return c;
            }
            else
            {
                c = SlotOrder(a.slot).CompareTo(SlotOrder(b.slot));
                if (c != 0) return c;
                c = string.CompareOrdinal(a.slot, b.slot);
                if (c != 0) return c;
            }

            string ai = a.item != null ? a.item.itemId : null;
            string bi = b.item != null ? b.item.itemId : null;
            c = string.CompareOrdinal(ai, bi);
            if (c != 0) return c;
            return string.CompareOrdinal(a.item != null ? a.item.uid : null, b.item != null ? b.item.uid : null);
        }

        internal static int SlotOrder(string slotName)
        {
            if (TryParseSlot(slotName, out EquipmentSlot slot)) return (int)slot;
            return int.MaxValue;
        }

        /// <summary>Exact (ordinal, case-sensitive) slot name read. "None", "weapon", "" and numbers are not slots.</summary>
        internal static bool TryParseSlot(string name, out EquipmentSlot slot)
        {
            slot = EquipmentSlot.None;
            if (string.IsNullOrEmpty(name)) return false;

            Array values = Enum.GetValues(typeof(EquipmentSlot));
            for (int i = 0; i < values.Length; i++)
            {
                EquipmentSlot candidate = (EquipmentSlot)values.GetValue(i);
                if (candidate == EquipmentSlot.None) continue;
                if (string.Equals(candidate.ToString(), name, StringComparison.Ordinal))
                {
                    slot = candidate;
                    return true;
                }
            }

            return false;
        }

        /// <summary>Canonicalizes and (re)computes the checksum. Builders call this last.</summary>
        public ContainerSnapshot Seal()
        {
            Canonicalize();
            checksum = ComputeChecksum();
            return this;
        }

        /// <summary>The checksum this content should carry (does not read or change the stored one).</summary>
        public string ComputeChecksum()
        {
            return ChecksumOf(BuildBody(true));
        }

        public bool ChecksumMatches()
        {
            return !string.IsNullOrEmpty(checksum) && string.Equals(checksum, ComputeChecksum(), StringComparison.Ordinal);
        }

        internal static string ChecksumOf(string canonicalBody)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(canonicalBody));
                StringBuilder sb = new StringBuilder(ChecksumPrefix.Length + hash.Length * 2);
                sb.Append(ChecksumPrefix);
                for (int i = 0; i < hash.Length; i++) sb.Append(hash[i].ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        /// <summary>True when the text has the exact "sha256:" + 64 lowercase hex shape.</summary>
        internal static bool IsChecksumShape(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length != ChecksumPrefix.Length + 64) return false;
            if (!text.StartsWith(ChecksumPrefix, StringComparison.Ordinal)) return false;
            for (int i = ChecksumPrefix.Length; i < text.Length; i++)
            {
                char c = text[i];
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            }
            return true;
        }

        // ------------------------------------------------------------------ JSON

        /// <summary>
        /// The canonical JSON text (compact, fixed member order, invariant culture, round-trip floats). It writes the
        /// entries in the order they are in: a builder-made snapshot is already canonical, and a hand-built one that
        /// is not is refused by the validator rather than reordered behind its checksum.
        /// </summary>
        public string ToJson()
        {
            StringBuilder sb = new StringBuilder(256);
            AppendJson(sb, false);
            return sb.ToString();
        }

        internal void AppendJson(StringBuilder sb, bool forChecksum)
        {
            sb.Append(BuildBody(forChecksum));
            sb.Append(",\"checksum\":");
            StrictJson.AppendString(sb, checksum ?? "");
            sb.Append('}');
        }

        /// <summary>Everything except the checksum member and the closing brace. This exact text is what the checksum covers.</summary>
        internal string BuildBody(bool forChecksum)
        {
            StringBuilder sb = new StringBuilder(256);
            sb.Append("{\"schemaVersion\":");
            StrictJson.AppendInt(sb, schemaVersion);
            sb.Append(",\"kind\":");
            StrictJson.AppendString(sb, kind ?? "");
            sb.Append(",\"containerId\":");
            StrictJson.AppendString(sb, containerId ?? "");
            sb.Append(",\"width\":");
            StrictJson.AppendInt(sb, width);
            sb.Append(",\"height\":");
            StrictJson.AppendInt(sb, height);
            sb.Append(",\"entries\":[");

            if (entries != null)
            {
                for (int i = 0; i < entries.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    AppendEntry(sb, entries[i], forChecksum);
                }
            }

            sb.Append(']');
            return sb.ToString();
        }

        private static void AppendEntry(StringBuilder sb, ContainerEntry entry, bool forChecksum)
        {
            if (entry == null)
            {
                sb.Append("null");
                return;
            }

            sb.Append("{\"slot\":");
            StrictJson.AppendString(sb, entry.slot ?? "");
            sb.Append(",\"x\":");
            StrictJson.AppendInt(sb, entry.x);
            sb.Append(",\"y\":");
            StrictJson.AppendInt(sb, entry.y);
            sb.Append(",\"item\":");
            AppendItem(sb, entry.item, forChecksum);
            sb.Append('}');
        }

        private static void AppendItem(StringBuilder sb, ItemSnapshot item, bool forChecksum)
        {
            if (item == null)
            {
                sb.Append("null");
                return;
            }

            sb.Append("{\"uid\":");
            StrictJson.AppendString(sb, item.uid ?? "");
            sb.Append(",\"itemId\":");
            StrictJson.AppendString(sb, item.itemId ?? "");
            sb.Append(",\"quantity\":");
            StrictJson.AppendInt(sb, item.quantity);
            sb.Append(",\"rarity\":");
            StrictJson.AppendInt(sb, item.rarity);
            sb.Append(",\"affixes\":[");

            if (item.affixes != null)
            {
                for (int i = 0; i < item.affixes.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    AffixSnapshot a = item.affixes[i];
                    if (a == null)
                    {
                        sb.Append("null");
                        continue;
                    }

                    sb.Append("{\"id\":");
                    StrictJson.AppendString(sb, a.id ?? "");
                    sb.Append(",\"key\":");
                    StrictJson.AppendString(sb, a.key ?? "");
                    sb.Append(",\"value\":");
                    if (forChecksum) StrictJson.AppendFloatBits(sb, a.value); else StrictJson.AppendFloat(sb, a.value);
                    sb.Append('}');
                }
            }

            sb.Append("]}");
        }

        /// <summary>
        /// Strict parse of the canonical JSON: malformed JSON, a wrong type, a missing/unknown/repeated field, an
        /// unsupported schemaVersion, an oversized document or a document that is not in canonical form are refusals
        /// with a reason and a null snapshot. Canonical-only input: the text must equal, byte for byte, what
        /// <see cref="ToJson"/> writes for the parsed result (no extra whitespace, no \u escapes, fixed member order,
        /// canonical number spelling, no "-0"), so an accepted current-version document has exactly one byte form. A
        /// document from another runtime whose float formatter spells a value differently is refused as non-canonical.
        /// This is structure only: whether the content is a legal container (catalog, bands, placement, checksum) is
        /// <see cref="ContainerSnapshotValidator.TryValidate"/>, run by every restore.
        /// </summary>
        public static bool TryFromJson(string json, out ContainerSnapshot snapshot, out string reason)
        {
            snapshot = null;
            if (!StrictJson.TryParse(json, out JsonNode root, out reason)) return false;
            if (!TryRead(root, "container snapshot", out ContainerSnapshot parsed, out bool migrated, out reason)) return false;

            if (!migrated && !string.Equals(parsed.ToJson(), json, StringComparison.Ordinal))
            {
                reason = "container snapshot is not in canonical form (its re-serialization differs from the text): " +
                         "whitespace, escapes, member order, number spelling or -0 are not accepted";
                return false;
            }

            snapshot = parsed;
            return true;
        }

        internal static bool TryRead(JsonNode root, string what, out ContainerSnapshot snapshot, out bool migrated, out string reason)
        {
            snapshot = null;

            if (!ContainerSchema.TryReadVersionAndUpgrade(root, what, out migrated, out reason)) return false;
            if (!TryReadFields(root, what, out ContainerSnapshot result, out reason)) return false;

            if (migrated) result.Seal(); // integrity was verified on the ORIGINAL document before migration; write a fresh checksum
            snapshot = result;
            reason = null;
            return true;
        }

        /// <summary>
        /// Strict structural read of one container tree with NO version handling: the same field rules as a normal
        /// read, used by <see cref="TryRead"/> after the version gate and by an older version's integrity check, which
        /// has to rebuild the document under its own rule before it is migrated.
        /// </summary>
        internal static bool TryReadFields(JsonNode root, string what, out ContainerSnapshot result, out string reason)
        {
            result = null;

            if (!StrictJson.TryExpectObject(root, what,
                    new[] { "schemaVersion", "kind", "containerId", "width", "height", "entries", "checksum" },
                    out Dictionary<string, JsonNode> map, out reason)) return false;

            result = new ContainerSnapshot();

            if (!StrictJson.TryInt(map["schemaVersion"], what + " schemaVersion", out result.schemaVersion, out reason)) return false;
            if (!StrictJson.TryString(map["kind"], what + " kind", out result.kind, out reason)) return false;
            if (!StrictJson.TryString(map["containerId"], what + " containerId", out result.containerId, out reason)) return false;
            if (!StrictJson.TryInt(map["width"], what + " width", out result.width, out reason)) return false;
            if (!StrictJson.TryInt(map["height"], what + " height", out result.height, out reason)) return false;
            if (!StrictJson.TryString(map["checksum"], what + " checksum", out result.checksum, out reason)) return false;

            if (!StrictJson.TryArray(map["entries"], what + " entries", MaxEntries, out List<JsonNode> entryNodes, out reason)) return false;

            result.entries = new ContainerEntry[entryNodes.Count];
            for (int i = 0; i < entryNodes.Count; i++)
            {
                if (!TryReadEntry(entryNodes[i], $"{what} entry {i}", out ContainerEntry entry, out reason)) { result = null; return false; }
                result.entries[i] = entry;
            }

            reason = null;
            return true;
        }

        private static bool TryReadEntry(JsonNode node, string what, out ContainerEntry entry, out string reason)
        {
            entry = null;
            if (!StrictJson.TryExpectObject(node, what, new[] { "slot", "x", "y", "item" }, out Dictionary<string, JsonNode> map, out reason))
                return false;

            ContainerEntry result = new ContainerEntry();
            if (!StrictJson.TryString(map["slot"], what + " slot", out result.slot, out reason)) return false;
            if (!StrictJson.TryInt(map["x"], what + " x", out result.x, out reason)) return false;
            if (!StrictJson.TryInt(map["y"], what + " y", out result.y, out reason)) return false;
            if (!TryReadItem(map["item"], what + " item", out result.item, out reason)) return false;

            entry = result;
            return true;
        }

        private static bool TryReadItem(JsonNode node, string what, out ItemSnapshot item, out string reason)
        {
            item = null;
            if (!StrictJson.TryExpectObject(node, what, new[] { "uid", "itemId", "quantity", "rarity", "affixes" },
                    out Dictionary<string, JsonNode> map, out reason)) return false;

            ItemSnapshot result = new ItemSnapshot();
            if (!StrictJson.TryString(map["uid"], what + " uid", out result.uid, out reason)) return false;
            if (!StrictJson.TryString(map["itemId"], what + " itemId", out result.itemId, out reason)) return false;
            if (!StrictJson.TryInt(map["quantity"], what + " quantity", out result.quantity, out reason)) return false;
            if (!StrictJson.TryInt(map["rarity"], what + " rarity", out result.rarity, out reason)) return false;

            if (!StrictJson.TryArray(map["affixes"], what + " affixes", MaxAffixesPerItemInDocument, out List<JsonNode> affixNodes, out reason))
                return false;

            result.affixes = new AffixSnapshot[affixNodes.Count];
            for (int i = 0; i < affixNodes.Count; i++)
            {
                string aw = $"{what} affix {i}";
                if (!StrictJson.TryExpectObject(affixNodes[i], aw, new[] { "id", "key", "value" }, out Dictionary<string, JsonNode> am, out reason))
                    return false;

                AffixSnapshot a = new AffixSnapshot();
                if (!StrictJson.TryString(am["id"], aw + " id", out a.id, out reason)) return false;
                if (!StrictJson.TryString(am["key"], aw + " key", out a.key, out reason)) return false;
                if (!StrictJson.TryFloat(am["value"], aw + " value", out a.value, out reason)) return false;
                result.affixes[i] = a;
            }

            item = result;
            return true;
        }
    }

    /// <summary>One registered migration: how to verify an older document's integrity, and how to rewrite its tree one version up.</summary>
    internal sealed class MigrationStep
    {
        /// <summary>Returns null when the ORIGINAL (older-version) document is intact under that version's own checksum rule, else a reason.</summary>
        public Func<JsonNode, string> VerifyIntegrity;
        /// <summary>Rewrites the tree from this version to version + 1 in place (must set schemaVersion; the stale checksum member may stay).</summary>
        public Func<JsonNode, string> Upgrade;
    }

    /// <summary>
    /// The schema version policy and the migration chain. Version 4 is current (D3c); the production migrations are
    /// 1 to 2 (<see cref="VerifyVersion1Integrity"/> then <see cref="UpgradeVersion1To2"/>, D3a), 2 to 3
    /// (<see cref="VerifyVersion2Integrity"/> then <see cref="UpgradeVersion2To3"/>, D3b) and 3 to 4
    /// (<see cref="VerifyVersion3Integrity"/> then <see cref="UpgradeVersion3To4"/>, D3c). A document older than 1 or
    /// newer than 4 is refused with a reason that says which.
    ///
    /// Correct order for a future migration (the order this code enforces): (1) verify the ORIGINAL document's checksum
    /// under the OLD version's own rule (<see cref="MigrationStep.VerifyIntegrity"/>; for a version that hashed the
    /// document text minus its checksum member, <see cref="TreeChecksum"/> does it), (2) rewrite the tree one version at a
    /// time (<see cref="MigrationStep.Upgrade"/>), (3) read the migrated tree through the current strict reader and
    /// RE-SEAL it, which writes a fresh checksum under the current version. A migrated model is therefore a normal current
    /// snapshot; the canonical-bytes rule for input applies only to documents that were already current. Migration only
    /// changes in-memory data, never a file. The loadout wrapper and its containers share one version number, so a
    /// container-only change still bumps both and migrates both.
    /// </summary>
    internal static class ContainerSchema
    {
        private static readonly Dictionary<int, MigrationStep> Steps = CreateProductionSteps();

        private static Dictionary<int, MigrationStep> CreateProductionSteps()
        {
            Dictionary<int, MigrationStep> steps = new Dictionary<int, MigrationStep>();
            steps[1] = new MigrationStep { VerifyIntegrity = VerifyVersion1Integrity, Upgrade = UpgradeVersion1To2 };
            steps[2] = new MigrationStep { VerifyIntegrity = VerifyVersion2Integrity, Upgrade = UpgradeVersion2To3 };
            steps[3] = new MigrationStep { VerifyIntegrity = VerifyVersion3Integrity, Upgrade = UpgradeVersion3To4 };
            return steps;
        }

        /// <summary>TEST-ONLY: register a migration from <paramref name="fromVersion"/> to fromVersion + 1 (replacing any step already there).</summary>
        internal static void RegisterMigrationForTest(int fromVersion, MigrationStep step) { Steps[fromVersion] = step; }

        /// <summary>TEST-ONLY: drop every test registration and restore exactly the production chain (1 to 2, 2 to 3, 3 to 4).</summary>
        internal static void ClearMigrationsForTest()
        {
            Steps.Clear();
            Dictionary<int, MigrationStep> production = CreateProductionSteps();
            foreach (KeyValuePair<int, MigrationStep> pair in production) Steps[pair.Key] = pair.Value;
        }

        /// <summary>
        /// D3a: is this version 1 document (a container, or a loadout wrapper around two containers) intact under the
        /// VERSION 1 checksum rule? That rule is not the tree-text rule: it is the SHA-256 over the canonical body with
        /// floats as exact bit patterns, so the tree is read back into a model with the strict reader (no version
        /// handling) and its own checksum recomputed. A wrapper is verified against its own checksum and must hold two
        /// version 1 containers; each container is verified again when it is read on its own.
        /// </summary>
        private static string VerifyVersion1Integrity(JsonNode root)
        {
            return VerifyLegacyIntegrity(root, 1);
        }

        /// <summary>D3b: version 2 hashes exactly like version 1 (same body, floats as bit patterns), so the same check applies.</summary>
        private static string VerifyVersion2Integrity(JsonNode root)
        {
            return VerifyLegacyIntegrity(root, 2);
        }

        /// <summary>D3c: version 3 hashes exactly like versions 1 and 2 (the wrapper had no weaponSet or belt yet), so the same check applies.</summary>
        private static string VerifyVersion3Integrity(JsonNode root)
        {
            return VerifyLegacyIntegrity(root, 3);
        }

        private static string VerifyLegacyIntegrity(JsonNode root, int version)
        {
            if (Member(root, "bag") != null || Member(root, "equipment") != null)
            {
                if (!LoadoutSnapshot.TryReadFieldsForVerification(root, out LoadoutSnapshot loadout, out string loadoutReason))
                    return loadoutReason;
                if (loadout.bag.schemaVersion != version || loadout.equipment.schemaVersion != version)
                    return $"a version {version} loadout must hold version {version} containers (the wrapper and its containers share one version)";
                return loadout.ChecksumMatches() ? null : $"the version {version} loadout checksum does not match its content";
            }

            if (!ContainerSnapshot.TryReadFields(root, "container snapshot", out ContainerSnapshot container, out string reason))
                return reason;
            return container.ChecksumMatches() ? null : $"the version {version} container checksum does not match its content";
        }

        /// <summary>
        /// D3a: version 1 to 2 changes no field. The meaning of the rarity int grew (3 Epic, 4 Legendary, 5 Unique, 6 Poor
        /// were appended), and version 1 could only ever have written 0 Common, 1 Uncommon or 2 Rare - so a version 1
        /// document naming any other tier was not written by version 1 and is refused rather than reinterpreted. A
        /// wrapper has no entries of its own; its containers are upgraded (and checked) when they are read.
        /// </summary>
        private static string UpgradeVersion1To2(JsonNode root)
        {
            JsonNode entries = Member(root, "entries");
            if (entries != null && entries.Type == JsonNode.Kind.Array)
            {
                for (int i = 0; i < entries.Items.Count; i++)
                {
                    JsonNode rarity = Member(Member(entries.Items[i], "item"), "rarity");
                    if (rarity == null) continue; // a missing or malformed item is reported by the strict reader that follows

                    if (!StrictJson.TryInt(rarity, $"entry {i} item rarity", out int value, out string reason)) return reason;
                    if (value != ItemRarityRules.StoredCommon && value != ItemRarityRules.StoredUncommon &&
                        value != ItemRarityRules.StoredRare)
                    {
                        return $"entry {i} item rarity {value} did not exist in schema version 1 (only 0 Common, 1 Uncommon, 2 Rare)";
                    }
                }
            }

            JsonNode version = Member(root, "schemaVersion");
            if (version == null) return "no schemaVersion to upgrade";
            version.Text = "2";
            return null;
        }

        /// <summary>The item whose old worn slot (Legs) moved to Feet in schema version 3. Named here, as a literal, on purpose: a migration is a fixed fact about old saves, not a lookup into today's data.</summary>
        internal const string FootpadBootsItemId = "footpad_boots";

        /// <summary>
        /// D3b: version 2 to 3 changes no field. Equipment entries store the slot NAME, and the Footpad Boots moved from
        /// Legs to Feet, so an equipment entry named Legs that holds footpad_boots is renamed Feet. Nothing else about the
        /// entry (uid, rarity, rolls) is touched, a Legs entry holding any other item is left alone (the validator decides
        /// about it as it always did) and a bag has no slot names, so it is only re-versioned. Version 2 could not have
        /// written the five slots appended in version 3, so a version 2 equipment entry naming one of them was not written
        /// by version 2 and is refused rather than reinterpreted. A wrapper has no entries of its own; its containers are
        /// upgraded (and checked) when they are read.
        /// </summary>
        private static string UpgradeVersion2To3(JsonNode root)
        {
            JsonNode kind = Member(root, "kind");
            JsonNode entries = Member(root, "entries");
            bool isEquipment = kind != null && kind.Type == JsonNode.Kind.String && kind.Text == ContainerKinds.Equipment;
            if (isEquipment && entries != null && entries.Type == JsonNode.Kind.Array)
            {
                for (int i = 0; i < entries.Items.Count; i++)
                {
                    JsonNode entry = entries.Items[i];
                    JsonNode slot = Member(entry, "slot");
                    if (slot == null || slot.Type != JsonNode.Kind.String) continue; // reported by the strict reader that follows

                    if (slot.Text == "Hands" || slot.Text == "Feet" || slot.Text == "Necklace" ||
                        slot.Text == "RingLeft" || slot.Text == "RingRight")
                    {
                        return $"entry {i} names the slot {slot.Text}, which did not exist in schema version 2";
                    }

                    JsonNode itemId = Member(Member(entry, "item"), "itemId");
                    if (itemId == null || itemId.Type != JsonNode.Kind.String) continue;

                    if (slot.Text == "Legs" && itemId.Text == FootpadBootsItemId) slot.Text = "Feet";
                }
            }

            JsonNode version = Member(root, "schemaVersion");
            if (version == null) return "no schemaVersion to upgrade";
            version.Text = "3";
            return null;
        }

        /// <summary>
        /// D3c: version 3 to 4 changes no container field. A bag is only re-versioned; an equipment container is re-versioned after
        /// checking that it names neither new slot (AltWeapon, AltOffHand did not exist in version 3: such a document was not
        /// written by version 3 and is refused rather than reinterpreted); the loadout wrapper gains weaponSet 0 (the saved
        /// Weapon and OffHand ARE weapon set 1, set 2 is empty, set 1 is live) and an empty belt (a version 3 save never knew one,
        /// so a restore binds the default keys exactly as it always did). The wrapper's two containers are upgraded when they are read.
        /// </summary>
        private static string UpgradeVersion3To4(JsonNode root)
        {
            JsonNode kind = Member(root, "kind");
            JsonNode entries = Member(root, "entries");
            bool isEquipment = kind != null && kind.Type == JsonNode.Kind.String && kind.Text == ContainerKinds.Equipment;
            if (isEquipment && entries != null && entries.Type == JsonNode.Kind.Array)
            {
                for (int i = 0; i < entries.Items.Count; i++)
                {
                    JsonNode slot = Member(entries.Items[i], "slot");
                    if (slot == null || slot.Type != JsonNode.Kind.String) continue; // reported by the strict reader that follows

                    if (slot.Text == "AltWeapon" || slot.Text == "AltOffHand")
                        return $"entry {i} names the slot {slot.Text}, which did not exist in schema version 3";
                }
            }

            bool isLoadout = Member(root, "bag") != null || Member(root, "equipment") != null;
            if (isLoadout)
            {
                if (Member(root, "weaponSet") != null || Member(root, "belt") != null)
                    return "a version 3 loadout cannot carry weaponSet or belt: they were added in version 4";

                JsonNode belt = new JsonNode { Type = JsonNode.Kind.Array, Items = new List<JsonNode>() };
                for (int i = 0; i < LoadoutSnapshot.BeltSlotCount; i++) belt.Items.Add(new JsonNode { Type = JsonNode.Kind.String, Text = "" });
                root.Members.Add(new KeyValuePair<string, JsonNode>("weaponSet", new JsonNode { Type = JsonNode.Kind.Number, Text = "0" }));
                root.Members.Add(new KeyValuePair<string, JsonNode>("belt", belt));
            }

            JsonNode version = Member(root, "schemaVersion");
            if (version == null) return "no schemaVersion to upgrade";
            version.Text = "4";
            return null;
        }

        /// <summary>Checksum of an older-style document: SHA-256 over the compact re-serialization of the tree without its checksum member.</summary>
        internal static string TreeChecksum(JsonNode root)
        {
            return ContainerSnapshot.ChecksumOf(StrictJson.Serialize(root, "checksum"));
        }

        internal static JsonNode Member(JsonNode root, string name)
        {
            if (root == null || root.Type != JsonNode.Kind.Object) return null;
            for (int i = 0; i < root.Members.Count; i++) if (root.Members[i].Key == name) return root.Members[i].Value;
            return null;
        }

        /// <summary>An in-memory (already parsed) snapshot must be the current version exactly; migration happens when parsing.</summary>
        public static bool IsCurrent(int version, string what, out string reason)
        {
            if (version > ContainerSnapshot.CurrentSchemaVersion)
            {
                reason = $"{what} schemaVersion {version} is newer than this build understands " +
                         $"(current {ContainerSnapshot.CurrentSchemaVersion}); refusing rather than guessing";
                return false;
            }

            if (version < ContainerSnapshot.CurrentSchemaVersion)
            {
                reason = $"{what} schemaVersion {version} is older than the current version " +
                         $"{ContainerSnapshot.CurrentSchemaVersion}; an older document must be migrated while it is parsed";
                return false;
            }

            reason = null;
            return true;
        }

        /// <summary>
        /// Reads the root's schemaVersion; newer is refused, current passes, older runs the registered chain (integrity
        /// check on the original first). <paramref name="migrated"/> tells the caller the tree was rewritten and must be re-sealed.
        /// </summary>
        public static bool TryReadVersionAndUpgrade(JsonNode root, string what, out bool migrated, out string reason)
        {
            migrated = false;

            if (root == null || root.Type != JsonNode.Kind.Object)
            {
                reason = $"{what} must be a json object (got {(root == null ? "nothing" : root.Describe())})";
                return false;
            }

            JsonNode versionNode = null;
            for (int i = 0; i < root.Members.Count; i++)
            {
                if (root.Members[i].Key == "schemaVersion")
                {
                    if (versionNode != null)
                    {
                        reason = $"{what} repeats the field 'schemaVersion'";
                        return false;
                    }
                    versionNode = root.Members[i].Value;
                }
            }

            if (versionNode == null)
            {
                reason = $"{what} has no schemaVersion";
                return false;
            }

            if (!StrictJson.TryInt(versionNode, what + " schemaVersion", out int version, out reason)) return false;

            if (version >= ContainerSnapshot.CurrentSchemaVersion) return IsCurrent(version, what, out reason);

            // Older: every step from this version to the current one must be registered, or it is refused.
            for (int v = version; v < ContainerSnapshot.CurrentSchemaVersion; v++)
            {
                if (!Steps.ContainsKey(v))
                {
                    reason = $"{what} schemaVersion {version} is older than the oldest supported version and no migration " +
                             $"is registered from version {v}";
                    return false;
                }
            }

            string integrity = Steps[version].VerifyIntegrity != null ? Steps[version].VerifyIntegrity(root) : "no integrity check registered";
            if (integrity != null)
            {
                reason = $"{what} (schemaVersion {version}) failed its integrity check before migration: {integrity}";
                return false;
            }

            for (int v = version; v < ContainerSnapshot.CurrentSchemaVersion; v++)
            {
                string failure = Steps[v].Upgrade(root);
                if (failure != null)
                {
                    reason = $"{what}: migration from version {v} failed: {failure}";
                    return false;
                }
            }

            if (!StrictJson.TryInt(Member(root, "schemaVersion"), what + " migrated schemaVersion", out int after, out reason)) return false;
            if (after != ContainerSnapshot.CurrentSchemaVersion)
            {
                reason = $"{what}: migration ended at schemaVersion {after}, not {ContainerSnapshot.CurrentSchemaVersion}";
                return false;
            }

            migrated = true;
            reason = null;
            return true;
        }
    }
}
