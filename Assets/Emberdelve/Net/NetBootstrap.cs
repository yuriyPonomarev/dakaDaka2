using System;
using System.Collections.Generic;
using System.IO;
using Emberdelve.Core;
using Emberdelve.Net.Rules;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;

namespace Emberdelve.Net
{
    /// <summary>
    /// Starts a NetworkManager + UnityTransport from code when (and only when) the command line asks for
    /// a network role (-ember-net server|client|host). With no such argument this class does nothing at
    /// all: no GameObject, no NetworkManager, no log line, NetRole stays Offline.
    ///
    /// U3A1 scope: connection, approval stub (protocol version / size / capacity), clean shutdown and exit
    /// codes. No player spawn, no gameplay. Every line starts with "[Emberdelve][Net][role]".
    /// </summary>
    public sealed class NetBootstrap : MonoBehaviour
    {
        private const float QuitFilePollSeconds = 0.25f;
        private const float ShutdownWaitSeconds = 5f;

        private NetLaunchArgs _args;
        private NetworkManager _nm;
        private string _tag;
        private bool _isServerSide;
        private readonly HashSet<ulong> _remoteClients = new HashSet<ulong>();

        private float _startedAt;
        private float _activeSince = -1f;      // listening (server/host) or connected (client)
        private float _nextQuitPoll;
        private bool _connectedOnce;
        private bool _shuttingDown;
        private float _shutdownDeadline;
        private int _exitCode;
        private string _shutdownReason = "";
        private bool _quitIssued;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Entry()
        {
            NetLaunchArgs args = NetLaunchArgs.Parse(Environment.GetCommandLineArgs());

            if (!args.IsValid)
            {
                // Only complain when the user clearly tried to use the network switches.
                Debug.LogError(NetTuning.LogPrefix + "[boot] invalid arguments: " + string.Join("; ", args.Errors));
                QuitProcess(NetExitCodes.BadArguments);
                return;
            }

            if (args.Mode == NetLaunchMode.Offline) return; // offline: absolutely nothing happens

            GameObject go = new GameObject("EmberdelveNetBootstrap");
            DontDestroyOnLoad(go);
            go.AddComponent<NetBootstrap>().Configure(args);
        }

        private void Configure(NetLaunchArgs args)
        {
            _args = args;
            switch (args.Mode)
            {
                case NetLaunchMode.Server: NetRole.Set(NetRoleKind.DedicatedServer); _tag = "server"; _isServerSide = true; break;
                case NetLaunchMode.Host: NetRole.Set(NetRoleKind.Host); _tag = "host"; _isServerSide = true; break;
                default: NetRole.Set(NetRoleKind.Client); _tag = "client"; _isServerSide = false; break;
            }
        }

        private void Log(string message) { Debug.Log(NetTuning.LogPrefix + "[" + _tag + "] " + message); }
        private void LogErr(string message) { Debug.LogError(NetTuning.LogPrefix + "[" + _tag + "] " + message); }

        private void Start()
        {
            _startedAt = Time.unscaledTime;
            Application.runInBackground = true;
            if (_isServerSide && Application.isBatchMode) Application.targetFrameRate = 60;

            Log("boot protocol=" + NetTuning.ProtocolVersion + " tick=" + NetTuning.TickRate +
                " port=" + _args.Port + " address=" + _args.Address + " listen=" + _args.ListenAddress +
                " maxPlayers=" + _args.MaxPlayers + " slot=" + _args.Slot + " class=" + _args.ClassId +
                " autoquit=" + _args.AutoQuitSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                " batch=" + Application.isBatchMode + " pid=" + System.Diagnostics.Process.GetCurrentProcess().Id);

            try
            {
                if (!BuildAndStart()) Fail(NetExitCodes.StartFailed, "start failed");
            }
            catch (Exception e)
            {
                LogErr("start exception: " + e);
                Fail(NetExitCodes.StartFailed, "start exception");
            }
        }

