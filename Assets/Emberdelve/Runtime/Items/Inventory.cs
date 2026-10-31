using System;
using System.Collections.Generic;
using UnityEngine;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Combat;
using Emberdelve.Player;

namespace Emberdelve.Items
{
    /// <summary>
    /// One stack of items as it currently sits in the grid: what it is, how many, which cell its top-left
    /// corner occupies, and the shape it blocks out. The shape comes from the item asset, so a two-by-three
    /// sword and a single-cell coin are stored by the same rule.
    ///
    /// U2B4: a gear stack also reports the one ItemInstance it carries, so an observer can tell two copies of
    /// one definition apart. A carried stack (currency, consumables, relics) keeps the definition-only shape in
    /// this slice and reports a null Instance.
    /// </summary>
    public readonly struct InventorySlot
    {
        public readonly ItemData Item;
        public readonly ItemInstance Instance;
        public readonly int Count;
        public readonly Vector2Int Origin;
        public readonly Vector2Int Size;
        public readonly int Index;
        /// <summary>Runtime identity of this exact inventory stack; zero means a detached/legacy view.</summary>
        public readonly ulong StackId;

        public InventorySlot(ItemData item, int count)
        {
            Item = item;
            Instance = null;
            Count = count;
            Origin = Vector2Int.zero;
            Size = Vector2Int.one;
            Index = -1;
            StackId = 0;
        }

        public InventorySlot(ItemData item, int count, Vector2Int origin, Vector2Int size, int index)
        {
            Item = item;
            Instance = null;
            Count = count;
            Origin = origin;
            Size = size;
            Index = index;
            StackId = 0;
        }

        public InventorySlot(ItemData item, ItemInstance instance, int count, Vector2Int origin,
            Vector2Int size, int index)
            : this(item, instance, count, origin, size, index, 0)
        {
        }

        public InventorySlot(ItemData item, ItemInstance instance, int count, Vector2Int origin,
            Vector2Int size, int index, ulong stackId)
        {
            Item = item;
            Instance = instance;
            Count = count;
            Origin = origin;
            Size = size;
            Index = index;
            StackId = stackId;
        }

        /// <summary>True when this stack is an actual item instance rather than a definition-only carried stack.</summary>
        public bool IsInstanceBacked => Instance != null;

        /// <summary>Canonical Guid "N" text of the identity this stack carries, empty when it carries none.</summary>
        public string UidText => Instance != null ? Instance.UidText : string.Empty;
    }

