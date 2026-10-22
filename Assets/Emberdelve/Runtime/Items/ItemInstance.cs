using System;
using System.Collections;
using System.Collections.Generic;
using Emberdelve.Data;

namespace Emberdelve.Items
{
    /// <summary>
    /// One final rolled affix as it exists on one item: the catalog id that produced it, the typed stat key
    /// that id is defined with, and the exact value that was accepted. A readonly struct in a private array
    /// that is never handed out, so "immutable" here is a type fact and not a promise callers are asked to
    /// respect. It carries no band, no weight and no definition reference: those belong to the catalog, and a
    /// record that could rewrite its own band would be a way to smuggle a value past validation.
    /// </summary>
    public readonly struct AffixRecord : IEquatable<AffixRecord>
    {
        public readonly string Id;
        public readonly AffixStatKey StatKey;
        public readonly float Value;

        public AffixRecord(string id, AffixStatKey statKey, float value)
        {
            Id = id;
            StatKey = statKey;
            Value = value;
        }

        /// <summary>Canonical allowlisted key text. Empty for a key value outside the allowlist.</summary>
        public string CanonicalKey => AffixStatKeys.Key(StatKey);

        public bool HasKnownKey => AffixStatKeys.IsKnown(StatKey);

        /// <summary>The plain DTO form of this exact record: same id, same canonical key, same value bits.</summary>
        public AffixSnapshot ToSnapshot()
        {
            return new AffixSnapshot(Id, CanonicalKey, Value);
        }

        public bool Equals(AffixRecord other)
        {
            if (!string.Equals(Id, other.Id, StringComparison.Ordinal)) return false;
            if (StatKey != other.StatKey) return false;
            return Value.Equals(other.Value);
        }

        public override bool Equals(object obj)
        {
            return obj is AffixRecord other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + (Id != null ? Id.GetHashCode() : 0);
                hash = hash * 31 + (int)StatKey;
                hash = hash * 31 + Value.GetHashCode();
                return hash;
            }
        }

        public static bool operator ==(AffixRecord a, AffixRecord b) => a.Equals(b);
        public static bool operator !=(AffixRecord a, AffixRecord b) => !a.Equals(b);

