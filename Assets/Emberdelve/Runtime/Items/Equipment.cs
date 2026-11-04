using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Combat;
using Emberdelve.Player;
using Emberdelve.Skills;

namespace Emberdelve.Items
{
    /// <summary>
    /// What the local player is wearing and holding, as data-driven slots.
    ///
    /// Equipping does two things and nothing else: a weapon item changes the hand through ClassLoadout, which
    /// already re-resolves the defense profile and re-enables the right combat component, and every equipped
    /// item contributes its StatModifiers through one named source on the same CharacterStatStack the class
    /// passives use. Health, mana, regeneration, movement, block reduction and damage multipliers therefore
    /// all follow one rule, and unequipping removes exactly what that item contributed.
    ///
    /// U2B4: a filled slot holds one ItemInstance, and the definition-facing view the UI, the stat rebuild and
    /// the existing fixtures read is derived from that instance. The instance is what travels between the bag
    /// and a slot, so a rolled piece of gear keeps its uid, its rarity and its exact rolls through equipping,
    /// unequipping and replacing.
    ///
    /// U2B5: the live stat rebuild reads those instances through <see cref="WornInstances"/>, so the actor's
    /// effective six scores are the pristine class scores plus each worn definition's fixed authored bonuses
    /// plus that instance's own rolled ability-score affix records - applied once, with no reroll, no extra mint
    /// and no asset write.
    ///
    /// U2B6: the same worn instances also feed the legacy StatModifier source below, so four rolled numeric
    /// affix keys reach the stat vocabulary the pools and locomotion already consume - max_health to
    /// SkillStat.MaxHealth flat, max_mana to SkillStat.MaxMana flat, move_speed to SkillStat.MoveSpeed flat
    /// (metres per second, never a multiplier) and mana_regen to SkillStat.ManaRegen flat (per second). The
    /// definition's authored modifiers and the instance's rolled records are both added once per worn slot and
    /// they ADD to the authored numbers, they never replace them. The six ability keys are deliberately not put
    /// into this source, because U2B5 already applies them through the attribute stack and applying them here as
    /// well would count them twice. armor is mapped in this same source too (U2B8): the fixed authored
    /// SkillStat.Armor modifiers a definition declares and that instance's rolled armor affix points both enter
    /// as Flat additions, and their summed total is the armor rating the rig exposes to the authority and
    /// DamageResolver applies to Physical damage only, from every direction, after the directional guard
    /// (Docs/Design/ARMOR_RULES.md) - an armor affix is still not silently reinterpreted as block reduction.
    /// melee_damage and projectile_damage were given their own authority-read flat weapon channel in U2B7
    /// rather than being smuggled into a damage multiplier.
    ///
    /// D3c: two weapon sets. Set 0 is <see cref="EquipmentSlot.Weapon"/> + <see cref="EquipmentSlot.OffHand"/>, set 1 is
    /// <see cref="EquipmentSlot.AltWeapon"/> + <see cref="EquipmentSlot.AltOffHand"/>. All four hold their worn instances all
    /// the time (so uids, snapshots and the "one uid, one place" rule see them), but only the ACTIVE set is live: the
    /// <see cref="WornInstances"/> window, the stat source and the move-speed penalty read the active set only, and the hand
    /// that combat uses is the active set's (set N's main hand is ClassLoadout slot N; an empty slot falls back to what the class
    /// asset authors, exactly as an empty Weapon slot always did). The active set is state of this component, changed only by
    /// <see cref="TrySwapWeaponSet"/> (the authority command SwapWeaponSet) or by a forced/local ClassLoadout switch that
    /// reports it through <see cref="BeginWeaponSet"/> / <see cref="CompleteWeaponSet"/>.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class Equipment : MonoBehaviour
    {
        /// <summary>
        /// Length of the slot arrays: one entry per <see cref="EquipmentSlot"/> numeric value (index 0 is None
        /// and never holds anything), derived from the enum's highest value so appending a slot to the enum
        /// is the only edit a new slot needs (U2B11: Back = 6 made this 7).
        /// </summary>
        public static readonly int SlotCount = ComputeSlotCount();

        private static int ComputeSlotCount()
        {
            int max = 0;
            foreach (EquipmentSlot slot in Enum.GetValues(typeof(EquipmentSlot)))
            {
                if ((int)slot > max) max = (int)slot;
            }
            return max + 1;
        }

        /// <summary>The authoritative per-slot item. Null when nothing is worn there.</summary>
        private readonly ItemInstance[] _gear = new ItemInstance[SlotCount];

        /// <summary>Definition-facing view of the same slots, rebuilt from the instances and never a second store.</summary>
        private readonly ItemData[] _slotViews = new ItemData[SlotCount];

        private PlayerRig _rig;
        private Inventory _inventory;

        private readonly List<StatModifier> _modifierScratch = new List<StatModifier>(8);
        private readonly List<string> _summaryScratch = new List<string>(6);

        private WornInstanceListView _wornInstanceView;

        /// <summary>D3c: the live weapon set (0 or 1). Mirrors ClassLoadout.ActiveSlot; saved in the loadout snapshot.</summary>
        private int _activeSet;

        /// <summary>D3c: while a restore (or another caller that commits its own revision) switches sets, the switch must not commit a second one.</summary>
        private int _quietSetChange;

        /// <summary>D3c: the live weapon set, 0 (Weapon + OffHand) or 1 (AltWeapon + AltOffHand).</summary>
        public int ActiveWeaponSet => _activeSet;

        /// <summary>
        /// D3c: the move-speed penalty of the pieces worn in the LIVE slots, in reference points on a 300 base, as
        /// <see cref="ItemData.moveSpeedPenalty"/> authors it. A weapon-granting item contributes nothing here (its weapon's
        /// own held penalty is what counts, once). Recomputed by every <see cref="RefreshStats"/>, read by the rig's
        /// move-flow factor, so it reaches the speed through exactly one path.
        /// </summary>
        public float MoveSpeedPenaltyPoints { get; private set; }

        public IReadOnlyList<ItemData> Slots => _slotViews;
        public int FilledCount { get; private set; }

        /// <summary>
        /// Every slot as the actual <see cref="ItemInstance"/> it holds, in slot order, with null for an empty
        /// slot. It is a window onto the one authoritative array and not a second store: it is rebuilt from the
        /// same <see cref="_gear"/> the transfers commit, and its view type implements IReadOnlyList and nothing
        /// else, so no cast makes the worn gear writable. This is the view the rig's attribute rebuild reads,
        /// which is what makes the numbers an actor runs with come from the very instances the bag handed over -
        /// uid, rarity and exact rolls included.
        /// </summary>
        public IReadOnlyList<ItemInstance> WornInstances
        {
            get
            {
                if (_wornInstanceView == null) _wornInstanceView = new WornInstanceListView(this, _gear);
                return _wornInstanceView;
            }
        }

        /// <summary>R7: the player this component belongs to; every event it raises carries it (0 = no player, e.g. a bare bag in a tool).</summary>
        private int OwnerId
        {
            get
            {
                if (_rig == null) _rig = GetComponent<PlayerRig>();
                return _rig != null ? _rig.PlayerId : GameEvents.NoPlayer;
            }
        }

        private void Awake()
        {
            _rig = GetComponent<PlayerRig>();
            _inventory = GetComponent<Inventory>();
        }

        public void Initialize(PlayerRig rig)
        {
            _rig = rig != null ? rig : GetComponent<PlayerRig>();
            _inventory = _rig != null && _rig.Inventory != null ? _rig.Inventory : GetComponent<Inventory>();
        }

        /// <summary>Finds or adds the equipment component on a rig, so an older scene still works.</summary>
        public static Equipment Ensure(GameObject target)
        {
            if (target == null) return null;
            Equipment existing = target.GetComponent<Equipment>();
            return existing != null ? existing : target.AddComponent<Equipment>();
        }

        public ItemData Equipped(EquipmentSlot slot)
        {
            int index = SlotIndex(slot);
            return index >= 0 ? _slotViews[index] : null;
        }

        /// <summary>
        /// The actual item one slot holds - the same instance the bag held before it was worn, with its uid,
        /// rarity and rolls intact - or null when the slot is empty.
        /// </summary>
        public ItemInstance EquippedInstance(EquipmentSlot slot)
        {
            int index = SlotIndex(slot);
            return index >= 0 ? _gear[index] : null;
        }

        /// <summary>True when one of these slots already holds this exact identity.</summary>
        public bool HoldsUid(Guid uid)
        {
            if (uid == Guid.Empty) return false;
            for (int i = 0; i < SlotCount; i++)
            {
                ItemInstance instance = _gear[i];
                if (instance != null && instance.Uid == uid) return true;
            }
            return false;
        }

        public bool IsEquipped(ItemData item)
        {
            if (item == null) return false;
            for (int i = 0; i < SlotCount; i++) if (_slotViews[i] == item) return true;
            return false;
        }

        public int IndexOfSlot(EquipmentSlot slot) => SlotIndex(slot);

        /// <summary>
        /// Moves the item in one specific bag slot into the matching equipment slot, and whatever was in that
        /// slot back into the bag. The bag half is planned and committed by Inventory as one transfer, so a
        /// replacement that only fits in the space the incoming item vacates is allowed, and a replacement that
        /// fits nowhere changes nothing at all. The selected slot is the one consumed, not the first stack that
        /// happens to hold the same definition: the instance that slot holds is what is worn.
        /// </summary>
        public bool TryEquipFromInventory(int inventoryIndex, out string reason)
        {
            return TryEquipFromInventory(inventoryIndex, EquipmentSlot.None, out reason);
        }

        /// <summary>
        /// D3b: <see cref="TryEquipFromInventory(int, out string)"/> with an EXPLICIT target slot. <see cref="EquipmentSlot.None"/>
        /// means "the item's own slot": for a ring that is the first empty ring slot, and when both ring slots are full the
        /// equip is refused (nothing is replaced behind the player's back) until the caller names the ring slot to replace.
        /// A named slot must accept the item (<see cref="EquipmentSlots.Accepts"/>): a ring fits either ring slot, anything
        /// else only its authored slot.
        /// </summary>
        public bool TryEquipFromInventory(int inventoryIndex, EquipmentSlot target, out string reason)
        {
            reason = null;

            if (_rig == null)
            {
                reason = "no player to wear it";
                return false;
            }

            if (_inventory == null)
            {
                reason = "no inventory to equip from";
                return false;
            }

            if (inventoryIndex < 0 || inventoryIndex >= _inventory.Slots.Count)
            {
                reason = "no item in that slot";
                return false;
            }

            ItemInstance incoming = _inventory.InstanceAt(inventoryIndex);
            if (incoming == null)
            {
                ItemData definition = _inventory.Slots[inventoryIndex].Item;
                reason = definition != null && definition.IsEquippable
                    ? $"the {definition.displayName} in that bag slot carries no item identity to wear"
                    : "no item in that slot";
                return false;
            }

            return TryEquipSelected(inventoryIndex, incoming, target, out reason);
        }

        /// <summary>
        /// Equips a definition the player is actually carrying. An ItemData asset on its own is a description,
        /// not an item: if the bag does not hold it there is nothing to move, so the call is refused instead of
        /// wearing a copy that never existed.
        /// </summary>
        public bool TryEquipItem(ItemData item, out string reason)
        {
            reason = null;

            if (item == null)
            {
                reason = "no item";
                return false;
            }

            if (!item.IsEquippable)
            {
                reason = $"{item.displayName} cannot be worn or held";
                return false;
            }

            if (_inventory == null)
            {
                reason = "no inventory to equip from";
                return false;
            }

            int bagIndex = _inventory.IndexOfItem(item);
            if (bagIndex < 0)
            {
                reason = $"{item.displayName} is not in the bag to equip";
                return false;
            }

            ItemInstance incoming = _inventory.InstanceAt(bagIndex);
            if (incoming == null)
            {
                reason = $"the {item.displayName} in bag slot {bagIndex} carries no item identity to wear";
                return false;
            }

            return TryEquipSelected(bagIndex, incoming, EquipmentSlot.None, out reason);
        }

        /// <summary>
        /// The one equip path. Everything that can be decided without touching state is decided here, first;
        /// the bag transfer is the last step and the last chance to refuse, so a refusal never leaves a slot
        /// half filled or a bag half emptied. The instance resolved from the selected bag slot is the instance
        /// that ends up worn: no copy, no re-roll and no second identity is minted on the way in.
        /// </summary>
        private bool TryEquipSelected(int bagIndex, ItemInstance incoming, EquipmentSlot requested, out string reason)
        {
            using (GameEventTransaction.Defer()) return TryEquipSelectedState(bagIndex, incoming, requested, out reason);
        }

        private bool TryEquipSelectedState(int bagIndex, ItemInstance incoming, EquipmentSlot requested, out string reason)
        {
            reason = null;

            if (_rig == null)
            {
                reason = "no player to wear it";
                return false;
            }

            if (_inventory == null)
            {
                reason = "no inventory to equip from";
                return false;
            }

            if (incoming == null || incoming.Definition == null)
            {
                reason = "no item";
                return false;
            }

            ItemData item = incoming.Definition;

            if (!incoming.IsEquippable)
            {
                reason = $"{item.displayName} cannot be worn or held";
                return false;
            }

            if (!TryChooseSlot(item, requested, out EquipmentSlot targetSlot, out reason)) return false;

            int index = SlotIndex(targetSlot);
            if (index < 0)
            {
                reason = "no such equipment slot";
                return false;
            }

            if (item.GrantsWeapon && item.grantedWeapon == null)
            {
                reason = $"{item.displayName} declares no weapon to hold";
                return false;
            }

            if (_rig.IsDead)
            {
                reason = "dead players cannot change gear";
                return false;
            }

            // D1b: the main-hand weapon cannot change while its wielder is mid-attack (same lock as the swap input).
            if (item.GrantsWeapon && item.equipSlot != EquipmentSlot.OffHand && _rig.Loadout != null && _rig.Loadout.IsActionLocked)
            {
                reason = "cannot change weapons during an attack";
                GameEvents.RaiseToast(OwnerId, _rig.Loadout.ActionLockMessage);
                return false;
            }

            if (!TryCheckHandRules(item, targetSlot, out int displaceOffIndex, out reason)) return false;

            // One identity, one place: the same item cannot be worn in another slot while it is also being
            // picked up out of the bag.
            if (HoldsUid(incoming.Uid))
            {
                reason = $"{item.displayName} [{incoming.UidText}] is already worn in another slot: " +
                         "one uid is one item";
                return false;
            }

            ItemInstance previous = _gear[index];

            if (!_inventory.CanCommitRevision(out reason)) return false;

            // The bag half first. Inventory plans the hole for the outgoing item using the space the incoming
            // unit is about to vacate, and commits both moves together. A refusal here has changed nothing, and
            // a success has told nobody yet: the transfer is silent so this component can finish its own half.
            // U5A: a two-handed weapon needs the off hand of its set free: the worn off-hand item goes to the bag first (the same unequip
            // every off-hand takes off uses). When the equip itself then fails the off-hand item is put back, so a refusal changes nothing.
            ItemInstance displacedOff = null;
            EquipmentSlot displacedSlot = EquipmentSlot.None;
            if (displaceOffIndex >= 0)
            {
                displacedOff = _gear[displaceOffIndex];
                displacedSlot = (EquipmentSlot)displaceOffIndex;
                if (!TryUnequipSlotState(displaceOffIndex, out string stowReason))
                {
                    reason = $"the off hand cannot be freed for {item.displayName}: {stowReason}";
                    return false;
                }
            }

            if (!_inventory.TryEquipTransfer(bagIndex, incoming, previous, out reason))
            {
                if (displacedOff != null)
                {
                    int back = _inventory.IndexOfInstance(displacedOff.Uid);
                    if (back >= 0) TryEquipSelectedState(back, displacedOff, displacedSlot, out _);
                }
                return false;
            }

            ApplyToSlot(index, incoming);
            RefreshStats();
            _inventory.CommitRevision();

            // Only now is the whole change real: bag, slot, hand and the refreshed stat source. The one
            // inventory notification an equipment transfer owes its observers is raised here, never from
            // inside the bag helper, so a subscriber can never read a half-committed transfer.
            GameEvents.RaiseInventoryChanged(OwnerId);
            GameEvents.RaiseEquipmentChanged(OwnerId, item, true);
            GameEvents.RaiseToast(OwnerId, $"Equipped {item.displayName}");
            return true;
        }

        /// <summary>
        /// D3b: which slot would this item go to? A named <paramref name="requested"/> slot must accept the item. With
        /// <see cref="EquipmentSlot.None"/> the item's own slot is used, except for a ring: the first EMPTY ring slot
        /// (RingLeft, then RingRight), and when both are full there is no default and the answer is a refusal that says an
        /// explicit target slot is required. Read-only.
        /// </summary>
        public bool TryChooseSlot(ItemData item, EquipmentSlot requested, out EquipmentSlot slot, out string reason)
        {
            slot = EquipmentSlot.None;

            if (item == null)
            {
                reason = "no item";
                return false;
            }

            if (!EquipmentSlots.IsRealSlot(item.equipSlot))
            {
                reason = $"{item.displayName} cannot be worn or held";
                return false;
            }

            if (requested != EquipmentSlot.None)
            {
                if (!EquipmentSlots.Accepts(item.equipSlot, requested))
                {
                    reason = $"{item.displayName} does not fit the {requested} slot";
                    return false;
                }

                slot = requested;
                reason = null;
                return true;
            }

            if (!EquipmentSlots.IsRing(item.equipSlot))
            {
                slot = item.equipSlot;
                reason = null;
                return true;
            }

            EquipmentSlot[] candidates = EquipmentSlots.CandidatesFor(item.equipSlot);
            for (int i = 0; i < candidates.Length; i++)
            {
                int candidate = SlotIndex(candidates[i]);
                if (candidate >= 0 && _gear[candidate] == null)
                {
                    slot = candidates[i];
                    reason = null;
                    return true;
                }
            }

            reason = $"both ring slots are full: name the ring slot to replace for {item.displayName}";
            return false;
        }

        /// <summary>
        /// D3b: moves the ring in <paramref name="from"/> to <paramref name="to"/> and whatever <paramref name="to"/> held
        /// back to <paramref name="from"/>, as one step. Both are ring slots and differ; at least one holds a ring (an empty
        /// target just moves the ring across). It is a pure exchange between two slots of one array: no bag cell is touched
        /// and no identity is created or dropped, so every uid is conserved. Every check comes before the first write, so a
        /// refusal (dead player, no revision room, a worn item that does not accept the other slot) changes nothing - not a
        /// slot, not the stats, not the shared revision. The stat source is rebuilt through the same
        /// <see cref="RefreshStats"/> every equip uses, so the totals are unchanged by a swap and nothing accumulates.
        /// </summary>
        public bool TrySwapRingSlots(EquipmentSlot from, EquipmentSlot to, out string reason)
        {
            using (GameEventTransaction.Defer()) return TrySwapRingSlotsState(from, to, out reason);
        }

        private bool TrySwapRingSlotsState(EquipmentSlot from, EquipmentSlot to, out string reason)
        {
            reason = null;

            if (!EquipmentSlots.IsRing(from) || !EquipmentSlots.IsRing(to) || from == to)
            {
                reason = "a ring swap needs the two different ring slots";
                return false;
            }

            if (_rig == null)
            {
                reason = "no player is wearing it";
                return false;
            }

            if (_rig.IsDead)
            {
                reason = "dead players cannot change gear";
                return false;
            }

            int a = SlotIndex(from);
            int b = SlotIndex(to);
            if (a < 0 || b < 0)
            {
                reason = "no such equipment slot";
                return false;
            }

            ItemInstance first = _gear[a];
            ItemInstance second = _gear[b];
            if (first == null && second == null)
            {
                reason = "both ring slots are empty";
                return false;
            }

            if (first != null && !EquipmentSlots.Accepts(first.Definition.equipSlot, to))
            {
                reason = $"{first.Definition.displayName} does not fit the {to} slot";
                return false;
            }

            if (second != null && !EquipmentSlots.Accepts(second.Definition.equipSlot, from))
            {
                reason = $"{second.Definition.displayName} does not fit the {from} slot";
                return false;
            }

            Inventory revisionOwner = BagPartner();
            if (revisionOwner == null)
            {
                reason = "equipment has no shared inventory revision owner";
                return false;
            }

            if (!revisionOwner.CanCommitRevision(out reason)) return false;

            _gear[a] = second;
            _gear[b] = first;
            RecomputeSlots();
            RefreshStats();
            revisionOwner.CommitRevision();

            GameEvents.RaiseInventoryChanged(OwnerId);
            return true;
        }

        // ------------------------------------------------------------ D3c: weapon sets

        /// <summary>D3c: is the slot at this array index part of what is live? Every slot is, except the hand slots of the set that is not in hand.</summary>
        internal bool IsSlotLive(int index)
        {
            int set = EquipmentSlots.SetOf((EquipmentSlot)index);
            return set < 0 || set == _activeSet;
        }

        // ------------------------------------------------------------ U5A: hands as items

        private bool _kitIsItems;

        /// <summary>
        /// U5A: true once the class kit has been minted into the hand slots as real items (a rig with a class, a catalog and a configured
        /// bare-fist weapon). From then on an empty hand is empty: no class weapon or shield is quietly put back, the main hand falls back to
        /// the unarmed fists and the off hand to nothing. A bare bag/equipment pair (a fixture without that wiring) keeps the class fallback.
        /// </summary>
        public bool KitIsItems => _kitIsItems;

        private WeaponData Fists => _rig != null && _rig.Loadout != null ? _rig.Loadout.Unarmed : null;

        /// <summary>U5A: starts seeding the class kit: clears the four hand slots (no bag, no revision: a class apply is a run start).</summary>
        internal void BeginKit()
        {
            for (int i = 1; i < SlotCount; i++)
            {
                if (EquipmentSlots.SetOf((EquipmentSlot)i) >= 0) _gear[i] = null;
            }
            _kitIsItems = true;
        }

        /// <summary>U5A: puts one minted kit item straight into a hand slot (the loadout already holds the matching weapon).</summary>
        internal void SeedKitHand(EquipmentSlot slot, ItemInstance instance)
        {
            int index = SlotIndex(slot);
            if (index >= 0 && instance != null && EquipmentSlots.SetOf(slot) >= 0) _gear[index] = instance;
        }

        /// <summary>U5A: finishes the kit seed: slot views, stats, and the rig's off hand for the live set.</summary>
        internal void EndKit()
        {
            RecomputeSlots();
            RefreshStats();
            if (_rig != null) _rig.SetOffhandWeapon(WornOffhandOfSet(_activeSet));
        }

        /// <summary>
        /// U5A: the hand rules, derived from the weapon DATA (<see cref="WeaponData.Hands"/>), checked on the server before anything moves.
        /// A shield goes to an off-hand slot only; a one-handed weapon goes to a main-hand slot only (no dual wielding); a two-handed weapon
        /// goes to a main hand and needs the off hand of its set empty (a worn off-hand item is moved to the bag by the equip); nothing goes
        /// to the off hand while the main hand of its set holds a two-handed weapon. The bare fists are never an item.
        /// </summary>
        public bool TryCheckHandRules(ItemData item, EquipmentSlot target, out int displaceOffIndex, out string reason)
        {
            displaceOffIndex = -1;
            reason = null;

            WeaponData held = item != null ? item.grantedWeapon : null;
            int set = EquipmentSlots.SetOf(target);
            if (held == null || set < 0) return true;

            if (held.isUnarmed)
            {
                reason = "bare fists are not an item";
                return false;
            }

            bool offSlot = EquipmentSlots.IsOffHand(target);
            HandUse use = held.Hands;
            if (use == HandUse.OffHandOnly && !offSlot)
            {
                reason = $"{item.displayName} is a shield: it only goes in an off hand";
                return false;
            }

            if (use != HandUse.OffHandOnly && offSlot)
            {
                reason = $"{item.displayName} is a weapon: it only goes in a main hand (no dual wielding)";
                return false;
            }

            int mainIndex = SlotIndex(EquipmentSlots.MainHandOf(set));
            int offIndex = SlotIndex(EquipmentSlots.OffHandOf(set));
            if (offSlot)
            {
                ItemInstance main = mainIndex >= 0 ? _gear[mainIndex] : null;
                WeaponData mainWeapon = main != null && main.Definition != null ? main.Definition.grantedWeapon : null;
                if (mainWeapon != null && mainWeapon.Hands == HandUse.TwoHanded)
                {
                    reason = $"{item.displayName} cannot be used: the two-handed {mainWeapon.displayName} fills both hands";
                    return false;
                }
            }
            else if (use == HandUse.TwoHanded && offIndex >= 0 && _gear[offIndex] != null)
            {
                displaceOffIndex = offIndex;
            }

            return true;
        }

        /// <summary>D3c: the instance worn in a weapon set's main hand slot (null = the class default weapon is in that hand).</summary>
        public ItemInstance MainHandInstance(int set)
        {
            return EquippedInstance(EquipmentSlots.MainHandOf(set));
        }

        /// <summary>
        /// D3c: the weapon that would be in set <paramref name="set"/>'s main hand: the worn item's weapon, else the class asset's
        /// (primary for set 0, secondary for set 1). Null when neither exists. Read-only.
        /// </summary>
        public WeaponData WeaponOfSet(int set)
        {
            int index = SlotIndex(EquipmentSlots.MainHandOf(set));
            return ResolveWeaponOfSet(set, index >= 0 ? _gear[index] : null);
        }

        private WeaponData ResolveWeaponOfSet(int set, ItemInstance wornMainHand)
        {
            if (set < 0 || set >= EquipmentSlots.WeaponSetCount) return null;
            if (wornMainHand != null && wornMainHand.Definition != null && wornMainHand.Definition.grantedWeapon != null)
                return wornMainHand.Definition.grantedWeapon;

            if (_kitIsItems) return set == 0 ? Fists : null; // U5A: an empty main hand is the bare fists (set 1) or nothing (set 2), never the class weapon

            CharacterClassData data = _rig != null ? _rig.Class : null;
            if (data == null) return null;
            return set == 0 ? data.primaryWeapon : data.secondaryWeapon;
        }

        /// <summary>The weapon set's off hand: the worn item's weapon, or null (the rig then falls back to the class asset's off hand).</summary>
        private WeaponData WornOffhandOfSet(int set)
        {
            int index = SlotIndex(EquipmentSlots.OffHandOf(set));
            ItemInstance worn = index >= 0 ? _gear[index] : null;
            return worn != null && worn.Definition != null ? worn.Definition.grantedWeapon : null;
        }

        /// <summary>
        /// D3c: ClassLoadout calls this when a different weapon set is about to take the hand, BEFORE the rig re-resolves its defense,
        /// so the new set's off hand is already in place when the main hand is applied. Records the live set; for a real swap it also
        /// puts the set's off hand in the rig (nothing is touched when the rig already has that exact off hand). Returns whether the
        /// live set value changed.
        /// </summary>
        internal bool BeginWeaponSet(int set, bool isSwap)
        {
            if (set < 0 || set >= EquipmentSlots.WeaponSetCount) return false;

            bool changed = _activeSet != set;
            _activeSet = set;

            if (isSwap && _rig != null)
            {
                WeaponData worn = WornOffhandOfSet(set);
                CharacterClassData data = _rig.Class;
                WeaponData effective = worn != null ? worn : (data != null && !_kitIsItems ? data.offhandShield : null);
                if (!ReferenceEquals(_rig.OffhandWeapon, effective)) _rig.SetOffhandWeapon(worn);
            }

            return changed;
        }

        /// <summary>
        /// D3c: ClassLoadout calls this after the hand has changed. A real swap (or any change of the live set) re-derives the stat source
        /// and the penalty from the new live slots through the same <see cref="RefreshStats"/> every equip uses, so nothing the other
        /// set granted stays behind and nothing is added twice; a real swap also commits ONE shared revision (the live set is part of the
        /// saved loadout) and tells observers once. A swap in the middle of a restore or an unequip leaves the revision to that operation.
        /// </summary>
        internal void CompleteWeaponSet(bool setChanged, bool isSwap)
        {
            if (!setChanged && !isSwap) return;

            // Nothing worn in any hand slot means neither set contributes anything, so a swap cannot change a stat: the (heavier)
            // rebuild is skipped and a bare class swaps exactly as it did before weapon sets existed.
            if (HasWornHandItem()) RefreshStats();

            if (isSwap)
            {
                ConsumableChannel channel = _rig != null ? _rig.GetComponent<ConsumableChannel>() : null;
                if (channel != null) channel.Cancel("swapped weapons");
            }

            if (!isSwap || _quietSetChange > 0) return;

            Inventory revisionOwner = BagPartner();
            if (revisionOwner != null && revisionOwner.CanCommitRevision(out _)) revisionOwner.CommitRevision();
            GameEvents.RaiseInventoryChanged(OwnerId);
        }

        private bool HasWornHandItem()
        {
            for (int set = 0; set < EquipmentSlots.WeaponSetCount; set++)
            {
                int main = SlotIndex(EquipmentSlots.MainHandOf(set));
                int off = SlotIndex(EquipmentSlots.OffHandOf(set));
                if ((main >= 0 && _gear[main] != null) || (off >= 0 && _gear[off] != null)) return true;
            }
            return false;
        }

        /// <summary>
        /// D3c: the weapon-set swap, as one step. Every check comes before the first write: the player lives, a loadout exists, the
        /// other set has a weapon to hold, the player is not mid-attack (the same <see cref="ClassLoadout.IsActionLocked"/> rule a
        /// player swap always had: a swing, a draw or a reload), and the shared revision has room. A refusal changes nothing - not
        /// the live set, the hand, the off hand, the stats, the revision. The change itself cannot fail after those checks: the
        /// loadout takes the hand, the off hand follows, the stat source is rebuilt from the other set (the previous set's
        /// contribution leaves in the same pass), one revision is committed, observers are told once.
        /// </summary>
        public bool TrySwapWeaponSet(out string reason)
        {
            reason = null;

            if (_rig == null)
            {
                reason = "no player to swap weapons for";
                return false;
            }

            if (_rig.IsDead)
            {
                reason = "dead players cannot swap weapons";
                return false;
            }

            ClassLoadout loadout = _rig.Loadout;
            if (loadout == null)
            {
                reason = "no weapon loadout";
                return false;
            }

            int target = 1 - _activeSet;
            if (loadout.WeaponInSlot(target) == null)
            {
                reason = $"weapon set {target + 1} has no weapon";
                return false;
            }

            if (loadout.IsActionLocked)
            {
                reason = "cannot swap weapons during an attack";
                GameEvents.RaiseToast(OwnerId, loadout.ActionLockMessage);
                return false;
            }

            Inventory revisionOwner = BagPartner();
            if (revisionOwner == null)
            {
                reason = "equipment has no shared inventory revision owner";
                return false;
            }

            if (!revisionOwner.CanCommitRevision(out reason)) return false;

            if (!loadout.ForceSwitchToSlot(target))
            {
                reason = "the weapon loadout did not take the other set";
                return false;
            }

            return true;
        }

        /// <summary>
        /// D3c restore, validation half: would set <paramref name="set"/> have a weapon in its main hand after this container is
        /// restored (a worn item in that hand, or the class asset's weapon)? Always true for set 1 and for a pair with no rig. Touches nothing.
        /// </summary>
        internal bool WouldHoldWeapon(ValidatedContainer validated, int set)
        {
            // Set 1 (the original hands) is never refused here - an empty hand has always meant "the class weapon" - and a bare
            // bag/equipment pair with no rig to drive has no hand to check.
            if (set == 0 || _rig == null || _rig.Loadout == null) return true;

            EquipmentSlot hand = EquipmentSlots.MainHandOf(set);
            if (validated != null)
            {
                for (int i = 0; i < validated.Entries.Count; i++)
                {
                    if (validated.Entries[i].Slot == hand && validated.Entries[i].Definition.grantedWeapon != null) return true;
                }
            }

            return ResolveWeaponOfSet(set, null) != null;
        }

        /// <summary>
        /// D3c restore, apply half: makes <paramref name="set"/> the live set as part of a loadout restore, which commits its own single
        /// revision and its own notification. Cannot fail after <see cref="WouldHoldWeapon"/> passed; with no loadout to drive (a bare
        /// bag and equipment pair) only the recorded set changes.
        /// </summary>
        internal void ApplyRestoredWeaponSet(int set)
        {
            if (set < 0 || set >= EquipmentSlots.WeaponSetCount) return;

            ClassLoadout loadout = _rig != null ? _rig.Loadout : null;
            _quietSetChange++;
            try
            {
                if (loadout != null && loadout.ActiveSlot >= 0 && loadout.ActiveSlot != set)
                {
                    loadout.ForceSwitchToSlot(set);
                }
                else
                {
                    bool changed = BeginWeaponSet(set, false);
                    CompleteWeaponSet(changed, false);
                }
            }
            finally { _quietSetChange--; }
        }

        public bool TryUnequip(EquipmentSlot slot, out string reason)
        {
            reason = null;

            int index = SlotIndex(slot);
            if (index < 0)
            {
                reason = "no such equipment slot";
                return false;
            }

            return TryUnequipSlot(index, out reason);
        }

        /// <summary>Takes every worn item off the player. In item mode (U5A) a clear is a reset to the starting kit: the class weapons and shield are minted again. <see cref="ClearToBare"/> empties the hands too.</summary>
        public void Clear()
        {
            using (GameEventTransaction.Defer()) ClearState(true);
        }

        /// <summary>U5A: removes everything worn, the hand items included: the main hand is the bare fists, the off hand and the second set are empty. Nothing is returned to the bag (this is a reset, not an unequip).</summary>
        public void ClearToBare()
        {
            using (GameEventTransaction.Defer()) ClearState(false);
        }

        private void ClearState(bool reseedKit)
        {
            bool changed = false;
            for (int i = 0; i < SlotCount; i++) changed |= _gear[i] != null;
            Inventory revisionOwner = BagPartner();
            if (changed && revisionOwner == null)
            {
                Debug.LogWarning("Equipment cannot clear a worn loadout without its shared inventory revision owner.", this);
                return;
            }
            if (changed && !revisionOwner.CanCommitRevision(out string revisionReason))
            {
                Debug.LogWarning(revisionReason, this);
                return;
            }

            for (int i = 0; i < SlotCount; i++)
            {
                if (_gear[i] != null) RestoreClassHand((EquipmentSlot)i);
                _gear[i] = null;
            }

            RecomputeSlots();
            RefreshStats();
            if (changed) revisionOwner.CommitRevision();
            if (reseedKit && _kitIsItems && _rig != null && _rig.Loadout != null) _rig.Loadout.ReseedKit(); // U5A: a clear is a reset to the starting kit
        }

        /// <summary>
        /// Recomputes the equipment contribution to the stat stack and tells the rig to re-apply the numbers
        /// that depend on it. Called after every equip and unequip, so there is no half-applied state.
        ///
        /// The scratch starts empty and is filled once per worn slot: first that slot's definition's own
        /// authored StatModifiers, then that very instance's rolled numeric affix records mapped onto the same
        /// vocabulary (U2B6). Both halves are Flat additions on top of what the class and the attribute
        /// derivation already produced - a rolled value never replaces an authored one - and because the source
        /// is replaced wholesale on every pass, unequipping removes exactly what was added and a repeated refresh
        /// adds nothing a second time. The rig then rebuilds the six scores from the same worn instances (U2B5),
        /// which is precisely why the six ability keys are not also added here. The armor key belongs to this
        /// vocabulary now (U2B8): a definition's authored SkillStat.Armor modifiers and that instance's rolled
        /// armor points both enter this same replaced source, and their flat total is the one armor rating the
        /// rig exposes to the combat authority - so equipping, unequipping, a different-uid swap and a repeated
        /// refresh all change the mitigation the very next accepted hit sees, and never twice.
        /// </summary>
        public void RefreshStats()
        {
            _modifierScratch.Clear();
            _summaryScratch.Clear();
            float penaltyPoints = 0f;

            for (int i = 0; i < SlotCount; i++)
            {
                ItemInstance worn = _gear[i];
                if (worn == null || !IsSlotLive(i)) continue;

                ItemData item = worn.Definition;

                // D3c: a worn piece's authored penalty joins the held-weapon penalty in CombatFlowRules.HeldFactor. A weapon item's
                // own number is its WeaponData's, which is already counted for the weapon in hand, so it is not added here.
                if (!item.GrantsWeapon && item.moveSpeedPenalty > 0f && !float.IsNaN(item.moveSpeedPenalty))
                    penaltyPoints += item.moveSpeedPenalty;

                if (item.statModifiers != null)
                {
                    for (int m = 0; m < item.statModifiers.Length; m++)
                    {
                        _modifierScratch.Add(item.statModifiers[m]);
                    }
                }

                AddRolledNumericAffixes(worn);

                _summaryScratch.Add($"{item.displayName} ({item.ShortSummary})");
            }

            MoveSpeedPenaltyPoints = penaltyPoints;

            CharacterStatStack stats = _rig != null ? _rig.Stats : null;
            if (stats != null)
            {
                stats.SetSource(CharacterStatStack.EquipmentSourceKey, _modifierScratch, _summaryScratch);
            }

            if (_rig != null) _rig.RefreshEquipmentStats();
        }

        public string Describe()
        {
            if (FilledCount == 0) return "nothing equipped";

            System.Text.StringBuilder builder = new System.Text.StringBuilder();
            for (int i = 0; i < SlotCount; i++)
            {
                if (_gear[i] == null) continue;
                if (builder.Length > 0) builder.Append(", ");
                builder.Append(((EquipmentSlot)i).ToString()).Append('=').Append(_gear[i].Definition.displayName);
            }
            return builder.ToString();
        }

        // ------------------------------------------------------------ container snapshot (R9)

        /// <summary>The container id the equipment writes into its snapshot and expects when restoring.</summary>
        public const string SnapshotContainerId = "equipment";

        internal Inventory BagPartner()
        {
            if (_inventory != null) return _inventory;
            if (_rig != null && _rig.Inventory != null) return _rig.Inventory;
            return GetComponent<Inventory>();
        }

        /// <summary>
        /// What is worn as a versioned, canonical, checksummed <see cref="ContainerSnapshot"/> (kind equipment, no
        /// grid): one entry per filled slot, in slot order, carrying the worn instance's uid, rarity and exact rolled
        /// values. Read-only.
        /// </summary>
        public ContainerSnapshot ToContainerSnapshot()
        {
            List<ContainerEntry> entries = new List<ContainerEntry>();
            for (int i = 1; i < SlotCount; i++)
            {
                ItemInstance worn = _gear[i];
                if (worn == null) continue;
                entries.Add(new ContainerEntry { slot = ((EquipmentSlot)i).ToString(), x = 0, y = 0, item = worn.ToSnapshot() });
            }

            ContainerSnapshot snapshot = new ContainerSnapshot
            {
                schemaVersion = ContainerSnapshot.CurrentSchemaVersion,
                kind = ContainerKinds.Equipment,
                containerId = SnapshotContainerId,
                width = 0,
                height = 0,
                entries = entries.ToArray()
            };

            return snapshot.Seal();
        }

        /// <summary>
        /// Replaces everything worn with a saved equipment container, exactly: the worn instances keep their saved uid,
        /// rarity and rolled values (nothing is minted or rerolled) and the stats are re-applied through the existing
        /// <see cref="RefreshStats"/> path, which replaces the equipment stat source wholesale and only moves pool
        /// CEILINGS - so there is no free healing and no stat accumulation, however many times this runs. Weapons hand
        /// the class hand back first and then take the saved weapon, like equipping. All-or-nothing: a snapshot that
        /// fails validation (see <see cref="ContainerSnapshotValidator"/>), names an item for the wrong slot, a weapon
        /// without a granted weapon, or a uid the bag already carries is refused and nothing changes. The catalog is
        /// the bag's catalog; <paramref name="others"/> adds uids held by other containers of the profile.
        /// </summary>
        public bool TryRestore(ContainerSnapshot snapshot, out string reason)
        {
            return TryRestore(snapshot, null, out reason);
        }

        public bool TryRestore(ContainerSnapshot snapshot, ItemUidRegistry others, out string reason)
        {
            using (GameEventTransaction.Defer()) return TryRestoreState(snapshot, others, out reason);
        }

        private bool TryRestoreState(ContainerSnapshot snapshot, ItemUidRegistry others, out string reason)
        {
            if (!TryPrepareRestore(snapshot, others, true, out ValidatedContainer validated, out reason)) return false;

            Inventory revisionOwner = BagPartner();
            if (revisionOwner == null || !revisionOwner.CanCommitRevision(out reason))
            {
                if (revisionOwner == null) reason = "equipment has no shared inventory revision owner";
                return false;
            }

            // The ledger passed in is the profile's: it is updated together with the restore (it was already checked).
            if (others != null && !others.TryReplaceOwner(validated.ContainerId, validated.Uids, out reason)) return false;

            List<ItemData> removed = ApplyRestore(validated);
            revisionOwner.CommitRevision();
            NotifyRestore(removed, validated);
            return true;
        }

        /// <summary>Validation half of a restore: touches nothing.</summary>
        internal bool TryPrepareRestore(ContainerSnapshot snapshot, ItemUidRegistry others, bool checkPartner,
            out ValidatedContainer validated, out string reason)
        {
            validated = null;

            Inventory bag = BagPartner();
            ItemCatalog catalog = bag != null ? bag.Catalog : null;
            if (catalog == null)
            {
                reason = "equipment has no item catalog (its bag has none), so a container snapshot cannot be validated";
                return false;
            }

            if (!ContainerSnapshotValidator.TryValidate(snapshot, catalog, others, ContainerKinds.Equipment, out validated, out reason))
            {
                validated = null;
                return false;
            }

            if (validated.ContainerId != SnapshotContainerId)
            {
                reason = $"equipment restores container '{SnapshotContainerId}', not '{snapshot.containerId}'";
                validated = null;
                return false;
            }

            for (int i = 0; i < validated.Entries.Count; i++)
            {
                ItemData definition = validated.Entries[i].Definition;
                if (definition.GrantsWeapon && definition.grantedWeapon == null)
                {
                    reason = $"{definition.displayName} declares no weapon to hold";
                    validated = null;
                    return false;
                }
            }

            if (!TryValidateHandRules(validated, out reason))
            {
                validated = null;
                return false;
            }

            if (checkPartner && bag != null)
            {
                for (int i = 0; i < validated.Uids.Count; i++)
                {
                    if (bag.HoldsInstanceUid(validated.Uids[i]))
                    {
                        reason = $"uid {validated.Uids[i]:N} is already carried in this player's bag: one uid is one item";
                        validated = null;
                        return false;
                    }
                }
            }

            reason = null;
            return true;
        }

        /// <summary>
        /// Apply half of a restore. Cannot fail (every entry was resolved and checked by <see cref="TryPrepareRestore"/>)
        /// and raises none of this component's events: the slots are replaced first, then the hands, then the stats, so
        /// whatever listens to a hand or pool change already sees the final slots. Returns the definitions that were
        /// worn before, for <see cref="NotifyRestore"/>.
        /// </summary>
        internal List<ItemData> ApplyRestore(ValidatedContainer validated)
        {
            List<ItemData> removed = new List<ItemData>();
            List<EquipmentSlot> handSlots = new List<EquipmentSlot>();

            for (int i = 1; i < SlotCount; i++)
            {
                ItemInstance previous = _gear[i];
                if (previous != null)
                {
                    removed.Add(previous.Definition);
                    if (previous.Definition.GrantsWeapon && EquipmentSlots.SetOf((EquipmentSlot)i) >= 0) handSlots.Add((EquipmentSlot)i);
                }
                _gear[i] = null;
            }

            for (int i = 0; i < validated.Entries.Count; i++)
            {
                ValidatedEntry entry = validated.Entries[i];
                _gear[SlotIndex(entry.Slot)] = entry.Instance;
            }

            RecomputeSlots();

            // Hand every previously worn weapon back to the class first, so a slot that is no longer worn does not
            // keep a saved weapon in the hand; then the restored weapons take their hands like equipping does.
            for (int i = 0; i < handSlots.Count; i++) RestoreClassHand(handSlots[i]);
            for (int i = 0; i < validated.Entries.Count; i++) ApplyHand(validated.Entries[i].Definition, validated.Entries[i].Slot);

            // Same refresh every equip/unequip uses: the equipment stat source is replaced wholesale and only the
            // ceilings are re-applied (SetMax never pays out), so nothing heals and nothing accumulates.
            RefreshStats();
            return removed;
        }

        /// <summary>Notify half of a restore: removed items first, then restored ones; a throwing subscriber is logged and changes nothing.</summary>
        internal void NotifyRestore(List<ItemData> removed, ValidatedContainer validated)
        {
            Guard(() => GameEvents.RaiseInventoryChanged(OwnerId));
            for (int i = 0; i < removed.Count; i++)
            {
                ItemData item = removed[i];
                Guard(() => GameEvents.RaiseEquipmentChanged(OwnerId, item, false));
            }
            for (int i = 0; i < validated.Entries.Count; i++)
            {
                ItemData item = validated.Entries[i].Definition;
                Guard(() => GameEvents.RaiseEquipmentChanged(OwnerId, item, true));
            }
        }

        private static void Guard(Action raise)
        {
            try { raise(); }
            catch (Exception e) { Debug.LogException(e); }
        }

        // ------------------------------------------------------------ internals

        private void ApplyToSlot(int index, ItemInstance instance)
        {
            _gear[index] = instance;
            RecomputeSlots();
            ApplyHand(instance.Definition, (EquipmentSlot)index);
        }

        /// <summary>
        /// The hand half of wearing an item: a weapon or shield definition takes the hand of the slot it was put in (D3c: a
        /// set-2 slot fills set 2's hand, which is only the hand in use while set 2 is the active set), anything else does nothing.
        /// </summary>
        private void ApplyHand(ItemData item, EquipmentSlot slot)
        {
            if (!item.GrantsWeapon) return;

            int set = EquipmentSlots.SetOf(slot);
            if (set < 0) return;

            if (EquipmentSlots.IsOffHand(slot))
            {
                if (_rig != null && set == _activeSet) _rig.SetOffhandWeapon(item.grantedWeapon);
                return;
            }

            ClassLoadout loadout = _rig != null ? _rig.Loadout : null;
            if (loadout != null) loadout.TryReplaceWeaponSlot(set, item.grantedWeapon);
        }

        /// <summary>
        /// Takes the item out of a slot and back into the bag, refusing before it mutates when the bag has no
        /// hole for it or there is no bag at all. A transfer with nowhere to put the item is not a removal:
        /// deleting worn gear is not how this component loses an item. The instance the slot held is the
        /// instance the bag receives, so the identity and its rolls survive the trip and nothing new is minted.
        /// A weapon item hands the hand back to whatever the class asset carries in it, so unequipping never
        /// leaves the player empty handed and never destroys a weapon.
        /// </summary>
        private bool TryUnequipSlot(int index, out string reason)
        {
            using (GameEventTransaction.Defer()) return TryUnequipSlotState(index, out reason);
        }

        private bool TryUnequipSlotState(int index, out string reason)
        {
            reason = null;

            if (index < 0 || index >= SlotCount)
            {
                reason = "no such equipment slot";
                return false;
            }

            ItemInstance worn = _gear[index];
            if (worn == null || worn.Definition == null)
            {
                reason = "nothing equipped there";
                return false;
            }

            ItemData item = worn.Definition;

            if (_rig == null)
            {
                reason = "no player is wearing it";
                return false;
            }

            if (_rig.IsDead)
            {
                reason = "dead players cannot change gear";
                return false;
            }

            // D1b: the main-hand weapon cannot change while its wielder is mid-attack (same lock as the swap input).
            if (item.GrantsWeapon && item.equipSlot != EquipmentSlot.OffHand && _rig.Loadout != null && _rig.Loadout.IsActionLocked)
            {
                reason = "cannot change weapons during an attack";
                GameEvents.RaiseToast(OwnerId, _rig.Loadout.ActionLockMessage);
                return false;
            }

            if (_inventory == null)
            {
                reason = "no bag to put it back in";
                return false;
            }

            if (!_inventory.CanCommitRevision(out reason)) return false;

            // Silent transfer: the bag now holds the item, but this slot still says it is worn and the stat
            // source still carries its contribution. Finish that before anyone is allowed to look.
            if (!_inventory.TryUnequipTransfer(worn, out reason)) return false;

            // The slot releases the identity in the same commit that put it in the bag, so there is no moment
            // in which this item is both worn and carried, and none in which it is neither.
            _gear[index] = null;
            RecomputeSlots();

            if (item.GrantsWeapon) RestoreClassHand((EquipmentSlot)index);

            RefreshStats();
            _inventory.CommitRevision();

            // One notification for one committed transfer, raised after the slot, the hand and the numbers
            // all agree with the bag.
            GameEvents.RaiseInventoryChanged(OwnerId);
            GameEvents.RaiseEquipmentChanged(OwnerId, item, false);
            return true;
        }

        private void RestoreClassHand(EquipmentSlot slot)
        {
            if (_kitIsItems)
            {
                RestoreEmptyHand(slot);
                return;
            }

            CharacterClassData data = _rig != null ? _rig.Class : null;
            if (data == null) return;

            int set = EquipmentSlots.SetOf(slot);
            if (set < 0) return;

            if (EquipmentSlots.IsOffHand(slot))
            {
                if (set == _activeSet) _rig.SetOffhandWeapon(data.offhandShield);
                return;
            }

            ClassLoadout loadout = _rig != null ? _rig.Loadout : null;
            if (loadout == null) return;

            WeaponData classWeapon = set == 0 ? data.primaryWeapon : data.secondaryWeapon;
            if (classWeapon != null)
            {
                loadout.TryReplaceWeaponSlot(set, classWeapon);
            }
            else if (set == 1)
            {
                // A class with no secondary weapon: set 2's hand is empty again. If it was the live set, the player falls back to set 1.
                _quietSetChange++;
                try { loadout.ClearSecondaryWeapon(); }
                finally { _quietSetChange--; }
            }
        }

        /// <summary>
        /// U5A: after a weapon or shield has been picked up into the bag, seats it in the matching EMPTY hand of the live weapon set (a shield in the
        /// off hand, a one-handed or two-handed weapon in the main hand). Never displaces anything: an occupied hand, a refused hand rule or an
        /// attack in progress leaves the item in the bag. Returns whether the item was seated.
        /// </summary>
        public bool TryAutoSeatHand(ItemInstance instance)
        {
            if (!_kitIsItems || instance == null || instance.Definition == null || _inventory == null) return false;
            WeaponData weapon = instance.Definition.grantedWeapon;
            if (weapon == null) return false;

            EquipmentSlot target = weapon.Hands == HandUse.OffHandOnly
                ? EquipmentSlots.OffHandOf(_activeSet)
                : EquipmentSlots.MainHandOf(_activeSet);
            int index = SlotIndex(target);
            if (index < 1 || _gear[index] != null) return false;
            if (!TryCheckHandRules(instance.Definition, target, out int displace, out _) || displace >= 0) return false;

            int bagIndex = _inventory.IndexOfInstance(instance.Uid);
            return bagIndex >= 0 && TryEquipFromInventory(bagIndex, target, out _);
        }

        /// <summary>U5A: an emptied hand in item mode: the main hand becomes the bare fists (set 1) or nothing (set 2), the off hand nothing.</summary>
        private void RestoreEmptyHand(EquipmentSlot slot)
        {
            int set = EquipmentSlots.SetOf(slot);
            if (set < 0 || _rig == null) return;

            if (EquipmentSlots.IsOffHand(slot))
            {
                if (set == _activeSet) _rig.SetOffhandWeapon(null);
                return;
            }

            ClassLoadout loadout = _rig.Loadout;
            if (loadout == null) return;

            if (set == 0)
            {
                WeaponData fists = Fists;
                if (fists != null) loadout.TryReplaceWeaponSlot(0, fists);
            }
            else
            {
                _quietSetChange++;
                try { loadout.ClearSecondaryWeapon(); }
                finally { _quietSetChange--; }
            }
        }

        /// <summary>U5A: the hand rules applied to a whole saved container (a forged snapshot cannot seat a shield in a main hand, a weapon in an off hand, or a shield next to a two-handed weapon).</summary>
        private bool TryValidateHandRules(ValidatedContainer validated, out string reason)
        {
            reason = null;
            for (int set = 0; set < EquipmentSlots.WeaponSetCount; set++)
            {
                WeaponData main = null;
                WeaponData off = null;
                for (int i = 0; i < validated.Entries.Count; i++)
                {
                    ValidatedEntry entry = validated.Entries[i];
                    if (entry.Slot == EquipmentSlots.MainHandOf(set)) main = entry.Definition.grantedWeapon;
                    else if (entry.Slot == EquipmentSlots.OffHandOf(set)) off = entry.Definition.grantedWeapon;
                }

                if (main != null && (main.isUnarmed || main.Hands == HandUse.OffHandOnly))
                {
                    reason = $"{main.displayName} cannot be held in a main hand";
                    return false;
                }

                if (off != null && off.Hands != HandUse.OffHandOnly)
                {
                    reason = $"{off.displayName} cannot be held in an off hand";
                    return false;
                }

                if (main != null && off != null && main.Hands == HandUse.TwoHanded)
                {
                    reason = $"the two-handed {main.displayName} leaves no hand for {off.displayName}";
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// U5A: takes a worn item out of its slot and out of the player's model entirely (to be laid on the ground by the authority). The slot
        /// releases the identity in the same commit the revision moves, the hand falls back to the fists / nothing, the stats are rebuilt.
        /// The expected uid must be what is worn there. A refusal changes nothing. <see cref="ReattachSlot"/> undoes it when the ground refuses.
        /// </summary>
        internal bool TryDetachSlot(EquipmentSlot slot, Guid expectedUid, out ItemInstance detached, out string reason)
        {
            using (GameEventTransaction.Defer()) return TryDetachSlotState(slot, expectedUid, out detached, out reason);
        }

        private bool TryDetachSlotState(EquipmentSlot slot, Guid expectedUid, out ItemInstance detached, out string reason)
        {
            detached = null;
            int index = SlotIndex(slot);
            if (index < 1)
            {
                reason = "no such equipment slot";
                return false;
            }

            ItemInstance worn = _gear[index];
            if (worn == null || worn.Definition == null)
            {
                reason = "nothing equipped there";
                return false;
            }

            if (worn.Uid != expectedUid)
            {
                reason = "the requested gear identity is no longer worn in that slot";
                return false;
            }

            if (_rig == null || _rig.IsDead)
            {
                reason = "dead players cannot change gear";
                return false;
            }

            ItemData item = worn.Definition;
            if (item.GrantsWeapon && !EquipmentSlots.IsOffHand(slot) && _rig.Loadout != null && _rig.Loadout.IsActionLocked)
            {
                reason = "cannot change weapons during an attack";
                GameEvents.RaiseToast(OwnerId, _rig.Loadout.ActionLockMessage);
                return false;
            }

            Inventory revisionOwner = BagPartner();
            if (revisionOwner == null)
            {
                reason = "equipment has no shared inventory revision owner";
                return false;
            }

            if (!revisionOwner.CanCommitRevision(out reason)) return false;

            _gear[index] = null;
            RecomputeSlots();
            if (item.GrantsWeapon) RestoreClassHand(slot);
            RefreshStats();
            revisionOwner.CommitRevision();

            GameEvents.RaiseInventoryChanged(OwnerId);
            GameEvents.RaiseEquipmentChanged(OwnerId, item, false);
            detached = worn;
            reason = null;
            return true;
        }

        /// <summary>U5A: puts a detached item back in its slot (the ground could not take it). Same stats / hand / revision path as an equip.</summary>
        internal void ReattachSlot(EquipmentSlot slot, ItemInstance instance)
        {
            using (GameEventTransaction.Defer())
            {
                int index = SlotIndex(slot);
                if (index < 1 || instance == null || _gear[index] != null) return;
                ApplyToSlot(index, instance);
                RefreshStats();
                Inventory revisionOwner = BagPartner();
                if (revisionOwner != null && revisionOwner.CanCommitRevision(out _)) revisionOwner.CommitRevision();
                GameEvents.RaiseInventoryChanged(OwnerId);
                GameEvents.RaiseEquipmentChanged(OwnerId, instance.Definition, true);
            }
        }

        private void RecomputeSlots()
        {
            FilledCount = 0;
            for (int i = 0; i < SlotCount; i++)
            {
                ItemInstance instance = _gear[i];
                _slotViews[i] = instance != null ? instance.Definition : null;
                if (instance != null) FilledCount++;
            }
        }

        /// <summary>
        /// U2B6: adds this worn instance's own rolled numeric affix records to the equipment source, one Flat
        /// modifier per record, in the instance's own record order, next to the definition's authored modifiers.
        /// max_health and max_mana are pool amounts (the rig applies them after the CON/INT-derived bases and
        /// keeps its existing integer rounding/clamping, so a bigger ceiling never pays out current pool);
        /// move_speed is metres per second added after the DEX factor and the passive/equipment multipliers -
        /// PlayerMovement reads multiplier and flat separately for exactly that reason, and sprint/crouch read
        /// their own SkillStat terms, so a walk-only affix cannot touch them; mana_regen is an amount per second
        /// applied after the WIS-derived regen, with the run's existing delay untouched.
        ///
        /// The six ability keys return false here because U2B5 already applies them through the attribute stack.
        /// melee_damage and projectile_damage return false because U2B7 already applies them through the
        /// authority-read flat weapon channel, and counting them in this source as well would apply them twice.
        /// armor maps (U2B8) onto SkillStat.Armor as a Flat amount, in the same replaced source as the
        /// definition's authored modifiers, because the armor rating IS this stack's flat total: authored points
        /// and rolled points summed once per worn slot and read back once by the rig's armor provider. It is
        /// deliberately NOT mapped onto BlockReduction - armor changes no guard, arc, parry or shield behaviour
        /// and never marks a hit as blocked. A non-finite value - which a validated instance cannot hold, but a
        /// tampered one could - is skipped instead of poisoning every stat the stack resolves.
        /// </summary>
        private void AddRolledNumericAffixes(ItemInstance worn)
        {
            IReadOnlyList<AffixRecord> records = worn.Affixes;
            if (records == null || records.Count == 0) return;

            for (int r = 0; r < records.Count; r++)
            {
                AffixRecord record = records[r];

                if (!AffixStatKeys.TryGetRoute(record.StatKey, out AffixStatRoute route)
                    || route.Kind != AffixStatRouteKind.SkillStatFlat) continue;

                float value = record.Value;
                if (float.IsNaN(value) || float.IsInfinity(value)) continue;

                _modifierScratch.Add(new StatModifier(route.SkillStat, SkillModifierMode.Flat, value));
            }
        }

        private static int SlotIndex(EquipmentSlot slot)
        {
            int index = (int)slot;
            return index > 0 && index < SlotCount ? index : -1;
        }

        /// <summary>
        /// The only door to the authoritative slot array, and it has no writable shape - the same pattern as
        /// <see cref="ItemInstance"/>'s affix view: it implements IReadOnlyList and nothing else, so casting it
        /// to IList, to ICollection or to an array throws instead of exposing the storage, and an out-of-range
        /// index is an exception rather than a silent null. It reads the live array, so it can never disagree
        /// with the slot a transfer just committed.
        /// </summary>
        private sealed class WornInstanceListView : IReadOnlyList<ItemInstance>
        {
            private readonly Equipment _owner;
            private readonly ItemInstance[] _slots;

            public WornInstanceListView(Equipment owner, ItemInstance[] slots)
            {
                _owner = owner;
                _slots = slots;
            }

            public ItemInstance this[int index]
            {
                get
                {
                    if (index < 0 || index >= _slots.Length)
                    {
                        throw new IndexOutOfRangeException($"equipment has {_slots.Length} slot(s), " +
                                                           $"index {index} is not one of them");
                    }
                    // D3c: the weapon set that is not in hand is worn but not live; it reads as empty here, so every stat consumer of
                    // this window (attributes, armor, affixes, the effective-stats capture) sees the active set only.
                    return _owner.IsSlotLive(index) ? _slots[index] : null;
                }
            }

            public int Count => _slots.Length;

            public IEnumerator<ItemInstance> GetEnumerator()
            {
                for (int i = 0; i < _slots.Length; i++) yield return _owner.IsSlotLive(i) ? _slots[i] : null;
            }

            IEnumerator IEnumerable.GetEnumerator()
            {
                return GetEnumerator();
            }
        }
    }
}