    /// <summary>
    /// Stack-based carrying bag for the local player, stored as a real width x height cell grid.
    ///
    /// It stores what the player holds; it never decides what a pickup is worth or whether a run succeeds
    /// (RunDirector does that from the facts raised through GameEvents). Consumable effects are resolved by the
    /// bound inventory authority so the live stack identity and owner are checked before an effect is applied.
    ///
    /// Placement is validated, not assumed: an item only enters the grid if its footprint fits, an existing
    /// stack only takes units of the same item up to its cap, and a move is refused when the destination
    /// overlaps something else. A partial add is a real outcome, so a nearly full bag leaves loot behind.
    ///
    /// U2B4: worn or held gear is stored as an actual <see cref="ItemInstance"/>, one identity per occupied
    /// gear stack. A legacy add of an equippable definition mints exactly one explicit Common instance for
    /// each gear object it accepts (quantity 1 each) through this bag's own issuer.
    ///
    /// R3: the bag no longer builds a catalog out of whatever definition it is handed. It holds the run's one
    /// <see cref="ItemCatalog"/> (<see cref="SetCatalog"/>, wired from the game config by the class loadout),
    /// and a gear definition that is not that catalog's own entry is refused before any state changes - no UID
    /// is minted for an arbitrary ItemData. A bag with no catalog mints nothing. <see cref="TryAddInstance"/>
    /// is the path for an instance that already exists and keeps its uid, rarity and rolls; when a catalog is
    /// set the instance's definition must be a catalog entry too. Carried stacks (currency, consumables,
    /// relics) keep the definition-only behavior in this slice.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class Inventory : MonoBehaviour
    {
        [SerializeField] private int gridWidth = 10;
        [SerializeField] private int gridHeight = 4;

        /// <summary>
        /// One cell-anchored stack. Definition-facing reads go through <see cref="Definition"/>, which prefers
        /// the instance's own definition, so a gear stack can never wear a definition its identity does not have.
        /// </summary>
        private sealed class Stack
        {
            public ulong StackId;
            public ItemData Item;
            public ItemInstance Instance;
            public int Count;
            public Vector2Int Origin;

            public ItemData Definition => Instance != null ? Instance.Definition : Item;
        }

        private readonly List<Stack> _stacks = new List<Stack>();
        private readonly List<InventorySlot> _views = new List<InventorySlot>();
        private int[] _cells = new int[40];
        [NonSerialized] private ulong _nextStackId = 1;
        [NonSerialized] private bool _stackIdsExhausted;

        private PlayerRig _rig;
        private RangedCombat _rangedCombat;

        // Local identity plumbing (U2B4). A local trusted issuer for one process, not a network authority.
        private ItemIdentityIssuer _issuer;
        private Equipment _equipment;
        [NonSerialized] private LocalInventoryAuthority _localAuthority;
        [NonSerialized] private HotbarBindings _hotbar;

        // R3: the run's one item catalog. Null until the loadout (or a test) supplies it; gear is only minted
        // against it.
        private ItemCatalog _catalog;

        /// <summary>The item catalog this bag mints and admits gear against, or null when none was supplied.</summary>
        public ItemCatalog Catalog => _catalog;

        /// <summary>
        /// R4: the UID source this bag mints with (starting kit, legacy gear adds). In a run it is the raid session's
        /// one issuer (<see cref="SetIssuer"/>, called by the run before the class is applied); a bag nobody pointed
        /// at an issuer (a fixture) lazily makes a private local one on first mint.
        /// </summary>
        public ItemIdentityIssuer Issuer => _issuer;

        /// <summary>Points the bag at the raid's issuer. Null is ignored (a bag never loses its issuer).</summary>
        public void SetIssuer(ItemIdentityIssuer issuer)
        {
            if (issuer != null) _issuer = issuer;
        }

        /// <summary>
        /// Supplies the run's item catalog. Setting the same catalog again is a no-op; passing null clears it
        /// (and a bag with no catalog mints no gear). Production wiring is ClassLoadout.Apply from
        /// GameConfig.itemCatalog; a fixture passes an explicit test catalog (ItemCatalog.CreateForTest).
        /// </summary>
        public void SetCatalog(ItemCatalog catalog)
        {
            _catalog = catalog;
        }

        public int GridWidth => gridWidth;
        public int GridHeight => gridHeight;

        /// <summary>Cells the grid has, which is what capacity means once items have a shape.</summary>
        public int Capacity => gridWidth * gridHeight;
        public int UsedSlots => _stacks.Count;
        public int UsedCells { get; private set; }
        public int FreeCells => Mathf.Max(0, Capacity - UsedCells);
        public IReadOnlyList<InventorySlot> Slots => _views;
        /// <summary>Local command authority bound to this rig's exact bag and equipment pair, when wired.</summary>
        public IInventoryAuthority CommandAuthority => GetOrCreateLocalAuthority();
        /// <summary>
        /// R6c2: the 1-4 hotbar assignments for this bag. Each slot names a runtime StackId, never a list index, so
        /// compaction cannot retarget a key. Cleared by <see cref="Clear"/>, re-seeded by a loadout restore and by
        /// <see cref="GiveStartingItems"/>.
        /// </summary>
        public HotbarBindings Hotbar => _hotbar ?? (_hotbar = new HotbarBindings(this));

        /// <summary>The player id this bag raises events for (0 = none). Exposed for the hotbar's change event.</summary>
        internal int OwnerPlayerId => OwnerId;

        public int TotalUnits { get; private set; }
        public int CarriedValue { get; private set; }

        /// <summary>Value recovered from the level this run. Starting kit is excluded so the extraction
        /// objective cannot be satisfied by gear the class already carries in.</summary>
        public int RecoveredValue { get; private set; }
        public int LifetimeCollected { get; private set; }

        private bool _givingStartingKit;

        /// <summary>Monotonic revision for the shared bag/equipment loadout owned by this inventory.</summary>
        public ulong Revision { get; private set; }

        /// <summary>Checks the shared revision boundary before a caller starts a transaction that may mutate state.</summary>
        internal bool CanCommitRevision(out string reason)
        {
            if (Revision == ulong.MaxValue)
            {
                reason = "this inventory/loadout revision is exhausted";
                return false;
            }

            reason = null;
            return true;
        }

        /// <summary>Commits one logical mutation. Call once after every state half is final and before outward events.</summary>
        internal void CommitRevision()
        {
            if (Revision == ulong.MaxValue)
                throw new InvalidOperationException("This inventory/loadout exhausted its revision space.");
            Revision++;
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
            _rangedCombat = GetComponent<RangedCombat>();
            EnsureGrid();
        }

        public void Initialize(PlayerRig rig)
        {
            if (!ReferenceEquals(_rig, rig)) _localAuthority = null;
            _rig = rig;
            _rangedCombat = GetComponent<RangedCombat>();
            EnsureGrid();
        }

        private LocalInventoryAuthority GetOrCreateLocalAuthority()
        {
            if (_rig == null) _rig = GetComponent<PlayerRig>();
            if (_rig == null) return null;
            if (_localAuthority == null)
            {
                Equipment equipment = _rig.Equipment != null ? _rig.Equipment : EquipmentPartner();
                if (equipment == null) return null;
                _localAuthority = new LocalInventoryAuthority(_rig, this, equipment);
            }
            return _localAuthority;
        }

        /// <summary>Kept for callers that only know a bag size: it becomes a grid of that many cells.</summary>
        public void Configure(int newCapacity)
        {
            int cells = Mathf.Max(1, newCapacity);
            int width = Mathf.Clamp(cells, 1, 12);
            int height = Mathf.Max(1, Mathf.CeilToInt(cells / (float)width));
            ConfigureGrid(width, height);
        }

        public void ConfigureGrid(int width, int height)
        {
            int newWidth = Mathf.Max(1, width);
            int newHeight = Mathf.Max(1, height);

            if (newWidth == gridWidth && newHeight == gridHeight)
            {
                EnsureGrid();
                return;
            }

            if (!CanCommitRevision(out string revisionReason))
            {
                Debug.LogWarning(revisionReason, this);
                return;
            }

            gridWidth = newWidth;
            gridHeight = newHeight;

            // Reseating the grid re-places everything the player is carrying, and an item that no longer
            // fits is left behind rather than silently deleted.
            List<Stack> carried = new List<Stack>(_stacks);
            List<string> leftBehind = new List<string>();
            _stacks.Clear();
            EnsureGrid();
            Recompute();

            for (int i = 0; i < carried.Count; i++)
            {
                Stack stack = carried[i];
                ItemData definition = stack.Definition;

                // Re-seating is a move, not a re-issue: the same instance object is placed in the new grid, so a
                // resized bag never mints a second identity or drops the rolls one gear stack already had.
                Vector2Int origin = FindFreeOrigin(definition.EffectiveFootprint);
                if (origin.x < 0)
                {
                    TotalUnits = Mathf.Max(0, TotalUnits - stack.Count);
                    CarriedValue = Mathf.Max(0, CarriedValue - definition.value * stack.Count);
                    leftBehind.Add($"Bag too small, left {definition.displayName} behind");
                    continue;
                }

                AddStack(definition, stack.Instance, stack.Count, origin, stack.StackId);
            }

            Recompute();
            CommitRevision();
            for (int i = 0; i < leftBehind.Count; i++) GameEvents.RaiseToast(OwnerId, leftBehind[i]);
            GameEvents.RaiseInventoryChanged(OwnerId);
        }

        public void GiveStartingItems(ItemData[] items)
        {
            if (items == null) return;

            _givingStartingKit = true;
            try
            {
                for (int i = 0; i < items.Length; i++)
                {
                    if (items[i] != null) TryAdd(items[i], 1);
                }
            }
            finally
            {
                _givingStartingKit = false;
                // R6c2: the raid loadout has just been (re)established; bind the first usable stacks to keys 1-4
                // before observers hear about the bag, so the HUD's refresh already sees the bindings.
                Hotbar.SeedDefaults();
                GameEvents.RaiseInventoryChanged(OwnerId);
            }
        }

        // ------------------------------------------------------------ adding

        public bool TryAdd(ItemData item, int amount = 1)
        {
            return TryAdd(item, amount, out _);
        }

        /// <summary>
        /// Adds what fits and answers the yes/no question only: true when every unit landed.
        /// A caller that has to keep a source in step with what it handed over needs the counted form
        /// <see cref="TryAdd(ItemData,int,out string,out int)"/>, because a partial add is a real outcome and a
        /// bool cannot say how many units actually committed.
        /// </summary>
        public bool TryAdd(ItemData item, int amount, out string reason)
        {
            return TryAdd(item, amount, out reason, out _);
        }

        /// <summary>
        /// Adds what fits: units go onto matching stacks first, then into new shapes anywhere a hole exists.
        /// Returns true only when every unit landed, and reports why the rest could not.
        ///
        /// <paramref name="accepted"/> is the exact number of units this call committed into the grid, which is
        /// the same number this method added to TotalUnits, CarriedValue, LifetimeCollected and RecoveredValue,
        /// and is therefore the only number a source container may remove from what it offered. It is 0 when
        /// nothing was committed (refused call, no room at all, or a refused gear mint that rolled the whole
        /// add back out) and equals <paramref name="amount"/> exactly when the return value is true.
        ///
        /// An equippable definition never joins an existing stack: each gear object this call accepts is placed
        /// as its own stack with its own freshly minted Common instance. If that identity cannot be minted the
        /// whole add is refused and every stack this call placed is taken back out.
        /// </summary>
        public bool TryAdd(ItemData item, int amount, out string reason, out int accepted)
        {
            reason = null;
            accepted = 0;

            if (item == null)
            {
                reason = "no item to add";
                return false;
            }

            if (amount <= 0)
            {
                reason = "nothing to add";
                return false;
            }

            // R3: gear identity is minted against the run's catalog only. Decided before any state is touched,
            // so a refused definition leaves the bag exactly as it was.
            if (item.IsEquippable && !CanMintGear(item, out string mintRefusal))
            {
                reason = $"could not give {item.displayName} an item identity: {mintRefusal}";
                GameEvents.RaiseToast(OwnerId, reason);
                return false;
            }

            if (!CanCommitRevision(out reason)) return false;

            EnsureGrid();

            int remaining = amount;
            int stackLimit = item.StackLimitForFootprint;

            // Prove the call can allocate every new stack ID before it fills any existing stack or mints a
            // gear UID. At the terminal ID boundary this fails closed instead of leaving a partial add behind.
            int unitsAfterExistingStacks = amount;
            for (int i = 0; i < _stacks.Count && unitsAfterExistingStacks > 0; i++)
            {
                Stack stack = _stacks[i];
                if (stack.Definition != item || stack.Instance != null) continue;
                int space = Mathf.Max(0, stackLimit - stack.Count);
                unitsAfterExistingStacks -= Mathf.Min(space, unitsAfterExistingStacks);
            }

            int requiredNewStackIds = item.IsEquippable
                ? unitsAfterExistingStacks
                : (int)(((long)unitsAfterExistingStacks + stackLimit - 1) / stackLimit);
            if (!CanAllocateStackIds(requiredNewStackIds))
            {
                reason = "this bag has exhausted its nonzero runtime stack identity space";
                GameEvents.RaiseToast(OwnerId, reason);
                return false;
            }

            for (int i = 0; i < _stacks.Count && remaining > 0; i++)
            {
                Stack stack = _stacks[i];
                if (stack.Definition != item) continue;
                // Gear stacks are separate items with their own identity: they never take units of a lookalike.
                if (stack.Instance != null) continue;

                int space = stackLimit - stack.Count;
                if (space <= 0) continue;

                int move = Mathf.Min(space, remaining);
                stack.Count += move;
                remaining -= move;
            }

            int createdStacks = 0;

            while (remaining > 0)
            {
                Vector2Int origin = FindFreeOrigin(item.EffectiveFootprint);
                if (origin.x < 0) break;

                int move = Mathf.Min(stackLimit, remaining);

                // Worn gear is one item, so every gear object this call actually places is given its identity
                // here: exactly one explicit Common instance per accepted gear stack, quantity 1 each.
                ItemInstance instance = null;
                if (item.IsEquippable)
                {
                    if (!TryMintCommonInstance(item, out instance, out string mintReason))
                    {
                        reason = $"could not give {item.displayName} an item identity: {mintReason}";
                        RollbackCreatedStacks(createdStacks);
                        GameEvents.RaiseToast(OwnerId, reason);
                        return false;
                    }

                    move = 1;
                }

                AddStack(item, instance, move, origin);
                createdStacks++;
                remaining -= move;
            }

            accepted = amount - remaining;
            Recompute();

            if (accepted <= 0)
            {
                reason = $"no room for {item.displayName} ({item.EffectiveFootprint.x}x{item.EffectiveFootprint.y} cells needed, {FreeCells} free)";
                GameEvents.RaiseToast(OwnerId, "Bag is full");
                return false;
            }

            TotalUnits += accepted;
            CarriedValue += item.value * accepted;

            CommitRevision();

            if (!_givingStartingKit)
            {
                LifetimeCollected += accepted;
                RecoveredValue += item.value * accepted;
                GameEvents.RaiseItemCollected(OwnerId, item, LifetimeCollected);
            }

            GameEvents.RaiseInventoryChanged(OwnerId);

            if (remaining > 0)
            {
                reason = $"bag full, left {remaining} {item.displayName} behind";
                GameEvents.RaiseToast(OwnerId, reason);
                return false;
            }

            return true;
        }

        /// <summary>Places an item at an exact cell, which is what a click in the inventory screen does.</summary>
        public bool TryAddAt(ItemData item, int amount, Vector2Int origin, out string reason)
        {
            reason = null;
            if (item == null || amount <= 0)
            {
                reason = "nothing to place";
                return false;
            }

            if (item.IsEquippable && !CanMintGear(item, out string mintRefusal))
            {
                reason = $"could not give {item.displayName} an item identity: {mintRefusal}";
                return false;
            }

            EnsureGrid();

            Vector2Int size = item.EffectiveFootprint;
            if (!CanPlace(origin, size, -1))
            {
                reason = $"no room for {item.displayName} at cell {origin}";
                return false;
            }

            if (!CanCommitRevision(out reason)) return false;

            int stackLimit = item.StackLimitForFootprint;
            // This exact placement begins in an empty footprint. Validate the maximum number of stacks it can
            // create before adding any of them, so exhaustion cannot strand a partial placement.
            int requiredNewStackIds = item.IsEquippable || !item.CanStack
                ? 1
                : (int)(((long)amount + stackLimit - 1) / stackLimit);
            if (!CanAllocateStackIds(requiredNewStackIds))
            {
                reason = "this bag has exhausted its nonzero runtime stack identity space";
                return false;
            }

            int placed = 0;

            if (item.CanStack)
            {
                int existing = IndexAtCell(origin);
                if (existing >= 0 && _stacks[existing].Definition == item && _stacks[existing].Instance == null)
                {
                    int space = stackLimit - _stacks[existing].Count;
                    int move = Mathf.Min(space, amount);
                    if (move > 0)
                    {
                        _stacks[existing].Count += move;
                        placed += move;
                    }
                }
            }

            int createdStacks = 0;

            while (placed < amount)
            {
                Vector2Int hole = placed == 0 ? origin : FindFreeOrigin(size);
                if (hole.x < 0) break;
                if (placed > 0 && !CanPlace(hole, size, -1)) break;

                int move = item.CanStack ? Mathf.Min(stackLimit, amount - placed) : 1;

                ItemInstance instance = null;
                if (item.IsEquippable)
                {
                    if (!TryMintCommonInstance(item, out instance, out string mintReason))
                    {
                        reason = $"could not give {item.displayName} an item identity: {mintReason}";
                        RollbackCreatedStacks(createdStacks);
                        return false;
                    }

                    move = 1;
                }

                AddStack(item, instance, move, hole);
                createdStacks++;
                placed += move;

                if (!item.CanStack) break;
            }

            Recompute();

            if (placed <= 0)
            {
                reason = $"could not place {item.displayName}";
                return false;
            }

            TotalUnits += placed;
            CarriedValue += item.value * placed;

            CommitRevision();

            if (!_givingStartingKit)
            {
                LifetimeCollected += placed;
                RecoveredValue += item.value * placed;
                GameEvents.RaiseItemCollected(OwnerId, item, LifetimeCollected);
            }

            GameEvents.RaiseInventoryChanged(OwnerId);

            if (placed < amount)
            {
                reason = $"only {placed} of {amount} {item.displayName} fit";
                return false;
            }

            return true;
        }

        // ------------------------------------------------------- item instances (U2B4)

        /// <summary>How many UIDs this bag's local issuer has minted. Minting is not free and not invisible.</summary>
        public int MintedInstanceCount => _issuer != null ? _issuer.MintedCount : 0;

        /// <summary>The instance one bag stack carries, or null when it is a definition-only carried stack.</summary>
        public ItemInstance InstanceAt(int index)
        {
            if (index < 0 || index >= _stacks.Count) return null;
            return _stacks[index].Instance;
        }

        /// <summary>The stack that carries this exact identity, or -1 when this bag carries no such item.</summary>
        public int IndexOfInstance(Guid uid)
        {
            if (uid == Guid.Empty) return -1;
            for (int i = 0; i < _stacks.Count; i++)
            {
                ItemInstance instance = _stacks[i].Instance;
                if (instance != null && instance.Uid == uid) return i;
            }
            return -1;
        }

        /// <summary>Current compacted index for a live stack identity, or -1 for zero/unknown/retired IDs.</summary>
        public int IndexOfStack(ulong stackId)
        {
            if (stackId == 0) return -1;
            for (int i = 0; i < _stacks.Count; i++)
                if (_stacks[i] != null && _stacks[i].StackId == stackId) return i;
            return -1;
        }

        /// <summary>Reads a live stack by identity. Zero, unknown, and retired IDs return false and a default view.</summary>
        public bool TryGetStack(ulong stackId, out InventorySlot slot)
        {
            int index = IndexOfStack(stackId);
            if (index < 0 || index >= _views.Count)
            {
                slot = default;
                return false;
            }

            slot = _views[index];
            return slot.StackId == stackId;
        }

        /// <summary>True when one of this bag's stacks already carries this identity.</summary>
        public bool HoldsInstanceUid(Guid uid) => IndexOfInstance(uid) >= 0;

        /// <summary>
        /// Places an instance that already exists - the path a roller (U2B3) or a future server hands a rolled
        /// item to the bag. The instance itself is what enters the grid: the same UID, the same rarity and the
        /// exact accepted rolls, with no mint, no reroll and no copy. A refusal touches nothing.
        ///
        /// Rolled non-equipment is refused rather than quietly flattened into a definition-only stack, because
        /// that would throw the identity and the rolls away. Currency and consumables keep the legacy
        /// <see cref="TryAdd"/> path in this slice.
        ///
        /// One identity may be carried by this bag exactly once: a UID already in the bag, or already worn by
        /// this player's equipment, is refused. That is this process's own ownership rule, not proof of a
        /// cross-player server ownership decision (U4A).
        /// </summary>
        public bool TryAddInstance(ItemInstance instance, out string reason)
        {
            return TryAddInstanceInternal(instance, new Vector2Int(-1, -1), false, out reason);
        }

        /// <summary>The exact-cell form of <see cref="TryAddInstance"/>, which is what a click in the grid does.</summary>
        public bool TryAddInstanceAt(ItemInstance instance, Vector2Int origin, out string reason)
        {
            return TryAddInstanceInternal(instance, origin, true, out reason);
        }

        private bool TryAddInstanceInternal(ItemInstance instance, Vector2Int origin, bool exactOrigin,
            out string reason)
        {
            reason = null;

            if (instance == null)
            {
                reason = "no item instance to add";
                return false;
            }

            ItemData definition = instance.Definition;

            if (!instance.IsEquippable)
            {
                reason = $"{definition.displayName} [{instance.UidText}] is carried rather than worn: this slice " +
                         "keeps currency and consumables on the definition path, so placing it as an instance " +
                         "would discard its rarity and rolls";
                return false;
            }

            if (instance.Quantity != 1)
            {
                reason = $"{definition.displayName} [{instance.UidText}] is worn gear, so one instance is exactly " +
                         $"one unit (got {instance.Quantity})";
                return false;
            }

            // R3: an instance is admitted only against this bag's catalog (a bag with no catalog admits nothing),
            // and it is re-validated against the catalog's one authority: the definition must be the catalog's own
            // object and every affix id, key, band, step and category must be the catalog's. A hand-built instance
            // carrying a forged roll is refused here, with the bag untouched.
            if (_catalog == null)
            {
                reason = $"{definition.displayName} [{instance.UidText}] cannot be admitted: this bag has no item catalog " +
                         "(Inventory.SetCatalog) to validate it against";
                return false;
            }

            if (!_catalog.Contains(definition))
            {
                reason = $"{definition.displayName} [{instance.UidText}] is built from a definition that is not an " +
                         "entry of this bag's item catalog";
                return false;
            }

            if (!ItemInstance.TryValidateSnapshot(instance.ToSnapshot(), _catalog.Authority, out string validationReason))
            {
                reason = $"{definition.displayName} [{instance.UidText}] does not validate against the item catalog: {validationReason}";
                return false;
            }

            EnsureGrid();

            if (HoldsInstanceUid(instance.Uid))
            {
                reason = $"this bag already carries {definition.displayName} [{instance.UidText}]: one uid is one item";
                return false;
            }

            Equipment partner = EquipmentPartner();
            if (partner != null && partner.HoldsUid(instance.Uid))
            {
                reason = $"{definition.displayName} [{instance.UidText}] is already worn by this player: " +
                         "one uid is one item";
                return false;
            }

            Vector2Int footprint = definition.EffectiveFootprint;
            Vector2Int landed = exactOrigin ? origin : FindFreeOrigin(footprint);

            if (landed.x < 0 || (exactOrigin && !CanPlace(landed, footprint, -1)))
            {
                reason = exactOrigin
                    ? $"no room for {definition.displayName} [{instance.UidText}] at cell {origin}"
                    : $"no {footprint.x}x{footprint.y} hole for {definition.displayName} [{instance.UidText}] " +
                      $"({FreeCells} free cells)";
                return false;
            }

            if (!CanCommitRevision(out reason)) return false;

            AddStack(definition, instance, 1, landed);
            Recompute();

            TotalUnits += 1;
            CarriedValue += definition.value;

            CommitRevision();

            // An instance arriving from outside is loot, exactly like the definition path: the starting kit is
            // still not loot, and an internal transfer never reaches this method at all.
            if (!_givingStartingKit)
            {
                LifetimeCollected += 1;
                RecoveredValue += definition.value;
                GameEvents.RaiseItemCollected(OwnerId, definition, LifetimeCollected);
            }

            GameEvents.RaiseInventoryChanged(OwnerId);
            return true;
        }

        /// <summary>
        /// Read-only pre-check for a gear add: does this bag have a catalog, and is the definition one of its
        /// entries? Touches nothing and mints nothing.
        /// </summary>
        private bool CanMintGear(ItemData definition, out string reason)
        {
            if (_catalog == null)
            {
                reason = "this bag has no item catalog (Inventory.SetCatalog), so it cannot mint gear identities";
                return false;
            }

            return _catalog.CanMint(definition, out reason);
        }

        /// <summary>
        /// Mints the one Common instance a legacy gear add needs, through the run's catalog
        /// (<see cref="ItemCatalog.TryMintCommon"/>): a definition that is not a catalog entry mints nothing.
        /// A mint failure is a refusal of the whole add and rolls back every stack this call placed.
        /// </summary>
        private bool TryMintCommonInstance(ItemData definition, out ItemInstance instance, out string reason)
        {
            instance = null;

            if (definition == null)
            {
                reason = "no item definition to give an identity";
                return false;
            }

            if (_catalog == null)
            {
                reason = "this bag has no item catalog (Inventory.SetCatalog), so it cannot mint gear identities";
                return false;
            }

            if (_issuer == null) _issuer = ItemIdentityIssuer.CreateLocalTrusted();

            return _catalog.TryMintCommon(_issuer, definition, out instance, out reason);
        }

        /// <summary>
        /// Restores one saved item into the bag as itself: the snapshot is resolved by id through the bag's
        /// catalog (an unknown id or any tampered field refuses, and nothing is placed), then the restored
        /// instance goes through <see cref="TryAddInstance"/> with its saved UID and rolls. No mint, no reroll.
        /// </summary>
        public bool TryAddSnapshot(ItemSnapshot snapshot, out string reason)
        {
            if (_catalog == null)
            {
                reason = "this bag has no item catalog, so a snapshot cannot be resolved";
                return false;
            }

            if (!_catalog.TryRestore(snapshot, out ItemInstance restored, out reason)) return false;

            return TryAddInstance(restored, out reason);
        }

        // ------------------------------------------------------- container snapshot (R9)

        /// <summary>The container id the bag writes into its snapshot and expects when restoring.</summary>
        public const string SnapshotContainerId = "bag";

        /// <summary>
        /// The bag as a versioned, canonical, checksummed <see cref="ContainerSnapshot"/>: every gear stack as its
        /// instance snapshot (uid, rarity, exact rolled values) at its origin cell, every carried stack as item id +
        /// quantity with no uid. Read-only: nothing is minted, rerolled or changed. The same bag always produces the
        /// same snapshot and the same JSON bytes (entries are in row/column order).
        /// </summary>
        public ContainerSnapshot ToContainerSnapshot()
        {
            EnsureGrid();

            List<ContainerEntry> entries = new List<ContainerEntry>(_stacks.Count);
            for (int i = 0; i < _stacks.Count; i++)
            {
                Stack stack = _stacks[i];
                ItemData definition = stack != null ? stack.Definition : null;
                if (definition == null) continue;

                ItemSnapshot item = stack.Instance != null
                    ? stack.Instance.ToSnapshot()
                    : new ItemSnapshot { uid = "", itemId = definition.itemId, quantity = stack.Count, rarity = 0, affixes = new AffixSnapshot[0] };

                entries.Add(new ContainerEntry { slot = "", x = stack.Origin.x, y = stack.Origin.y, item = item });
            }

            ContainerSnapshot snapshot = new ContainerSnapshot
            {
                schemaVersion = ContainerSnapshot.CurrentSchemaVersion,
                kind = ContainerKinds.Bag,
                containerId = SnapshotContainerId,
                width = gridWidth,
                height = gridHeight,
                entries = entries.ToArray()
            };

            return snapshot.Seal();
        }

        /// <summary>
        /// Replaces the bag's contents with a saved container, exactly (same uids, same rarity and rolled values, same
        /// cells; nothing is minted or rerolled). All-or-nothing: the snapshot is fully validated against this bag's
        /// catalog first (see <see cref="ContainerSnapshotValidator"/>) and a refusal leaves the bag byte-identical.
        /// A catalog is mandatory; the snapshot's grid must equal this bag's grid; a uid this player's equipment
        /// already wears is refused (restore bag and equipment together with <see cref="LoadoutSnapshot"/>), and
        /// <paramref name="others"/> adds the uids held by other containers of the profile (for example the stash).
        /// The lifetime/recovered-value counters are run statistics and are left as they are; TotalUnits and
        /// CarriedValue are recomputed from the restored content.
        /// </summary>
        public bool TryRestore(ContainerSnapshot snapshot, out string reason)
        {
            return TryRestore(snapshot, null, out reason);
        }

        public bool TryRestore(ContainerSnapshot snapshot, ItemUidRegistry others, out string reason)
        {
            if (!TryPrepareRestore(snapshot, others, true, out ValidatedContainer validated, out reason)) return false;

            if (!CanCommitRevision(out reason)) return false;

            // The ledger passed in is the profile's: it is updated together with the restore (it was already checked).
            if (others != null && !others.TryReplaceOwner(validated.ContainerId, validated.Uids, out reason)) return false;

            ApplyRestore(validated);
            CommitRevision();
            NotifyRestore();
            return true;
        }

        /// <summary>Validation half of a restore: touches nothing.</summary>
        internal bool TryPrepareRestore(ContainerSnapshot snapshot, ItemUidRegistry others, bool checkPartner,
            out ValidatedContainer validated, out string reason)
        {
            validated = null;

            if (_catalog == null)
            {
                reason = "this bag has no item catalog (Inventory.SetCatalog), so a container snapshot cannot be validated";
                return false;
            }


            if (!ContainerSnapshotValidator.TryValidate(snapshot, _catalog, others, ContainerKinds.Bag, out validated, out reason))
            {
                validated = null;
                return false;
            }

            if (validated.ContainerId != SnapshotContainerId)
            {
                validated = null;
                reason = $"this bag restores container '{SnapshotContainerId}', not '{snapshot.containerId}'";
                return false;
            }

            if (validated.Width != gridWidth || validated.Height != gridHeight)
            {
                reason = $"the snapshot is a {validated.Width}x{validated.Height} grid but this bag is {gridWidth}x{gridHeight}";
                validated = null;
                return false;
            }

            if (!CanAllocateStackIds(validated.Entries.Count))
            {
                reason = "this bag has exhausted its nonzero runtime stack identity space";
                validated = null;
                return false;
            }

            if (checkPartner)
            {
                Equipment partner = EquipmentPartner();
                if (partner != null)
                {
                    for (int i = 0; i < validated.Uids.Count; i++)
                    {
                        if (partner.HoldsUid(validated.Uids[i]))
                        {
                            reason = $"uid {validated.Uids[i]:N} is already worn by this player's equipment: one uid is one item";
                            validated = null;
                            return false;
                        }
                    }
                }
            }

            reason = null;
            return true;
        }

        /// <summary>
        /// Apply half of a restore. Cannot fail (everything was resolved and checked by <see cref="TryPrepareRestore"/>)
        /// and raises NO event, so a loadout restore can apply both containers before anyone is told.
        /// </summary>
        internal void ApplyRestore(ValidatedContainer validated)
        {
            // D3c: the belt is read as item identities while the old stacks still exist, and bound again to the restored stacks below.
            BeltIdentity[] belt = Hotbar.CaptureIdentities();

            _stacks.Clear();

            int units = 0;
            int value = 0;
            for (int i = 0; i < validated.Entries.Count; i++)
            {
                ValidatedEntry entry = validated.Entries[i];
                _stacks.Add(new Stack
                {
                    StackId = AllocateStackId(),
                    Item = entry.Definition,
                    Instance = entry.Instance,
                    Count = entry.Quantity,
                    Origin = entry.Origin
                });
                units += entry.Quantity;
                value += entry.Definition.value * entry.Quantity;
            }

            TotalUnits = units;
            CarriedValue = value;
            Recompute();

            // R6c2: every restored stack has a fresh StackId, so no old BINDING can survive. D3c: the belt does - by item identity
            // (uid or definition), never by index: each key is bound again to the restored stack that carries what it pointed at.
            // A belt that held nothing before the restore (a fresh bag) gets the default assignment, as before. No event is
            // raised here (see the doc above); NotifyRestore's InventoryChanged refreshes the HUD.
            if (!Hotbar.RebindFromIdentities(belt)) Hotbar.SeedDefaults();
        }

        /// <summary>Notify half of a restore: a throwing subscriber is logged and cannot undo or abort an applied restore.</summary>
        internal void NotifyRestore()
        {
            try { GameEvents.RaiseInventoryChanged(OwnerId); }
            catch (Exception e) { Debug.LogException(e); }
        }

        /// <summary>
        /// The equipment that shares this identity boundary, when there is one. A bag on its own (a geometry
        /// probe, a container fixture) has none, and that is not an error.
        /// </summary>
        private Equipment EquipmentPartner()
        {
            if (_equipment != null) return _equipment;
            if (_rig != null) _equipment = _rig.Equipment;
            if (_equipment == null) _equipment = GetComponent<Equipment>();
            return _equipment;
        }

        /// <summary>Takes back the stacks one add call appended, so a refusal mid-loop leaves nothing behind.</summary>
        private void RollbackCreatedStacks(int createdStacks)
        {
            if (createdStacks <= 0) return;
            _stacks.RemoveRange(_stacks.Count - createdStacks, createdStacks);
            Recompute();
        }

        // ------------------------------------------------------------ moving

        /// <summary>True when a size x size block fits entirely inside the grid without covering another item.</summary>
        public bool CanPlace(Vector2Int origin, Vector2Int size, int ignoreIndex = -1, int alsoIgnore = -1)
        {
            EnsureGrid();

            int width = Mathf.Max(1, size.x);
            int height = Mathf.Max(1, size.y);

            if (origin.x < 0 || origin.y < 0) return false;
            if (origin.x > gridWidth - width || origin.y > gridHeight - height) return false; // overflow-safe: no origin + size sum

            for (int y = origin.y; y < origin.y + height; y++)
            {
                for (int x = origin.x; x < origin.x + width; x++)
                {
                    int occupant = _cells[y * gridWidth + x];
                    if (occupant < 0) continue;
                    if (occupant == ignoreIndex || occupant == alsoIgnore) continue;
                    return false;
                }
            }

            return true;
        }

        /// <summary>First free hole that can hold the shape, scanning row by row. (-1,-1) when there is none.</summary>
        public Vector2Int FindFreeOrigin(Vector2Int size)
        {
            return FindFreeOriginIgnoring(size, -1);
        }

        /// <summary>
        /// Same scan with one stack treated as absent. This is what lets a replacement be planned: the hole
        /// the incoming unit is about to vacate is a legal destination for the item leaving the slot.
        /// </summary>
        private Vector2Int FindFreeOriginIgnoring(Vector2Int size, int ignoreIndex)
        {
            EnsureGrid();

            int width = Mathf.Max(1, size.x);
            int height = Mathf.Max(1, size.y);

            for (int y = 0; y + height <= gridHeight; y++)
            {
                for (int x = 0; x + width <= gridWidth; x++)
                {
                    if (CanPlace(new Vector2Int(x, y), size, ignoreIndex)) return new Vector2Int(x, y);
                }
            }

            return new Vector2Int(-1, -1);
        }

        /// <summary>Moves an existing stack to a new cell. The stack keeps its count and its shape.</summary>
        public bool TryMove(int index, Vector2Int origin, out string reason)
        {
            reason = null;
            EnsureGrid();

            if (index < 0 || index >= _stacks.Count)
            {
                reason = "no item there";
                return false;
            }

            Stack stack = _stacks[index];
            bool changed = stack.Origin != origin;
            Vector2Int size = stack.Definition.EffectiveFootprint;

            if (!CanPlace(origin, size, index))
            {
                reason = $"{stack.Definition.displayName} does not fit at cell {origin}";
                return false;
            }

            if (changed && !CanCommitRevision(out reason)) return false;

            stack.Origin = origin;
            Recompute();
            if (changed) CommitRevision();
            GameEvents.RaiseInventoryChanged(OwnerId);
            return true;
        }

        /// <summary>Swaps two stacks by exchanging their cells, which is what dragging one item onto another does.</summary>
        public bool TrySwap(int a, int b, out string reason)
        {
            reason = null;
            EnsureGrid();

            if (a < 0 || a >= _stacks.Count || b < 0 || b >= _stacks.Count || a == b)
            {
                reason = "cannot swap those slots";
                return false;
            }

            Stack first = _stacks[a];
            Stack second = _stacks[b];

            Vector2Int firstOrigin = first.Origin;
            Vector2Int secondOrigin = second.Origin;

            // The two shapes each item would wear after the exchange, measured the same way the rest of the
            // bag measures a footprint and never smaller than a single cell.
            Vector2Int declaredFirstShape = first.Definition != null ? first.Definition.EffectiveFootprint : Vector2Int.one;
            Vector2Int declaredSecondShape = second.Definition != null ? second.Definition.EffectiveFootprint : Vector2Int.one;
            Vector2Int firstShape = new Vector2Int(Mathf.Max(1, declaredFirstShape.x), Mathf.Max(1, declaredFirstShape.y));
            Vector2Int secondShape = new Vector2Int(Mathf.Max(1, declaredSecondShape.x), Mathf.Max(1, declaredSecondShape.y));

            // Destination rectangles, computed before anything is touched: the first item lands on the second
            // one's cell with its own shape, the second lands on the first one's cell with its own shape.
            // Upper edges are exclusive, so footprints that merely share a border are legal neighbours.
            int firstDestMinX = secondOrigin.x;
            int firstDestMaxX = secondOrigin.x + firstShape.x;
            int firstDestMinY = secondOrigin.y;
            int firstDestMaxY = secondOrigin.y + firstShape.y;
            int secondDestMinX = firstOrigin.x;
            int secondDestMaxX = firstOrigin.x + secondShape.x;
            int secondDestMinY = firstOrigin.y;
            int secondDestMaxY = firstOrigin.y + secondShape.y;

            // Both shapes have to fit in the other's hole, so a long sword cannot trade places with a coin
            // that only covers one of its cells.
            // A swap lifts both items before it checks, so each shape is measured against the other's hole
            // with both stacks treated as absent. Without that, two items of the same shape could never
            // trade places because each one's hole is still recorded as occupied by the other.
            // Lifting both items is only half the question: the two holes are still checked against every
            // third stack in the bag, and the two destination rectangles are checked against each other.
            // Two unequal shapes can each fit in the other's hole on their own and still cover the same
            // cell once both are put down, which is an overlap the grid may never hold.
            if (!CanPlace(secondOrigin, firstShape, a, b) ||
                !CanPlace(firstOrigin, secondShape, b, a))
            {
                reason = $"{first.Definition.displayName} and {second.Definition.displayName} cannot trade places";
                return false;
            }

            bool destinationsCollide = firstDestMinX < secondDestMaxX && secondDestMinX < firstDestMaxX &&
                                       firstDestMinY < secondDestMaxY && secondDestMinY < firstDestMaxY;

            if (destinationsCollide)
            {
                string firstName = first.Item != null ? first.Item.displayName : "item";
                string secondName = second.Item != null ? second.Item.displayName : "item";
                int overlapX = Mathf.Min(firstDestMaxX, secondDestMaxX) - Mathf.Max(firstDestMinX, secondDestMinX);
                int overlapY = Mathf.Min(firstDestMaxY, secondDestMaxY) - Mathf.Max(firstDestMinY, secondDestMinY);
                reason = $"{firstName} at {secondOrigin} ({firstShape.x}x{firstShape.y}) and {secondName} at {firstOrigin} " +
                         $"({secondShape.x}x{secondShape.y}) would cover the same {overlapX}x{overlapY} cells";
                return false;
            }

            if (!CanCommitRevision(out reason)) return false;

            first.Origin = secondOrigin;
            second.Origin = firstOrigin;
            Recompute();
            CommitRevision();
            GameEvents.RaiseInventoryChanged(OwnerId);
            return true;
        }

        /// <summary>
        /// Combines two stacks of the same item: units move out of <paramref name="sourceIndex"/> only into the
        /// headroom <paramref name="targetIndex"/> actually has, whatever will not fit stays at the source, and
        /// only a source that runs out is removed. A stack that survives keeps its own cell.
        ///
        /// This is rearranging what the player already owns. Nothing enters or leaves the bag, so TotalUnits and
        /// CarriedValue are untouched, LifetimeCollected and RecoveredValue are untouched, no ItemCollected is
        /// raised, and the one notification an inventory change owes its observers is raised exactly once.
        /// A refusal touches nothing and notifies nothing.
        /// </summary>
        public bool TryMergeStacks(int sourceIndex, int targetIndex, out string reason)
        {
            reason = null;
            EnsureGrid();

            if (sourceIndex < 0 || sourceIndex >= _stacks.Count || targetIndex < 0 || targetIndex >= _stacks.Count)
            {
                reason = "no item in those slots";
                return false;
            }

            if (sourceIndex == targetIndex)
            {
                reason = "that is already one stack";
                return false;
            }

            Stack source = _stacks[sourceIndex];
            Stack target = _stacks[targetIndex];

            if (source == null || source.Definition == null || source.Count <= 0)
            {
                reason = "nothing to combine";
                return false;
            }

            if (target == null || target.Definition == null || target.Count <= 0)
            {
                reason = "no item to combine into";
                return false;
            }

            // Gear never merges by definition. Two pieces that share a base archetype are two items with two
            // identities (and, once rolled, two different sets of rolls); combining them would either discard
            // one identity or invent a second one, and minting is not this helper's job.
            if (source.Instance != null || target.Instance != null)
            {
                reason = $"{DescribeStack(source)} and {DescribeStack(target)} are separate items: " +
                         "worn or held gear is never merged into another stack";
                return false;
            }

            if (source.Definition != target.Definition)
            {
                reason = $"{source.Definition.displayName} and {target.Definition.displayName} are not the same item";
                return false;
            }

            int limit = target.Definition.StackLimitForFootprint;
            int space = limit - target.Count;

            if (space <= 0)
            {
                reason = $"{target.Definition.displayName} stack is full ({target.Count}/{limit})";
                return false;
            }

            int moved = Mathf.Min(space, source.Count);
            if (!CanCommitRevision(out reason)) return false;
            target.Count += moved;
            source.Count -= moved;

            // Only the stack that ran out is taken off the grid. The remainder keeps its own cell, so a
            // partial combine neither duplicates the item nor frees a cell something else could claim.
            if (source.Count <= 0) _stacks.RemoveAt(sourceIndex);

            Recompute();
            CommitRevision();
            GameEvents.RaiseInventoryChanged(OwnerId);
            return true;
        }

        // ---------------------------------------------------- equipment transfers

        /// <summary>
        /// The bag half of an equipment change, planned before any of it is committed.
        ///
        /// One unit leaves <paramref name="bagIndex"/>, and the stack there is checked against the exact
        /// instance the equipment side resolved: the identity is what is transferred, so two stacks of one
        /// definition can never be confused for each other. When the player is replacing something the item
        /// they are taking off lands in a hole that only exists because the incoming unit is about to leave.
        /// The hole is found first: if there is none the bag is untouched and the caller must not change its
        /// side either. A stack that survives the removal keeps every cell it already had, so only a stack of
        /// one unit opens a hole.
        ///
        /// The outgoing instance is the one the equipment slot is releasing. Its ownership moves with it: the
        /// same object is placed in the bag in the same commit that hands the slot to the incoming instance,
        /// so no moment exists where the item is in two places or in none. No second identity is minted and
        /// nothing is rerolled on the way.
        ///
        /// Nothing here is newly collected loot: the collection counters stay where they were, no
        /// ItemCollected event is raised, and no notification at all is raised.
        ///
        /// Internal and deliberately silent. The bag half is only half of an equipment change, and an observer
        /// notified here would see the incoming item gone from the bag while the old gear is still worn and
        /// the stat source is still stale. Equipment therefore finishes its own slot/hand/stat commit first
        /// and raises the single InventoryChanged notification once both halves exist. A refusal touches
        /// nothing and notifies nothing.
        /// </summary>
        internal bool TryEquipTransfer(int bagIndex, ItemInstance expectedIncoming, ItemInstance outgoing,
            out string reason)
        {
            reason = null;

            EnsureGrid();

            if (bagIndex < 0 || bagIndex >= _stacks.Count)
            {
                reason = "no item in that bag slot";
                return false;
            }

            Stack taken = _stacks[bagIndex];
            if (taken == null || taken.Definition == null || taken.Count <= 0)
            {
                reason = "that bag slot holds nothing";
                return false;
            }

            if (expectedIncoming == null)
            {
                reason = "no item to take out of the bag";
                return false;
            }

            ItemInstance incoming = taken.Instance;

            if (incoming == null)
            {
                reason = $"the {taken.Definition.displayName} in that bag slot carries no item identity to wear";
                return false;
            }

            if (incoming.Uid != expectedIncoming.Uid)
            {
                reason = $"that bag slot holds {incoming.Definition.displayName} [{incoming.UidText}], not " +
                         $"{expectedIncoming.Definition.displayName} [{expectedIncoming.UidText}]";
                return false;
            }

            ItemData incomingDefinition = incoming.Definition;
            ItemData outgoingDefinition = outgoing != null ? outgoing.Definition : null;
            bool opensHole = taken.Count == 1;
            Vector2Int outgoingOrigin = new Vector2Int(-1, -1);

            if (outgoing != null)
            {
                if (outgoingDefinition == null)
                {
                    reason = "the item being taken off has no item definition to put back in the bag";
                    return false;
                }

                // The identity being released must not already be carried somewhere else in this bag, or the
                // transfer would end with one uid in two stacks.
                int alreadyCarried = IndexOfInstance(outgoing.Uid);
                if (alreadyCarried >= 0)
                {
                    reason = $"this bag already carries {outgoingDefinition.displayName} [{outgoing.UidText}] " +
                             $"in stack {alreadyCarried}, so the worn copy cannot be put back into it";
                    return false;
                }

                Vector2Int outgoingFootprint = outgoingDefinition.EffectiveFootprint;
                outgoingOrigin = opensHole
                    ? FindFreeOriginIgnoring(outgoingFootprint, bagIndex)
                    : FindFreeOrigin(outgoingFootprint);

                if (outgoingOrigin.x < 0)
                {
                    reason = $"no {outgoingFootprint.x}x{outgoingFootprint.y} hole for {outgoingDefinition.displayName}, and taking {incomingDefinition.displayName} out of the bag does not open one";
                    return false;
                }
            }

            // Planned. Allocate any returned item's new stack identity before touching the incoming stack, so
            // even identity-space exhaustion cannot leave half of this transfer committed.
            ulong outgoingStackId = outgoing != null ? AllocateStackId() : 0;

            // Commit both moves together; the notification waits for the equipment side to commit.
            if (opensHole)
            {
                if (outgoing != null)
                {
                    // The Stack object is reused for the outgoing gear, but it is a different exact item.
                    // Retire the incoming stack identity and give the outgoing gear its newly allocated one.
                    taken.StackId = outgoingStackId;
                    taken.Item = outgoingDefinition;
                    taken.Instance = outgoing;
                    taken.Origin = outgoingOrigin;
                }
                else
                {
                    _stacks.RemoveAt(bagIndex);
                }
            }
            else
            {
                taken.Count -= 1;
                if (outgoing != null) AddStack(outgoingDefinition, outgoing, 1, outgoingOrigin, outgoingStackId);
            }

            Recompute();

            TotalUnits = Mathf.Max(0, TotalUnits - 1);
            CarriedValue = Mathf.Max(0, CarriedValue - incomingDefinition.value);

            if (outgoing != null)
            {
                TotalUnits += 1;
                CarriedValue += outgoingDefinition.value;
            }

            // Silent on purpose: Equipment raises the one notification after its side is committed.
            return true;
        }

        /// <summary>
        /// Puts an item the player is already wearing back into the bag, carrying the very instance the slot
        /// held: same uid, same rarity, same rolls, no mint and no copy. Planned first: when no hole of the
        /// item's own shape exists the bag is untouched, so the caller keeps the item in its slot instead of
        /// deleting it. Moving already-owned gear is not loot, so LifetimeCollected, RecoveredValue and
        /// ItemCollected are deliberately untouched.
        ///
        /// Internal and silent for the same reason as the equip half: the outgoing item would otherwise be
        /// visible in the bag while it is still worn. Equipment clears the slot, restores the hand, refreshes
        /// the stat source and only then raises the one InventoryChanged notification.
        /// </summary>
        internal bool TryUnequipTransfer(ItemInstance instance, out string reason)
        {
            reason = null;

            if (instance == null || instance.Definition == null)
            {
                reason = "nothing to put back in the bag";
                return false;
            }

            ItemData item = instance.Definition;

            EnsureGrid();

            if (HoldsInstanceUid(instance.Uid))
            {
                reason = $"this bag already carries {item.displayName} [{instance.UidText}]: " +
                         "one uid is one item, so the worn copy is not added a second time";
                return false;
            }

            Vector2Int footprint = item.EffectiveFootprint;
            Vector2Int origin = FindFreeOrigin(footprint);
            if (origin.x < 0)
            {
                reason = $"bag has no {footprint.x}x{footprint.y} hole for {item.displayName}";
                return false;
            }

            AddStack(item, instance, 1, origin);
            Recompute();

            TotalUnits += 1;
            CarriedValue += item.value;

            // Silent on purpose: Equipment raises the one notification after its side is committed.
            return true;
        }

        // ------------------------------------------------------------ removing

        /// <summary>Takes units out of a stack. Returns how many actually left; an empty stack is removed.</summary>
        public int TryRemoveAt(int index, int amount)
        {
            EnsureGrid();

            if (index < 0 || index >= _stacks.Count || amount <= 0) return 0;

            Stack stack = _stacks[index];
            int removed = Mathf.Min(amount, stack.Count);
            if (removed <= 0 || !CanCommitRevision(out _)) return 0;
            stack.Count -= removed;

            // A stack that runs out takes its identity with it: no stack, no uid, no stale reference left in a
            // view or a cell map. Gear is one unit per stack, so removing a gear unit removes the whole stack.
            if (stack.Count <= 0) _stacks.RemoveAt(index);

            Recompute();
            TotalUnits = Mathf.Max(0, TotalUnits - removed);
            CarriedValue = Mathf.Max(0, CarriedValue - stack.Definition.value * removed);
            CommitRevision();
            GameEvents.RaiseInventoryChanged(OwnerId);
            return removed;
        }

        /// <summary>Consumes an exact live stack silently for authority-owned effects, committing one revision.</summary>
        internal bool TryConsumeStackSilently(ulong stackId, int amount, ItemData expectedDefinition,
            out ItemData consumedDefinition, out string reason)
        {
            consumedDefinition = null;
            EnsureGrid();
            int index = IndexOfStack(stackId);
            if (index < 0 || index >= _stacks.Count)
            {
                reason = "source stack identity is unknown or retired";
                return false;
            }
            if (amount != 1)
            {
                reason = "authority use consumes exactly one item";
                return false;
            }

            Stack stack = _stacks[index];
            if (stack == null || stack.Count < 1 || stack.Definition == null || stack.Definition != expectedDefinition)
            {
                reason = "the live stack no longer matches the requested item";
                return false;
            }
            if (!CanCommitRevision(out reason)) return false;

            consumedDefinition = stack.Definition;
            stack.Count--;
            if (stack.Count <= 0) _stacks.RemoveAt(index);
            Recompute();
            TotalUnits = Mathf.Max(0, TotalUnits - 1);
            CarriedValue = Mathf.Max(0, CarriedValue - consumedDefinition.value);
            CommitRevision();
            reason = null;
            return true;
        }

        /// <summary>Publishes the inventory half after a silent authority-owned consume and its effect commit.</summary>
        internal void NotifyAuthorityMutation() => GameEvents.RaiseInventoryChanged(OwnerId);

        public int IndexOfItem(ItemData item)
        {
            if (item == null) return -1;
            for (int i = 0; i < _stacks.Count; i++) if (_stacks[i].Definition == item) return i;
            return -1;
        }

        public int IndexAtCell(Vector2Int cell)
        {
            EnsureGrid();
            if (cell.x < 0 || cell.y < 0 || cell.x >= gridWidth || cell.y >= gridHeight) return -1;
            return _cells[cell.y * gridWidth + cell.x];
        }

        public InventorySlot SlotAtCell(Vector2Int cell)
        {
            int index = IndexAtCell(cell);
            return index >= 0 ? _views[index] : default;
        }

        public bool IsCellOccupied(Vector2Int cell) => IndexAtCell(cell) >= 0;

        /// <summary>
        /// Adds units to an existing stack, limited by that item's stack limit. Used when two stacks of the
        /// same item are combined on the grid.
        /// </summary>
        public int TryAddToSlot(int index, int amount)
        {
            EnsureGrid();

            if (index < 0 || index >= _stacks.Count || amount <= 0) return 0;

            Stack stack = _stacks[index];
            if (stack.Definition == null || !stack.Definition.CanStack) return 0;
            // An instance-backed stack is one concrete item: it does not take on units of a lookalike.
            if (stack.Instance != null) return 0;

            int space = stack.Definition.StackLimitForFootprint - stack.Count;
            int added = Mathf.Min(space, amount);
            if (added <= 0) return 0;

            if (!CanCommitRevision(out _)) return 0;

            stack.Count += added;

            Recompute();
            CommitRevision();
            GameEvents.RaiseInventoryChanged(OwnerId);
            return added;
        }

        // ------------------------------------------------------------ using

        /// <summary>
        /// Index-addressed use, kept for existing callers and fixtures. Production hotbar input no longer uses it
        /// (see <see cref="TryUseStack"/> and <see cref="Hotbar"/>): an index silently retargets when the list compacts.
        /// </summary>
        public bool TryUseSlot(int index)
        {
            if (index < 0 || index >= _stacks.Count) return false;
            return SubmitUse(GetOrCreateLocalAuthority(), _views[index]);
        }

        /// <summary>
        /// R6c2: uses exactly one unit of the live stack with this identity through the same authority Use
        /// command as <see cref="TryUseSlot"/> (stack ID plus the exact gear-UID precondition). A zero, unknown or
        /// retired ID does nothing, and no other stack - including a same-definition lookalike - can answer for it.
        /// </summary>
        public bool TryUseStack(ulong stackId)
        {
            if (stackId == 0 || !TryGetStack(stackId, out InventorySlot slot)) return false;
            return SubmitUse(GetOrCreateLocalAuthority(), slot);
        }

        private bool SubmitUse(LocalInventoryAuthority authority, InventorySlot slot)
        {
            if (authority == null || slot.StackId == 0 ||
                !authority.TryCreateLocalCommand(InventoryCommandKind.Use, slot.StackId, 0,
                    Vector2Int.zero, EquipmentSlot.None,
                    slot.Instance != null ? slot.Instance.Uid : Guid.Empty, Guid.Empty,
                    out InventoryCommand command, out _)) return false;
            return authority.Submit(_rig, command).Accepted;
        }

        public void Clear()
        {
            bool changed = _stacks.Count > 0 || TotalUnits != 0 || CarriedValue != 0;
            if (changed && !CanCommitRevision(out string revisionReason))
            {
                Debug.LogWarning(revisionReason, this);
                return;
            }

            _stacks.Clear();
            TotalUnits = 0;
            CarriedValue = 0;
            Recompute();
            Hotbar.RetireAll();
            if (changed) CommitRevision();
            GameEvents.RaiseInventoryChanged(OwnerId);
        }

        // ------------------------------------------------------------ grid bookkeeping

        private void AddStack(ItemData item, ItemInstance instance, int count, Vector2Int origin)
        {
            AddStack(item, instance, count, origin, AllocateStackId());
        }

        /// <summary>Recreates a stack at a new grid position while preserving its live identity.</summary>
        private void AddStack(ItemData item, ItemInstance instance, int count, Vector2Int origin, ulong stackId)
        {
            if (stackId == 0) throw new ArgumentOutOfRangeException(nameof(stackId), "Live inventory stacks require a nonzero identity.");
            _stacks.Add(new Stack { StackId = stackId, Item = item, Instance = instance, Count = count, Origin = origin });
            Recompute();
        }

        private ulong AllocateStackId()
        {
            if (_stackIdsExhausted || _nextStackId == 0)
                throw new InvalidOperationException("This Inventory exhausted its nonzero stack identity space.");

            ulong id = _nextStackId;
            if (id == ulong.MaxValue) _stackIdsExhausted = true;
            else _nextStackId = id + 1;
            return id;
        }

        private bool CanAllocateStackIds(int count)
        {
            if (count <= 0) return true;
            if (_stackIdsExhausted || _nextStackId == 0) return false;
            return (ulong)(count - 1) <= ulong.MaxValue - _nextStackId;
        }

        /// <summary>How one stack is named in a refusal: its definition and, when it has one, its identity.</summary>
        private static string DescribeStack(Stack stack)
        {
            if (stack == null || stack.Definition == null) return "empty stack";
            return stack.Instance != null
                ? $"{stack.Definition.displayName} [{stack.Instance.UidText}]"
                : stack.Definition.displayName;
        }

        private void EnsureGrid()
        {
            gridWidth = Mathf.Max(1, gridWidth);
            gridHeight = Mathf.Max(1, gridHeight);

            if (_cells == null || _cells.Length != gridWidth * gridHeight)
            {
                System.Array.Resize(ref _cells, gridWidth * gridHeight);
            }
        }

        /// <summary>Rebuilds the cell occupancy map and the read-only slot view from the stacks.</summary>
        private void Recompute()
        {
            EnsureGrid();

            for (int i = 0; i < _cells.Length; i++) _cells[i] = -1;

            UsedCells = 0;
            _views.Clear();

            for (int i = 0; i < _stacks.Count; i++)
            {
                Stack stack = _stacks[i];
                if (stack == null || stack.Definition == null) continue;

                Vector2Int size = stack.Definition.EffectiveFootprint;
                _views.Add(new InventorySlot(stack.Definition, stack.Instance, stack.Count, stack.Origin, size, i,
                    stack.StackId));

                for (int y = stack.Origin.y; y < stack.Origin.y + size.y; y++)
                {
                    for (int x = stack.Origin.x; x < stack.Origin.x + size.x; x++)
                    {
                        if (x < 0 || y < 0 || x >= gridWidth || y >= gridHeight) continue;
                        if (_cells[y * gridWidth + x] >= 0) continue;
                        _cells[y * gridWidth + x] = i;
                        UsedCells++;
                    }
                }
            }
        }
    }
}
