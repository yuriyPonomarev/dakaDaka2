using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Emberdelve.AI;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Interaction;
using Emberdelve.Items;
using Emberdelve.Player;

namespace Emberdelve.EditorTools
{
    /// <summary>
    /// R4+R5 gate: one seeded loot / identity pipeline with a project-owned RNG and a per-raid seed.
    /// Run: -executeMethod Emberdelve.EditorTools.EmberdelveLootPipelineGate.Run (opens the Level01 scene in play mode).
    /// Set EMBERDELVE_RAID_SEED to also check the injected-seed path of the real RunDirector.
    /// </summary>
    [InitializeOnLoad]
    public static class EmberdelveLootPipelineGate
    {
        private const string Pending = "Emberdelve.LootPipeline.Pending";

        static EmberdelveLootPipelineGate()
        {
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.EnteredPlayMode && SessionState.GetBool(Pending, false))
                {
                    SessionState.SetBool(Pending, false);
                    new GameObject("LootPipeline_Gate").AddComponent<LootPipelineHarness>();
                }
            };
        }

        public static void Run()
        {
            SessionState.SetBool(Pending, true);
            EditorSceneManager.OpenScene("Assets/Emberdelve/Scenes/Emberdelve_Level01.unity");
            EditorApplication.EnterPlaymode();
        }
    }

    public sealed class LootPipelineHarness : MonoBehaviour
    {
        private const string DataFolder = "Assets/Emberdelve/Data/";

        private int _n, _fail;
        private readonly List<UnityEngine.Object> _cleanup = new List<UnityEngine.Object>();

        private ItemCatalog _catalog;
        private AffixDefinition[] _affixes;
        private ItemData _boots, _cuirass, _cloak, _coin, _draught, _arrows, _relic;
        private Emberdelve.Flow.RunDirector _director;
        private PlayerRig _rig;

        private void Check(bool ok, string text)
        {
            _n++;
            if (!ok)
            {
                _fail++;
                Debug.LogError("[LootPipeline] mismatch " + text);
            }
        }

        private T Make<T>() where T : ScriptableObject
        {
            T x = ScriptableObject.CreateInstance<T>();
            _cleanup.Add(x);
            return x;
        }

        private IEnumerator Start()
        {
            yield return null;
            yield return null;

            IEnumerator all = Everything();
            while (true)
            {
                bool more;
                try { more = all.MoveNext(); }
                catch (Exception e) { _fail++; _n++; Debug.LogException(e); break; }
                if (!more) break;
                yield return all.Current;
            }

            foreach (UnityEngine.Object x in _cleanup) if (x != null) Destroy(x);

            Debug.Log("[Emberdelve][LootPipeline] " + (_fail == 0 ? "PASS" : "FAIL") + " checks=" + _n + " failures=" + _fail);
            EditorApplication.Exit(_fail == 0 ? 0 : 1);
        }

        private IEnumerator Everything()
        {
            _rig = FindFirstObjectByType<PlayerRig>();
            _director = FindFirstObjectByType<Emberdelve.Flow.RunDirector>();
            Check(_rig != null && _director != null && !_rig.IsDead, "actual living run host");
            if (_rig == null || _director == null) yield break;

            _catalog = AssetDatabase.LoadAssetAtPath<ItemCatalog>(EmberdelveAssetLibrary.ItemCatalogPath);
            Check(_catalog != null, "real item catalog asset loads");
            if (_catalog == null) yield break;
            _affixes = new List<AffixDefinition>(_catalog.Affixes).ToArray();
            _boots = AssetDatabase.LoadAssetAtPath<ItemData>(DataFolder + "Item_FootpadBoots.asset");
            _cuirass = AssetDatabase.LoadAssetAtPath<ItemData>(DataFolder + "Item_BoiledCuirass.asset");
            _cloak = AssetDatabase.LoadAssetAtPath<ItemData>(DataFolder + "Item_AshenCloak.asset");
            _coin = AssetDatabase.LoadAssetAtPath<ItemData>(DataFolder + "Item_EmberCoin.asset");
            _draught = AssetDatabase.LoadAssetAtPath<ItemData>(DataFolder + "Item_BloodDraught.asset");
            _arrows = AssetDatabase.LoadAssetAtPath<ItemData>(DataFolder + "Item_ArrowBundle.asset");
            _relic = AssetDatabase.LoadAssetAtPath<ItemData>(DataFolder + "Item_AshenRelic.asset");
            Check(_boots != null && _cuirass != null && _cloak != null && _coin != null && _draught != null && _arrows != null && _relic != null && _affixes.Length == 13,
                "real items and the 13 real affixes load");

            RngKnownAnswers();
            SeedDerivation();
            TableKnownAnswers();
            EnemyDropExpectations();
            DropKeys();
            SourceScan();
            RunStartWiring();
            SessionDeterminism();
            UniqueIdentity();
            RunSeedLifecycle();
            ChestWithoutSession();

            yield return LiveEnemyDropPickupAndEquip();
            yield return RejectedAndDuplicatePickup();
            yield return RealChestUsesRaidIssuer();
        }

        // ------------------------------------------------------------------------------------------ RNG

        private void RngKnownAnswers()
        {
            // Reference vectors for SplitMix64 (seed 0 and 1234567 are the published ones), computed by an
            // independent Python implementation.
            DeterministicRng r0 = new DeterministicRng(0);
            Check(r0.NextUInt64() == 0xE220A8397B1DCDAFUL && r0.NextUInt64() == 0x6E789E6AA1B965F4UL && r0.NextUInt64() == 0x06C45D188009454FUL,
                "SplitMix64 seed 0 known answers");

            DeterministicRng r1 = new DeterministicRng(1234567);
            ulong[] expect = { 6457827717110365317UL, 3203168211198807973UL, 9817491932198370423UL, 4593380528125082431UL, 16408922859458223821UL };
            bool ok = true;
            for (int i = 0; i < expect.Length; i++) ok &= r1.NextUInt64() == expect[i];
            Check(ok, "SplitMix64 seed 1234567 known answers");

            DeterministicRng rd = new DeterministicRng(0xDEADBEEFUL);
            Check(rd.NextUInt64() == 0x4adfb90f68c9eb9bUL && rd.NextUInt64() == 0xde586a3141a10922UL && rd.NextUInt64() == 0x021fbc2f8e1cfc1dUL && rd.NextUInt64() == 0x7466ce737be16790UL,
                "SplitMix64 seed 0xDEADBEEF known answers");

            DeterministicRng r42 = new DeterministicRng(42);
            Check(r42.NextUInt32() == 3184996902u && r42.NextUInt32() == 686809907u, "NextUInt32 known answers (high half)");
            Check(new DeterministicRng(42).NextDouble() == 0.7415648787718233, "NextDouble known answer (53-bit)");

            DeterministicRng r7 = new DeterministicRng(7);
            int[] ten = { 7, 4, 6, 3, 4, 5, 8, 2 };
            ok = true;
            for (int i = 0; i < ten.Length; i++) ok &= r7.Next(10) == ten[i];
            int[] thousand = { 985, 425, 83, 516 };
            for (int i = 0; i < thousand.Length; i++) ok &= r7.Next(1000) == thousand[i];
            Check(ok, "Next(int) known answers (unbiased rejection sampling)");

            DeterministicRng rr = new DeterministicRng(7);
            int[] ranged = { 2, -1, 1, -2, -1, 0, 3, -3 };
            ok = true;
            for (int i = 0; i < ranged.Length; i++) ok &= rr.Next(-5, 5) == ranged[i];
            Check(ok, "Next(min,max) known answers");

            // Edge ranges never overflow and stay in range.
            DeterministicRng re = new DeterministicRng(99);
            ok = true;
            for (int i = 0; i < 2000; i++)
            {
                int a = re.Next(int.MaxValue);
                int b = re.Next(int.MinValue, int.MaxValue);
                int c = re.Next(-3, -2);
                ok &= a >= 0 && a < int.MaxValue && b >= int.MinValue && b < int.MaxValue && c == -3;
            }
            Check(ok, "extreme ranges stay inside [min,max)");
            Check(new DeterministicRng(5).Next(1) == 0, "Next(1) is 0");
            bool threw = false;
            try { new DeterministicRng(5).Next(0); } catch (ArgumentOutOfRangeException) { threw = true; }
            Check(threw, "Next(0) is refused");
            threw = false;
            try { new DeterministicRng(5).Next(3, 3); } catch (ArgumentOutOfRangeException) { threw = true; }
            Check(threw, "Next(3,3) is refused");

            // Uniformity: 120000 draws over 6 buckets, each within 3% of the mean (fixed seed, so a stable result).
            int[] bucket = new int[6];
            DeterministicRng u = new DeterministicRng(2026);
            for (int i = 0; i < 120000; i++) bucket[u.Next(6)]++;
            ok = true;
            for (int i = 0; i < 6; i++) ok &= Mathf.Abs(bucket[i] - 20000) < 600;
            Check(ok, "Next(6) is uniform (" + string.Join(",", bucket) + ")");

            DeterministicRng dbl = new DeterministicRng(11);
            ok = true;
            for (int i = 0; i < 20000; i++) { double d = dbl.NextDouble(); ok &= d >= 0.0 && d < 1.0; }
            Check(ok, "NextDouble stays in [0,1)");

            // Streams.
            DeterministicRng parent = new DeterministicRng(0);
            DeterministicRng fa = parent.Fork(1);
            DeterministicRng fb = parent.Fork(2);
            Check(fa.NextUInt64() == 10705738211186751225UL && fa.NextUInt64() == 3032427185751592386UL && fa.NextUInt64() == 18053816495668303280UL,
                "Fork(1) of seed 0 known answers");
            Check(fb.NextUInt64() == 2292871244840924839UL && fb.NextUInt64() == 7058838293530920321UL && fb.NextUInt64() == 4274227827683330440UL,
                "Fork(2) of seed 0 known answers");

            // Independence: a stream does not depend on how much its parent or sibling consumed.
            DeterministicRng p1 = new DeterministicRng(555), p2 = new DeterministicRng(555);
            for (int i = 0; i < 100; i++) p2.NextUInt64();
            DeterministicRng s1 = p1.Fork(9), s2 = p2.Fork(9);
            ok = true;
            for (int i = 0; i < 50; i++) ok &= s1.NextUInt64() == s2.NextUInt64();
            Check(ok, "a fork ignores how much its parent consumed");

            DeterministicRng q1 = new DeterministicRng(555);
            ulong firstOfParent = new DeterministicRng(555).NextUInt64();
            q1.Fork(1).NextUInt64();
            q1.Fork(2).NextUInt64();
            Check(q1.NextUInt64() == firstOfParent, "forking does not advance the parent");

            DeterministicRng x1 = new DeterministicRng(31337).Fork(1), x2 = new DeterministicRng(31337).Fork(2);
            HashSet<ulong> seen = new HashSet<ulong>();
            int overlap = 0;
            for (int i = 0; i < 2000; i++) seen.Add(x1.NextUInt64());
            for (int i = 0; i < 2000; i++) if (seen.Contains(x2.NextUInt64())) overlap++;
            Check(overlap == 0, "two sibling streams share no value over 2000 draws");

            // Bitwise independence sanity: XOR of paired draws from two streams has a balanced bit count.
            DeterministicRng y1 = new DeterministicRng(8).Fork(1), y2 = new DeterministicRng(8).Fork(2);
            long ones = 0;
            for (int i = 0; i < 4000; i++) ones += PopCount(y1.NextUInt64() ^ y2.NextUInt64());
            Check(Math.Abs(ones - 4000L * 32L) < 4000L * 32L / 50, "sibling streams are bit-uncorrelated (" + ones + " ones in " + 4000 * 64 + ")");

            DeterministicRng rs = new DeterministicRng(77);
            ulong first = rs.NextUInt64();
            rs.NextUInt64();
            Check(rs.Restart().NextUInt64() == first && rs.Seed == 77UL, "Restart replays from the seed");
            Check(DeterministicRng.FromInt(-1).Seed == 0xFFFFFFFFUL && DeterministicRng.FromInt(5).Seed == 5UL, "int seeds are zero-extended");

            // Platform stability: the integer paths contain no floating point at all.
            string source = ReadRuntime("Core/DeterministicRng.cs");
            int a0 = source.IndexOf("public int Next(int maxExclusive)", StringComparison.Ordinal);
            int a1 = source.IndexOf("public double NextDouble()", StringComparison.Ordinal);
            string integerPaths = a0 >= 0 && a1 > a0 ? source.Substring(a0, a1 - a0) : "double";
            Check(!integerPaths.Contains("double") && !integerPaths.Contains("float") && !integerPaths.Contains("Math."),
                "integer RNG paths (Next) use no floating point");
            string code = StripComments(source);
            Check(!code.Contains("System.Random") && !code.Contains("UnityEngine"), "the RNG file depends on no platform generator or engine");

            // Mixing helpers.
            Check(RngMix.HashString("") == 0xcbf29ce484222325UL && RngMix.HashString("a") == 0xaf63dc4c8601ec8cUL && RngMix.HashString("chest") == 0x82d5d67636f69a16UL,
                "FNV-1a 64 known answers");
            Check(RngMix.Combine(0, 0) == 0xa706dd2f4d197e6fUL && RngMix.Combine(1, 2) == 0x51b01d53ca27e54dUL && RngMix.Combine(2, 1) == 0xe06dd043328bd285UL,
                "Combine known answers, and it is not commutative");
        }

        private static int PopCount(ulong v)
        {
            int c = 0;
            while (v != 0) { v &= v - 1; c++; }
            return c;
        }

        // ------------------------------------------------------------------------------------ derivation

        private void SeedDerivation()
        {
            const ulong raid = 0x0123456789ABCDEFUL;
            Check(RaidSession.ChestSeed(raid, "Chest_Ember_5_7_2", 303) == 0xb669b7a668a1bbaeUL, "chest seed known answer");
            Check(RaidSession.EnemyDropSeed(raid, "hollow_servitor@10,-8") == 0x09a811f9be908bcfUL, "enemy drop seed known answer");
            Check(RaidSession.UidStreamSeed(raid) == 0x4b62dbd973eee2b1UL, "uid stream seed known answer");

            HashSet<ulong> seeds = new HashSet<ulong>();
            seeds.Add(RaidSession.ChestSeed(raid, "c", 1));
            seeds.Add(RaidSession.ChestSeed(raid + 1, "c", 1));
            seeds.Add(RaidSession.ChestSeed(raid, "d", 1));
            seeds.Add(RaidSession.ChestSeed(raid, "c", 2));
            seeds.Add(RaidSession.EnemyDropSeed(raid, "c"));
            seeds.Add(RaidSession.UidStreamSeed(raid));
            Check(seeds.Count == 6, "raid, chest id, salt and domain each change the derived seed");
            Check(RaidSession.ChestSeed(raid, "c", -1) != RaidSession.ChestSeed(raid, "c", 1), "negative salt is a distinct salt");
        }

        // ----------------------------------------------------------------------------- table known answers

        private void TableKnownAnswers()
        {
            ItemData coin = CarriedItem("coin", ItemKind.Currency, 80);
            ItemData potion = CarriedItem("potion", ItemKind.Consumable, 5);
            ItemData arrows = CarriedItem("arrows", ItemKind.AmmoPack, 40);
            ItemData relic = CarriedItem("relic", ItemKind.Relic, 1);

            LootTable table = Make<LootTable>();
            table.maxRolls = 3;
            table.guaranteeFirstEntry = true;
            table.entries = new[]
            {
                new LootEntry { item = coin, minAmount = 2, maxAmount = 5, weight = 5, maxCopies = 2 },
                new LootEntry { item = potion, minAmount = 1, maxAmount = 1, weight = 4, maxCopies = 1 },
                new LootEntry { item = arrows, minAmount = 1, maxAmount = 2, weight = 3, maxCopies = 1 },
                new LootEntry { item = relic, minAmount = 1, maxAmount = 1, weight = 1, maxCopies = 1 },
            };

            // Expected values come from an independent Python implementation of the same algorithm
            // (shape stream = Fork(1) of SplitMix64(seed), guaranteed first entry, weighted picks).
            Check(RollShape(table, 123) == "coin5,coin4,potion1,arrows1", "table known answer seed 123: " + RollShape(table, 123));
            Check(RollShape(table, 7) == "coin3,coin3,potion1,relic1", "table known answer seed 7: " + RollShape(table, 7));
            Check(RollShape(table, 4294967295) == "coin3,potion1,coin4,arrows1", "table known answer seed 0xFFFFFFFF: " + RollShape(table, 4294967295));
            Check(RollShape(table, 0) == "coin3,coin2,arrows1,potion1", "table known answer seed 0: " + RollShape(table, 0));

            // The legacy int entry point is the zero-extended 64-bit seed.
            List<LootDrop> viaInt = new List<LootDrop>();
            Check(table.TryRoll(123, viaInt, null, out string reason), "carried table rolls with no catalog: " + reason);
            Check(Shape(viaInt) == RollShape(table, 123), "int seed overload equals the 64-bit seed");

            // nothingWeight: 0 keeps the stream; >0 turns rolls into misses.
            table.nothingWeight = 6;
            Check(RollShape(table, 123) == "coin5,coin4,potion1,arrows1", "nothingWeight 6 seed 123: " + RollShape(table, 123));
            Check(RollShape(table, 7) == "coin3,coin3,arrows2", "nothingWeight 6 seed 7: " + RollShape(table, 7));
            Check(RollShape(table, 4294967295) == "coin3,relic1,arrows1,coin2", "nothingWeight 6 seed 0xFFFFFFFF: " + RollShape(table, 4294967295));

            LootTable chance = Make<LootTable>();
            chance.maxRolls = 1;
            chance.nothingWeight = 1;
            chance.entries = new[] { new LootEntry { item = coin, minAmount = 1, maxAmount = 1, weight = 1, maxCopies = 1 } };
            int dropped = 0;
            for (ulong s = 0; s < 2000; s++) if (RollShape(chance, s).Length > 0) dropped++;
            Check(dropped > 850 && dropped < 1150, "nothingWeight 1 vs weight 1 drops about half the time (" + dropped + "/2000)");
        }

        private ItemData CarriedItem(string id, ItemKind kind, int stack)
        {
            ItemData item = Make<ItemData>();
            item.itemId = id;
            item.displayName = id;
            item.kind = kind;
            item.maxStack = stack;
            return item;
        }

        private string RollShape(LootTable table, ulong seed)
        {
            List<LootDrop> drops = new List<LootDrop>();
            Check(table.TryRollSeeded(seed, drops, null, ItemIdentityIssuer.CreateLocalTrusted(), out string reason), "shape roll: " + reason);
            return Shape(drops);
        }

        private static string Shape(List<LootDrop> drops)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < drops.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(drops[i].Item.itemId).Append(drops[i].Amount);
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------------------------- source scan

        private static string RuntimeRoot => Path.Combine(Application.dataPath, "Emberdelve", "Runtime");

        private static string ReadRuntime(string relative) => File.ReadAllText(Path.Combine(RuntimeRoot, relative));

        private static string StripComments(string source)
        {
            StringBuilder sb = new StringBuilder();
            foreach (string line in source.Split('\n'))
            {
                string trimmed = line.TrimStart();
                if (trimmed.StartsWith("//")) continue;
                sb.Append(line).Append('\n');
            }
            return sb.ToString();
        }

        private static readonly Regex RandomUse = new Regex(
            @"UnityEngine\.Random|System\.Random|\bnew\s+Random\s*\(|(?<![\w.])Random\.(Range|value|insideUnitCircle|insideUnitSphere|onUnitSphere|rotation|rotationUniform|ColorHSV|InitState|state|seed)\b",
            RegexOptions.Compiled);

        // Files in Runtime that may keep a non-project random source. Anything else under Runtime must be clean.
        // EnemyBrain: AI patrol waypoint angle jitter (movement, not loot, drop or identity; follow-up R4R5 note).
        // TorchFlicker: purely visual light flicker.
        private static readonly string[] RandomAllowlist = { "AI/EnemyBrain.cs", "Feedback/TorchFlicker.cs" };

        private void SourceScan()
        {
            string[] files = Directory.GetFiles(RuntimeRoot, "*.cs", SearchOption.AllDirectories);
            Check(files.Length > 50, "source scan sees the runtime files (" + files.Length + ")");

            List<string> offenders = new List<string>();
            HashSet<string> allowlistUsed = new HashSet<string>();
            foreach (string file in files)
            {
                string relative = file.Substring(RuntimeRoot.Length + 1).Replace('\\', '/');
                string code = StripComments(File.ReadAllText(file));
                if (!RandomUse.IsMatch(code)) continue;

                if (Array.IndexOf(RandomAllowlist, relative) >= 0) allowlistUsed.Add(relative);
                else offenders.Add(relative);
            }

            Check(offenders.Count == 0, "no UnityEngine.Random / System.Random in loot, drop, identity or gameplay-outcome runtime files; offenders: " + string.Join(", ", offenders));
            Check(allowlistUsed.Count == RandomAllowlist.Length, "the visual/AI allowlist is not stale (still uses a non-project random: " + string.Join(", ", allowlistUsed) + ")");

            // Other non-reproducible or platform sources, and low-level identity minting, outside explicit allowlists.
            // Entropy (RaidSession seed default) lives in RunContext; ItemInstance.cs owns the default uid draw; the review bridge
            // only stamps a log timestamp.
            (string pattern, string[] allowed)[] guarded =
            {
                (@"Unity\.Mathematics\.Random|\bnew\s+Unity\.Mathematics", new string[0]),
                (@"RandomNumberGenerator", new string[0]),
                (@"Environment\.TickCount|Stopwatch\.GetTimestamp", new string[0]),
                (@"Guid\.NewGuid\s*\(", new[] { "Core/RunContext.cs", "Items/ItemInstance.cs" }),
                (@"DateTime\.(Now|UtcNow)", new[] { "Core/RunContext.cs", "Review/EmberdelveReviewBridge.cs" }),
                (@"\bTryCreateNew\s*\(", new[] { "Items/ItemInstance.cs" }),
                (@"ItemIdentityRules\.TryMintUid\s*\(", new[] { "Items/ItemInstance.cs" }),
            };
            List<string> guardedHits = new List<string>();
            foreach (string file in files)
            {
                string relative = file.Substring(RuntimeRoot.Length + 1).Replace('\\', '/');
                string code = StripComments(File.ReadAllText(file));
                foreach ((string pattern, string[] allowed) g in guarded)
                {
                    if (Regex.IsMatch(code, g.pattern) && Array.IndexOf(g.allowed, relative) < 0) guardedHits.Add(relative + " ~ " + g.pattern);
                }
            }
            Check(guardedHits.Count == 0, "no Unity.Mathematics.Random, RandomNumberGenerator, TickCount, Guid.NewGuid, DateTime.Now or low-level uid minting outside the allowlist: " + string.Join("; ", guardedHits));

            // The scoped loot / identity files, named explicitly so a rename cannot silently drop them from the scan.
            string[] scoped =
            {
                "Items/LootRoller.cs", "Items/LootPickup.cs", "Items/RaidSession.cs", "Items/ItemInstance.cs", "Items/ItemCatalog.cs",
                "Items/Inventory.cs", "Items/Equipment.cs", "Items/ItemUidRegistry.cs", "Items/ContainerSnapshot.cs",
                "Data/LootTable.cs", "Data/EnemyData.cs", "Interaction/ChestInteractable.cs", "Level/ChestSpawner.cs",
                "Flow/RunDirector.cs", "AI/EnemyAgent.cs", "Core/RunContext.cs", "Core/DeterministicRng.cs",
            };
            bool allClean = true;
            foreach (string relative in scoped)
            {
                string path = Path.Combine(RuntimeRoot, relative);
                if (!File.Exists(path)) { allClean = false; Debug.LogError("[LootPipeline] mismatch scoped file missing: " + relative); continue; }
                if (RandomUse.IsMatch(StripComments(File.ReadAllText(path)))) { allClean = false; Debug.LogError("[LootPipeline] mismatch random source in " + relative); }
            }
            Check(allClean, "every scoped loot / identity file exists and is free of platform random sources");

            // One issuer per raid: production loot code never builds its own.
            string[] noPrivateIssuer = { "Interaction/ChestInteractable.cs", "AI/EnemyAgent.cs", "Items/LootPickup.cs", "Flow/RunDirector.cs", "Items/ItemCatalog.cs", "Level/ChestSpawner.cs" };
            bool none = true;
            foreach (string relative in noPrivateIssuer)
            {
                if (StripComments(ReadRuntime(relative)).Contains("CreateLocalTrusted(")) { none = false; Debug.LogError("[LootPipeline] mismatch private issuer in " + relative); }
            }
            Check(none, "chest / enemy / pickup / run / session code never creates a private issuer");

            List<string> issuerMakers = new List<string>();
            foreach (string file in files)
            {
                if (StripComments(File.ReadAllText(file)).Contains("CreateLocalTrusted(")) issuerMakers.Add(file.Substring(RuntimeRoot.Length + 1).Replace('\\', '/'));
            }
            issuerMakers.Sort(StringComparer.Ordinal);
            // ItemInstance.cs defines it, RaidSession (default, non-seeded session) wraps it, LootTable.cs has the tooling
            // overload, Inventory.cs is the lazy fixture fallback for a bag nobody pointed at an issuer.
            Check(string.Join(",", issuerMakers) == "Data/LootTable.cs,Items/Inventory.cs,Items/ItemInstance.cs,Items/RaidSession.cs",
                "CreateLocalTrusted is only referenced by its definition, the session, the table tooling overload and the bag fallback: " + string.Join(",", issuerMakers));

            string chest = StripComments(ReadRuntime("Interaction/ChestInteractable.cs"));
            Check(!chest.Contains(".TryRoll(") && chest.Contains("TryRollChest("), "the chest rolls through the raid session only");
            string run = StripComments(ReadRuntime("Flow/RunDirector.cs"));
            Check(run.Contains("SetIssuer(Session.Issuer)") && run.Contains("TryRollEnemyDrops("), "the run points the bag at the raid issuer and rolls enemy drops through the session");
            string enemy = StripComments(ReadRuntime("AI/EnemyAgent.cs"));
            Check(enemy.Contains("DropEnemyLoot(") && !enemy.Contains("lootDropChance") && !enemy.Contains("SpawnLoot("), "the enemy delegates its drop to the host and rolls nothing itself");
        }

        // ------------------------------------------------------------------ enemy drop expectations

        private void EnemyDropExpectations()
        {
            // Pins the documented balance numbers (Docs/Tasks/R4R5.md, AssetLibrary.ApplyEnemyDropTables) with the real tables.
            EnemyData warden = AssetDatabase.LoadAssetAtPath<EnemyData>(DataFolder + "Enemy_AshenWarden.asset");
            EnemyData servitor = AssetDatabase.LoadAssetAtPath<EnemyData>(DataFolder + "Enemy_HollowServitor.asset");
            Check(warden != null && warden.dropTable != null && servitor != null && servitor.dropTable != null, "both enemies reference a drop table");
            if (warden == null || servitor == null || warden.dropTable == null || servitor.dropTable == null) return;

            const int kills = 6000;
            Dictionary<string, int> w = new Dictionary<string, int>(), sv = new Dictionary<string, int>();
            int wardenGear = 0, wardenGearAffix1 = 0;
            for (int i = 0; i < kills; i++)
            {
                RaidSession session = RaidSession.Begin(RngMix.Combine((ulong)i, 0x77UL), "gate");
                List<LootDrop> a = new List<LootDrop>(), b = new List<LootDrop>();
                Check(session.TryRollEnemyDrops(warden, "warden@" + i, _catalog, a, out string r1) & session.TryRollEnemyDrops(servitor, "servitor@" + i, _catalog, b, out string r2), "enemy drop roll: " + r1 + r2);
                foreach (LootDrop d in a)
                {
                    w[d.Item.itemId] = (w.ContainsKey(d.Item.itemId) ? w[d.Item.itemId] : 0) + d.Amount;
                    if (d.Instance != null) { wardenGear++; if (d.Instance.AffixCount == 1) wardenGearAffix1++; }
                }
                foreach (LootDrop d in b) sv[d.Item.itemId] = (sv.ContainsKey(d.Item.itemId) ? sv[d.Item.itemId] : 0) + d.Amount;
            }

            double Rate(Dictionary<string, int> m, ItemData item) => (m.ContainsKey(item.itemId) ? m[item.itemId] : 0) / (double)kills;
            Debug.Log("[LootPipeline] info expected units per kill, warden: relic " + Rate(w, _relic) + " draught " + Rate(w, _draught) + " coin " + Rate(w, _coin) + " boots " + Rate(w, _boots) +
                      "; servitor: coin " + Rate(sv, _coin) + " draught " + Rate(sv, _draught) + " arrows " + Rate(sv, _arrows));
            Check(Math.Abs(Rate(w, _relic) - 0.68) < 0.035 && Math.Abs(Rate(w, _draught) - 0.68) < 0.035, "Warden drops about 0.68 relics and 0.68 draughts per kill (was 90% under the legacy rule)");
            Check(Math.Abs(Rate(w, _coin) - 0.90) < 0.035, "Warden drops about 0.90 coins per kill");
            Check(Math.Abs(Rate(w, _boots) - 0.39) < 0.035, "Warden drops about 0.39 Uncommon Footpad Boots per kill");
            Check(wardenGear > 0 && wardenGear == wardenGearAffix1, "every Warden gear drop is an Uncommon instance with exactly one rolled affix");
            Check(Math.Abs(Rate(sv, _coin) - 0.533) < 0.035 && Math.Abs(Rate(sv, _draught) - 0.533) < 0.035 && Math.Abs(Rate(sv, _arrows) - 0.533) < 0.035,
                "Servitor keeps the legacy expectation of about 0.53 units of each item per kill");
        }

        private void DropKeys()
        {
            EnemyAgent.ResetDropKeys();
            Vector3 spot = new Vector3(12.3f, 0f, -5.1f);
            string first = EnemyAgent.AllocateDropKey("gate_enemy", spot);
            string second = EnemyAgent.AllocateDropKey("gate_enemy", spot);
            string third = EnemyAgent.AllocateDropKey("gate_enemy", spot);
            Check(first == "gate_enemy@49,-20" && second == "gate_enemy@49,-20#1" && third == "gate_enemy@49,-20#2", "two enemies at one position get different drop keys, invariant formatting, ASCII minus: " + first + " " + second + " " + third);
            Check(EnemyAgent.AllocateDropKey("gate_enemy", spot + Vector3.right * 4f) == "gate_enemy@65,-20" && EnemyAgent.AllocateDropKey("other", spot) == "other@49,-20", "different cell or enemy id keeps the plain key");
            EnemyAgent.ResetDropKeys();
            Check(EnemyAgent.AllocateDropKey("gate_enemy", spot) == first, "a new run restarts allocation, so (raid seed, spawn order) fixes the keys");

            CultureInfo previous = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo odd = (CultureInfo)CultureInfo.InvariantCulture.Clone();
                odd.NumberFormat.NegativeSign = "\u2212";
                CultureInfo.CurrentCulture = odd;
                EnemyAgent.ResetDropKeys();
                Check(EnemyAgent.AllocateDropKey("gate_enemy", spot) == first, "the drop key does not depend on the current culture");
            }
            finally { CultureInfo.CurrentCulture = previous; EnemyAgent.ResetDropKeys(); }

            RaidSession session = RaidSession.Begin(4242UL, "gate");
            Check(session.EnemyDropSeed(first) != session.EnemyDropSeed(second), "the two keys derive different drop seeds");
        }

        // --------------------------------------------------------------------------- run-start wiring

        private void RunStartWiring()
        {
            Inventory bag = _rig.Inventory;
            RaidSession session = _director.Session;
            Check(session != null, "the run director owns a raid session");
            if (session == null) return;

            string env = Environment.GetEnvironmentVariable("EMBERDELVE_RAID_SEED");
            if (!string.IsNullOrEmpty(env) && ulong.TryParse(env, out ulong envSeed))
                Check(session.RaidSeed == envSeed && session.SeedSource != "entropy", "injected raid seed reaches the real run director (" + session + ")");
            else
                Check(session.SeedSource == "entropy", "default raid seed comes from entropy (" + session + ")");

            Check(ReferenceEquals(bag.Issuer, session.Issuer), "the player's bag mints with the raid's one issuer");

            int kit = 0;
            bool kitIssued = true;
            for (int i = 0; i < bag.UsedSlots; i++)
            {
                ItemInstance inst = bag.InstanceAt(i);
                if (inst == null) continue;
                kit++;
                kitIssued &= session.Issuer.HasIssued(inst.Uid);
            }
            foreach (EquipmentSlot slot in Enum.GetValues(typeof(EquipmentSlot)))
            {
                ItemInstance worn = _rig.Equipment.EquippedInstance(slot);
                if (worn == null) continue;
                kit++;
                kitIssued &= session.Issuer.HasIssued(worn.Uid);
            }
            Check(kitIssued, "every starting-kit instance was issued by the raid issuer (" + kit + " instances)");
            Debug.Log("[LootPipeline] info starting kit instances=" + kit + " " + session);

            // A legacy gear add (the bag mints a Common identity) also goes through the raid issuer.
            bag.Clear();
            _rig.Equipment.Clear();
            int before = session.Issuer.MintedCount;
            Check(bag.TryAdd(_boots, 1, out string reason), "legacy gear add works: " + reason);
            Check(session.Issuer.MintedCount == before + 1 && session.Issuer.HasIssued(FirstInstance(bag).Uid), "the bag's mint is counted by, and recorded in, the raid issuer");
            bag.Clear();
        }

        private static ItemInstance FirstInstance(Inventory bag)
        {
            for (int i = 0; i < bag.UsedSlots; i++)
            {
                ItemInstance inst = bag.InstanceAt(i);
                if (inst != null) return inst;
            }
            return null;
        }

        // ------------------------------------------------------------ same (raid, chest) -> same contents

        private LootTable GearTable(int maxRolls, int copies, bool guaranteeFirst)
        {
            LootTable t = Make<LootTable>();
            t.maxRolls = maxRolls;
            t.guaranteeFirstEntry = guaranteeFirst;
            t.affixCatalog = (AffixDefinition[])_affixes.Clone();
            t.entries = new[]
            {
                new LootEntry { item = _boots, minAmount = 1, maxAmount = 1, weight = 1, maxCopies = copies, affixRarity = AffixRarity.Rare },
                new LootEntry { item = _cuirass, minAmount = 1, maxAmount = 1, weight = 1, maxCopies = copies, affixRarity = AffixRarity.Uncommon },
                new LootEntry { item = _cloak, minAmount = 1, maxAmount = 1, weight = 1, maxCopies = copies, affixRarity = AffixRarity.Common },
            };
            return t;
        }

        private static string Signature(List<LootDrop> drops, bool withUids)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < drops.Count; i++)
            {
                LootDrop d = drops[i];
                sb.Append(d.Item.itemId).Append('x').Append(d.Amount);
                if (d.Instance != null)
                {
                    sb.Append('[').Append((int)d.Instance.Rarity);
                    for (int k = 0; k < d.Instance.AffixCount; k++)
                        sb.Append(':').Append(d.Instance.Affixes[k].Id).Append('=').Append(d.Instance.Affixes[k].Value.ToString("R", CultureInfo.InvariantCulture));
                    if (withUids) sb.Append('#').Append(d.Instance.UidText);
                    sb.Append(']');
                }
                sb.Append(';');
            }
            return sb.ToString();
        }

        private void SessionDeterminism()
        {
            LootTable ember = null, sealedTable = null;
            for (int i = 0; i < _director.Chests.Chests.Count; i++)
            {
                ChestInteractable c = _director.Chests.Chests[i];
                if (c.Variant == ChestVariant.Ember && ember == null) ember = c.LootTable;
                if (c.Variant == ChestVariant.Sealed && sealedTable == null) sealedTable = c.LootTable;
            }
            Check(ember != null && sealedTable != null, "the real ember and sealed chest tables are in the level");
            if (ember == null || sealedTable == null) return;

            const ulong raid = 0x5EED0000C0FFEEUL;
            string reason;

            // Same raid seed in three separate sessions (random uids).
            List<string> sigs = new List<string>();
            List<string> uidSigs = new List<string>();
            for (int s = 0; s < 3; s++)
            {
                RaidSession session = RaidSession.Begin(raid, "gate");
                List<LootDrop> drops = new List<LootDrop>();
                Check(session.TryRollChest(ember, "Chest_Ember_5_7_2", 303, _catalog, drops, out reason), "ember chest rolls: " + reason);
                sigs.Add(Signature(drops, false));
                uidSigs.Add(Signature(drops, true));
            }
            Check(sigs[0] == sigs[1] && sigs[1] == sigs[2] && sigs[0].Contains("["), "same (raid seed, chest) gives identical contents including rolled affixes: " + sigs[0]);
            Check(uidSigs[0] != uidSigs[1] && uidSigs[1] != uidSigs[2], "with random uids the identities differ between sessions even though contents are identical");

            // Same with deterministic uids: identities replay too.
            List<string> det = new List<string>();
            for (int s = 0; s < 2; s++)
            {
                RaidSession session = RaidSession.Begin(raid, "gate", true);
                List<LootDrop> drops = new List<LootDrop>();
                Check(session.TryRollChest(ember, "Chest_Ember_5_7_2", 303, _catalog, drops, out reason), "deterministic-uid roll: " + reason);
                det.Add(Signature(drops, true));
            }
            Check(det[0] == det[1] && det[0].Contains("#"), "deterministic-uid sessions replay contents AND uids");

            // The chest id, the salt and the table all matter.
            RaidSession one = RaidSession.Begin(raid, "gate");
            string baseSig = Sig(one, ember, "Chest_Ember_5_7_2", 303);
            Check(Sig(one, ember, "Chest_Ember_6_7_2", 303) != baseSig || Sig(one, ember, "Chest_Ember_9_1_4", 303) != baseSig, "a different chest id changes the contents");
            Check(Sig(one, ember, "Chest_Ember_5_7_2", 304) != baseSig || Sig(one, ember, "Chest_Ember_5_7_2", 305) != baseSig, "a different authored salt changes the contents");

            // Different raid seeds give different contents, over many seeds, for the same authored chest (the F7 bug).
            HashSet<string> distinctEmber = new HashSet<string>();
            HashSet<string> distinctSealed = new HashSet<string>();
            int seedCount = 300;
            for (int i = 0; i < seedCount; i++)
            {
                RaidSession session = RaidSession.Begin(RngMix.Combine((ulong)i, 0x1234UL), "gate");
                distinctEmber.Add(Sig(session, ember, "Chest_Ember_5_7_2", 303));
                distinctSealed.Add(Sig(session, sealedTable, "Chest_Sealed_9_9_4", 505));
            }
            Debug.Log("[LootPipeline] info distinct ember contents over " + seedCount + " raid seeds: " + distinctEmber.Count + ", sealed: " + distinctSealed.Count);
            Check(distinctEmber.Count > seedCount * 0.8, "different raid seeds give different ember chest contents (" + distinctEmber.Count + "/" + seedCount + ")");
            Check(distinctSealed.Count > seedCount * 0.5, "different raid seeds give different sealed chest contents (" + distinctSealed.Count + "/" + seedCount + ")");

            // The authored layout seeds are salts: five real chests of one raid roll five different seeds.
            HashSet<ulong> effective = new HashSet<ulong>();
            for (int i = 0; i < _director.Chests.Chests.Count; i++)
            {
                ChestInteractable c = _director.Chests.Chests[i];
                effective.Add(RaidSession.ChestSeed(raid, c.ChestId, c.Seed));
            }
            Check(effective.Count == _director.Chests.Chests.Count && effective.Count >= 5, "every real chest derives its own seed from the raid (" + effective.Count + ")");
        }

        private string Sig(RaidSession session, LootTable table, string chestId, int salt)
        {
            List<LootDrop> drops = new List<LootDrop>();
            Check(session.TryRollChest(table, chestId, salt, _catalog, drops, out string reason), "roll: " + reason);
            return Signature(drops, false);
        }

        // -------------------------------------------------------------- identity uniqueness in one run

        private void UniqueIdentity()
        {
            LootTable sim = GearTable(4, 3, false);
            EnemyData enemy = Make<EnemyData>();
            enemy.enemyId = "sim_enemy";
            enemy.dropTable = sim;

            foreach (bool deterministic in new[] { false, true })
            {
                RaidSession session = RaidSession.Begin(0xABCDEF01UL, "gate", deterministic);
                HashSet<Guid> all = new HashSet<Guid>();
                int instances = 0;
                bool allIssued = true, distinctLists = true;

                for (int i = 0; i < 300; i++)
                {
                    List<LootDrop> chest = new List<LootDrop>();
                    List<LootDrop> drops = new List<LootDrop>();
                    Check(session.TryRollChest(sim, "sim_chest_" + i, i, _catalog, chest, out string r1), "sim chest: " + r1);
                    Check(session.TryRollEnemyDrops(enemy, "sim_enemy@" + i + ",0", _catalog, drops, out string r2), "sim drop: " + r2);
                    foreach (List<LootDrop> list in new[] { chest, drops })
                    {
                        for (int k = 0; k < list.Count; k++)
                        {
                            if (list[k].Instance == null) continue;
                            instances++;
                            distinctLists &= all.Add(list[k].Instance.Uid);
                            allIssued &= session.Issuer.HasIssued(list[k].Instance.Uid);
                        }
                    }
                }

                string mode = deterministic ? "seeded" : "random";
                Check(instances >= 1000, mode + " session simulated at least 1000 drops and chest items (" + instances + ")");
                Check(distinctLists && all.Count == instances, mode + " session: no UID collision across " + instances + " simulated chest and enemy items");
                Check(allIssued && session.Issuer.MintedCount == instances, mode + " session: every UID was issued by the one raid issuer (" + session.Issuer.MintedCount + ")");

                // Across containers: bag, equipment and ground all draw from the one ledger, so a profile registry accepts them side by side.
                ItemUidRegistry registry = new ItemUidRegistry();
                List<Guid> bagUids = new List<Guid>(), groundUids = new List<Guid>();
                int idx = 0;
                foreach (Guid uid in all) { (idx++ % 2 == 0 ? bagUids : groundUids).Add(uid); }
                Check(registry.TryReplaceOwner("bag", bagUids, out string rb) & registry.TryReplaceOwner("ground", groundUids, out string rg),
                    mode + " session: bag and ground uid sets are disjoint in a profile ledger: " + rb + rg);
            }

            // The uid ledger inside the issuer refuses to reuse an identity: a stuck source mints once, then nothing.
            ItemIdentityIssuer stuck = ItemIdentityIssuer.CreateSeeded(new ConstantRng());
            Check(stuck.TryMintUid(out Guid first, out _) && first != Guid.Empty, "a seeded issuer mints from its stream");
            Check(!stuck.TryMintUid(out Guid second, out string stuckReason) && second == Guid.Empty && stuck.MintedCount == 1 && !string.IsNullOrEmpty(stuckReason),
                "an issuer whose source repeats mints nothing instead of reusing a uid");

            // Deterministic uid known answer (independent oracle): seed stream of raid 0x0123456789ABCDEF.
            RaidSession known = RaidSession.Begin(0x0123456789ABCDEFUL, "gate", true);
            Check(known.Issuer.TryMintUid(out Guid knownUid, out _) && knownUid.ToString("N") == "7e2cbb32962548d3931bbf7f100ebb3d",
                "seeded uid known answer: " + knownUid.ToString("N"));
        }

        private sealed class ConstantRng : ILootRng
        {
            public ulong NextUInt64() => 0x1122334455667788UL;
            public uint NextUInt32() => 1;
            public int Next(int maxExclusive) => 0;
            public int Next(int minInclusive, int maxExclusive) => minInclusive;
            public double NextDouble() => 0.5;
        }

        // ----------------------------------------------------------------- run seed and per-run issuer

        private void RunSeedLifecycle()
        {
            string env = Environment.GetEnvironmentVariable("EMBERDELVE_RAID_SEED");
            bool envSet = !string.IsNullOrEmpty(env) && ulong.TryParse(env, out _);

            RunContext.SetNextRaidSeed(12345UL);
            Check(RunContext.HasPendingRaidSeed, "an explicit next seed is pending");
            ulong a = RunContext.ResolveRaidSeed(out string srcA);
            Check(a == 12345UL && srcA == "explicit" && !RunContext.HasPendingRaidSeed, "an explicit seed wins and is consumed once");
            ulong b = RunContext.ResolveRaidSeed(out string srcB);
            if (envSet) Check(b == ulong.Parse(env) && srcB == "environment", "without an explicit seed the injected environment seed is used");
            else Check(b != 12345UL && srcB == "entropy", "the raid after an explicit one falls back to entropy, not a replay");
            RunContext.ClearNextRaidSeed();

            HashSet<ulong> defaults = new HashSet<ulong>();
            for (int i = 0; i < 1000; i++) defaults.Add(RunContext.DefaultRaidSeed());
            Check(defaults.Count == 1000 && !defaults.Contains(0UL), "1000 default raid seeds are all distinct");

            // A retry builds a new session: fresh issuer, no uid shared with the previous run.
            RaidSession run1 = RaidSession.Begin(RunContext.DefaultRaidSeed(), "entropy");
            RaidSession run2 = RaidSession.Begin(RunContext.DefaultRaidSeed(), "entropy");
            Check(!ReferenceEquals(run1.Issuer, run2.Issuer) && run1.RaidSeed != run2.RaidSeed, "each run gets its own issuer and seed");

            ItemUidRegistry profile = new ItemUidRegistry();
            List<Guid> uids1 = Mint(run1.Issuer, 50), uids2 = Mint(run2.Issuer, 50);
            Check(run2.Issuer.MintedCount == 50 && !uids1.Exists(u => run2.Issuer.HasIssued(u)), "the second run's issuer starts empty and shares no uid with the first");
            Check(profile.TryReplaceOwner("stash", uids1, out string r1) & profile.TryReplaceOwner("bag", uids2, out string r2),
                "the profile ledger holds run 1 (banked in the stash) and run 2 (the retry's bag) without a collision: " + r1 + r2);

            // Replaying the very same seed with deterministic uids reproduces identities, and the ledger refuses the replay.
            RaidSession replayA = RaidSession.Begin(99UL, "gate", true), replayB = RaidSession.Begin(99UL, "gate", true);
            List<Guid> ra = Mint(replayA.Issuer, 5), rb = Mint(replayB.Issuer, 5);
            ItemUidRegistry replayProfile = new ItemUidRegistry();
            Check(ra.Count == 5 && ra.TrueForAll(u => rb.Contains(u)), "same seed with seeded uids replays the same uids");
            Check(replayProfile.TryReplaceOwner("stash", ra, out _) && !replayProfile.TryReplaceOwner("bag", rb, out _), "the profile ledger refuses a replayed identity set");

            // Default sessions never replay identities even for the same raid seed.
            List<Guid> rx = Mint(RaidSession.Begin(99UL, "gate").Issuer, 5), ry = Mint(RaidSession.Begin(99UL, "gate").Issuer, 5);
            Check(!rx.Exists(u => ry.Contains(u)), "default (random-uid) sessions with the same raid seed issue different identities");
        }

        private static List<Guid> Mint(ItemIdentityIssuer issuer, int count)
        {
            List<Guid> list = new List<Guid>();
            for (int i = 0; i < count; i++) if (issuer.TryMintUid(out Guid uid, out _)) list.Add(uid);
            return list;
        }

        // -------------------------------------------------------------------- chest with no session

        private sealed class BareHost : MonoBehaviour, ILootHost
        {
            public Inventory Bag;
            public Inventory Inventory => Bag;
            public bool CollectLoot(ItemData item, int amount) => false;
            public void SpawnLoot(ItemData item, int amount, Vector3 worldPosition) { }
        }

        private void ChestWithoutSession()
        {
            GameObject go = new GameObject("NoSession_Chest");
            _cleanup.Add(go);
            BareHost host = go.AddComponent<BareHost>();
            host.Bag = _rig.Inventory;
            ChestInteractable chest = go.AddComponent<ChestInteractable>();
            chest.Configure(ChestVariant.Supply, GearTable(2, 1, true), 5, host);
            Check(!chest.Open() && chest.Remaining.Count == 0 && !chest.IsEmpty, "a chest whose host has no raid session refuses to roll (no private fallback seed or issuer)");
        }

        // --------------------------------------------------- live enemy drop -> pickup -> bag -> equipment

        private void ResetPlayer(int w, int h)
        {
            _rig.Equipment.Clear();
            _rig.Inventory.Clear();
            _rig.Inventory.ConfigureGrid(w, h);
        }

        private IEnumerator LiveEnemyDropPickupAndEquip()
        {
            Inventory bag = _rig.Inventory;
            ResetPlayer(10, 4);

            // Fixed raid seed for this section (test seam), so the expectations below are stable.
            RaidSession session = RaidSession.Begin(0xD209D209UL, "gate");
            _director.UseSessionForTest(session);
            Check(ReferenceEquals(bag.Issuer, session.Issuer), "the test seam re-points the bag at the new raid issuer");

            EnemyAgent agent = null;
            foreach (EnemyAgent candidate in FindObjectsByType<EnemyAgent>(FindObjectsSortMode.None))
            {
                if (!candidate.IsDead && candidate.Data != null) { agent = candidate; break; }
            }
            Check(agent != null, "a live enemy exists to kill");
            if (agent == null) yield break;

            // One guaranteed Rare boots drop, so the live path produces gear with two rolled affixes.
            LootTable rare = GearTable(1, 1, true);
            rare.entries = new[] { new LootEntry { item = _boots, minAmount = 1, maxAmount = 1, weight = 1, maxCopies = 1, affixRarity = AffixRarity.Rare } };
            EnemyData data = agent.Data;
            LootTable originalTable = data.dropTable;
            data.dropTable = rare;

            HashSet<LootPickup> before = new HashSet<LootPickup>(FindObjectsByType<LootPickup>(FindObjectsSortMode.None));
            string dropKey = agent.DropKey;
            agent.Health.ApplyDamage(new DamageInfo(100000f, agent.transform.position, Vector3.forward, agent.transform.position + Vector3.back, 0,
                EntityKind.LocalPlayer, DamageType.Physical, false, false, 0f));
            yield return null;
            data.dropTable = originalTable;

            Check(agent.IsDead, "the enemy died");
            List<LootPickup> fresh = new List<LootPickup>();
            foreach (LootPickup p in FindObjectsByType<LootPickup>(FindObjectsSortMode.None)) if (!before.Contains(p)) fresh.Add(p);
            Check(fresh.Count == 1 && fresh[0].Instance != null, "the killed enemy dropped one pickup holding a concrete instance (" + fresh.Count + ")");
            if (fresh.Count != 1 || fresh[0].Instance == null) yield break;

            LootPickup pickup = fresh[0];
            ItemInstance dropped = pickup.Instance;
            Check(dropped.Rarity == AffixRarity.Rare && dropped.AffixCount == 2 && dropped.Definition == _boots, "the dropped instance is Rare boots with two rolled affixes");
            Check(session.Issuer.HasIssued(dropped.Uid), "the dropped instance's UID came from the raid issuer");

            // The live drop equals the pure (raid seed, drop key) roll: same affixes, same everything but the identity.
            RaidSession twin = RaidSession.Begin(0xD209D209UL, "gate");
            EnemyData probe = Make<EnemyData>();
            probe.dropTable = rare;
            List<LootDrop> expected = new List<LootDrop>();
            Check(twin.TryRollEnemyDrops(probe, dropKey, _catalog, expected, out string reason), "twin roll: " + reason);
            Check(expected.Count == 1 && Signature(expected, false) == Signature(new List<LootDrop> { new LootDrop(_boots, 1, dropped) }, false),
                "the live drop equals the roll of (raid seed, drop key): " + Signature(expected, false));

            string snapshot = dropped.ToSnapshot().ToJson();
            int uidsBefore = session.Issuer.MintedCount;
            pickup.OnInteractInstant();
            yield return null;

            int slot = bag.IndexOfInstance(dropped.Uid);
            Check(slot >= 0 && ReferenceEquals(bag.InstanceAt(slot), dropped), "pickup puts the very same instance into the bag");
            Check(bag.InstanceAt(slot).ToSnapshot().ToJson() == snapshot, "UID, rarity and rolled values survive pickup exactly");
            Check(session.Issuer.MintedCount == uidsBefore && pickup == null, "pickup mints nothing and removes the ground pickup");

            Check(_rig.Equipment.TryEquipFromInventory(slot, out reason) && ReferenceEquals(_rig.Equipment.EquippedInstance(EquipmentSlot.Legs), dropped), "the picked-up instance equips as itself: " + reason);
            Check(_rig.Equipment.EquippedInstance(EquipmentSlot.Legs).ToSnapshot().ToJson() == snapshot, "equipment holds the exact UID and rolls");
            Check(_director.Session.Issuer.MintedCount == uidsBefore, "equipping mints nothing");

            // Uncommon and Common gear from a drop table keep identity through the ground too.
            ResetPlayer(10, 4);
            LootTable mixed = GearTable(3, 1, true);
            probe.dropTable = mixed;
            List<LootDrop> mixedDrops = new List<LootDrop>();
            Check(session.TryRollEnemyDrops(probe, "mixed", _catalog, mixedDrops, out reason) && mixedDrops.Count >= 2, "mixed-rarity drop table rolls: " + reason);
            bool allInstances = true, affixCountsRight = true;
            List<LootPickup> ground = new List<LootPickup>();
            for (int i = 0; i < mixedDrops.Count; i++)
            {
                LootDrop d = mixedDrops[i];
                allInstances &= d.Instance != null;
                if (d.Instance == null) continue;
                affixCountsRight &= d.Instance.AffixCount == (int)d.Instance.Rarity;
                ground.Add(_director.SpawnLootInstance(d.Instance, _rig.transform.position + Vector3.forward * 2f));
            }
            Check(allInstances && affixCountsRight, "every gear drop is a concrete instance with affix count equal to its rarity (Common 0, Uncommon 1, Rare 2)");
            yield return null;
            bool preserved = true;
            for (int i = 0; i < ground.Count; i++)
            {
                ItemInstance inst = ground[i].Instance;
                string snap = inst.ToSnapshot().ToJson();
                ground[i].OnInteractInstant();
                int idx = bag.IndexOfInstance(inst.Uid);
                preserved &= idx >= 0 && ReferenceEquals(bag.InstanceAt(idx), inst) && bag.InstanceAt(idx).ToSnapshot().ToJson() == snap;
            }
            Check(preserved, "Common, Uncommon and Rare ground pickups all land in the bag as the same instances");
            yield return null;

            // A definition pickup still behaves as before (partial acceptance, U1C2H).
            ResetPlayer(1, 1);
            ItemData stackCoin = Make<ItemData>();
            stackCoin.itemId = "pipeline_coin";
            stackCoin.kind = ItemKind.Currency;
            stackCoin.maxStack = 80;
            stackCoin.value = 1;
            Check(bag.TryAdd(stackCoin, 79, out reason), "bag holds 79 coins");
            GameObject coinGo = new GameObject("Pipeline_CoinPickup");
            _cleanup.Add(coinGo);
            LootPickup coinPickup = coinGo.AddComponent<LootPickup>();
            coinPickup.Configure(stackCoin, 5, _director);
            coinPickup.OnInteractInstant();
            Check(bag.TotalUnits == 80 && coinPickup.Amount == 4 && coinPickup.Instance == null, "a definition pickup keeps the counted partial contract (4 stay on the ground)");
            ResetPlayer(10, 4);
        }

        private IEnumerator RejectedAndDuplicatePickup()
        {
            Inventory bag = _rig.Inventory;
            RaidSession session = _director.Session;
            ResetPlayer(1, 1);

            ItemData filler = Make<ItemData>();
            filler.itemId = "pipeline_filler";
            filler.maxStack = 1;
            Check(bag.TryAdd(filler, 1, out string reason), "full bag fixture: " + reason);

            LootTable one = GearTable(1, 1, true);
            one.entries = new[] { new LootEntry { item = _cuirass, minAmount = 1, maxAmount = 1, weight = 1, maxCopies = 1, affixRarity = AffixRarity.Uncommon } };
            EnemyData probe = Make<EnemyData>();
            probe.dropTable = one;
            List<LootDrop> drops = new List<LootDrop>();
            Check(session.TryRollEnemyDrops(probe, "rejected", _catalog, drops, out reason) && drops.Count == 1 && drops[0].Instance != null, "Uncommon cuirass drop: " + reason);
            ItemInstance instance = drops[0].Instance;
            string snapshot = instance.ToSnapshot().ToJson();

            LootPickup pickup = _director.SpawnLootInstance(instance, _rig.transform.position + Vector3.forward * 2f);
            yield return null;
            int units = bag.TotalUnits;
            int issued = session.Issuer.MintedCount;
            pickup.OnInteractInstant();
            yield return null;

            Check(pickup != null && ReferenceEquals(pickup.Instance, instance) && pickup.Instance.ToSnapshot().ToJson() == snapshot,
                "a refused pickup (full bag) stays on the ground holding the same instance, UID and rolls");
            Check(!bag.HoldsInstanceUid(instance.Uid) && bag.TotalUnits == units && session.Issuer.MintedCount == issued, "the refused pickup changed nothing in the bag and minted nothing");

            // Free the space: the same pickup now delivers exactly that instance.
            bag.Clear();
            bag.ConfigureGrid(10, 4);
            pickup.OnInteractInstant();
            yield return null;
            int slot = bag.IndexOfInstance(instance.Uid);
            Check(slot >= 0 && ReferenceEquals(bag.InstanceAt(slot), instance) && bag.InstanceAt(slot).ToSnapshot().ToJson() == snapshot && pickup == null,
                "after the bag has room, the retried pickup delivers the exact instance and disappears");

            // The same identity on the ground twice is refused the second time (one uid is one item).
            LootPickup twin = _director.SpawnLootInstance(instance, _rig.transform.position + Vector3.forward * 2f);
            yield return null;
            int count = bag.UsedSlots;
            twin.OnInteractInstant();
            yield return null;
            Check(twin != null && bag.UsedSlots == count && bag.IndexOfInstance(instance.Uid) == slot, "a second pickup of an identity the bag already holds is refused and stays on the ground");
            Destroy(twin.gameObject);

            ResetPlayer(10, 4);
        }

        // --------------------------------------------------------------------------- a real chest

        private IEnumerator RealChestUsesRaidIssuer()
        {
            Inventory bag = _rig.Inventory;
            ResetPlayer(10, 4);
            RaidSession session = RaidSession.Begin(0xFEED0001UL, "gate");
            _director.UseSessionForTest(session);

            ChestInteractable ember = null;
            for (int i = 0; i < _director.Chests.Chests.Count; i++)
            {
                ChestInteractable c = _director.Chests.Chests[i];
                if (c.Variant == ChestVariant.Ember && !c.Opened && c.Remaining.Count == 0) { ember = c; break; }
            }

            if (ember == null)
            {
                Check(false, "an unopened ember chest exists");
                yield break;
            }

            int before = session.Issuer.MintedCount;
            Check(ember.Open(), "the real ember chest opens through the raid session");
            int minted = session.Issuer.MintedCount - before;
            bool allMine = true;
            int gear = 0;
            for (int i = 0; i < bag.UsedSlots; i++)
            {
                ItemInstance inst = bag.InstanceAt(i);
                if (inst == null) continue;
                gear++;
                allMine &= session.Issuer.HasIssued(inst.Uid);
            }
            Check(gear > 0 && minted == gear && allMine, "chest gear in the bag was issued by the raid issuer (" + gear + " items, " + minted + " minted)");

            // The chest's contents equal the pure (raid, chest id, salt) roll.
            RaidSession twin = RaidSession.Begin(0xFEED0001UL, "gate");
            List<LootDrop> expected = new List<LootDrop>();
            Check(twin.TryRollChest(ember.LootTable, ember.ChestId, ember.Seed, _catalog, expected, out string reason), "twin chest roll: " + reason);
            int expectedGear = 0;
            for (int i = 0; i < expected.Count; i++) if (expected[i].Instance != null) expectedGear++;
            Check(expectedGear == gear, "the opened chest holds what (raid seed, chest id, salt) says it holds");
            yield return null;
            ResetPlayer(10, 4);
        }
    }
}
