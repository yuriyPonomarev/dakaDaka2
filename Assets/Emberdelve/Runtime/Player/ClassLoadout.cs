using System.Collections.Generic;
using UnityEngine;
using Emberdelve.Core;
using Emberdelve.Combat;
using Emberdelve.Data;
using Emberdelve.InputLayer;
using Emberdelve.Items;
using Emberdelve.Skills;

namespace Emberdelve.Player
{
    /// <summary>
    /// Applies a CharacterClassData to the player rig, owns the primary/secondary weapon slots, and
    /// builds the temporary weapon visuals. This is the only place that knows which components a class
    /// needs and which weapon is currently in hand, so adding a class or a secondary weapon is a new
    /// asset plus (at most) one new combat component.
    ///
    /// Switching only changes which component is live and which weapon the rig reports as its main hand.
    /// Damage still goes exclusively through the combat authority.
    /// </summary>
    public sealed class ClassLoadout : MonoBehaviour
    {
        private const int SlotCapacity = 2;

        [Header("Wiring")]
        [SerializeField] private Transform weaponAnchor;
        [SerializeField] private MeleeCombat meleeCombat;
        [SerializeField] private RangedCombat rangedCombat;
        [SerializeField] private Camera targetCamera;

        private WeaponVisualRig _visual;
        private FirstPersonArmsDriver _arms;
        private WeaponViewModelSet _viewModels;
        private bool _armsPending;
        private ItemData _shownUtility;
        private CharacterClassData _appliedClass;
        private PlayerRig _rig;

        private readonly WeaponData[] _slots = new WeaponData[SlotCapacity];
        private readonly List<RangedAmmoState> _ammo = new List<RangedAmmoState>(SlotCapacity);
        private int _activeSlot;

        public bool HasMuzzle => _visual != null && _visual.Muzzle != null;
        public Vector3 MuzzlePosition => _visual != null && _visual.Muzzle != null ? _visual.Muzzle.position : Vector3.zero;
        public CharacterClassData AppliedClass => _appliedClass;

        private WeaponData _unarmed;

        /// <summary>U5A: the bare-fist weapon the main hand falls back to (GameConfig.unarmedWeapon); null for a config that has none.</summary>
        public WeaponData Unarmed => _unarmed;

        /// <summary>U5A: the hands changed without the main hand changing (an off hand taken off or put on): dress the arms again from the settled state.</summary>
        public void RefreshHands()
        {
            if (_rig == null || _activeSlot < 0) return;
            SyncArms(ActiveWeapon);
        }

        public WeaponData Primary => _slots[0];
        public WeaponData Secondary => _slots[1];
        /// <summary>Goes up on every Equip (every change of the weapon in hand, also one undone within the same frame).</summary>
        public int WeaponChangeCount { get; private set; }

        public WeaponData ActiveWeapon => _activeSlot >= 0 && _activeSlot < SlotCapacity ? _slots[_activeSlot] : null;
        public int ActiveSlot => _activeSlot;
        public bool UsingSecondary => _activeSlot == 1;
        public int EquippedCount => (_slots[0] != null ? 1 : 0) + (_slots[1] != null ? 1 : 0);

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
            if (meleeCombat == null) meleeCombat = GetComponent<MeleeCombat>();
            if (rangedCombat == null) rangedCombat = GetComponent<RangedCombat>();
            if (targetCamera == null && GetComponentInParent<Camera>() != null) targetCamera = GetComponentInParent<Camera>();
        }

        internal void InitializeReplicatedClass(CharacterClassData data, GameConfig config, PlayerRig rig)
        {
            _appliedClass = data;
            _rig = rig;
            _unarmed = config.unarmedWeapon;
            _slots[0] = _unarmed;
            _slots[1] = null;
            _activeSlot = 0;
            // The first authoritative snapshot supplies every item. Never mint a local kit.
            rig.Equipment.BeginKit();
        }

