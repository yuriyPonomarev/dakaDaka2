using System;
using System.IO;
using System.Linq;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Player;
using UnityEngine;

namespace Emberdelve.Items
{
    /// <summary>Opt-in only through offline preparation. CLI network and test raids never touch player saves.</summary>
    public static class OfflineProfileSession
    {
        public static OfflineProfile Current { get; private set; }
        public static string LastError { get; private set; }
        private static string _raidId;
        private static LoadoutSnapshot _departing;
        private static LoadoutSnapshot _returning;
        private static OfflineAmmunition _departingAmmo, _returningAmmo;
        private static bool _pendingSettlement;
        private static bool _applied;
        private static bool _cancelled;
        public static bool HasRaid => !string.IsNullOrEmpty(_raidId);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset()
        { Current?.Dispose(); Current = null; LastError = null; _raidId = null; _departing = _returning = null; _departingAmmo = _returningAmmo = null; _pendingSettlement = _applied = _cancelled = false; }

        public static bool Select(GameConfig config, CharacterClassData character, out string reason)
        {
            if (!NetRole.IsOffline) { reason = "Persistent preparation is available offline"; return false; }
            if (_pendingSettlement && !RetrySettlement(out reason)) return false;
            if (HasRaid)
            {
                // Returning to preparation before extraction is abandonment.
                if (!Complete(false, null, out reason)) return false;
            }
            if (Current != null && Current.ClassId == character.classId) { reason = null; return true; }
            if (!ItemIdentityRules.IsStableItemId(character.classId, out reason)) return false;
            string root = Path.Combine(Application.persistentDataPath, "OfflineProfiles");
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            // An isolated disk directory for lifecycle acceptance; never affects a normal player's save.
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++)
                if (args[i] == "--ember-profile-dir") root = Path.GetFullPath(args[i + 1]);
#endif
            if (!OfflineProfile.TryOpen(Path.Combine(root, character.classId + ".json"), config.itemCatalog, character, out var profile, out reason))
            { LastError = reason; return false; }
            Current?.Dispose(); Current = profile; LastError = null; return true;
        }
        public static bool Depart(out string reason)
        {
            if (Current == null || !NetRole.IsOffline) { reason = "Open offline preparation first"; return false; }
            if (_pendingSettlement && !RetrySettlement(out reason)) return false;
            bool began = RunContext.SelectedRoyaleMode.HasValue
                ? Current.BeginRoyaleRaid(out _raidId, out _departing, out _departingAmmo, out reason)
                : Current.BeginRaid(out _raidId, out _departing, out _departingAmmo, out reason);
            if (!began) return false;
            _applied = false; LastError = null; return true;
        }
        public static void ApplyPrepared(PlayerRig rig)
        {
            if (!NetRole.IsOffline || !HasRaid || _applied || _departing == null || rig == null || !rig.IsLocallyControlled || rig.Class?.classId != Current.ClassId) return;
            string reason = "Prepared ammunition is missing";
            if (_departingAmmo == null || rig.Loadout == null || _departingAmmo.stored == null || _departingAmmo.stored.Length != 0
                || !_departingAmmo.TryValidate(_departing.bag.entries.Concat(_departing.equipment.entries).Select(e => e.item),
                    rig.Inventory.Catalog, rig.Class, out reason)
                || !LoadoutSnapshot.TryRestore(_departing, rig.Inventory, rig.Equipment, null, out reason)
                || !rig.Loadout.RestorePreparedAmmo(_departingAmmo, _departing, out reason))
            {
                LastError = "Could not restore prepared kit: " + reason;
                rig.SetInputEnabled(false);
                throw new InvalidOperationException(LastError);
            }
            _applied = true;
            rig.InitializeVitals();
            Debug.Log("[Emberdelve][Profile] prepared kit restored class=" + Current.ClassId);
        }
        public static bool Complete(bool extracted, PlayerRig rig, out string reason)
        {
            reason = null;
            if (!NetRole.IsOffline || !HasRaid) return true;
            if (!_pendingSettlement)
            {
                _returning = extracted && rig != null ? LoadoutSnapshot.Capture(rig.Inventory, rig.Equipment) : null;
                _returningAmmo = _returning != null ? rig.Loadout.CapturePreparedAmmo(_returning) : new OfflineAmmunition();
                _pendingSettlement = true;
            }
            return RetrySettlement(out reason);
        }
        public static bool CancelDeparture(out string reason)
        {
            if (!HasRaid) { reason = null; return true; }
            _returning = _departing; _returningAmmo = _departingAmmo; _pendingSettlement = true; _cancelled = true;
            return RetrySettlement(out reason);
        }
        public static bool RetrySettlement(out string reason)
        {
            reason = null;
            if (!_pendingSettlement) return true;
            if (!Current.FinishRaid(_raidId, _returning, _returningAmmo, out reason, _cancelled)) { LastError = reason; return false; }
            _raidId = null; _departing = _returning = null; _departingAmmo = _returningAmmo = null; _pendingSettlement = _applied = _cancelled = false; LastError = null;
            Debug.Log("[Emberdelve][Profile] raid settled and saved class=" + Current.ClassId);
            return true;
        }
    }
}
