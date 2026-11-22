using System;
using System.Collections.Generic;
using Emberdelve.Flow;
using Emberdelve.Level;
using Unity.Collections;
using Unity.Netcode;

namespace Emberdelve.Net
{
    /// <summary>Destination geometry ACK barrier. Gameplay content and floor clock publication are separate.</summary>
    internal sealed class NetRoyaleMapServer : IDisposable
    {
        private sealed class Sent
        {
            public RoyaleMapSnapshot Snapshot;
            public float At;
            public bool Ready, PublicReady;
        }
        private readonly NetworkManager _network;
        private readonly RunDirector _director;
        private readonly Func<ulong, bool> _initialReady;
        private readonly Action<string> _log;
        private readonly Action<ulong> _publishWorld;
        private readonly HashSet<int> _activated = new HashSet<int>();
        private readonly HashSet<(ulong client, int floor)> _retiredSent = new HashSet<(ulong, int)>();
        private readonly Dictionary<(ulong client, int floor), Sent> _sent = new Dictionary<(ulong, int), Sent>();
        public NetRoyaleMapServer(NetworkManager network, RunDirector director, Func<ulong, bool> initialReady, Action<string> log, Action<ulong> publishWorld)
        {
            _network = network; _director = director; _initialReady = initialReady; _log = log; _publishWorld = publishWorld;
            network.CustomMessagingManager.RegisterNamedMessageHandler(NetRoyaleMapClient.ReadyMessage, OnReady);
            network.CustomMessagingManager.RegisterNamedMessageHandler(NetRoyaleMapClient.PublicReadyMessage, OnPublicReady);
        }
        public bool GeometryReady(int floor)
        {
            if (!_director.FloorCoordinator.TryCopyMapSnapshot(floor, out var snapshot)) return false;
            foreach (ulong client in _network.ConnectedClientsIds)
                if (client != NetworkManager.ServerClientId && (!_initialReady(client)
                    || !_sent.TryGetValue((client, floor), out var state) || !state.Ready || state.Snapshot.Hash != snapshot.Hash)) return false;
            return true;
        }
        public bool PublicStateReady(int floor)
        {
            if (!GeometryReady(floor)) return false;
            foreach (ulong client in _network.ConnectedClientsIds)
                if (client != NetworkManager.ServerClientId && (!_sent.TryGetValue((client, floor), out var state) || !state.PublicReady)) return false;
            return true;
        }