        public void Apply(CharacterClassData data, GameConfig config, PlayerRig rig)
        {
            if (data == null || rig == null) return;
            if (rig.Inventory != null && !rig.Inventory.CanCommitRevision(out _)) return;
            _appliedClass = data;
            _rig = rig;

            // The skill kit goes first: its passives decide the numbers everything below configures, so a
            // passive that raises health, mana or movement is already counted when those pools are built.
            if (rig.Skills != null) rig.Skills.ApplyClass(data, config);
            CharacterStatStack stats = rig.Stats;

            rig.Movement.ApplyClass(data, config.levelMask);
            rig.Movement.ApplyStatStack(stats);
            rig.Look.ApplyClass(data);
            rig.Look.ConfigureSensitivity(config.mouseSensitivity);

            // The rig owns the vitals rule: attribute-derived pristine bases plus the passive/gear stack on
            // top, applied once, and filled once because applying a class is a run start and not a gear
            // change. Equipment later takes the clamp-only path instead of this one.
            rig.InitializeVitals();

            if (rig.Inventory != null)
            {
                // R3: the bag mints the starting kit against the run's one item catalog and nothing else. A
                // config without a catalog leaves a catalog the bag already has untouched (a fixture's explicit
                // one); a bag that ends up with none mints no gear and the kit's gear is refused with a reason.
                if (config.itemCatalog != null) rig.Inventory.SetCatalog(config.itemCatalog);
                else if (rig.Inventory.Catalog == null)
                    Debug.LogError("[Emberdelve][ClassLoadout] GameConfig has no itemCatalog: gear cannot be minted for the starting kit.", this);

                rig.Inventory.ConfigureGrid(config.inventoryGridWidth, config.inventoryGridHeight);
                rig.Inventory.GiveStartingItems(data.startingItems);
            }

            _slots[0] = data.primaryWeapon;
            _slots[1] = data.secondaryWeapon;
            _unarmed = config != null ? config.unarmedWeapon : null;
            SeedKitItems(data, config, rig);
            if (rig.Equipment != null && rig.Equipment.KitIsItems && _slots[0] == null) _slots[0] = _unarmed;

            _ammo.Clear();
            SeedAmmo(0, data.startingReserveAmmo, data.maxReserveAmmo);
            SeedAmmo(1, data.secondaryStartingReserveAmmo, data.secondaryMaxReserveAmmo);

            _activeSlot = -1;
            Equip(0);

            if (targetCamera != null) targetCamera.fieldOfView = config.fieldOfView;
        }

        /// <summary>
        /// U5A: the class kit becomes real items in the hand slots (the primary weapon in the main hand, the off-hand shield in the off hand
        /// unless the primary is two-handed, the secondary weapon in the second set's main hand), minted through the bag's own issuer so each
        /// has a uid like any other gear. Done only when every piece can be minted (a catalog entry exists for it) and the config names the bare
        /// fists; otherwise the rig keeps the old class-fallback hands and says why.
        /// </summary>
        private void SeedKitItems(CharacterClassData data, GameConfig config, PlayerRig rig)
        {
            Equipment gear = rig.Equipment;
            Inventory bag = rig.Inventory;
            if (gear == null || bag == null || bag.Catalog == null || _unarmed == null) return;

            bool offAllowed = data.primaryWeapon == null || data.primaryWeapon.Hands != HandUse.TwoHanded;
            ItemInstance main = null, off = null, alt = null;
            if (!MintKit(bag, data.primaryWeapon, out main)) return;
            if (offAllowed && !MintKit(bag, data.offhandShield, out off)) return;
            if (!MintKit(bag, data.secondaryWeapon, out alt)) return;

            gear.BeginKit();
            gear.SeedKitHand(EquipmentSlot.Weapon, main);
            gear.SeedKitHand(EquipmentSlot.OffHand, off);
            gear.SeedKitHand(EquipmentSlot.AltWeapon, alt);
            gear.EndKit();
        }

