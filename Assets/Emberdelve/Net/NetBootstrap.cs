using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Emberdelve.Core;
using Emberdelve.Net.Rules;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

namespace Emberdelve.Net
{
    /// <summary>
    /// Starts a NetworkManager + UnityTransport from code when (and only when) the command line asks for
    /// a network role (-ember-net server|client|host). With no such argument this class does nothing at
    /// all: no GameObject, no NetworkManager, no log line, NetRole stays Offline.
    ///
    /// U3A1: connection, protocol check, clean shutdown and exit codes. U3A3: the server side moved to NetServer (scene load,
    /// approval rules, spawn on connect, removal on disconnect, status lines) and NGO scene management is on, so the server
    /// loads the dungeon and clients follow. A client process also gets a NetClientProbe (view logging, optional test script).
    /// No gameplay input yet. Every line starts with "[Emberdelve][Net][role]".
    ///
    /// U3A5: the same code path can also be started from the lobby's Online panel (NetLobbyBridge, "interactive" mode): the role is
    /// set when the player clicks Join/Host instead of at launch, the lobby shows the progress, and a refusal, a lost connection, a
    /// cancel or Escape tears the session down and loads the lobby again instead of quitting the process. Quit-file / auto-quit
    /// (tests) still end the process. A process started with -ember-net never becomes interactive.
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
        private NetServer _server;

        private float _startedAt;
        private float _activeSince = -1f;      // listening (server/host) or connected (client)
        private float _nextQuitPoll;
        private bool _connectedOnce;
        private bool _shuttingDown;
        private float _shutdownDeadline;
        private int _exitCode;
        private string _shutdownReason = "";
        private bool _quitIssued;

        // UTP_RESET (Docs/Tasks/UTP_RESET.md)
        private UnityTransport _transport;
        private NetUdpPath _effectivePath = NetUdpPath.Stock;
        private NetUdpResetGuard.ScanJob _scanJob;
        private bool _scanReported;
        private float _stoppedAt = -1f;

        // U3A5 (lobby-started session)
        private bool _interactive;
        private bool _returnToLobby;
        private NetLobbyPhase _endPhase = NetLobbyPhase.Left;
        private string _endText = "";
        private bool _cursorLocked;
        private static NetBootstrap _lobbySession;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Entry()
        {
            // Inert until the lobby's Online panel calls it: only stores two delegates (no object, no log, no NetworkManager).
            NetLobbyBridge.Register(StartFromLobby, CancelFromLobby);

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

        /// <summary>
        /// U3A5: the lobby's Join / Host button. Builds the same argument list the command line would carry (the real command line
        /// is appended last, so test switches such as a quit file, timeouts or an explicit slot still apply and win) and starts the
        /// same bootstrap component. Returns "" on success, else the reason it could not start.
        /// </summary>
        private static string StartFromLobby(bool host, string address, int port, string classId, string slot)
        {
            if (_lobbySession != null) return "a session is already running";
            if (NetRole.IsNetworked) return "this process already has a network role";

            List<string> argv = new List<string>
            {
                "-ember-net", host ? "host" : "client",
                "-ember-port", port.ToString(CultureInfo.InvariantCulture),
                "-ember-slot", slot,
                "-ember-class", classId,
                "-ember-net-connect-timeout", "30"
            };
            if (!host)
            {
                argv.Add("-ember-address");
                argv.Add(address);
            }
            else if (address == NetTuning.AnyListen)
            {
                argv.Add("-ember-listen");
                argv.Add("any");
            }

            argv.AddRange(Environment.GetCommandLineArgs());
            NetLaunchArgs args = NetLaunchArgs.Parse(argv.ToArray());
            if (!args.IsValid) return "invalid arguments: " + string.Join("; ", args.Errors);
            if (args.Mode == NetLaunchMode.Offline || args.Mode == NetLaunchMode.Server) return "this build was started with conflicting network arguments";

            GameObject go = new GameObject("EmberdelveNetBootstrap");
            DontDestroyOnLoad(go);
            NetBootstrap bootstrap = go.AddComponent<NetBootstrap>();
            bootstrap._interactive = true;
            bootstrap.Configure(args);
            _lobbySession = bootstrap;
            return "";
        }

        private static void CancelFromLobby()
        {
            if (_lobbySession != null) _lobbySession.EndSession(NetLobbyPhase.Idle, "cancelled", "cancel");
        }

        /// <summary>The parsed command line of this process (null offline). Read by the client-side movement stream.</summary>
        internal static NetLaunchArgs LaunchArgs { get; private set; }

        private void Configure(NetLaunchArgs args)
        {
            _args = args;
            LaunchArgs = args;
            switch (args.Mode)
            {
                case NetLaunchMode.Server: NetRole.Set(NetRoleKind.DedicatedServer); _tag = "server"; _isServerSide = true; break;
                case NetLaunchMode.Host: NetRole.Set(NetRoleKind.Host); _tag = "host"; _isServerSide = true; break;
                default: NetRole.Set(NetRoleKind.Client); _tag = "client"; _isServerSide = false; break;
            }

            // Proof switch: no enemies in a simulating process. Set before the first scene loads (NetSceneRoles reads it in Awake).
            if (_isServerSide) NetRole.SetEnemiesDisabled(args.NoEnemies);
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
                " maxPlayers=" + _args.MaxPlayers + " slot=" + _args.Slot + " class=" + RequestedClass() +
                " noEnemies=" + _args.NoEnemies + " script=" + (_args.Script.Length == 0 ? "-" : _args.Script) +
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
            _transport = transport;

            // UTP_RESET: which socket layer this role runs on. Printed first so a log always says what it was testing.
            Log("socket-path " + NetSocketPath.Apply(transport, _args.UdpPath, out _effectivePath));

            int protocol = _args.ProtocolOverride >= 0 ? _args.ProtocolOverride : NetTuning.ProtocolVersion;

            NetworkConfig config = new NetworkConfig
            {
                NetworkTransport = transport,
                ProtocolVersion = (ushort)NetTuning.ProtocolVersion,
                TickRate = NetTuning.TickRate,
                ConnectionApproval = true,
                PlayerPrefab = null,
                EnableSceneManagement = true, // U3A3: the server loads the dungeon, clients follow (NetServer.RequestDungeonScene)
                ConnectionData = NetConnectPayload.Encode(protocol, _args.Slot, RequestedClass())
            };
            _nm.NetworkConfig = config;
            GameObject playerPrefab = RegisterPrefabs();

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
                _server = new NetServer(_nm, _args, playerPrefab, Log, LogErr);
                _server.Install();
                bool ok = _args.Mode == NetLaunchMode.Host ? _nm.StartHost() : _nm.StartServer();
                return ok;
            }

            transport.SetConnectionData(_args.Address, (ushort)_args.Port);
            Log("connecting address=" + _args.Address + " port=" + _args.Port + " timeout=" + _args.ConnectTimeoutSeconds + "s");
            gameObject.AddComponent<NetClientProbe>().Configure(_args);
            bool started = _nm.StartClient();
            if (started) StartUdpResetFix(false);
            return started;
        }

