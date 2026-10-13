using UnityEngine;
using Emberdelve.Core;
using Emberdelve.Data;

namespace Emberdelve.Items
{
    /// <summary>What a loot holder must provide so pickups can hand items to the player.</summary>
    public interface ILootHost
    {
        Inventory Inventory { get; }
        bool CollectLoot(ItemData item, int amount);
        void SpawnLoot(ItemData item, int amount, Vector3 worldPosition);
    }

    /// <summary>
    /// A piece of loot lying in the world. Interacting with it asks the run layer to add the item to
    /// the inventory; the pickup only disappears when that succeeds, so a full bag is a real risk.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class LootPickup : MonoBehaviour, IInteractable
    {
        [Header("Content")]
        [SerializeField] private ItemData item;
        [SerializeField] private int amount = 1;

        [Header("Wiring")]
        [Tooltip("Assigned to the RunDirector by the level builder; left empty the pickup searches its parents.")]
        [SerializeField] private MonoBehaviour lootHost;

        [Header("Presentation")]
        [SerializeField] private float bobAmplitude = 0.09f;
        [SerializeField] private float bobSpeed = 1.6f;
        [SerializeField] private float spinSpeed = 42f;

        private ILootHost _host;
        private Vector3 _home;
        private float _phase;

        public ItemData Item => item;
        public int Amount => Mathf.Max(1, amount);
        public string PromptText => amount > 1 ? $"Take {item.displayName} x{amount}" : $"Take {item.displayName}";
        public bool RequiresHold => false;
        public float HoldDuration => 0f;

        private void Awake()
        {
            _host = lootHost as ILootHost;
            if (_host == null) _host = GetComponentInParent<ILootHost>();
            _home = transform.position;
            _phase = Random.value * Mathf.PI * 2f;
        }

        public void Configure(ItemData newItem, int newAmount, MonoBehaviour newHost)
        {
            item = newItem;
            amount = Mathf.Max(1, newAmount);
            lootHost = newHost;
            _host = newHost as ILootHost;
        }

        public bool CanInteract(out string reason)
        {
            if (item == null)
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

        public void OnInteractInstant()
        {
            if (!CanInteract(out string reason))
            {
                GameEvents.RaiseToast(reason ?? "Cannot take item");
                return;
            }

            if (_host.CollectLoot(item, amount)) Destroy(gameObject);
        }

        public void OnHoldComplete() => OnInteractInstant();

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
