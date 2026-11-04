using UnityEngine;
using Emberdelve.Core;
using Emberdelve.Data;

namespace Emberdelve.Items
{
    /// <summary>What a loot holder must provide so pickups can hand items to the player.</summary>
    public interface ILootHost
    {
        /// <summary>R2: takes the units for THIS actor's bag (the host keeps its own per-player record; it never falls back to a default player).</summary>
        bool CollectLoot(IInteractionActor actor, ItemData item, int amount);
        void SpawnLoot(ItemData item, int amount, Vector3 worldPosition);
    }

    /// <summary>
    /// The counted half of the loot contract, for a host that can say how much of an offer it actually
    /// committed. A source container (a chest, a pickup on the ground) has to decrement what it holds by the
    /// units that really landed, and <see cref="ILootHost.CollectLoot"/> cannot express a partial result:
    /// its bool is true only when everything landed, so a false leaves the container guessing.
    ///
    /// A host that only implements <see cref="ILootHost"/> keeps working unchanged; the containers fall back
    /// to measuring the bag that host reports, which is what <see cref="LootCollection"/> does explicitly
    /// rather than reading a false as zero.
    /// </summary>
    public interface ICountedLootHost : ILootHost
    {
        /// <summary>
        /// Commits as much of <paramref name="amount"/> as the host can carry and returns the exact number of
        /// units it took: 0 when it took none, <paramref name="amount"/> when it took all of them, and
        /// something in between when it took part. It never returns a negative number or more than was asked.
        /// </summary>
        int CollectLootCounted(IInteractionActor actor, ItemData item, int amount);
    }

    /// <summary>
    /// The optional instance half of the loot contract (U2B9): a host that can take an item that already
    /// exists - one concrete rolled gear item with its UID, its rarity and its accepted affix records - and
    /// take it as <em>that</em> item.
    ///
    /// It is deliberately separate from <see cref="ILootHost"/> and <see cref="ICountedLootHost"/> rather than
    /// an extra method on them: a host written before instances existed keeps compiling and keeps behaving
    /// exactly as it did, and a container can tell the two apart instead of guessing. A host that does not
    /// implement this interface is a refusal for a rolled drop, never a fallback - handing an already-rolled
    /// item to <see cref="ILootHost.CollectLoot"/> would take only its definition, mint a fresh Common
    /// identity and throw the rolled rarity and affixes away.
    /// </summary>
    public interface IInstanceLootHost : ILootHost
    {
        /// <summary>
        /// Takes this exact instance, or refuses and changes nothing. The instance the host accepts is the same
        /// object it was handed: no mint, no copy, no reroll, no quantity change. The reason says why a refusal
        /// happened (no room, a duplicate identity, a run state that may not receive loot, a host that does not
        /// carry instances at all).
        /// </summary>
        bool TryCollectLootInstance(IInteractionActor actor, ItemInstance instance, out string reason);
    }

    /// <summary>U5A: where a dropped item goes. The server lays the exact instance on the ground; false changes nothing (the caller puts the item back).</summary>
    public interface IGroundItemSink
    {
        bool TrySpawnDropped(ItemInstance instance, Vector3 position, out string reason);
    }

    /// <summary>
    /// R4: a host that owns enemy drops. The enemy reports where and what died (data + stable key); the host rolls the
    /// drop table with the raid's seed and issuer and spawns the result, gear as concrete instances. The enemy never
    /// rolls anything itself.
    /// </summary>
    public interface IEnemyDropHost : ILootHost
    {
        void DropEnemyLoot(EnemyData enemy, string dropKey, Vector3 position);
    }