        /// <summary>
        /// U5A: a full clear of the worn gear puts the class kit back (fresh items, fresh uids): an Equipment.Clear is a reset to the starting
        /// loadout, never to bare hands. Taking a hand item off, or dropping it, is what empties a hand.
        /// </summary>
        internal void ReseedKit()
        {
            if (_rig == null || _appliedClass == null || _rig.Equipment == null || !_rig.Equipment.KitIsItems) return;
            _slots[0] = _appliedClass.primaryWeapon;
            _slots[1] = _appliedClass.secondaryWeapon;
            SeedKitItems(_appliedClass, null, _rig);
            if (_slots[0] == null) _slots[0] = _unarmed;
            int active = _activeSlot >= 0 && _slots[_activeSlot] != null ? _activeSlot : 0;
            Equip(active);
        }

        private static bool MintKit(Inventory bag, WeaponData weapon, out ItemInstance instance)
        {
            instance = null;
            if (weapon == null) return true;

            IReadOnlyList<ItemData> items = bag.Catalog.Items;
            for (int i = 0; i < items.Count; i++)
            {
                ItemData candidate = items[i];
                if (candidate == null || candidate.grantedWeapon != weapon || !candidate.IsEquippable) continue;
                if (bag.TryMintKitInstance(candidate, out instance, out string reason)) return true;
                Debug.LogWarning($"[Emberdelve][ClassLoadout] kit item for {weapon.weaponId} could not be minted: {reason}");
                return false;
            }

            Debug.LogWarning($"[Emberdelve][ClassLoadout] no catalog item grants {weapon.weaponId}: the kit stays on the class fallback hands.");
            return false;
        }

        private void Update()
        {
            var held = _rig != null && _rig.Inventory != null ? _rig.Inventory.HeldItem : null;
            if (held != _shownUtility) SyncArms(ActiveWeapon);
            if (_armsPending && Application.isPlaying) SyncArms(ActiveWeapon);
            if (_rig == null || _rig.IsDead) return;
            if (Emberdelve.Core.NetRole.IsClientOnly) return; // U3B3: a network client asks the server to swap (NetCombatSender); it never swaps locally

            EmberdelveInput input = _rig.Input;
            if (input == null) return;

            if (input.SwapPressed) RequestWeaponSetSwap();
        }

        /// <summary>
        /// D3c: the swap key. Weapon set 1 is slot 0 (main hand = primary weapon) and set 2 is slot 1, so the existing SwapWeapon key
        /// IS the weapon-set swap. It is submitted as the sequenced, owner-bound SwapWeaponSet inventory command, so the swap is
        /// decided by the authority (refused while swinging, drawing or reloading; atomic; one revision). A bare rig with no command
        /// authority keeps the direct local swap. The press is read once on its frame: a refusal queues nothing.
        /// </summary>
        public bool RequestWeaponSetSwap()
        {
            Equipment gear = _rig != null ? _rig.Equipment : null;
            Inventory bag = _rig != null ? _rig.Inventory : null;
            IInventoryAuthority authority = bag != null ? bag.CommandAuthority : null;
            if (authority == null) return SwitchWeapon();
            if (gear == null) return false;

            int active = _activeSlot < 0 ? 0 : _activeSlot;
            int target = active == 0 ? 1 : 0;
            if (_slots[target] == null) return false;

            ItemInstance current = gear.MainHandInstance(active);
            ItemInstance next = gear.MainHandInstance(target);
            if (!authority.TryCreateLocalCommand(InventoryCommandKind.SwapWeaponSet, 0, 0, Vector2Int.zero, EquipmentSlot.None,
                    current != null ? current.Uid : System.Guid.Empty, next != null ? next.Uid : System.Guid.Empty,
                    out InventoryCommand command, out _)) return false;

            return authority.Submit(_rig, command).Accepted;
        }

        /// <summary>D3c: the weapon in a set's main hand (class asset or worn item), or null when that set has none. Read-only.</summary>
        public WeaponData WeaponInSlot(int slot)
        {
            return slot >= 0 && slot < SlotCapacity ? _slots[slot] : null;
        }

        /// <summary>
        /// D3c: empties set 2's main hand (a class with no secondary weapon whose worn set-2 weapon was taken off). When set 2 was in
        /// hand the loadout first falls back to set 1, so it is never left holding nothing.
        /// </summary>
        internal void ClearSecondaryWeapon()
        {
            ClearSecondaryWeaponCore(true);
        }

