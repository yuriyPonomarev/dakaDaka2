using System;
using System.Collections.Generic;
using System.Globalization;

namespace Emberdelve.Net.Rules
{
    public enum NetLaunchMode
    {
        Offline = 0,
        Server = 1,
        Client = 2,
        Host = 3
    }

    /// <summary>Shared constants of the network layer (no engine or package dependency).</summary>
    public static class NetTuning
    {
        /// <summary>Wire protocol version checked in connection approval.</summary>
        public const int ProtocolVersion = 1;

        public const int DefaultPort = 7777;
        public const string DefaultAddress = "127.0.0.1";
        public const string LoopbackListen = "127.0.0.1";
        public const string AnyListen = "0.0.0.0";

        /// <summary>NGO network tick rate (user decision: 30 Hz).</summary>
        public const uint TickRate = 30;

        public const int DefaultMaxPlayers = 9;
        public const int DefaultConnectTimeoutSeconds = 15;

        /// <summary>The class a client asks for when none is given (a server-approved id; the server resolves it).</summary>
        public const string DefaultClassId = "warrior";
        public const string LogPrefix = "[Emberdelve][Net]";
    }

    /// <summary>Process exit codes of a networked player (0 only for a clean, expected end).</summary>
    public static class NetExitCodes
    {
        public const int Ok = 0;
        public const int BadArguments = 2;
        public const int StartFailed = 10;
        /// <summary>Client could not connect (timeout) or the server refused it.</summary>
        public const int ConnectFailed = 11;
        public const int TransportFailure = 12;
        /// <summary>The connection was lost without this process asking for it.</summary>
        public const int UnexpectedDisconnect = 13;
        public const int ShutdownTimeout = 14;
    }

    /// <summary>
    /// Parsed command line of a networked player. Accepted spellings per option:
    /// "-ember-net server", "--ember-net=server", "-ember-net=server".
    ///
    ///   -ember-net server|client|host        role (absent = Offline, nothing else is read)
    ///   -ember-port N                        (alias -ember-net-port)  default 7777
    ///   -ember-address A                     (alias -ember-net-address) client target, default 127.0.0.1
    ///   -ember-listen loopback|any           server bind, default loopback (any triggers a firewall prompt)
    ///   -ember-slot S                        (alias -ember-net-slot) dev-scoped identity, NOT authentication
    ///   -ember-class C                       (alias -ember-net-class) requested class id string
    ///   -ember-net-max-players N             default 9
    ///   -ember-net-autoquit SECONDS          server/host: from listening; client: from connected; 0 = off
    ///   -ember-net-quit-file PATH            server/host: graceful shutdown when the file appears
    ///   -ember-net-connect-timeout SECONDS   client, default 15
    ///   -ember-net-disconnect-timeout-ms MS  transport disconnect timeout (0 = transport default)
    ///   -ember-net-protocol-override N       test only: the client announces another protocol version
    ///   -ember-net-no-enemies [true|false]   server/host: the dungeon's enemies are not started (proof scenarios); a bare flag = true
    ///   -ember-net-script NAME               client test driver (dev/proof): "" (none) or "forge-owner"
    /// </summary>
    public sealed class NetLaunchArgs
    {
        private readonly List<string> _errors = new List<string>();

        public NetLaunchMode Mode { get; private set; } = NetLaunchMode.Offline;
        public int Port { get; private set; } = NetTuning.DefaultPort;
        public string Address { get; private set; } = NetTuning.DefaultAddress;
        public bool ListenAny { get; private set; }
        public string Slot { get; private set; } = "dev";
        public string ClassId { get; private set; } = "";
        public int MaxPlayers { get; private set; } = NetTuning.DefaultMaxPlayers;
        public float AutoQuitSeconds { get; private set; }
        public string QuitFile { get; private set; } = "";
        public int ConnectTimeoutSeconds { get; private set; } = NetTuning.DefaultConnectTimeoutSeconds;
        public int DisconnectTimeoutMs { get; private set; }
        public int ProtocolOverride { get; private set; } = -1;
        public bool NoEnemies { get; private set; }
        public string Script { get; private set; } = "";

        /// <summary>The client test scripts this build knows (empty = none).</summary>
        public const string ScriptForgeOwner = "forge-owner";

        public IReadOnlyList<string> Errors => _errors;
        public bool IsValid => _errors.Count == 0;
        public string ListenAddress => ListenAny ? NetTuning.AnyListen : NetTuning.LoopbackListen;

        public static NetLaunchArgs Parse(string[] args)
        {
            NetLaunchArgs result = new NetLaunchArgs();
            if (args == null) return result;

            for (int i = 0; i < args.Length; i++)
            {
                string raw = args[i];
                if (string.IsNullOrEmpty(raw) || raw[0] != '-') continue;

                string body = raw.TrimStart('-');
                string name = body;
                string value = null;
                int eq = body.IndexOf('=');
                if (eq >= 0)
                {
                    name = body.Substring(0, eq);
                    value = body.Substring(eq + 1);
                }

                name = name.ToLowerInvariant();
                if (!IsKnown(name)) continue;

                if (value == null && IsFlag(name))
                {
                    // A switch: bare = true, optionally followed by an explicit true/false.
                    value = "true";
                    if (i + 1 < args.Length && IsBoolLiteral(args[i + 1]))
                    {
                        value = args[i + 1];
                        i++;
                    }
                }

                if (value == null)
                {
                    if (i + 1 < args.Length && args[i + 1] != null && !args[i + 1].StartsWith("-", StringComparison.Ordinal))
                    {
                        value = args[i + 1];
                        i++;
                    }
                    else
                    {
                        result._errors.Add("missing value for -" + name);
                        continue;
                    }
                }

                result.Apply(name, value.Trim().Trim('"'));
            }

            return result;
        }

