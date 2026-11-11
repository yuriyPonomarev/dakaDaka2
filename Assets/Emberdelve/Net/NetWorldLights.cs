using System.Collections.Generic;
using System.Globalization;
using Emberdelve.Core;
using Emberdelve.Interaction;
using Emberdelve.Level;
using Emberdelve.Player;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace Emberdelve.Net
{
    /// <summary>Owner intent identifies only a fixture and desired state; sender supplies actor identity.</summary>
    public sealed class NetWorldLights : MonoBehaviour, IWorldLightAuthority
    {
        private const string StateMessage = "ember.world.lights.state.v1";
        private const string RequestMessage = "ember.world.lights.request.v1";
        private const int MaxLights = 512;
        private NetworkManager _network;
        private bool _registered, _received;
        private float _nextSnapshot;
        private readonly Dictionary<ulong, bool> _states = new Dictionary<ulong, bool>();
        private readonly Dictionary<ulong, float> _nextRequest = new Dictionary<ulong, float>();
        public bool LightStateReady => _network != null && _network.IsListening && (_network.IsServer || _received);

        public void Configure(NetworkManager network)
        {
            _network = network;
            GameServices.Register<IWorldLightAuthority>(this);
            DungeonModulePlanSession.Changed += OnPlanChanged;
        }
        private void OnPlanChanged(DungeonModulePlan plan) { _states.Clear(); _nextRequest.Clear(); _received = false; _nextSnapshot = 0f; }
        private bool Layout(out int seed, out uint hash)
        {
            var plan = DungeonModulePlanSession.Current;
            seed = plan != null ? plan.Seed : 0; hash = 0;
            return plan != null && uint.TryParse(plan.LayoutHash, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out hash);
        }
        private void Update()
        {
            if (_network == null || !_network.IsListening || _network.CustomMessagingManager == null) return;
            if (!_registered)
            {
                if (_network.IsServer) _network.CustomMessagingManager.RegisterNamedMessageHandler(RequestMessage, ReceiveRequest);
                else _network.CustomMessagingManager.RegisterNamedMessageHandler(StateMessage, ReceiveState);
                _registered = true;
            }
            if (!_network.IsServer)
            {
                foreach (var light in WorldLightInteractable.Active)
                    if (light != null && _states.TryGetValue(light.StableId, out bool lit)) light.ApplyReplicatedState(lit);
                return;
            }
            if (Time.unscaledTime < _nextSnapshot || !Layout(out int seed, out uint hash)) return;
            _nextSnapshot = Time.unscaledTime + .35f;
            int count = WorldLightInteractable.Active.Count;
            if (count > MaxLights) { Debug.LogError("World light snapshot capacity exceeded"); return; }
            using (var writer = new FastBufferWriter(12 + count * 9, Allocator.Temp))
            {
                writer.WriteValueSafe(seed); writer.WriteValueSafe(hash); writer.WriteValueSafe(count);
                foreach (var light in WorldLightInteractable.Active)
                {
                    writer.WriteValueSafe(light.StableId); writer.WriteValueSafe((byte)(light.IsLit ? 1 : 0));
                }
                foreach (ulong client in _network.ConnectedClientsIds)
                    if (client != NetworkManager.ServerClientId)
                        _network.CustomMessagingManager.SendNamedMessage(StateMessage, client, writer, NetworkDelivery.ReliableFragmentedSequenced);
            }
        }
        public bool RequestLightState(IInteractionActor actor, ulong id, bool lit)
        {
            if (!LightStateReady || _network.LocalClient == null || _network.LocalClient.PlayerObject == null) return false;
            var owner = _network.LocalClient.PlayerObject.GetComponent<PlayerRig>();
            if (!ReferenceEquals(actor, owner)) return false;
            if (_network.IsServer) return ApplyRequest(_network.LocalClientId, id, lit);
            if (!Layout(out int seed, out uint hash)) return false;
            using (var writer = new FastBufferWriter(17, Allocator.Temp))
            {
                writer.WriteValueSafe(seed); writer.WriteValueSafe(hash); writer.WriteValueSafe(id); writer.WriteValueSafe((byte)(lit ? 1 : 0));
                _network.CustomMessagingManager.SendNamedMessage(RequestMessage, NetworkManager.ServerClientId, writer, NetworkDelivery.ReliableSequenced);
            }
            return true;
        }
        public static bool TryReadRequest(FastBufferReader reader, out int seed, out uint hash, out ulong id, out bool lit)
        {
            seed = 0; hash = 0; id = 0; lit = false;
            // Named-message readers already consumed their transport header; Length includes it.
            if (reader.Length - reader.Position != 17 || !reader.TryBeginRead(17)) return false;
            reader.ReadValueSafe(out seed); reader.ReadValueSafe(out hash); reader.ReadValueSafe(out id); reader.ReadValueSafe(out byte value);
            lit = value == 1;
            return value <= 1;
        }
        private static bool Reject(ulong sender, ulong id, string reason)
        {
            Debug.Log("[WorldLight] request rejected sender=" + sender + " id=" + id + " reason=" + reason);
            return false;
        }
        private void ReceiveRequest(ulong sender, FastBufferReader reader)
        {
            if (!_network.IsServer) return;
            if (!TryReadRequest(reader, out int seed, out uint hash, out ulong id, out bool lit))
            { Reject(sender, id, "malformed payload remaining=" + (reader.Length - reader.Position)); return; }
            if (!Layout(out int expectedSeed, out uint expectedHash) || seed != expectedSeed || hash != expectedHash)
            { Reject(sender, id, "layout mismatch"); return; }
            ApplyRequest(sender, id, lit);
        }
        private bool ApplyRequest(ulong sender, ulong id, bool lit)
        {
            if (!_network.IsServer || !_network.ConnectedClients.TryGetValue(sender, out var client) || client.PlayerObject == null || !client.PlayerObject.IsSpawned)
                return Reject(sender, id, "sender has no spawned player");
            if (_nextRequest.TryGetValue(sender, out float next) && Time.unscaledTime < next) return Reject(sender, id, "rate limited");
            _nextRequest[sender] = Time.unscaledTime + .2f;
            var actor = client.PlayerObject.GetComponent<PlayerRig>();
            if (actor == null) return Reject(sender, id, "missing actor");
            foreach (var light in WorldLightInteractable.Active)
                if (light != null && light.StableId == id)
                {
                    if (!light.TrySetAuthoritatively(actor, lit, out string reason))
                        return Reject(sender, id, reason + " origin=" + actor.InteractionOrigin + " target=" + light.transform.position);
                    _nextSnapshot = 0f;
                    Debug.Log("[WorldLight] request accepted sender=" + sender + " id=" + id + " lit=" + lit);
                    return true;
                }
            return Reject(sender, id, "unknown fixture");
        }
        private void ReceiveState(ulong sender, FastBufferReader reader)
        {
            if (_network.IsServer || sender != NetworkManager.ServerClientId || reader.Length - reader.Position < 12 || !reader.TryBeginRead(12)) return;
            reader.ReadValueSafe(out int seed); reader.ReadValueSafe(out uint hash); reader.ReadValueSafe(out int count);
            if (!Layout(out int expectedSeed, out uint expectedHash) || seed != expectedSeed || hash != expectedHash || count < 0 || count > MaxLights || reader.Length - reader.Position != count * 9 || !reader.TryBeginRead(count * 9)) return;
            var incoming = new Dictionary<ulong, bool>(count);
            for (int i = 0; i < count; i++)
            {
                reader.ReadValueSafe(out ulong id); reader.ReadValueSafe(out byte lit);
                if (lit > 1 || incoming.ContainsKey(id)) return;
                incoming.Add(id, lit == 1);
            }
            _states.Clear();
            foreach (var pair in incoming) _states.Add(pair.Key, pair.Value);
            _received = true;
        }
        private void OnDestroy()
        {
            GameServices.Unregister<IWorldLightAuthority>(this);
            DungeonModulePlanSession.Changed -= OnPlanChanged;
            if (_registered && _network != null && _network.CustomMessagingManager != null)
                _network.CustomMessagingManager.UnregisterNamedMessageHandler(_network.IsServer ? RequestMessage : StateMessage);
        }
    }
}
