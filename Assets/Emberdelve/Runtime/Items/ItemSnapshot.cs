using System;
using UnityEngine;
using Emberdelve.Data;

namespace Emberdelve.Items
{
    /// <summary>
    /// One affix as it travels or persists: the catalog id, the canonical allowlisted key text and the exact
    /// accepted value. Plain public fields only, so JsonUtility can read and write it. It carries no band, no
    /// weight, no category and no definition reference - those live in the trusted catalog, and a snapshot
    /// that could name its own category or its own range would be a way of smuggling them in.
    /// </summary>
    [Serializable]
    public sealed class AffixSnapshot
    {
        public string id;
        public string key;
        public float value;

        public AffixSnapshot()
        {
        }

        public AffixSnapshot(string id, string key, float value)
        {
            this.id = id;
            this.key = key;
            this.value = value;
        }

        /// <summary>An independent copy: same text and same value bits, no shared object.</summary>
        public AffixSnapshot Clone()
        {
            return new AffixSnapshot(id, key, value);
        }

        /// <summary>
        /// Strict canonical key read: the text must be exactly one of the canonical keys as
        /// <see cref="AffixStatKeys"/> spells them, compared ordinal, with no trimming and no case folding.
        /// Anything else - "Armor", "armor ", "attack", "" - is an unknown key, and the out parameter stays
        /// at the first legal value rather than a guessed one.
        /// </summary>
        public static bool TryCanonicalKey(string raw, out AffixStatKey key)
        {
            key = AffixStatKey.Strength;
            if (string.IsNullOrEmpty(raw)) return false;

            for (int i = 0; i < AffixStatKeys.KeyCount; i++)
            {
                AffixStatKey candidate = (AffixStatKey)i;
                if (AffixStatKeys.Key(candidate) != raw) continue;
                key = candidate;
                return true;
            }

            return false;
        }

        public override string ToString()
        {
            return $"{id} {key} {value}";
        }
    }

    /// <summary>
    /// The transfer/persistence DTO of one item instance: UID text, item definition id, quantity, raw rarity
    /// and the affix records. JsonUtility-compatible plain fields only.
    ///
    /// Deliberately dumb. It stores raw values, so a hand-edited or tampered file arrives as what it is - a
    /// rarity of 7, a quantity of 0, a key that no definition owns - and <see cref="ItemInstance.TryRestore"/>
    /// refuses it. Nothing in this type clamps, defaults, trims, fills in a category or mints a UID, and the
    /// helpers below only report: they never repair.
    ///
    /// R9: this JsonUtility form is a single-item transfer DTO only (JsonUtility ignores unknown fields and defaults
    /// a missing rarity to Common). Anything that is persisted goes through <see cref="ContainerSnapshot"/> /
    /// <see cref="LoadoutSnapshot"/>: versioned, strict (unknown/missing/repeated fields refused), culture-
    /// independent exact floats, checksummed, and validated through <see cref="ItemInstance.TryRestore"/>.
    /// </summary>
    [Serializable]
    public sealed class ItemSnapshot
    {
        public string uid;
        public string itemId;
        public int quantity;
        public int rarity;
        public AffixSnapshot[] affixes;

        public ItemSnapshot()
        {
        }

        public int AffixCount => affixes == null ? 0 : affixes.Length;

        /// <summary>A deep copy: a new array of new entries, so mutating the copy cannot touch the original.</summary>
        public ItemSnapshot Clone()
        {
            ItemSnapshot copy = new ItemSnapshot
            {
                uid = this.uid,
                itemId = this.itemId,
                quantity = this.quantity,
                rarity = this.rarity
            };

            if (affixes == null)
            {
                copy.affixes = null;
                return copy;
            }

            AffixSnapshot[] entries = new AffixSnapshot[affixes.Length];
            for (int i = 0; i < affixes.Length; i++)
            {
                entries[i] = affixes[i] == null ? null : affixes[i].Clone();
            }

            copy.affixes = entries;
            return copy;
        }

        public string ToJson(bool pretty = false)
        {
            return JsonUtility.ToJson(this, pretty);
        }

        /// <summary>
        /// Parse only. A null/blank string, a parse failure or a parse that produced no object is a refusal
        /// with a reason and a null snapshot; a syntactically valid but semantically broken document still
        /// parses and is left for <see cref="ItemInstance.TryValidateSnapshot"/> to refuse on the facts.
        /// </summary>
        public static bool TryFromJson(string json, out ItemSnapshot snapshot, out string reason)
        {
            snapshot = null;
            reason = null;

            if (string.IsNullOrEmpty(json) || string.IsNullOrWhiteSpace(json))
            {
                reason = "item snapshot json is empty";
                return false;
            }

            try
            {
                snapshot = JsonUtility.FromJson<ItemSnapshot>(json);
            }
            catch (Exception e)
            {
                snapshot = null;
                reason = $"item snapshot json could not be parsed: {e.Message}";
                return false;
            }

            if (snapshot == null)
            {
                reason = "item snapshot json did not produce an item snapshot";
                return false;
            }

            return true;
        }

        /// <summary>The stored UID text as a Guid, or false with the out parameter at Guid.Empty. The stored
        /// text is never rewritten, so a second call on the same snapshot reports the same thing.</summary>
        public bool TryGetUid(out Guid uidValue, out string reason)
        {
            return ItemIdentityRules.TryReadUid(uid, out uidValue, out reason);
        }

        /// <summary>The raw rarity as an enum, or false with the enum left at Common. Never clamped.</summary>
        public bool TryGetRarity(out AffixRarity rarityValue, out string reason)
        {
            if (!AffixRarities.IsKnownValue(rarity))
            {
                rarityValue = AffixRarity.Common;
                reason = $"snapshot rarity {rarity} is not a known rarity ({ItemRarityRules.StoredLegend})";
                return false;
            }

            rarityValue = (AffixRarity)rarity;
            reason = null;
            return true;
        }

        public bool TryGetItemId(out string itemIdValue, out string reason)
        {
            if (!ItemIdentityRules.IsStableItemId(itemId, out reason))
            {
                itemIdValue = null;
                return false;
            }

            itemIdValue = itemId;
            return true;
        }

        public override string ToString()
        {
            return $"uid={uid} item={itemId} qty={quantity} rarity={rarity} affixes={AffixCount}";
        }
    }
}