        public bool PublicStateReadyForClient(int floor, ulong client)
        {
            if (client == NetworkManager.ServerClientId) return true;
            return _initialReady(client) && _sent.TryGetValue((client, floor), out var state) && state.PublicReady
                && _director.FloorCoordinator.TryCopyMapSnapshot(floor, out var snapshot)
                && snapshot.Hash == state.Snapshot.Hash;
        }
        public void Tick(float now)
        {
            var clients = new List<ulong>(_network.ConnectedClientsIds);
            foreach (ulong client in clients)
            {
                if (client == NetworkManager.ServerClientId || !_initialReady(client)) continue;
                for (int floor = 2; floor <= 3; floor++)
                {
                    if (!_director.FloorCoordinator.TryCopyMapSnapshot(floor, out var snapshot)) continue;
                    if (!_activated.Contains(floor))
                    {
                        if (!_director.FloorCoordinator.TryActivate(floor, out _)) continue;
                        _activated.Add(floor);
                    }
                    if (_sent.TryGetValue((client, floor), out var state))
                    {
                        if (state.Snapshot.Hash != snapshot.Hash || (!state.PublicReady && !_director.FloorCoordinator.IsRetired(floor) && now - state.At > 60))
                        { _log("Royale geometry timeout or replacement client=" + client + " floor=" + floor); _network.DisconnectClient(client, "destination geometry unavailable"); break; }
                        continue;
                    }
                    // Floor3 is never delivered ahead of acknowledgement for floor2.
                    if (floor == 3 && (!_sent.TryGetValue((client, 2), out var preceding) || !preceding.Ready)) continue;
                    var bytes = snapshot.Encode();
                    _sent.Add((client, floor), new Sent { Snapshot = snapshot, At = now });
                    using (var writer = new FastBufferWriter(bytes.Length, Allocator.Temp))
                    {
                        writer.WriteBytesSafe(bytes, bytes.Length);
                        _network.CustomMessagingManager.SendNamedMessage(NetRoyaleMapClient.PlanMessage, client, writer, NetworkDelivery.ReliableSequenced);
                    }
                    _log("Royale geometry sent client=" + client + " floor=" + floor + " hash=" + snapshot.Hash);
                }
                for (int source = 1; source <= 2; source++)
                {
                    if (!_director.FloorCoordinator.IsRetired(source) || _retiredSent.Contains((client, source))
                        || !_sent.TryGetValue((client, source + 1), out var destination) || !destination.Ready
                        || (!destination.PublicReady && !_director.FloorCoordinator.IsRetired(source + 1))) continue;
                    using (var writer = new FastBufferWriter(NetRoyaleMapClient.RetireBytes, Allocator.Temp))
                    {
                        writer.WriteValueSafe((byte)source);
                        writer.WriteValueSafe(destination.Snapshot.InitialHash);
                        writer.WriteValueSafe(destination.Snapshot.Hash);
                        _network.CustomMessagingManager.SendNamedMessage(NetRoyaleMapClient.RetireMessage, client, writer, NetworkDelivery.ReliableSequenced);
                    }
                    _retiredSent.Add((client, source));
                    _log("Royale source retired client=" + client + " floor=" + source);
                }
            }
            var stale = new List<(ulong client, int floor)>();
            foreach (var key in _sent.Keys) if (!clients.Contains(key.client)) stale.Add(key);
            foreach (var key in stale) _sent.Remove(key);
            _retiredSent.RemoveWhere(key => !clients.Contains(key.client));
        }
        private void OnReady(ulong client, FastBufferReader reader)
        {
            if (client == NetworkManager.ServerClientId) return;
            if (reader.Length - reader.Position != RoyaleMapSnapshot.AckBytes || !reader.TryBeginRead(RoyaleMapSnapshot.AckBytes))
            { _network.DisconnectClient(client, "invalid destination geometry acknowledgement"); return; }
            var ack = new byte[RoyaleMapSnapshot.AckBytes]; reader.ReadBytesSafe(ref ack, ack.Length);
            if (!_initialReady(client) || !_sent.TryGetValue((client, ack[0]), out var sent) || !sent.Snapshot.Accepts(ack))
            { _network.DisconnectClient(client, "destination geometry mismatch"); return; }
            if (sent.Ready) return;
            sent.Ready = true;
            _publishWorld?.Invoke(client);
            _log("Royale geometry acknowledged client=" + client + " floor=" + sent.Snapshot.Floor + " hash=" + sent.Snapshot.Hash);
        }
        private void OnPublicReady(ulong client, FastBufferReader reader)
        {
            if (client == NetworkManager.ServerClientId) return;
            if (reader.Length - reader.Position != RoyaleMapSnapshot.AckBytes || !reader.TryBeginRead(RoyaleMapSnapshot.AckBytes))
            { _network.DisconnectClient(client, "invalid destination public state acknowledgement"); return; }
            var ack = new byte[RoyaleMapSnapshot.AckBytes]; reader.ReadBytesSafe(ref ack, ack.Length);
            if (!_initialReady(client) || !_sent.TryGetValue((client, ack[0]), out var sent) || !sent.Ready || !sent.Snapshot.Accepts(ack))
            { _network.DisconnectClient(client, "destination public state mismatch"); return; }
            if (sent.PublicReady) return;
            sent.PublicReady = true;
            _log("Royale public state acknowledged client=" + client + " floor=" + sent.Snapshot.Floor);
        }
        public void Dispose()
        {
            if (_network != null && _network.CustomMessagingManager != null)
            {
                _network.CustomMessagingManager.UnregisterNamedMessageHandler(NetRoyaleMapClient.ReadyMessage);
                _network.CustomMessagingManager.UnregisterNamedMessageHandler(NetRoyaleMapClient.PublicReadyMessage);
            }
            _sent.Clear();
            _retiredSent.Clear();
        }
    }
}
