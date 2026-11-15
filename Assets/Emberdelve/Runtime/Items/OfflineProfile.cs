using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Emberdelve.Core;
using Emberdelve.Data;
using UnityEngine;

namespace Emberdelve.Items
{
    public enum PreparationArea { Stash, Bag, Equipment }

    public sealed class PreparationState
    {
        public long Revision;
        public int Gold;
        public string ActiveRaid = "";
        public string Supplied = "";
        public ContainerSnapshot Stash;
        public LoadoutSnapshot Ready;
        public OfflineAmmunition Ammunition = new OfflineAmmunition();
        public PreparationState Clone() => new PreparationState { Revision = Revision, Gold = Gold,
            ActiveRaid = ActiveRaid, Supplied = Supplied, Stash = Stash.Clone(), Ready = Ready.Clone(), Ammunition = Ammunition.Clone() };
        public ContainerSnapshot Container(PreparationArea area) => area == PreparationArea.Stash ? Stash :
            area == PreparationArea.Bag ? Ready.bag : Ready.equipment;
    }

    [Serializable]
    public sealed class OfflineProfileEnvelope
    {
        public int version = 2;
        public string classId;
        public long revision;
        public int gold;
        public string activeRaid;
        public string supplied;
        public string stash;
        public string loadout;
        public string ammunition;
        public string ammunitionChecksum;
        public string checksum;
        public string Digest() => ContainerSnapshot.ChecksumOf(version + "|" + classId + "|" + revision + "|" + gold + "|" + activeRaid + "|" + stash + "|" + loadout + (string.IsNullOrEmpty(supplied) ? "" : "|" + supplied) +
            (version == 1 ? "" : "|" + ammunition + "|" + ammunitionChecksum));
    }

    /// <summary>One local class profile. Copy/validate/write/commit: refused operations never mutate live state.</summary>
    public sealed class OfflineProfile : IDisposable
    {
        private readonly string _path;
        private readonly ItemCatalog _catalog;
        private readonly CharacterClassData _class;
        private PreparationState _state;
        private FileStream _raidLease;
        private LoadoutSnapshot _departingKit;
        private bool _royaleDeparture;
        private OfflineAmmunition _departingAmmo;
        public void Dispose() { _raidLease?.Dispose(); _raidLease = null; }
        private readonly ItemIdentityIssuer _issuer = ItemIdentityIssuer.CreateLocalTrusted();
        public PreparationState Snapshot => _state.Clone();
        public string SavePath => _path;
        public string ClassId => _class.classId;
        public int AmmoCapacity(string weaponId) => AmmoCarryRules.Capacity(_class, weaponId);
        public ItemCatalog Catalog => _catalog;
        public long Revision => _state.Revision;
        public string ActiveRaid => _state.ActiveRaid;
        public IReadOnlyList<ItemData> Stock { get; }
        public IReadOnlyList<WeaponData> AmmoWeapons => _catalog.Items.Where(i => i != null && i.grantedWeapon != null &&
            i.grantedWeapon.IsRanged).Select(i => i.grantedWeapon).Distinct().OrderBy(w => w.weaponId, StringComparer.Ordinal).ToArray();

        private OfflineProfile(string path, ItemCatalog catalog, CharacterClassData character)
        {
            _path = path; _catalog = catalog; _class = character;
            Stock = catalog.Items.Where(i => i != null &&
                ((i.grantedWeapon != null && (i.grantedWeapon == character.primaryWeapon ||
                    i.grantedWeapon == character.secondaryWeapon || i.grantedWeapon == character.offhandShield)) ||
                 i.kind == ItemKind.Consumable || i.kind == ItemKind.AmmoPack ||
                 (i.kind == ItemKind.Armor && i.equipSlot != EquipmentSlot.None)))
                .OrderBy(i => i.kind).ThenBy(i => i.itemId, StringComparer.Ordinal).ToArray();
        }