        private bool BuildAndStart()
        {
            GameObject go = gameObject;
            _nm = go.AddComponent<NetworkManager>();
            UnityTransport transport = go.AddComponent<UnityTransport>();

            int protocol = _args.ProtocolOverride >= 0 ? _args.ProtocolOverride : NetTuning.ProtocolVersion;

            NetworkConfig config = new NetworkConfig
            {
                NetworkTransport = transport,
                ProtocolVersion = (ushort)NetTuning.ProtocolVersion,
                TickRate = NetTuning.TickRate,
                ConnectionApproval = true,
                PlayerPrefab = null,
                EnableSceneManagement = false,
                ConnectionData = NetConnectPayload.Encode(protocol, _args.Slot, _args.ClassId)
            };
            _nm.NetworkConfig = config;

            if (_args.DisconnectTimeoutMs > 0)
            {
                transport.DisconnectTimeoutMS = _args.DisconnectTimeoutMs;
                transport.HeartbeatTimeoutMS = Mathf.Max(100, Mathf.Min(500, _args.DisconnectTimeoutMs / 4));
            }

            _nm.OnClientConnectedCallback += OnClientConnected;
            _nm.OnClientDisconnectCallback += OnClientDisconnected;
            _nm.OnTransportFailure += OnTransportFailure;
            _nm.OnServerStarted += OnServerStarted;

            if (_isServerSide)
            {
                transport.SetConnectionData(_args.ListenAddress, (ushort)_args.Port, _args.ListenAddress);
                _nm.ConnectionApprovalCallback = Approve;
                bool ok = _args.Mode == NetLaunchMode.Host ? _nm.StartHost() : _nm.StartServer();
                return ok;
            }

            transport.SetConnectionData(_args.Address, (ushort)_args.Port);
            Log("connecting address=" + _args.Address + " port=" + _args.Port + " timeout=" + _args.ConnectTimeoutSeconds + "s");
            return _nm.StartClient();
        }

        // ---- server side -----------------------------------------------------------------------

        private void Approve(NetworkManager.ConnectionApprovalRequest request, NetworkManager.ConnectionApprovalResponse response)
        {
            bool isHostSelf = request.ClientNetworkId == NetworkManager.ServerClientId;
            int admitted = isHostSelf ? 0 : _remoteClients.Count + (_args.Mode == NetLaunchMode.Host ? 1 : 0);
            NetApprovalResult r = NetApproval.Evaluate(request.Payload, NetTuning.ProtocolVersion, admitted, _args.MaxPlayers);

            response.Approved = r.Approved;
            response.CreatePlayerObject = false;
            response.Pending = false;
            response.Reason = r.Approved ? "" : r.Reason;

            Log("approval id=" + request.ClientNetworkId + " approved=" + r.Approved +
                " slot=" + (r.Slot.Length == 0 ? "-" : r.Slot) + " class=" + (r.ClassId.Length == 0 ? "-" : r.ClassId) +
                " reason=" + (r.Approved ? "-" : r.Reason));
        }

        private void OnServerStarted()
        {
            _activeSince = Time.unscaledTime;
            Log("listening port=" + _args.Port + " address=" + _args.ListenAddress + " protocol=" + NetTuning.ProtocolVersion);
        }

        // ---- shared callbacks ------------------------------------------------------------------

        private void OnClientConnected(ulong id)
        {
            if (_isServerSide)
            {
                if (id != NetworkManager.ServerClientId) _remoteClients.Add(id);
                Log("client connected id=" + id + " total=" + _remoteClients.Count);
                return;
            }

            _connectedOnce = true;
            _activeSince = Time.unscaledTime;
            Log("connected id=" + id);
        }

