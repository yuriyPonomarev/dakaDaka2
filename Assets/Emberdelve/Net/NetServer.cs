using System;
using System.Collections.Generic;
using Emberdelve.Combat;
using Emberdelve.Core;
using Emberdelve.Flow;
using Emberdelve.Net.Rules;
using Emberdelve.Player;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Emberdelve.Net
{
    /// <summary>
    /// U3A3: the server side of the network layer (dedicated server and host). Owned and driven by NetBootstrap.
    ///
    ///   - loads the dungeon scene through NGO scene management once the server listens (clients follow on their own);
    ///   - ConnectionApprovalCallback: the pure NetApproval rule, fed with this process's own facts (admitted/reserved seats,
    ///     slots in use, the RunDirector's approved class list). Approvals that arrive before the dungeon scene (and so the
    ///     RunDirector) exists are HELD (response.Pending) and decided the moment it does;
    ///   - OnClientConnected: RunDirector.TrySpawnNetworkPlayer (id, party, class, sender bound, registered) and only then
    ///     NetworkObject.SpawnAsPlayerObject(clientId): nothing is ever half-spawned;
    ///   - OnClientDisconnect: RunDirector.RemovePlayer (session, party seat, registry, sender) and despawn, immediately;
    ///   - one status line every two seconds: "status players=N senders=N registry=N clients=N".
    ///
    /// Everything a client says is data: the connect payload is a slot string and a class id string, nothing else.
    /// </summary>
    internal sealed class NetServer
    {
        private const float StatusSeconds = 2f;
        private const float SceneLoadTimeoutSeconds = 60f;
        private const float UnconnectedReservationSeconds = 20f;

        private sealed class Approved
        {
            public string Slot;
            public string ClassId;
            public float ApprovedAt;
            public bool Connected;
        }

        private sealed class Pending
        {
            public ulong ClientId;
            public byte[] Payload;
            public NetworkManager.ConnectionApprovalResponse Response;
        }

        private readonly NetworkManager _nm;
        private readonly NetLaunchArgs _args;
        private readonly GameObject _playerPrefab;
        private readonly Action<string> _log;
        private readonly Action<string> _logError;
        private readonly NetIdentityTable _table = new NetIdentityTable();
        private readonly Dictionary<ulong, Approved> _approved = new Dictionary<ulong, Approved>();
        private readonly List<Pending> _pending = new List<Pending>();
        private readonly List<ulong> _awaitingSpawn = new List<ulong>();
        private readonly List<ulong> _scratch = new List<ulong>();

        private RunDirector _director;
        private bool _sceneRequested;
        private bool _sceneFailed;
        private float _sceneRequestedAt;
        private float _nextStatusAt;

        public NetServer(NetworkManager nm, NetLaunchArgs args, GameObject playerPrefab, Action<string> log, Action<string> logError)
        {
            _nm = nm;
            _args = args;
            _playerPrefab = playerPrefab;
            _log = log;
            _logError = logError;
        }

        public NetIdentityTable Table => _table;

        /// <summary>True once the dungeon scene is loaded here and its RunDirector is running (admission can decide).</summary>
        public bool IsReady => _director != null;

        /// <summary>Connections that hold a seat (approved, spawned or about to be).</summary>
        public int SeatCount => _approved.Count;

        public void Install()
        {
            _nm.ConnectionApprovalCallback = Approve;
            NetIdentityTable.Current = _table;
        }

        /// <summary>Players / sender bindings / combat entities right now, in the status-line format.</summary>
        public string CountsText()
        {
            int players = _director != null ? _director.Players.Count : 0;
            return "players=" + players + " senders=" + CombatSenders.BoundCount + " registry=" + CombatRegistry.All.Count;
        }

        // ---- scene + readiness -----------------------------------------------------------------

        /// <summary>Called every frame by NetBootstrap.</summary>
        public void Tick(float now)
        {
            if (_nm == null || !_nm.IsListening) return;

            if (!_sceneRequested) RequestDungeonScene(now);

            if (_director == null && _sceneRequested && !_sceneFailed) CheckReady(now);

            if (_director != null || _sceneFailed)
            {
                FlushPendingApprovals();
                FlushAwaitingSpawns();
            }

            PurgeStaleReservations(now);

            if (now >= _nextStatusAt)
            {
                _nextStatusAt = now + StatusSeconds;
                _log("status " + CountsText() + " clients=" + _approved.Count + " ready=" + (_director != null));
            }
        }

        private void RequestDungeonScene(float now)
        {
            _sceneRequested = true;
            _sceneRequestedAt = now;

            string sceneName = GameConfig.DefaultDungeonSceneName;
            try
            {
                if (_nm.SceneManager == null)
                {
                    Fail("scene management is off");
                    return;
                }

                SceneEventProgressStatus status = _nm.SceneManager.LoadScene(sceneName, LoadSceneMode.Single);
                _log("loading scene " + sceneName + " status=" + status);
                if (status != SceneEventProgressStatus.Started) Fail("scene load not started: " + status);
            }
            catch (Exception e)
            {
                Fail("scene load threw " + e.Message);
            }
        }

        private void CheckReady(float now)
        {
            if (GameServices.TryGet(out RunDirector director) && director != null && director.State == RunState.Running)
            {
                _director = director;
                _log("ready scene=" + GameConfig.DefaultDungeonSceneName + " " + CountsText() + " maxPlayers=" + _args.MaxPlayers +
                     " enemies=" + (NetRole.EnemiesDisabled ? "off" : "on"));
                return;
            }

            if (now - _sceneRequestedAt > SceneLoadTimeoutSeconds) Fail("dungeon scene not ready after " + SceneLoadTimeoutSeconds + "s");
        }

        private void Fail(string why)
        {
            _sceneFailed = true;
            _logError("server cannot host the dungeon: " + why);
        }

        // ---- approval --------------------------------------------------------------------------

        private void Approve(NetworkManager.ConnectionApprovalRequest request, NetworkManager.ConnectionApprovalResponse response)
        {
            ulong clientId = request.ClientNetworkId;
            bool hostSelf = clientId == NetworkManager.ServerClientId;

            if (_director == null && !_sceneFailed && !hostSelf)
            {
                // The dungeon (and the class list that lives in it) is still loading: hold the decision.
                response.Pending = true;
                _pending.Add(new Pending { ClientId = clientId, Payload = request.Payload, Response = response });
                _log("approval id=" + clientId + " held (dungeon loading)");
                return;
            }

            Decide(clientId, request.Payload, response);
        }

        private void FlushPendingApprovals()
        {
            if (_pending.Count == 0) return;

            Pending[] held = _pending.ToArray();
            _pending.Clear();
            for (int i = 0; i < held.Length; i++)
            {
                Decide(held[i].ClientId, held[i].Payload, held[i].Response);
                held[i].Response.Pending = false;
            }
        }

        private void Decide(ulong clientId, byte[] payload, NetworkManager.ConnectionApprovalResponse response)
        {
            NetApprovalResult r = _sceneFailed
                ? new NetApprovalResult(false, "server not ready", "", "")
                : NetApproval.Evaluate(payload, NetTuning.ProtocolVersion, _approved.Count, _args.MaxPlayers, SlotInUse, ClassApproved);

            response.Approved = r.Approved;
            response.CreatePlayerObject = false;
            response.Pending = false;
            response.Reason = r.Approved ? "" : r.Reason;

            if (r.Approved)
                _approved[clientId] = new Approved { Slot = r.Slot, ClassId = r.ClassId, ApprovedAt = Time.unscaledTime };

            _log("approval id=" + clientId + " approved=" + r.Approved +
                 " slot=" + (r.Slot.Length == 0 ? "-" : r.Slot) + " class=" + (r.ClassId.Length == 0 ? "-" : r.ClassId) +
                 " reason=" + (r.Approved ? "-" : r.Reason));
        }

        private bool SlotInUse(string slot)
        {
            foreach (KeyValuePair<ulong, Approved> pair in _approved)
                if (string.Equals(pair.Value.Slot, slot, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // The host's own approval can come before the dungeon exists; its class is then checked at spawn time instead.
        private bool ClassApproved(string classId)
        {
            return _director == null || _director.TryResolveClass(classId, out _);
        }

        /// <summary>A seat granted to a connection that never completed the join is given back (killed during scene sync, etc.).</summary>
        private void PurgeStaleReservations(float now)
        {
            if (_approved.Count == 0) return;

            _scratch.Clear();
            foreach (KeyValuePair<ulong, Approved> pair in _approved)
            {
                if (!pair.Value.Connected && now - pair.Value.ApprovedAt > UnconnectedReservationSeconds) _scratch.Add(pair.Key);
            }

            for (int i = 0; i < _scratch.Count; i++)
            {
                _approved.Remove(_scratch[i]);
                _log("reservation expired client=" + _scratch[i] + " (never completed the join)");
            }
        }

        // ---- connect / disconnect --------------------------------------------------------------

        public void OnClientConnected(ulong clientId)
        {
            if (!_approved.TryGetValue(clientId, out Approved info))
            {
                // Not through our approval (should not happen with ConnectionApproval on); do not give it an actor.
                _logError("client " + clientId + " connected without an approval record; disconnecting it");
                if (clientId != NetworkManager.ServerClientId) _nm.DisconnectClient(clientId, "not approved");
                return;
            }

            info.Connected = true;

            if (_director == null)
            {
                if (!_awaitingSpawn.Contains(clientId)) _awaitingSpawn.Add(clientId);
                return;
            }

            SpawnActor(clientId, info);
        }

        private void FlushAwaitingSpawns()
        {
            if (_awaitingSpawn.Count == 0) return;

            ulong[] waiting = _awaitingSpawn.ToArray();
            _awaitingSpawn.Clear();
            for (int i = 0; i < waiting.Length; i++)
            {
                if (_sceneFailed || !_approved.TryGetValue(waiting[i], out Approved info))
                {
                    if (waiting[i] != NetworkManager.ServerClientId) _nm.DisconnectClient(waiting[i], "server not ready");
                    continue;
                }

                SpawnActor(waiting[i], info);
            }
        }

        private void SpawnActor(ulong clientId, Approved info)
        {
            if (_table.TryGetByClient(clientId, out _)) return;

            bool local = clientId == NetworkManager.ServerClientId;
            PlayerRig rig = null;
            string reason;
            bool joined;
            try
            {
                joined = _director.TrySpawnNetworkPlayer(_playerPrefab, info.ClassId, local, out rig, out reason);
            }
            catch (Exception e)
            {
                joined = false;
                reason = "spawn threw " + e.Message;
            }

            if (!joined || rig == null)
            {
                _logError("spawn refused client=" + clientId + " reason='" + reason + "'");
                RefuseAfterApproval(clientId, reason);
                return;
            }

            NetworkObject netObject = rig.GetComponent<NetworkObject>();
            if (netObject == null)
            {
                _director.RemovePlayer(rig.PlayerId, "prefab has no NetworkObject", true);
                _logError("spawn refused client=" + clientId + " reason='prefab has no NetworkObject'");
                RefuseAfterApproval(clientId, "server error");
                return;
            }

            NetIdentity row = new NetIdentity
            {
                ClientId = clientId,
                Slot = info.Slot,
                ClassId = info.ClassId,
                PlayerId = rig.PlayerId,
                PartyId = rig.PartyId,
                Rig = rig,
                Object = netObject
            };

            try
            {
                netObject.SpawnAsPlayerObject(clientId, true);
            }
            catch (Exception e)
            {
                _director.RemovePlayer(row.PlayerId, "spawn failed", true);
                _logError("SpawnAsPlayerObject threw for client " + clientId + ": " + e.Message);
                RefuseAfterApproval(clientId, "server error");
                return;
            }

            _table.Add(row);
            _log("admitted client=" + clientId + " id=" + row.PlayerId + " party=" + row.PartyId + " slot=" + row.Slot +
                 " class=" + row.ClassId + " netId=" + netObject.NetworkObjectId + " " + CountsText());
        }

        private void RefuseAfterApproval(ulong clientId, string reason)
        {
            _approved.Remove(clientId);
            if (clientId != NetworkManager.ServerClientId) _nm.DisconnectClient(clientId, reason);
        }

        public void OnClientDisconnected(ulong clientId)
        {
            _approved.Remove(clientId);
            _awaitingSpawn.Remove(clientId);
            for (int i = _pending.Count - 1; i >= 0; i--)
                if (_pending[i].ClientId == clientId) _pending.RemoveAt(i);

            if (_table.RemoveByClient(clientId, out NetIdentity row)) RemoveActor(row, "disconnect");
        }

        private void RemoveActor(NetIdentity row, string reason)
        {
            if (_director != null) _director.RemovePlayer(row.PlayerId, reason, false);

            NetworkObject netObject = row.Object;
            if (netObject != null && netObject.IsSpawned)
            {
                try { netObject.Despawn(true); }
                catch (Exception e) { _logError("despawn threw for id=" + row.PlayerId + ": " + e.Message); }
            }
            else if (row.Rig != null)
            {
                UnityEngine.Object.Destroy(row.Rig.gameObject);
            }

            _log("removed client=" + row.ClientId + " id=" + row.PlayerId + " slot=" + row.Slot + " reason=" + reason + " " + CountsText());
        }

        /// <summary>Server stops: every remaining actor is taken out of the run first so the counts end at zero.</summary>
        public void OnShutdownBegin()
        {
            NetIdentity[] rows = new List<NetIdentity>(_table.All).ToArray();
            for (int i = 0; i < rows.Length; i++)
            {
                _table.RemoveByClient(rows[i].ClientId, out _);
                RemoveActor(rows[i], "shutdown");
            }

            _approved.Clear();
            _pending.Clear();
            _awaitingSpawn.Clear();
        }

        public void Dispose()
        {
            if (NetIdentityTable.Current == _table) NetIdentityTable.Current = null;
        }
    }
}
