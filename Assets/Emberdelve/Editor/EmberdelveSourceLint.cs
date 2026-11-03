using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Emberdelve.EditorTools
{
    /// <summary>
    /// Non-blocking source-text lint. These are the architecture rules that used to live in the gates as "grep the .cs files" checks.
    /// They are advisory: a finding is logged as "[Emberdelve][Lint] WARN: rule: file:line" and never fails a gate or this run.
    ///
    ///   Unity.exe -batchmode -nographics -projectPath . -logFile Logs/lint.log -executeMethod Emberdelve.EditorTools.EmberdelveSourceLint.Run
    ///
    /// Matching is on comment- and string-stripped code (positions and line numbers are preserved), and call tokens are whole
    /// identifiers (a negative lookbehind for identifier characters), so OnDestroy() never matches Destroy(.
    /// Final line: [Emberdelve][Lint] PASS checks=N failures=0 warnings=W
    /// </summary>
    public static class EmberdelveSourceLint
    {
        private const string Tag = "[Emberdelve][Lint]";
        private const string Id = @"[A-Za-z0-9_]";

        private static int _checks;
        private static int _warnings;
        private static string _assets;
        private static string _runtime;
        private static string _net;
        private static string _netRules;
        private static List<RpcInfo> _rpcs;
        private static readonly Dictionary<string, string> Cache = new Dictionary<string, string>();

        public static void Run()
        {
            _checks = 0;
            _warnings = 0;
            _rpcs = null;
            Cache.Clear();
            _assets = Application.dataPath.Replace('\\', '/');
            _runtime = Path.Combine(Application.dataPath, "Emberdelve", "Runtime");
            _net = Path.Combine(Application.dataPath, "Emberdelve", "Net");
            _netRules = Path.Combine(Application.dataPath, "Emberdelve", "NetRules");

            RunRule("BladeDefense/parry-not-consumed-outside-data-types", ParryNotConsumed);
            RunRule("CombatFlow/no-stamina-in-runtime-code", NoStamina);
            RunRule("ItemCatalog/single-authority-builder", ItemCatalogAuthority);
            RunRule("LootPipeline/runtime-visible-to-scan", RuntimeVisible);
            RunRule("LootPipeline/no-platform-random-in-runtime", PlatformRandom);
            RunRule("LootPipeline/guarded-nondeterministic-sources", GuardedSources);
            RunRule("LootPipeline/scoped-loot-identity-files-clean", ScopedFilesClean);
            RunRule("LootPipeline/no-private-issuer", NoPrivateIssuer);
            RunRule("LootPipeline/CreateLocalTrusted-reference-set", IssuerMakerSet);
            RunRule("LootPipeline/chest-run-enemy-wiring", LootWiring);
            RunRule("LootPipeline/DeterministicRng-integer-paths-and-purity", RngPurity);
            RunRule("Net/rpc-count-nonvacuous", RpcCount);
            RunRule("Net/rpc-no-identity-parameter", RpcIdentityParameters);
            RunRule("Net/rpc-no-obsolete-RequireOwnership", RpcRequireOwnership);
            RunRule("NetMovement/movement-rpcs-exist", MovementRpcs);
            RunRule("NetCombat+NetSkill/rpcs-outside-development-guard", RpcsOutsideGuard);
            RunRule("NetCombat+NetSkill/intent-rpc-everyone-permission", IntentRpcPermission);
            RunRule("NetSkill/forge-and-abuse-probes-inside-development-guard", ProbesInsideGuard);
            RunRule("NetSkill/no-SkillIntentProofRpc", NoProofRpc);
            RunRule("NetCombat/server-checks-identity-then-gate-then-execute", ServerOrder);
            RunRule("NetCombat/client-combat-code-never-applies-damage", ClientNeverDamages);
            RunRule("NetCombat/server-driven-entry-points", ServerDrivenEntryPoints);
            RunRule("NetSkill/slot-bound-from-SkillNetLimits", SlotBound);
            RunRule("RetroVisual/forbidden-call-matcher-self-test", ForbiddenSelfTest);
            RunRule("RetroVisual/EnemyVisualDriver-presentation-only", DriverPresentationOnly);

            Debug.Log(Tag + " PASS checks=" + _checks + " failures=0 warnings=" + _warnings);
            EmberdelveGateExit.Exit(0);
        }

        // ------------------------------------------------------------------ runner

        private static void RunRule(string rule, Action<string> body)
        {
            _checks++;
            try { body(rule); }
            catch (Exception e) { Warn(rule, "lint rule threw " + e.GetType().Name + ": " + e.Message, 0); }
        }

        private static void Warn(string rule, string file, int line)
        {
            _warnings++;
            Debug.Log(Tag + " WARN: " + rule + ": " + file + ":" + line);
        }

        private static string Rel(string absolute)
        {
            string a = absolute.Replace('\\', '/');
            return a.StartsWith(_assets, StringComparison.OrdinalIgnoreCase) ? "Assets" + a.Substring(_assets.Length) : a;
        }

        private static int LineOf(string code, int index)
        {
            int line = 1;
            for (int i = 0; i < index && i < code.Length; i++) if (code[i] == '\n') line++;
            return line;
        }

        private static string Code(string path)
        {
            if (!Cache.TryGetValue(path, out string code))
            {
                code = File.Exists(path) ? Strip(File.ReadAllText(path)) : null;
                Cache[path] = code;
            }

            return code;
        }

        private static string[] RuntimeFiles() { return Directory.Exists(_runtime) ? Directory.GetFiles(_runtime, "*.cs", SearchOption.AllDirectories) : new string[0]; }

        private static string[] NetFiles() { return Directory.Exists(_net) ? Directory.GetFiles(_net, "*.cs", SearchOption.AllDirectories) : new string[0]; }

        private static string RuntimeRelative(string file) { return file.Substring(_runtime.Length + 1).Replace('\\', '/'); }

        private static bool Missing(string rule, string path)
        {
            if (Code(path) != null) return false;
            Warn(rule, Rel(path) + " (file missing)", 0);
            return true;
        }

        /// <summary>Blanks comments, string literals and char literals; newlines and positions are preserved so indices map to lines.</summary>
        internal static string Strip(string s)
        {
            StringBuilder sb = new StringBuilder(s.Length);
            int n = s.Length;
            int i = 0;
            bool lineStart = true;
            while (i < n)
            {
                char c = s[i];
                char d = i + 1 < n ? s[i + 1] : '\0';
                if (lineStart && (c == ' ' || c == '\t')) { sb.Append(c); i++; continue; }
                if (lineStart && c == '#')
                {
                    while (i < n && s[i] != '\n') { sb.Append(s[i]); i++; }
                    continue;
                }

                lineStart = false;
                if (c == '\n') { sb.Append(c); i++; lineStart = true; continue; }
                if (c == '/' && d == '/')
                {
                    while (i < n && s[i] != '\n') { sb.Append(s[i] == '\r' ? '\r' : ' '); i++; }
                    continue;
                }

                if (c == '/' && d == '*')
                {
                    sb.Append("  ");
                    i += 2;
                    while (i < n && !(s[i] == '*' && i + 1 < n && s[i + 1] == '/')) { sb.Append(s[i] == '\n' || s[i] == '\r' ? s[i] : ' '); i++; }
                    if (i < n) { sb.Append("  "); i += 2; }
                    continue;
                }

                if (c == '$' || c == '@' || c == '"')
                {
                    int j = i;
                    bool verbatim = false;
                    while (j < n && (s[j] == '$' || s[j] == '@')) { if (s[j] == '@') verbatim = true; j++; }
                    if (j < n && s[j] == '"')
                    {
                        for (int k = i; k < j; k++) sb.Append(' ');
                        sb.Append('"');
                        i = j + 1;
                        while (i < n)
                        {
                            char e = s[i];
                            if (verbatim)
                            {
                                if (e == '"' && i + 1 < n && s[i + 1] == '"') { sb.Append("  "); i += 2; continue; }
                                if (e == '"') break;
                            }
                            else
                            {
                                if (e == '\\' && i + 1 < n) { sb.Append(' ').Append(s[i + 1] == '\n' || s[i + 1] == '\r' ? s[i + 1] : ' '); i += 2; continue; }
                                if (e == '"' || e == '\n') break;
                            }

                            sb.Append(e == '\n' || e == '\r' ? e : ' ');
                            i++;
                        }

                        if (i < n && s[i] == '"') { sb.Append('"'); i++; }
                        continue;
                    }
                }

                if (c == '\'')
                {
                    int j = i + 1;
                    if (j < n && s[j] == '\\') j += 2;
                    else j += 1;
                    while (j < n && s[j] != '\'' && j - i < 12 && s[j] != '\n') j++;
                    if (j < n && s[j] == '\'')
                    {
                        for (int k = i; k <= j; k++) sb.Append(' ');
                        i = j + 1;
                        continue;
                    }
                }

                sb.Append(c);
                i++;
            }

            return sb.ToString();
        }

        /// <summary>A whole-identifier call: not preceded by an identifier character, followed by optional space and "(".</summary>
        private static Regex Call(string name)
        {
            return new Regex(@"(?<!" + Id + ")" + Regex.Escape(name) + @"\s*\(");
        }

        private static Regex Word(string name)
        {
            return new Regex(@"(?<!" + Id + ")" + Regex.Escape(name) + "(?!" + Id + ")");
        }

        private static void ScanFiles(string rule, IEnumerable<string> files, Regex regex, Func<string, bool> skip)
        {
            foreach (string file in files)
            {
                if (skip != null && skip(file)) continue;
                string code = Code(file);
                if (code == null) continue;
                foreach (Match m in regex.Matches(code)) Warn(rule, Rel(file), LineOf(code, m.Index));
            }
        }

        // ------------------------------------------------------------------ BladeDefense / CombatFlow / ItemCatalog

        private static void ParryNotConsumed(string rule)
        {
            if (!Directory.Exists(_runtime)) { Warn(rule, "the runtime source folder was not found", 0); return; }
            string[] allowed = { "DefenseModel.cs", "WeaponData.cs", "PlayerRig.cs", "BladeDefenseRules.cs" };
            Regex parry = new Regex(@"(?<!" + Id + @")CanParry(?!" + Id + @")|(?<!" + Id + @")DefenseCapability\s*\.\s*Parry(?!" + Id + ")");
            ScanFiles(rule, RuntimeFiles(), parry, f => Array.IndexOf(allowed, Path.GetFileName(f)) >= 0);
        }

        private static void NoStamina(string rule)
        {
            Regex stamina = new Regex(Id + "*[Ss]tamina" + Id + "*");
            ScanFiles(rule, RuntimeFiles(), stamina, f => Path.GetFileName(f) == "StaminaComponent.cs");
        }

        private static void ItemCatalogAuthority(string rule)
        {
            Regex authority = new Regex(@"(?<!" + Id + @")ItemInstanceAuthority\s*\.\s*TryCreate\s*\(");
            Regex testSeam = new Regex(@"(?<!" + Id + @")(CreateForTest|RegisterForTest)\s*\(");
            Regex local = new Regex(@"(?<!" + Id + @")LocalCatalogFor" + Id + "*");
            Func<string, bool> notCatalog = f => Path.GetFileName(f) == "ItemCatalog.cs";
            ScanFiles(rule + " (production builds its own authority)", RuntimeFiles(), authority, notCatalog);
            ScanFiles(rule + " (test-only catalog seam used in production)", RuntimeFiles(), testSeam, notCatalog);
            ScanFiles(rule + " (per-definition local catalog survives)", RuntimeFiles(), local, null);
        }

        // ------------------------------------------------------------------ LootPipeline

        private static readonly Regex RandomUse = new Regex(
            @"UnityEngine\s*\.\s*Random|System\s*\.\s*Random|(?<!" + Id + @")new\s+Random\s*\(|(?<![\w.])Random\s*\.\s*(Range|value|insideUnitCircle|insideUnitSphere|onUnitSphere|rotation|rotationUniform|ColorHSV|InitState|state|seed)(?!" + Id + ")");

        // EnemyBrain: AI patrol waypoint angle jitter; TorchFlicker / WallStopFeedback: purely visual.
        private static readonly string[] RandomAllowlist = { "AI/EnemyBrain.cs", "Feedback/TorchFlicker.cs", "Feedback/WallStopFeedback.cs" };

        private static void RuntimeVisible(string rule)
        {
            int count = RuntimeFiles().Length;
            if (count <= 50) Warn(rule + " (only " + count + " runtime files seen)", Rel(_runtime), 0);
        }

        private static void PlatformRandom(string rule)
        {
            HashSet<string> used = new HashSet<string>();
            foreach (string file in RuntimeFiles())
            {
                string relative = RuntimeRelative(file);
                string code = Code(file);
                Match m = RandomUse.Match(code);
                if (!m.Success) continue;
                if (Array.IndexOf(RandomAllowlist, relative) >= 0) used.Add(relative);
                else Warn(rule, Rel(file), LineOf(code, m.Index));
            }

            foreach (string allowed in RandomAllowlist)
                if (!used.Contains(allowed)) Warn(rule + " (allowlist entry is stale)", Rel(Path.Combine(_runtime, allowed)), 0);
        }

        private static void GuardedSources(string rule)
        {
            (string pattern, string[] allowed)[] guarded =
            {
                (@"Unity\s*\.\s*Mathematics\s*\.\s*Random|(?<!" + Id + @")new\s+Unity\s*\.\s*Mathematics", new string[0]),
                (@"(?<!" + Id + @")RandomNumberGenerator(?!" + Id + ")", new string[0]),
                (@"Environment\s*\.\s*TickCount|Stopwatch\s*\.\s*GetTimestamp", new string[0]),
                (@"(?<!" + Id + @")Guid\s*\.\s*NewGuid\s*\(", new[] { "Core/RunContext.cs", "Items/ItemInstance.cs", "Rules/ItemIdentityRules.cs", "Net/NetLobbyBridge.cs" }),
                (@"(?<!" + Id + @")DateTime\s*\.\s*(Now|UtcNow)(?!" + Id + ")", new[] { "Core/RunContext.cs", "Review/EmberdelveReviewBridge.cs" }),
                (@"(?<!" + Id + @")TryCreateNew\s*\(", new[] { "Items/ItemInstance.cs" }),
                (@"(?<!" + Id + @")ItemIdentityRules\s*\.\s*TryMintUid\s*\(", new[] { "Items/ItemInstance.cs" }),
            };
            foreach (string file in RuntimeFiles())
            {
                string relative = RuntimeRelative(file);
                string code = Code(file);
                foreach ((string pattern, string[] allowed) g in guarded)
                {
                    Match m = Regex.Match(code, g.pattern);
                    if (m.Success && Array.IndexOf(g.allowed, relative) < 0) Warn(rule + " [" + g.pattern + "]", Rel(file), LineOf(code, m.Index));
                }
            }
        }

        private static readonly string[] ScopedLootFiles =
        {
            "Items/LootRoller.cs", "Items/LootPickup.cs", "Items/RaidSession.cs", "Items/ItemInstance.cs", "Items/ItemCatalog.cs",
            "Rules/ItemIdentityRules.cs", "Items/LootTableRollService.cs",
            "Items/Inventory.cs", "Items/Equipment.cs", "Items/ItemUidRegistry.cs", "Items/ContainerSnapshot.cs",
            "Data/LootTable.cs", "Data/EnemyData.cs", "Interaction/ChestInteractable.cs", "Level/ChestSpawner.cs",
            "Flow/RunDirector.cs", "AI/EnemyAgent.cs", "Core/RunContext.cs", "Core/DeterministicRng.cs",
        };

        private static void ScopedFilesClean(string rule)
        {
            foreach (string relative in ScopedLootFiles)
            {
                string path = Path.Combine(_runtime, relative);
                if (Missing(rule + " (scoped file missing)", path)) continue;
                string code = Code(path);
                Match m = RandomUse.Match(code);
                if (m.Success) Warn(rule, Rel(path), LineOf(code, m.Index));
            }
        }

        private static readonly Regex CreateLocalTrusted = new Regex(@"(?<!" + Id + @")CreateLocalTrusted\s*\(");

        private static void NoPrivateIssuer(string rule)
        {
            string[] none = { "Interaction/ChestInteractable.cs", "AI/EnemyAgent.cs", "Items/LootPickup.cs", "Flow/RunDirector.cs", "Items/ItemCatalog.cs", "Level/ChestSpawner.cs" };
            foreach (string relative in none)
            {
                string path = Path.Combine(_runtime, relative);
                if (Missing(rule + " (file missing)", path)) continue;
                string code = Code(path);
                Match m = CreateLocalTrusted.Match(code);
                if (m.Success) Warn(rule, Rel(path), LineOf(code, m.Index));
            }
        }

        private static void IssuerMakerSet(string rule)
        {
            // ItemInstance.cs defines it, RaidSession wraps it, LootTableRollService.cs has the tooling overload, Inventory.cs is the lazy fixture fallback.
            HashSet<string> expected = new HashSet<string> { "Items/Inventory.cs", "Items/ItemInstance.cs", "Items/LootTableRollService.cs", "Items/RaidSession.cs" };
            HashSet<string> found = new HashSet<string>();
            foreach (string file in RuntimeFiles())
            {
                string code = Code(file);
                Match m = CreateLocalTrusted.Match(code);
                if (!m.Success) continue;
                string relative = RuntimeRelative(file);
                found.Add(relative);
                if (!expected.Contains(relative)) Warn(rule + " (unexpected reference)", Rel(file), LineOf(code, m.Index));
            }

            foreach (string relative in expected)
                if (!found.Contains(relative)) Warn(rule + " (expected reference is gone)", Rel(Path.Combine(_runtime, relative)), 0);
        }

        private static void LootWiring(string rule)
        {
            string chestPath = Path.Combine(_runtime, "Interaction", "ChestInteractable.cs");
            if (!Missing(rule, chestPath))
            {
                string chest = Code(chestPath);
                Match bad = Regex.Match(chest, @"\.\s*TryRoll\s*\(");
                if (bad.Success) Warn(rule + " (chest rolls outside the raid session)", Rel(chestPath), LineOf(chest, bad.Index));
                if (!Call("TryRollChest").IsMatch(chest)) Warn(rule + " (chest never calls TryRollChest)", Rel(chestPath), 0);
            }

            string runPath = Path.Combine(_runtime, "Flow", "RunDirector.cs");
            if (!Missing(rule, runPath))
            {
                string run = Code(runPath);
                if (!Regex.IsMatch(run, @"SetIssuer\s*\(\s*Session\s*\.\s*Issuer\s*\)")) Warn(rule + " (run does not point the bag at the raid issuer)", Rel(runPath), 0);
                if (!Call("TryRollEnemyDrops").IsMatch(run)) Warn(rule + " (run does not roll enemy drops through the session)", Rel(runPath), 0);
            }

            string enemyPath = Path.Combine(_runtime, "AI", "EnemyAgent.cs");
            if (!Missing(rule, enemyPath))
            {
                string enemy = Code(enemyPath);
                if (!Call("DropEnemyLoot").IsMatch(enemy)) Warn(rule + " (enemy does not delegate its drop to the host)", Rel(enemyPath), 0);
                Match chance = Word("lootDropChance").Match(enemy);
                if (chance.Success) Warn(rule + " (enemy rolls its own drop chance)", Rel(enemyPath), LineOf(enemy, chance.Index));
                Match spawn = Call("SpawnLoot").Match(enemy);
                if (spawn.Success) Warn(rule + " (enemy spawns loot itself)", Rel(enemyPath), LineOf(enemy, spawn.Index));
            }
        }

        private static void RngPurity(string rule)
        {
            string path = Path.Combine(_runtime, "Core", "DeterministicRng.cs");
            if (Missing(rule, path)) return;
            string code = Code(path);
            Match a0 = Regex.Match(code, @"public\s+int\s+Next\s*\(\s*int\s+maxExclusive\s*\)");
            Match a1 = Regex.Match(code, @"public\s+double\s+NextDouble\s*\(\s*\)");
            if (!a0.Success || !a1.Success || a1.Index <= a0.Index) { Warn(rule + " (integer path boundaries not found)", Rel(path), 0); }
            else
            {
                string integerPaths = code.Substring(a0.Index, a1.Index - a0.Index);
                Match fp = Regex.Match(integerPaths, @"(?<!" + Id + @")(double|float)(?!" + Id + @")|(?<!" + Id + @")Math\s*\.");
                if (fp.Success) Warn(rule + " (floating point in integer RNG path)", Rel(path), LineOf(code, a0.Index + fp.Index));
            }

            Match platform = Regex.Match(code, @"System\s*\.\s*Random|UnityEngine");
            if (platform.Success) Warn(rule + " (RNG file depends on a platform generator or the engine)", Rel(path), LineOf(code, platform.Index));
        }

        // ------------------------------------------------------------------ Net: [Rpc] structure

        private sealed class RpcInfo
        {
            public string File;
            public int Line;
            public string Attribute;
            public string Method;
            public string Parameters;
        }

        private static List<RpcInfo> Rpcs()
        {
            if (_rpcs != null) return _rpcs;
            _rpcs = new List<RpcInfo>();
            foreach (string file in NetFiles())
            {
                string code = Code(file);
                int from = 0;
                while (true)
                {
                    int at = code.IndexOf("[Rpc(", from, StringComparison.Ordinal);
                    if (at < 0) break;
                    from = at + 5;
                    int close = code.IndexOf(']', at);
                    int open = close < 0 ? -1 : code.IndexOf('(', close);
                    int end = open < 0 ? -1 : code.IndexOf(')', open);
                    if (close < 0 || end < 0) continue;
                    string head = code.Substring(close + 1, open - close - 1);
                    Match name = Regex.Match(head, @"(" + Id + @"+)\s*$");
                    _rpcs.Add(new RpcInfo
                    {
                        File = Rel(file),
                        Line = LineOf(code, at),
                        Attribute = code.Substring(at, close - at),
                        Method = name.Success ? name.Groups[1].Value : head.Trim(),
                        Parameters = code.Substring(open, end - open + 1),
                    });
                }
            }

            return _rpcs;
        }

        private static void RpcCount(string rule)
        {
            int count = Rpcs().Count;
            if (count < 14) Warn(rule + " (found " + count + ", expected >= 14)", Rel(_net), 0);
        }

        private static void RpcIdentityParameters(string rule)
        {
            Regex identity = new Regex(@"(?i)(player|combat|party|sender|actor)\s*_?id");
            foreach (RpcInfo r in Rpcs()) if (identity.IsMatch(r.Parameters)) Warn(rule + " (" + r.Method + ")", r.File, r.Line);
        }

        private static void RpcRequireOwnership(string rule)
        {
            foreach (RpcInfo r in Rpcs()) if (r.Attribute.IndexOf("RequireOwnership", StringComparison.Ordinal) >= 0) Warn(rule + " (" + r.Method + ")", r.File, r.Line);
        }

        private static void MovementRpcs(string rule)
        {
            bool submit = false, forge = false, ack = false;
            foreach (RpcInfo r in Rpcs())
            {
                if (r.Method.Contains("SubmitMoveRpc")) submit = true;
                if (r.Method.Contains("ForgeMoveTestRpc")) forge = true;
                if (r.Method.Contains("AckRpc")) ack = true;
            }

            if (!submit) Warn(rule + " (SubmitMoveRpc missing)", Rel(_net), 0);
            if (!forge) Warn(rule + " (ForgeMoveTestRpc missing)", Rel(_net), 0);
            if (!ack) Warn(rule + " (AckRpc missing)", Rel(_net), 0);
        }

        // ------------------------------------------------------------------ Net: development guard and structure

        /// <summary>True when the index lies inside an unclosed "#if DEVELOPMENT_BUILD" block (nested #if blocks are tracked).</summary>
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

        private static void RpcsOutsideGuard(string rule)
        {
            string path = Path.Combine(_net, "NetPlayerRig.cs");
            if (Missing(rule, path)) return;
            string rig = Code(path);
            string[] names = { "CombatIntentRpc", "CombatResultRpc", "CombatSwingRpc", "CombatShotRpc", "CombatFlightEndRpc", "CombatHitRpc", "EnemyPoseRpc", "EnemyVitalsRpc", "SkillIntentRpc", "SkillResultRpc", "SkillVisualRpc" };
            foreach (string name in names)
            {
                Match m = Regex.Match(rig, @"public\s+void\s+" + name + @"\s*\(");
                if (!m.Success) Warn(rule + " (" + name + " not found)", Rel(path), 0);
                else if (InsideDevelopmentGuard(rig, m.Index)) Warn(rule + " (" + name + " is inside the development guard)", Rel(path), LineOf(rig, m.Index));
            }
        }

        private static void IntentRpcPermission(string rule)
        {
            string path = Path.Combine(_net, "NetPlayerRig.cs");
            if (Missing(rule, path)) return;
            string rig = Code(path);
            foreach (string name in new[] { "CombatIntentRpc", "SkillIntentRpc" })
            {
                bool ok = Regex.IsMatch(rig, @"\[Rpc\(\s*SendTo\s*\.\s*Server\s*,\s*InvokePermission\s*=\s*RpcInvokePermission\s*\.\s*Everyone\s*\)\]\s*public\s+void\s+" + name + @"\s*\(");
                if (!ok) Warn(rule + " (" + name + " is not a default-delivery server RPC with InvokePermission Everyone)", Rel(path), 0);
            }
        }

        private static void ProbesInsideGuard(string rule)
        {
            string rigPath = Path.Combine(_net, "NetPlayerRig.cs");
            if (!Missing(rule, rigPath))
            {
                string rig = Code(rigPath);
                foreach (string name in new[] { "ForgeOwnerTestRpc", "ForgeMoveTestRpc" })
                {
                    Match m = Regex.Match(rig, @"public\s+void\s+" + name + @"\s*\(");
                    if (!m.Success || !InsideDevelopmentGuard(rig, m.Index)) Warn(rule + " (" + name + " is not inside the development guard)", Rel(rigPath), m.Success ? LineOf(rig, m.Index) : 0);
                }
            }

            string combatSender = Path.Combine(_net, "NetCombatSender.cs");
            if (!Missing(rule, combatSender))
            {
                string code = Code(combatSender);
                Match m = Regex.Match(code, @"private\s+void\s+Abuse\s*\(");
                if (!m.Success || !InsideDevelopmentGuard(code, m.Index)) Warn(rule + " (combat Abuse probe is not inside the development guard)", Rel(combatSender), m.Success ? LineOf(code, m.Index) : 0);
            }

            string skillSender = Path.Combine(_net, "NetSkillSender.cs");
            if (!Missing(rule, skillSender))
            {
                string code = Code(skillSender);
                foreach (string name in new[] { "RunAbuse", "RunProof" })
                {
                    Match m = Regex.Match(code, @"private\s+void\s+" + name + @"\s*\(\s*\)");
                    if (!m.Success || !InsideDevelopmentGuard(code, m.Index)) Warn(rule + " (" + name + " is not inside the development guard)", Rel(skillSender), m.Success ? LineOf(code, m.Index) : 0);
                }
            }
        }

        private static void NoProofRpc(string rule)
        {
            string path = Path.Combine(_net, "NetPlayerRig.cs");
            if (Missing(rule, path)) return;
            string rig = Code(path);
            Match m = Word("SkillIntentProofRpc").Match(rig);
            if (m.Success) Warn(rule, Rel(path), LineOf(rig, m.Index));
        }

        private static void ServerOrder(string rule)
        {
            string path = Path.Combine(_net, "NetCombatServer.cs");
            if (Missing(rule, path)) return;
            string server = Code(path);
            Match identity = Regex.Match(server, @"IdentityFault\s*\(\s*target\s*,\s*sender");
            Match gate = Regex.Match(server, @"conn\s*\.\s*Gate\s*\.\s*Submit\s*\(");
            Match execute = Regex.Match(server, @":\s*Execute\s*\(\s*conn");
            if (!(identity.Success && gate.Success && execute.Success && gate.Index > identity.Index && execute.Index > gate.Index))
                Warn(rule + " (identity, then gate, then execute order not found)", Rel(path), 0);
            if (!Regex.IsMatch(server, @"(?<!" + Id + @")ChargeReject\s*\(\s*sender")) Warn(rule + " (rejects are not charged to the shared ledger)", Rel(path), 0);
        }

        private static void ClientNeverDamages(string rule)
        {
            foreach (string name in new[] { "NetCombatSender.cs", "NetCombatFx.cs", "NetEnemyViews.cs", "NetOwnerCombatView.cs" })
            {
                string path = Path.Combine(_net, name);
                if (Missing(rule, path)) continue;
                string code = Code(path);
                if (code.Trim().Length == 0) Warn(rule + " (file is empty)", Rel(path), 0);
                foreach (Regex bad in new[] { new Regex(@"\.\s*ApplyDamage\s*\("), Word("LocalCombatAuthority"), Word("TryBeginMeleeSwing") })
                {
                    Match m = bad.Match(code);
                    if (m.Success) Warn(rule, Rel(path), LineOf(code, m.Index));
                }
            }
        }

        private static void ServerDrivenEntryPoints(string rule)
        {
            string healthPath = Path.Combine(_runtime, "Combat", "HealthComponent.cs");
            if (!Missing(rule, healthPath))
            {
                string health = Code(healthPath);
                if (!Regex.IsMatch(health, @"ClientBlockedDamage\s*\+\+") || !Regex.IsMatch(health, @"NetRole\s*\.\s*IsClientOnly")) Warn(rule + " (HealthComponent does not refuse damage in a client-only process)", Rel(healthPath), 0);
            }

            string meleePath = Path.Combine(_runtime, "Combat", "MeleeCombat.cs");
            if (!Missing(rule, meleePath) && !Word("RemoteDriven").IsMatch(Code(meleePath))) Warn(rule + " (MeleeCombat has no RemoteDriven entry point)", Rel(meleePath), 0);
            string rangedPath = Path.Combine(_runtime, "Combat", "RangedCombat.cs");
            if (!Missing(rule, rangedPath) && !Word("ServerReleaseDraw").IsMatch(Code(rangedPath))) Warn(rule + " (RangedCombat has no ServerReleaseDraw entry point)", Rel(rangedPath), 0);
        }

        private static void SlotBound(string rule)
        {
            string casterPath = Path.Combine(_runtime, "Skills", "SkillCaster.cs");
            Regex limits = new Regex(@"SkillNetLimits\s*\.\s*ActiveSlots");
            if (!Missing(rule, casterPath) && !limits.IsMatch(Code(casterPath))) Warn(rule + " (SkillCaster does not declare its slots from SkillNetLimits.ActiveSlots)", Rel(casterPath), 0);
            string gatePath = Path.Combine(_netRules, "SkillIntentGate.cs");
            if (!Missing(rule, gatePath))
            {
                string gate = Code(gatePath);
                if (!limits.IsMatch(gate)) Warn(rule + " (SkillIntentGate does not bound the slot with SkillNetLimits.ActiveSlots)", Rel(gatePath), 0);
                Match literal = Regex.Match(gate, @"slot\s*>=\s*\d");
                if (literal.Success) Warn(rule + " (literal slot bound)", Rel(gatePath), LineOf(gate, literal.Index));
            }
        }

        // ------------------------------------------------------------------ RetroVisual: presentation-only driver

        private static readonly string[] DriverForbiddenCalls = { "ApplyDamage", "ApplyStatus", "SetStunned", "SetHealth", "Heal", "TryBeginAttack", "MarkDead", "NotifyDamaged", "Destroy", "DestroyImmediate" };

        private static void ForbiddenSelfTest(string rule)
        {
            Regex destroy = Call("Destroy");
            string stripped = Strip("private void OnDestroy() { Destroy(gameObject); } // Destroy(x)\nvar s = \"Destroy(y)\"; Object.Destroy (x);");
            MatchCollection matches = destroy.Matches(stripped);
            if (matches.Count != 2 || destroy.IsMatch("private void OnDestroy()") || !destroy.IsMatch("Object.Destroy (x);"))
                Warn(rule + " (matcher found " + matches.Count + " Destroy( calls, expected 2)", "Assets/Emberdelve/Editor/EmberdelveSourceLint.cs", 0);
        }

        private static void DriverPresentationOnly(string rule)
        {
            string path = Path.Combine(_runtime, "AI", "EnemyVisualDriver.cs");
            if (Missing(rule + " (EnemyVisualDriver source exists)", path)) return;
            string code = Code(path);
            List<Regex> forbidden = new List<Regex>();
            foreach (string call in DriverForbiddenCalls) forbidden.Add(Call(call));
            forbidden.Add(new Regex(@"(?<!" + Id + @")transform\s*\.\s*(position|rotation)\s*=(?!=)"));
            forbidden.Add(new Regex(@"(?<!" + Id + @")Physics\s*\."));
            foreach (Regex r in forbidden)
                foreach (Match m in r.Matches(code)) Warn(rule + " (" + Regex.Replace(m.Value, @"\s+", "") + ")", Rel(path), LineOf(code, m.Index));
        }
    }
}