        internal void ClearSecondaryWeaponSilently()
        {
            ClearSecondaryWeaponCore(false);
        }

        private void ClearSecondaryWeaponCore(bool publishEvents)
        {
            if (publishEvents && !CanMutateHands()) return;
            if (_slots[1] == null) return;
            if (_activeSlot == 1 && _slots[0] != null)
            {
                if (publishEvents) Equip(0);
                else EquipSilently(0);
            }
            if (_activeSlot != 1) _slots[1] = null;
        }

        /// <summary>Toggles primary and secondary. Returns false when the class has no secondary weapon.</summary>
        public bool SwitchWeapon()
        {
            return SwitchToSlot(_activeSlot == 0 ? 1 : 0);
        }

        /// <summary>
        /// D1b: true while the wielder is in the action phase of an attack: a melee swing running on the authority
        /// (windup, active, recovery) or a ranged draw. B1: also while the ranged weapon in hand is reloading.
        /// A player swap is refused during it.
        /// </summary>
        public bool IsActionLocked =>
            (_rig != null && GameServices.TryGet<ICombatAuthority>(out ICombatAuthority authority)
                && authority is ICombatFlowState flow && flow.IsSwingInProgress(_rig.CombatId))
            || (rangedCombat != null && rangedCombat.enabled && (rangedCombat.IsDrawing || rangedCombat.IsReloading));

        /// <summary>B1: true while the ranged weapon in hand is reloading (the part of <see cref="IsActionLocked"/> that is not a swing or a draw).</summary>
        public bool IsReloadLocked => rangedCombat != null && rangedCombat.enabled && rangedCombat.IsReloading;

        public string ActionLockMessage => IsReloadLocked ? "Cannot swap weapons while reloading" : "Cannot swap weapons during an attack";

        public bool SwitchToSlot(int slot)
        {
            if (slot < 0 || slot >= SlotCapacity) return false;
            if (slot == _activeSlot) return false;
            if (_slots[slot] == null) return false;
            if (!CanMutateHands()) return false;

            if (IsActionLocked)
            {
                // Refused with no state change and nothing queued: the swap input is only read on the frame it is pressed.
                GameEvents.RaiseToast(OwnerId, ActionLockMessage);
                return false;
            }

            Equip(slot);
            return true;
        }

        /// <summary>
        /// Forced change (class apply, respawn, tools): not a player swap, so the attack lock does not apply. The
        /// authority still ends a running swing as WeaponChanged (defence in depth).
        /// </summary>
        public bool ForceSwitchToSlot(int slot)
        {
            if (slot < 0 || slot >= SlotCapacity || slot == _activeSlot || _slots[slot] == null) return false;
            if (!CanMutateHands()) return false;
            Equip(slot);
            return true;
        }

        /// <summary>
        /// Puts a different weapon into a loadout hand, which is what equipping a weapon item from the
        /// inventory does. Ammunition follows the weapon rather than the hand, so a bow that already has
        /// bolts recorded keeps them and a brand new one is seeded from the class's numbers for that slot.
        /// The previously held weapon is returned to the caller so unequipping can put it back.
        /// </summary>
        public WeaponData TryReplaceWeaponSlot(int slot, WeaponData weapon)
        {
            return TryReplaceWeaponSlotCore(slot, weapon, true);
        }

        internal WeaponData TryReplaceWeaponSlotSilently(int slot, WeaponData weapon)
        {
            return TryReplaceWeaponSlotCore(slot, weapon, false);
        }

