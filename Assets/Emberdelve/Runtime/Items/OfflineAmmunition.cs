using System;
using System.Collections.Generic;
using System.Linq;
using Emberdelve.Data;
using UnityEngine;

namespace Emberdelve.Items
{
    public static class AmmoCarryRules
    {
        public static int Capacity(CharacterClassData character, string weaponId)
        {
            if (character == null) return 0;
            if (character.primaryWeapon != null && character.primaryWeapon.weaponId == weaponId)
                return Math.Max(0, character.maxReserveAmmo);
            if (character.secondaryWeapon != null && character.secondaryWeapon.weaponId == weaponId)
                return Math.Max(0, character.secondaryMaxReserveAmmo);
            return Math.Max(0, Math.Max(character.maxReserveAmmo, character.secondaryMaxReserveAmmo));
        }
    }

    /// <summary>Owned weapon magazines follow item UID; loose ammunition follows its weapon definition.
    /// Missing records mean zero. This sidecar never supplies starting ammunition.</summary>
    [Serializable]
    public sealed class OfflineAmmunition
    {
        public const int StoredCapacity = 999999;
        [Serializable] public sealed class LoadedEntry
        {
            public string uid;
            public string weaponId;
            public int magazine;
            public LoadedEntry Clone() => new LoadedEntry { uid = uid, weaponId = weaponId, magazine = magazine };
        }
        [Serializable] public sealed class ReserveEntry
        {
            public string weaponId;
            public int quantity;
            public ReserveEntry Clone() => new ReserveEntry { weaponId = weaponId, quantity = quantity };
        }
        public LoadedEntry[] loaded = Array.Empty<LoadedEntry>();
        public ReserveEntry[] carried = Array.Empty<ReserveEntry>();
        public ReserveEntry[] stored = Array.Empty<ReserveEntry>();

        public OfflineAmmunition Clone() => new OfflineAmmunition {
            loaded = loaded?.Select(e => e?.Clone()).ToArray(),
            carried = carried?.Select(e => e?.Clone()).ToArray(),
            stored = stored?.Select(e => e?.Clone()).ToArray()
        };
        public string ToJson() => JsonUtility.ToJson(this);
        public static bool TryFromJson(string json, out OfflineAmmunition ammunition, out string reason)
        {
            ammunition = null; reason = "Invalid ammunition data";
            if (string.IsNullOrWhiteSpace(json)) return false;
            try
            {
                var parsed = JsonUtility.FromJson<OfflineAmmunition>(json);
                if (parsed == null || !parsed.ValidateRecords(out reason)) return false;
                ammunition = parsed; reason = null; return true;
            }
            catch (ArgumentException) { return false; }
        }
        public int LoadedCount(string uid) => loaded?.FirstOrDefault(e => e != null && e.uid == uid)?.magazine ?? 0;
        public int CarriedCount(string weaponId) => carried?.FirstOrDefault(e => e != null && e.weaponId == weaponId)?.quantity ?? 0;
        public int StoredCount(string weaponId) => stored?.FirstOrDefault(e => e != null && e.weaponId == weaponId)?.quantity ?? 0;
        public void SetLoaded(string uid, string weaponId, int count)
        {
            if (string.IsNullOrEmpty(uid) || string.IsNullOrEmpty(weaponId) || count < 0)
                throw new ArgumentException("A loaded record needs a UID, weapon and nonnegative count");
            loaded = (loaded ?? Array.Empty<LoadedEntry>()).Where(e => e.uid != uid)
                .Concat(count == 0 ? Array.Empty<LoadedEntry>() : new[] { new LoadedEntry { uid = uid, weaponId = weaponId, magazine = count } }).ToArray();
        }
        public void SetReserve(string weaponId, int count, bool stored)
        {
            if (string.IsNullOrEmpty(weaponId) || count < 0) throw new ArgumentException("A reserve needs a weapon and nonnegative count");
            var records = ((stored ? this.stored : carried) ?? Array.Empty<ReserveEntry>()).Where(e => e.weaponId != weaponId)
                .Concat(count == 0 ? Array.Empty<ReserveEntry>() : new[] { new ReserveEntry { weaponId = weaponId, quantity = count } }).ToArray();
            if (stored) this.stored = records; else carried = records;
        }
        public static WeaponData ResolveWeapon(ItemCatalog catalog, string weaponId) =>
            catalog?.Items.FirstOrDefault(i => i != null && i.grantedWeapon != null &&
                i.grantedWeapon.IsRanged && i.grantedWeapon.weaponId == weaponId)?.grantedWeapon;

        private bool ValidateRecords(out string reason)
        {
            reason = "Invalid or duplicate ammunition record";
            if (loaded == null || carried == null || stored == null) return false;
            var uids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in loaded)
                if (entry == null || string.IsNullOrEmpty(entry.uid) || string.IsNullOrEmpty(entry.weaponId) ||
                    entry.magazine < 0 || !uids.Add(entry.uid)) return false;
            foreach (var records in new[] { carried, stored })
            {
                var keys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var entry in records)
                    if (entry == null || string.IsNullOrEmpty(entry.weaponId) || entry.quantity < 0 || !keys.Add(entry.weaponId)) return false;
            }
            reason = null; return true;
        }
        public bool TryValidate(IEnumerable<ItemSnapshot> owned, ItemCatalog catalog, CharacterClassData character, out string reason)
        {
            if (!ValidateRecords(out reason)) return false;
            if (owned == null || catalog == null || character == null) { reason = "Missing ammunition ownership context"; return false; }
            var items = owned.ToArray();
            foreach (var entry in loaded)
            {
                var matches = items.Where(i => i != null && i.uid == entry.uid).ToArray();
                if (matches.Length != 1 || !catalog.TryResolveItem(matches[0].itemId, out var item, out _) ||
                    item.grantedWeapon == null || !item.grantedWeapon.IsRanged || item.grantedWeapon.weaponId != entry.weaponId ||
                    entry.magazine > Mathf.Clamp(item.grantedWeapon.magazineSize, 1, 99))
                { reason = "Loaded ammunition does not match an owned weapon or magazine capacity"; return false; }
            }
            foreach (var entry in carried)
                if (ResolveWeapon(catalog, entry.weaponId) == null || entry.quantity > AmmoCarryRules.Capacity(character, entry.weaponId))
                { reason = "Invalid carried ammunition type or capacity"; return false; }
            foreach (var entry in stored)
                if (ResolveWeapon(catalog, entry.weaponId) == null || entry.quantity > StoredCapacity)
                { reason = "Invalid stored ammunition type or capacity"; return false; }
            reason = null; return true;
        }
    }
}
