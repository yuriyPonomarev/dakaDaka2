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
                if (_wornInstanceView == null) _wornInstanceView = new WornInstanceListView(_gear);
                return _wornInstanceView;
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

            return TryEquipSelected(inventoryIndex, incoming, out reason);
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

            return TryEquipSelected(bagIndex, incoming, out reason);
        }

        /// <summary>
        /// The one equip path. Everything that can be decided without touching state is decided here, first;
        /// the bag transfer is the last step and the last chance to refuse, so a refusal never leaves a slot
        /// half filled or a bag half emptied. The instance resolved from the selected bag slot is the instance
        /// that ends up worn: no copy, no re-roll and no second identity is minted on the way in.
        /// </summary>
        private bool TryEquipSelected(int bagIndex, ItemInstance incoming, out string reason)
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

            int index = SlotIndex(item.equipSlot);
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

            // One identity, one place: the same item cannot be worn in another slot while it is also being
            // picked up out of the bag.
            if (HoldsUid(incoming.Uid))
            {
                reason = $"{item.displayName} [{incoming.UidText}] is already worn in another slot: " +
                         "one uid is one item";
                return false;
            }

            ItemInstance previous = _gear[index];

            // The bag half first. Inventory plans the hole for the outgoing item using the space the incoming
            // unit is about to vacate, and commits both moves together. A refusal here has changed nothing, and
            // a success has told nobody yet: the transfer is silent so this component can finish its own half.
            if (!_inventory.TryEquipTransfer(bagIndex, incoming, previous, out reason)) return false;

            ApplyToSlot(index, incoming);
            RefreshStats();

            // Only now is the whole change real: bag, slot, hand and the refreshed stat source. The one
            // inventory notification an equipment transfer owes its observers is raised here, never from
            // inside the bag helper, so a subscriber can never read a half-committed transfer.
            GameEvents.RaiseInventoryChanged();
            GameEvents.RaiseEquipmentChanged(item, true);
            GameEvents.RaiseToast($"Equipped {item.displayName}");
            return true;
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

        public void Clear()
        {
            for (int i = 0; i < SlotCount; i++)
            {
                if (_gear[i] != null) RestoreClassHand((EquipmentSlot)i);
                _gear[i] = null;
            }

            RecomputeSlots();
            RefreshStats();
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

            for (int i = 0; i < SlotCount; i++)
            {
                ItemInstance worn = _gear[i];
                if (worn == null) continue;

                ItemData item = worn.Definition;

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
            if (!TryPrepareRestore(snapshot, others, true, out ValidatedContainer validated, out reason)) return false;

            // The ledger passed in is the profile's: it is updated together with the restore (it was already checked).
            if (others != null && !others.TryReplaceOwner(validated.ContainerId, validated.Uids, out reason)) return false;

            List<ItemData> removed = ApplyRestore(validated);
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
                    if (previous.Definition.GrantsWeapon) handSlots.Add((EquipmentSlot)i);
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
            for (int i = 0; i < validated.Entries.Count; i++) ApplyHand(validated.Entries[i].Definition);

            // Same refresh every equip/unequip uses: the equipment stat source is replaced wholesale and only the
            // ceilings are re-applied (SetMax never pays out), so nothing heals and nothing accumulates.
            RefreshStats();
            return removed;
        }

        /// <summary>Notify half of a restore: removed items first, then restored ones; a throwing subscriber is logged and changes nothing.</summary>
        internal void NotifyRestore(List<ItemData> removed, ValidatedContainer validated)
        {
            Guard(() => GameEvents.RaiseInventoryChanged());
            for (int i = 0; i < removed.Count; i++)
            {
                ItemData item = removed[i];
                Guard(() => GameEvents.RaiseEquipmentChanged(item, false));
            }
            for (int i = 0; i < validated.Entries.Count; i++)
            {
                ItemData item = validated.Entries[i].Definition;
                Guard(() => GameEvents.RaiseEquipmentChanged(item, true));
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
            ApplyHand(instance.Definition);
        }

        /// <summary>The hand half of wearing an item: a weapon or shield definition takes its hand, anything else does nothing.</summary>
        private void ApplyHand(ItemData item)
        {
            if (!item.GrantsWeapon) return;

            if (item.equipSlot == EquipmentSlot.OffHand)
            {
                if (_rig != null) _rig.SetOffhandWeapon(item.grantedWeapon);
                return;
            }

            ClassLoadout loadout = _rig != null ? _rig.Loadout : null;
            if (loadout != null) loadout.TryReplaceWeaponSlot(0, item.grantedWeapon);
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

            if (_inventory == null)
            {
                reason = "no bag to put it back in";
                return false;
            }

            // Silent transfer: the bag now holds the item, but this slot still says it is worn and the stat
            // source still carries its contribution. Finish that before anyone is allowed to look.
            if (!_inventory.TryUnequipTransfer(worn, out reason)) return false;

            // The slot releases the identity in the same commit that put it in the bag, so there is no moment
            // in which this item is both worn and carried, and none in which it is neither.
            _gear[index] = null;
            RecomputeSlots();

            if (item.GrantsWeapon) RestoreClassHand((EquipmentSlot)index);

            RefreshStats();

            // One notification for one committed transfer, raised after the slot, the hand and the numbers
            // all agree with the bag.
            GameEvents.RaiseInventoryChanged();
            GameEvents.RaiseEquipmentChanged(item, false);
            return true;
        }

        private void RestoreClassHand(EquipmentSlot slot)
        {
            CharacterClassData data = _rig != null ? _rig.Class : null;
            if (data == null) return;

            if (slot == EquipmentSlot.OffHand)
            {
                if (_rig != null) _rig.SetOffhandWeapon(data.offhandShield);
                return;
            }

            ClassLoadout loadout = _rig != null ? _rig.Loadout : null;
            if (loadout != null && data.primaryWeapon != null) loadout.TryReplaceWeaponSlot(0, data.primaryWeapon);
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

                if (!TryNumericStatTarget(record.StatKey, out SkillStat stat)) continue;

                float value = record.Value;
                if (float.IsNaN(value) || float.IsInfinity(value)) continue;

                _modifierScratch.Add(new StatModifier(stat, SkillModifierMode.Flat, value));
            }
        }

        /// <summary>The one allowlisted numeric-key mapping this slice applies. False means "not applied by this
        /// slice", never "count it as zero": an unmapped key contributes nothing rather than something guessed.
        /// The attribute keys are excluded on purpose (U2B5 owns them) and so are melee_damage and
        /// projectile_damage (U2B7's authority-read flat weapon channel owns them). armor maps here (U2B8)
        /// because the armor rating is defined as this stack's flat total: authored and rolled armor points
        /// summed once, read back by the rig's armor provider and mitigated by DamageResolver on Physical damage
        /// only - never reinterpreted as block reduction.</summary>
        private static bool TryNumericStatTarget(AffixStatKey key, out SkillStat stat)
        {
            switch (key)
            {
                case AffixStatKey.MaxHealth: stat = SkillStat.MaxHealth; return true;
                case AffixStatKey.MaxMana: stat = SkillStat.MaxMana; return true;
                case AffixStatKey.MoveSpeed: stat = SkillStat.MoveSpeed; return true;
                case AffixStatKey.ManaRegen: stat = SkillStat.ManaRegen; return true;
                case AffixStatKey.Armor: stat = SkillStat.Armor; return true;
                default: stat = SkillStat.MoveSpeed; return false;
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
            private readonly ItemInstance[] _slots;

            public WornInstanceListView(ItemInstance[] slots)
            {
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
                    return _slots[index];
                }
            }

            public int Count => _slots.Length;

            public IEnumerator<ItemInstance> GetEnumerator()
            {
                for (int i = 0; i < _slots.Length; i++) yield return _slots[i];
            }

            IEnumerator IEnumerable.GetEnumerator()
            {
                return GetEnumerator();
            }
        }
    }
}
