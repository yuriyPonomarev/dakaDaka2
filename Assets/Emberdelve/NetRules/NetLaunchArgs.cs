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

    /// <summary>Which socket layer the transport runs on (Docs/Tasks/UTP_RESET.md).</summary>
    public enum NetUdpPath
    {
        /// <summary>The package's own UDP socket, untouched (the control of the probe).</summary>
        Stock = 0,
        /// <summary>The package's own UDP socket, SIO_UDP_CONNRESET / SIO_UDP_NETRESET switched off after the fact (default).</summary>
        Scan = 1,
        /// <summary>A custom INetworkInterface over System.Net.Sockets (needs the EMBER_UTP_MANAGED_SOCKET define).</summary>
        Managed = 2,
        /// <summary>UnityTransport.UseWebSockets (TCP; no ICMP involved).</summary>
        WebSockets = 3
    }

    /// <summary>Shared constants of the network layer (no engine or package dependency).</summary>
    public static class NetTuning
    {
        /// <summary>Wire protocol version checked in connection approval.</summary>
        public const int ProtocolVersion = 10;

        public const int DefaultPort = 7777;
        public const string DefaultAddress = "127.0.0.1";
        public const string LoopbackListen = "127.0.0.1";
        public const string AnyListen = "0.0.0.0";

        /// <summary>NGO network tick rate (user decision: 30 Hz).</summary>
        public const uint TickRate = 30;

        /// <summary>The fixed step the server simulates one input frame with (1 / <see cref="TickRate"/>).</summary>
        public const float TickSeconds = 1f / TickRate;

        public const int DefaultMaxPlayers = 9;
        public const int DefaultConnectTimeoutSeconds = 15;

        /// <summary>Client: wait after a requested shutdown finished, before the process quits (ms).</summary>
        public const int DefaultClientLingerMs = 250;

        /// <summary>The class a client asks for when none is given (a server-approved id; the server resolves it).</summary>
        public const string DefaultClassId = "warrior";
        public const string LogPrefix = "[Emberdelve][Net]";
        public const string ModularDungeonSceneName = "Emberdelve_ModularCrypts";
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
    ///   -ember-net-corpse-proof-server      server, development-only: register a unique-UID corpse model and stage reach/LOS checks
    ///   -ember-net-corpse-proof-action-file PATH   server, development-only: stage B as distant, occluded, then nearby
    ///   -ember-net-corpse-proof-role a|b    client, development-only: drive real corpse state/transfer RPCs from the process gate
    ///   -ember-net-combat-enemy-proof-server server, development-only: stage ene-a beside a live enemy with clear line of sight for the real melee proof
    ///   -ember-net-combat-test-fixture-server server, development-only: stage two gate players in validated clear positions in the modular entry room
    ///   -ember-net-door-proof-server      server, development-only: live door state, authority, range, LOS and late-join proof
    ///   -ember-net-door-proof-role a|b|late client, development-only: participate in the live door proof
    ///   -ember-net-door-proof-action-file PATH client: command file used to issue proof door toggles
    ///   -ember-net-floor-loot-proof-server [true]    server, development-only: stage a unique rolled world pickup for the floor-loot process gate
    ///   -ember-net-floor-loot-proof-role a|b|late    client, development-only: participate in the floor-loot proof
    ///   -ember-net-floor-loot-proof-action-file PATH server: staged far/LOS/contest commands from the process gate
    ///   -ember-net-door-proof-server-action-file PATH server: stage the proof actor at range or behind an occluder
    ///   -ember-net-script SPEC               client test driver (dev/proof): "" (none), "forge-owner", or a movement script / comma separated
    ///                                        chain such as "walk-forward:3@90,sprint-forward:1.5@270,jump" (see NetScriptSpec)
    ///   -ember-net-script-start-file PATH    client: the script starts when this file exists (default: 1.5 s after the player spawned)
    ///   -ember-net-script-yaw DEGREES        client: view yaw for steps without @yaw (default: the yaw the server spawned the player with)
    ///   -ember-net-dev-shove SLOT:YAW[:M]    server, development builds only (U3A4b proof): teleport the player of that dev slot once, M meters (default 2)
    ///                                        along world yaw YAW, to force a prediction snap (see NetDevShove)
    ///   -ember-net-dev-shove-file PATH       server: the shove happens when this file exists (default: never)
    ///   -ember-net-udp-path stock|scan|managed|ws   socket layer of every role (default scan; see Docs/Tasks/UTP_RESET.md):
    ///                                        scan = stock socket, then find it and turn off SIO_UDP_CONNRESET / SIO_UDP_NETRESET (Windows);
    ///                                        managed = custom INetworkInterface over System.Net.Sockets; ws = WebSockets (TCP)
    ///   -ember-net-udp-reset-fix [true|false] legacy spelling: true = scan, false = stock (an explicit -ember-net-udp-path wins)
    ///   -ember-net-client-linger-ms MS       client: after a requested shutdown finished, wait this long before the process quits
    ///                                        (default 250; 0 = quit at once)
    ///   -ember-net-skill-reject-burst N      server (U3B2): rejected skill requests one connection may send for free before they count
    ///                                        as excess (token bucket size, default 8, 1..1000)
    ///   -ember-net-skill-reject-rate R       server: that allowance refills R rejects per second (default 1, 0..1000)
    ///   -ember-net-skill-disconnect-after N  server: excess rejects that disconnect the client (default 8, 1..100000); a slot
    ///                                        returning on probation needs N shifted right once per remembered strike (min 1)
    ///   -ember-net-skill-memory-seconds S    server: how long a slot disconnected for abuse is remembered (default 120, 0..86400; 0 = never)
    ///   -ember-net-party-size N              server/host (U3B3 proof): lowers the largest party to N players (1..3); 1 puts every player in a party
    ///                                        of their own, so players are hostile to each other (PvP). Default: the authored GameConfig value
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
        /// <summary>Development-only acceptance proof for a two-client modular-map server session.</summary>
        public bool ModularMapProofServer { get; private set; }
        public string Script { get; private set; } = "";
        /// <summary>The parsed script, null when none was given.</summary>
        public NetScriptSpec ScriptSpec { get; private set; }
        public string ScriptStartFile { get; private set; } = "";
        public bool InventoryContestServer { get; private set; }
        public bool FloorLootProofServer { get; private set; }
        public string FloorLootProofRole { get; private set; } = "";
        public string FloorLootProofActionFile { get; private set; } = "";
        public bool InventoryDropProofServer { get; private set; }
        public string InventoryDropProofRole { get; private set; } = "";
        public string InventoryDropProofActionFile { get; private set; } = "";
        public string InventoryDropProofExpectedFile { get; private set; } = "";
        /// <summary>Development-only server fixture for the real corpse container RPC proof.</summary>
        public bool CorpseProofServer { get; private set; }
        /// <summary>Development-only proof that an actual admitted actor death commits corpse custody with rollback/retry.</summary>
        public bool CorpseDeathProofServer { get; private set; }
        public string CorpseDeathProofActionFile { get; private set; } = "";
        /// <summary>Development-only client probe role for the real corpse container RPC proof.</summary>
        public string CorpseProofRole { get; private set; } = "";
        /// <summary>Development-only server action file used to stage corpse proof range/LOS scenarios.</summary>
        public string CorpseProofActionFile { get; private set; } = "";
        /// <summary>Development-only server fixture for the real enemy melee proof gate.</summary>
        public bool CombatEnemyProofServer { get; private set; }
        /// <summary>Development-only real-network combat gate fixture: stage two admitted players in the entry module arena.</summary>
        public bool CombatTestFixtureServer { get; private set; }
        /// <summary>Development-only server fixture that positions extraction proof actors at the real extraction point.</summary>
        public bool ExtractionProofServer { get; private set; }
        /// <summary>Development-only short raid deadline proof; never changes the GameConfig asset.</summary>
        public bool TimeoutProofServer { get; private set; }
        /// <summary>Development-only client probe: start a real extraction hold when the scripted movement start file appears.</summary>
        public bool ExtractionProofOwner { get; private set; }
        /// <summary>Development-only live door state / authority proof fixture.</summary>
        public bool DoorProofServer { get; private set; }
        /// <summary>Development-only live door proof client role: a, b, or late.</summary>
        public string DoorProofRole { get; private set; } = "";
        /// <summary>Development-only client action file used by the real door RPC proof.</summary>
        public string DoorProofActionFile { get; private set; } = "";
        /// <summary>Development-only server action file for range and LOS rejection stages.</summary>
        public string DoorProofServerActionFile { get; private set; } = "";
        /// <summary>Optional development-only action file used to trigger an authoritative extraction interruption after begin.</summary>
        public string ExtractionProofActionFile { get; private set; } = "";
        public string InventoryContestRole { get; private set; } = "";
        public bool HasScriptYaw { get; private set; }
        public float ScriptYaw { get; private set; }
        /// <summary>U3A4b development shove (server); null when not given.</summary>
        public NetDevShove DevShove { get; private set; }
        public string DevShoveFile { get; private set; } = "";
        /// <summary>The socket layer (default <see cref="NetUdpPath.Scan"/>).</summary>
        public NetUdpPath UdpPath { get; private set; } = NetUdpPath.Scan;
        private bool _udpPathExplicit;
        /// <summary>True when the stock socket is kept and its connection-reset notifications are switched off (Windows).</summary>
        public bool UdpResetFix { get { return UdpPath == NetUdpPath.Scan; } }
        /// <summary>Client side: extra wait after a requested shutdown before the process quits (ms, default 250, 0 = off).</summary>
        public int ClientLingerMs { get; private set; } = NetTuning.DefaultClientLingerMs;
        /// <summary>U3B2 server abuse numbers (see <see cref="SkillAbusePolicy"/>).</summary>
        public double SkillRejectBurst { get; private set; } = SkillAbusePolicy.DefaultRejectBurst;
        public double SkillRejectRate { get; private set; } = SkillAbusePolicy.DefaultRejectRefillPerSecond;
        public int SkillDisconnectAfter { get; private set; } = SkillAbusePolicy.DefaultDisconnectExcess;
        public double SkillMemorySeconds { get; private set; } = SkillAbusePolicy.DefaultMemorySeconds;
        /// <summary>U3B3 server: largest party size override (1..3); 0 = keep the authored value.</summary>
        public int PartySizeOverride { get; private set; }

        /// <summary>The abuse policy these arguments describe.</summary>
        public SkillAbusePolicy SkillAbusePolicy
        {
            get { return new SkillAbusePolicy(SkillRejectBurst, SkillRejectRate, SkillDisconnectAfter, SkillMemorySeconds); }
        }

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
                case "ember-net-modular-map": // obsolete opt-out; recognized only so old launch scripts fail closed
                case "ember-net-modular-proof-server":
                case "ember-net-inventory-contest-server":
                case "ember-net-floor-loot-proof-server":
                case "ember-net-inventory-drop-proof-server":
                case "ember-net-corpse-proof-server":
                case "ember-net-corpse-death-proof-server":
                case "ember-net-combat-enemy-proof-server":
                case "ember-net-combat-test-fixture-server":
                case "ember-net-extraction-proof-server":
                case "ember-net-timeout-proof-server":
                case "ember-net-door-proof-server":
                case "ember-net-door-proof-role":
                case "ember-net-door-proof-action-file":
                case "ember-net-door-proof-server-action-file":
                case "ember-net-floor-loot-proof-role":
                case "ember-net-floor-loot-proof-action-file":
                case "ember-net-inventory-drop-proof-role":
                case "ember-net-inventory-drop-proof-action-file":
                case "ember-net-inventory-drop-proof-expected-file":
                case "ember-net-extraction-proof-owner":
                case "ember-net-extraction-proof-action-file":
                case "ember-net-inventory-contest-role":
                case "ember-net-corpse-proof-role":
                case "ember-net-corpse-proof-action-file":
                case "ember-net-corpse-death-proof-action-file":
                case "ember-net-script":
                case "ember-net-script-start-file":
                case "ember-net-script-yaw":
                case "ember-net-dev-shove":
                case "ember-net-dev-shove-file":
                case "ember-net-udp-reset-fix":
                case "ember-net-udp-path":
                case "ember-net-client-linger-ms":
                case "ember-net-skill-reject-burst":
                case "ember-net-skill-reject-rate":
                case "ember-net-skill-disconnect-after":
                case "ember-net-skill-memory-seconds":
                case "ember-net-party-size":
                    return true;
                default:
                    return false;
            }
        }

        private static bool IsFlag(string name) { return name == "ember-net-no-enemies" || name == "ember-net-modular-proof-server" || name == "ember-net-udp-reset-fix" || name == "ember-net-inventory-contest-server" || name == "ember-net-floor-loot-proof-server" || name == "ember-net-inventory-drop-proof-server" || name == "ember-net-corpse-proof-server" || name == "ember-net-corpse-death-proof-server" || name == "ember-net-combat-enemy-proof-server" || name == "ember-net-combat-test-fixture-server" || name == "ember-net-extraction-proof-server" || name == "ember-net-timeout-proof-server" || name == "ember-net-extraction-proof-owner" || name == "ember-net-door-proof-server"; }

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
                case "ember-net-modular-map":
                    _errors.Add("map selection is no longer supported; every gameplay mode uses Modular Crypts");
                    break;
                case "ember-net-modular-proof-server":
                    {
                        string b = value.ToLowerInvariant();
                        if (b == "true" || b == "1" || b == "") ModularMapProofServer = true;
                        else if (b == "false" || b == "0") ModularMapProofServer = false;
                        else _errors.Add("invalid modular-proof-server '" + value + "' (true|false)");
                    }
                    break;
                case "ember-net-inventory-contest-server":
                    InventoryContestServer = value == "true" || value == "1";
                    break;
                case "ember-net-floor-loot-proof-server":
                    FloorLootProofServer = value == "true" || value == "1";
                    break;
                case "ember-net-floor-loot-proof-role":
                    value = value.Trim().ToLowerInvariant();
                    if (value == "a" || value == "b" || value == "late") FloorLootProofRole = value;
                    else _errors.Add("invalid floor-loot proof role '" + value + "' (a|b|late)");
                    break;
                case "ember-net-floor-loot-proof-action-file":
                    FloorLootProofActionFile = value;
                    break;
                case "ember-net-inventory-drop-proof-server":
                    InventoryDropProofServer = value == "true" || value == "1";
                    break;
                case "ember-net-inventory-drop-proof-role":
                    value = value.Trim().ToLowerInvariant();
                    if (value == "owner" || value == "peer" || value == "late") InventoryDropProofRole = value;
                    else _errors.Add("invalid inventory-drop proof role '" + value + "' (owner|peer|late)");
                    break;
                case "ember-net-inventory-drop-proof-action-file":
                    InventoryDropProofActionFile = value;
                    break;
                case "ember-net-inventory-drop-proof-expected-file":
                    InventoryDropProofExpectedFile = value;
                    break;
                case "ember-net-corpse-proof-server":
                    CorpseProofServer = value == "true" || value == "1";
                    break;
                case "ember-net-corpse-death-proof-server":
                    CorpseDeathProofServer = value == "true" || value == "1";
                    break;
                case "ember-net-corpse-death-proof-action-file":
                    CorpseDeathProofActionFile = value;
                    break;
                case "ember-net-corpse-proof-role":
                    value = value.Trim().ToLowerInvariant();
                    if (value == "a" || value == "b") CorpseProofRole = value;
                    else _errors.Add("invalid corpse proof role '" + value + "' (a|b)");
                    break;
                case "ember-net-corpse-proof-action-file":
                    CorpseProofActionFile = value;
                    break;
                case "ember-net-combat-enemy-proof-server":
                    CombatEnemyProofServer = value == "true" || value == "1";
                    break;
                case "ember-net-combat-test-fixture-server":
                    CombatTestFixtureServer = value == "true" || value == "1";
                    break;
                case "ember-net-extraction-proof-server":
                    ExtractionProofServer = value == "true" || value == "1";
                    break;
                case "ember-net-timeout-proof-server":
                    TimeoutProofServer = value == "true" || value == "1";
                    break;
                case "ember-net-door-proof-server":
                    DoorProofServer = value == "true" || value == "1";
                    break;
                case "ember-net-door-proof-role":
                    value = value.Trim().ToLowerInvariant();
                    if (value == "a" || value == "b" || value == "late") DoorProofRole = value;
                    else _errors.Add("invalid door proof role '" + value + "' (a|b|late)");
                    break;
                case "ember-net-door-proof-action-file":
                    DoorProofActionFile = value;
                    break;
                case "ember-net-door-proof-server-action-file":
                    DoorProofServerActionFile = value;
                    break;
                case "ember-net-extraction-proof-owner":
                    ExtractionProofOwner = value == "true" || value == "1";
                    break;
                case "ember-net-extraction-proof-action-file":
                    ExtractionProofActionFile = value;
                    break;
                case "ember-net-inventory-contest-role":
                    value = value.Trim().ToLowerInvariant();
                    if (value == "a" || value == "b") InventoryContestRole = value;
                    else _errors.Add("invalid inventory contest role '" + value + "' (a|b)");
                    break;
                case "ember-net-udp-reset-fix":
                    {
                        string f = value.ToLowerInvariant();
                        if (f == "true" || f == "1" || f == "") { if (!_udpPathExplicit) UdpPath = NetUdpPath.Scan; }
                        else if (f == "false" || f == "0") { if (!_udpPathExplicit) UdpPath = NetUdpPath.Stock; }
                        else _errors.Add("invalid udp-reset-fix '" + value + "' (true|false)");
                    }
                    break;
                case "ember-net-udp-path":
                    switch (value.ToLowerInvariant())
                    {
                        case "stock": case "off": UdpPath = NetUdpPath.Stock; _udpPathExplicit = true; break;
                        case "scan": UdpPath = NetUdpPath.Scan; _udpPathExplicit = true; break;
                        case "managed": UdpPath = NetUdpPath.Managed; _udpPathExplicit = true; break;
                        case "ws": case "websockets": UdpPath = NetUdpPath.WebSockets; _udpPathExplicit = true; break;
                        default: _errors.Add("invalid udp-path '" + value + "' (stock|scan|managed|ws)"); break;
                    }
                    break;
                case "ember-net-client-linger-ms":
                    if (TryInt(value, out int linger) && linger >= 0 && linger <= 5000) ClientLingerMs = linger;
                    else _errors.Add("invalid client linger '" + value + "' (0..5000)");
                    break;
                case "ember-net-skill-reject-burst":
                    if (TryDouble(value, out double rejectBurst) && rejectBurst >= 1.0 && rejectBurst <= 1000.0) SkillRejectBurst = rejectBurst;
                    else _errors.Add("invalid skill reject burst '" + value + "' (1..1000)");
                    break;
                case "ember-net-skill-reject-rate":
                    if (TryDouble(value, out double rejectRate) && rejectRate >= 0.0 && rejectRate <= 1000.0) SkillRejectRate = rejectRate;
                    else _errors.Add("invalid skill reject rate '" + value + "' (0..1000)");
                    break;
                case "ember-net-skill-disconnect-after":
                    if (TryInt(value, out int disconnectAfter) && disconnectAfter >= 1 && disconnectAfter <= 100000) SkillDisconnectAfter = disconnectAfter;
                    else _errors.Add("invalid skill disconnect-after '" + value + "' (1..100000)");
                    break;
                case "ember-net-skill-memory-seconds":
                    if (TryDouble(value, out double memorySeconds) && memorySeconds >= 0.0 && memorySeconds <= 86400.0) SkillMemorySeconds = memorySeconds;
                    else _errors.Add("invalid skill memory seconds '" + value + "' (0..86400)");
                    break;
                case "ember-net-party-size":
                    if (TryInt(value, out int partySize) && partySize >= 1 && partySize <= 3) PartySizeOverride = partySize;
                    else _errors.Add("invalid party size '" + value + "' (1..3)");
                    break;
                case "ember-net-script":
                    {
                        string scriptText = value.Trim().ToLowerInvariant();
                        if (scriptText == "" || scriptText == "none")
                        {
                            Script = "";
                            ScriptSpec = null;
                        }
                        else if (NetScriptSpec.TryParse(scriptText, out NetScriptSpec parsedSpec, out string scriptError))
                        {
                            Script = parsedSpec.Text;
                            ScriptSpec = parsedSpec;
                        }
                        else
                        {
                            _errors.Add("invalid script '" + value + "': " + scriptError);
                        }
                    }
                    break;
                case "ember-net-script-start-file":
                    ScriptStartFile = value;
                    break;
                case "ember-net-script-yaw":
                    if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float scriptYaw) &&
                        !float.IsNaN(scriptYaw) && !float.IsInfinity(scriptYaw))
                    {
                        HasScriptYaw = true;
                        ScriptYaw = MoveInputFrame.WrapDegrees(scriptYaw);
                    }
                    else _errors.Add("invalid script yaw '" + value + "' (degrees)");
                    break;
                case "ember-net-dev-shove":
                    if (NetDevShove.TryParse(value, out NetDevShove shove, out string shoveError)) DevShove = shove;
                    else _errors.Add("invalid dev shove '" + value + "': " + shoveError);
                    break;
                case "ember-net-dev-shove-file":
                    DevShoveFile = value;
                    break;
                case "ember-net-protocol-override":
                    if (TryInt(value, out int po) && po >= 0) ProtocolOverride = po;
                    else _errors.Add("invalid protocol override '" + value + "'");
                    break;
            }
        }

        private static bool TryDouble(string value, out double result)
        {
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result) &&
                   !double.IsNaN(result) && !double.IsInfinity(result);
        }

        private static bool TryInt(string value, out int result)
        {
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
        }
    }
}
