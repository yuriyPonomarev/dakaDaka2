using System.Collections.Generic;
using UnityEngine;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Items;

namespace Emberdelve.Interaction
{
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
    public sealed class ChestInteractable : MonoBehaviour, IInteractable
    {
        [Header("Content")]
        [SerializeField] private ChestVariant variant = ChestVariant.Supply;
        [SerializeField] private LootTable lootTable;
        [Tooltip("Stable per-chest seed from the layout, so the same chest in the same level always holds the same loot.")]
        [SerializeField] private int seed = 1;
        [SerializeField] private MonoBehaviour lootHost;

        [Header("Presentation")]
        [SerializeField] private Vector3 bodySize = new Vector3(1.1f, 0.75f, 0.75f);
        [SerializeField] private Transform lid;

        private readonly List<LootDrop> _remaining = new List<LootDrop>();
        private ILootHost _host;
        private bool _rolled;
        private bool _opened;
        private int _takenCount;
        private int _takenValue;
        private int _leftBehind;
        private float _lidAngle;

        private const float LidOpenAngle = 115f;

        public ChestVariant Variant => variant;
        public LootTable LootTable => lootTable;
        public int Seed => seed;
        public bool Opened => _opened;
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
                int units = 0;
                for (int i = 0; i < _remaining.Count; i++) units += Mathf.Max(0, _remaining[i].Amount);
                return units;
            }
        }

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

        public bool CanInteract(out string blockedReason)
        {
            if (lootTable == null)
            {
                blockedReason = "This chest has no contents configured";
                return false;
            }

            if (_rolled && _remaining.Count == 0)
            {
                blockedReason = "This chest is empty";
                return false;
            }

            if (variant == ChestVariant.Sealed)
            {
                int recovered = _host != null && _host.Inventory != null ? _host.Inventory.RecoveredValue : 0;
                if (recovered < SealedUnlockValue)
                {
                    blockedReason = $"Sealed chest: recover {SealedUnlockValue} value first ({recovered})";
                    return false;
                }
            }

            blockedReason = null;
            return true;
        }

        public void OnInteractInstant() => Open();

        public void OnHoldComplete() => Open();

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
        public bool Open()
        {
            if (!CanInteract(out string reason))
            {
                GameEvents.RaiseToast(reason);
                return false;
            }

            if (!_rolled)
            {
                // R3: gear is rolled against the run's one item catalog, which the host's bag carries.
                ItemCatalog catalog = _host != null && _host.Inventory != null ? _host.Inventory.Catalog : null;
                if (!lootTable.TryRoll(seed, _remaining, catalog, out string rollReason))
                {
                    Debug.LogError($"[Emberdelve][Chest] {name} variant={variant} seed={seed}: contents refused, " +
                                    $"nothing was produced - {rollReason}", this);
                    GameEvents.RaiseToast("Chest contents are invalid");
                    return false;
                }

                _rolled = true;
            }

            if (_host == null)
            {
                GameEvents.RaiseToast("No inventory available");
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
                    if (!LootCollection.TryCollectInstance(_host, drop.Instance, out string refusal))
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
                    accepted = LootCollection.CollectAccepted(_host, drop.Item, offered);
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

            Debug.Log($"[Emberdelve][Chest] {name} variant={variant} seed={seed} opened: takenUnits={takenUnits} " +
                      $"value={takenValue} leftUnits={leftUnits} stacks={_remaining.Count} " +
                      $"totalTaken={_takenCount} totalValue={_takenValue}");
            GameEvents.RaiseToast(_remaining.Count > 0
                ? $"Took {takenUnits} item(s) worth {takenValue}, left {leftUnits} in the chest"
                : $"Took {takenValue} value from {variant} chest");

            return true;
        }

        /// <summary>True once the chest has been rolled and has nothing left to give.</summary>
        public bool IsEmpty => _rolled && _remaining.Count == 0;

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