        public static bool TryOpen(string path, ItemCatalog catalog, CharacterClassData character,
            out OfflineProfile profile, out string reason)
        {
            profile = null;
            if (catalog == null || character == null) { reason = "Profile configuration is missing"; return false; }
            var created = new OfflineProfile(path, catalog, character);
            try
            {
                if (File.Exists(path))
                {
                    if (!created.Decode(File.ReadAllText(path, Encoding.UTF8), out created._state, out reason)) return false;
                    // A previous process never completed this raid. Keep the stash; the carried kit was already debited.
                    if (!string.IsNullOrEmpty(created._state.ActiveRaid))
                    {
                        using (var raidLease = new FileStream(path + ".raidlock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                        {
                            var abandoned = created._state.Clone(); abandoned.ActiveRaid = "";
                            if (!created.Commit(abandoned, out reason)) return false;
                        }
                    }
                }
                else
                {
                    created._state = new PreparationState { Revision = -1, Gold = 150,
                        Stash = EmptyGrid("stash", ContainerKinds.Stash, 12, 20), Ready = EmptyLoadout() };
                    var first = created._state.Clone();
                    foreach (ItemData item in character.startingItems ?? Array.Empty<ItemData>())
                    {
                        if (item == null) continue;
                        if (!created.Mint(item, out ItemSnapshot snapshot, out reason) ||
                            !created.Add(first.Ready.bag, snapshot, null, out reason)) return false;
                    }
                    if (!created.SeedWeapon(first, character.primaryWeapon, EquipmentSlot.Weapon, out reason) ||
                        !created.SeedWeapon(first, character.secondaryWeapon, EquipmentSlot.AltWeapon, out reason) ||
                        (character.primaryWeapon?.Hands != HandUse.TwoHanded &&
                         !created.SeedWeapon(first, character.offhandShield, EquipmentSlot.OffHand, out reason))) return false;
                    if (!created.Commit(first, out reason)) return false;
                }
                if (created._state.Stash.width == 10 && created._state.Stash.height == 12)
                {
                    var expanded = created._state.Clone(); expanded.Stash.width = 12; expanded.Stash.height = 20;
                    if (!created.Commit(expanded, out reason)) return false;
                }
                profile = created; reason = null; return true;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException)
            { reason = "Could not open profile: " + e.Message; return false; }
        }

        public static LoadoutSnapshot EmptyLoadout() => new LoadoutSnapshot {
            bag = EmptyGrid("bag", ContainerKinds.Bag, 10, 5),
            equipment = new ContainerSnapshot { kind = ContainerKinds.Equipment, containerId = "equipment", entries = Array.Empty<ContainerEntry>() }.Seal()
        }.Seal();
        private static ContainerSnapshot EmptyGrid(string id, string kind, int width, int height) =>
            new ContainerSnapshot { containerId = id, kind = kind, width = width, height = height, entries = Array.Empty<ContainerEntry>() }.Seal();

        private bool SeedWeapon(PreparationState state, WeaponData weapon, EquipmentSlot slot, out string reason)
        {
            reason = null; if (weapon == null) return true;
            var item = _catalog.Items.FirstOrDefault(i => i != null && i.grantedWeapon == weapon);
            if (item == null) { reason = "Starter weapon is missing from catalog"; return false; }
            if (!Mint(item, out var snapshot, out reason)) return false;
            Append(state.Ready.equipment, new ContainerEntry { slot = slot.ToString(), item = snapshot }); return true;
        }
        private bool Mint(ItemData item, out ItemSnapshot snapshot, out string reason)
        {
            snapshot = null;
            if (item.IsEquippable)
            {
                if (!_catalog.TryMintCommon(_issuer, item, out var instance, out reason)) return false;
                snapshot = instance.ToSnapshot();
            }
            else snapshot = new ItemSnapshot { itemId = item.itemId, quantity = 1, uid = "", rarity = 0, affixes = Array.Empty<AffixSnapshot>() };
            reason = null; return true;
        }
        public static int BuyPrice(ItemData item) => Math.Max(5, Math.Min(1000000, item.value) * 2);
        public static int SellPrice(ItemData item, int quantity) => (int)Math.Min(int.MaxValue, (long)Math.Max(1, Math.Min(1000000, item.value) / 2) * Math.Max(0, quantity));

        public bool IsSupplied(ItemSnapshot item) => !string.IsNullOrEmpty(item.uid) &&
            (_state.Supplied ?? "").Split(',').Contains(item.uid);
        public int SaleValue(ItemData item, ItemSnapshot snapshot) => IsSupplied(snapshot) ? 0 : SellPrice(item, snapshot.quantity);
        public bool EquipBaseWeapons(long revision, out string reason)
        {
            if (!CanEdit(revision, out reason)) return false;
            var next = _state.Clone();
            var weapons = new[] { _class.primaryWeapon, _class.secondaryWeapon, _class.offhandShield };
            var slots = new[] { EquipmentSlot.Weapon, EquipmentSlot.AltWeapon, EquipmentSlot.OffHand };
            for (int i = 0; i < slots.Length; i++)
            {
                if (weapons[i] == null || next.Ready.equipment.entries.Any(e => e.slot == slots[i].ToString())) continue;
                if (i == 2 && next.Ready.equipment.entries.Any(e => e.slot == EquipmentSlot.Weapon.ToString() &&
                    _catalog.TryResolveItem(e.item.itemId, out var def, out _) && def.grantedWeapon?.Hands == HandUse.TwoHanded)) continue;
                var definition = _catalog.Items.FirstOrDefault(d => d != null && d.grantedWeapon == weapons[i]);
                // A supplied copy in the bag must be reused rather than minted again.
                var existing = next.Ready.bag.entries.FirstOrDefault(e => e.item.itemId == definition?.itemId && IsSupplied(e.item));
                if (existing != null) { reason = "Your supplied weapon is already in the bag. Equip it first."; return false; }
                if (!SeedWeapon(next, weapons[i], slots[i], out reason)) return false;
                string uid = next.Ready.equipment.entries.Last().item.uid;
                next.Supplied = string.IsNullOrEmpty(next.Supplied) ? uid : next.Supplied + "," + uid;
            }
            return Commit(next, out reason);
        }

        public bool Buy(string itemId, long revision, out string reason)
        {
            if (!CanEdit(revision, out reason)) return false;
            var item = Stock.FirstOrDefault(i => i.itemId == itemId);
            if (item == null) { reason = "The quartermaster does not sell this item"; return false; }
            var next = _state.Clone(); int price = BuyPrice(item);
            if (next.Gold < price) { reason = "Not enough gold"; return false; }
            if (!Mint(item, out var snapshot, out reason) || !Add(next.Stash, snapshot, null, out reason)) return false;
            next.Gold -= price; return Commit(next, out reason);
        }
        public bool Sell(PreparationArea area, int index, long revision, out string reason)
        {
            if (!CanEdit(revision, out reason)) return false;
            var next = _state.Clone(); var container = next.Container(area);
            if (!Entry(container, index, out var source, out reason)) return false;
            if (!_catalog.TryResolveItem(source.item.itemId, out var item, out reason)) return false;
            if (next.Ammunition.LoadedCount(source.item.uid) > 0) { reason = "Unload the weapon before selling it"; return false; }
            next.Ammunition.loaded = next.Ammunition.loaded.Where(e => e.uid != source.item.uid).ToArray();
            long price = (long)SaleValue(item, source.item) + next.Gold;
            if (price > int.MaxValue) { reason = "Gold limit reached"; return false; }
            Remove(container, index); next.Gold = (int)price; Normalize(next.Ready);
            return Commit(next, out reason);
        }
        public bool Move(PreparationArea sourceArea, int index, PreparationArea targetArea,
            EquipmentSlot slot, Vector2Int? cell, long revision, out string reason)
        {
            if (!CanEdit(revision, out reason)) return false;
            var next = _state.Clone(); var sourceContainer = next.Container(sourceArea);
            if (!Entry(sourceContainer, index, out var source, out reason)) return false;
            var item = source.item.Clone();
            if (targetArea == PreparationArea.Stash && IsSupplied(item)) { reason = "Extract with supplied gear before storing it"; return false; }
            var destination = next.Container(targetArea);
            if (cell.HasValue && targetArea == PreparationArea.Bag && QuickBeltRules.TryGetSlot(cell.Value, destination.width, out _))
            {
                if (!_catalog.TryResolveItem(item.itemId, out var utility, out reason)) return false;
                if (!QuickBeltRules.CanStore(utility) || item.quantity > QuickBeltRules.StackLimit(utility))
                { reason = "Only healing stacks up to five or one torch fit a belt slot"; return false; }
                var occupied = destination.entries.FirstOrDefault(e => e.x == cell.Value.x && e.y == cell.Value.y);
                if (ReferenceEquals(occupied, source)) { reason = null; return true; }
                if (occupied != null)
                {
                    if (utility.CanStack && occupied.item.itemId == item.itemId && string.IsNullOrEmpty(item.uid) && string.IsNullOrEmpty(occupied.item.uid))
                    {
                        int moved = Math.Min(item.quantity, QuickBeltRules.StackLimit(utility) - occupied.item.quantity);
                        if (moved <= 0) { reason = "That belt stack is full"; return false; }
                        occupied.item.quantity += moved;
                        source.item.quantity -= moved;
                        if (source.item.quantity == 0) Remove(sourceContainer, index);
                    }
                    else
                    {
                        if (sourceArea == PreparationArea.Equipment) { reason = "Move this item to the bag first"; return false; }
                        var displaced = occupied.item.Clone();
                        Remove(destination, Array.IndexOf(destination.entries, occupied));
                        Remove(sourceContainer, Array.IndexOf(sourceContainer.entries, source));
                        if (!Add(sourceContainer, displaced, new Vector2Int(source.x, source.y), out reason) || !Add(destination, item, cell, out reason)) return false;
                    }
                    Normalize(next.Ready); return Commit(next, out reason);
                }
            }
            Remove(sourceContainer, index);
            if (targetArea == PreparationArea.Equipment)
            {
                if (!_catalog.TryResolveItem(item.itemId, out var definition, out reason)) return false;
                if (!EquipmentSlots.Accepts(definition.equipSlot, slot)) { reason = "Item does not fit that slot"; return false; }
                int occupied = Array.FindIndex(destination.entries, e => e.slot == slot.ToString());
                if (occupied >= 0)
                {
                    var previous = destination.entries[occupied].item.Clone(); Remove(destination, occupied);
                    // Displaced gear returns to the same storage area, or to the bag when moving worn gear.
                    if (!Add(sourceArea == PreparationArea.Stash ? next.Stash : next.Ready.bag, previous, null, out reason)) return false;
                }
                Append(destination, new ContainerEntry { slot = slot.ToString(), item = item });
                if (!ResolveHandConflicts(next, slot, sourceArea, out reason)) return false;
            }
            else if (!Add(destination, item, cell, out reason)) return false;
            Normalize(next.Ready); return Commit(next, out reason);
        }
        public bool StoreBag(long revision, out string reason)
        {
            if (!CanEdit(revision, out reason)) return false;
            var next = _state.Clone();
            foreach (var entry in next.Ready.bag.entries)
                if (IsSupplied(entry.item)) { reason = "Equip supplied weapons before storing the bag"; return false; }
                else if (!Add(next.Stash, entry.item.Clone(), null, out reason)) return false;
            next.Ready.bag.entries = Array.Empty<ContainerEntry>(); Normalize(next.Ready);
            return Commit(next, out reason);
        }
        /// <summary>Atomically parks owned preparation and issues a fresh base kit. Cancelled launches cannot farm supplies.</summary>
        public bool BeginRoyaleRaid(out string raidId, out LoadoutSnapshot kit, out OfflineAmmunition ammo, out string reason)
        {
            raidId = null; kit = null; ammo = null;
            if (!CanEdit(_state.Revision, out reason)) return false;
            var next = _state.Clone();
            var discarded = new HashSet<string>();
            foreach (var item in Owned(next.Ready))
            {
                if (IsSupplied(item)) { discarded.Add(item.uid); continue; }
                if (!Add(next.Stash, item.Clone(), null, out reason))
                { reason = "Make room in your stash for prepared equipment before entering Royale: " + reason; return false; }
            }
            var toStore = next.Ammunition.carried.Select(e => new OfflineAmmunition.ReserveEntry { weaponId = e.weaponId, quantity = e.quantity })
                .Concat(next.Ammunition.loaded.Where(e => discarded.Contains(e.uid))
                    .Select(e => new OfflineAmmunition.ReserveEntry { weaponId = e.weaponId, quantity = e.magazine }));
            foreach (var group in toStore.GroupBy(e => e.weaponId))
            {
                long count = next.Ammunition.StoredCount(group.Key) + group.Sum(e => (long)e.quantity);
                if (count > OfflineAmmunition.StoredCapacity) { reason = "Make room in stored ammunition before entering Royale"; return false; }
                next.Ammunition.SetReserve(group.Key, (int)count, true);
            }
            next.Ammunition.loaded = next.Ammunition.loaded.Where(e => !discarded.Contains(e.uid)).ToArray();
            next.Ammunition.carried = Array.Empty<OfflineAmmunition.ReserveEntry>();
            next.Ready = EmptyLoadout(); next.Supplied = "";
            var supplied = new PreparationState { Ready = EmptyLoadout() };
            foreach (var item in _class.startingItems ?? Array.Empty<ItemData>())
                if (item != null && (!Mint(item, out var entry, out reason) || !Add(supplied.Ready.bag, entry, null, out reason))) return false;
            if (!SeedWeapon(supplied, _class.primaryWeapon, EquipmentSlot.Weapon, out reason)
                || !SeedWeapon(supplied, _class.secondaryWeapon, EquipmentSlot.AltWeapon, out reason)
                || (_class.primaryWeapon?.Hands != HandUse.TwoHanded && !SeedWeapon(supplied, _class.offhandShield, EquipmentSlot.OffHand, out reason))) return false;
            // Only a newly issued base kit gets automatic bindings. Persisted player layouts stay untouched.
            int beltSlot = 0;
            foreach (var entry in supplied.Ready.bag.entries)
            {
                if (beltSlot == supplied.Ready.belt.Length) break;
                if (!_catalog.TryResolveItem(entry.item.itemId, out var definition, out reason)) return false;
                if (!QuickBeltRules.CanStore(definition) || entry.item.quantity > QuickBeltRules.StackLimit(definition)) continue;
                string binding = string.IsNullOrEmpty(entry.item.uid) ? "i:" + entry.item.itemId : "u:" + entry.item.uid;
                entry.x = supplied.Ready.bag.width + beltSlot; entry.y = 0;
                supplied.Ready.belt[beltSlot++] = binding;
            }
            Normalize(supplied.Ready);
            var suppliedAmmo = new OfflineAmmunition();
            foreach (var entry in supplied.Ready.equipment.entries)
                if (_catalog.TryResolveItem(entry.item.itemId, out var definition, out _) && definition.grantedWeapon != null && definition.grantedWeapon.IsRanged)
                    suppliedAmmo.SetLoaded(entry.item.uid, definition.grantedWeapon.weaponId, Math.Max(0, definition.grantedWeapon.magazineSize));
            var weapons = new[] { _class.primaryWeapon, _class.secondaryWeapon };
            var reserves = new[] { _class.startingReserveAmmo, _class.secondaryStartingReserveAmmo };
            for (int i = 0; i < weapons.Length; i++) if (weapons[i] != null && weapons[i].IsRanged)
                suppliedAmmo.SetReserve(weapons[i].weaponId, Math.Max(0, Math.Min(reserves[i], AmmoCarryRules.Capacity(_class, weapons[i].weaponId))), false);
            if (!suppliedAmmo.TryValidate(Owned(supplied.Ready), _catalog, _class, out reason)) return false;
            try { _raidLease = new FileStream(_path + ".raidlock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            { reason = "This profile is already in a raid in another game instance"; return false; }
            next.ActiveRaid = Guid.NewGuid().ToString("N");
            if (!Commit(next, out reason)) { Dispose(); return false; }
            _royaleDeparture = true; _departingKit = supplied.Ready.Clone(); _departingAmmo = suppliedAmmo.Clone();
            raidId = next.ActiveRaid; kit = supplied.Ready; ammo = suppliedAmmo; return true;
        }
        public bool BeginRaid(out string raidId, out LoadoutSnapshot kit, out string reason) =>
            BeginRaid(out raidId, out kit, out _, out reason);
        public bool BeginRaid(out string raidId, out LoadoutSnapshot kit, out OfflineAmmunition ammo, out string reason)
        {
            raidId = null; kit = null; ammo = null;
            if (!CanEdit(_state.Revision, out reason)) return false;
            try { _raidLease = new FileStream(_path + ".raidlock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { reason = "This profile is already in a raid in another game instance"; return false; }
            var outgoing = _state.Ready.Clone(); var next = _state.Clone();
            var readyUids = new HashSet<string>(Owned(outgoing).Select(i => i.uid), StringComparer.Ordinal);
            var outgoingAmmo = new OfflineAmmunition {
                loaded = next.Ammunition.loaded.Where(e => readyUids.Contains(e.uid)).Select(e => e.Clone()).ToArray(),
                carried = next.Ammunition.carried.Select(e => e.Clone()).ToArray()
            };
            next.Ammunition.loaded = next.Ammunition.loaded.Where(e => !readyUids.Contains(e.uid)).ToArray();
            next.Ammunition.carried = Array.Empty<OfflineAmmunition.ReserveEntry>();
            next.ActiveRaid = Guid.NewGuid().ToString("N"); next.Ready = EmptyLoadout();
            if (!Commit(next, out reason)) { Dispose(); return false; }
            _royaleDeparture = false; _departingKit = outgoing.Clone(); _departingAmmo = outgoingAmmo.Clone();
            raidId = next.ActiveRaid; kit = outgoing; ammo = outgoingAmmo; return true;
        }
        public bool FinishRaid(string raidId, LoadoutSnapshot extracted, out string reason, bool cancelled = false) =>
            FinishRaid(raidId, extracted, new OfflineAmmunition(), out reason, cancelled);
        public bool FinishRaid(string raidId, LoadoutSnapshot extracted, OfflineAmmunition ammo, out string reason, bool cancelled = false)
        {
            if (string.IsNullOrEmpty(raidId) || raidId != _state.ActiveRaid) { reason = "Raid was already settled or does not belong to this profile"; return false; }
            if (extracted != null && (extracted.bag?.entries == null || extracted.equipment?.entries == null ||
                extracted.belt == null || extracted.belt.Length != LoadoutSnapshot.BeltSlotCount ||
                extracted.bag.entries.Any(e => e?.item == null) || extracted.equipment.entries.Any(e => e?.item == null)))
            { reason = "Invalid extracted loadout"; return false; }
            if (ammo == null || ammo.stored == null || ammo.stored.Length != 0)
            { reason = "Raid ammunition cannot include stored reserves"; return false; }
            if (!ammo.TryValidate(extracted == null ? Array.Empty<ItemSnapshot>() : Owned(extracted), _catalog, _class, out reason)) return false;
            if (cancelled && (_departingKit == null || _departingAmmo == null || extracted == null ||
                extracted.ToJson() != _departingKit.ToJson() || ammo.ToJson() != _departingAmmo.ToJson()))
            { reason = "Cancellation must restore the exact departing kit and ammunition"; return false; }
            var next = _state.Clone(); next.ActiveRaid = ""; if (!cancelled) next.Supplied = "";
            if (extracted != null && !(cancelled && _royaleDeparture))
            {
                next.Ready = extracted.Clone();
                next.Ammunition.loaded = next.Ammunition.loaded.Concat(ammo.loaded.Select(e => e.Clone())).ToArray();
                next.Ammunition.carried = ammo.carried.Select(e => e.Clone()).ToArray();
            }
            Normalize(next.Ready);
            if (!Commit(next, out reason)) return false;
            _departingKit = null; _departingAmmo = null; _royaleDeparture = false;
            Dispose(); return true;
        }
        private static IEnumerable<ItemSnapshot> Owned(LoadoutSnapshot kit) =>
            kit.bag.entries.Concat(kit.equipment.entries).Select(e => e.item);
        private bool AmmoWeapon(PreparationState state, PreparationArea area, int index,
            out ItemSnapshot item, out WeaponData weapon, out string reason)
        {
            item = null; weapon = null;
            if (!Enum.IsDefined(typeof(PreparationArea), area)) { reason = "Invalid ammunition area"; return false; }
            if (!Entry(state.Container(area), index, out var entry, out reason)) return false;
            item = entry.item;
            if (!_catalog.TryResolveItem(item.itemId, out var definition, out reason)) return false;
            weapon = definition.grantedWeapon;
            if (weapon == null || !weapon.IsRanged || string.IsNullOrEmpty(item.uid)) { reason = "Select a ranged weapon"; return false; }
            return true;
        }
        public bool LoadAmmo(PreparationArea area, int index, long revision, out string reason) =>
            ChangeLoaded(area, index, revision, false, out reason);
        public bool UnloadAmmo(PreparationArea area, int index, long revision, out string reason) =>
            ChangeLoaded(area, index, revision, true, out reason);
        private bool ChangeLoaded(PreparationArea area, int index, long revision, bool unload, out string reason)
        {
            if (!CanEdit(revision, out reason)) return false;
            var next = _state.Clone();
            if (!AmmoWeapon(next, area, index, out var item, out var weapon, out reason)) return false;
            bool stored = area == PreparationArea.Stash;
            int loaded = next.Ammunition.LoadedCount(item.uid);
            int reserve = stored ? next.Ammunition.StoredCount(weapon.weaponId) : next.Ammunition.CarriedCount(weapon.weaponId);
            int limit = stored ? OfflineAmmunition.StoredCapacity : AmmoCarryRules.Capacity(_class, weapon.weaponId);
            int moved = unload ? loaded : Math.Min(reserve, Mathf.Clamp(weapon.magazineSize, 1, 99) - loaded);
            if (moved <= 0) { reason = unload ? "Weapon is empty" : "No ammunition or magazine is full"; return false; }
            if (unload && (long)reserve + moved > limit) { reason = "No room to unload all ammunition"; return false; }
            next.Ammunition.SetLoaded(item.uid, weapon.weaponId, unload ? 0 : loaded + moved);
            next.Ammunition.SetReserve(weapon.weaponId, unload ? reserve + moved : reserve - moved, stored);
            return Commit(next, out reason);
        }
        public bool UnpackAmmo(PreparationArea area, int index, string weaponId, long revision, out string reason)
        {
            if (!CanEdit(revision, out reason)) return false;
            if (area != PreparationArea.Stash && area != PreparationArea.Bag) { reason = "Select an ammunition pack in storage or bag"; return false; }
            var next = _state.Clone(); var container = next.Container(area);
            if (!Entry(container, index, out var entry, out reason) || !_catalog.TryResolveItem(entry.item.itemId, out var definition, out reason)) return false;
            var weapon = OfflineAmmunition.ResolveWeapon(_catalog, weaponId);
            if (definition.kind != ItemKind.AmmoPack || definition.ammoGrant <= 0 || !definition.MatchesAmmo(weapon))
            { reason = "Pack does not contain ammunition for that weapon"; return false; }
            bool stored = area == PreparationArea.Stash;
            int reserve = stored ? next.Ammunition.StoredCount(weaponId) : next.Ammunition.CarriedCount(weaponId);
            int limit = stored ? OfflineAmmunition.StoredCapacity : AmmoCarryRules.Capacity(_class, weaponId);
            if ((long)reserve + definition.ammoGrant > limit) { reason = "Not enough room for the entire ammunition pack"; return false; }
            if (entry.item.quantity == 1) Remove(container, index); else entry.item.quantity--;
            next.Ammunition.SetReserve(weaponId, reserve + definition.ammoGrant, stored);
            Normalize(next.Ready); return Commit(next, out reason);
        }
        public bool TransferAmmo(string weaponId, int quantity, bool toStash, long revision, out string reason)
        {
            if (!CanEdit(revision, out reason)) return false;
            if (quantity <= 0 || OfflineAmmunition.ResolveWeapon(_catalog, weaponId) == null)
            { reason = "Select a positive quantity of known ammunition"; return false; }
            var next = _state.Clone();
            int carried = next.Ammunition.CarriedCount(weaponId), stored = next.Ammunition.StoredCount(weaponId);
            long newCarried = (long)carried + (toStash ? -quantity : quantity);
            long newStored = (long)stored + (toStash ? quantity : -quantity);
            if (newCarried < 0 || newCarried > AmmoCarryRules.Capacity(_class, weaponId) || newStored < 0 || newStored > OfflineAmmunition.StoredCapacity)
            { reason = "Not enough ammunition or destination capacity"; return false; }
            next.Ammunition.SetReserve(weaponId, (int)newCarried, false);
            next.Ammunition.SetReserve(weaponId, (int)newStored, true);
            return Commit(next, out reason);
        }
        private bool CanEdit(long revision, out string reason)
        {
            reason = !string.IsNullOrEmpty(_state.ActiveRaid) ? "Return from the raid before editing your kit" :
                revision != _state.Revision ? "Inventory changed; select the item again" : null;
            return reason == null;
        }
        private bool ResolveHandConflicts(PreparationState next, EquipmentSlot changed, PreparationArea sourceArea, out string reason)
        {
            reason = null;
            var equipment = next.Ready.equipment;
            for (int set = 0; set < 2; set++)
            {
                var main = set == 0 ? EquipmentSlot.Weapon : EquipmentSlot.AltWeapon;
                var off = set == 0 ? EquipmentSlot.OffHand : EquipmentSlot.AltOffHand;
                var weapon = equipment.entries.FirstOrDefault(e => e.slot == main.ToString());
                int offIndex = Array.FindIndex(equipment.entries, e => e.slot == off.ToString());
                if (weapon == null || offIndex < 0) continue;
                if (!_catalog.TryResolveItem(weapon.item.itemId, out var definition, out reason)) return false;
                if (definition.grantedWeapon?.Hands != HandUse.TwoHanded) continue;
                if (changed == off) { reason = "A two-handed weapon occupies both hands"; return false; }
                var displaced = equipment.entries[offIndex].item.Clone(); Remove(equipment, offIndex);
                if (!Add(sourceArea == PreparationArea.Stash ? next.Stash : next.Ready.bag, displaced, null, out reason)) return false;
            }
            return true;
        }
        private static void Normalize(LoadoutSnapshot kit)
        {
            if (kit.weaponSet == 1 && !kit.equipment.entries.Any(e => e.slot == EquipmentSlot.AltWeapon.ToString())) kit.weaponSet = 0;
            for (int i = 0; i < kit.belt.Length; i++)
            {
                var cell = QuickBeltRules.Cell(i, kit.bag.width);
                var entry = kit.bag.entries.FirstOrDefault(e => e.x == cell.x && e.y == cell.y);
                kit.belt[i] = entry == null ? "" : string.IsNullOrEmpty(entry.item.uid) ? "i:" + entry.item.itemId : "u:" + entry.item.uid;
            }
            kit.Seal();
        }
        private bool Add(ContainerSnapshot target, ItemSnapshot item, Vector2Int? cell, out string reason)
        {
            if (target.kind == ContainerKinds.Stash && IsSupplied(item)) { reason = "Extract with supplied gear before storing it"; return false; }
            if (!_catalog.TryResolveItem(item.itemId, out var definition, out reason)) return false;
            if (cell.HasValue && target.kind == ContainerKinds.Bag && QuickBeltRules.TryGetSlot(cell.Value, target.width, out _))
            {
                if (!QuickBeltRules.CanStore(definition) || item.quantity > QuickBeltRules.StackLimit(definition) || target.entries.Any(e => e.x == cell.Value.x && e.y == cell.Value.y))
                { reason = "Item does not fit the selected belt slot"; return false; }
                Append(target, new ContainerEntry { x = cell.Value.x, y = cell.Value.y, item = item.Clone() });
                reason = null; return true;
            }
            int remaining = item.quantity;
            if (!cell.HasValue && definition.CanStack)
                foreach (var entry in target.entries.Where(e => e.x < target.width && e.item.itemId == item.itemId && string.IsNullOrEmpty(e.item.uid)))
                {
                    int moved = Math.Min(remaining, definition.StackLimitForFootprint - entry.item.quantity);
                    entry.item.quantity += moved; remaining -= moved;
                    if (remaining == 0) { reason = null; return true; }
                }
            Vector2Int shape = definition.EffectiveFootprint;
            for (int y = 0; y <= target.height - shape.y; y++)
            for (int x = 0; x <= target.width - shape.x; x++)
            {
                if (cell.HasValue && cell.Value != new Vector2Int(x, y)) continue;
                bool free = true;
                foreach (var existing in target.entries)
                {
                    if (!_catalog.TryResolveItem(existing.item.itemId, out var other, out reason)) return false;
                    var size = other.EffectiveFootprint;
                    if (x < existing.x + size.x && x + shape.x > existing.x && y < existing.y + size.y && y + shape.y > existing.y) { free = false; break; }
                }
                if (!free) continue;
                var placed = item.Clone(); placed.quantity = remaining;
                Append(target, new ContainerEntry { x = x, y = y, item = placed }); reason = null; return true;
            }
            reason = "No room for that item in the selected container"; return false;
        }
        private static bool Entry(ContainerSnapshot container, int index, out ContainerEntry entry, out string reason)
        {
            entry = index >= 0 && index < container.entries.Length ? container.entries[index] : null;
            reason = entry == null ? "Select an item first" : null; return entry != null;
        }
        private static void Append(ContainerSnapshot target, ContainerEntry entry)
        { var entries = target.entries; Array.Resize(ref entries, entries.Length + 1); entries[entries.Length - 1] = entry; target.entries = entries; }
        private static void Remove(ContainerSnapshot target, int index)
        { target.entries = target.entries.Where((e, i) => i != index).ToArray(); }

        private bool Validate(PreparationState state, out string reason)
        {
            reason = "Invalid profile";
            if (state.Gold < 0 || state.Ready == null || state.Stash == null || state.Ready.bag == null || state.Ready.equipment == null ||
                ((state.Stash.width != 12 || state.Stash.height != 20) && (state.Stash.width != 10 || state.Stash.height != 12)) || state.Ready.bag.width != 10 || state.Ready.bag.height != 5 ||
                state.Ready.belt == null || state.Ready.belt.Length != LoadoutSnapshot.BeltSlotCount || !state.Ready.ChecksumMatches()) return false;
            var uids = new HashSet<Guid>();
            foreach (var container in new[] { state.Stash, state.Ready.bag, state.Ready.equipment })
            {
                string kind = ReferenceEquals(container, state.Stash) ? ContainerKinds.Stash : ReferenceEquals(container, state.Ready.bag) ? ContainerKinds.Bag : ContainerKinds.Equipment;
                if (!ContainerSnapshotValidator.TryValidate(container, _catalog, null, kind, out var validated, out reason)) return false;
                foreach (var uid in validated.Uids) if (!uids.Add(uid)) { reason = "The same item occurs in two containers"; return false; }
            }
            if (state.Ready.weaponSet < 0 || state.Ready.weaponSet > 1) { reason = "Invalid weapon set"; return false; }
            foreach (string binding in state.Ready.belt)
                if (!LoadoutSnapshot.TryParseBeltText(binding, _catalog, out _, out reason)) return false;
            return state.Ammunition != null && state.Ammunition.TryValidate(
                state.Stash.entries.Select(e => e.item).Concat(Owned(state.Ready)), _catalog, _class, out reason);
        }
        private bool Commit(PreparationState next, out string reason)
        {
            next.Stash.Seal(); next.Ready.Seal();
            if (!Validate(next, out reason)) return false;
            if (_state.Revision == long.MaxValue) { reason = "Profile revision limit reached"; return false; }
            next.Revision = _state.Revision + 1;
            var envelope = new OfflineProfileEnvelope { classId = ClassId, revision = next.Revision, gold = next.Gold,
                activeRaid = next.ActiveRaid, supplied = next.Supplied, stash = next.Stash.ToJson(), loadout = next.Ready.ToJson(), ammunition = next.Ammunition.ToJson() };
            envelope.ammunitionChecksum = ContainerSnapshot.ChecksumOf(envelope.ammunition);
            envelope.checksum = envelope.Digest();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path));
                using (var lease = new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                {
                    if (File.Exists(_path))
                    {
                        if (!Decode(File.ReadAllText(_path, Encoding.UTF8), out var disk, out reason)) return false;
                        if (disk.Revision != _state.Revision) { reason = "Profile changed in another game instance. Reopen preparation."; return false; }
                    }
                    else if (_state.Revision != -1) { reason = "Profile file disappeared; refusing to recreate it"; return false; }
                    using (var file = new FileStream(_path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        byte[] bytes = Encoding.UTF8.GetBytes(JsonUtility.ToJson(envelope, true));
                        file.Write(bytes, 0, bytes.Length); file.Flush(true);
                    }
                    if (File.Exists(_path)) File.Replace(_path + ".tmp", _path, _path + ".bak");
                    else File.Move(_path + ".tmp", _path);
                }
                _state = next; reason = null; return true;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            { reason = "Could not save profile. Nothing changed: " + e.Message; return false; }
        }
        private bool Decode(string json, out PreparationState state, out string reason)
        {
            state = null; reason = "Profile is damaged or from an unsupported version; the file was kept unchanged";
            OfflineProfileEnvelope envelope;
            try { envelope = JsonUtility.FromJson<OfflineProfileEnvelope>(json); } catch (ArgumentException) { return false; }
            if (envelope == null || (envelope.version != 1 && envelope.version != 2) || envelope.classId != ClassId || envelope.revision < 0 || envelope.checksum != envelope.Digest()) return false;
            if (!ContainerSnapshot.TryFromJson(envelope.stash, out var stash, out reason) ||
                !LoadoutSnapshot.TryFromJson(envelope.loadout, out var kit, out reason)) return false;
            var ammunition = new OfflineAmmunition();
            if (envelope.version == 2 && (string.IsNullOrEmpty(envelope.ammunition) ||
                envelope.ammunitionChecksum != ContainerSnapshot.ChecksumOf(envelope.ammunition) ||
                !OfflineAmmunition.TryFromJson(envelope.ammunition, out ammunition, out reason))) return false;
            var candidate = new PreparationState { Ammunition = ammunition, Revision = envelope.revision, Gold = envelope.gold, ActiveRaid = envelope.activeRaid ?? "", Supplied = envelope.supplied ?? "", Stash = stash, Ready = kit };
            if (!Validate(candidate, out reason)) return false;
            state = candidate; return true;
        }
    }
}
