using System;

namespace Emberdelve.Items
{
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
}