        private WeaponData TryReplaceWeaponSlotCore(int slot, WeaponData weapon, bool publishEvents)
        {
            if (publishEvents && !CanMutateHands()) return null;
            if (slot < 0 || slot >= SlotCapacity) return null;
            if (weapon == null) return null;

            WeaponData previous = _slots[slot];
            if (previous == weapon) return null;

            // D1b: replacing the weapon IN HAND is a swap too (Equipment refuses earlier in the equip transaction; this is the
            // last line): refused mid-attack with no state change.
            if (slot == _activeSlot && IsActionLocked)
            {
                GameEvents.RaiseToast(OwnerId, ActionLockMessage);
                return null;
            }

            if (previous != null && previous.IsRanged
                && rangedCombat != null && rangedCombat.enabled
                && rangedCombat.Weapon == previous)
            {
                StoreAmmo(previous);
            }

            _slots[slot] = weapon;

            if (weapon.IsRanged && FindAmmo(weapon) == null)
            {
                int reserve = slot == 0 ? (_appliedClass != null ? _appliedClass.startingReserveAmmo : 0)
                                        : (_appliedClass != null ? _appliedClass.secondaryStartingReserveAmmo : 0);
                int maxReserve = slot == 0 ? (_appliedClass != null ? _appliedClass.maxReserveAmmo : 0)
                                           : (_appliedClass != null ? _appliedClass.secondaryMaxReserveAmmo : 0);
                SeedAmmo(slot, reserve, maxReserve);
            }

            if (slot == _activeSlot)
            {
                if (publishEvents) Equip(slot);
                else EquipSilently(slot);
            }

            return previous;
        }

        private bool CanMutateHands()
        {
            Inventory inventory = _rig != null ? _rig.Inventory : null;
            return inventory == null || inventory.CanCommitRevision(out _);
        }

        internal sealed class DropHandSnapshot
        {
            internal WeaponData[] Weapons;
            internal int ActiveSlot;
            internal int ChangeCount;
            internal RangedAmmoState[] Ammo;
        }

        internal DropHandSnapshot CaptureDropHandSnapshot()
        {
            var snapshot = new DropHandSnapshot
            {
                Weapons = (WeaponData[])_slots.Clone(),
                ActiveSlot = _activeSlot,
                ChangeCount = WeaponChangeCount,
                Ammo = new RangedAmmoState[_ammo.Count]
            };
            for (int i = 0; i < _ammo.Count; i++) snapshot.Ammo[i] = CopyAmmo(_ammo[i]);
            return snapshot;
        }

        internal void RestoreDropHandSnapshot(DropHandSnapshot snapshot)
        {
            if (snapshot == null) return;
            for (int i = 0; i < SlotCapacity; i++) _slots[i] = snapshot.Weapons[i];
            _ammo.Clear();
            for (int i = 0; i < snapshot.Ammo.Length; i++) _ammo.Add(CopyAmmo(snapshot.Ammo[i]));
            _activeSlot = snapshot.ActiveSlot;

            WeaponData weapon = ActiveWeapon;
            bool melee = weapon != null && !weapon.IsRanged;
            bool ranged = weapon != null && weapon.IsRanged;
            if (meleeCombat != null)
            {
                meleeCombat.enabled = melee && !NetRole.IsClientOnly;
                meleeCombat.Initialize(weapon);
            }
            if (rangedCombat != null)
            {
                rangedCombat.enabled = ranged && !NetRole.IsClientOnly;
                if (ranged)
                {
                    RangedAmmoState state = FindAmmo(weapon) ?? CreateAmmo(weapon, 0, 0);
                    rangedCombat.Initialize(weapon, state.reserve, state.maxReserve, state.magazine);
                }
                else rangedCombat.Initialize(weapon);
            }

            RebuildWeaponVisual(weapon);
            if (_rig != null && _rig.Equipment != null) _rig.Equipment.BeginWeaponSet(_activeSlot, false);
            if (_rig != null) _rig.ApplyMainHandWeapon(weapon);
            if (_arms != null) SyncArms(weapon);
            WeaponChangeCount = snapshot.ChangeCount;
        }

        internal bool MatchesDropHandSnapshot(DropHandSnapshot snapshot)
        {
            if (snapshot == null || snapshot.Weapons == null || snapshot.Weapons.Length != SlotCapacity ||
                snapshot.ActiveSlot != _activeSlot || snapshot.ChangeCount != WeaponChangeCount ||
                snapshot.Ammo == null || snapshot.Ammo.Length != _ammo.Count) return false;
            for (int i = 0; i < SlotCapacity; i++)
                if (!ReferenceEquals(snapshot.Weapons[i], _slots[i])) return false;
            for (int i = 0; i < _ammo.Count; i++)
            {
                RangedAmmoState expected = snapshot.Ammo[i];
                RangedAmmoState actual = _ammo[i];
                if (expected == null || actual == null)
                {
                    if (!ReferenceEquals(expected, actual)) return false;
                    continue;
                }
                if (expected.weaponId != actual.weaponId ||
                    expected.magazine != actual.magazine || expected.reserve != actual.reserve ||
                    expected.maxReserve != actual.maxReserve) return false;
            }
            return true;
        }

