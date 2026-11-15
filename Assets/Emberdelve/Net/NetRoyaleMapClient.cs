using System;
using System.Collections.Generic;
using Emberdelve.Level;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace Emberdelve.Net
{
    internal sealed class NetRoyaleMapClient : MonoBehaviour
    {
        internal const string PlanMessage = "emberdelve.royale-geometry.v1";
        internal const string ReadyMessage = "emberdelve.royale-geometry-ready.v1";
        internal const string PublicReadyMessage = "emberdelve.royale-public-ready.v1";
        internal const string RetireMessage = "emberdelve.royale-retire.v1";
        internal const int RetireBytes = 9;
        private readonly Dictionary<int, RoyaleMapSnapshot> _content = new Dictionary<int, RoyaleMapSnapshot>();
        private readonly HashSet<int> _contentReady = new HashSet<int>();
        private float _nextContentCheck;
        private NetworkManager _network;
        private bool _registered;
        private RoyaleClientMaps _maps;
        public void Configure(NetworkManager network) { _network = network; }
        private void Update()
        {
            if (_network == null || _network.CustomMessagingManager == null) return;
            if (!_registered)
            {
                _network.CustomMessagingManager.RegisterNamedMessageHandler(PlanMessage, OnPlan);
                _network.CustomMessagingManager.RegisterNamedMessageHandler(RetireMessage, OnRetire);
                _registered = true;
            }
            if (_maps == null || Time.unscaledTime < _nextContentCheck || !_network.IsConnectedClient) return;
            _nextContentCheck = Time.unscaledTime + .25f;
            var owner = _network.LocalClient?.PlayerObject;
            var inventory = owner != null ? owner.GetComponent<NetInventoryClient>() : null;
            if (inventory == null || !inventory.ChestStateReady || !inventory.DoorStateReady) return;
            foreach (var entry in _content)
            {
                if (_contentReady.Contains(entry.Key) || !_maps.PublicStateReady(entry.Key, inventory.HasChestState,
                    id => inventory.TryGetDoorState(id, out _, out _))) continue;
                var bytes = entry.Value.Acknowledge(true);
                using (var writer = new FastBufferWriter(bytes.Length, Allocator.Temp))
                { writer.WriteBytesSafe(bytes, bytes.Length); _network.CustomMessagingManager.SendNamedMessage(PublicReadyMessage,
                    NetworkManager.ServerClientId, writer, NetworkDelivery.ReliableSequenced); }
                _contentReady.Add(entry.Key);
                Debug.Log("[RoyaleMap] public state ready floor=" + entry.Key + " hash=" + entry.Value.Hash);
            }
        }
        private void OnPlan(ulong sender, FastBufferReader reader)
        {
            if (sender != NetworkManager.ServerClientId) return;
            if (reader.Length - reader.Position != RoyaleMapSnapshot.PayloadBytes
                || !reader.TryBeginRead(RoyaleMapSnapshot.PayloadBytes))
            { Debug.LogError("[RoyaleMap] Invalid geometry payload length"); return; }
            var bytes = new byte[RoyaleMapSnapshot.PayloadBytes]; reader.ReadBytesSafe(ref bytes, bytes.Length);
            var catalog = Resources.Load<DungeonModuleCatalog>("Emberdelve/DungeonModuleCatalog");
            string reason = "Missing module catalog";
            if (catalog == null || !RoyaleMapSnapshot.TryDecode(bytes, out var snapshot, out reason))
            { Debug.LogError("[RoyaleMap] Rejected geometry: " + reason); return; }
            if (_maps == null)
            {
                foreach (var map in FindObjectsByType<DungeonModuleSceneAssembler>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                    if (map.AcceptsSessionPlan && map.RuntimeMapValidationComplete) { _maps = new RoyaleClientMaps(map, catalog); break; }
            }
            bool accepted = _maps != null && _maps.TryApply(snapshot, out reason);
            var ack = snapshot.Acknowledge(accepted);
            using (var writer = new FastBufferWriter(ack.Length, Allocator.Temp))
            {
                writer.WriteBytesSafe(ack, ack.Length);
                _network.CustomMessagingManager.SendNamedMessage(ReadyMessage, NetworkManager.ServerClientId, writer, NetworkDelivery.ReliableSequenced);
            }
            if (accepted)
            { _content[snapshot.Floor] = snapshot; Debug.Log("[RoyaleMap] geometry ready floor=" + snapshot.Floor + " hash=" + snapshot.Hash); }
            else Debug.LogError("[RoyaleMap] Geometry failed floor=" + snapshot.Floor + " reason=" + reason);
        }
        private void OnRetire(ulong sender, FastBufferReader reader)
        {
            if (sender != NetworkManager.ServerClientId) return;
            if (reader.Length - reader.Position != RetireBytes || !reader.TryBeginRead(RetireBytes))
            { Debug.LogError("[RoyaleMap] Invalid retirement payload"); return; }
            reader.ReadValueSafe(out byte floor);
            reader.ReadValueSafe(out uint initialHash);
            reader.ReadValueSafe(out uint destinationHash);
            string reason = "Geometry not installed";
            if (_maps == null || !_maps.TryRetire(floor, initialHash, destinationHash, out reason))
            { Debug.LogError("[RoyaleMap] Retirement rejected: " + reason); return; }
            foreach (var map in FindObjectsByType<DungeonModuleSceneAssembler>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (!map.RuntimeContentRetired || map.RuntimeLayout == null) continue;
                var layout = map.RuntimeLayout;
                NetEnemyViews.RetireFloor(new Rect(layout.origin.x, layout.origin.z,
                    layout.gridWidth * layout.CellSize, layout.gridHeight * layout.CellSize));
            }
            _content.Remove(floor); _contentReady.Remove(floor);
            Debug.Log("[RoyaleMap] source retired floor=" + floor + " destination=" + destinationHash);
        }
        private void OnDestroy()
        {
            if (_registered && _network != null && _network.CustomMessagingManager != null)
            {
                _network.CustomMessagingManager.UnregisterNamedMessageHandler(PlanMessage);
                _network.CustomMessagingManager.UnregisterNamedMessageHandler(RetireMessage);
            }
            _maps?.Dispose(); _maps = null; _content.Clear(); _contentReady.Clear();
        }
    }
}
