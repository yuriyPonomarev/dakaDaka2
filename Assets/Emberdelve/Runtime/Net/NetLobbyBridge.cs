using System;

namespace Emberdelve.Core
{
    /// <summary>Where the lobby's Online panel is in the join flow.</summary>
    public enum NetLobbyPhase
    {
        Idle = 0,
        Connecting = 1,
        Connected = 2,
        /// <summary>The server (or the transport) said no before the player ever got in.</summary>
        Refused = 3,
        /// <summary>The player was in and the connection ended (server gone, left with Escape).</summary>
        Left = 4,
        Failed = 5
    }

    /// <summary>
    /// U3A5: the one-way seam between the lobby UI (this assembly, no networking package) and the network layer (Emberdelve.Net).
    /// The lobby asks <see cref="TryStart"/> to start a client or host; the Net assembly registers the handlers before the first scene
    /// loads and reports progress through <see cref="Report"/>. With no Net assembly (or before it registered) <see cref="IsAvailable"/>
    /// is false and the lobby shows no Online panel. Nothing here touches the offline path: it holds a status string and two delegates.
    /// </summary>
    public static class NetLobbyBridge
    {
        /// <summary>Starts a client (host == false) or a host. Returns "" on success, else the reason it could not start.</summary>
        public delegate string StartHandler(bool host, string address, int port, string classId, string slot);

        private static StartHandler _start;
        private static Action _cancel;
        private static string _sessionSlot;

        public static bool IsAvailable => _start != null;
        public static NetLobbyPhase Phase { get; private set; } = NetLobbyPhase.Idle;
        public static string Status { get; private set; } = "";

        /// <summary>Raised whenever <see cref="Phase"/> or <see cref="Status"/> changes.</summary>
        public static event Action Changed;

        /// <summary>
        /// The development-scoped identity this process joins with (NOT authentication; see NETWORK_SPIKE). Generated once per
        /// process, so leaving and joining again in the same session is a rejoin under the same slot; two game windows on one PC get
        /// two different slots. A command-line -ember-slot overrides it.
        /// </summary>
        public static string SessionSlot
        {
            get
            {
                if (string.IsNullOrEmpty(_sessionSlot)) _sessionSlot = "p" + Guid.NewGuid().ToString("N").Substring(0, 8);
                return _sessionSlot;
            }
        }

        public static void Register(StartHandler start, Action cancel)
        {
            _start = start;
            _cancel = cancel;
        }

        /// <summary>Asks the network layer to start. A false result carries the reason (also reported as the status).</summary>
        public static bool TryStart(bool host, string address, int port, string classId, out string error)
        {
            error = "";
            if (_start == null)
            {
                error = "networking is not available in this build";
                Report(NetLobbyPhase.Failed, error);
                return false;
            }

            if (Phase == NetLobbyPhase.Connecting || Phase == NetLobbyPhase.Connected)
            {
                error = "already " + (Phase == NetLobbyPhase.Connecting ? "connecting" : "connected");
                return false;
            }

            Report(NetLobbyPhase.Connecting, host ? "starting host..." : "connecting to " + address + ":" + port + "...");
            string result;
            try { result = _start(host, address, port, classId, SessionSlot); }
            catch (Exception e) { result = "start failed: " + e.Message; }

            if (!string.IsNullOrEmpty(result))
            {
                error = result;
                Report(NetLobbyPhase.Failed, result);
                return false;
            }

            return true;
        }

        public static void Cancel()
        {
            if (_cancel != null) _cancel();
        }

        public static void Report(NetLobbyPhase phase, string status)
        {
            Phase = phase;
            Status = status ?? "";
            Action handler = Changed;
            if (handler != null) handler();
        }

        /// <summary>Back to a blank state (tests; a fresh lobby after a finished session keeps the last message on purpose).</summary>
        public static void ResetForTests()
        {
            Phase = NetLobbyPhase.Idle;
            Status = "";
            _sessionSlot = null;
        }
    }
}