        /// <summary>
        /// U3A2: registers the network player prefab (same asset on server and client, so the hashes match). Runs only on a
        /// networked role, before the NetworkManager starts. A missing catalog is logged, not fatal: the U3A1 handshake keeps
        /// working without a player prefab.
        /// </summary>
        private GameObject RegisterPrefabs()
        {
            NetPrefabCatalog catalog = Resources.Load<NetPrefabCatalog>(NetPrefabCatalog.ResourcePath);
            if (catalog == null || catalog.PlayerPrefab == null)
            {
                Log("no network prefab catalog at Resources/" + NetPrefabCatalog.ResourcePath +
                    " (run Emberdelve/Net/Build Net Player Prefab); player objects cannot spawn");
                return null;
            }

            _nm.AddNetworkPrefab(catalog.PlayerPrefab);
            Log("registered network prefab " + catalog.PlayerPrefab.name);
            return catalog.PlayerPrefab;
        }

        /// <summary>The class id this process asks for (client) or hosts as (host): the launch argument, else the default class.</summary>
        private string RequestedClass()
        {
            return string.IsNullOrWhiteSpace(_args.ClassId) ? NetTuning.DefaultClassId : _args.ClassId;
        }

        // ---- server side -----------------------------------------------------------------------

        private void OnServerStarted()
        {
            _activeSince = Time.unscaledTime;
            Log("listening port=" + _args.Port + " address=" + _args.ListenAddress + " protocol=" + NetTuning.ProtocolVersion);
            StartUdpResetFix(true);
            if (_args.UdpPath == NetUdpPath.Managed) Log("socket-path managed detail " + NetSocketPath.ManagedDetail());
            if (_interactive) NetLobbyBridge.Report(NetLobbyPhase.Connected, "hosting on " + _args.ListenAddress + ":" + _args.Port + " (Esc leaves)");
        }

        // ---- shared callbacks ------------------------------------------------------------------