        private void OnClientDisconnected(ulong id)
        {
            if (_isServerSide)
            {
                _remoteClients.Remove(id);
                Log("client disconnected id=" + id + " total=" + _remoteClients.Count);
                return;
            }

            string reason = _nm != null ? _nm.DisconnectReason : "";
            if (string.IsNullOrEmpty(reason)) reason = "-";

            if (_shuttingDown)
            {
                Log("disconnected id=" + id + " (requested)");
                return;
            }

            if (!_connectedOnce)
            {
                Log("rejected reason='" + reason + "'");
                Fail(NetExitCodes.ConnectFailed, "rejected");
            }
            else
            {
                Log("disconnected unexpectedly reason='" + reason + "'");
                Fail(NetExitCodes.UnexpectedDisconnect, "connection lost");
            }
        }

        private void OnTransportFailure()
        {
            LogErr("transport failure");
            if (!_shuttingDown)
            {
                Fail(_connectedOnce || _isServerSide ? NetExitCodes.TransportFailure : NetExitCodes.ConnectFailed,
                    "transport failure");
            }
        }

        // ---- lifecycle -------------------------------------------------------------------------

        private void Update()
        {
            if (_args == null || _quitIssued) return;
            float now = Time.unscaledTime;

            if (_shuttingDown)
            {
                bool stopped = _nm == null || (!_nm.IsListening && !_nm.ShutdownInProgress);
                if (stopped || now >= _shutdownDeadline)
                {
                    if (!stopped && _exitCode == NetExitCodes.Ok) _exitCode = NetExitCodes.ShutdownTimeout;
                    FinishQuit(stopped);
                }
                return;
            }

            if (_isServerSide)
            {
                if (_activeSince >= 0f && _args.AutoQuitSeconds > 0f && now - _activeSince >= _args.AutoQuitSeconds)
                {
                    BeginShutdown("autoquit");
                    return;
                }

                if (!string.IsNullOrEmpty(_args.QuitFile) && now >= _nextQuitPoll)
                {
                    _nextQuitPoll = now + QuitFilePollSeconds;
                    if (File.Exists(_args.QuitFile)) BeginShutdown("quit-file");
                }
                return;
            }

            // client
            if (!_connectedOnce)
            {
                if (now - _startedAt >= _args.ConnectTimeoutSeconds)
                {
                    Log("connect timeout after " + _args.ConnectTimeoutSeconds + "s");
                    Fail(NetExitCodes.ConnectFailed, "connect timeout");
                }
                return;
            }

            if (_args.AutoQuitSeconds > 0f && now - _activeSince >= _args.AutoQuitSeconds)
                BeginShutdown("autoquit");
        }

        private void Fail(int exitCode, string reason)
        {
            if (_quitIssued) return;
            if (_exitCode == NetExitCodes.Ok) _exitCode = exitCode;
            BeginShutdown("fail: " + reason);
        }

        private void BeginShutdown(string reason)
        {
            if (_shuttingDown) return;
            _shuttingDown = true;
            _shutdownReason = reason;
            _shutdownDeadline = Time.unscaledTime + ShutdownWaitSeconds;
            Log("shutdown begin reason=" + reason + " players=" + _remoteClients.Count);

            if (_nm != null && (_nm.IsListening || _nm.IsServer || _nm.IsClient)) _nm.Shutdown();
        }

        private void FinishQuit(bool clean)
        {
            if (_quitIssued) return;
            _quitIssued = true;
            Log("shutdown complete clean=" + clean + " players=" + _remoteClients.Count + " exit=" + _exitCode);
            QuitProcess(_exitCode);
        }

        private static void QuitProcess(int code)
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit(code);
#endif
        }

        private void OnDestroy()
        {
            if (_nm == null) return;
            _nm.OnClientConnectedCallback -= OnClientConnected;
            _nm.OnClientDisconnectCallback -= OnClientDisconnected;
            _nm.OnTransportFailure -= OnTransportFailure;
            _nm.OnServerStarted -= OnServerStarted;
        }
    }
}