        public override string ToString()
        {
            return $"{Id} {CanonicalKey} {Value}";
        }
    }

    /// <summary>
    /// UID and item-id identity rules, in one place so the instance, the snapshot and the issuer cannot
    /// disagree about what a legal identity looks like.
    ///
    /// The canonical UID text is Guid "N": 32 lowercase hex characters, no hyphens, no braces, no padding
    /// whitespace. Anything else - a hyphenated form, uppercase hex, a truncated string, a random word, the
    /// all-zero Guid - is reported as malformed instead of being coerced into the nearest Guid. Item ids use
    /// the same stable-id shape the affix ids already use, which is the shape every authored ItemData asset
    /// in this project already has.
    /// </summary>
    public static class ItemIdentityRules
    {
        public const int UidCanonicalLength = 32;
        public const int MaxItemIdLength = 64;

        /// <summary>
        /// Mint one fresh identity. Only an issuer calls this; a restore path never does, so a restored item
        /// keeps the UID it was saved with instead of quietly becoming a different item.
        /// </summary>
        public static bool TryMintUid(out Guid uid, out string reason)
        {
            uid = Guid.NewGuid();
            if (uid == Guid.Empty)
            {
                reason = "identity issuer could not mint a uid";
                return false;
            }

            string text = uid.ToString("N");
            if (text.Length != UidCanonicalLength)
            {
                uid = Guid.Empty;
                reason = $"minted uid '{text}' is not the canonical {UidCanonicalLength}-character form";
                return false;
            }

            reason = null;
            return true;
        }

        /// <summary>Canonical text of one UID. Empty Guid has no canonical form and yields "".</summary>
        public static string CanonicalUid(Guid uid)
        {
            return uid == Guid.Empty ? string.Empty : uid.ToString("N");
        }

        /// <summary>
        /// Strict read of one UID string. Rejects null/empty, surrounding whitespace, any length other than
        /// 32, any character outside 0-9 a-f (so uppercase hex and hyphens are malformed, not variants),
        /// anything Guid.TryParse would refuse, the all-zero Guid, and any text that does not round-trip to
        /// its own canonical form. Nothing is trimmed, padded or case-folded first.
        /// </summary>
        public static bool TryReadUid(string uidText, out Guid uid, out string reason)
        {
            uid = Guid.Empty;

            if (string.IsNullOrEmpty(uidText))
            {
                reason = "item uid is missing";
                return false;
            }

            if (uidText.Length != UidCanonicalLength)
            {
                reason = $"item uid '{uidText}' is not the canonical {UidCanonicalLength}-character Guid 'N' form";
                return false;
            }

            for (int i = 0; i < uidText.Length; i++)
            {
                char c = uidText[i];
                bool legal = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
                if (!legal)
                {
                    reason = $"item uid '{uidText}' has character '{c}' at index {i}; only lowercase hex is legal";
                    return false;
                }
            }

            if (!Guid.TryParse(uidText, out uid))
            {
                reason = $"item uid '{uidText}' is not a Guid";
                return false;
            }

            if (uid == Guid.Empty)
            {
                reason = "item uid is the all-zero identity, which means no item at all";
                return false;
            }

            if (uid.ToString("N") != uidText)
            {
                string canonical = uid.ToString("N");
                reason = $"item uid '{uidText}' does not round-trip to its canonical form '{canonical}'";
                return false;
            }

            reason = null;
            return true;
        }

        /// <summary>
        /// Stable item-id shape: non-empty, no surrounding whitespace, at most 64 characters, starting with a
        /// lowercase letter, then lowercase letters, digits or '_'. A malformed id is a failure with a reason,
        /// never a trim or a case fold.
        /// </summary>
        public static bool IsStableItemId(string itemId, out string reason)
        {
            if (string.IsNullOrEmpty(itemId))
            {
                reason = "item definition id is empty";
                return false;
            }

            if (itemId.Trim() != itemId)
            {
                reason = $"item definition id '{itemId}' has surrounding whitespace";
                return false;
            }

            if (itemId.Length > MaxItemIdLength)
            {
                reason = $"item definition id '{itemId}' is longer than {MaxItemIdLength} characters";
                return false;
            }

            if (itemId[0] < 'a' || itemId[0] > 'z')
            {
                reason = $"item definition id '{itemId}' must start with a lowercase letter";
                return false;
            }

            for (int i = 1; i < itemId.Length; i++)
            {
                char c = itemId[i];
                if (c >= 'a' && c <= 'z') continue;
                if (c >= '0' && c <= '9') continue;
                if (c == '_') continue;
                reason = $"item definition id '{itemId}' has illegal character '{c}' at index {i}";
                return false;
            }

            reason = null;
            return true;
        }
    }

    /// <summary>
    /// The one place in this slice that is allowed to invent an identity. It mints exactly one fresh UID per
    /// successful instance and never re-mints, replaces or reuses one: an item's UID is decided when the item
    /// comes into existence and is carried unchanged through copy, quantity change, export and restore.
    ///
    /// This is a LOCAL trusted issuer for a single process, which is what the offline build needs today. It
    /// is not an authority service and it is not proof of server-side minting: a real server (U3A/U4A) owns
    /// the identity of an item and a client must never mint one for loot it wants.
    /// </summary>
    public sealed class ItemIdentityIssuer
    {
        /// <summary>Attempts at drawing a UID this issuer has not issued before it gives up and mints nothing.</summary>
        private const int MaxUidAttempts = 8;

        private int _minted;

        // R4: the issuer keeps its own ledger of what it issued, so "unique within a run" is enforced here rather
        // than hoped for. A candidate already in the ledger is discarded and redrawn.
        private readonly HashSet<Guid> _issued = new HashSet<Guid>();

        // Null: UIDs come from Guid.NewGuid (unique, not reproducible - the default and the production choice).
        // Non-null: UIDs come from this stream (reproducible from the raid seed - tests and replays).
        private readonly Emberdelve.Core.ILootRng _uidStream;

        private ItemIdentityIssuer(Emberdelve.Core.ILootRng uidStream)
        {
            _uidStream = uidStream;
        }

        public static ItemIdentityIssuer CreateLocalTrusted()
        {
            return new ItemIdentityIssuer(null);
        }

        /// <summary>
        /// An issuer whose UIDs are drawn from <paramref name="uidStream"/> (128 bits per UID, formatted as a
        /// version-4 UUID). The same stream state gives the same UIDs, so two issuers built from the same seed
        /// issue the SAME identities: use it for tests and replays, never for two live runs of one profile.
        /// </summary>
        public static ItemIdentityIssuer CreateSeeded(Emberdelve.Core.ILootRng uidStream)
        {
            if (uidStream == null) throw new ArgumentNullException(nameof(uidStream));
            return new ItemIdentityIssuer(uidStream);
        }

        /// <summary>True when UIDs are reproducible from a stream rather than random.</summary>
        public bool IsSeeded => _uidStream != null;

        /// <summary>How many UIDs this issuer has produced. Minting is not free and not invisible.</summary>
        public int MintedCount => _minted;

        /// <summary>True when this issuer minted <paramref name="uid"/>.</summary>
        public bool HasIssued(Guid uid) => _issued.Contains(uid);

        public bool TryMintUid(out Guid uid, out string reason)
        {
            for (int attempt = 0; attempt < MaxUidAttempts; attempt++)
            {
                if (_uidStream == null)
                {
                    if (!ItemIdentityRules.TryMintUid(out uid, out reason)) return false;
                }
                else
                {
                    uid = DrawSeededUid(_uidStream);
                }

                if (!_issued.Add(uid)) continue;

                _minted++;
                reason = null;
                return true;
            }

            uid = Guid.Empty;
            reason = $"identity issuer drew {MaxUidAttempts} uids it had already issued; it mints nothing rather than reuse one";
            return false;
        }

        private static Guid DrawSeededUid(Emberdelve.Core.ILootRng stream)
        {
            ulong high = stream.NextUInt64();
            ulong low = stream.NextUInt64();

            byte[] bytes = new byte[16];
            for (int i = 0; i < 8; i++)
            {
                bytes[i] = (byte)(high >> (8 * (7 - i)));
                bytes[8 + i] = (byte)(low >> (8 * (7 - i)));
            }

            // Version 4 / variant 1 bits, so the text is a normal UUID; 122 bits remain random.
            bytes[7] = (byte)((bytes[7] & 0x0F) | 0x40); // Guid(byte[]) reads bytes 6-7 as little-endian Data3: the version is the high nibble of byte 7
            bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
            return new Guid(bytes);
        }

        /// <summary>
        /// Mint one UID and build one instance with it. If the instance is refused, no instance exists, the
        /// minted UID is discarded and never handed back, and MintedCount is not incremented for it.
        /// </summary>
        public bool TryCreateInstance(ItemData definition, int quantity, AffixRarity rarity,
            IReadOnlyList<AffixRecord> affixes, ItemInstanceAuthority authority,
            out ItemInstance instance, out string reason)
        {
            instance = null;

            // Validate first: a refused creation must not consume an identity.
            if (!ItemInstance.TryValidateForCreation(definition, quantity, rarity, affixes, authority,
                out AffixRecord[] frozen, out AffixCategory category, out reason))
            {
                return false;
            }

            if (!TryMintUid(out Guid uid, out reason)) return false;

            instance = ItemInstance.FromValidated(uid, definition, quantity, rarity, category, frozen);
            return true;
        }
    }

    /// <summary>
    /// One concrete item: a fixed authored definition plus the per-instance facts that make it this item and
    /// not another copy of the same archetype - a stable UID, a quantity, a rarity and the final affix
    /// records that were accepted for it.
    ///
    /// Immutable by construction: every field is readonly, there is no public constructor and no default
    /// instance that silently acquires a UID, the affix array is private and only reachable through a
    /// read-only view that implements nothing but IReadOnlyList (so a cast to IList or to an array throws
    /// instead of handing out a mutable door), and every input list is deep-copied on the way in. A change
    /// to a quantity produces a NEW instance that carries the SAME UID, because the item did not become a
    /// different item - only how many of it exist changed. Splitting, merging and transferring stacks are
    /// deliberately not here: a split would need a second UID, and minting one is the issuer's job, not an
    /// inventory helper's.
    ///
    /// These are local trusted building blocks. They validate an item against a catalog this process trusts;
    /// they do not authorize a client to own, create or hand over an item.
    /// </summary>
    public sealed class ItemInstance
    {
        private static readonly AffixRecord[] NoAffixes = new AffixRecord[0];

        private readonly Guid _uid;
        private readonly string _uidText;
        private readonly ItemData _definition;
        private readonly string _definitionId;
        private readonly int _quantity;
        private readonly AffixRarity _rarity;
        private readonly AffixCategory _category;
        private readonly AffixRecord[] _affixes;
        private readonly AffixListView _affixView;

        private ItemInstance(Guid uid, ItemData definition, int quantity, AffixRarity rarity,
            AffixCategory category, AffixRecord[] frozenAffixes)
        {
            _uid = uid;
            _uidText = uid.ToString("N");
            _definition = definition;
            // Captured at construction: the instance's identity is tied to the item id it was built with, so
            // renaming the asset later does not silently re-point an existing item or its snapshots.
            _definitionId = definition.itemId;
            _quantity = quantity;
            _rarity = rarity;
            _category = category;
            _affixes = frozenAffixes;
            _affixView = new AffixListView(frozenAffixes);
        }

        // ------------------------------------------------------------------ identity and state

        public Guid Uid => _uid;
        public string UidText => _uidText;
        public ItemData Definition => _definition;
        public string DefinitionId => _definitionId;
        public int Quantity => _quantity;
        public AffixRarity Rarity => _rarity;
        public int RarityValue => (int)_rarity;

        /// <summary>
        /// The trusted gear category this instance was validated against, resolved from the supplied authority
        /// when the instance was built. A snapshot never carries a category, so this is never caller-supplied.
        /// </summary>
        public AffixCategory Category => _category;

        /// <summary>Read-only view of the final affix records. Not an array, not an IList: no cast makes it writable.</summary>
        public IReadOnlyList<AffixRecord> Affixes => _affixView;

        public int AffixCount => _affixes.Length;
        public bool HasAffixes => _affixes.Length > 0;
        public bool IsEquippable => _definition.IsEquippable;
        public EquipmentSlot EquipSlot => _definition.equipSlot;
        public bool CanStack => _definition.CanStack;

        /// <summary>The definition's own effective per-stack cap, read live from the base archetype.</summary>
        public int StackLimit => _definition.StackLimitForFootprint;

        /// <summary>
        /// Sum of this item's accepted affix values for one key, exact values only. Read-only and applied
        /// nowhere: fixed definition stats stay where they are, and combining the two into an effective bonus
        /// is the integration slice's job.
        /// </summary>
        public float AffixTotal(AffixStatKey key)
        {
            float total = 0f;
            for (int i = 0; i < _affixes.Length; i++)
            {
                if (_affixes[i].StatKey == key) total += _affixes[i].Value;
            }
            return total;
        }

        public bool SameIdentityAs(ItemInstance other)
        {
            return other != null && other._uid == _uid;
        }

        public string Describe()
        {
            System.Text.StringBuilder builder = new System.Text.StringBuilder();
            builder.Append(_definition.displayName).Append(" [").Append(_rarity).Append("] x").Append(_quantity);
            for (int i = 0; i < _affixes.Length; i++)
            {
                builder.Append(" | ").Append(_affixes[i]);
            }
            return builder.ToString();
        }

        // ------------------------------------------------------------------------ factories

        /// <summary>
        /// Build one new instance and mint exactly one fresh UID for it - only after everything else has
        /// validated, so a refusal mints nothing. The definition must be the one the supplied authority
        /// itself owns for its id; a definition that is merely shaped like a catalog entry is refused. Deep-
        /// copies the supplied affix list; the caller's list, array or record objects cannot be reached
        /// through the returned instance.
        /// </summary>
        internal static bool TryCreateNew(ItemData definition, int quantity, AffixRarity rarity,
            IReadOnlyList<AffixRecord> affixes, ItemInstanceAuthority authority,
            out ItemInstance instance, out string reason)
        {
            instance = null;

            if (!TryValidateForCreation(definition, quantity, rarity, affixes, authority,
                out AffixRecord[] frozen, out AffixCategory category, out reason))
            {
                return false;
            }

            if (!ItemIdentityRules.TryMintUid(out Guid uid, out reason)) return false;

            instance = new ItemInstance(uid, definition, quantity, rarity, category, frozen);
            return true;
        }

        /// <summary>
        /// Build one new instance under an identity that already exists - the path an issuer (or a future
        /// server) uses when the UID was decided before the item was built. The supplied UID is used as-is:
        /// it is never re-minted, and the all-zero Guid is refused. A refused definition produces no instance
        /// and consumes no identity.
        /// </summary>
        public static bool TryCreateWithUid(Guid uid, ItemData definition, int quantity, AffixRarity rarity,
            IReadOnlyList<AffixRecord> affixes, ItemInstanceAuthority authority,
            out ItemInstance instance, out string reason)
        {
            instance = null;

            if (uid == Guid.Empty)
            {
                reason = "item uid is the all-zero identity, which means no item at all";
                return false;
            }

            if (!TryValidateForCreation(definition, quantity, rarity, affixes, authority,
                out AffixRecord[] frozen, out AffixCategory category, out reason))
            {
                return false;
            }

            instance = new ItemInstance(uid, definition, quantity, rarity, category, frozen);
            return true;
        }

        /// <summary>
        /// Re-materialize one instance from its snapshot. The UID is read, never minted; the item definition
        /// and the affix definitions come from the supplied trusted catalog; the gear category comes from the
        /// catalog's trusted category resolver, never from the snapshot. Every problem - malformed UID,
        /// unknown item, invalid quantity, unknown rarity, wrong affix count, duplicate id or key, mismatched
        /// id/key pair, nonfinite, out-of-range or off-step value, disallowed category - refuses the whole
        /// restore with a null instance and one reason. Nothing is clamped, defaulted, trimmed or rerolled.
        /// </summary>
        public static bool TryRestore(ItemSnapshot snapshot, ItemInstanceAuthority authority,
            out ItemInstance instance, out string reason)
        {
            instance = null;

            if (!ValidateSnapshot(snapshot, authority, out Guid uid, out ItemData definition,
                out int quantity, out AffixRarity rarity, out AffixRecord[] frozen,
                out AffixCategory category, out reason))
            {
                return false;
            }

            instance = new ItemInstance(uid, definition, quantity, rarity, category, frozen);
            return true;
        }

        /// <summary>
        /// The same full validation as TryRestore without producing an instance, for a gate or a loader that
        /// wants to know whether a snapshot is acceptable before it is used.
        /// </summary>
        public static bool TryValidateSnapshot(ItemSnapshot snapshot, ItemInstanceAuthority authority,
            out string reason)
        {
            return ValidateSnapshot(snapshot, authority, out Guid ignoredUid, out ItemData ignoredDefinition,
                out int ignoredQuantity, out AffixRarity ignoredRarity, out AffixRecord[] ignoredAffixes,
                out AffixCategory ignoredCategory, out reason);
        }

        /// <summary>
        /// Re-derive this same item from scratch: every rule is re-checked against the supplied authority and
        /// the affix records are deep-copied, so the copy shares no mutable state with the original. The UID
        /// is carried unchanged - a copy is the same item re-materialized, not a second item, and this method
        /// mints nothing. Duplicating an identity for real (two owners of one UID) is contested-transfer work
        /// this slice does not do.
        /// </summary>
        public bool TryCopy(ItemInstanceAuthority authority, out ItemInstance copy, out string reason)
        {
            copy = null;

            if (!TryValidateForCreation(_definition, _quantity, _rarity, _affixView, authority,
                out AffixRecord[] frozen, out AffixCategory category, out reason))
            {
                return false;
            }

            copy = new ItemInstance(_uid, _definition, _quantity, _rarity, category, frozen);
            return true;
        }

        /// <summary>
        /// A new instance of the SAME identity holding a different number of units. The quantity rule is
        /// re-checked against the base definition; the affix records were validated when this instance was
        /// built and are carried unchanged, with no reroll and no regeneration. The UID is preserved,
        /// because consuming or restating how many units one stack holds does not create a new item.
        ///
        /// This is not a split and not a merge: both would need a second identity, and minting UIDs is the
        /// issuer's job. Inventory stack ownership (which instance is in which cell, who may move it) is the
        /// integration slice's job.
        /// </summary>
        public bool TryWithQuantity(int quantity, out ItemInstance next, out string reason)
        {
            next = null;

            if (!TryValidateQuantity(_definition, quantity, out reason)) return false;

            next = new ItemInstance(_uid, _definition, quantity, _rarity, _category, CopyAffixes(_affixes));
            return true;
        }

        /// <summary>
        /// The plain transfer/persistence form of this instance: a fresh deep copy into JsonUtility-friendly
        /// DTOs. Mutating the returned snapshot - its array, its entries, its strings - cannot reach this
        /// instance, and the values are the exact accepted ones, not re-derived or re-stepped.
        /// </summary>
        public ItemSnapshot ToSnapshot()
        {
            AffixSnapshot[] entries = new AffixSnapshot[_affixes.Length];
            for (int i = 0; i < _affixes.Length; i++) entries[i] = _affixes[i].ToSnapshot();

            return new ItemSnapshot
            {
                uid = _uidText,
                itemId = _definitionId,
                quantity = _quantity,
                rarity = (int)_rarity,
                affixes = entries
            };
        }

        // ---------------------------------------------------------------- validation internals

        /// <summary>
        /// Used by the issuer and the create paths. Validates everything, returns a private frozen copy.
        ///
        /// The first thing it establishes is which definition is authoritative: the supplied object's id is
        /// resolved through the supplied catalog and the object that resolves must be the very object the
        /// caller handed over. Only after that identity check does any mutable field of the definition
        /// (quantity cap, footprint, equipment slot) or any supplied quantity/rarity/affix claim get read for
        /// its meaning, so a hand-made ItemData - one whose id is not in the catalog, or one that copies a
        /// catalog id while carrying its own stack cap - is refused instead of believed.
        /// </summary>
        internal static bool TryValidateForCreation(ItemData definition, int quantity, AffixRarity rarity,
            IReadOnlyList<AffixRecord> affixes, ItemInstanceAuthority authority,
            out AffixRecord[] frozen, out AffixCategory category, out string reason)
        {
            frozen = null;
            category = AffixCategory.None;

            if (authority == null)
            {
                reason = "no trusted item/affix catalog was supplied";
                return false;
            }

            if (definition == null)
            {
                reason = "no item definition";
                return false;
            }

            if (!ItemIdentityRules.IsStableItemId(definition.itemId, out reason)) return false;

            // Trusted-definition gate, deliberately before quantity, category and affix semantics: an id is
            // not a definition. The catalog's own object for that id is the only one that may build an item.
            if (!authority.TryResolveItem(definition.itemId, out ItemData trusted, out reason)) return false;

            if (!ReferenceEquals(trusted, definition))
            {
                reason = $"the supplied '{definition.itemId}' definition is not the definition the trusted " +
                         $"catalog owns for that item id: a definition that shares a catalog id but is not " +
                         $"the registered object cannot create an instance";
                return false;
            }

            if (!TryValidateQuantity(trusted, quantity, out reason)) return false;

            if (!AffixRarities.TryExpectedAffixCount(rarity, out int expected))
            {
                reason = $"unknown rarity value {(int)rarity}: only Common (0 affixes), Uncommon (1) and Rare (2) exist";
                return false;
            }

            int supplied = affixes == null ? 0 : affixes.Count;
            if (supplied != expected)
            {
                reason = $"rarity {rarity} expects {expected} affix record(s), got {supplied}";
                return false;
            }

            if (!authority.TryCategory(trusted, out category, out reason))
            {
                category = AffixCategory.None;
                return false;
            }

            if (category != AffixCategory.None && !AffixCategories.IsKnownSet(category))
            {
                reason = $"trusted category for {definition.itemId} is not a known category set: " +
                         $"{AffixCategories.Describe(category)}";
                category = AffixCategory.None;
                return false;
            }

            if (expected > 0)
            {
                if (!trusted.IsEquippable)
                {
                    reason = $"{definition.itemId} is not equipment, so it may not carry rolled affixes " +
                            $"(rarity {rarity})";
                    category = AffixCategory.None;
                    return false;
                }

                if (!AffixCategories.IsSingle(category))
                {
                    reason = $"rolled gear needs exactly one known gear category, {definition.itemId} resolved to " +
                            $"{AffixCategories.Describe(category)}";
                    category = AffixCategory.None;
                    return false;
                }
            }

            if (expected == 0)
            {
                frozen = NoAffixes;
                reason = null;
                return true;
            }

            AffixRecord[] copy = new AffixRecord[expected];
            HashSet<string> seenIds = new HashSet<string>(StringComparer.Ordinal);
            HashSet<AffixStatKey> seenKeys = new HashSet<AffixStatKey>();

            for (int i = 0; i < expected; i++)
            {
                AffixRecord record = affixes[i];

                if (!AffixDefinition.IsStableId(record.Id))
                {
                    reason = $"affix entry {i}: id '{record.Id}' is not a stable affix id";
                    return false;
                }

                if (!authority.TryResolveAffix(record.Id, out AffixDefinition affix, out reason)) return false;

                if (affix.statKey != record.StatKey)
                {
                    reason = $"affix '{record.Id}' is defined with stat key '{affix.CanonicalKey}' but the entry " +
                            $"claims '{record.CanonicalKey}'";
                    return false;
                }

                if (!seenIds.Add(record.Id))
                {
                    reason = $"duplicate affix id '{record.Id}' on one item";
                    return false;
                }

                if (!seenKeys.Add(record.StatKey))
                {
                    reason = $"duplicate affix stat key '{record.CanonicalKey}' on one item";
                    return false;
                }

                // The band, the range, the step grid and the category all come from the catalog definition.
                // The record's own value is only ever read.
                if (!affix.TryValidateRolledValue(record.Value, category, out string valueReason))
                {
                    reason = $"affix '{record.Id}': {valueReason}";
                    return false;
                }

                copy[i] = record;
            }

            frozen = copy;
            reason = null;
            return true;
        }

        private static bool ValidateSnapshot(ItemSnapshot snapshot, ItemInstanceAuthority authority,
            out Guid uid, out ItemData definition, out int quantity, out AffixRarity rarity,
            out AffixRecord[] frozen, out AffixCategory category, out string reason)
        {
            uid = Guid.Empty;
            definition = null;
            quantity = 0;
            rarity = AffixRarity.Common;
            frozen = null;
            category = AffixCategory.None;

            if (snapshot == null)
            {
                reason = "no item snapshot to restore";
                return false;
            }

            if (authority == null)
            {
                reason = "no trusted item/affix catalog was supplied";
                return false;
            }

            if (!ItemIdentityRules.TryReadUid(snapshot.uid, out uid, out reason)) return false;

            if (!AffixRarities.IsKnownValue(snapshot.rarity))
            {
                reason = $"snapshot rarity {snapshot.rarity} is not a known rarity (0 Common, 1 Uncommon, 2 Rare)";
                return false;
            }

            rarity = (AffixRarity)snapshot.rarity;

            // Read the snapshot's records into typed records before anything is resolved, so a caller that
            // mutates its own snapshot array afterwards cannot change what was already validated.
            if (!TryReadSnapshotAffixes(snapshot.affixes, out AffixRecord[] records, out reason)) return false;

            if (!authority.TryResolveItem(snapshot.itemId, out definition, out reason)) return false;

            quantity = snapshot.quantity;

            return TryValidateForCreation(definition, quantity, rarity, records, authority,
                out frozen, out category, out reason);
        }

        private static bool TryReadSnapshotAffixes(AffixSnapshot[] snapshots, out AffixRecord[] records,
            out string reason)
        {
            if (snapshots == null)
            {
                // A missing array is not a claim of zero affixes; the count rule decides whether that is legal.
                records = NoAffixes;
                reason = null;
                return true;
            }

            records = new AffixRecord[snapshots.Length];

            for (int i = 0; i < snapshots.Length; i++)
            {
                AffixSnapshot entry = snapshots[i];
                if (entry == null)
                {
                    records = null;
                    reason = $"snapshot affix entry {i} is null";
                    return false;
                }

                if (!AffixSnapshot.TryCanonicalKey(entry.key, out AffixStatKey key))
                {
                    records = null;
                    reason = $"snapshot affix entry {i}: stat key '{entry.key}' is not one of the canonical " +
                            $"allowlisted keys";
                    return false;
                }

                records[i] = new AffixRecord(entry.id, key, entry.value);
            }

            reason = null;
            return true;
        }

        /// <summary>
        /// The existing carrying rule, read from the definition: worn or held gear is exactly one unit, and a
        /// carried item is 1..its own effective stack cap (ItemData.StackLimitForFootprint, which is already
        /// 1 for anything that cannot stack). 0, negative and over-cap are refused, not clamped.
        /// </summary>
        internal static bool TryValidateQuantity(ItemData definition, int quantity, out string reason)
        {
            if (definition.IsEquippable)
            {
                if (quantity != 1)
                {
                    reason = $"{definition.itemId} is worn or held, so its quantity must be exactly 1 (got {quantity})";
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

        private static AffixRecord[] CopyAffixes(AffixRecord[] source)
        {
            if (source == null || source.Length == 0) return NoAffixes;

            AffixRecord[] copy = new AffixRecord[source.Length];
            Array.Copy(source, copy, source.Length);
            return copy;
        }

        /// <summary>
        /// The only door to the frozen affix array, and it has no writable shape: it implements
        /// IReadOnlyList and nothing else, so casting it to IList, to ICollection or to an array throws
        /// InvalidCastException instead of exposing the storage. Indexing out of range is an exception, not a
        /// silent default.
        /// </summary>
        private sealed class AffixListView : IReadOnlyList<AffixRecord>
        {
            private readonly AffixRecord[] _items;

            public AffixListView(AffixRecord[] items)
            {
                _items = items;
            }

            public AffixRecord this[int index]
            {
                get
                {
                    if (index < 0 || index >= _items.Length)
                    {
                        throw new IndexOutOfRangeException($"item has {_items.Length} affix record(s), " +
                                                           $"index {index} is not one of them");
                    }
                    return _items[index];
                }
            }

            public int Count => _items.Length;

            public IEnumerator<AffixRecord> GetEnumerator()
            {
                for (int i = 0; i < _items.Length; i++) yield return _items[i];
            }

            IEnumerator IEnumerable.GetEnumerator()
            {
                return GetEnumerator();
            }
        }

        /// <summary>Only the issuer / factory paths reach this, and only with fully validated inputs.</summary>
        internal static ItemInstance FromValidated(Guid uid, ItemData definition, int quantity, AffixRarity rarity,
            AffixCategory category, AffixRecord[] frozenAffixes)
        {
            return new ItemInstance(uid, definition, quantity, rarity, category, frozenAffixes);
        }
    }
}