    /// <summary>
    /// The one rule a loot source uses to find out how much of what it offered actually arrived, so a
    /// partial collection shrinks the source by exactly that much instead of keeping or discarding the whole
    /// offer.
    /// </summary>
    public static class LootCollection
    {
        /// <summary>
        /// Hands <paramref name="amount"/> units to the host and returns how many the host actually committed.
        ///
        /// Counted host: the host's own number, clamped into the range that was offered.
        ///
        /// Legacy bool-only host: the bool cannot name a count, so the count is read off the bag that host
        /// reports - the units the acting player's bag gained during the call are the units that
        /// were committed, and that is what the source is shrunk by. A bag that shrank or gained more than was
        /// offered is clamped, and a disagreement with the host's own bool is logged instead of being guessed.
        /// When the host reports no bag at all there is nothing to measure, and the only reading left is the
        /// documented bool contract (true = everything landed, false = nothing landed), which is exactly the
        /// pre-count behavior: explicit, and never a silent partial credit.
        /// </summary>
        public static int CollectAccepted(ILootHost host, IInteractionActor actor, ItemData item, int amount)
        {
            if (host == null || item == null) return 0;

            int requested = Mathf.Max(0, amount);
            if (requested <= 0) return 0;

            if (host is ICountedLootHost counted)
                return Mathf.Clamp(counted.CollectLootCounted(actor, item, requested), 0, requested);

            Inventory bag = actor != null ? actor.Inventory : null;
            if (bag == null) return host.CollectLoot(actor, item, requested) ? requested : 0;

            int before = bag.TotalUnits;
            bool tookAll = host.CollectLoot(actor, item, requested);
            int committed = Mathf.Clamp(bag.TotalUnits - before, 0, requested);

            if (tookAll && committed != requested)
            {
                Debug.LogWarning($"[Emberdelve][Loot] host {host.GetType().Name} answered true for {requested} " +
                                 $"{item.displayName} but its bag only gained {committed} units; the source is " +
                                 "shrunk by what the bag actually gained, not by the bool.");
            }

            return committed;
        }

        /// <summary>
        /// Hands one already-rolled instance to the host and says whether the host took it.
        ///
        /// A host that implements <see cref="IInstanceLootHost"/> decides for itself - the run host puts the very
        /// instance into the player's bag through the bag's own instance path, so the accepted item is the same
        /// object with the same UID, rarity and rolls.
        ///
        /// A host that does not is a refusal with a reason, and the source keeps the instance it already had.
        /// There is deliberately no fallback to <see cref="ILootHost.CollectLoot"/>: that path is given an
        /// <see cref="ItemData"/> and would remint a Common identity and discard the rolled metadata, which is
        /// the exact thing this contract exists to prevent.
        /// </summary>
        public static bool TryCollectInstance(ILootHost host, IInteractionActor actor, ItemInstance instance, out string reason)
        {
            if (instance == null)
            {
                reason = "no item instance to hand over";
                return false;
            }

            if (host == null)
            {
                reason = "no loot host: the rolled item has nowhere to go";
                return false;
            }

            if (host is IInstanceLootHost instanceHost)
            {
                if (instanceHost.TryCollectLootInstance(actor, instance, out reason)) return true;

                if (string.IsNullOrEmpty(reason))
                {
                    reason = $"host {host.GetType().Name} refused {instance.Describe()}";
                }
                return false;
            }

            reason = $"host {host.GetType().Name} does not implement IInstanceLootHost: " +
                     $"{instance.Definition.displayName} [{instance.UidText}] {instance.Rarity} is not handed to " +
                     "CollectLoot(ItemData), because that path mints a new Common identity and discards the " +
                     "rolled rarity and affixes";
            return false;
        }
    }