        private static bool IsKnown(string name)
        {
            switch (name)
            {
                case "ember-net":
                case "ember-port":
                case "ember-net-port":
                case "ember-address":
                case "ember-net-address":
                case "ember-listen":
                case "ember-net-listen":
                case "ember-slot":
                case "ember-net-slot":
                case "ember-class":
                case "ember-net-class":
                case "ember-net-max-players":
                case "ember-net-autoquit":
                case "ember-net-quit-file":
                case "ember-net-connect-timeout":
                case "ember-net-disconnect-timeout-ms":
                case "ember-net-protocol-override":
                case "ember-net-no-enemies":
                case "ember-net-script":
                    return true;
                default:
                    return false;
            }
        }

        private static bool IsFlag(string name) { return name == "ember-net-no-enemies"; }

        private static bool IsBoolLiteral(string text)
        {
            if (text == null) return false;
            string t = text.Trim().ToLowerInvariant();
            return t == "true" || t == "false" || t == "1" || t == "0";
        }

        private void Apply(string name, string value)
        {
            switch (name)
            {
                case "ember-net":
                    switch (value.ToLowerInvariant())
                    {
                        case "server": case "dedicated": Mode = NetLaunchMode.Server; break;
                        case "client": Mode = NetLaunchMode.Client; break;
                        case "host": Mode = NetLaunchMode.Host; break;
                        case "offline": case "": Mode = NetLaunchMode.Offline; break;
                        default: _errors.Add("unknown -ember-net value '" + value + "' (server|client|host)"); break;
                    }
                    break;
                case "ember-port":
                case "ember-net-port":
                    if (TryInt(value, out int port) && port >= 1 && port <= 65535) Port = port;
                    else _errors.Add("invalid port '" + value + "' (1..65535)");
                    break;
                case "ember-address":
                case "ember-net-address":
                    if (value.Length == 0) _errors.Add("empty address");
                    else Address = value;
                    break;
                case "ember-listen":
                case "ember-net-listen":
                    string l = value.ToLowerInvariant();
                    if (l == "any") ListenAny = true;
                    else if (l == "loopback") ListenAny = false;
                    else _errors.Add("invalid listen '" + value + "' (loopback|any)");
                    break;
                case "ember-slot":
                case "ember-net-slot":
                    Slot = value;
                    break;
                case "ember-class":
                case "ember-net-class":
                    ClassId = value;
                    break;
                case "ember-net-max-players":
                    if (TryInt(value, out int max) && max >= 1 && max <= 64) MaxPlayers = max;
                    else _errors.Add("invalid max players '" + value + "' (1..64)");
                    break;
                case "ember-net-autoquit":
                    if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float s) &&
                        !float.IsNaN(s) && !float.IsInfinity(s) && s >= 0f) AutoQuitSeconds = s;
                    else _errors.Add("invalid autoquit '" + value + "' (seconds >= 0)");
                    break;
                case "ember-net-quit-file":
                    QuitFile = value;
                    break;
                case "ember-net-connect-timeout":
                    if (TryInt(value, out int ct) && ct >= 1 && ct <= 3600) ConnectTimeoutSeconds = ct;
                    else _errors.Add("invalid connect timeout '" + value + "' (1..3600)");
                    break;
                case "ember-net-disconnect-timeout-ms":
                    if (TryInt(value, out int dt) && dt >= 0 && dt <= 600000) DisconnectTimeoutMs = dt;
                    else _errors.Add("invalid disconnect timeout '" + value + "' (0..600000)");
                    break;
                case "ember-net-no-enemies":
                    {
                        string b = value.ToLowerInvariant();
                        if (b == "true" || b == "1" || b == "") NoEnemies = true;
                        else if (b == "false" || b == "0") NoEnemies = false;
                        else _errors.Add("invalid no-enemies '" + value + "' (true|false)");
                    }
                    break;
                case "ember-net-script":
                    {
                        string name2 = value.ToLowerInvariant();
                        if (name2 == "" || name2 == "none") Script = "";
                        else if (name2 == ScriptForgeOwner) Script = ScriptForgeOwner;
                        else _errors.Add("unknown script '" + value + "' (forge-owner)");
                    }
                    break;
                case "ember-net-protocol-override":
                    if (TryInt(value, out int po) && po >= 0) ProtocolOverride = po;
                    else _errors.Add("invalid protocol override '" + value + "'");
                    break;
            }
        }

        private static bool TryInt(string value, out int result)
        {
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
        }
    }
}
