using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Emberdelve.EditorTools
{
    /// <summary>
    /// Runs many Emberdelve gates inside ONE Editor process (batch or the open editor) instead of one Unity
    /// launch per gate. Pass/fail rules mirror Tools/Run-EmberdelveGates.ps1. The queue state lives in
    /// SessionState so it survives the domain reload that play-mode gates trigger.
    /// </summary>
    [InitializeOnLoad]
    public static class EmberdelveGateSuite
    {
        const string StateKey = "Emberdelve.GateSuite.State";
        const string Tag = "[Emberdelve][GateSuite] ";
        public const int DefaultTimeoutSeconds = 900;

        [Serializable] sealed class SceneEntry { public string path; public bool isLoaded; public bool isActive; }

        [Serializable] sealed class GateResult
        {
            public string method; public string name; public bool passed; public int exitCode; public bool timedOut;
            public float seconds; public int checks; public int failures;
            public List<string> summaries = new List<string>(); public List<string> problems = new List<string>();
        }

        [Serializable] sealed class State
        {
            public bool active; public bool batch; public string outDir; public int timeout; public long startedTicks;
            public List<string> gates = new List<string>(); public int index; public string phase;
            public long gateStartTicks; public long exitTicks; public long terminalTicks; public int terminalCode; public int scanned; public bool exitReceived; public int exitCode; public bool capturing;
            public List<string> lines = new List<string>(); public List<GateResult> results = new List<GateResult>();
            public List<SceneEntry> scenes = new List<SceneEntry>(); public bool restoreScenes; public string error;
        }

        static State S;
        public static bool Active { get { return S != null && S.active; } }

        static readonly Regex PositiveRx = new Regex(@"\]\s+PASS\b.*\bchecks=\d+.*\bfailures=0\b");
        static readonly Regex BadRx = new Regex(@"error CS\d+|\]\s+(?:FAIL\b|mismatch\b)|[A-Za-z]+Exception:|Aborting batchmode due to failure");
        static readonly Regex TerminalRx = new Regex(@"\]\s+(PASS|FAIL)\b.*\bchecks=\d+.*\bfailures=(\d+)");
        static readonly Regex CountsRx = new Regex(@"checks=(\d+).*?failures=(\d+)");

        static EmberdelveGateSuite()
        {
            AssemblyReloadEvents.beforeAssemblyReload += Save;
            try
            {
                string j = SessionState.GetString(StateKey, "");
                if (!string.IsNullOrEmpty(j))
                {
                    State s = JsonUtility.FromJson<State>(j);
                    if (s != null && s.active) { S = s; Hook(); }
                }
            }
            catch (Exception) { }
        }

        static void Hook()
        {
            EditorApplication.update -= OnUpdate; EditorApplication.update += OnUpdate;
            Application.logMessageReceivedThreaded -= OnLog; Application.logMessageReceivedThreaded += OnLog;
        }

        static void Unhook()
        {
            EditorApplication.update -= OnUpdate;
            Application.logMessageReceivedThreaded -= OnLog;
        }

        static void Save()
        {
            State s = S;
            if (s == null) return;
            string j;
            lock (s) { j = JsonUtility.ToJson(s); }
            SessionState.SetString(StateKey, s.active ? j : "");
        }

        static void OnLog(string condition, string stack, LogType type)
        {
            State s = S;
            if (s == null || !s.capturing || condition == null) return;
            lock (s)
            {
                if (s.lines.Count > 50000) return;
                foreach (string l in condition.Split('\n')) { string t = l.Trim(); if (t.Length > 0) s.lines.Add(t); }
            }
        }

        // ---------------------------------------------------------------- entry points

        public static void RunFromCommandLine()
        {
            string[] args = Environment.GetCommandLineArgs();
            string gates = ArgValue(args, "-emberGates");
            string outDir = ArgValue(args, "-emberOut");
            string to = ArgValue(args, "-emberGateTimeout");
            int timeout = DefaultTimeoutSeconds;
            if (!string.IsNullOrEmpty(to)) int.TryParse(to, out timeout);
            if (timeout < 5) timeout = DefaultTimeoutSeconds;
            if (string.IsNullOrEmpty(gates))
            {
                Debug.LogError(Tag + "missing -emberGates <comma list or set name>");
                if (Application.isBatchMode) EditorApplication.Exit(2);
                return;
            }
            if (string.IsNullOrEmpty(outDir)) outDir = Path.Combine(ProjectRoot(), "Logs/gates-suite/" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            string err;
            List<string> list = ExpandGates(gates, out err);
            if (err != null || list.Count == 0)
            {
                Debug.LogError(Tag + "bad gate spec: " + (err ?? "empty"));
                if (Application.isBatchMode) EditorApplication.Exit(2);
                return;
            }
            StartCore(list, Path.GetFullPath(outDir), Application.isBatchMode, timeout);
        }

        public static string StartInteractive(string gatesCsvOrSet, string outDir)
        {
            if (S != null && S.active) return "ERROR: a gate suite run is already active";
            if (EditorApplication.isPlayingOrWillChangePlaymode) return "ERROR: editor is in (or entering) play mode";
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return "ERROR: editor is compiling/updating";
            string err;
            List<string> list = ExpandGates(gatesCsvOrSet ?? "", out err);
            if (err != null || list.Count == 0) return "ERROR: " + (err ?? "no gates");
            if (string.IsNullOrEmpty(outDir)) outDir = Path.Combine(ProjectRoot(), "Logs/quick/" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            else if (!Path.IsPathRooted(outDir)) outDir = Path.Combine(ProjectRoot(), outDir);
            return StartCore(list, Path.GetFullPath(outDir), Application.isBatchMode, DefaultTimeoutSeconds);
        }

        public static string Status()
        {
            State s = S;
            if (s == null || !s.active) return "idle";
            string cur = s.index < s.gates.Count ? ShortName(s.gates[s.index]) : "";
            return "running " + Math.Min(s.index + 1, s.gates.Count) + "/" + s.gates.Count + " current=" + cur;
        }

        /// <summary>Called by EmberdelveGateExit.Exit while a suite run is active.</summary>
        public static void NotifyExit(int code)
        {
            State s = S;
            if (s == null || !s.active) return;
            lock (s)
            {
                if (!s.exitReceived) { s.exitCode = code; s.exitTicks = DateTime.UtcNow.Ticks; }
                else if (code != 0) s.exitCode = code;
                s.exitReceived = true;
            }
            if (EditorApplication.isPlaying) EditorApplication.isPlaying = false;
        }

        static string StartCore(List<string> gates, string outDir, bool batch, int timeout)
        {
            Directory.CreateDirectory(outDir);
            S = new State { active = true, batch = batch, outDir = outDir, timeout = timeout, startedTicks = DateTime.UtcNow.Ticks, phase = "next", restoreScenes = !batch };
            S.gates.AddRange(gates);
            Hook();
            WriteOutputs(S, false);
            Save();
            return Path.Combine(outDir, "summary.json");
        }

        // ---------------------------------------------------------------- state machine

        static void OnUpdate()
        {
            State s = S;
            if (s == null || !s.active) { Unhook(); return; }
            try { Step(s); }
            catch (Exception e)
            {
                s.error = e.ToString();
                s.capturing = false;
                Debug.LogError(Tag + "internal error: " + e);
                Finish(s);
            }
        }

        static void Step(State s)
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            if (s.phase == "next")
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode) return;
                if (s.index >= s.gates.Count) { Finish(s); return; }
                BeginGate(s);
                return;
            }
            // Start each gate on a later editor update. Unity can finish startup/import work at
            // the end of the update that invokes RunFromCommandLine; BuildPipeline.BuildPlayer
            // must not run in that same turn while a domain reload is queued. Keeping this phase
            // in SessionState also lets the suite resume correctly if that reload happens here.
            if (s.phase == "invoke")
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode) return;
                InvokeGate(s);
                return;
            }
            if (!s.exitReceived && !s.batch)
            {
                // Several gates only call Exit when Application.isBatchMode. In the open editor treat a final
                // '] PASS|FAIL ... checks=N ... failures=M' line followed by 1.5 s of quiet as the gate's exit.
                lock (s)
                {
                    for (; s.scanned < s.lines.Count; s.scanned++)
                    {
                        Match tm = TerminalRx.Match(s.lines[s.scanned]);
                        if (tm.Success) { s.terminalTicks = DateTime.UtcNow.Ticks; s.terminalCode = (tm.Groups[1].Value == "PASS" && tm.Groups[2].Value == "0") ? 0 : 1; }
                    }
                }
                if (s.terminalTicks > 0 && new TimeSpan(DateTime.UtcNow.Ticks - s.terminalTicks).TotalSeconds > 1.5)
                {
                    long tt = s.terminalTicks; int tc = s.terminalCode;
                    lock (s) { s.exitReceived = true; s.exitCode = tc; s.exitTicks = tt; }
                }
            }
            double elapsed = (DateTime.UtcNow - new DateTime(s.gateStartTicks, DateTimeKind.Utc)).TotalSeconds;
            bool timedOut = !s.exitReceived && elapsed > s.timeout;
            if (!s.exitReceived && !timedOut) return;
            if (EditorApplication.isPlaying) EditorApplication.isPlaying = false;
            if (EditorApplication.isPlayingOrWillChangePlaymode && elapsed < s.timeout + 60) return;
            EndGate(s, timedOut);
        }

        static void BeginGate(State s)
        {
            string name = s.gates[s.index];
            lock (s) { s.lines.Clear(); s.exitReceived = false; s.exitCode = 0; s.exitTicks = 0; s.terminalTicks = 0; s.terminalCode = 0; s.scanned = 0; }
            s.gateStartTicks = DateTime.UtcNow.Ticks;
            CaptureScenes(s);
            MethodInfo m = Resolve(name);
            if (m == null)
            {
                GateResult r = new GateResult { method = name, name = ShortName(name), passed = false, exitCode = -2 };
                r.problems.Add("gate method not found: " + name);
                s.results.Add(r); s.index++;
                WriteOutputs(s, false); Save();
                return;
            }
            s.phase = "invoke";
            s.capturing = true;
            Save();
        }

        static void InvokeGate(State s)
        {
            if (s == null || !s.active || s.index >= s.gates.Count) return;
            string name = s.gates[s.index];
            MethodInfo m = Resolve(name);
            if (m == null)
            {
                lock (s)
                {
                    s.lines.Add("gate method not found after editor reload: " + name);
                    s.exitReceived = true; s.exitCode = 1; s.exitTicks = DateTime.UtcNow.Ticks;
                }
                s.phase = "waiting";
                Save();
                return;
            }
            s.phase = "waiting";
            Save();
            try { m.Invoke(null, null); }
            catch (Exception e)
            {
                Exception ex = (e is TargetInvocationException && e.InnerException != null) ? e.InnerException : e;
                lock (s)
                {
                    s.lines.Add("Run() threw " + ex.GetType().Name + ": " + ex.Message);
                    s.exitReceived = true; s.exitCode = 1; s.exitTicks = DateTime.UtcNow.Ticks;
                }
            }
        }

        static void EndGate(State s, bool timedOut)
        {
            s.capturing = false;
            List<string> lines;
            lock (s) { lines = new List<string>(s.lines); }
            string method = s.gates[s.index];
            long end = (s.exitReceived && s.exitTicks > 0) ? s.exitTicks : DateTime.UtcNow.Ticks;
            GateResult r = new GateResult { method = method, name = ShortName(method), timedOut = timedOut, exitCode = timedOut ? -1 : s.exitCode };
            r.seconds = (float)Math.Round(new TimeSpan(end - s.gateStartTicks).TotalSeconds, 2);

            string expected = ExpectedLine(method);
            HashSet<string> seen = new HashSet<string>();
            int lastChecks = 0, lastFail = 0; bool haveCounts = false;
            foreach (string l in lines)
            {
                if (PositiveRx.IsMatch(l)) r.summaries.Add(l);
                Match cm = CountsRx.Match(l);
                if (cm.Success && l.IndexOf("] ", StringComparison.Ordinal) >= 0) { lastChecks = int.Parse(cm.Groups[1].Value); lastFail = int.Parse(cm.Groups[2].Value); haveCounts = true; }
                if (BadRx.IsMatch(l))
                {
                    if (l.StartsWith("Pipeline: unhandled error serving ")) continue;
                    if (expected != null && l == expected) continue;
                    if (seen.Add(l)) r.problems.Add(l);
                }
            }
            if (r.summaries.Count > 0) { Match pm = CountsRx.Match(r.summaries[0]); if (pm.Success) { r.checks = int.Parse(pm.Groups[1].Value); r.failures = int.Parse(pm.Groups[2].Value); } }
            else if (haveCounts) { r.checks = lastChecks; r.failures = lastFail; }
            if (timedOut) r.problems.Add("timed out after " + s.timeout + "s without Exit");
            if (r.summaries.Count == 0) r.problems.Add("no '] PASS ... checks=N ... failures=0' line seen");
            if (!timedOut && r.exitCode != 0) r.problems.Add("exit code " + r.exitCode);
            r.passed = !timedOut && r.exitCode == 0 && r.summaries.Count >= 1 && CountBad(r, expected) == 0;
            if (r.problems.Count > 20) r.problems.RemoveRange(20, r.problems.Count - 20);
            s.results.Add(r);
            s.index++;
            s.phase = "next";
            RestoreScenes(s);
            WriteOutputs(s, false);
            Save();
        }

        // bad lines only (synthetic problems appended above are not bad lines)
        static int CountBad(GateResult r, string expected)
        {
            int n = 0;
            foreach (string p in r.problems)
                if (BadRx.IsMatch(p) && !p.StartsWith("Pipeline: unhandled error serving ") && !(expected != null && p == expected)) n++;
            return n;
        }

        static void Finish(State s)
        {
            s.active = false; s.phase = "done"; s.capturing = false;
            bool all = string.IsNullOrEmpty(s.error) && s.results.Count == s.gates.Count;
            foreach (GateResult r in s.results) if (!r.passed) all = false;
            WriteOutputs(s, true);
            SessionState.SetString(StateKey, "");
            Unhook();
            Debug.Log(Tag + "finished gates=" + s.results.Count + "/" + s.gates.Count + " allPassed=" + all);
            if (s.batch && Application.isBatchMode) EditorApplication.Exit(all ? 0 : 1);
        }

        static string ExpectedLine(string method)
        {
            if (method == "Emberdelve.EditorTools.EmberdelveContainerSnapshotGate.Run") return "InvalidOperationException: expected test exception from a throwing subscriber";
            if (method == "Emberdelve.EditorTools.EmberdelveInventoryAuthorityGate.Run") return "InvalidOperationException: expected inventory use observer exception";
            if (method == "Emberdelve.EditorTools.EmberdelveLoadoutD3cGate.Run") return "InvalidOperationException: expected D3c observer exception";
            return null;
        }

        // ---------------------------------------------------------------- scenes

        static void CaptureScenes(State s)
        {
            s.scenes.Clear();
            if (!s.restoreScenes) return;
            foreach (SceneSetup ss in EditorSceneManager.GetSceneManagerSetup())
                s.scenes.Add(new SceneEntry { path = ss.path, isLoaded = ss.isLoaded, isActive = ss.isActive });
        }

        static void RestoreScenes(State s)
        {
            if (!s.restoreScenes || s.scenes.Count == 0) return;
            try
            {
                SceneSetup[] cur = EditorSceneManager.GetSceneManagerSetup();
                bool same = cur.Length == s.scenes.Count;
                if (same)
                    for (int i = 0; i < cur.Length; i++)
                        if (cur[i].path != s.scenes[i].path || cur[i].isLoaded != s.scenes[i].isLoaded || cur[i].isActive != s.scenes[i].isActive) { same = false; break; }
                if (same) return;
                SceneSetup[] arr = new SceneSetup[s.scenes.Count];
                for (int i = 0; i < arr.Length; i++)
                    arr[i] = new SceneSetup { path = s.scenes[i].path, isLoaded = s.scenes[i].isLoaded, isActive = s.scenes[i].isActive };
                EditorSceneManager.RestoreSceneManagerSetup(arr);
            }
            catch (Exception e) { Debug.LogWarning(Tag + "scene restore failed: " + e.Message); }
        }

        // ---------------------------------------------------------------- resolution

        static string ProjectRoot() { return Directory.GetParent(Application.dataPath).FullName; }

        static string ArgValue(string[] args, string key)
        {
            for (int i = 0; i < args.Length - 1; i++) if (string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return null;
        }

        static string Normalize(string t)
        {
            t = t.Trim();
            if (t.IndexOf('.') < 0) return "Emberdelve.EditorTools.Emberdelve" + t + "Gate.Run";
            if (t.StartsWith("Emberdelve.")) return t;
            return "Emberdelve.EditorTools.Emberdelve" + t;
        }

        static string ShortName(string full)
        {
            int d = full.LastIndexOf('.');
            if (d < 0) return full;
            string method = full.Substring(d + 1);
            string type = full.Substring(0, d);
            int d2 = type.LastIndexOf('.');
            if (d2 >= 0) type = type.Substring(d2 + 1);
            if (type.StartsWith("Emberdelve") && type.Length > 10) type = type.Substring(10);
            if (type.EndsWith("Gate") && type.Length > 4) type = type.Substring(0, type.Length - 4);
            return method == "Run" ? type : type + "." + method;
        }

        static MethodInfo Resolve(string full)
        {
            int d = full.LastIndexOf('.');
            if (d < 0) return null;
            string typeName = full.Substring(0, d), method = full.Substring(d + 1);
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    Type t = a.GetType(typeName, false);
                    if (t == null) continue;
                    MethodInfo m = t.GetMethod(method, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null, Type.EmptyTypes, null);
                    if (m != null) return m;
                }
                catch (Exception) { }
            }
            return null;
        }

        static List<string> ExpandGates(string spec, out string err)
        {
            err = null;
            List<string> res = new List<string>();
            Dictionary<string, object> sets = LoadSets(out err);
            if (err != null) return res;
            foreach (string raw in spec.Split(new char[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string tok = raw.Trim();
                if (tok.Length == 0) continue;
                object o;
                if (sets != null && sets.TryGetValue(tok, out o) && o is List<object>)
                {
                    foreach (object g in (List<object>)o) { string n = Normalize(Convert.ToString(g)); if (!res.Contains(n)) res.Add(n); }
                }
                else { string n = Normalize(tok); if (!res.Contains(n)) res.Add(n); }
            }
            return res;
        }

        static Dictionary<string, object> LoadSets(out string err)
        {
            err = null;
            string path = Path.Combine(ProjectRoot(), "Tools/gate-sets.json");
            if (!File.Exists(path)) return null;
            try
            {
                Dictionary<string, object> root = MiniJson.Parse(File.ReadAllText(path)) as Dictionary<string, object>;
                object so;
                if (root != null && root.TryGetValue("sets", out so)) return so as Dictionary<string, object>;
                return null;
            }
            catch (Exception e) { err = "cannot parse gate-sets.json: " + e.Message; return null; }
        }

        // ---------------------------------------------------------------- output

        static string Esc(string v)
        {
            StringBuilder sb = new StringBuilder();
            foreach (char c in v ?? "")
            {
                switch (c)
                {
                    case '\u0022': sb.Append('\\').Append('\u0022'); break;
                    case '\\': sb.Append('\\').Append('\\'); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 32) sb.Append("\\u").Append(((int)c).ToString("x4")); else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        static string Q(string v) { return '\u0022' + Esc(v) + '\u0022'; }

        static string StrArray(List<string> l)
        {
            StringBuilder sb = new StringBuilder("[");
            for (int i = 0; i < l.Count; i++) { if (i > 0) sb.Append(','); sb.Append(Q(l[i])); }
            return sb.Append(']').ToString();
        }

        static void WriteOutputs(State s, bool done)
        {
            try
            {
                CultureInfo inv = CultureInfo.InvariantCulture;
                bool all = string.IsNullOrEmpty(s.error) && done && s.results.Count == s.gates.Count;
                foreach (GateResult r in s.results) if (!r.passed) all = false;
                double total = new TimeSpan(DateTime.UtcNow.Ticks - s.startedTicks).TotalSeconds;
                StringBuilder j = new StringBuilder();
                j.Append('{');
                j.Append(Q("status")).Append(':').Append(Q(done ? "done" : "running")).Append(',');
                j.Append(Q("passed")).Append(':').Append(all ? "true" : "false").Append(',');
                j.Append(Q("total")).Append(':').Append(s.gates.Count).Append(',');
                j.Append(Q("completed")).Append(':').Append(s.results.Count).Append(',');
                j.Append(Q("elapsedSeconds")).Append(':').Append(Math.Round(total, 1).ToString(inv)).Append(',');
                if (!string.IsNullOrEmpty(s.error)) j.Append(Q("error")).Append(':').Append(Q(s.error)).Append(',');
                j.Append(Q("results")).Append(':').Append('[');
                for (int i = 0; i < s.results.Count; i++)
                {
                    GateResult r = s.results[i];
                    if (i > 0) j.Append(',');
                    j.Append('{');
                    j.Append(Q("method")).Append(':').Append(Q(r.method)).Append(',');
                    j.Append(Q("name")).Append(':').Append(Q(r.name)).Append(',');
                    j.Append(Q("passed")).Append(':').Append(r.passed ? "true" : "false").Append(',');
                    j.Append(Q("exitCode")).Append(':').Append(r.exitCode).Append(',');
                    j.Append(Q("timedOut")).Append(':').Append(r.timedOut ? "true" : "false").Append(',');
                    j.Append(Q("seconds")).Append(':').Append(r.seconds.ToString(inv)).Append(',');
                    j.Append(Q("checks")).Append(':').Append(r.checks).Append(',');
                    j.Append(Q("failures")).Append(':').Append(r.failures).Append(',');
                    j.Append(Q("summaries")).Append(':').Append(StrArray(r.summaries)).Append(',');
                    j.Append(Q("problems")).Append(':').Append(StrArray(r.problems));
                    j.Append('}');
                }
                j.Append(']').Append('}');

                StringBuilder t = new StringBuilder();
                foreach (GateResult r in s.results)
                {
                    t.Append(r.passed ? "PASS " : "FAIL ").Append(r.name).Append(' ').Append(r.checks).Append('/').Append(r.failures).Append(' ')
                     .Append(r.seconds.ToString("0.0", inv)).Append('s');
                    if (r.timedOut) t.Append(" TIMEOUT");
                    t.Append('\n');
                    if (!r.passed)
                        for (int i = 0; i < r.problems.Count && i < 3; i++) t.Append("  ").Append(r.problems[i]).Append('\n');
                }
                if (done) t.Append("# gates=").Append(s.results.Count).Append('/').Append(s.gates.Count).Append(" allPassed=").Append(all ? "true" : "false").Append(" seconds=").Append(Math.Round(total, 1).ToString(inv)).Append('\n');
                AtomicWrite(Path.Combine(s.outDir, "summary.json"), j.ToString());
                AtomicWrite(Path.Combine(s.outDir, "summary.txt"), t.ToString());
            }
            catch (Exception e) { Debug.LogWarning(Tag + "could not write summary: " + e.Message); }
        }

        static void AtomicWrite(string path, string text)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, text, new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        // ---------------------------------------------------------------- minimal JSON reader

        sealed class MiniJson
        {
            string t; int i;
            public static object Parse(string s) { MiniJson p = new MiniJson(); p.t = s; return p.Val(); }
            void Ws() { while (i < t.Length && char.IsWhiteSpace(t[i])) i++; }
            object Val()
            {
                Ws();
                char c = t[i];
                if (c == '{')
                {
                    i++; Dictionary<string, object> d = new Dictionary<string, object>(); Ws();
                    if (t[i] == '}') { i++; return d; }
                    while (true) { Ws(); string k = Str(); Ws(); i++; d[k] = Val(); Ws(); if (t[i] == ',') { i++; continue; } i++; break; }
                    return d;
                }
                if (c == '[')
                {
                    i++; List<object> l = new List<object>(); Ws();
                    if (t[i] == ']') { i++; return l; }
                    while (true) { l.Add(Val()); Ws(); if (t[i] == ',') { i++; continue; } i++; break; }
                    return l;
                }
                if (c == '\u0022') return Str();
                int st = i;
                while (i < t.Length && Array.IndexOf(new char[] { ',', '}', ']', ' ', '\r', '\n', '\t' }, t[i]) < 0) i++;
                string lit = t.Substring(st, i - st);
                if (lit == "true") return true;
                if (lit == "false") return false;
                if (lit == "null") return null;
                return double.Parse(lit, CultureInfo.InvariantCulture);
            }
            string Str()
            {
                i++; StringBuilder sb = new StringBuilder();
                while (t[i] != '\u0022')
                {
                    if (t[i] == '\\')
                    {
                        i++;
                        switch (t[i])
                        {
                            case 'n': sb.Append('\n'); break;
                            case 't': sb.Append('\t'); break;
                            case 'r': sb.Append('\r'); break;
                            case 'u': sb.Append((char)Convert.ToInt32(t.Substring(i + 1, 4), 16)); i += 4; break;
                            default: sb.Append(t[i]); break;
                        }
                    }
                    else sb.Append(t[i]);
                    i++;
                }
                i++;
                return sb.ToString();
            }
        }
    }
}
