using System;
using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Emberdelve.BladeDefense;
using Emberdelve.Combat;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Flow;
using Emberdelve.Items;
using Emberdelve.Player;

namespace Emberdelve.EditorTools
{
    /// <summary>
    /// U5A gate: hands are items. On the real level rig (real Equipment, Inventory, ClassLoadout, RunDirector ground, command authority) it proves
    /// the class kit is minted as items with uids, an emptied hand is the bare fists / nothing (never the class weapon), the equip / unequip / drop /
    /// pickup flow moves ONE identity between exactly one place at a time, the guard and the riposte eligibility follow the current hands,
    /// a two-handed weapon stows the off-hand item (or is refused with nothing moved when the bag is full), and every forged command is refused with
    /// the state untouched. Every expectation is derived from the weapon / class data and the live model, not from typed-in results.
    ///
    /// Batch entry: Emberdelve.EditorTools.EmberdelveHandsGate.Run
    /// Final line: [Emberdelve][Hands] PASS|FAIL checks=N failures=M, then EmberdelveGateExit.Exit.
    /// </summary>
    [InitializeOnLoad]
    public static class EmberdelveHandsGate
    {
        private const string Pending = "Emberdelve.HandsGate.Pending";

        static EmberdelveHandsGate()
        {
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.EnteredPlayMode && SessionState.GetBool(Pending, false))
                {
                    SessionState.SetBool(Pending, false);
                    new GameObject("Hands_Gate").AddComponent<HandsHarness>();
                }
            };
        }

        public static void Run()
        {
            SessionState.SetBool(Pending, true);
            EditorSceneManager.OpenScene("Assets/Emberdelve/Scenes/Emberdelve_Level01.unity");
            EditorApplication.EnterPlaymode();
        }

        internal static void Finish(int checks, int failures)
        {
            Debug.Log("[Emberdelve][Hands] " + (failures == 0 ? "PASS" : "FAIL") + " checks=" + checks + " failures=" + failures);
            EmberdelveGateExit.Exit(failures == 0 ? 0 : 1);
        }
    }

    public sealed class HandsHarness : MonoBehaviour
    {
        private int _checks;
        private int _failures;
        private PlayerRig _rig;
        private Inventory _bag;
        private Equipment _gear;
        private ClassLoadout _loadout;
        private IInventoryAuthority _authority;
        private ItemCatalog _catalog;
        private ItemInstanceAuthority _items;
        private ulong _forgedSequence = 7_000_000;

        private void Check(bool ok, string label)
        {
            _checks++;
            if (ok) return;
            _failures++;
            Debug.LogError("[Hands] mismatch " + label);
        }

        private IEnumerator Start()
        {
            for (int i = 0; i < 10; i++) yield return null;
            IEnumerator run = Execute();
            while (true)
            {
                bool more;
                try { more = run.MoveNext(); }
                catch (Exception e) { _failures++; Debug.LogException(e); break; }
                if (!more) break;
                yield return run.Current;
            }

            EmberdelveHandsGate.Finish(_checks, _failures);
        }

        // ------------------------------------------------------------------ helpers

        private ItemData Resolve(string id)
        {
            bool ok = _catalog.TryResolveItem(id, out ItemData item, out string reason);
            Check(ok && item != null, "catalog resolves " + id + ": " + reason);
            return item;
        }

        private ItemInstance Mint(ItemData item)
        {
            bool made = ItemInstance.TryCreateNew(item, 1, AffixRarity.Common, null, _items, out ItemInstance instance, out string reason);
            Check(made && instance != null, "mint " + item.itemId + ": " + reason);
            if (!made || instance == null) return null;
            bool added = _bag.TryAddInstance(instance, out reason);
            Check(added, "the " + item.itemId + " enters the bag: " + reason);
            return added ? instance : null;
        }

        private InventoryCommandResult Send(InventoryCommandKind kind, ulong stack, EquipmentSlot slot, Guid source, Guid target)
        {
            bool ok = _authority.TryCreateLocalCommand(kind, stack, 0, Vector2Int.zero, slot, source, target, out InventoryCommand command, out string why);
            if (!ok) return new InventoryCommandResult(false, false, _bag.Revision, "create: " + why);
            return _authority.Submit(_rig, command);
        }

        private ulong StackOf(ItemInstance instance)
        {
            int index = _bag.IndexOfInstance(instance.Uid);
            return index >= 0 ? _bag.Slots[index].StackId : 0UL;
        }

        private Guid WornUid(EquipmentSlot slot)
        {
            ItemInstance worn = _gear.EquippedInstance(slot);
            return worn != null ? worn.Uid : Guid.Empty;
        }

        private InventoryCommandResult EquipFromBag(ItemInstance instance, EquipmentSlot slot) =>
            Send(InventoryCommandKind.Equip, StackOf(instance), slot, instance.Uid, WornUid(slot));

        private InventoryCommandResult Unequip(EquipmentSlot slot) =>
            Send(InventoryCommandKind.Unequip, 0, slot, WornUid(slot), Guid.Empty);

        private InventoryCommandResult DropWorn(EquipmentSlot slot) =>
            Send(InventoryCommandKind.Drop, 0, slot, WornUid(slot), Guid.Empty);

        private InventoryCommandResult DropFromBag(ItemInstance instance) =>
            Send(InventoryCommandKind.Drop, StackOf(instance), EquipmentSlot.None, instance.Uid, Guid.Empty);

        /// <summary>How many places hold this uid: bag cells, worn slots, ground pickups. Must be exactly one for an item that exists.</summary>
        private int Census(Guid uid, bool includeGround = true)
        {
            int n = 0;
            for (int i = 0; i < _bag.Slots.Count; i++)
                if (_bag.Slots[i].Instance != null && _bag.Slots[i].Instance.Uid == uid) n++;
            for (int s = 1; s < Equipment.SlotCount; s++)
                if (WornUid((EquipmentSlot)s) == uid) n++;
            if (includeGround)
            {
                LootPickup[] pickups = FindObjectsByType<LootPickup>(FindObjectsSortMode.None);
                for (int i = 0; i < pickups.Length; i++)
                    if (pickups[i] != null && pickups[i].Instance != null && pickups[i].Instance.Uid == uid) n++;
            }
            return n;
        }

        private LootPickup GroundOf(Guid uid)
        {
            LootPickup[] pickups = FindObjectsByType<LootPickup>(FindObjectsSortMode.None);
            for (int i = 0; i < pickups.Length; i++)
                if (pickups[i] != null && pickups[i].Instance != null && pickups[i].Instance.Uid == uid) return pickups[i];
            return null;
        }

        private string Snap()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append("rev=").Append(_bag.Revision).Append(" set=").Append(_gear.ActiveWeaponSet);
            sb.Append(" main=").Append(_rig.ActiveWeapon != null ? _rig.ActiveWeapon.weaponId : "-");
            sb.Append(" off=").Append(_rig.OffhandWeapon != null ? _rig.OffhandWeapon.weaponId : "-");
            sb.Append(" units=").Append(_bag.TotalUnits).Append(" gear{");
            for (int s = 1; s < Equipment.SlotCount; s++)
            {
                ItemInstance worn = _gear.EquippedInstance((EquipmentSlot)s);
                sb.Append(worn != null ? worn.UidText : "-").Append(';');
            }
            sb.Append("} bag{");
            for (int i = 0; i < _bag.Slots.Count; i++)
                sb.Append(_bag.Slots[i].Instance != null ? _bag.Slots[i].Instance.UidText : "i:" + _bag.Slots[i].Item.itemId).Append(';');
            return sb.Append('}').ToString();
        }

        private void ClearGround()
        {
            LootPickup[] pickups = FindObjectsByType<LootPickup>(FindObjectsSortMode.None);
            for (int i = 0; i < pickups.Length; i++)
                if (pickups[i] != null && pickups[i].Instance != null) Destroy(pickups[i].gameObject);
        }

        private InventoryCommandResult SubmitRaw(InventoryCommandKind kind, int owner, ulong stack, EquipmentSlot slot, Guid source, Guid target)
        {
            _forgedSequence++;
            InventoryCommand command = new InventoryCommand(kind, owner, _forgedSequence, _bag.Revision, stack, 0, Vector2Int.zero, slot, source, target);
            return _authority.Submit(_rig, command);
        }

        // ------------------------------------------------------------------ the run

        private IEnumerator Execute()
        {
            _rig = FindFirstObjectByType<PlayerRig>();
            Check(_rig != null && !_rig.IsDead && _rig.Inventory != null && _rig.Equipment != null && _rig.Loadout != null, "live level rig has a bag, equipment and loadout");
            if (_rig == null || _rig.IsDead || _rig.Inventory == null || _rig.Equipment == null || _rig.Loadout == null) yield break;

            _bag = _rig.Inventory;
            _gear = _rig.Equipment;
            _loadout = _rig.Loadout;
            _catalog = _bag.Catalog;
            string reason = "no catalog";
            Check(_catalog != null && _catalog.IsValid(out reason), "the live bag uses a valid catalog: " + reason);
            if (_catalog == null || !_catalog.TryGetAuthority(out _items, out reason)) yield break;
            _authority = _bag.CommandAuthority;
            Check(_authority != null, "the bag exposes its bound authority");
            bool haveDirector = GameServices.TryGet<RunDirector>(out RunDirector director) && director != null;
            Check(haveDirector, "the level has a run director to lay ground items");
            if (_authority == null || !haveDirector) yield break;

            // ---- A. the class kit is items in the hand slots
            CharacterClassData cls = _rig.Class;
            Check(cls != null && _gear.KitIsItems, "the class kit is minted as items (item mode is on)");
            Check(_loadout.Unarmed != null && _loadout.Unarmed.isUnarmed && _loadout.Unarmed.weaponId == "unarmed_fists", "the loadout knows the bare-fist weapon from the config");
            if (cls == null || _loadout.Unarmed == null) yield break;
            _loadout.ForceSwitchToSlot(0);
            _gear.Clear();
            _bag.Clear();
            _bag.ConfigureGrid(10, 6);
            Check(_loadout.ActiveSlot == 0, "set 1 is in hand");
            HashSet<Guid> kitUids = new HashSet<Guid>();
            ItemInstance kitMain = _gear.EquippedInstance(EquipmentSlot.Weapon);
            ItemInstance kitOff = _gear.EquippedInstance(EquipmentSlot.OffHand);
            ItemInstance kitAlt = _gear.EquippedInstance(EquipmentSlot.AltWeapon);
            Check(cls.primaryWeapon == null || (kitMain != null && kitMain.Definition.grantedWeapon == cls.primaryWeapon && _rig.ActiveWeapon == cls.primaryWeapon), "the primary weapon is the item in the main hand");
            bool classShield = cls.offhandShield != null && (cls.primaryWeapon == null || !cls.primaryWeapon.twoHanded);
            Check(classShield ? (kitOff != null && kitOff.Definition.grantedWeapon == cls.offhandShield && _rig.OffhandWeapon == cls.offhandShield && _rig.Defense.HasShield)
                              : (kitOff == null && _rig.OffhandWeapon == null && !_rig.Defense.HasShield), "the class shield (if any) is the item in the off hand, otherwise the off hand is empty");
            Check(cls.secondaryWeapon == null ? kitAlt == null : (kitAlt != null && kitAlt.Definition.grantedWeapon == cls.secondaryWeapon), "the secondary weapon is the item in the second set's main hand");
            foreach (ItemInstance k in new[] { kitMain, kitOff, kitAlt })
                if (k != null) Check(k.Uid != Guid.Empty && kitUids.Add(k.Uid) && Census(k.Uid) == 1, "each kit item has its own uid held in exactly one place");

            // ---- B. empty hands are fists and nothing
            foreach (EquipmentSlot slot in new[] { EquipmentSlot.OffHand, EquipmentSlot.AltOffHand, EquipmentSlot.AltWeapon, EquipmentSlot.Weapon })
            {
                if (_gear.EquippedInstance(slot) == null) continue;
                Guid uid = WornUid(slot);
                InventoryCommandResult r = Unequip(slot);
                Check(r.Accepted, "unequip " + slot + ": " + r.Reason);
                Check(_gear.EquippedInstance(slot) == null && Census(uid) == 1 && _bag.HoldsInstanceUid(uid), "the " + slot + " item is now in the bag and nowhere else");
            }

            WeaponData fists = _loadout.Unarmed;
            yield return null;
            Check(_rig.ActiveWeapon == fists, "an empty main hand is the bare fists, not the class weapon");
            Check(_rig.OffhandWeapon == null, "an empty off hand stays empty, the class shield does not come back");
            Check(!_rig.Defense.CanBlock && !_rig.Defense.CanParry && !_rig.Defense.HasShield && !_rig.IsGuarding, "bare hands cannot block, parry or hold a shield");
            Check(_loadout.WeaponInSlot(1) == null, "an empty second set holds nothing");
            Check(fists.UsesSweptMelee && fists.swingPatterns.Length > 0 && fists.swingPatterns[0].kind == SwingKind.Thrust, "the fists punch through the swept-melee path with a thrust pattern");

            ItemData swordItem = Resolve("ashen_short_sword");
            ItemData shieldItem = Resolve("emberbulwark_shield");
            ItemData bowItem = Resolve("hollow_bow");
            ItemData ringItem = Resolve("cinder_band_ring");
            if (swordItem == null || shieldItem == null || bowItem == null) yield break;
            WeaponData sword = swordItem.grantedWeapon;
            WeaponData shield = shieldItem.grantedWeapon;
            WeaponData bow = bowItem.grantedWeapon;
            Check(fists.baseDamage < sword.baseDamage && fists.reach < sword.reach, "the fists hit weaker and shorter than the sword (data)");
            Check(sword.Hands == HandUse.OneHanded && shield.Hands == HandUse.OffHandOnly && bow.Hands == HandUse.TwoHanded, "the hand rule is derived from data: sword one-handed, shield off-hand only, bow two-handed");

            ItemInstance swordInst = Mint(swordItem);
            ItemInstance shieldInst = Mint(shieldItem);
            ItemInstance bowInst = Mint(bowItem);
            if (swordInst == null || shieldInst == null || bowInst == null) yield break;

            // ---- C. a sword alone: blocks (sword-only), no shield
            InventoryCommandResult eq = EquipFromBag(swordInst, EquipmentSlot.Weapon);
            Check(eq.Accepted, "the sword is equipped like clothing: " + eq.Reason);
            yield return null;
            Check(_rig.ActiveWeapon == sword && _gear.EquippedInstance(EquipmentSlot.Weapon) == swordInst, "the sword is the main hand weapon and the item in the slot");
            Check(_rig.Defense.CanBlock == sword.CanBlock && _rig.Defense.CanParry == sword.CanParry && !_rig.Defense.HasShield && !_rig.GuardHasShield, "the guard is derived from the sword alone: no shield");
            bool ripostePlain = RiposteRules.KitCanRiposte(sword.canRiposte, sword.IsShield, _rig.GuardHasShield, sword.IsRanged, sword.RiposteStrikeCount, sword.riposteWithOffhandShield);
            Check(ripostePlain == sword.CanRiposteWeapon, "sword-only riposte eligibility follows the sword data");

            // ---- D. shield in the off hand
            eq = EquipFromBag(shieldInst, EquipmentSlot.OffHand);
            Check(eq.Accepted, "the shield is equipped to the off hand: " + eq.Reason);
            yield return null;
            Check(_rig.OffhandWeapon == shield && _rig.Defense.HasShield && _rig.GuardHasShield && _rig.Defense.CanBlock && !_rig.Defense.CanParry, "with the shield worn the guard has a shield and the shield never parries");
            bool ripostePair = RiposteRules.KitCanRiposte(sword.canRiposte, sword.IsShield, _rig.GuardHasShield, sword.IsRanged, sword.RiposteStrikeCount, sword.riposteWithOffhandShield);
            Check(ripostePair == (sword.CanRiposteWeapon && sword.riposteWithOffhandShield), "sword+shield riposte eligibility is the sword's off-hand-shield allowance");

            // ---- E. drop the shield myself
            string before = Snap();
            ulong revBefore = _bag.Revision;
            InventoryCommandResult dropShield = DropWorn(EquipmentSlot.OffHand);
            Check(dropShield.Accepted, "dropping the worn shield is accepted: " + dropShield.Reason);
            yield return null;
            Check(_gear.EquippedInstance(EquipmentSlot.OffHand) == null && !_bag.HoldsInstanceUid(shieldInst.Uid) && _bag.Revision > revBefore, "the shield left the model and the revision moved");
            LootPickup ground = GroundOf(shieldInst.Uid);
            Check(ground != null && ground.Instance == shieldInst && Census(shieldInst.Uid) == 1, "the dropped shield is one ground item holding that exact instance (uid in exactly one place)");
            Check(_rig.OffhandWeapon == null && !_rig.Defense.HasShield && !_rig.GuardHasShield && _rig.Defense.CanBlock == sword.CanBlock, "the guard is sword-only again after the drop");
            Check(_loadout.Arms == null || !_loadout.Arms.ShieldShown, "the first-person arms no longer show a shield");
            Check(Snap() != before, "the drop changed the state");

            // ---- F. pickup: out of reach, then in reach into the empty off hand
            if (ground != null)
            {
                Vector3 home = _rig.transform.position;
                _rig.transform.position = home + new Vector3(80f, 0f, 0f);
                Physics.SyncTransforms();
                bool far = ground.CanInteract(_rig, out string farReason);
                _rig.transform.position = home;
                Physics.SyncTransforms();
                Check(!far, "a ground item out of reach cannot be taken: " + farReason);
                string unchanged = Snap();
                ground.OnInteractInstant(_rig);
                yield return null;
                Check(ground == null || ground.gameObject == null, "the pickup is spent once taken");
                Check(_rig.OffhandWeapon == shield && _gear.EquippedInstance(EquipmentSlot.OffHand) == shieldInst && _rig.Defense.HasShield, "the shield picked up goes into the empty matching hand and the guard has it again");
                Check(Census(shieldInst.Uid) == 1, "after the pickup the shield uid is in exactly one place");
                Check(unchanged != Snap(), "the pickup changed the state");
            }

            // a race: two takers of one ground item in the same frame -> the instance is handed out once
            DropWorn(EquipmentSlot.OffHand);
            yield return null;
            LootPickup contested = GroundOf(shieldInst.Uid);
            Check(contested != null, "the shield is on the ground again");
            if (contested != null)
            {
                contested.OnInteractInstant(_rig);
                contested.OnInteractInstant(_rig);
                Check(Census(shieldInst.Uid, false) == 1, "two takers in one frame: the uid is held once");
                yield return null;
                Check(Census(shieldInst.Uid) == 1, "after the frame the uid is still in exactly one place");
            }

            // ---- G. forged commands change nothing
            if (WornUid(EquipmentSlot.OffHand) == Guid.Empty && _bag.HoldsInstanceUid(shieldInst.Uid)) EquipFromBag(shieldInst, EquipmentSlot.OffHand);
            ItemInstance spareShield = Mint(shieldItem);
            ItemInstance spareSword = Mint(swordItem);
            string state = Snap();
            Guid stranger = Guid.NewGuid();
            Guid wornMainUid = WornUid(EquipmentSlot.Weapon);
            Check(!SubmitRaw(InventoryCommandKind.Drop, _rig.PlayerId + 77, 0, EquipmentSlot.Weapon, wornMainUid, Guid.Empty).Accepted, "a drop command for another player's id is refused");
            Check(!SubmitRaw(InventoryCommandKind.Drop, _rig.PlayerId, 0, EquipmentSlot.Weapon, stranger, Guid.Empty).Accepted, "dropping an item that is not worn there (random uid) is refused");
            Check(!SubmitRaw(InventoryCommandKind.Drop, _rig.PlayerId, StackOf(bowInst), EquipmentSlot.None, stranger, Guid.Empty).Accepted, "dropping a bag stack with a uid it does not hold is refused");
            Check(!SubmitRaw(InventoryCommandKind.Drop, _rig.PlayerId, 0, (EquipmentSlot)250, wornMainUid, Guid.Empty).Accepted, "a drop from an invalid slot is refused");
            Check(!SubmitRaw(InventoryCommandKind.Drop, _rig.PlayerId, 0, EquipmentSlot.Weapon, Guid.Empty, Guid.Empty).Accepted, "a drop with no identity is refused");
            Check(!SubmitRaw(InventoryCommandKind.Drop, _rig.PlayerId, 987654, EquipmentSlot.None, bowInst.Uid, Guid.Empty).Accepted, "dropping an unknown stack id is refused");
            Check(!EquipFromBag(bowInst, EquipmentSlot.OffHand).Accepted, "a two-handed bow forged into the off hand is refused");
            if (spareSword != null) Check(!EquipFromBag(spareSword, EquipmentSlot.OffHand).Accepted, "a sword forged into the off hand is refused (no dual wielding)");
            if (spareShield != null)
            {
                Check(!EquipFromBag(spareShield, EquipmentSlot.Weapon).Accepted, "a shield forged into the main hand is refused");
                Check(!EquipFromBag(spareShield, EquipmentSlot.AltWeapon).Accepted, "a shield forged into the second set's main hand is refused");
            }
            Check(Snap() == state, "every forged command left the state exactly as it was");
            Check(Census(wornMainUid) == 1 && Census(bowInst.Uid) == 1, "after the forged commands the uids are still in exactly one place");

            // a replayed sequence
            bool made = _authority.TryCreateLocalCommand(InventoryCommandKind.Drop, StackOf(bowInst), 0, Vector2Int.zero, EquipmentSlot.None, bowInst.Uid, Guid.Empty, out InventoryCommand once, out _);
            if (made)
            {
                InventoryCommandResult first = _authority.Submit(_rig, once);
                InventoryCommandResult replay = _authority.Submit(_rig, once);
                Check(first.Accepted && !replay.Accepted, "a replayed command (same sequence) is refused");
                yield return null;
                Check(Census(bowInst.Uid) == 1, "the replay did not duplicate the dropped item");
                LootPickup bowGround = GroundOf(bowInst.Uid);
                if (bowGround != null) bowGround.OnInteractInstant(_rig);
                yield return null;
                Check(_bag.HoldsInstanceUid(bowInst.Uid) && Census(bowInst.Uid) == 1, "the dropped bow was picked back into the bag (hands are occupied, so it stays in the bag)");
            }

            // ---- H. a duplicate uid on the ground is never taken a second time
            ItemInstance wornSword = _gear.EquippedInstance(EquipmentSlot.Weapon);
            if (wornSword != null)
            {
                LootPickup clone = director.SpawnLootInstance(wornSword, _rig.transform.position + _rig.transform.forward * 1.1f + Vector3.up * 0.5f);
                yield return null;
                string dupState = Snap();
                if (clone != null) clone.OnInteractInstant(_rig);
                yield return null;
                Check(Census(wornSword.Uid, false) == 1 && Snap() == dupState, "a ground item carrying an uid the player already wears is refused and the model is unchanged");
                if (clone != null) Destroy(clone.gameObject);
            }

            ClearGround();
            yield return null;

            // ---- I. a two-handed weapon stows the off hand
            ItemInstance shieldWorn = _gear.EquippedInstance(EquipmentSlot.OffHand);
            ItemInstance swordWorn = _gear.EquippedInstance(EquipmentSlot.Weapon);
            Check(shieldWorn != null && swordWorn != null, "setup: a sword and a shield are worn");
            if (shieldWorn != null && swordWorn != null)
            {
                if (_bag.IndexOfInstance(bowInst.Uid) < 0) _bag.TryAddInstance(bowInst, out _);

                // full bag: no room to stow the shield -> refused, nothing moved
                List<ItemInstance> filler = new List<ItemInstance>();
                ItemData fill = ringItem != null ? ringItem : Resolve("ember_coin");
                if (fill != null && fill.IsEquippable)
                {
                    int guard = 0;
                    while (_bag.FindFreeOrigin(shieldItem.EffectiveFootprint).x >= 0 && guard++ < 80)
                    {
                        if (!ItemInstance.TryCreateNew(fill, 1, AffixRarity.Common, null, _items, out ItemInstance filler1, out _)) break;
                        if (!_bag.TryAddInstance(filler1, out _)) break;
                        filler.Add(filler1);
                    }
                }
                Check(_bag.FindFreeOrigin(shieldItem.EffectiveFootprint).x < 0, "the bag has no hole left for the shield");
                string full = Snap();
                InventoryCommandResult refused = EquipFromBag(bowInst, EquipmentSlot.Weapon);
                Check(!refused.Accepted && Snap() == full, "a two-handed weapon with no room to stow the off-hand shield is refused and nothing moves: " + refused.Reason);
                Check(Census(shieldWorn.Uid) == 1 && Census(swordWorn.Uid) == 1 && Census(bowInst.Uid) == 1, "the refusal keeps every uid in one place");

                // make room again, then it stows
                for (int i = 0; i < filler.Count; i++)
                {
                    int at = _bag.IndexOfInstance(filler[i].Uid);
                    if (at >= 0) _bag.TryRemoveAt(at, 1);
                }
                InventoryCommandResult two = EquipFromBag(bowInst, EquipmentSlot.Weapon);
                Check(two.Accepted, "the two-handed bow is equipped when there is room: " + two.Reason);
                yield return null;
                Check(_gear.EquippedInstance(EquipmentSlot.Weapon) == bowInst && _gear.EquippedInstance(EquipmentSlot.OffHand) == null, "the bow is in the main hand and the off hand is empty");
                Check(_bag.HoldsInstanceUid(shieldWorn.Uid) && _bag.HoldsInstanceUid(swordWorn.Uid) && Census(shieldWorn.Uid) == 1 && Census(swordWorn.Uid) == 1, "the shield was stowed in the bag and the replaced sword is in the bag (one place each)");
                Check(_rig.ActiveWeapon == bow && _rig.OffhandWeapon == null && !_rig.Defense.HasShield, "the guard and the hand follow: bow in hand, no shield");
                string bowState = Snap();
                InventoryCommandResult shieldNextToBow = EquipFromBag(shieldWorn, EquipmentSlot.OffHand);
                Check(!shieldNextToBow.Accepted && _gear.EquippedInstance(EquipmentSlot.OffHand) == null && Snap() == bowState, "a shield cannot be put next to a two-handed weapon: " + shieldNextToBow.Reason);

                // back to sword and shield
                Check(EquipFromBag(swordWorn, EquipmentSlot.Weapon).Accepted, "the sword goes back to the main hand");
                Check(EquipFromBag(shieldWorn, EquipmentSlot.OffHand).Accepted, "and the shield to the off hand once the hand is free");
                yield return null;
                Check(_rig.Defense.HasShield && _rig.ActiveWeapon == sword, "sword and shield again");
            }

            // ---- J. dropping the main hand leaves the fists
            InventoryCommandResult dropMain = DropWorn(EquipmentSlot.Weapon);
            Check(dropMain.Accepted, "the main-hand weapon can be dropped: " + dropMain.Reason);
            yield return null;
            Check(_rig.ActiveWeapon == fists && !_rig.Defense.CanParry && _rig.Defense.CanBlock == _rig.Defense.HasShield, "after dropping the main hand the fists are in hand: they never parry, and only the worn shield can still block");

            // ---- K. cleanup: a clear is a reset to the starting kit
            ClearGround();
            _gear.Clear();
            _bag.Clear();
            yield return null;
            Check(_gear.KitIsItems && (cls.primaryWeapon == null || _rig.ActiveWeapon == cls.primaryWeapon), "after a clear the starting kit is back");
        }
    }
}
