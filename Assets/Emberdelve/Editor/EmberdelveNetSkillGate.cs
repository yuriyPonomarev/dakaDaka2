using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Emberdelve.Data;
using Emberdelve.Net.Rules;
using Emberdelve.Skills;
using Emberdelve.Stats;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Emberdelve.EditorTools
{
    /// <summary>
    /// U3B2 acceptance gate: the production server-owned skill channel (active skills, slots 0 and 1) over the wire, real independent
    /// processes on loopback.
    ///
    ///   Unity.exe -batchmode -nographics -projectPath . -logFile Logs/net_skill.log
    ///             -executeMethod Emberdelve.EditorTools.EmberdelveNetSkillGate.Run
    ///
    /// (Do NOT pass -quit.) Optional: -ember-proof-dir ABS_DIR (default ProjectRoot/Builds/net-skill/yyyyMMdd-HHmmss),
    /// -ember-proof-reuse-exe ABS_EXE (skip the build; the exe must be a development net-proof player built from THIS source: the
    /// U3B2 RPCs and launch arguments must be in it). A live Editor only blocks and returns; the Editor is exited only in batch mode,
    /// through EmberdelveGateExit.
    ///
    /// Every expected number is DERIVED from the authored data at run time, never typed in: the warrior's mana pool is
    /// AttributeRules.Derive over the class asset (INT modifier included, so 70 and not the authored base 75 today), the costs are
    /// the class's two active skills' EffectiveManaCost, the cooldown bounds are the skills' EffectiveCooldown times the cooldown
    /// curve's clamp (0.4..1.2), the abuse numbers are read from the server's own "skill-server ready" line.
    ///
    /// Scenario (server --ember-net-no-enemies, reject allowance 8 with no refill, disconnect after 8 excess, memory 600 s):
    ///   A (warrior, skill-cast) and B (warrior, skill-abuse) are admitted. A, on a start file, presses slot 0, slot 0 again and
    ///   slot 1: the server's own caster casts the first (mana = pool - cost0, cooldown 0 set), REFUSES the second with reason Cooldown
    ///   and spends nothing, casts the third (mana = pool - cost0 - cost1); A's owner log shows, for each request, the server's exact
    ///   mana and cooldowns (same text as the server's line) and later the replicated state variable; B, who watches, logs exactly two
    ///   cast visuals of A's object (the skill ids from the data) and A logs none of its own; the client side cast nothing itself
    ///   (3 requests sent, 3 server lines, SkillCaster.ClientRoleBlocks 0). Then B, on a start file, forges requests on A's object,
    ///   replays, sends a malformed slot and floods: every reject is counted on B's connection (identity first, the target
    ///   untouched), the log is rate limited, B is disconnected with "skill abuse" exactly when the excess reaches the threshold
    ///   (total = allowance + threshold), A is not affected (no server line names A after its three casts). B REJOINS with the same
    ///   slot: the server admits it as a new actor ON PROBATION (empty allowance, empty gate, threshold halved from the strike count)
    ///   and disconnects it after only that halved threshold, strike 2. A and the server quit cleanly (exit 0, players=0 senders=0
    ///   registry=0). Static checks: no RPC identity parameters, no obsolete RequireOwnership on any [Rpc] under Net/, the production
    ///   RPCs outside the development guard and the hostile probes inside it, one shared slot constant.
    /// Only PIDs this gate started are ever killed; a kill forced by a timeout is a failure. Final line:
    /// [Emberdelve][NetSkill] PASS|FAIL checks=N failures=M
    /// </summary>
    public static class EmberdelveNetSkillGate
    {
        private const string Tag = "[Emberdelve][NetSkill]";
        private const string S = "[Emberdelve][Net][server] ";
        private const string C = "[Emberdelve][Net][client] ";
        private const int DisconnectTimeoutMs = 4000;
        private const string Num = @"(-?\d+(?:\.\d+)?)";
        private const string NumBody = @"-?\d+(?:\.\d+)?";
        private const string ViewRegex = @"\[client\] skill-view mana=(?<mana>" + NumBody + @") maxMana=(?<max>" + NumBody + @") cd0=(?<cd0>" + NumBody + @") cd1=(?<cd1>" + NumBody + ")";

        // The abuse numbers the gate gives the server (and then checks against the server's own report).
        private const int RejectBurst = 8;
        private const int RejectRate = 0;
        private const int DisconnectAfter = 8;
        private const int MemorySeconds = 600;

        private static int _checks;
        private static int _failures;
        private static readonly List<Process> Started = new List<Process>();

        private sealed class CastLine
        {
            public string Client, PlayerId, Skill, Reason, Cast;
            public int Slot;
            public uint Seq;
            public string ManaBeforeText, ManaAfterText, MaxManaText, Cd0Text, Cd1Text;
            public double ManaBefore, ManaAfter, MaxMana, Cd0, Cd1;
            public long InputAge;
        }

        private sealed class ResultLine
        {
            public string Outcome, Reason, Skill;
            public int Slot;
            public uint Seq;
            public string ManaText, MaxManaText, Cd0Text, Cd1Text;
            public double Mana, Cd0, Cd1;
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
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            string runDir = ArgValue("-ember-proof-dir");
            if (string.IsNullOrEmpty(runDir))
                runDir = Path.Combine(projectRoot, "Builds", "net-skill", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            runDir = Path.GetFullPath(runDir);
            if (runDir.StartsWith(Path.GetFullPath(Application.dataPath), StringComparison.OrdinalIgnoreCase))
            {
                Check(false, "run dir must be outside Assets: " + runDir);
                return 1;
            }

            string logs = Path.Combine(runDir, "logs");
            Directory.CreateDirectory(logs);
            Debug.Log(Tag + " runDir=" + runDir);

            // ---- static checks ----------------------------------------------------------------------
            CheckSource(projectRoot);

            // ---- expectations from the authored data --------------------------------------------------
            CharacterClassData warrior = LoadClass("warrior");
            Check(warrior != null, "warrior class asset found");
            if (warrior == null) return 1;
            Check(warrior.activeSkills != null && warrior.activeSkills.Length == SkillNetLimits.ActiveSlots &&
                  warrior.activeSkills[0] != null && warrior.activeSkills[1] != null,
                "warrior has exactly " + SkillNetLimits.ActiveSlots + " active skills");
            if (warrior.activeSkills == null || warrior.activeSkills.Length != SkillNetLimits.ActiveSlots ||
                warrior.activeSkills[0] == null || warrior.activeSkills[1] == null) return 1;

            SkillData skill0 = warrior.activeSkills[0];
            SkillData skill1 = warrior.activeSkills[1];
            int pool = AttributeRules.Derive(new AttributeSnapshot(warrior.BaseAttributes), warrior.AttributeBases).MaxMana;
            int cost0 = skill0.EffectiveManaCost;
            int cost1 = skill1.EffectiveManaCost;
            Debug.Log(Tag + " derived warrior pool=" + pool + " (authored base " + warrior.EffectiveMaxMana + ") skill0=" + skill0.skillId +
                      " cost=" + cost0 + " cd=" + skill0.EffectiveCooldown + " skill1=" + skill1.skillId + " cost=" + cost1 + " cd=" + skill1.EffectiveCooldown);
            Check(cost0 > 0 && cost1 > 0 && cost0 + cost1 <= pool, "the two costs " + cost0 + "+" + cost1 + " fit the derived pool " + pool + " (the script needs both casts to succeed)");
            Check(skill0.EffectiveCooldown > 0f && skill1.EffectiveCooldown > 0f, "both skills have a cooldown");
            double cdMin0 = skill0.EffectiveCooldown * SkillCooldownRules.MinimumMultiplier - 0.05;
            double cdMax0 = skill0.EffectiveCooldown * 1.2 + 0.05;
            double cdMin1 = skill1.EffectiveCooldown * SkillCooldownRules.MinimumMultiplier - 0.05;
            double cdMax1 = skill1.EffectiveCooldown * 1.2 + 0.05;
            Check(SkillCaster.ActiveSlotCapacity == SkillNetLimits.ActiveSlots, "SkillCaster.ActiveSlotCapacity == SkillNetLimits.ActiveSlots (" + SkillNetLimits.ActiveSlots + ")");

            // ---- 1. player ------------------------------------------------------------------
            string exe = ArgValue("-ember-proof-reuse-exe");
            if (string.IsNullOrEmpty(exe))
            {
                exe = Path.Combine(runDir, "player", "EmberdelveNet.exe");
                bool built = EmberdelveWindowsBuild.BuildNetProofPlayer(exe, out string buildError);
                Check(built, "player build" + (built ? "" : " failed: " + buildError));
                if (!built) return 1;
            }
            else
            {
                Check(File.Exists(exe), "reused player exists: " + exe);
                if (!File.Exists(exe)) return 1;
            }

            // ---- 2. server ------------------------------------------------------------------
            int port = FreeUdpPort();
            Check(port > 0, "free udp port " + port);
            if (port <= 0) return 1;

            string quitServer = Path.Combine(runDir, "server.quit");
            string quitA = Path.Combine(runDir, "client_a.quit");
            string quitB = Path.Combine(runDir, "client_b.quit");
            string quitB2 = Path.Combine(runDir, "client_b2.quit");
            string goA = Path.Combine(runDir, "a.go");
            string goB = Path.Combine(runDir, "b.go");
            string goB2 = Path.Combine(runDir, "b2.go");
            foreach (string f in new[] { quitServer, quitA, quitB, quitB2, goA, goB, goB2 }) if (File.Exists(f)) File.Delete(f);

            string serverLog = Path.Combine(logs, "server.log");
            Process server = Launch(exe, serverLog,
                "-ember-net server -ember-port " + port + " -ember-net-quit-file " + Q(quitServer) +
                " -ember-net-autoquit 600 -ember-net-disconnect-timeout-ms " + DisconnectTimeoutMs +
                " -ember-net-max-players 3 -ember-net-no-enemies" +
                " -ember-net-skill-reject-burst " + RejectBurst + " -ember-net-skill-reject-rate " + RejectRate +
                " -ember-net-skill-disconnect-after " + DisconnectAfter + " -ember-net-skill-memory-seconds " + MemorySeconds);
            Check(server != null, "server process started pid=" + (server != null ? server.Id.ToString() : "-"));
            if (server == null) return 1;

            Check(WaitForLog(serverLog, S + "listening", 60, server), "server logged 'listening'");
            Match ready = WaitForRegex(serverLog, @"\[server\] ready scene=\S+ players=(\d+) senders=(\d+) registry=(\d+)", 90, server);
            Check(ready != null, "server loaded the dungeon and logged 'ready'");
            if (ready == null) return 1;
            Check(ready.Groups[3].Value == "0" && LogContains(serverLog, "enemies=off"), "no enemies: registry=0, enemies=off");

            Match policyLine = WaitForRegex(serverLog, @"\[server\] skill-server ready slots=(?<slots>\d+) gateBurst=\S+ gateRate=\S+ rejectBurst=(?<burst>" + NumBody + @") rejectRefill=(?<refill>" + NumBody +
                                                      @") disconnectAfter=(?<after>\d+) memorySeconds=(?<memory>" + NumBody + ")", 10, server);
            Check(policyLine != null, "server logged its skill policy ('skill-server ready')");
            if (policyLine == null) return 1;
            Check(GI(policyLine, "slots") == SkillNetLimits.ActiveSlots, "server reports " + SkillNetLimits.ActiveSlots + " active slots");
            double policyBurst = GD(policyLine, "burst");
            double policyRate = GD(policyLine, "refill");
            int policyAfter = GI(policyLine, "after");
            double policyMemory = GD(policyLine, "memory");
            Check(policyBurst == RejectBurst && policyRate == RejectRate && policyAfter == DisconnectAfter && policyMemory == MemorySeconds,
                "server policy equals the command line (burst " + policyBurst + ", refill " + policyRate + ", after " + policyAfter + ", memory " + policyMemory + ")");
            SkillAbusePolicy policy = new SkillAbusePolicy(policyBurst, policyRate, policyAfter, policyMemory);
            int firstTotalExpected = (int)policyBurst + policy.ThresholdFor(0);
            int probationThreshold = policy.ThresholdFor(1);
            Check(probationThreshold < policy.ThresholdFor(0), "the probation threshold " + probationThreshold + " is lower than the first-offence threshold " + policy.ThresholdFor(0));

            // ---- 3. A and B -----------------------------------------------------------------
            string logA = Path.Combine(logs, "client_a.log");
            Process a = Launch(exe, logA, ClientArgs(port, "skl-a", "warrior", Q(quitA),
                "-ember-net-script skill-cast -ember-net-script-start-file " + Q(goA)) + " -ember-net-autoquit 500");
            Check(a != null, "client A started");
            if (a == null) return 1;
            Match admA = WaitForRegex(serverLog, @"admitted client=(?<client>\d+) id=(?<id>\d+) party=(?<party>\d+) slot=skl-a class=warrior netId=(?<net>\d+) players=(?<players>\d+)", 90, server);
            Check(admA != null, "server admitted A (warrior)");
            if (admA == null) return 1;
            string clientIdA = admA.Groups["client"].Value;
            string idA = admA.Groups["id"].Value;
            string netIdA = admA.Groups["net"].Value;

            string logB = Path.Combine(logs, "client_b.log");
            Process b = Launch(exe, logB, ClientArgs(port, "skl-b", "warrior", Q(quitB),
                "-ember-net-script skill-abuse -ember-net-script-start-file " + Q(goB)) + " -ember-net-autoquit 500");
            Check(b != null, "client B started");
            if (b == null) return 1;
            Match admB = WaitForRegex(serverLog, @"admitted client=(?<client>\d+) id=(?<id>\d+) party=(?<party>\d+) slot=skl-b class=warrior netId=(?<net>\d+) players=(?<players>\d+)", 90, server);
            Check(admB != null, "server admitted B (warrior)");
            if (admB == null) return 1;
            string clientIdB = admB.Groups["client"].Value;
            string idB = admB.Groups["id"].Value;
            Check(clientIdA != clientIdB && idA != idB, "A and B have distinct client ids and player ids");

            string idsAB = SortedIds(idA, idB);
            Check(WaitForRegex(logA, @"\[client\] owner id=" + idA + @" views=2 owned=1 class=warrior party=\d+ ids=" + idsAB, 60, a) != null, "A settles with views=2");
            Check(WaitForRegex(logB, @"\[client\] owner id=" + idB + @" views=2 owned=1 class=warrior party=\d+ ids=" + idsAB, 60, b) != null, "B settles with views=2");
            Check(LogContains(logA, C + "skill sender started script=skill-cast realInput=False"), "A's skill sender runs the skill-cast script with no real input");
            Check(LogContains(logB, C + "skill sender started script=skill-abuse realInput=False"), "B's skill sender runs the skill-abuse script with no real input");
            Check(!LogContains(serverLog, "skill-cast client=") && !LogContains(serverLog, "skill reject="), "nothing skill-related reached the server before the start files");
            Check(!LogContains(serverLog, "skill-abuse probation"), "no slot is on probation at the start");

            // ---- 4. A casts ------------------------------------------------------------------
            File.WriteAllText(goA, "go");
            bool threeCasts = WaitForCount(serverLog, @"\[server\] skill-cast client=" + clientIdA + @" ", 3, 40, server);
            Check(threeCasts, "server logged three skill-cast lines for A's connection");
            Thread.Sleep(2500); // any extra (duplicate) cast would show up now
            List<CastLine> casts = ParseCasts(serverLog, clientIdA);
            Check(casts.Count == 3, "exactly three requests reached A's caster (no client-side duplicate): " + casts.Count);
            if (casts.Count < 3) return 1;

            CastLine c1 = casts[0], c2 = casts[1], c3 = casts[2];
            Check(c1.Seq == 1 && c2.Seq == 2 && c3.Seq == 3 && c1.Slot == 0 && c2.Slot == 0 && c3.Slot == 1, "sequences 1,2,3 on slots 0,0,1 (one counter across both slots)");
            Check(c1.PlayerId == idA && c2.PlayerId == idA && c3.PlayerId == idA, "all three acted on A's own server rig (id " + idA + ")");
            Check(c1.Skill == skill0.skillId && c3.Skill == skill1.skillId, "server resolved the slots to the data's skills " + skill0.skillId + " / " + skill1.skillId);

            Check(c1.Cast == "True" && c1.Reason == "None", "request 1 cast (slot 0)");
            Check(Near(c1.ManaBefore, pool) && Near(c1.MaxMana, pool), "mana before the first cast is the derived pool " + pool + " (was " + c1.ManaBeforeText + " / " + c1.MaxManaText + ")");
            Check(Near(c1.ManaAfter, pool - cost0), "first cast spent exactly " + cost0 + " (mana " + c1.ManaAfterText + ", expected " + (pool - cost0) + ")");
            Check(c1.Cd0 >= cdMin0 && c1.Cd0 <= cdMax0 && c1.Cd0 > 0.0, "slot 0 cooldown " + c1.Cd0Text + " is positive and inside the data's bounds [" + cdMin0.ToString("F2", CultureInfo.InvariantCulture) + "," + cdMax0.ToString("F2", CultureInfo.InvariantCulture) + "]");
            Check(Near(c1.Cd1, 0.0), "slot 1 cooldown untouched by the first cast");

            Check(c2.Cast == "False" && c2.Reason == "Cooldown", "request 2 (slot 0 again) refused with reason Cooldown (was cast=" + c2.Cast + " reason=" + c2.Reason + ")");
            Check(Near(c2.ManaBefore, c1.ManaAfter) && Near(c2.ManaAfter, c1.ManaAfter), "the refused cast spent no mana (" + c2.ManaBeforeText + " -> " + c2.ManaAfterText + ")");
            Check(c2.Cd0 > 0.0 && c2.Cd0 < c1.Cd0 && Near(c2.Cd1, 0.0), "the refused cast did not restart the cooldown (" + c1.Cd0Text + " -> " + c2.Cd0Text + ")");

            Check(c3.Cast == "True" && c3.Reason == "None", "request 3 cast (slot 1)");
            Check(Near(c3.ManaBefore, c1.ManaAfter), "slot 1 cast started from the mana the first cast left (no regeneration inside the regen delay)");
            Check(Near(c3.ManaAfter, pool - cost0 - cost1), "second cast spent exactly " + cost1 + " more (mana " + c3.ManaAfterText + ", expected " + (pool - cost0 - cost1) + ")");
            Check(c3.Cd1 >= cdMin1 && c3.Cd1 <= cdMax1 && c3.Cd1 > 0.0 && c3.Cd0 > 0.0 && c3.Cd0 < c2.Cd0, "slot 1 cooldown " + c3.Cd1Text + " inside the data's bounds, slot 0 still counting down");
            Check(c1.InputAge >= 0 && c2.InputAge >= 0 && c3.InputAge >= 0, "the server aim came from the movement stream (inputAgeTicks " + c1.InputAge + "," + c2.InputAge + "," + c3.InputAge + ")");

            // The owner's log: the server's exact numbers, per request.
            bool results = WaitForCount(logA, @"\[client\] skill-result seq=", 3, 30, a);
            Check(results, "A logged three skill-result lines");
            List<ResultLine> got = ParseResults(logA);
            Check(got.Count == 3, "A has exactly three results (was " + got.Count + ")");
            if (got.Count == 3)
            {
                CastLine[] expect = { c1, c2, c3 };
                for (int i = 0; i < 3; i++)
                {
                    ResultLine r = got[i];
                    CastLine e = expect[i];
                    bool same = r.Seq == e.Seq && r.Slot == e.Slot && r.Outcome == (e.Cast == "True" ? "Cast" : "Refused") && r.Reason == e.Reason &&
                                r.Skill == e.Skill && r.ManaText == e.ManaAfterText && r.MaxManaText == e.MaxManaText && r.Cd0Text == e.Cd0Text && r.Cd1Text == e.Cd1Text;
                    Check(same, "A's owner log for request " + e.Seq + " equals the server's line (outcome " + r.Outcome + ", reason " + r.Reason + ", mana " + r.ManaText + ", cd " + r.Cd0Text + "/" + r.Cd1Text + ")");
                }
            }

            // The replicated variable follows the server (regeneration is still held back by the regen delay).
            bool viewOk = WaitForPredicate(() =>
            {
                foreach (Match m in Regex.Matches(ReadShared(logA), ViewRegex))
                    if (Near(GD(m, "mana"), pool - cost0 - cost1) && Near(GD(m, "max"), pool) && GD(m, "cd1") > 0.0) return true;
                return false;
            }, 20, a);
            Check(viewOk, "A's replicated skill-view shows mana " + (pool - cost0 - cost1) + " / " + pool + " and slot 1 cooling down");
            Check(LogContains(logA, C + "skill-intent sent seq=1 slot=0") && LogContains(logA, C + "skill-intent sent seq=3 slot=1") &&
                  CountMatches(logA, @"\[client\] skill-intent sent seq=") == 3, "A sent exactly three intents (seq 1..3)");
            Check(LogContains(logA, C + "script skill-cast done sent=3 throttled=0"), "A's script finished: sent=3 throttled=0");

            // Observers: B saw A cast, A saw nothing of its own cast, nobody applied an outcome.
            bool visuals = WaitForCount(logB, @"\[client\] skill-visual netId=" + netIdA + @" ", 2, 20, b);
            Check(visuals, "B logged cast visuals of A's object (netId " + netIdA + ")");
            Thread.Sleep(1000);
            Check(CountMatches(logB, @"\[client\] skill-visual ") == 2, "B logged exactly two visuals (the refused request showed none)");
            Check(LogContains(logB, C + "skill-visual netId=" + netIdA + " playerId=" + idA + " slot=0 skill=" + skill0.skillId) &&
                  LogContains(logB, C + "skill-visual netId=" + netIdA + " playerId=" + idA + " slot=1 skill=" + skill1.skillId),
                "B's visuals name slot 0 / slot 1 and the data's skill ids");
            Check(!LogContains(logA, "skill-visual netId=" + netIdA + " "), "A (the owner) got no observer visual of its own casts (netId " + netIdA + ")");
            Check(!LogContains(logB, "skill-result"), "B received no result for A's requests");
            bool bViewClean = true;
            foreach (Match m in Regex.Matches(ReadShared(logB), ViewRegex))
                if (!Near(GD(m, "mana"), pool) || GD(m, "cd0") != 0.0 || GD(m, "cd1") != 0.0) bViewClean = false;
            Check(bViewClean, "B's own replicated state never shows A's spending (B has not cast)");
            Check(CountMatches(serverLog, @"skill reject=") == 0, "no reject so far: honest traffic is never counted as abuse");

            // ---- 5. B abuses ------------------------------------------------------------------
            File.WriteAllText(goB, "go");
            Match dc1 = WaitForRegex(serverLog, DisconnectRegex(clientIdB), 40, server);
            Check(dc1 != null, "server disconnected B for skill abuse");
            if (dc1 == null) dc1 = Regex.Match("", "x"); // keep going: later sections must still run (all fields read as missing)
            int s1 = GI(dc1, "strikes"), prior1 = GI(dc1, "prior");
            int id1 = GI(dc1, "identity"), inv1 = GI(dc1, "invalid"), rep1 = GI(dc1, "replay"), fl1 = GI(dc1, "flood");
            int total1 = GI(dc1, "total"), excess1 = GI(dc1, "excess"), thr1 = GI(dc1, "threshold");
            Check(dc1.Groups["slot"].Value == "skl-b" && s1 == 1 && prior1 == 0, "first strike for slot skl-b (strikes=" + s1 + " priorStrikes=" + prior1 + ")");
            Check(id1 == 3, "all three forged requests were counted as identity rejects on B's connection (identity=" + id1 + ")");
            Check(inv1 == 1 && rep1 == 1 && fl1 >= 1, "the malformed slot, the replay and the flood were each counted (invalid=" + inv1 + " replay=" + rep1 + " flood=" + fl1 + ")");
            Check(total1 == id1 + inv1 + rep1 + fl1, "every reject of every kind is in the total (" + total1 + ")");
            Check(thr1 == policy.ThresholdFor(0) && excess1 == thr1, "disconnected exactly when the excess reached the threshold (excess " + excess1 + ", threshold " + thr1 + ")");
            Check(total1 == firstTotalExpected, "total rejects at the disconnect = allowance + threshold = " + firstTotalExpected + " (was " + total1 + ")");

            Check(Regex.IsMatch(ReadShared(serverLog), @"\[server\] skill reject=Identity client=" + clientIdB + @" slot=skl-b fault=not-your-object targetNet=" + netIdA + @" count=3 "),
                "the forged requests were charged to B (sender) naming only A's object id " + netIdA + ", fault not-your-object");
            Check(!Regex.IsMatch(ReadShared(serverLog), @"skill reject=\w+ client=" + clientIdA + @" "), "no reject was ever charged to A");
            Check(CountMatches(serverLog, @"skill reject=Flood client=" + clientIdB + @" ") < fl1, "flood logging is rate limited (" + CountMatches(serverLog, @"skill reject=Flood client=" + clientIdB + @" ") + " lines for " + fl1 + " rejects)");
            Check(LogContains(serverLog, "skill reject=Replay client=" + clientIdB) && LogContains(serverLog, "skill reject=Invalid client=" + clientIdB), "replay and invalid rejects are logged");

            bool bExited = WaitForExit(b, 40);
            Check(bExited && b.ExitCode == NetExitCodes.UnexpectedDisconnect, "B exited by itself after the disconnect with code " + NetExitCodes.UnexpectedDisconnect + (bExited ? " (was " + b.ExitCode + ")" : ""));
            Check(LogContains(logB, C + "disconnected unexpectedly reason='skill abuse'"), "B was told why: 'skill abuse'");
            Check(Regex.IsMatch(ReadShared(serverLog), @"removed client=" + clientIdB + @" id=" + idB + @" slot=skl-b reason=disconnect"), "server removed B's actor");
            Check(Regex.IsMatch(ReadShared(serverLog), @"skill-conn final client=" + clientIdB + @" slot=skl-b "), "B's connection state was discarded");

            // A is unaffected by B's abuse.
            Check(!SafeHasExited(a) && !LogContains(logA, "disconnected unexpectedly"), "A stayed connected through B's abuse");
            Check(CountMatches(serverLog, @"skill-cast client=" + clientIdA + @" ") == 3, "A's caster saw nothing after its three requests (still 3)");
            Check(CountMatches(logA, @"\[client\] skill-result seq=") == 3, "A's owner log gained no result from B's abuse");

            // ---- 6. B rejoins ------------------------------------------------------------------
            string logB2 = Path.Combine(logs, "client_b2.log");
            Process b2 = Launch(exe, logB2, ClientArgs(port, "skl-b", "warrior", Q(quitB2),
                "-ember-net-script skill-abuse -ember-net-script-start-file " + Q(goB2)) + " -ember-net-autoquit 500");
            Check(b2 != null, "client B2 (same slot skl-b) started");
            if (b2 == null) return 1;
            Match admB2 = WaitForRegexCount(serverLog, @"admitted client=(?<client>\d+) id=(?<id>\d+) party=(?<party>\d+) slot=skl-b class=warrior netId=(?<net>\d+) players=(?<players>\d+)", 2, 90, server);
            Check(admB2 != null, "server admitted the returning slot as a new actor");
            if (admB2 == null) return 1;
            string clientIdB2 = admB2.Groups["client"].Value;
            string idB2 = admB2.Groups["id"].Value;
            Check(idB2 != idB && clientIdB2 != clientIdB, "the new actor has a new id (" + idB2 + ") and connection (" + clientIdB2 + ")");
            Check(LogContains(serverLog, "rejoin slot=skl-b newId=" + idB2 + " previousId=" + idB), "server logged the rejoin of slot skl-b");

            Match prob = WaitForRegex(serverLog, @"\[server\] skill-abuse probation client=" + clientIdB2 + @" slot=skl-b strikes=(?<strikes>\d+) disconnectAfter=(?<after>\d+) rejectTokens=(?<rtok>" + NumBody +
                                                @") gateTokens=(?<gtok>" + NumBody + @") memoryLeft=(?<left>" + NumBody + ")", 20, server);
            Check(prob != null, "the returning slot was admitted ON PROBATION (abuse memory survived the actor)");
            if (prob == null) prob = Regex.Match("", "x");
            Check(GI(prob, "strikes") == 1, "probation carries the one strike on record");
            Check(GI(prob, "after") == probationThreshold, "probation threshold " + prob.Groups["after"].Value + " = base " + policy.ThresholdFor(0) + " halved once = " + probationThreshold);
            Check(GD(prob, "rtok") == 0.0 && GD(prob, "gtok") == 0.0, "probation starts with an EMPTY reject allowance and an EMPTY skill gate (no fresh burst for a rejoin)");
            double left = GD(prob, "left");
            Check(left > 0.0 && left <= policyMemory, "the memory window is still open (" + prob.Groups["left"].Value + " s of " + policyMemory + ")");
            Check(CountMatches(serverLog, @"skill-abuse probation ") == 1, "only the returning slot was put on probation (A and the first B were not)");

            Check(WaitForRegex(logB2, @"\[client\] owner id=" + idB2 + @" views=2 owned=1 class=warrior ", 60, b2) != null, "B2 settles with views=2");
            File.WriteAllText(goB2, "go");
            Match dc2 = WaitForRegex(serverLog, DisconnectRegex(clientIdB2), 40, server);
            Check(dc2 != null, "server disconnected the returning B again");
            if (dc2 == null) dc2 = Regex.Match("", "x");
            int s2 = GI(dc2, "strikes"), prior2 = GI(dc2, "prior");
            int id2 = GI(dc2, "identity"), total2 = GI(dc2, "total"), excess2 = GI(dc2, "excess"), thr2 = GI(dc2, "threshold");
            int others2 = GI(dc2, "invalid") + GI(dc2, "replay") + GI(dc2, "flood");
            Check(s2 == 2 && prior2 == 1, "second strike recorded (strikes=" + s2 + " priorStrikes=" + prior2 + ")");
            Check(thr2 == probationThreshold && excess2 == thr2, "disconnected at the probation threshold " + probationThreshold + " (excess " + excess2 + ")");
            Check(total2 == thr2 && total2 < total1, "no free allowance on probation: " + total2 + " rejects instead of " + total1);
            Check(id2 == 3 && others2 == total2 - 3, "the forged identity rejects were counted first (identity=" + id2 + ", other kinds=" + others2 + ")");
            bool b2Exited = WaitForExit(b2, 40);
            Check(b2Exited && b2.ExitCode == NetExitCodes.UnexpectedDisconnect, "B2 exited by itself with code " + NetExitCodes.UnexpectedDisconnect + (b2Exited ? " (was " + b2.ExitCode + ")" : ""));
            Check(LogContains(logB2, C + "disconnected unexpectedly reason='skill abuse'"), "B2 was told 'skill abuse'");
            Check(!SafeHasExited(a), "A is still connected after both abuse runs");
            Check(CountMatches(serverLog, @"skill-cast client=" + clientIdA + @" ") == 3 && !Regex.IsMatch(ReadShared(serverLog), @"skill reject=\w+ client=" + clientIdA + @" "), "A was still never touched");

            Check(CountMatches(logA, @"\[client\] skill-visual netId=" + idB + @" ") == 2, "A, as an observer, logged exactly B's two valid casts (netId " + idB + ")");

            // ---- 7. graceful end -----------------------------------------------------------------
            File.WriteAllText(quitA, "quit");
            bool aExited = WaitForExit(a, 30);
            Check(aExited && a.ExitCode == NetExitCodes.Ok, "A left by quit file with exit 0" + (aExited ? " (was " + a.ExitCode + ")" : ""));
            Check(LogContains(logA, C + "skill-sender final sent=3 throttled=0 lastSeq=3 clientCastBlocks=0"), "A's sender summary: 3 sent, 0 throttled, the client cast nothing itself (clientCastBlocks=0)");
            Check(LogContains(logA, C + "shutdown complete clean=True"), "A logged a clean shutdown");
            Check(WaitForRegex(serverLog, @"removed client=" + clientIdA + @" id=" + idA + @" slot=skl-a reason=disconnect players=0 senders=0 registry=0", 30, server) != null, "server removed A: players=0 senders=0 registry=0");
            Check(CountMatches(serverLog, @"skill-conn final client=") == 3, "three connections ended (A, B, B2), each with its final line");

            File.WriteAllText(quitServer, "quit");
            bool sExited = WaitForExit(server, 30);
            Check(sExited, "server exited after the quit file");
            if (sExited) Check(server.ExitCode == NetExitCodes.Ok, "server exit code 0 (was " + server.ExitCode + ")");
            Check(LogContains(serverLog, S + "shutdown players=0 senders=0 registry=0"), "server logged 'shutdown players=0 senders=0 registry=0'");
            Check(LogContains(serverLog, S + "shutdown complete clean=True"), "server logged a clean shutdown");

            // ---- 8. forbidden patterns, leftovers --------------------------------------------------
            string[] forbidden = { "error CS", "NullReferenceException", "Exception:", "InvalidOperationException" };
            foreach (string path in Directory.GetFiles(logs, "*.log"))
            {
                string text = ReadShared(path);
                foreach (string pattern in forbidden)
                    Check(text.IndexOf(pattern, StringComparison.Ordinal) < 0, Path.GetFileName(path) + " has no '" + pattern + "'");
            }

            foreach (Process p in Started) Check(SafeHasExited(p), "pid " + p.Id + " is gone");
            return 0;
        }

        // ---- static source checks ---------------------------------------------------------------

        private static void CheckSource(string projectRoot)
        {
            string net = Path.Combine(projectRoot, "Assets", "Emberdelve", "Net");
            string rules = Path.Combine(projectRoot, "Assets", "Emberdelve", "NetRules");
            string runtime = Path.Combine(projectRoot, "Assets", "Emberdelve", "Runtime");

            int rpcs = 0, identityParams = 0, obsolete = 0;
            if (Directory.Exists(net))
            {
                foreach (string file in Directory.GetFiles(net, "*.cs", SearchOption.AllDirectories))
                {
                    string text = File.ReadAllText(file);
                    int from = 0;
                    while (true)
                    {
                        int at = text.IndexOf("[Rpc(", from, StringComparison.Ordinal);
                        if (at < 0) break;
                        from = at + 5;
                        int close = text.IndexOf(']', at);
                        int open = close < 0 ? -1 : text.IndexOf('(', close);
                        int end = open < 0 ? -1 : text.IndexOf(')', open);
                        if (close < 0 || end < 0) continue;
                        rpcs++;
                        if (text.Substring(at, close - at).IndexOf("RequireOwnership", StringComparison.Ordinal) >= 0) obsolete++;
                        string parameters = text.Substring(open, end - open + 1);
                        if (Regex.IsMatch(parameters, @"(?i)(player|combat|party|sender|actor)\s*_?id"))
                        {
                            identityParams++;
                            Debug.LogError(Tag + " RPC with an identity parameter in " + Path.GetFileName(file) + ": " + parameters);
                        }
                    }
                }
            }

            Check(rpcs >= 6, "source scan found the RPCs under Net/ (" + rpcs + ")");
            Check(identityParams == 0, "no [Rpc] signature under Net/ carries a player/combat/party/sender id parameter");
            Check(obsolete == 0, "no [Rpc] attribute under Net/ uses the obsolete RequireOwnership (InvokePermission instead)");

            string rig = ReadFile(Path.Combine(net, "NetPlayerRig.cs"));
            foreach (string name in new[] { "SkillIntentRpc", "SkillResultRpc", "SkillVisualRpc" })
            {
                int at = rig.IndexOf("public void " + name + "(", StringComparison.Ordinal);
                Check(at > 0 && !InsideDevelopmentGuard(rig, at), name + " exists outside the development guard (compiled in release builds)");
            }

            Check(rig.IndexOf("[Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]\n        public void SkillIntentRpc", StringComparison.Ordinal) >= 0 ||
                  rig.IndexOf("[Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]\r\n        public void SkillIntentRpc", StringComparison.Ordinal) >= 0,
                "SkillIntentRpc is a reliable (default delivery) server RPC with InvokePermission Everyone (the server handler is the check)");
            foreach (string name in new[] { "ForgeOwnerTestRpc", "ForgeMoveTestRpc" })
            {
                int at = rig.IndexOf("public void " + name + "(", StringComparison.Ordinal);
                Check(at > 0 && InsideDevelopmentGuard(rig, at), name + " stays inside the development guard");
            }

            Check(rig.IndexOf("SkillIntentProofRpc", StringComparison.Ordinal) < 0, "the U3B1 development proof RPC is gone (the probes use the production channel)");
            string sender = ReadFile(Path.Combine(net, "NetSkillSender.cs"));
            int abuse = sender.IndexOf("private void RunAbuse()", StringComparison.Ordinal);
            int proof = sender.IndexOf("private void RunProof()", StringComparison.Ordinal);
            Check(abuse > 0 && InsideDevelopmentGuard(sender, abuse) && proof > 0 && InsideDevelopmentGuard(sender, proof), "the hostile skill probes (abuse, proof) are inside the development guard");

            Check(ReadFile(Path.Combine(runtime, "Skills", "SkillCaster.cs")).IndexOf("SkillNetLimits.ActiveSlots", StringComparison.Ordinal) >= 0, "SkillCaster.ActiveSlotCapacity is declared from SkillNetLimits.ActiveSlots");
            string gateSource = ReadFile(Path.Combine(rules, "SkillIntentGate.cs"));
            Check(gateSource.IndexOf("SkillNetLimits.ActiveSlots", StringComparison.Ordinal) >= 0 && !Regex.IsMatch(gateSource, @"slot\s*>=\s*\d"), "SkillIntentGate bounds the slot with SkillNetLimits.ActiveSlots (no literal)");
        }

        /// <summary>True when the character index lies inside an unclosed "#if DEVELOPMENT_BUILD" block (nested #if blocks are tracked).</summary>
        private static bool InsideDevelopmentGuard(string text, int index)
        {
            string[] lines = text.Substring(0, index).Split('\n');
            List<bool> stack = new List<bool>();
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.StartsWith("#if ", StringComparison.Ordinal)) stack.Add(line.IndexOf("DEVELOPMENT_BUILD", StringComparison.Ordinal) >= 0 && line.IndexOf('!') < 0);
                else if (line.StartsWith("#endif", StringComparison.Ordinal) && stack.Count > 0) stack.RemoveAt(stack.Count - 1);
                else if (line.StartsWith("#else", StringComparison.Ordinal) && stack.Count > 0) stack[stack.Count - 1] = false;
            }

            for (int i = 0; i < stack.Count; i++) if (stack[i]) return true;
            return false;
        }

        // ---- parsing -------------------------------------------------------------------------------

        private static string DisconnectRegex(string clientId)
        {
            return @"\[server\] skill-abuse disconnect client=" + clientId + @" slot=(?<slot>\S+) reason='skill abuse' strikes=(?<strikes>\d+) priorStrikes=(?<prior>\d+) identity=(?<identity>\d+) invalid=(?<invalid>\d+) replay=(?<replay>\d+) flood=(?<flood>\d+) total=(?<total>\d+) excess=(?<excess>\d+) threshold=(?<threshold>\d+) memorySeconds=\S+";
        }

        /// <summary>Named integer group; a missing or malformed value fails a check and reads as int.MinValue (never throws).</summary>
        private static int GI(Match m, string group)
        {
            string s = m != null ? m.Groups[group].Value : "";
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)) return v;
            Check(false, "log field '" + group + "' missing or not an integer ('" + s + "')");
            return int.MinValue;
        }

        private static double GD(Match m, string group)
        {
            string s = m != null ? m.Groups[group].Value : "";
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) return v;
            Check(false, "log field '" + group + "' missing or not a number ('" + s + "')");
            return double.NaN;
        }

        private static long GL(Match m, string group)
        {
            string s = m != null ? m.Groups[group].Value : "";
            if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long v)) return v;
            Check(false, "log field '" + group + "' missing or not an integer ('" + s + "')");
            return long.MinValue;
        }

        private static List<CastLine> ParseCasts(string serverLog, string clientId)
        {
            List<CastLine> list = new List<CastLine>();
            string pattern = @"\[server\] skill-cast client=" + clientId + @" id=(?<id>\d+) slot=(?<slot>\d) seq=(?<seq>\d+) skill=(?<skill>\S+) cast=(?<cast>True|False) reason=(?<reason>\w+) " +
                             @"manaBefore=(?<mb>" + NumBody + @") manaAfter=(?<ma>" + NumBody + @") maxMana=(?<max>" + NumBody + @") cd0=(?<cd0>" + NumBody + @") cd1=(?<cd1>" + NumBody +
                             @") aimYaw=" + NumBody + @" aimPitch=" + NumBody + @" inputAgeTicks=(?<age>-?\d+)";
            foreach (Match m in Regex.Matches(ReadShared(serverLog), pattern))
            {
                list.Add(new CastLine
                {
                    Client = clientId,
                    PlayerId = m.Groups["id"].Value,
                    Slot = GI(m, "slot"),
                    Seq = (uint)Math.Max(0, GL(m, "seq")),
                    Skill = m.Groups["skill"].Value,
                    Cast = m.Groups["cast"].Value,
                    Reason = m.Groups["reason"].Value,
                    ManaBeforeText = m.Groups["mb"].Value, ManaBefore = GD(m, "mb"),
                    ManaAfterText = m.Groups["ma"].Value, ManaAfter = GD(m, "ma"),
                    MaxManaText = m.Groups["max"].Value, MaxMana = GD(m, "max"),
                    Cd0Text = m.Groups["cd0"].Value, Cd0 = GD(m, "cd0"),
                    Cd1Text = m.Groups["cd1"].Value, Cd1 = GD(m, "cd1"),
                    InputAge = GL(m, "age")
                });
            }

            return list;
        }

        private static List<ResultLine> ParseResults(string clientLog)
        {
            List<ResultLine> list = new List<ResultLine>();
            string pattern = @"\[client\] skill-result seq=(?<seq>\d+) slot=(?<slot>\d) outcome=(?<outcome>\w+) reason=(?<reason>\w+) skill=(?<skill>\S+) mana=(?<mana>" + NumBody +
                             @") maxMana=(?<max>" + NumBody + @") cd0=(?<cd0>" + NumBody + @") cd1=(?<cd1>" + NumBody + @") dur0=" + NumBody + @" dur1=" + NumBody;
            foreach (Match m in Regex.Matches(ReadShared(clientLog), pattern))
            {
                list.Add(new ResultLine
                {
                    Seq = (uint)Math.Max(0, GL(m, "seq")),
                    Slot = GI(m, "slot"),
                    Outcome = m.Groups["outcome"].Value,
                    Reason = m.Groups["reason"].Value,
                    Skill = m.Groups["skill"].Value,
                    ManaText = m.Groups["mana"].Value, Mana = GD(m, "mana"),
                    MaxManaText = m.Groups["max"].Value,
                    Cd0Text = m.Groups["cd0"].Value, Cd0 = GD(m, "cd0"),
                    Cd1Text = m.Groups["cd1"].Value, Cd1 = GD(m, "cd1")
                });
            }

            return list;
        }

        private static CharacterClassData LoadClass(string classId)
        {
            foreach (string guid in AssetDatabase.FindAssets("t:CharacterClassData"))
            {
                CharacterClassData data = AssetDatabase.LoadAssetAtPath<CharacterClassData>(AssetDatabase.GUIDToAssetPath(guid));
                if (data != null && string.Equals(data.classId, classId, StringComparison.OrdinalIgnoreCase)) return data;
            }

            return null;
        }

        private static double Dbl(string text) { return double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture); }

        /// <summary>Two numbers as the logs print them (3 decimals) agree.</summary>
        private static bool Near(double a, double b) { return Math.Abs(a - b) <= 0.0105; }

        // ---- helpers ---------------------------------------------------------------------------

        private static string ClientArgs(int port, string slot, string classId, string quitFileQuoted, string extra)
        {
            string args = "-ember-net client -ember-address 127.0.0.1 -ember-port " + port +
                          " -ember-slot " + slot + " -ember-class " + classId +
                          " -ember-net-connect-timeout 60 -ember-net-disconnect-timeout-ms " + DisconnectTimeoutMs;
            if (!string.IsNullOrEmpty(quitFileQuoted)) args += " -ember-net-quit-file " + quitFileQuoted;
            if (!string.IsNullOrEmpty(extra)) args += " " + extra;
            return args;
        }

        private static string SortedIds(string x, string y)
        {
            int a, b;
            if (!int.TryParse(x, out a) || !int.TryParse(y, out b)) return x + "," + y;
            return a < b ? a + "," + b : b + "," + a;
        }

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

        private static bool WaitForPredicate(Func<bool> condition, double seconds, Process owner)
        {
            DateTime end = DateTime.UtcNow.AddSeconds(seconds);
            while (true)
            {
                if (condition()) return true;
                if (DateTime.UtcNow >= end) return false;
                if (owner != null && SafeHasExited(owner)) return condition();
                Thread.Sleep(200);
            }
        }

        private static bool WaitForLog(string path, string needle, double seconds, Process owner)
        {
            return WaitForPredicate(() => LogContains(path, needle), seconds, owner);
        }

        private static bool WaitForCount(string path, string pattern, int count, double seconds, Process owner)
        {
            return WaitForPredicate(() => CountMatches(path, pattern) >= count, seconds, owner);
        }

        private static Match WaitForRegex(string path, string pattern, double seconds, Process owner)
        {
            return WaitForRegexCount(path, pattern, 1, seconds, owner);
        }

        /// <summary>The <paramref name="nth"/> (1-based) match of the pattern in the log.</summary>
        private static Match WaitForRegexCount(string path, string pattern, int nth, double seconds, Process owner)
        {
            Match found = null;
            WaitForPredicate(() =>
            {
                MatchCollection all = Regex.Matches(ReadShared(path), pattern);
                if (all.Count < nth) return false;
                found = all[nth - 1];
                return true;
            }, seconds, owner);
            return found;
        }

        private static int CountMatches(string path, string pattern)
        {
            return Regex.Matches(ReadShared(path), pattern).Count;
        }

        private static bool LogContains(string path, string needle)
        {
            return ReadShared(path).IndexOf(needle, StringComparison.Ordinal) >= 0;
        }

        private static string ReadFile(string path)
        {
            try { return File.Exists(path) ? File.ReadAllText(path) : ""; }
            catch (IOException) { return ""; }
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
