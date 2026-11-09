using System.Collections.Generic;
using UnityEngine;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Items;
using Emberdelve.Stats;

namespace Emberdelve.Interaction
{
    /// <summary>Network bridge used by a client chest view; the server remains the only loot authority.</summary>
    public interface IChestInteractionAuthority
    {
        bool ChestStateReady { get; }
        bool RequestChestOpen(string chestId);
        bool BeginChestHold(string chestId);
        void ContinueChestHold(string chestId);
        void CancelChestHold(string chestId);
        bool CompleteChestHold(string chestId);
    }

    /// <summary>
    /// How a chest behaves and how good its table is. The variant is data on the component and decides the
    /// open rules and the blockout shape; it is never compared against an id or a display name.
    /// </summary>
    public enum ChestVariant
    {
        /// <summary>Quick, common, small table. Opened with one press.</summary>
        Supply = 0,
        /// <summary>Richer table, but it has to be held open, so it costs the player time in a hostile level.</summary>
        Ember = 1,
        /// <summary>Sealed until the run has proven itself: only opens once the player has recovered enough value.</summary>
        Sealed = 2,
    }

    /// <summary>
    /// A container in the level. It rolls its loot table once, with a seed that comes from the layout, and
    /// then hands those items to the run layer through <see cref="ILootHost"/>. Anything the player cannot
    /// carry stays in the chest, so a full bag is a real decision rather than a toast.
    ///
    /// The chest never touches the inventory directly and never decides whether the run succeeds: it asks the
    /// host to take each drop and keeps exactly what the host did not commit. A drop that landed in full leaves
    /// the chest, a drop that landed partly stays with only its unaccepted units, and a drop that landed
    /// nowhere stays untouched, so the units a chest rolled are the units it can still account for - opening a
    /// chest neither creates nor destroys loot, and reopening offers the remainder of the same roll.
    ///
    /// U2B9 added the gear half. A rolled drop <em>is</em> an <see cref="ItemInstance"/>: the chest holds that
    /// exact object in <see cref="Remaining"/>, offers it through <see cref="IInstanceLootHost"/> only, and on
    /// refusal puts the very same object back with the same UID, the same rarity and the same accepted affix
    /// records. It is never re-rolled on open, reopen, a full-bag retry or an inspection, never handed to the
    /// definition path (which would mint a different item), and never granted twice - the bag refuses an
    /// identity it already carries.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ChestInteractable : MonoBehaviour, IInteractable, IActorHoldDuration, IInteractableHoldLifecycle
    {
        [Header("Content")]
        [SerializeField] private ChestVariant variant = ChestVariant.Supply;
        [SerializeField] private LootTable lootTable;
        [Tooltip("Authored per-chest SALT from the layout. R5: it no longer decides the loot alone; the effective seed is " +
                 "derived from the raid seed, this chest's id and this salt (RaidSession.ChestSeed), so the same chest " +
                 "holds the same loot within one raid seed and different loot in different raids.")]
        [SerializeField] private int seed = 1;
        [SerializeField] private MonoBehaviour lootHost;

        [Header("Presentation")]
        [SerializeField] private Vector3 bodySize = new Vector3(1.1f, 0.75f, 0.75f);
        [SerializeField] private Transform lid;

        private readonly List<LootDrop> _remaining = new List<LootDrop>();
        private ILootHost _host;
        private bool _rolled;
        private bool _opened;
        private bool _opening;
        private bool _presentationOnly;
        private bool _replicatedStateKnown;
        private bool _replicatedRolled;
        private bool _replicatedOpened;
        private int _replicatedRemainingUnits;
        private uint _stateRevision;
        private int _takenCount;
        private int _takenValue;
        private int _leftBehind;
        private float _lidAngle;

        private const float LidOpenAngle = 115f;

        public ChestVariant Variant => variant;
        public LootTable LootTable => lootTable;
        public int Seed => seed;

        /// <summary>
        /// The chest's stable id for seed derivation: its GameObject name, which the spawner builds from variant, cell
        /// and layout index. Renaming a chest changes its loot, like changing its salt does.
        /// </summary>
        public string ChestId => name;
        public bool Opened => Emberdelve.Core.NetRole.IsClientOnly && _replicatedStateKnown ? _replicatedOpened : _opened;
        public bool IsRolled => Emberdelve.Core.NetRole.IsClientOnly && _replicatedStateKnown ? _replicatedRolled : _rolled;
        public uint StateRevision => _stateRevision;
        public IReadOnlyList<LootDrop> Remaining => _remaining;

        /// <summary>Units this chest has actually handed over, counted per unit rather than per rolled drop.</summary>
        public int TakenCount => _takenCount;

        /// <summary>Value of the units this chest actually handed over, not the value it once offered.</summary>
        public int TakenValue => _takenValue;

        /// <summary>Units still in the chest, summed over the drops that are left.</summary>
        public int RemainingUnits
        {
            get
            {
                if (Emberdelve.Core.NetRole.IsClientOnly && _replicatedStateKnown) return _replicatedRemainingUnits;
                int units = 0;
                for (int i = 0; i < _remaining.Count; i++) units += Mathf.Max(0, _remaining[i].Amount);
                return units;
            }
        }
        public bool IsPresentationOnly => _presentationOnly;

        /// <summary>Units the host could not take, across every open this chest has had.</summary>
        public int LeftBehindCount => _leftBehind;
        public Vector3 BodySize => bodySize;
        public Transform Lid => lid;

        /// <summary>Blockout shape and the block that swings open, wired by the spawner.</summary>
        public void ConfigureBody(Vector3 size, Transform lidTransform)
        {
            bodySize = size;
            lid = lidTransform;
        }

        public string PromptText
        {
            get
            {
                if (Emberdelve.Core.NetRole.IsClientOnly)
                {
                    if (!_replicatedStateKnown) return "Syncing chest...";
                    if (_replicatedRolled && _replicatedRemainingUnits == 0) return "Chest (empty)";
                    if (!_replicatedRolled) return RequiresHold ? "Hold to open chest" : "Open chest";
                }
                if (_remaining.Count == 0 && _rolled) return _opened ? "Chest (empty)" : "Open chest";
                return RequiresHold ? "Hold to open chest" : "Open chest";
            }
        }

        public bool RequiresHold => HoldDuration > 0f;

        public float HoldDuration
        {
            get
            {
                switch (variant)
                {
                    case ChestVariant.Ember: return 1.4f;
                    case ChestVariant.Sealed: return 2.2f;
                    default: return 0f;
                }
            }
        }

        /// <summary>Only chests opt into CHA timing. Instant supply chests remain instant.</summary>
        public float ResolveHoldDuration(IInteractionActor actor)
        {
            float rate = actor is IInteractionSpeedSource source ? source.InteractionSpeedFactor : 1f;
            return InteractionSpeedRules.Duration(HoldDuration, rate);
        }

        private void Awake()
        {
            _host = lootHost as ILootHost;
            if (_host == null) _host = GetComponentInParent<ILootHost>();
        }

        private void Update()
        {
            if (lid == null || !_opened) return;
            if (_lidAngle >= LidOpenAngle) return;

            _lidAngle = Mathf.Min(LidOpenAngle, _lidAngle + Time.deltaTime * 150f);
            lid.localRotation = Quaternion.Euler(-_lidAngle, 0f, 0f);
        }

        public void Configure(ChestVariant newVariant, LootTable table, int newSeed, MonoBehaviour host)
        {
            variant = newVariant;
            lootTable = table;
            seed = newSeed;
            lootHost = host;
            _host = host as ILootHost;
            if (_host == null) _host = GetComponentInParent<ILootHost>();
        }

        /// <summary>Marks a client replica as display/interaction geometry only; it can never roll or grant loot locally.</summary>
        public void SetPresentationOnly()
        {
            _presentationOnly = true;
            lootHost = null;
            _host = null;
            _remaining.Clear();
            _rolled = false;
            _opened = false;
        }

#if DEVELOPMENT_BUILD || UNITY_EDITOR
        /// <summary>Harness-only setup: seeds one real instance; opening and admission still use normal production logic.</summary>
        public bool InstallContestFixtureInstance(ItemInstance instance)
        {
            if (variant != ChestVariant.Supply || instance == null || !instance.IsEquippable || lootTable == null) return false;
            _remaining.Clear();
            _remaining.Add(new LootDrop(instance.Definition, 1, instance));
            _rolled = true;
            _opened = false;
            _opening = false;
            AdvanceStateRevision();
            return true;
        }
#endif

        public bool CanInteract(IInteractionActor actor, out string blockedReason)
        {
            if (Emberdelve.Core.NetRole.IsClientOnly)
            {
                if (!TryGetNetworkAuthority(actor, out IChestInteractionAuthority authority) || !authority.ChestStateReady)
                {
                    blockedReason = "Chest state is syncing";
                    return false;
                }
                if (!InteractionRules.Validate(actor, this, InteractionRules.ReachOf(_host), out blockedReason)) return false;
                if (_replicatedStateKnown && _replicatedRolled && _replicatedRemainingUnits == 0)
                {
                    blockedReason = "This chest is empty";
                    return false;
                }
            }
            else if (!InteractionRules.Validate(actor, this, InteractionRules.ReachOf(_host), _host, out blockedReason)) return false;

            if (lootTable == null)
            {
                blockedReason = "This chest has no contents configured";
                return false;
            }

            if (!Emberdelve.Core.NetRole.IsClientOnly && _rolled && _remaining.Count == 0)
            {
                blockedReason = "This chest is empty";
                return false;
            }

            if (variant == ChestVariant.Sealed)
            {
                // R2: the ACTOR's carried value unlocks it, not some other player's.
                int recovered = actor.Inventory != null ? actor.Inventory.RecoveredValue : 0;
                if (recovered < SealedUnlockValue)
                {
                    blockedReason = $"Sealed chest: recover {SealedUnlockValue} value first ({recovered})";
                    return false;
                }
            }

            blockedReason = null;
            return true;
        }

        public void OnInteractInstant(IInteractionActor actor) => Open(actor);

        public bool OnHoldStarted(IInteractionActor actor)
        {
            if (!Emberdelve.Core.NetRole.IsClientOnly) return true;
            return TryGetNetworkAuthority(actor, out IChestInteractionAuthority authority) && authority.BeginChestHold(ChestId);
        }

        public void OnHoldCanceled(IInteractionActor actor)
        {
            if (Emberdelve.Core.NetRole.IsClientOnly && TryGetNetworkAuthority(actor, out IChestInteractionAuthority authority))
                authority.CancelChestHold(ChestId);
        }

        public void OnHoldProgress(IInteractionActor actor)
        {
            if (Emberdelve.Core.NetRole.IsClientOnly && TryGetNetworkAuthority(actor, out IChestInteractionAuthority authority))
                authority.ContinueChestHold(ChestId);
        }

        public void OnHoldComplete(IInteractionActor actor)
        {
            if (!Emberdelve.Core.NetRole.IsClientOnly) { Open(actor); return; }
            if (TryGetNetworkAuthority(actor, out IChestInteractionAuthority authority)) authority.CompleteChestHold(ChestId);
        }

        /// <summary>
        /// Rolls the table once and hands each drop to the run host, counting the units the host actually
        /// committed. A drop the host took in full leaves the chest; a drop it only partly took stays with its
        /// unaccepted units and is offered again on the next open; a drop the host took none of stays untouched.
        /// Nothing is created or destroyed by opening a chest, the roll is never repeated, and the diagnostics
        /// describe what happened rather than what was attempted.
        ///
        /// A rolled gear drop moves as the instance the chest produced, or not at all: refusal leaves that exact
        /// object in <see cref="Remaining"/>, same UID, same rarity, same rolls, and there is no definition-shaped
        /// fallback that would mint a replacement item.
        ///
        /// The return value says whether the chest was opened, not whether it was emptied. A table whose
        /// profiles or catalog cannot be produced is a failed open with the reason logged: the chest does not
        /// mark itself rolled, so it does not pretend to be an empty container it never produced.
        /// </summary>
        public bool Open(IInteractionActor actor)
        {
            if (Emberdelve.Core.NetRole.IsClientOnly)
            {
                if (!CanInteract(actor, out string reason))
                {
                    GameEvents.RaiseToast(actor != null ? actor.PlayerId : GameEvents.NoPlayer, reason);
                    return false;
                }
                if (!TryGetNetworkAuthority(actor, out IChestInteractionAuthority authority) || !authority.RequestChestOpen(ChestId))
                {
                    GameEvents.RaiseToast(actor != null ? actor.PlayerId : GameEvents.NoPlayer, "Waiting for the server");
                    return false;
                }
                return true;
            }

            // Inventory notifications may synchronously trigger another interaction before the first transfer returns.
            if (_opening) return false;
            _opening = true;
            try
            {
                // Publish inventory notifications only after the source has recorded the transfer.
                using (GameEventTransaction.Defer()) return OpenCore(actor);
            }
            finally { _opening = false; }
        }

        /// <summary>Applies only server-authored presentation state; clients never receive or grant the chest's loot objects.</summary>
        public bool ApplyReplicatedState(string chestId, bool rolled, bool opened, int remainingUnits)
        {
            if (!string.Equals(ChestId, chestId, System.StringComparison.Ordinal) || remainingUnits < 0 || (opened && !rolled)) return false;
            _replicatedStateKnown = true;
            _replicatedRolled = rolled;
            _replicatedOpened = opened;
            _replicatedRemainingUnits = remainingUnits;
            _opened = opened;
            return true;
        }

        public static bool IsValidNetworkChestId(string chestId)
        {
            if (string.IsNullOrEmpty(chestId) || chestId.Length > 64) return false;
            for (int i = 0; i < chestId.Length; i++)
            {
                char c = chestId[i];
                if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '-'))
                    return false;
            }
            return true;
        }

        private static bool TryGetNetworkAuthority(IInteractionActor actor, out IChestInteractionAuthority authority)
        {
            authority = null;
            Component actorComponent = actor as Component;
            if (actorComponent == null) return false;
            MonoBehaviour[] behaviours = actorComponent.GetComponents<MonoBehaviour>();
            for (int i = 0; i < behaviours.Length; i++)
            {
                authority = behaviours[i] as IChestInteractionAuthority;
                if (authority != null) return true;
            }
            return false;
        }

        private bool OpenCore(IInteractionActor actor)
        {
            int owner = actor != null ? actor.PlayerId : GameEvents.NoPlayer;
            if (_presentationOnly)
            {
                GameEvents.RaiseToast(owner, "Chest contents are controlled by the server");
                return false;
            }
            if (!CanInteract(actor, out string reason))
            {
                GameEvents.RaiseToast(owner, reason);
                return false;
            }

            if (!_rolled)
            {
                // R3: gear is rolled against the run's one item catalog, which the host's bag carries.
                ItemCatalog catalog = actor.Inventory != null ? actor.Inventory.Catalog : null;

                // R4/R5: the roll belongs to the raid. Its seed is derived from the raid seed, and every UID comes from
                // the raid's one issuer; a host with no raid session cannot roll a chest at all (no local fallback).
                RaidSession session = (_host as IRaidSessionHost)?.Session;
                if (session == null)
                {
                    Debug.LogError($"[Emberdelve][Chest] {name}: the loot host has no raid session, so the chest " +
                                   "cannot be rolled (chests roll through RaidSession, never a private seed)", this);
                    GameEvents.RaiseToast(owner, "Chest contents are invalid");
                    return false;
                }

                if (!session.TryRollChest(lootTable, ChestId, seed, catalog, _remaining, out string rollReason))
                {
                    Debug.LogError($"[Emberdelve][Chest] {name} variant={variant} salt={seed}: contents refused, " +
                                    $"nothing was produced - {rollReason}", this);
                    GameEvents.RaiseToast(owner, "Chest contents are invalid");
                    return false;
                }

                _rolled = true;
                AdvanceStateRevision();
            }

            if (_host == null)
            {
                GameEvents.RaiseToast(owner, "No inventory available");
                return false;
            }

            int takenUnits = 0;
            int takenValue = 0;
            int leftUnits = 0;

            for (int i = _remaining.Count - 1; i >= 0; i--)
            {
                LootDrop drop = _remaining[i];
                if (drop.Item == null)
                {
                    _remaining.RemoveAt(i);
                    continue;
                }

                int offered = Mathf.Max(0, drop.Amount);
                ItemData definition = drop.Instance != null ? drop.Instance.Definition : drop.Item;
                int accepted;

                if (drop.IsRolled)
                {
                    // One concrete item, offered as itself. Refused means it stays here unchanged.
                    if (!LootCollection.TryCollectInstance(_host, actor, drop.Instance, out string refusal))
                    {
                        Debug.Log($"[Emberdelve][Chest] {name}: kept {drop} in the chest - {refusal}");
                        accepted = 0;
                    }
                    else
                    {
                        accepted = 1;
                    }
                }
                else
                {
                    accepted = LootCollection.CollectAccepted(_host, actor, drop.Item, offered);
                }

                if (accepted > 0)
                {
                    takenUnits += accepted;
                    takenValue += definition.value * accepted;
                    _takenCount += accepted;
                    _takenValue += definition.value * accepted;
                }

                int unpaid = offered - accepted;
                if (unpaid <= 0)
                {
                    // Taken in full: the drop is gone, and only a drop with nothing left in it leaves the chest.
                    _remaining.RemoveAt(i);
                    continue;
                }

                // Partial, or a refusal: the chest keeps exactly the units the host did not commit - same item,
                // same roll and, for rolled gear, the very same instance object with its own UID and records.
                _remaining[i] = new LootDrop(drop.Item, unpaid, drop.Instance);
                _leftBehind += unpaid;
                leftUnits += unpaid;
            }

            _opened = true;
            AdvanceStateRevision();

            Debug.Log($"[Emberdelve][Chest] {name} variant={variant} salt={seed} opened: takenUnits={takenUnits} " +
                      $"value={takenValue} leftUnits={leftUnits} stacks={_remaining.Count} " +
                      $"totalTaken={_takenCount} totalValue={_takenValue}");
            GameEvents.RaiseToast(owner, _remaining.Count > 0
                ? $"Took {takenUnits} item(s) worth {takenValue}, left {leftUnits} in the chest"
                : $"Took {takenValue} value from {variant} chest");

            return true;
        }

        /// <summary>True once the chest has been rolled and has nothing left to give.</summary>
        public bool IsEmpty => Emberdelve.Core.NetRole.IsClientOnly && _replicatedStateKnown
            ? _replicatedRolled && _replicatedRemainingUnits == 0
            : _rolled && _remaining.Count == 0;

        private void AdvanceStateRevision() { unchecked { _stateRevision++; } }

        /// <summary>Value the player must have recovered before a sealed chest will open.</summary>
        public const int SealedUnlockValue = 60;

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = variant == ChestVariant.Ember ? new Color(0.95f, 0.55f, 0.2f)
                            : variant == ChestVariant.Sealed ? new Color(0.55f, 0.45f, 0.9f)
                            : new Color(0.45f, 0.7f, 0.45f);
            Gizmos.DrawWireCube(transform.position + Vector3.up * (bodySize.y * 0.5f), bodySize);
        }
    }
}
