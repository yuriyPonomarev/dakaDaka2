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
using Emberdelve.Ballistics;
using Emberdelve.Combat;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Net.Rules;
using Emberdelve.Stats;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Emberdelve.EditorTools
{
    /// <summary>
    /// U3B3 acceptance gate: weapon combat over the wire (server authoritative PvP + PvE visibility), real independent processes on loopback.
    ///
    ///   Unity.exe -batchmode -nographics -projectPath . -logFile Logs/net_combat.log
    ///             -executeMethod Emberdelve.EditorTools.EmberdelveNetCombatGate.Run
    ///
    /// (Do NOT pass -quit.) Optional: -ember-proof-dir ABS_DIR (default ProjectRoot/Builds/net-combat/yyyyMMdd-HHmmss),
    /// -ember-proof-reuse-exe ABS_EXE (skip the build; the exe must be a development net-proof player built from THIS source).
    /// A live Editor only blocks and returns; the Editor is exited only in batch mode, through EmberdelveGateExit.
    ///
    /// Four server sessions, each with its own server and clients (--ember-net-party-size 1 puts every player in a party of their own, so
    /// players are hostile):
    ///   1. melee-duel: A and B (warriors) at their spawns. A walks up and swings; the server resolves every swing on A's rig. B takes the
    ///      first hit unguarded, then guards (reduced / blocked hits), then drops the guard and dies. Every hit's damage is re-derived from the
    ///      authored data (weapon base, pattern and combo multipliers, hit-location table, guard reduction, armor formula) from the factors the
    ///      server logged, within a tolerance; the attribute multiplier and the flat bonus are read from the server's report (and bounded).
    ///      Health and death reach both clients through the replicated variable; after death B's actions are refused (reason Dead).
    ///   2. ranged-shot: A (archer) shoots B at a distance computed from the data (the weapon's air-time falloff window), with the pitch
    ///      solved for the weapon's speed and gravity. The server logs the launch and every hit; the gate integrates the flight with
    ///      ProjectileBallistics and checks the contact point, the air time, the falloff scale and the final damage; the full draw's charge
    ///      is the SERVER's measurement (1.0 after a long hold), an early release carries a charge below 1.
    ///   3. combat-abuse: forged, replayed, malformed and flooding intents are rejected and counted on the abuser's connection through the
    ///      shared skill / combat ledger; the victim is never touched; the abuser is disconnected when the excess reaches the threshold.
    ///   4. enemies: a client sees the server's enemies (proxy count equals the server's registry, health reported).
    /// Static checks: no RPC identity parameters, no obsolete RequireOwnership, production RPCs outside the development guard, the hostile
    /// probe inside it, client code never applies damage, the server decides before it executes.
    /// Only PIDs this gate started are ever killed; a kill forced by a timeout is a failure. Final line:
    /// [Emberdelve][NetCombat] PASS|FAIL checks=N failures=M
    /// </summary>
    public static class EmberdelveNetCombatGate
    {
        private const string Tag = "[Emberdelve][NetCombat]";
        private const string S = "[Emberdelve][Net][server] ";
        private const string C = "[Emberdelve][Net][client] ";
        private const int DisconnectTimeoutMs = 4000;
        private const string NumBody = @"-?\d+(?:\.\d+)?";
        private const string N = "(?<{0}>" + NumBody + ")";

        private const int RejectBurst = 8;
        private const int RejectRate = 0;
        private const int DisconnectAfter = 8;
        private const int MemorySeconds = 600;

        private static int _checks;
        private static int _failures;
        private static readonly List<Process> Started = new List<Process>();

        private sealed class Session
        {
            public Process Server;
            public string ServerLog;
            public int Port;
            public string QuitFile;
            public string Name;
        }

        private sealed class HitLine
        {
            public string Source, Kind, Weapon, Location, Blocked, GuardBroken, Killed;
            public int Attacker, Target;
            public double Amount, LocMult, BlockReduction, OutMult, Flat, Armor, Step, Zone, Charge, Air, AirScale, Dist, Px, Py, Pz;
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
                runDir = Path.Combine(projectRoot, "Builds", "net-combat", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            runDir = Path.GetFullPath(runDir);
            if (runDir.StartsWith(Path.GetFullPath(Application.dataPath), StringComparison.OrdinalIgnoreCase))
            {
                Check(false, "run dir must be outside Assets: " + runDir);
                return 1;
            }

            string logs = Path.Combine(runDir, "logs");
            Directory.CreateDirectory(logs);
            Debug.Log(Tag + " runDir=" + runDir);

            // ---- data -------------------------------------------------------------------------------
            CharacterClassData warrior = LoadClass("warrior");
            CharacterClassData archer = LoadClass("archer");
            Check(warrior != null && archer != null, "warrior and archer class assets found");
            if (warrior == null || archer == null) return 1;
            WeaponData sword = warrior.primaryWeapon;
            WeaponData bow = archer.primaryWeapon;
            Check(sword != null && !sword.IsRanged && sword.UsesSweptMelee, "the warrior's primary weapon is a swept melee weapon (" + (sword != null ? sword.weaponId : "-") + ")");
            Check(bow != null && bow.IsRanged, "the archer's primary weapon is ranged (" + (bow != null ? bow.weaponId : "-") + ")");
            if (sword == null || bow == null || !sword.UsesSweptMelee || !bow.IsRanged) return 1;

            List<HitLocationTable> tables = new List<HitLocationTable> { HitLocationTable.Default };
            foreach (string guid in AssetDatabase.FindAssets("t:HitLocationTable"))
            {
                HitLocationTable t = AssetDatabase.LoadAssetAtPath<HitLocationTable>(AssetDatabase.GUIDToAssetPath(guid));
                if (t != null) tables.Add(t);
            }

            int warriorHealth = AttributeRules.Derive(new AttributeSnapshot(warrior.BaseAttributes), warrior.AttributeBases).MaxHealth;
            Check(warriorHealth > 0, "derived warrior health " + warriorHealth + " (authored base " + warrior.maxHealth + ")");

            SwingPatternData pattern0 = sword.comboSteps != null && sword.comboSteps.Length > 0 && sword.comboSteps[0].patternIndex < sword.swingPatterns.Length
                ? sword.swingPatterns[sword.comboSteps[0].patternIndex] : sword.swingPatterns[0];
            SwingTiming timing = pattern0.Timing;
            double swingTotal = timing.Windup + timing.Active + timing.Recovery;
            double reach = pattern0.EffectiveReach(sword);
            double interval = Math.Max(0.4, swingTotal + 0.4);
            double stop = Math.Max(1.0, 0.6 * reach);
            Debug.Log(Tag + " derived sword=" + sword.weaponId + " pattern=" + pattern0.patternId + " swing=" + F(swingTotal) + "s reach=" + F(reach) +
                      " -> interval=" + F(interval) + " stop=" + F(stop));

            // ---- player -------------------------------------------------------------------------------
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

            int rc = MeleeSession(exe, runDir, logs, sword, tables, warriorHealth, interval, stop);
            if (rc != 0) return rc;
            rc = ParrySession(exe, runDir, logs, sword, tables, interval, stop);
            if (rc != 0) return rc;
            rc = RangedSession(exe, runDir, logs, bow, tables);
            if (rc != 0) return rc;
            rc = AbuseSession(exe, runDir, logs);
            if (rc != 0) return rc;
            rc = EnemySession(exe, runDir, logs);
            if (rc != 0) return rc;

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

        // ============================================================================================
        // 1. melee duel
        // ============================================================================================

        private static int MeleeSession(string exe, string runDir, string logs, WeaponData sword, List<HitLocationTable> tables, int warriorHealth, double interval, double stop)
        {
            Debug.Log(Tag + " ---- session 1: melee-duel");
            Session s = StartServer(exe, runDir, logs, "melee", "-ember-net-no-enemies -ember-net-party-size 1");
            if (s == null) return 1;
            Check(LogContains(s.ServerLog, "party size override=1"), "server applied the party size override");
            Check(LogContains(s.ServerLog, S + "combat-server ready kinds=" + CombatNetLimits.KindMax), "server logged 'combat-server ready' with the intent kind count from the shared constant");

            string goA = Path.Combine(runDir, "melee_a.go"), goB = Path.Combine(runDir, "melee_b.go");
            string quitA = Path.Combine(runDir, "melee_a.quit"), quitB = Path.Combine(runDir, "melee_b.quit");
            foreach (string f in new[] { goA, goB, quitA, quitB }) if (File.Exists(f)) File.Delete(f);

            string logA = Path.Combine(logs, "melee_a.log"), logB = Path.Combine(logs, "melee_b.log");
            Process a = Launch(exe, logA, ClientArgs(s.Port, "cmb-a", "warrior", Q(quitA), "-ember-net-script melee-duel -ember-net-script-start-file " + Q(goA)) + " -ember-net-autoquit 500");
            Match admA = WaitForRegex(s.ServerLog, AdmittedRegex("cmb-a", "warrior"), 90, s.Server);
            Check(a != null && admA != null, "server admitted A");
            if (admA == null) return 1;
            Process b = Launch(exe, logB, ClientArgs(s.Port, "cmb-b", "warrior", Q(quitB), "-ember-net-script melee-duel -ember-net-script-start-file " + Q(goB)) + " -ember-net-autoquit 500");
            Match admB = WaitForRegex(s.ServerLog, AdmittedRegex("cmb-b", "warrior"), 90, s.Server);
            Check(b != null && admB != null, "server admitted B");
            if (admB == null) return 1;
            int idA = GI(admA, "id"), idB = GI(admB, "id");
            string clientA = admA.Groups["client"].Value, clientB = admB.Groups["client"].Value;
            Check(idA < idB, "A (the attacker role) has the lower id (" + idA + " < " + idB + ")");
            Check(GI(admA, "party") != GI(admB, "party"), "A and B are in different parties (hostile): " + admA.Groups["party"].Value + " vs " + admB.Groups["party"].Value);

            Match spA = WaitForRegex(s.ServerLog, SpawnRegex(clientA), 10, s.Server);
            Match spB = WaitForRegex(s.ServerLog, SpawnRegex(clientB), 10, s.Server);
            Check(spA != null && spB != null, "server logged both spawn poses");
            double d0 = spA != null && spB != null ? Flat(GD(spA, "x"), GD(spA, "z"), GD(spB, "x"), GD(spB, "z")) : double.NaN;
            Debug.Log(Tag + " spawn distance " + F(d0));
            Check(!double.IsNaN(d0) && d0 > 0.5, "the spawns are apart (" + F(d0) + " m)");

            Check(WaitForRegex(logA, @"\[client\] owner id=" + idA + @" views=2 owned=1", 60, a) != null, "A settles with views=2");
            Check(WaitForRegex(logB, @"\[client\] owner id=" + idB + @" views=2 owned=1", 60, b) != null, "B settles with views=2");
            Check(LogContains(logA, C + "combat sender started script=melee-duel realInput=False"), "A's combat sender runs the melee-duel script with no real input");
            Check(!LogContains(s.ServerLog, "combat-intent client=") && !LogContains(s.ServerLog, "combat-hit "), "no combat reached the server before the start files");

            File.WriteAllText(goA, "go stop=" + F(stop) + " interval=" + F(interval) + " presses=80");
            File.WriteAllText(goB, "go hold=4");

            Match dead = WaitForRegex(s.ServerLog, @"\[server\] combat-vitals id=" + idB + @" health=0\.000 max=(?<max>" + NumBody + @") dead=True", 120, s.Server);
            Check(dead != null, "B died on the server (health 0, dead=True)");
            if (dead == null) { DumpTail(s.ServerLog); }
            if (dead != null) Check(Near(GD(dead, "max"), warriorHealth), "B's maximum health is the derived pool " + warriorHealth + " (was " + dead.Groups["max"].Value + ")");
            Thread.Sleep(5000); // B's post-mortem attempts and A's script end

            List<HitLine> hits = ParseHits(s.ServerLog);
            List<HitLine> onB = hits.FindAll(h => h.Attacker == idA && h.Target == idB && h.Source == "Melee");
            Check(onB.Count >= 3, "the server resolved at least 3 melee hits of A on B (" + onB.Count + ")");
            Check(hits.TrueForAll(h => h.Attacker == idA), "every hit of the session was A's (B never attacked)");
            double sum = 0.0, sumBeforeLast = 0.0;
            int unblocked = 0, blocked = 0;
            for (int i = 0; i < onB.Count; i++)
            {
                HitLine h = onB[i];
                string why = MeleeMismatch(h, sword, tables);
                Check(why == null, "hit " + (i + 1) + ": " + h.Location + " " + (h.Blocked == "True" ? "BLOCKED" : "open") + " amount " + F(h.Amount) + " matches the data-derived value" + (why != null ? " (" + why + ")" : ""));
                sum += h.Amount;
                if (i < onB.Count - 1) sumBeforeLast += h.Amount;
                if (h.Blocked == "True") blocked++; else unblocked++;
            }

            Check(onB.Count > 0 && onB[0].Blocked == "False", "the first hit landed on an unguarded B");
            Check(blocked >= 1 && onB.Exists(h => h.Blocked == "True" && h.BlockReduction > 0.0), "at least one hit was reduced by B's guard (blocked=" + blocked + ")");
            Check(unblocked >= 2, "unguarded hits landed too (" + unblocked + ")");
            if (onB.Count > 0) Check(onB[onB.Count - 1].Killed == "True", "the last hit is the killing hit");
            Check(sumBeforeLast < warriorHealth && sum >= warriorHealth - 0.2, "health lost equals the reported amounts: " + F(sumBeforeLast) + " < " + warriorHealth + " <= " + F(sum));

            // B's server-side actions: guard accepted, then every action after death refused with Dead.
            Check(CountMatches(s.ServerLog, @"combat-intent client=" + clientB + @" id=" + idB + @" kind=GuardDown seq=\d+ outcome=Accepted reason=None") >= 1, "B's guard-down was accepted by the server");
            Check(CountMatches(s.ServerLog, @"combat-intent client=" + clientB + @" id=" + idB + @" kind=GuardUp seq=\d+ outcome=Accepted reason=None") >= 1, "B's guard-up was accepted");
            int refusedDead = CountMatches(s.ServerLog, @"combat-intent client=" + clientB + @" id=" + idB + @" kind=\w+ seq=\d+ outcome=Refused reason=Dead");
            Check(refusedDead >= 3, "after death B's actions were refused with reason Dead (" + refusedDead + ")");
            Check(CountMatches(s.ServerLog, @"combat-swing id=" + idB + " ") == 0, "B never swung");
            Check(CountMatches(s.ServerLog, @"combat-intent client=" + clientA + @" id=" + idA + @" kind=AttackPress seq=\d+ outcome=Accepted") >= onB.Count, "every hit came from an accepted AttackPress of A");
            Check(CountMatches(s.ServerLog, @"combat-intent client=" + clientA + @" id=" + idA + @" kind=\w+ seq=\d+ outcome=\w+ reason=\w+ charge=" + NumBody + @" aimYaw=" + NumBody + @" aimPitch=" + NumBody + @" inputAgeTicks=\d+") >= 1,
                "intent lines carry the server aim and the input age");
            Check(CountMatches(s.ServerLog, @"combat reject=") == 0, "honest traffic produced no combat reject");

            // Replication: death and health reach both clients; clients never log a locally applied hit.
            foreach (KeyValuePair<string, string> pair in new[] { new KeyValuePair<string, string>("A", logA), new KeyValuePair<string, string>("B", logB) })
            {
                Check(WaitForRegex(pair.Value, @"\[client\] vitals id=" + idB + @" health=0\.0 max=\S+ dead=True owned=\w+ applied=True", 20, null) != null, pair.Key + " saw B's death through the replicated variable");
                Check(CountMatches(pair.Value, @"\[client\] vitals id=" + idB + " ") >= 2, pair.Key + " saw B's health change before it died");
                Check(!LogContains(pair.Value, "combat-hit src=") && !LogContains(pair.Value, "combat-swing id=") && !LogContains(pair.Value, "skill-cast client="), pair.Key + " logged no server-side combat line");
            }

            int serverSwings = CountMatches(s.ServerLog, @"combat-swing id=" + idA + " ");
            Check(serverSwings >= onB.Count, "the server logged the swings (" + serverSwings + ")");
            Check(WaitForCount(logB, @"\[client\] server-swing id=" + idA + " ", serverSwings, 20, b), "B (observer) was told every swing of A (" + serverSwings + ")");
            Check(WaitForCount(logB, @"\[client\] server-hit src=Melee attacker=" + idA + " target=" + idB + " ", onB.Count, 20, b), "B saw every hit on itself as a server report");
            Check(WaitForCount(logA, @"\[client\] server-hit src=Melee attacker=" + idA + " target=" + idB + " ", onB.Count, 20, a), "A saw every hit it caused as a server report (no local resolution)");
            Check(LogContains(logB, "blocked=True"), "B saw a blocked hit report");
            Check(WaitForCount(logA, @"\[client\] combat-result seq=\d+ kind=AttackPress outcome=Accepted", onB.Count, 20, a), "A's owner log holds an accepted result per swing");
            Check(WaitForCount(logB, @"\[client\] combat-result seq=\d+ kind=\w+ outcome=Refused reason=Dead", 3, 20, b), "B's owner log shows the Dead refusals");

            QuitClient(a, quitA, logA, "A");
            QuitClient(b, quitB, logB, "B");
            Check(LogContains(logA, C + "combat-sender final sent=") && LogContains(logA, "clientBlockedDamage=0"), "A's final summary: the client applied no damage (clientBlockedDamage=0)");
            Check(LogContains(logB, "clientBlockedDamage=0"), "B's final summary: clientBlockedDamage=0");
            return EndServer(s);
        }


        // ============================================================================================
        // 1b. parry -> riposte over the network (FIX2): the REAL host/server path, two player processes
        // ============================================================================================

        private static int ParrySession(string exe, string runDir, string logs, WeaponData sword, List<HitLocationTable> tables, double interval, double stop)
        {
            Debug.Log(Tag + " ---- session 1b: melee-parry");
            sword = LiveWeapon(sword);
            Session s = StartServer(exe, runDir, logs, "parry", "-ember-net-no-enemies -ember-net-party-size 1");
            if (s == null) return 1;

            string goA = Path.Combine(runDir, "parry_a.go"), goB = Path.Combine(runDir, "parry_b.go");
            string quitA = Path.Combine(runDir, "parry_a.quit"), quitB = Path.Combine(runDir, "parry_b.quit");
            foreach (string f in new[] { goA, goB, quitA, quitB }) if (File.Exists(f)) File.Delete(f);

            string logA = Path.Combine(logs, "parry_a.log"), logB = Path.Combine(logs, "parry_b.log");
            Process a = Launch(exe, logA, ClientArgs(s.Port, "par-a", "warrior", Q(quitA), "-ember-net-script melee-parry -ember-net-script-start-file " + Q(goA)) + " -ember-net-autoquit 500");
            Match admA = WaitForRegex(s.ServerLog, AdmittedRegex("par-a", "warrior"), 90, s.Server);
            Check(a != null && admA != null, "parry: server admitted A");
            if (admA == null) return 1;
            Process b = Launch(exe, logB, ClientArgs(s.Port, "par-b", "warrior", Q(quitB), "-ember-net-script melee-parry -ember-net-script-start-file " + Q(goB)) + " -ember-net-autoquit 500");
            Match admB = WaitForRegex(s.ServerLog, AdmittedRegex("par-b", "warrior"), 90, s.Server);
            Check(b != null && admB != null, "parry: server admitted B");
            if (admB == null) return 1;
            int idA = GI(admA, "id"), idB = GI(admB, "id");
            string clientA = admA.Groups["client"].Value, clientB = admB.Groups["client"].Value;
            Check(idA < idB, "parry: A (the attacker role) has the lower id");

            Check(WaitForRegex(logA, @"\[client\] owner id=" + idA + @" views=2 owned=1", 60, a) != null, "parry: A settles with views=2");
            Check(WaitForRegex(logB, @"\[client\] owner id=" + idB + @" views=2 owned=1", 60, b) != null, "parry: B settles with views=2");
            Check(LogContains(logB, C + "combat sender started script=melee-parry realInput=False"), "parry: B's combat sender runs the melee-parry script");

            File.WriteAllText(goA, "go stop=" + F(stop) + " interval=" + F(interval) + " presses=1");
            File.WriteAllText(goB, "go hold=8");

            int strikes = sword.RiposteStrikeCount;
            Match last = WaitForRegex(s.ServerLog, @"combat-swing id=" + idB + @" .*riposte=" + strikes, 90, s.Server);
            Check(last != null, "parry: the server started riposte strike " + strikes + " of B (the data's strike count)");
            if (last == null) { DumpTail(s.ServerLog); }
            Thread.Sleep(4000);

            List<HitLine> hits = ParseHits(s.ServerLog);
            List<HitLine> onB = hits.FindAll(h => h.Attacker == idA && h.Target == idB && h.Source == "Melee");
            List<HitLine> onA = hits.FindAll(h => h.Attacker == idB && h.Target == idA && h.Source == "Melee");
            Check(onB.Count == 1, "parry: A's single swing hit B once (got " + onB.Count + ")");
            if (onB.Count == 1)
            {
                HitLine h = onB[0];
                Check(h.Blocked == "True" && h.GuardBroken == "False", "parry: B's sword-and-shield block held (not broken)");
                Check(h.Amount == 0.0, "parry: the held block took ZERO HP (reported amount " + F(h.Amount) + ")");
                Check(Near(h.BlockReduction, Emberdelve.BladeDefense.BladeGuardRules.HeldGuardReduction), "parry: the reported reduction is the held-guard value");
            }

            double mult = Emberdelve.BladeDefense.RiposteRules.StrikeMultiplier(sword.riposteDamageMultiplier);
            Check(onA.Count == strikes, "parry: B's riposte landed exactly the data's " + strikes + " strikes on A (got " + onA.Count + ")");
            for (int i = 0; i < onA.Count; i++)
            {
                HitLine h = onA[i];
                string why = MeleeMismatch(h, sword, tables, true);
                Check(why == null && h.Blocked == "False", "parry: riposte hit " + (i + 1) + " (" + F(h.Amount) + ") matches the data-derived value" + (why != null ? " (" + why + ")" : ""));
                Check(Near(h.Step, mult), "parry: riposte hit " + (i + 1) + " carries the data multiplier x" + F(mult) + " (step " + F(h.Step) + ")");
            }

            for (int k = 1; k <= strikes; k++)
                Check(CountMatches(s.ServerLog, @"combat-swing id=" + idB + @" .*riposte=" + k + @"\b") == 1, "parry: exactly one server swing line with riposte=" + k);
            Check(CountMatches(s.ServerLog, @"combat-swing id=" + idA + " ") == 1, "parry: A swung exactly once");
            Check(CountMatches(s.ServerLog, @"combat-intent client=" + clientB + @" id=" + idB + @" kind=GuardDown seq=\d+ outcome=Accepted reason=None") >= 2, "parry: B's two guard presses were accepted");
            Check(CountMatches(s.ServerLog, @"combat-intent client=" + clientB + @" id=" + idB + @" kind=GuardUp seq=\d+ outcome=Accepted reason=None") >= 1, "parry: B's release was accepted");
            Check(CountMatches(s.ServerLog, @"combat reject=") == 0, "parry: honest traffic produced no combat reject");
            Check(WaitForCount(logA, @"\[client\] server-swing id=" + idB + @" .*riposte=1", 1, 20, a), "parry: A (the target) was told riposte strike 1");

            QuitClient(a, quitA, logA, "A");
            QuitClient(b, quitB, logB, "B");
            Check(LogContains(logA, "clientBlockedDamage=0") && LogContains(logB, "clientBlockedDamage=0"), "parry: the clients applied no damage themselves");
            return EndServer(s);
        }

        /// <summary>
        /// The WeaponData the gate resolved before the player build can be unloaded by the build (Unity fake-null on the weapon or on its
        /// SwingPatternData sub-assets), after which its pattern list enumerates as zero live patterns. The expected value is derived from the
        /// authored data, so the weapon is looked up again from the AssetDatabase by its id whenever the held one has no live pattern.
        /// </summary>
        private static WeaponData LiveWeapon(WeaponData held)
        {
            if (held != null && held.swingPatterns != null)
                foreach (SwingPatternData p in held.swingPatterns) if (p != null) return held;
            string id = ReferenceEquals(held, null) ? null : held.weaponId; // a fake-null (unloaded) asset still carries its managed id
            WeaponData best = null;
            foreach (string guid in AssetDatabase.FindAssets("t:WeaponData"))
            {
                WeaponData w = AssetDatabase.LoadAssetAtPath<WeaponData>(AssetDatabase.GUIDToAssetPath(guid));
                if (w == null || (id != null && w.weaponId != id)) continue;
                best = w;
                if (w.swingPatterns != null && w.swingPatterns.Length > 0) break;
            }

            return best ?? held;
        }

        /// <summary>The damage left after the defender's guard: a guard that HELD took the whole hit (0 HP, FIX2); everything else keeps the 1.0 floor.</summary>
        private static double GuardedAmount(HitLine h, double pre)
        {
            if (h.Blocked != "True") return pre;
            double reduction = Math.Min(1.0, Math.Max(0.0, h.BlockReduction));
            if (h.GuardBroken != "True" && reduction >= 1.0) return 0.0;
            return Math.Max(1.0, pre * (1.0 - reduction));
        }

        private static string MeleeMismatch(HitLine h, WeaponData sword, List<HitLocationTable> tables, bool riposteStrike = false)
        {
            sword = LiveWeapon(sword);
            if (h.Weapon != sword.weaponId) return "weapon " + h.Weapon + " != " + sword.weaponId;
            HitLocation loc;
            if (!Enum.TryParse(h.Location, out loc)) return "unknown location " + h.Location;
            bool locOk = false;
            foreach (HitLocationTable t in tables) if (Math.Abs(t.Multiplier(loc) - h.LocMult) <= 0.0015) locOk = true;
            if (!locOk) return "location multiplier " + F(h.LocMult) + " is not the table's for " + h.Location;
            if (!(h.OutMult > 0.0 && h.OutMult < 10.0) || h.Flat < 0.0 || h.Zone <= 0.0) return "implausible factors out=" + F(h.OutMult) + " flat=" + F(h.Flat) + " zone=" + F(h.Zone);

            bool stepOk = !sword.HasComboChain && Math.Abs(h.Step - 1.0) < 0.0015;
            if (sword.HasComboChain) foreach (ComboStep cs in sword.comboSteps) if (Math.Abs(cs.damageMultiplier - h.Step) <= 0.0015) stepOk = true;
            if (riposteStrike && Math.Abs(Emberdelve.BladeDefense.RiposteRules.StrikeMultiplier(sword.riposteDamageMultiplier) - h.Step) <= 0.0015) stepOk = true;
            if (!stepOk) return "combo step multiplier " + F(h.Step) + " is not in the weapon's chain";

            double baseDamage = sword.BoostedBaseDamage((float)h.Flat);
            System.Text.StringBuilder tried = new System.Text.StringBuilder();
            int considered = 0;
            foreach (SwingPatternData p in sword.swingPatterns)
            {
                if (p == null) continue;
                considered++;
                // the resolver's own order: base x pattern x step x outgoing x location x zone (floor 1), then the guard (floor 1), then armor (floor 1)
                double pre = Math.Max(1.0, baseDamage * p.damageMultiplier * h.Step * h.OutMult * h.LocMult * h.Zone);
                double after = GuardedAmount(h, pre);
                double final = h.Armor > 0.0 && after > 0.0 ? Math.Max(1.0, after * (100.0 / (100.0 + h.Armor))) : after;
                double tolerance = Math.Max(0.1, 0.01 * h.Amount);
                if (Math.Abs(final - h.Amount) <= tolerance) return null;
                tried.Append(" [pattern ").Append(p.patternId).Append(" dmgMult ").Append(F(p.damageMultiplier)).Append(" -> pre ").Append(F(pre))
                    .Append(" after-guard ").Append(F(after)).Append(" final ").Append(F(final)).Append(" tol ").Append(F(tolerance)).Append(']');
            }

            return "no pattern of the weapon explains " + F(h.Amount) + " (" + considered + " patterns tried; base " + F(baseDamage) + " flat " + F(h.Flat) + ", step " + F(h.Step) + ", out " + F(h.OutMult) +
                   ", loc " + F(h.LocMult) + ", zone " + F(h.Zone) + ", blocked " + h.Blocked + " reduction " + F(h.BlockReduction) + ", armor " + F(h.Armor) + ";" + tried + ")";
        }

        // ============================================================================================
        // 2. ranged shot
        // ============================================================================================

        private static int RangedSession(string exe, string runDir, string logs, WeaponData bow, List<HitLocationTable> tables)
        {
            Debug.Log(Tag + " ---- session 2: ranged-shot");
            Session s = StartServer(exe, runDir, logs, "ranged", "-ember-net-no-enemies -ember-net-party-size 1");
            if (s == null) return 1;

            string goA = Path.Combine(runDir, "ranged_a.go"), goB = Path.Combine(runDir, "ranged_b.go");
            string quitA = Path.Combine(runDir, "ranged_a.quit"), quitB = Path.Combine(runDir, "ranged_b.quit");
            foreach (string f in new[] { goA, goB, quitA, quitB }) if (File.Exists(f)) File.Delete(f);

            string logA = Path.Combine(logs, "ranged_a.log"), logB = Path.Combine(logs, "ranged_b.log");
            Process a = Launch(exe, logA, ClientArgs(s.Port, "rng-a", "archer", Q(quitA), "-ember-net-script ranged-shot -ember-net-script-start-file " + Q(goA)) + " -ember-net-autoquit 500");
            Match admA = WaitForRegex(s.ServerLog, AdmittedRegex("rng-a", "archer"), 90, s.Server);
            Check(a != null && admA != null, "server admitted A (archer)");
            if (admA == null) return 1;
            Process b = Launch(exe, logB, ClientArgs(s.Port, "rng-b", "warrior", Q(quitB), "-ember-net-script ranged-shot -ember-net-script-start-file " + Q(goB)) + " -ember-net-autoquit 500");
            Match admB = WaitForRegex(s.ServerLog, AdmittedRegex("rng-b", "warrior"), 90, s.Server);
            Check(b != null && admB != null, "server admitted B (warrior target)");
            if (admB == null) return 1;
            int idA = GI(admA, "id"), idB = GI(admB, "id");
            string clientA = admA.Groups["client"].Value, clientB = admB.Groups["client"].Value;
            Check(idA < idB, "A (the shooter role) has the lower id");

            Match spA = WaitForRegex(s.ServerLog, SpawnRegex(clientA), 10, s.Server);
            Match spB = WaitForRegex(s.ServerLog, SpawnRegex(clientB), 10, s.Server);
            Check(spA != null && spB != null, "server logged both spawn poses");
            if (spA == null || spB == null) return 1;
            // The player standing in dungeon row 3 (open ground to the east: hall, corridor, library) walks east to make the distance.
            int cellA = (int)Math.Floor(GD(spA, "z") / 2.0), cellB = (int)Math.Floor(GD(spB, "z") / 2.0);
            bool aWalks = cellA == 3 || cellB != 3;
            Debug.Log(Tag + " spawn rows A=" + cellA + " B=" + cellB + " walker=" + (aWalks ? "A (shooter)" : "B (target)"));
            Check(cellA == 3 || cellB == 3, "one spawn is in the open row 3 (rows " + cellA + ", " + cellB + ")");

            // The shooting distance from the data: the middle of the weapon's air-time falloff window when it has one.
            double speed = bow.projectileSpeed;
            double targetAir = 0.5;
            if (bow.HasAirTimeFalloff)
            {
                ProjectileFalloffKey first = bow.projectileFalloff[0], last = bow.projectileFalloff[bow.projectileFalloff.Length - 1];
                targetAir = 0.5 * (first.time + last.time);
            }

            double away = Math.Min(18.0, Math.Max(8.0, speed * targetAir));
            double drawSeconds = bow.drawTime;
            double hold = drawSeconds * 1.6 + 0.8;
            double early = 0.04;
            string common = "away=" + F(away) + " pspeed=" + F(speed) + " pgrav=" + F(bow.projectileGravity);
            string walk = " awayyaw=90"; // east: the open row (hall, corridor, library)
            Debug.Log(Tag + " derived bow=" + bow.weaponId + " speed=" + F(speed) + " gravity=" + F(bow.projectileGravity) + " airTarget=" + F(targetAir) + " -> away=" + F(away) + " hold=" + F(hold));

            Check(WaitForRegex(logA, @"\[client\] owner id=" + idA + @" views=2 owned=1", 60, a) != null, "A settles with views=2");
            Check(WaitForRegex(logB, @"\[client\] owner id=" + idB + @" views=2 owned=1", 60, b) != null, "B settles with views=2");
            string fileA = "go shots=2 hold=" + F(hold) + " early=" + F(early) + " " + common + (aWalks ? walk : "");
            string fileB = "go " + common + (aWalks ? "" : walk);
            File.WriteAllText(goA, fileA);
            File.WriteAllText(goB, fileB);

            bool shotsDone = WaitForRegex(logA, @"\[client\] script ranged-shot done role=shooter", 120, a) != null;
            Check(shotsDone, "the shooter's script finished");
            if (!shotsDone) DumpTail(logA);
            Thread.Sleep(2500);

            List<string> shotLines = Lines(s.ServerLog, @"combat-shot id=" + idA + " ");
            Check(shotLines.Count >= 2, "the server launched at least two arrows for A (" + shotLines.Count + ")");
            List<ShotLine> shots = ParseShots(s.ServerLog, idA);
            foreach (ShotLine sh in shots)
            {
                Check(sh.Weapon == bow.weaponId && Math.Abs(sh.Speed - bow.projectileSpeed) <= 0.01 && Math.Abs(sh.Gravity - bow.projectileGravity) <= 0.01,
                    "arrow " + sh.Serial + ": the launch parameters come from the weapon data (speed " + F(sh.Speed) + ", gravity " + F(sh.Gravity) + ")");
                Check(sh.Charge >= bow.minChargeToFire - 0.001 && sh.Charge <= 1.0001, "arrow " + sh.Serial + ": the server-measured charge " + F(sh.Charge) + " is in [" + F(bow.minChargeToFire) + ",1]");
            }

            Check(shots.Count >= 2 && shots[0].Charge >= 0.999 && shots[1].Charge >= 0.999, "the two full draws carry charge 1.0, measured by the server's clock");
            Check(CountMatches(s.ServerLog, @"combat-intent client=" + clientA + @" id=" + idA + @" kind=DrawRelease seq=\d+ outcome=Accepted reason=None charge=1\.000") >= 2, "the release lines show the server's charge 1.000");

            // The early draw: either the server refuses it (TooEarly) or it fires with a measured charge below 1; a client cannot claim a charge.
            List<string> releases = Lines(s.ServerLog, @"combat-intent client=" + clientA + @" id=" + idA + @" kind=DrawRelease ");
            Check(releases.Count >= 3, "the shooter released three draws (" + releases.Count + ")");
            if (releases.Count >= 3)
            {
                Match last = Regex.Match(releases[releases.Count - 1], @"outcome=(?<o>\w+) reason=(?<r>\w+) charge=(?<c>" + NumBody + ")");
                double lastCharge = GD(last, "c");
                Check(lastCharge < 0.999, "the early draw's charge " + F(lastCharge) + " is below the full charge (the server measured the hold)");
                double minHold = Math.Max(early, CombatNetLimits.ClientMinIntervalSeconds);
                if (minHold / drawSeconds < bow.minChargeToFire * 0.9) Check(last.Groups["r"].Value == "TooEarly", "an early release is refused with reason TooEarly");
                else Debug.Log(Tag + " early-release refusal not asserted: the client's send spacing (" + F(minHold) + " s) exceeds the minimum charge window (" + F(drawSeconds * bow.minChargeToFire) + " s)");
            }

            List<HitLine> hits = ParseHits(s.ServerLog).FindAll(h => h.Attacker == idA && h.Target == idB && h.Source == "Projectile");
            Check(hits.Count >= 1, "at least one arrow hit B on the server (" + hits.Count + ")");
            for (int i = 0; i < hits.Count; i++)
            {
                HitLine h = hits[i];
                string why = ArrowMismatch(h, bow, tables);
                Check(why == null, "arrow hit " + (i + 1) + ": " + h.Location + " amount " + F(h.Amount) + " at air " + F(h.Air) + " s (scale " + F(h.AirScale) + ") matches the data-derived value" + (why != null ? " (" + why + ")" : ""));
                ShotLine origin = shots.Count > 0 ? shots[Math.Min(shots.Count - 1, i)] : null;
                string flight = origin != null ? FlightMismatch(h, origin) : "no launch line";
                Check(flight == null, "arrow hit " + (i + 1) + ": the contact point follows the server's D2 ballistics (drop included)" + (flight != null ? " (" + flight + ")" : ""));
                Check(h.Dist > 0.0 && Math.Abs(h.Dist - away) <= 4.0, "arrow hit " + (i + 1) + ": distance " + F(h.Dist) + " is near the scripted distance " + F(away));
            }

            if (bow.HasAirTimeFalloff && hits.Count > 0)
                Check(hits.Exists(h => h.AirScale >= 0.0 && Math.Abs(h.AirScale - bow.AirTimeFalloffScale((float)h.Air)) <= 0.01), "the falloff scale applied equals the weapon's table at the logged air time");

            Check(CountMatches(s.ServerLog, @"combat reject=") == 0, "honest ranged traffic produced no combat reject");
            Check(WaitForCount(logB, @"\[client\] server-shot id=" + idA + " ", shots.Count, 20, b), "B (observer) was told every arrow launch (" + shots.Count + ")");
            Check(WaitForCount(logB, @"\[client\] server-flight-end id=" + idA + " ", shots.Count, 20, b), "B was told every flight end");
            foreach (Match m in Regex.Matches(ReadShared(logB), @"server-flight-end id=" + idA + @" serial=\d+ air=(?<air>" + NumBody + @") replicaAir=(?<rep>" + NumBody + ")"))
            {
                double air = GD(m, "air"), rep = GD(m, "rep");
                Check(rep < 0.0 || Math.Abs(air - rep) <= 0.35, "B's arrow replica flew " + F(rep) + " s, the server's flight " + F(air) + " s (same integrator)");
            }

            Check(WaitForRegex(logB, @"\[client\] vitals id=" + idB + @" health=\S+ max=\S+ dead=False owned=True applied=True", 20, b) != null, "B (owner) saw its health change from the server's variable");
            Check(!LogContains(logA, "combat-hit src=") && !LogContains(logB, "combat-hit src="), "clients logged no server-side hit line");

            QuitClient(a, quitA, logA, "A");
            QuitClient(b, quitB, logB, "B");
            Check(LogContains(logA, "clientBlockedDamage=0") && LogContains(logB, "clientBlockedDamage=0"), "no client applied damage");
            return EndServer(s);
        }

        private static string ArrowMismatch(HitLine h, WeaponData bow, List<HitLocationTable> tables)
        {
            if (h.Weapon != bow.weaponId) return "weapon " + h.Weapon;
            HitLocation loc;
            if (!Enum.TryParse(h.Location, out loc)) return "unknown location " + h.Location;
            bool locOk = false;
            foreach (HitLocationTable t in tables) if (Math.Abs(t.Multiplier(loc) - h.LocMult) <= 0.0015) locOk = true;
            if (!locOk) return "location multiplier " + F(h.LocMult);
            if (!(h.OutMult > 0.0 && h.OutMult < 10.0) || h.Flat < 0.0) return "implausible factors";

            double charged = bow.BoostedBaseDamage((float)h.Flat) * Mathf.Lerp(bow.minChargeDamageScale, 1f, (float)h.Charge);
            double pre = Math.Max(1.0, charged * h.OutMult * h.LocMult);
            double after = GuardedAmount(h, pre);
            double armored = h.Armor > 0.0 ? Math.Max(1.0, after * (100.0 / (100.0 + h.Armor))) : after;
            double scale = h.AirScale;
            double final;
            if (bow.HasAirTimeFalloff)
            {
                double expectedScale = bow.AirTimeFalloffScale((float)h.Air);
                if (Math.Abs(expectedScale - scale) > 0.01) return "falloff scale " + F(scale) + " != table " + F(expectedScale) + " at " + F(h.Air) + " s";
                final = Math.Max(1.0, armored * expectedScale);
            }
            else
            {
                double legacy = bow.DamageAtRange((float)h.Dist, (float)h.Flat) / Math.Max(0.0001, bow.BoostedBaseDamage((float)h.Flat));
                final = Math.Max(1.0, armored * legacy);
            }

            return Math.Abs(final - h.Amount) <= Math.Max(0.15, 0.015 * h.Amount) ? null : "expected " + F(final) + " (charged " + F(charged) + ", out " + F(h.OutMult) + ", loc " + F(h.LocMult) + ", armor " + F(h.Armor) + ", scale " + F(scale) + ")";
        }

        private static string FlightMismatch(HitLine h, ShotLine shot)
        {
            Vector3 dir = new Vector3((float)shot.Dx, (float)shot.Dy, (float)shot.Dz).normalized;
            BallisticState start = new BallisticState((float)shot.Ox, (float)shot.Oy, (float)shot.Oz, dir.x * (float)shot.Speed, dir.y * (float)shot.Speed, dir.z * (float)shot.Speed);
            BallisticState at = ProjectileBallistics.PositionAt(in start, (float)shot.Gravity, (float)h.Air);
            double tolerance = shot.Speed * ProjectileBallistics.StepSeconds + 0.6;
            double dx = at.PX - h.Px, dy = at.PY - h.Py, dz = at.PZ - h.Pz;
            double miss = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            double straightY = shot.Oy + dir.y * shot.Speed * h.Air;
            double drop = straightY - at.PY;
            if (miss > tolerance) return "contact is " + F(miss) + " m from the integrated position (tolerance " + F(tolerance) + ")";
            if (shot.Gravity > 0.0 && drop < 0.0) return "no drop although gravity is " + F(shot.Gravity);
            return null;
        }

        // ============================================================================================
        // 3. abuse
        // ============================================================================================

        private static int AbuseSession(string exe, string runDir, string logs)
        {
            Debug.Log(Tag + " ---- session 3: combat-abuse");
            Session s = StartServer(exe, runDir, logs, "abuse", "-ember-net-no-enemies -ember-net-party-size 1" +
                " -ember-net-skill-reject-burst " + RejectBurst + " -ember-net-skill-reject-rate " + RejectRate +
                " -ember-net-skill-disconnect-after " + DisconnectAfter + " -ember-net-skill-memory-seconds " + MemorySeconds);
            if (s == null) return 1;
            Match policyLine = WaitForRegex(s.ServerLog, @"\[server\] skill-server ready slots=\d+ gateBurst=\S+ gateRate=\S+ rejectBurst=(?<burst>" + NumBody + @") rejectRefill=(?<refill>" + NumBody + @") disconnectAfter=(?<after>\d+) memorySeconds=(?<memory>" + NumBody + ")", 10, s.Server);
            Check(policyLine != null, "server logged its abuse policy");
            if (policyLine == null) return 1;
            SkillAbusePolicy policy = new SkillAbusePolicy(GD(policyLine, "burst"), GD(policyLine, "refill"), GI(policyLine, "after"), GD(policyLine, "memory"));
            int expectedTotal = (int)GD(policyLine, "burst") + policy.ThresholdFor(0);

            string goA = Path.Combine(runDir, "abuse_a.go"), goB = Path.Combine(runDir, "abuse_b.go");
            string quitA = Path.Combine(runDir, "abuse_a.quit"), quitB = Path.Combine(runDir, "abuse_b.quit");
            foreach (string f in new[] { goA, goB, quitA, quitB }) if (File.Exists(f)) File.Delete(f);
            string logA = Path.Combine(logs, "abuse_a.log"), logB = Path.Combine(logs, "abuse_b.log");

            Process a = Launch(exe, logA, ClientArgs(s.Port, "abu-a", "warrior", Q(quitA), "-ember-net-script combat-abuse -ember-net-script-start-file " + Q(goA)) + " -ember-net-autoquit 500");
            Match admA = WaitForRegex(s.ServerLog, AdmittedRegex("abu-a", "warrior"), 90, s.Server);
            Check(a != null && admA != null, "server admitted A (victim)");
            if (admA == null) return 1;
            Process b = Launch(exe, logB, ClientArgs(s.Port, "abu-b", "warrior", Q(quitB), "-ember-net-script combat-abuse -ember-net-script-start-file " + Q(goB)) + " -ember-net-autoquit 500");
            Match admB = WaitForRegex(s.ServerLog, AdmittedRegex("abu-b", "warrior"), 90, s.Server);
            Check(b != null && admB != null, "server admitted B (abuser)");
            if (admB == null) return 1;
            string clientA = admA.Groups["client"].Value, clientB = admB.Groups["client"].Value;
            int idA = GI(admA, "id"), idB = GI(admB, "id");
            string netIdA = admA.Groups["net"].Value;
            Check(WaitForRegex(logA, @"\[client\] owner id=" + idA + @" views=2 owned=1", 60, a) != null, "A settles with views=2");
            Check(WaitForRegex(logB, @"\[client\] owner id=" + idB + @" views=2 owned=1", 60, b) != null, "B settles with views=2");
            Thread.Sleep(1500);
            int vitalsBefore = CountMatches(s.ServerLog, @"combat-vitals id=" + idA + " ");

            File.WriteAllText(goB, "go");
            File.WriteAllText(goA, "go");
            Match dc = WaitForRegex(s.ServerLog, DisconnectRegex(clientB), 60, s.Server);
            Check(dc != null, "server disconnected B for abuse");
            if (dc == null) { DumpTail(s.ServerLog); dc = Regex.Match("", "x"); }
            int identity = GI(dc, "identity"), invalid = GI(dc, "invalid"), replay = GI(dc, "replay"), flood = GI(dc, "flood");
            int total = GI(dc, "total"), excess = GI(dc, "excess"), threshold = GI(dc, "threshold");
            Check(identity == 3, "the three forged intents on A's object were identity rejects on B's connection (identity=" + identity + ")");
            Check(invalid == 1 && replay == 1 && flood >= 1, "the malformed kind, the replay and the flood were each counted (invalid=" + invalid + ", replay=" + replay + ", flood=" + flood + ")");
            Check(total == identity + invalid + replay + flood, "every reject is in the total (" + total + ")");
            Check(threshold == policy.ThresholdFor(0) && excess == threshold, "disconnected exactly when the excess reached the threshold (" + excess + "/" + threshold + ")");
            Check(total == expectedTotal, "total rejects = allowance + threshold = " + expectedTotal + " (was " + total + ")");
            Check(Regex.IsMatch(ReadShared(s.ServerLog), @"\[server\] combat reject=Identity client=" + clientB + @" slot=abu-b fault=not-your-object targetNet=" + netIdA + @" count=3 "), "the forged intents were charged to the SENDER (B), through the shared ledger (log tag 'combat')");
            Check(!Regex.IsMatch(ReadShared(s.ServerLog), @"(combat|skill) reject=\w+ client=" + clientA + " "), "nothing was ever charged to A");
            Check(CountMatches(s.ServerLog, @"combat reject=Flood client=" + clientB + " ") < flood, "flood logging is rate limited");
            Check(CountMatches(s.ServerLog, @"combat-intent client=" + clientA + " ") == 0, "A (the victim) received no intent processing at all");
            Thread.Sleep(2000);
            Check(CountMatches(s.ServerLog, @"combat-vitals id=" + idA + " ") == vitalsBefore, "A's health picture never changed (" + vitalsBefore + " vitals lines)");
            // B's own valid intent(s) are real: a valid AttackPress starts a real swing (on B's own rig, at nobody). The count comes from the probe's script constant.
            int validPresses = CombatNetLimits.AbuseProbeValidAttackPresses;
            Check(LogContains(logB, "script combat-abuse sent target=") && LogContains(logB, " valid=" + validPresses + " replay=1 invalid=1"), "B's script reports the " + validPresses + " valid intent(s) the gate derives its swings from");
            int validAccepted = CountMatches(s.ServerLog, @"combat-intent client=" + clientB + @" id=" + idB + @" kind=AttackPress seq=\d+ outcome=Accepted reason=None");
            Check(validAccepted == validPresses, "the server accepted exactly B's valid AttackPress intent(s) (" + validAccepted + " of " + validPresses + ")");
            int swingsByB = CountMatches(s.ServerLog, @"combat-swing id=" + idB + " ");
            Check(swingsByB == validAccepted, "the swings are exactly the ones B's valid intents caused (" + swingsByB + " swings for " + validAccepted + " accepted)");
            Check(CountMatches(s.ServerLog, @"combat-swing id=" + idA + " ") == 0, "A (the forged-on victim) never swung");
            Check(CountMatches(s.ServerLog, @"combat-swing ") == swingsByB, "no other swing happened on the server");
            Check(CountMatches(s.ServerLog, @"combat-hit ") == 0, "no hit happened (the abuser's swing reached nobody, its guard toggles hurt nobody)");
            Match fin = WaitForRegex(s.ServerLog, @"skill-conn final client=" + clientB + @" .* ignored=(?<ign>\d+)", 20, s.Server);
            Check(fin != null && GI(fin, "ign") >= 1, "traffic after the disconnect decision was dropped unseen (ignored=" + (fin != null ? fin.Groups["ign"].Value : "?") + ")");
            Check(WaitForRegex(s.ServerLog, @"combat-conn final client=" + clientB + @" id=" + idB + @" accepted=\d+ refused=\d+ gateAccepted=(?<ga>\d+) ", 20, s.Server) != null, "B's combat connection state was discarded with its final counters");

            bool bExited = WaitForExit(b, 40);
            Check(bExited && b.ExitCode == NetExitCodes.UnexpectedDisconnect, "B exited by itself with code " + NetExitCodes.UnexpectedDisconnect + (bExited ? " (was " + b.ExitCode + ")" : ""));
            Check(LogContains(logB, C + "disconnected unexpectedly reason='skill abuse'"), "B was told 'skill abuse'");
            Check(!SafeHasExited(a) && !LogContains(logA, "disconnected unexpectedly"), "A stayed connected");
            Check(!LogContains(logA, "server-hit") && !LogContains(logA, "combat-result"), "A saw no hit and received no combat result");
            QuitClient(a, quitA, logA, "A");
            return EndServer(s);
        }

        // ============================================================================================
        // 4. enemies
        // ============================================================================================

        private static int EnemySession(string exe, string runDir, string logs)
        {
            Debug.Log(Tag + " ---- session 4: enemy visibility");
            Session s = StartServer(exe, runDir, logs, "enemy", "");
            if (s == null) return 1;
            Match ready = Regex.Match(ReadShared(s.ServerLog), @"\[server\] ready scene=\S+ players=(\d+) senders=(\d+) registry=(?<reg>\d+)");
            int enemies = GI(ready, "reg");
            Check(enemies > 0 && LogContains(s.ServerLog, "enemies=on"), "the server runs with enemies (registry=" + enemies + ")");

            string quitA = Path.Combine(runDir, "enemy_a.quit");
            if (File.Exists(quitA)) File.Delete(quitA);
            string logA = Path.Combine(logs, "enemy_a.log");
            Process a = Launch(exe, logA, ClientArgs(s.Port, "ene-a", "warrior", Q(quitA), "") + " -ember-net-autoquit 500");
            Match adm = WaitForRegex(s.ServerLog, AdmittedRegex("ene-a", "warrior"), 90, s.Server);
            Check(a != null && adm != null, "server admitted the client");
            if (adm == null) return 1;

            Match view = WaitForRegex(logA, @"\[client\] enemy-view summary total=(?<total>\d+) alive=(?<alive>\d+) dead=(?<dead>\d+) poses=(?<poses>\d+)", 40, a);
            Check(view != null, "the client logged an enemy-view summary");
            if (view != null)
            {
                Check(GI(view, "total") == enemies, "the client knows every server enemy (" + view.Groups["total"].Value + " of " + enemies + ")");
                Check(GI(view, "alive") >= 1 && GI(view, "poses") > 0, "enemy poses arrive (alive=" + view.Groups["alive"].Value + ", poses=" + view.Groups["poses"].Value + ")");
            }

            Check(CountMatches(logA, @"\[client\] enemy-view id=\d+ health=\S+ max=\S+ dead=False") >= 1, "enemy health is reported to the client");
            QuitClient(a, quitA, logA, "client");
            Check(LogContains(logA, "clientBlockedDamage=0"), "the client applied no damage");
            return EndServer(s);
        }

        // ============================================================================================
        // parsing
        // ============================================================================================

        private sealed class ShotLine
        {
            public string Weapon;
            public int Serial;
            public double Ox, Oy, Oz, Dx, Dy, Dz, Speed, Gravity, Charge;
        }

        private static string AdmittedRegex(string slot, string classId)
        {
            return @"admitted client=(?<client>\d+) id=(?<id>\d+) party=(?<party>\d+) slot=" + slot + " class=" + classId + @" netId=(?<net>\d+) players=(?<players>\d+)";
        }

        private static string SpawnRegex(string clientId)
        {
            return @"combat-spawn client=" + clientId + @" id=\d+ party=\d+ x=(?<x>" + NumBody + @") y=(?<y>" + NumBody + @") z=(?<z>" + NumBody + @") yaw=" + NumBody;
        }

        private static string DisconnectRegex(string clientId)
        {
            return @"\[server\] skill-abuse disconnect client=" + clientId + @" slot=(?<slot>\S+) reason='skill abuse' strikes=(?<strikes>\d+) priorStrikes=(?<prior>\d+) identity=(?<identity>\d+) invalid=(?<invalid>\d+) replay=(?<replay>\d+) flood=(?<flood>\d+) total=(?<total>\d+) excess=(?<excess>\d+) threshold=(?<threshold>\d+) memorySeconds=\S+";
        }

        private static List<HitLine> ParseHits(string serverLog)
        {
            List<HitLine> list = new List<HitLine>();
            string pattern = @"combat-hit src=(?<src>\w+) attacker=(?<att>\d+) attackerCombat=\d+ target=(?<tgt>\d+) targetCombat=\d+ targetKind=(?<kind>\w+) weapon=(?<w>\S+) amount=(?<amt>" + NumBody +
                             @") loc=(?<loc>\w+) locMult=(?<lm>" + NumBody + @") blocked=(?<bl>True|False) blockReduction=(?<br>" + NumBody + @") guardBroken=(?<gb>True|False) killed=(?<k>True|False) " +
                             @"outMult=(?<om>" + NumBody + @") flat=(?<fl>" + NumBody + @") armor=(?<ar>" + NumBody + @") step=(?<st>" + NumBody + @") zone=(?<zn>" + NumBody + @") contactT=" + NumBody +
                             @" charge=(?<ch>" + NumBody + @") air=(?<air>" + NumBody + @") airScale=(?<as>" + NumBody + @") dist=(?<dist>" + NumBody + @") px=(?<px>" + NumBody + @") py=(?<py>" + NumBody + @") pz=(?<pz>" + NumBody + ")";
            foreach (Match m in Regex.Matches(ReadShared(serverLog), pattern))
            {
                list.Add(new HitLine
                {
                    Source = m.Groups["src"].Value, Kind = m.Groups["kind"].Value, Weapon = m.Groups["w"].Value, Location = m.Groups["loc"].Value,
                    Blocked = m.Groups["bl"].Value, GuardBroken = m.Groups["gb"].Value, Killed = m.Groups["k"].Value,
                    Attacker = GI(m, "att"), Target = GI(m, "tgt"),
                    Amount = GD(m, "amt"), LocMult = GD(m, "lm"), BlockReduction = GD(m, "br"), OutMult = GD(m, "om"), Flat = GD(m, "fl"), Armor = GD(m, "ar"),
                    Step = GD(m, "st"), Zone = GD(m, "zn"), Charge = GD(m, "ch"), Air = GD(m, "air"), AirScale = GD(m, "as"), Dist = GD(m, "dist"),
                    Px = GD(m, "px"), Py = GD(m, "py"), Pz = GD(m, "pz")
                });
            }

            return list;
        }

        private static List<ShotLine> ParseShots(string serverLog, int playerId)
        {
            List<ShotLine> list = new List<ShotLine>();
            string pattern = @"combat-shot id=" + playerId + @" combat=\d+ serial=(?<serial>\d+) weapon=(?<w>\S+) ox=(?<ox>" + NumBody + @") oy=(?<oy>" + NumBody + @") oz=(?<oz>" + NumBody + @") dx=(?<dx>" + NumBody +
                             @") dy=(?<dy>" + NumBody + @") dz=(?<dz>" + NumBody + @") speed=(?<sp>" + NumBody + @") gravity=(?<g>" + NumBody + @") maxTime=" + NumBody + @" range=" + NumBody + @" charge=(?<ch>" + NumBody + ")";
            foreach (Match m in Regex.Matches(ReadShared(serverLog), pattern))
            {
                list.Add(new ShotLine
                {
                    Weapon = m.Groups["w"].Value, Serial = GI(m, "serial"),
                    Ox = GD(m, "ox"), Oy = GD(m, "oy"), Oz = GD(m, "oz"), Dx = GD(m, "dx"), Dy = GD(m, "dy"), Dz = GD(m, "dz"),
                    Speed = GD(m, "sp"), Gravity = GD(m, "g"), Charge = GD(m, "ch")
                });
            }

            return list;
        }

        private static List<string> Lines(string path, string needleRegex)
        {
            List<string> list = new List<string>();
            foreach (string line in ReadShared(path).Split('\n')) if (Regex.IsMatch(line, needleRegex)) list.Add(line);
            return list;
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

        private static CharacterClassData LoadClass(string classId)
        {
            foreach (string guid in AssetDatabase.FindAssets("t:CharacterClassData"))
            {
                CharacterClassData data = AssetDatabase.LoadAssetAtPath<CharacterClassData>(AssetDatabase.GUIDToAssetPath(guid));
                if (data != null && string.Equals(data.classId, classId, StringComparison.OrdinalIgnoreCase)) return data;
            }

            return null;
        }

        private static string F(double value) { return value.ToString("F3", CultureInfo.InvariantCulture); }
        private static bool Near(double a, double b) { return Math.Abs(a - b) <= 0.0105; }
        private static double Flat(double x1, double z1, double x2, double z2) { return Math.Sqrt((x1 - x2) * (x1 - x2) + (z1 - z2) * (z1 - z2)); }

        // ============================================================================================
        // process helpers
        // ============================================================================================

        private static Session StartServer(string exe, string runDir, string logs, string name, string extra)
        {
            int port = FreeUdpPort();
            Check(port > 0, name + ": free udp port " + port);
            if (port <= 0) return null;

            Session s = new Session { Name = name, Port = port, QuitFile = Path.Combine(runDir, name + "_server.quit"), ServerLog = Path.Combine(logs, name + "_server.log") };
            if (File.Exists(s.QuitFile)) File.Delete(s.QuitFile);
            s.Server = Launch(exe, s.ServerLog,
                "-ember-net server -ember-port " + port + " -ember-net-quit-file " + Q(s.QuitFile) +
                " -ember-net-autoquit 900 -ember-net-disconnect-timeout-ms " + DisconnectTimeoutMs + " -ember-net-max-players 3 " + extra);
            Check(s.Server != null, name + ": server process started");
            if (s.Server == null) return null;
            Check(WaitForLog(s.ServerLog, S + "listening", 60, s.Server), name + ": server logged 'listening'");
            Match ready = WaitForRegex(s.ServerLog, @"\[server\] ready scene=\S+ players=(\d+) senders=(\d+) registry=(\d+)", 90, s.Server);
            Check(ready != null, name + ": server loaded the dungeon and logged 'ready'");
            if (ready == null) return null;
            return s;
        }

        private static int EndServer(Session s)
        {
            File.WriteAllText(s.QuitFile, "quit");
            bool exited = WaitForExit(s.Server, 40);
            Check(exited, s.Name + ": server exited after the quit file");
            if (exited) Check(s.Server.ExitCode == NetExitCodes.Ok, s.Name + ": server exit code 0 (was " + s.Server.ExitCode + ")");
            Check(LogContains(s.ServerLog, S + "shutdown players=0 senders=0 registry=0") || s.Name == "enemy", s.Name + ": server logged 'shutdown players=0 senders=0 registry=0'");
            Check(LogContains(s.ServerLog, S + "shutdown complete clean=True"), s.Name + ": server logged a clean shutdown");
            return 0;
        }

        private static void QuitClient(Process p, string quitFile, string log, string label)
        {
            if (p == null) return;
            if (!SafeHasExited(p))
            {
                File.WriteAllText(quitFile, "quit");
                bool exited = WaitForExit(p, 40);
                Check(exited && p.ExitCode == NetExitCodes.Ok, label + " left by quit file with exit 0" + (exited ? " (was " + p.ExitCode + ")" : ""));
            }

            Check(LogContains(log, C + "shutdown complete clean=True"), label + " logged a clean shutdown");
        }

        private static void DumpTail(string path)
        {
            string text = ReadShared(path);
            string[] lines = text.Split('\n');
            StringBuilder sb = new StringBuilder();
            for (int i = Math.Max(0, lines.Length - 25); i < lines.Length; i++) sb.AppendLine(lines[i]);
            Debug.Log(Tag + " tail of " + Path.GetFileName(path) + ":\n" + sb);
        }

        private static string ClientArgs(int port, string slot, string classId, string quitFileQuoted, string extra)
        {
            string args = "-ember-net client -ember-address 127.0.0.1 -ember-port " + port +
                          " -ember-slot " + slot + " -ember-class " + classId +
                          " -ember-net-connect-timeout 60 -ember-net-disconnect-timeout-ms " + DisconnectTimeoutMs;
            if (!string.IsNullOrEmpty(quitFileQuoted)) args += " -ember-net-quit-file " + quitFileQuoted;
            if (!string.IsNullOrEmpty(extra)) args += " " + extra;
            return args;
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
            Match found = null;
            WaitForPredicate(() =>
            {
                Match m = Regex.Match(ReadShared(path), pattern);
                if (!m.Success) return false;
                found = m;
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