        private void OnClientConnected(ulong id)
        {
            if (_isServerSide)
            {
                if (id != NetworkManager.ServerClientId) _remoteClients.Add(id);
                Log("client connected id=" + id + " total=" + _remoteClients.Count);
                if (_server != null) _server.OnClientConnected(id);
                return;
            }

            _connectedOnce = true;
            _activeSince = Time.unscaledTime;
            Log("connected id=" + id);
            if (_interactive) NetLobbyBridge.Report(NetLobbyPhase.Connected, "connected to " + _args.Address + ":" + _args.Port + " (Esc leaves)");
        }

        private void OnClientDisconnected(ulong id)
        {
            if (_isServerSide)
            {
                _remoteClients.Remove(id);
                Log("client disconnected id=" + id + " total=" + _remoteClients.Count);
                if (_args.UdpPath == NetUdpPath.Managed) Log("socket-path managed detail " + NetSocketPath.ManagedDetail());
                if (_server != null) _server.OnClientDisconnected(id);
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
                Fail(NetExitCodes.ConnectFailed, "rejected", NetLobbyPhase.Refused,
                    reason == "-" ? "could not connect to " + _args.Address + ":" + _args.Port : "refused: " + reason);
            }
            else
            {
                Log("disconnected unexpectedly reason='" + reason + "'");
                Fail(NetExitCodes.UnexpectedDisconnect, "connection lost", NetLobbyPhase.Left,
                    "disconnected from the server" + (reason == "-" ? "" : ": " + reason));
            }
        }

        private void OnTransportFailure()
        {
            LogErr("transport failure");
            if (!_shuttingDown)
            {
                Fail(_connectedOnce || _isServerSide ? NetExitCodes.TransportFailure : NetExitCodes.ConnectFailed,
                    "transport failure", NetLobbyPhase.Failed, "network error (transport failure)");
            }
        }

        // ---- lifecycle -------------------------------------------------------------------------

        private void Update()
        {
            if (_args == null || _quitIssued) return;
            float now = Time.unscaledTime;
            DrainScanLog();

            if (_shuttingDown)
            {
                bool stopped = _nm == null || (!_nm.IsListening && !_nm.ShutdownInProgress);
                if (stopped && ShouldLinger(now)) return;
                if (stopped || now >= _shutdownDeadline)
                {
                    if (!stopped && _exitCode == NetExitCodes.Ok) _exitCode = NetExitCodes.ShutdownTimeout;
                    FinishQuit(stopped);
                }
                return;
            }

            if (_interactive) InteractiveUpdate();

            if (_isServerSide)
            {
                if (_server != null) _server.Tick(now);

                if (_activeSince >= 0f && _args.AutoQuitSeconds > 0f && now - _activeSince >= _args.AutoQuitSeconds)
                {
                    BeginShutdown("autoquit");
                    return;
                }

                PollQuitFile(now);
                return;
            }

            // A client may also be asked to leave gracefully through a file (admission gate; same flag as the server's).
            if (_connectedOnce) PollQuitFile(now);
            if (_shuttingDown) return;

            // client
            if (!_connectedOnce)
            {
                if (now - _startedAt >= _args.ConnectTimeoutSeconds)
                {
                    Log("connect timeout after " + _args.ConnectTimeoutSeconds + "s");
                    Fail(NetExitCodes.ConnectFailed, "connect timeout", NetLobbyPhase.Failed,
                        "no answer from " + _args.Address + ":" + _args.Port + " (timeout)");
                }
                return;
            }

            if (_args.AutoQuitSeconds > 0f && now - _activeSince >= _args.AutoQuitSeconds)
                BeginShutdown("autoquit");
        }

        // ---- UTP_RESET mitigations -------------------------------------------------------------

        /// <summary>
        /// Mitigation (b), client only: after a REQUESTED shutdown has finished (the process exits next), wait ClientLingerMs before
        /// quitting, so the disconnect has left the machine and the server has had time to read it before this process is gone. Not
        /// used for a lost connection (nothing to say to a server that already dropped us), nor for a lobby-started session that
        /// returns to the lobby instead of quitting. Bounded by the shutdown deadline.
        /// </summary>
        private bool ShouldLinger(float now)
        {
            if (_isServerSide || _returnToLobby || _exitCode != NetExitCodes.Ok || _args.ClientLingerMs <= 0) return false;
            if (now >= _shutdownDeadline) return false;
            if (_stoppedAt < 0f)
            {
                _stoppedAt = now;
                Log("linger ms=" + _args.ClientLingerMs + " reason=" + _shutdownReason);
            }

            return now - _stoppedAt < _args.ClientLingerMs / 1000f;
        }