    /// <summary>
    /// A piece of loot lying in the world. Interacting with it asks the run layer to add the item to
    /// the inventory; the pickup only disappears when the whole stack landed, so a full bag is a real risk.
    /// A bag that took part of the stack keeps the remainder on the ground with the units that did not land,
    /// so a partial pickup neither duplicates what was carried nor silently throws the rest away, and nothing
    /// is re-rolled or re-created on the way.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class LootPickup : MonoBehaviour, IInteractable
    {
        [Header("Content")]
        [SerializeField] private ItemData item;
        [SerializeField] private int amount = 1;

        // R4: a pickup of worn gear carries the concrete instance (UID, rarity, rolled affixes) it was dropped as.
        // Not serialized: instances only exist at runtime, and a pickup placed in a scene is a plain definition pickup.
        private ItemInstance _instance;

        [Header("Wiring")]
        [Tooltip("Assigned to the RunDirector by the level builder; left empty the pickup searches its parents.")]
        [SerializeField] private MonoBehaviour lootHost;

        [Header("Presentation")]
        [SerializeField] private float bobAmplitude = 0.09f;
        [SerializeField] private float bobSpeed = 1.6f;
        [SerializeField] private float spinSpeed = 42f;

        private ILootHost _host;
        private bool _taken; // U5A: once an instance has been handed to a bag the pickup is spent, even within the frame Unity still lists it
        private Vector3 _home;
        private float _phase;

        public ItemData Item => _instance != null ? _instance.Definition : item;
        public int Amount => _instance != null ? 1 : Mathf.Max(1, amount);

        /// <summary>The concrete item this pickup holds, or null for a definition+amount pickup.</summary>
        public ItemInstance Instance => _instance;

        public string PromptText
        {
            get
            {
                ItemData shown = Item;
                if (shown == null) return "Take item";
                if (_instance != null) return $"Take {shown.displayName} ({_instance.Rarity})";
                return amount > 1 ? $"Take {shown.displayName} x{amount}" : $"Take {shown.displayName}";
            }
        }
        public bool RequiresHold => false;
        public float HoldDuration => 0f;

        private void Awake()
        {
            _host = lootHost as ILootHost;
            if (_host == null) _host = GetComponentInParent<ILootHost>();
            _home = transform.position;
            // Visual only, and derived from where the pickup lies rather than from a random source.
            Vector3 p = transform.position;
            ulong bits = RngMix.Combine(unchecked((ulong)(long)Mathf.RoundToInt(p.x * 64f)),
                                        unchecked((ulong)(long)Mathf.RoundToInt(p.z * 64f)));
            _phase = (bits >> 40) / 16777216f * Mathf.PI * 2f;
        }

        public void Configure(ItemData newItem, int newAmount, MonoBehaviour newHost)
        {
            _instance = null;
            item = newItem;
            amount = Mathf.Max(1, newAmount);
            lootHost = newHost;
            _host = newHost as ILootHost;
        }

        /// <summary>Configures this pickup as one concrete item: taking it hands the very same instance to the bag.</summary>
        public void Configure(ItemInstance instance, MonoBehaviour newHost)
        {
            _instance = instance;
            item = instance != null ? instance.Definition : null;
            amount = 1;
            lootHost = newHost;
            _host = newHost as ILootHost;
        }

        public bool CanInteract(IInteractionActor actor, out string reason)
        {
            if (!Emberdelve.Interaction.InteractionRules.Validate(actor, this,
                Emberdelve.Interaction.InteractionRules.ReachOf(_host), _host, out reason)) return false;

            if (Item == null)
            {
                reason = "Empty container";
                return false;
            }

            if (_host == null)
            {
                reason = "No inventory available";
                return false;
            }

            reason = null;
            return true;
        }

        public void OnInteractInstant(IInteractionActor actor)
        {
            if (!CanInteract(actor, out string reason))
            {
                GameEvents.RaiseToast(actor != null ? actor.PlayerId : GameEvents.NoPlayer, reason ?? "Cannot take item");
                return;
            }

            if (_instance != null)
            {
                if (_taken)
                {
                    GameEvents.RaiseToast(actor.PlayerId, "Already taken");
                    return;
                }

                // One concrete item, offered as itself through the instance path. A refusal (full bag, duplicate
                // identity, run not running) leaves this pickup and its instance exactly as they were.
                if (LootCollection.TryCollectInstance(_host, actor, _instance, out string refusal))
                {
                    _taken = true;
                    Destroy(gameObject);
                    return;
                }

                GameEvents.RaiseToast(actor.PlayerId, "Cannot take item");
                Debug.Log($"[Emberdelve][LootPickup] {name}: kept {_instance.Describe()} on the ground - {refusal}");
                return;
            }

            int requested = Mathf.Max(1, amount);
            int accepted = LootCollection.CollectAccepted(_host, actor, item, requested);

            // Nothing moved: the pickup stays exactly as it was, same amount, same object.
            if (accepted <= 0) return;

            int left = requested - accepted;
            if (left <= 0)
            {
                Destroy(gameObject);
                return;
            }

            // Partial: only the units the bag did not commit are still lying here.
            amount = left;
            Debug.Log($"[Emberdelve][LootPickup] {name}: took {accepted} of {requested} {item.displayName}, " +
                      $"{amount} left on the ground");
        }

        public void OnHoldComplete(IInteractionActor actor) => OnInteractInstant(actor);

        private void Update()
        {
            _phase += Time.deltaTime * bobSpeed;
            Vector3 bob = _home + Vector3.up * (Mathf.Sin(_phase) * bobAmplitude);
            Vector3 delta = bob - transform.position;
            if (delta.sqrMagnitude > 0.0001f) transform.position = bob;
            transform.Rotate(Vector3.up, spinSpeed * Time.deltaTime, Space.World);
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = item != null ? item.iconColor : Color.yellow;
            Gizmos.DrawWireSphere(transform.position, 0.35f);
        }
    }
}
