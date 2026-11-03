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
        private CharacterClassData _appliedClass;
        private PlayerRig _rig;

        private readonly WeaponData[] _slots = new WeaponData[SlotCapacity];
        private readonly List<RangedAmmoState> _ammo = new List<RangedAmmoState>(SlotCapacity);
        private int _activeSlot;

        public bool HasMuzzle => _visual != null && _visual.Muzzle != null;
        public Vector3 MuzzlePosition => _visual != null && _visual.Muzzle != null ? _visual.Muzzle.position : Vector3.zero;
        public CharacterClassData AppliedClass => _appliedClass;

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

        public void Apply(CharacterClassData data, GameConfig config, PlayerRig rig)
        {
            if (data == null || rig == null) return;
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

            _ammo.Clear();
            SeedAmmo(0, data.startingReserveAmmo, data.maxReserveAmmo);
            SeedAmmo(1, data.secondaryStartingReserveAmmo, data.secondaryMaxReserveAmmo);

            _activeSlot = -1;
            Equip(0);

            if (targetCamera != null) targetCamera.fieldOfView = config.fieldOfView;
        }

        private void Update()
        {
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
            if (_slots[1] == null) return;
            if (_activeSlot == 1 && _slots[0] != null) Equip(0);
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

            if (slot == _activeSlot) Equip(slot);

            return previous;
        }

        private void Equip(int slot)
        {
            using (GameEventTransaction.Defer()) EquipState(slot);
        }

        private void EquipState(int slot)
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
                meleeCombat.enabled = weaponIsMelee;
                meleeCombat.Initialize(weapon);
            }

            if (rangedCombat != null)
            {
                rangedCombat.enabled = weaponIsRanged;

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

            GameEvents.RaiseActiveWeapon(OwnerId, weapon);
            if (weapon != null) GameEvents.RaiseToast(OwnerId, $"Equipped {weapon.displayName}");
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
        }

        public void PlaySwingVisual(WeaponData weapon, SwingPatternData pattern, in SwingTiming timing)
        {
            if (_visual != null) _visual.PlaySwing(weapon, pattern, timing);
        }

        public void PlaySwingRecoil()
        {
            if (_visual != null) _visual.PlayRecoil();
        }

        /// <summary>The first-person weapon visual (null when nothing is held). Exposed for tools and gates.</summary>
        public WeaponVisualRig Visual => _visual;

        /// <summary>R7: a rig cloned from a live one inherits a copy of the template's weapon model under the anchor; the clone builds its own, so the copy goes first.</summary>
        public void DiscardInheritedVisuals()
        {
            _visual = null;
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
        }

        public void RebuildWeaponVisual(WeaponData weapon)
        {
            if (_visual != null)
            {
                Destroy(_visual.gameObject);
                _visual = null;
            }

            if (weapon == null || weaponAnchor == null) return;
            Transform aimFrame = _rig != null && _rig.Look != null ? _rig.Look.AimTransform : weaponAnchor;
            _visual = WeaponVisualBuilder.Build(weapon, weaponAnchor, aimFrame);
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
