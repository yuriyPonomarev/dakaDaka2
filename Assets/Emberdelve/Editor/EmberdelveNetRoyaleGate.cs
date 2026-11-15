using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Emberdelve.Net.Rules;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Emberdelve.EditorTools
{
    public static class EmberdelveNetRoyaleGate
    {
        private const string Tag = "[Emberdelve][NetRoyale]";

        private static int _checks;
        private static int _failures;
        private static readonly List<Process> Started = new List<Process>();

        private static string _modeOnly;
        public static void RunTrio()
        {
            _modeOnly = "trio";
            try { Run(); } finally { _modeOnly = null; }
        }
        public static void Run()
        {
            int exit = 1;
            try { exit = Execute(); }
            catch (Exception e)
            {
                Check(false, "gate threw " + e);
                exit = 1;
            }
            finally { KillSurvivors(); }

            string verdict = _failures == 0 ? "PASS" : "FAIL";
            string line = Tag + " " + verdict + " checks=" + _checks + " failures=" + _failures;
            if (_failures == 0) Debug.Log(line); else Debug.LogError(line);

            if (Application.isBatchMode) EmberdelveGateExit.Exit(_failures == 0 && exit == 0 ? 0 : 1);
        }

        private static int Execute()
        {
            string root = Directory.GetParent(Application.dataPath).FullName;
            string runDir = Path.Combine(root, "Builds", "net-royale", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(runDir);
            Debug.Log(Tag + " runDir=" + runDir);
            string exe = EmberdelvePlayerBuildCache.TestPlayerPath(Path.Combine(runDir, "player", "EmberdelveNet.exe"), "net-proof");
            bool built = EmberdelveWindowsBuild.BuildNetProofPlayer(exe, out string buildError);
            Check(built, "player build " + buildError); if (!built) return 1;
            foreach (string mode in _modeOnly == null ? new[] { "solo", "duo", "trio" } : new[] { _modeOnly })
            {
                int before = _failures; RunMode(exe, runDir, mode);
                if (_failures != before) break; // Diagnose the first integration failure before more expensive launches.
            }
            return _failures == 0 ? 0 : 1;
        }

        private static void RunMode(string exe, string root, string mode)
        {
            int capacity = mode == "trio" ? 15 : 16;
            int size = mode == "solo" ? 1 : mode == "duo" ? 2 : 3;
            string dir = Path.Combine(root, mode); Directory.CreateDirectory(dir);
            string hostLog = Path.Combine(dir, "host.log"), clientLog = Path.Combine(dir, "client.log");
            string hostQuit = Path.Combine(dir, "host.quit"), clientQuit = Path.Combine(dir, "client.quit");
            string descentFile = Path.Combine(dir, "descent.request");
            int port = FreeUdpPort(); Check(port > 0, mode + " free port"); if (port <= 0) return;
            Process host = Launch(exe, hostLog, "-ember-net host -ember-port " + port
                + " -ember-royale-mode " + mode + " -ember-royale-map-proof -ember-slot royale-host -ember-class warrior -ember-net-autoquit 180"
                + " -ember-royale-descent-proof-file " + Q(descentFile) + " -ember-net-quit-file " + Q(hostQuit));
            Check(host != null && WaitForLog(hostLog, "listening", 60, host), mode + " host listening");
            if (host == null) return;
            Process client = Launch(exe, clientLog, "-ember-net client -ember-port " + port
                + " -ember-royale-observe -ember-slot royale-client -ember-class archer -ember-net-autoquit 180 -ember-net-connect-timeout 45"
                + " -ember-royale-descent-proof-file " + Q(descentFile) + " -ember-net-quit-file " + Q(clientQuit));
            Check(client != null, mode + " client launched");
            if (client == null) { File.WriteAllText(hostQuit, "quit"); WaitForExit(host, 30); return; }
            bool started = WaitForLog(hostLog, "Royale floor 1 started population=" + capacity, 60, host);
            Check(started, mode + " full population starts");
            Check(WaitForLog(hostLog, "Royale geometry proof BOTH DESTINATIONS ACKNOWLEDGED publicState=true", 60, host), mode + " both destination geometry ACKs return to server");
            string geometryText = ReadShared(clientLog);
            Check(geometryText.Contains("[RoyaleMap] public state ready floor=2"), mode + " client floor2 chests and doors have server state");
            Check(geometryText.Contains("[RoyaleMap] public state ready floor=3"), mode + " client floor3 chests and doors have server state");
            Check(Regex.Matches(geometryText, @"\[RoyaleMap\] geometry ready floor=2 hash=\d+").Count == 1, mode + " client installs floor2 once");
            Check(Regex.Matches(geometryText, @"\[RoyaleMap\] geometry ready floor=3 hash=\d+").Count == 1, mode + " client installs floor3 once");
            Check(WaitForRegex(clientLog, @"owner id=\d+ views=" + capacity + @" owned=1", 20, client) != null,
                mode + " client sees full population and exactly one owned human");
            string serverText = ReadShared(hostLog);
            var bots = Regex.Matches(serverText, @"bot admitted id=(\d+) party=(\d+) floor=1 netId=(\d+)");
            Check(bots.Count == capacity - 2, mode + " exact bot fill: " + bots.Count);
            var ids = new HashSet<int>(); var parties = new Dictionary<int, int>();
            foreach (Match human in Regex.Matches(serverText, @"admitted client=\d+ id=(\d+) party=(\d+)"))
            {
                int id = int.Parse(human.Groups[1].Value), party = int.Parse(human.Groups[2].Value);
                Check(ids.Add(id), mode + " unique human id " + id);
                parties[party] = parties.TryGetValue(party, out int n) ? n + 1 : 1;
            }
            foreach (Match bot in bots)
            {
                int id = int.Parse(bot.Groups[1].Value), party = int.Parse(bot.Groups[2].Value);
                Check(ids.Add(id), mode + " unique bot id " + id);
                parties[party] = parties.TryGetValue(party, out int n) ? n + 1 : 1;
                Check(Regex.IsMatch(serverText, @"player object spawned role=Server netId=" + bot.Groups[3].Value + @" owner=0 playerId=" + id + @"\b"),
                    mode + " server-owned bot is not a local host player " + id);
                Check(WaitForRegex(clientLog, @"floor movement applied id=" + id + @" generation=2 transit=False", 10, client) != null,
                    mode + " observer sees bot arrival " + id);
            }
            Check(ids.Count == capacity, mode + " all actor identities accounted for");
            Check(parties.Count == capacity / size, mode + " expected number of parties");
            foreach (var party in parties) Check(party.Value == size, mode + " full party " + party.Key + " size=" + party.Value);
            // Independent client transforms must actually advance after admission; generation2 is the starting landing.
            var timer = Stopwatch.StartNew(); bool moved = false;
            while (timer.Elapsed.TotalSeconds < 15 && !SafeHasExited(host) && !SafeHasExited(client))
            {
                string text = ReadShared(clientLog);
                foreach (Match bot in bots)
                {
                    var landing = Regex.Match(text, @"floor movement applied id=" + bot.Groups[1].Value
                        + @" generation=2 transit=False pos=\(([-0-9.]+), ?([-0-9.]+), ?([-0-9.]+)\)");
                    if (!landing.Success) continue;
                    var views = Regex.Matches(text.Substring(landing.Index + landing.Length), @"view id=" + bot.Groups[1].Value
                        + @" pos=\(([-0-9.]+),([-0-9.]+),([-0-9.]+)\)");
                    Vector3 first = Position(landing);
                    foreach (Match view in views) if (Vector3.Distance(first, Position(view)) > .3f) { moved = true; break; }
                }
                if (moved) break; Thread.Sleep(200);
            }
            Check(moved, mode + " observer sees adventurer bot movement through the running controller");
            File.WriteAllText(descentFile, "botloot-stage");
            Match botLoot = WaitForRegex(hostLog, @"Royale bot loot staged id=(\d+) uid=([0-9a-f]{32})", 10, host);
            Check(botLoot != null, mode + " bot loot source staged for client observation");
            if (botLoot != null)
            {
                string uid = botLoot.Groups[2].Value;
                File.WriteAllText(descentFile, "botloot-watch " + uid);
                Check(WaitForLog(clientLog, "[RoyaleBotLoot] present uid=" + uid + " count=1", 10, client),
                    mode + " client sees exact public UID before bot pickup");
                File.WriteAllText(descentFile, "botloot-go " + uid);
                Check(WaitForLog(hostLog, "Royale bot loot collected id=" + botLoot.Groups[1].Value + " uid=" + uid + " copies=1", 10, host),
                    mode + " ordinary bot controller collects exact UID once");
                Check(WaitForLog(clientLog, "[RoyaleBotLoot] removed uid=" + uid + " count=0", 10, client),
                    mode + " client removes consumed bot loot source");
            }
            for (int floor = 2; floor <= 3; floor++)
            {
                File.WriteAllText(descentFile, floor.ToString());
                bool arrived = WaitForLog(hostLog, "Royale descent proof floor=" + floor + " population=" + capacity + " fullParties=True", 60, host);
                Check(arrived, mode + " floor" + floor + " full population and preserved human custody");
                if (!arrived) break;
                Check(WaitForLog(clientLog, "[RoyaleMap] source retired floor=" + (floor - 1), 15, client),
                    mode + " client retires acknowledged source floor" + (floor - 1));
                string destinationText = ReadShared(hostLog);
                if (botLoot != null)
                {
                    string botId = botLoot.Groups[1].Value, botNetworkId = "";
                    foreach (Match bot in bots) if (bot.Groups[1].Value == botId) botNetworkId = bot.Groups[3].Value;
                    Check(botNetworkId.Length > 0 && destinationText.Contains("Royale bot descent floor=" + floor
                        + " id=" + botId + " netId=" + botNetworkId + " uid=" + botLoot.Groups[2].Value
                        + " retained=True generation=" + (floor * 2)), mode + " same bot carries picked UID/kit/ammo floor" + floor);
                    Check(WaitForRegex(clientLog, @"floor movement applied id=" + botId + " generation=" + (floor * 2)
                        + " transit=False", 15, client) != null, mode + " client observes original bot arrival floor" + floor);
                }
                Check(Regex.IsMatch(destinationText, @"descent intent sender=\d+ floor=" + (floor - 1) + @" group=\d+ slot=\d+ accepted=True"),
                    mode + " real client portal RPC accepted floor" + (floor - 1));
                Check(ReadShared(clientLog).Contains("[RoyalePortal] interaction floor=" + (floor - 1)), mode + " client uses replicated portal interaction view");
                var arrivals = Regex.Matches(destinationText, @"Royale descent actor floor=" + floor
                    + @" id=(\d+) netId=(\d+) kit=(\S+) retained=True generation=(\d+) magazine=(\d+) reserve=(\d+) pos=\(([-0-9.]+), ?([-0-9.]+), ?([-0-9.]+)\)");
                Check(arrivals.Count == 2, mode + " both human rigs retain all items/ammunition floor" + floor);
                foreach (Match arrival in arrivals)
                {
                    int generation = floor * 2;
                    Check(arrival.Groups[4].Value == generation.ToString(), mode + " successive arrival generation floor" + floor);
                    Check(WaitForRegex(clientLog, @"floor movement applied id=" + arrival.Groups[1].Value + " generation=" + generation + " transit=False", 15, client) != null,
                        mode + " remote observer receives human arrival floor" + floor + " id=" + arrival.Groups[1].Value);
                }
                Match owner = WaitForRegex(clientLog, @"\[RoyaleOwner\] floor=" + floor
                    + @" id=(\d+) netId=(\d+) generation=(\d+) kit=(\S+) magazine=(\d+) reserve=(\d+) remaining=([0-9.]+) pos=\(([-0-9.]+), ?([-0-9.]+), ?([-0-9.]+)\)", 15, client);
                Check(owner != null, mode + " owner has destination clock and inventory floor" + floor);
                if (floor == 3) Check(WaitForLog(clientLog, "[RoyaleExit] floor=3 generation=6", 15, client),
                    mode + " owner final extraction view matches server position and hold duration");
                Check(WaitForRegex(clientLog, @"\[RoyaleZone\] floor=" + floor + " generation=" + (floor * 2) + @" radius=[1-9][0-9.]*", 15, client) != null,
                    mode + " owner receives active destination zone floor" + floor);
                if (owner != null)
                {
                    Match authority = null;
                    foreach (Match arrival in arrivals) if (arrival.Groups[1].Value == owner.Groups[1].Value) authority = arrival;
                    Check(authority != null && owner.Groups[2].Value == authority.Groups[2].Value && owner.Groups[4].Value == authority.Groups[3].Value
                        && owner.Groups[5].Value == authority.Groups[5].Value && owner.Groups[6].Value == authority.Groups[6].Value,
                        mode + " client replica equals retained authority rig/items/ammo floor" + floor);
                    Check(float.Parse(owner.Groups[7].Value, System.Globalization.CultureInfo.InvariantCulture) > 60,
                        mode + " fresh destination clock ignores expired source floor" + floor);
                    if (authority != null)
                    {
                        Match a = Regex.Match(authority.Value, @"pos=\(([-0-9.]+), ?([-0-9.]+), ?([-0-9.]+)\)");
                        Match b = Regex.Match(owner.Value, @"pos=\(([-0-9.]+), ?([-0-9.]+), ?([-0-9.]+)\)");
                        Check(Vector3.Distance(Position(a), Position(b)) <= .2f, mode + " owner remains at authoritative destination landing floor" + floor);
                    }
                }
            }

            Check(ReadShared(hostLog).Contains("descent rejected=identity") && ReadShared(hostLog).Contains("descent rejected=floor-or-slot")
                && Regex.IsMatch(ReadShared(hostLog), @"descent intent sender=\d+ floor=1 group=0 slot=0 accepted=False"),
                mode + " forged owner, invalid floor/slot and closed portal intents refused");
            Match recoveredLoot = Regex.Match(ReadShared(hostLog), @"Royale loot proof id=\d+ key=(\S+) value=([1-9]\d*)");
            Check(recoveredLoot.Success, mode + " remote collected actual floor loot before first descent");
            if (recoveredLoot.Success) for (int floor = 2; floor <= 3; floor++)
                Check(ReadShared(hostLog).Contains("Royale loot retained floor=" + floor + " key=" + recoveredLoot.Groups[1].Value
                    + " found=True value=" + recoveredLoot.Groups[2].Value), mode + " exact found loot identity/count/value survives floor" + floor);
            File.WriteAllText(descentFile, "5");
            Check(WaitForLog(hostLog, "Royale bot final exit approaching id=", 10, host), mode + " carried-loot bot approaches final exit");
            Check(WaitForRegex(hostLog, @"Royale bot final exit id=\d+ uid=[0-9a-f]{32} retained=True held=True", 15, host) != null,
                mode + " normal bot brain completes timed final exit with original UID kit ammo identity");
            File.WriteAllText(descentFile, "4");
            Check(WaitForLog(clientLog, "[RoyaleFinal] client hold started", 20, client), mode + " client begins real final gate hold");
            Check(WaitForRegex(hostLog, @"extract-hold complete sender=\d+ player=\d+ seq=\d+ accepted=True", 15, host) != null,
                mode + " server accepts final extraction after both descents");
            Check(WaitForRegex(clientLog, @"\[RoyaleFinal\] outcome=Extracted id=\d+ extracted=True cache=True", 15, client) != null,
                mode + " owner receives terminal extraction and result cache");
            Match finalLoot = Regex.Match(ReadShared(clientLog), @"\[RoyaleFinal\] outcome=Extracted id=\d+ extracted=True cache=True loot=([0-9.]+)");
            Check(recoveredLoot.Success && finalLoot.Success && float.Parse(finalLoot.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)
                == int.Parse(recoveredLoot.Groups[2].Value), mode + " extracted owner result retains recovered loot value");
            File.WriteAllText(clientQuit, "quit"); Check(WaitForExit(client, 30) && client.ExitCode == 0, mode + " client clean exit");
            File.WriteAllText(hostQuit, "quit"); Check(WaitForExit(host, 30) && host.ExitCode == 0, mode + " host clean exit");
            Check(LogContains(hostLog, "shutdown players=0 senders=0")
                && LogContains(hostLog, "shutdown registeredAdventurers=0 bots=0"),
                mode + " bot and human identities fully retired while map monsters remain independent");
            foreach (var log in new[] { hostLog, clientLog })
                Check(!Regex.IsMatch(ReadShared(log), @"NullReferenceException|InvalidOperationException|IndexOutOfRangeException|Exception:"), mode + " no runtime exception in " + Path.GetFileName(log));
        }

        private static Vector3 Position(Match match) => new Vector3(
            float.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture),
            float.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture),
            float.Parse(match.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture));

        private static void Check(bool ok, string what)
        {
            _checks++;
            if (!ok) _failures++;
            if (ok) Debug.Log(Tag + " ok: " + what); else Debug.LogError(Tag + " FAIL: " + what);
        }

        private static string Q(string path) { return "\"" + path + "\""; }

        private static Process Launch(string exe, string logPath, string gameArgs)
        {
            try
            {
                if (File.Exists(logPath)) File.Delete(logPath);
                ProcessStartInfo info = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = "-batchmode -nographics -logFile " + Q(logPath) + " " + gameArgs,
                    WorkingDirectory = Path.GetDirectoryName(exe),
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                Process process = Process.Start(info);
                if (process != null) Started.Add(process);
                Debug.Log(Tag + " launched pid=" + (process != null ? process.Id.ToString() : "-") + " " + info.Arguments);
                return process;
            }
            catch (Exception e)
            {
                Debug.LogError(Tag + " launch failed: " + e.Message);
                return null;
            }
        }

        private static bool WaitForLog(string path, string needle, double seconds, Process owner)
        {
            DateTime end = DateTime.UtcNow.AddSeconds(seconds);
            while (true)
            {
                if (LogContains(path, needle)) return true;
                if (DateTime.UtcNow >= end) return false;
                if (owner != null && SafeHasExited(owner)) return LogContains(path, needle);
                Thread.Sleep(200);
            }
        }

        private static Match WaitForRegex(string path, string pattern, double seconds, Process owner)
        {
            DateTime end = DateTime.UtcNow.AddSeconds(seconds);
            while (true)
            {
                Match m = Regex.Match(ReadShared(path), pattern);
                if (m.Success) return m;
                if (DateTime.UtcNow >= end) return null;
                if (owner != null && SafeHasExited(owner))
                {
                    m = Regex.Match(ReadShared(path), pattern);
                    return m.Success ? m : null;
                }
                Thread.Sleep(200);
            }
        }

        private static bool LogContains(string path, string needle)
        {
            return ReadShared(path).IndexOf(needle, StringComparison.Ordinal) >= 0;
        }

        private static string ReadShared(string path)
        {
            try
            {
                if (!File.Exists(path)) return "";
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (StreamReader reader = new StreamReader(fs, Encoding.UTF8))
                    return reader.ReadToEnd();
            }
            catch (IOException) { return ""; }
            catch (UnauthorizedAccessException) { return ""; }
        }

        private static bool SafeHasExited(Process p)
        {
            try { return p == null || p.HasExited; }
            catch (Exception) { return true; }
        }

        /// <summary>Waits for a graceful exit. On timeout kills only this process (the caller's check then fails).</summary>
        private static bool WaitForExit(Process p, double seconds)
        {
            try
            {
                if (p.WaitForExit((int)(seconds * 1000))) return true;
                Debug.LogError(Tag + " pid " + p.Id + " did not exit within " + seconds + "s; killing it");
                _failures++;
                _checks++;
                p.Kill();
                p.WaitForExit(5000);
            }
            catch (Exception e)
            {
                Debug.LogError(Tag + " wait/kill failed: " + e.Message);
            }
            return false;
        }

        private static void KillSurvivors()
        {
            foreach (Process p in Started)
            {
                try
                {
                    if (!p.HasExited)
                    {
                        Debug.LogError(Tag + " cleanup: killing own pid " + p.Id);
                        _failures++;
                        _checks++;
                        p.Kill();
                        p.WaitForExit(5000);
                    }
                }
                catch (Exception) { /* already gone */ }
            }
        }

        private static int FreeUdpPort()
        {
            try
            {
                using (UdpClient udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
                    return ((IPEndPoint)udp.Client.LocalEndPoint).Port;
            }
            catch (Exception) { return -1; }
        }

        private static string ArgValue(string name)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1].Trim('"');
            return null;
        }
    }
}