        private static RangedAmmoState CopyAmmo(RangedAmmoState state)
        {
            return state == null ? null : new RangedAmmoState
            {
                weaponId = state.weaponId,
                magazine = state.magazine,
                reserve = state.reserve,
                maxReserve = state.maxReserve
            };
        }

        private void Equip(int slot)
        {
            using (GameEventTransaction.Defer()) EquipState(slot, true);
        }

        private void EquipSilently(int slot) => EquipState(slot, false);

        private void EquipState(int slot, bool publishEvents)
        {
            // -1 means nothing has been equipped yet, which is the case on the very first Equip from Apply.
            // Storing at that point would copy the component's uninitialized ammo over the seeded state.
            WeaponData previous = _activeSlot >= 0 && _activeSlot < SlotCapacity ? _slots[_activeSlot] : null;

            if (previous != null && previous.IsRanged
                && rangedCombat != null && rangedCombat.enabled
                && rangedCombat.Weapon == previous)
            {
                StoreAmmo(previous);
            }

            // D3c: the weapon set follows the hand. A change of set is a swap (not the very first Equip of a class apply); the
            // set's off hand is put in place before the rig resolves its defense for the new main hand.
            bool isSwap = _activeSlot >= 0 && _activeSlot != slot;
            Equipment setOwner = _rig != null ? _rig.Equipment : null;
            bool setChanged = setOwner != null && setOwner.BeginWeaponSet(slot, isSwap);

            _activeSlot = slot;
            WeaponChangeCount++;
            WeaponData weapon = _slots[slot];

            bool weaponIsMelee = weapon != null && !weapon.IsRanged;
            bool weaponIsRanged = weapon != null && weapon.IsRanged;

            // Exactly one combat component is live at a time, and it is the weapon that decides which one.
            if (meleeCombat != null)
            {
                meleeCombat.enabled = weaponIsMelee && !NetRole.IsClientOnly;
                meleeCombat.Initialize(weapon);
            }

            if (rangedCombat != null)
            {
                rangedCombat.enabled = weaponIsRanged && !NetRole.IsClientOnly;

                if (weaponIsRanged)
                {
                    RangedAmmoState state = FindAmmo(weapon);
                    if (state == null) state = CreateAmmo(weapon, 0, 0);
                    rangedCombat.Initialize(weapon, state.reserve, state.maxReserve, state.magazine);
                }
                else
                {
                    rangedCombat.Initialize(weapon);
                }
            }

            RebuildWeaponVisual(weapon);

            // The defense profile follows the hand it is standing in: a crossbow main hand cannot block
            // with a sword's capability, and a rapier is where a Parry flag may legitimately appear.
            if (_rig != null) _rig.ApplyMainHandWeapon(weapon);

            // All hand, pool and ammo callbacks remain deferred until the new stats and shared revision are final.
            if (setOwner != null) setOwner.CompleteWeaponSet(setChanged, isSwap);

            // V3d: the off hand is final only now (the set's off hand is restored by BeginWeaponSet / ApplyMainHandWeapon above), so the first-person hands are dressed again from the settled state.
            if (_arms != null) SyncArms(weapon);

            if (publishEvents)
            {
                GameEvents.RaiseActiveWeapon(OwnerId, weapon);
                // Client hands are reconstructed from snapshots, including bag-only changes.
                // Keep the presentation event, but do not announce those refreshes as equip actions.
                if (weapon != null && !NetRole.IsClientOnly)
                    GameEvents.RaiseToast(OwnerId, $"Equipped {weapon.displayName}");
            }
        }