        /// <summary>
        /// Socket path "scan": switch off the Windows UDP connection-reset notifications on the transport's socket. The scan runs on a
        /// background thread (NetUdpResetGuard); its log lines are drained from Update, the result is logged once.
        /// </summary>
        private void StartUdpResetFix(bool serverRole)
        {
            if (_effectivePath != NetUdpPath.Scan)
            {
                if (serverRole) Log("udp-connreset fix off (udp-path=" + _args.UdpPath + ")");
                return;
            }

            if (_scanJob != null) return;
            _scanJob = NetUdpResetGuard.StartAsync(serverRole ? _args.Port : 0, serverRole, 15000);
        }

        private void DrainScanLog()
        {
            if (_scanJob == null) return;
            string line;
            while (_scanJob.TryDequeue(out line)) Log(line);
            if (_scanJob.Done && !_scanReported)
            {
                _scanReported = true;
                Log(_scanJob.Result.Line);
                if (_scanJob.Result.Detail.Length > 0) Log("udp-connreset detail " + _scanJob.Result.Detail);
            }
        }

        private void PollQuitFile(float now)
        {
            if (string.IsNullOrEmpty(_args.QuitFile) || now < _nextQuitPoll) return;
            _nextQuitPoll = now + QuitFilePollSeconds;
            if (File.Exists(_args.QuitFile)) BeginShutdown("quit-file");
        }

        private void Fail(int exitCode, string reason)
        {
            Fail(exitCode, reason, NetLobbyPhase.Failed, reason);
        }

        private void Fail(int exitCode, string reason, NetLobbyPhase lobbyPhase, string lobbyText)
        {
            if (_quitIssued) return;
            if (_exitCode == NetExitCodes.Ok) _exitCode = exitCode;
            if (_interactive && !_returnToLobby)
            {
                _returnToLobby = true;
                _endPhase = lobbyPhase;
                _endText = lobbyText;
            }

            BeginShutdown("fail: " + reason);
        }

        // ---- U3A5: lobby-started session -------------------------------------------------------

        /// <summary>Per frame, lobby-started sessions only: grab the pointer once the player exists, and Escape leaves.</summary>
        private void InteractiveUpdate()
        {
            if (_shuttingDown || _quitIssued) return;

            if (!_cursorLocked && (_connectedOnce || _isServerSide) && HasOwnedRig())
            {
                _cursorLocked = true;
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }

            Keyboard keyboard = Keyboard.current;
            if ((_connectedOnce || _activeSince >= 0f) && keyboard != null && keyboard.escapeKey.wasPressedThisFrame)
                EndSession(NetLobbyPhase.Left, "left the game", "leave");
        }

        private static bool HasOwnedRig()
        {
            IReadOnlyList<NetPlayerRig> rigs = NetPlayerRig.Spawned;
            for (int i = 0; i < rigs.Count; i++)
                if (rigs[i] != null && rigs[i].IsSpawned && rigs[i].IsOwner) return true;
            return false;
        }

        /// <summary>User-requested end of a lobby-started session (Escape, Cancel): shut down, then back to the lobby with a message.</summary>
        private void EndSession(NetLobbyPhase phase, string lobbyText, string why)
        {
            if (!_interactive || _shuttingDown || _quitIssued) return;
            _returnToLobby = true;
            _endPhase = phase;
            _endText = lobbyText;
            BeginShutdown(why);
        }

        private void ReturnToLobby()
        {
            Log("returning to the lobby: " + _endText);
            if (_lobbySession == this) _lobbySession = null;
            LaunchArgs = null;
            NetRole.ResetToOffline();
            NetLobbyBridge.Report(_endPhase, _endText);
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            SceneManager.LoadScene(GameConfig.DefaultLobbySceneName);
            Destroy(gameObject);
        }

        private void BeginShutdown(string reason)
        {
            if (_shuttingDown) return;
            _shuttingDown = true;
            _shutdownReason = reason;
            _shutdownDeadline = Time.unscaledTime + ShutdownWaitSeconds;
            Log("shutdown begin reason=" + reason + " players=" + _remoteClients.Count);

            if (_server != null) _server.OnShutdownBegin();
            if (_nm != null && (_nm.IsListening || _nm.IsServer || _nm.IsClient)) _nm.Shutdown();
        }

        private void FinishQuit(bool clean)
        {
            if (_quitIssued) return;
            _quitIssued = true;
            if (_server != null)
            {
                Log("shutdown " + _server.CountsText());
                _server.Dispose();
            }
            Log("shutdown complete clean=" + clean + " players=" + _remoteClients.Count + " exit=" + _exitCode);
            if (_returnToLobby)
            {
                ReturnToLobby();
                return;
            }

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