        private void SeedAmmo(int slot, int startingReserve, int maxReserve)
        {
            WeaponData weapon = _slots[slot];
            if (weapon == null || !weapon.IsRanged) return;

            RangedAmmoState state = CreateAmmo(weapon, startingReserve, maxReserve);
            state.magazine = Mathf.Clamp(weapon.magazineSize, 1, 99);
        }

        private void StoreAmmo(WeaponData weapon)
        {
            RangedAmmoState state = FindAmmo(weapon);
            if (state == null || rangedCombat == null) return;

            state.magazine = rangedCombat.Magazine;
            state.reserve = rangedCombat.Reserve;
        }

        /// <summary>
        /// R1c2: every ranged weapon's ammunition as a kit seed. The pool of the weapon in hand comes from the live component, the
        /// others from the stored per-weapon state (the same numbers a swap would restore).
        /// </summary>
        internal KitAmmoSeed[] CaptureAmmoSeeds(RangedCombat live)
        {
            List<KitAmmoSeed> seeds = new List<KitAmmoSeed>();
            for (int i = 0; i < _ammo.Count; i++)
            {
                RangedAmmoState state = _ammo[i];
                if (state == null) continue;

                WeaponData weapon = null;
                for (int s = 0; s < _slots.Length; s++)
                {
                    if (_slots[s] != null && _slots[s].weaponId == state.weaponId) { weapon = _slots[s]; break; }
                }
                if (weapon == null || !weapon.IsRanged) continue;

                int magazine = state.magazine;
                int reserve = state.reserve;
                int maxReserve = state.maxReserve;
                if (live != null && live.enabled && live.Weapon == weapon)
                {
                    magazine = live.Magazine;
                    reserve = live.Reserve;
                    maxReserve = live.MaxReserve;
                }

                seeds.Add(new KitAmmoSeed
                {
                    WeaponId = state.weaponId,
                    MagazineSize = Mathf.Clamp(weapon.magazineSize, 1, 99),
                    ReloadTime = weapon.reloadTime,
                    Magazine = magazine,
                    Reserve = reserve,
                    MaxReserve = maxReserve,
                });
            }
            return seeds.ToArray();
        }

        /// <summary>Ammo carried for a weapon that is not currently in hand, or null if the kit has none.</summary>
        public RangedAmmoState GetAmmoState(WeaponData weapon)
        {
            return weapon != null ? FindAmmo(weapon) : null;
        }

        private RangedAmmoState FindAmmo(WeaponData weapon)
        {
            for (int i = 0; i < _ammo.Count; i++)
            {
                if (_ammo[i] != null && _ammo[i].weaponId == weapon.weaponId) return _ammo[i];
            }
            return null;
        }

        private RangedAmmoState CreateAmmo(WeaponData weapon, int startingReserve, int maxReserve)
        {
            RangedAmmoState state = new RangedAmmoState
            {
                weaponId = weapon.weaponId,
                magazine = 0,
                reserve = Mathf.Max(0, startingReserve),
                maxReserve = Mathf.Max(0, maxReserve)
            };
            state.reserve = Mathf.Min(state.reserve, state.maxReserve);
            _ammo.Add(state);
            return state;
        }

        public void PlaySwingVisual(WeaponData weapon, SwingPatternData pattern)
        {
            if (_visual != null) _visual.PlaySwing(weapon, pattern);
            if (_arms != null && pattern != null) _arms.PlaySwing(pattern.kind, pattern.Timing.Total);
        }

        public void PlaySwingVisual(WeaponData weapon, SwingPatternData pattern, in SwingTiming timing)
        {
            if (_visual != null) _visual.PlaySwing(weapon, pattern, timing);
            if (_arms != null && pattern != null) _arms.PlaySwing(pattern.kind, timing.Total);
        }

        public void PlaySwingRecoil()
        {
            if (_visual != null) _visual.PlayRecoil();
            if (_arms != null) _arms.PlayRecoil();
        }

        /// <summary>The first-person weapon visual (null when nothing is held). Exposed for tools and gates.</summary>
        public WeaponVisualRig Visual => _visual;

        /// <summary>R7: a rig cloned from a live one inherits a copy of the template's weapon model under the anchor; the clone builds its own, so the copy goes first.</summary>
        public void DiscardInheritedVisuals()
        {
            _visual = null;
            _arms = null;
            _armsPending = false;
            if (weaponAnchor == null) return;
            for (int i = weaponAnchor.childCount - 1; i >= 0; i--) Destroy(weaponAnchor.GetChild(i).gameObject);
        }

        public void SetDrawVisual(float progress)
        {
            if (_visual != null) _visual.SetDrawProgress(progress);
        }

        public void SetGuardHeldVisual(bool held)
        {
            if (_visual != null) _visual.SetGuardHeld(held);
            if (_arms != null) _arms.SetBlock(held);
        }

        public void RebuildWeaponVisual(WeaponData weapon)
        {
            if (_visual != null)
            {
                Destroy(_visual.gameObject);
                _visual = null;
            }

            if (weaponAnchor == null) return;
            if (weapon == null) { SyncArms(null); return; }
            Transform aimFrame = _rig != null && _rig.Look != null ? _rig.Look.AimTransform : weaponAnchor;
            _visual = WeaponVisualBuilder.Build(weapon, weaponAnchor, aimFrame);
            // Server/observer rigs still need the swing transform, but their camera weapon is
            // not a world model. PlayerAvatar owns the visible remote hands and equipment.
            if (_visual != null && _rig != null && !_rig.IsLocallyControlled) _visual.HideBlockoutRenderers();
            if (_visual != null && weapon.isUnarmed) _visual.HideBlockoutRenderers(); // U5A: the bare fists have no blockout model, only the swing
            SyncArms(weapon);
        }

        /// <summary>The first-person arms of the local player (null for other players, servers, tools and weapons without a model).</summary>
        public FirstPersonArmsDriver Arms => _arms;

        /// <summary>
        /// V3c: dresses the local player's hands with the weapon model (presentation only). The arms exist only for the local,
        /// camera-carrying player in play mode; a weapon without a model (bows, the claw) keeps its blockout visual and shows no arms.
        /// When the rig is not yet known to be local (class applied before the run director assigns control) the attempt is retried.
        /// </summary>
        private void SyncArms(WeaponData weapon)
        {
            if (_viewModels == null) _viewModels = WeaponViewModelSet.Load();
            if (_arms == null && Application.isPlaying)
            {
                _arms = FirstPersonArmsDriver.Create(weaponAnchor, _rig, targetCamera, _viewModels);
                _armsPending = _arms == null && _viewModels != null && _viewModels.armsPrefab != null && weapon != null;
            }

            if (_arms == null)
            {
                _shownUtility = _rig != null && _rig.Inventory != null ? _rig.Inventory.HeldItem : null;
                if (_visual != null) _visual.gameObject.SetActive(_shownUtility == null);
                return;
            }
            _armsPending = false;
            _shownUtility = _rig != null && _rig.Inventory != null ? _rig.Inventory.HeldItem : null;
            if (_visual != null) _visual.gameObject.SetActive(_shownUtility == null);
            if (_shownUtility != null)
            {
                _arms.SetVisual(null);
                _arms.SetUtility(_shownUtility);
                _arms.SetBlock(false);
                _arms.gameObject.SetActive(true);
                return;
            }
            bool dressed = weapon != null && (_arms.SetWeapons(weapon, _rig != null ? _rig.OffhandWeapon : null) || weapon.isUnarmed);
            _arms.SetVisual(_visual);
            _arms.gameObject.SetActive(dressed);
            if (dressed)
            {
                if (_visual != null && weapon.weaponId != "hollow_bow") _visual.HideBlockoutRenderers();
                _arms.SetBlock(_rig != null && _rig.IsGuarding);
            }
        }

        /// <summary>Per-weapon ammunition, so a swap cannot quietly refill a half-empty magazine.</summary>
        public sealed class RangedAmmoState
        {
            public string weaponId;
            public int magazine;
            public int reserve;
            public int maxReserve;

            public override string ToString()
            {
                return $"{weaponId}: {magazine} / {reserve} (cap {maxReserve})";
            }
        }
    }
}
