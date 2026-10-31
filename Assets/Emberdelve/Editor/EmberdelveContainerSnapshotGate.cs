using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Emberdelve.Combat;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Items;
using Emberdelve.Player;
using Emberdelve.Skills;
using Emberdelve.Stats;

namespace Emberdelve.EditorTools
{
    /// <summary>
    /// R9 gate: the versioned, validated, atomic container snapshot (bag + equipment) and its JSON.
    ///
    /// Play mode on the real level scene with the real rig, bag, equipment and the REAL item catalog asset (no test
    /// catalog). Covers: culture-independent exact float round trip (en-US, ru-RU, de-DE), determinism, every
    /// refusal in the task with the target asserted unchanged afterwards, tamper/checksum, cross-container uid
    /// uniqueness, restore followed by equip/unequip, the combined loadout's all-or-nothing restore and the schema
    /// version policy.
    ///
    /// Batch entry: Emberdelve.EditorTools.EmberdelveContainerSnapshotGate.Run
    /// </summary>
    [InitializeOnLoad]
    public static class EmberdelveContainerSnapshotGate
    {
        private const string Pending = "Emberdelve.ContainerSnapshot.Pending";

        static EmberdelveContainerSnapshotGate()
        {
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.EnteredPlayMode && SessionState.GetBool(Pending, false))
                {
                    SessionState.SetBool(Pending, false);
                    new GameObject("ContainerSnapshot_Gate").AddComponent<ContainerSnapshotHarness>();
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

    public sealed class ContainerSnapshotHarness : MonoBehaviour
    {
        private int n, fail;
        private readonly List<UnityEngine.Object> cleanup = new List<UnityEngine.Object>();

        private PlayerRig rig;
        private Inventory bag;
        private Equipment gear;
        private ItemCatalog cat;

        private ContainerSnapshot bagSnap0;
        private ContainerSnapshot gearSnap0;
        private LoadoutSnapshot loadout0;
        private string bagJson0, gearJson0, loadoutJson0;
        private string describe0, statsNoPools0, pristineStats;
        private WeaponData pristineMain, pristineOff;
        private int totalUnits0, carriedValue0;

        private void Check(bool ok, string text)
        {
            n++;
            if (!ok) { fail++; Debug.LogError("[ContainerSnapshot] mismatch " + text); }
        }

        private IEnumerator Start()
        {
            for (int i = 0; i < 10; i++) yield return null;

            try { Execute(); }
            catch (Exception e) { fail++; Debug.LogException(e); }
            finally { foreach (var x in cleanup) if (x != null) Destroy(x); }

            Debug.Log("[Emberdelve][ContainerSnapshot] " + (fail == 0 ? "PASS" : "FAIL") + " checks=" + n + " failures=" + fail);
            EmberdelveGateExit.Exit(fail == 0 ? 0 : 1);
        }

        // ------------------------------------------------------------------ helpers

        private static string F(float v) { return BitConverter.SingleToInt32Bits(v).ToString(CultureInfo.InvariantCulture); }

        private static string DescribeInstance(ItemInstance i)
        {
            if (i == null) return "-";
            StringBuilder sb = new StringBuilder();
            sb.Append(i.UidText).Append(':').Append(i.DefinitionId).Append(':').Append(i.Quantity).Append(':').Append((int)i.Rarity).Append(':').Append((int)i.Category);
            for (int a = 0; a < i.Affixes.Count; a++)
                sb.Append(';').Append(i.Affixes[a].Id).Append('/').Append(i.Affixes[a].CanonicalKey).Append('/').Append(F(i.Affixes[a].Value));
            return sb.ToString();
        }

        private string DescribeBagGear(Inventory b, Equipment g)
        {
            List<string> rows = new List<string>();
            for (int i = 0; i < b.Slots.Count; i++)
            {
                InventorySlot s = b.Slots[i];
                rows.Add(s.Origin.y.ToString("D3", CultureInfo.InvariantCulture) + "," + s.Origin.x.ToString("D3", CultureInfo.InvariantCulture) + "|" +
                         s.Item.itemId + "|" + s.Count + "|" + DescribeInstance(s.Instance));
            }
            rows.Sort(StringComparer.Ordinal);
            StringBuilder sb = new StringBuilder();
            sb.Append("bag{").Append(string.Join("#", rows)).Append("}units=").Append(b.TotalUnits).Append(",value=").Append(b.CarriedValue)
              .Append(",cells=").Append(b.UsedCells).Append(",grid=").Append(b.GridWidth).Append('x').Append(b.GridHeight).Append(" gear{");
            for (int s = 1; s < Equipment.SlotCount; s++)
                sb.Append((EquipmentSlot)s).Append('=').Append(DescribeInstance(g.EquippedInstance((EquipmentSlot)s))).Append('#');
            sb.Append('}');
            return sb.ToString();
        }

        private string Describe() { return DescribeBagGear(bag, gear); }

        private string StatsNoPools()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("armor=").Append(F(rig.GetArmorRating())).Append(" maxhp=").Append(F(rig.MaxHealth)).Append(" maxmana=").Append(rig.Mana.MaxMana)
              .Append(" ms=").Append(F(rig.Stats.Get(SkillStat.MoveSpeed, 0f))).Append(" mr=").Append(F(rig.Stats.Get(SkillStat.ManaRegen, 0f))).Append(" scores=");
            for (int i = 0; i < AttributeRules.AttributeCount; i++) sb.Append(F(rig.AttributeStats.GetAttribute((SixAttribute)i))).Append(',');
            sb.Append(" main=").Append(rig.MainHandWeapon != null ? rig.MainHandWeapon.name : "null")
              .Append(" off=").Append(rig.OffhandWeapon != null ? rig.OffhandWeapon.name : "null");
            return sb.ToString();
        }

        private string Pools() { return F(rig.CurrentHealth) + "/" + F(rig.Mana.CurrentMana); }

        /// <summary>Everything a refusal must leave untouched: bag, worn gear, counters, stats, pools, hands.</summary>
        private string State() { return Describe() + " | " + StatsNoPools() + " | " + Pools() + " | rec=" + bag.RecoveredValue + ",life=" + bag.LifetimeCollected; }

        private ItemData Def(string id)
        {
            bool ok = cat.TryResolveItem(id, out ItemData d, out string reason);
            Check(ok && d != null, "catalog resolves " + id + " " + reason);
            return d;
        }

        private ItemInstance Gear(ItemData def, AffixRarity rarity, params AffixRecord[] records)
        {
            bool ok = ItemInstance.TryCreateNew(def, 1, rarity, records.Length == 0 ? null : records, cat.Authority, out ItemInstance inst, out string reason);
            Check(ok && inst != null, $"mint {def.itemId} {rarity}: {reason}");
            return inst;
        }

        private static AffixRecord R(string id, AffixStatKey key, float value) { return new AffixRecord(id, key, value); }

        private void AddStack(ItemData d, int count)
        {
            Vector2Int o = bag.FindFreeOrigin(d.EffectiveFootprint);
            bool ok = o.x >= 0 && bag.TryAddAt(d, count, o, out string reason);
            Check(ok, $"stack {d.itemId} x{count} placed ({o})");
        }

        private static ContainerEntry GearEntry(ContainerSnapshot s, string itemId, int rarity)
        {
            for (int i = 0; i < s.entries.Length; i++)
            {
                ContainerEntry e = s.entries[i];
                if (e != null && e.item != null && e.item.itemId == itemId && e.item.rarity == rarity && !string.IsNullOrEmpty(e.item.uid)) return e;
            }
            return null;
        }

        private static ContainerEntry StackEntry(ContainerSnapshot s, string itemId)
        {
            for (int i = 0; i < s.entries.Length; i++)
            {
                ContainerEntry e = s.entries[i];
                if (e != null && e.item != null && e.item.itemId == itemId && string.IsNullOrEmpty(e.item.uid)) return e;
            }
            return null;
        }

        private static ContainerEntry SlotEntry(ContainerSnapshot s, string slot)
        {
            for (int i = 0; i < s.entries.Length; i++)
                if (s.entries[i] != null && s.entries[i].slot == slot) return s.entries[i];
            return null;
        }

        private ContainerSnapshot BagClone() { return bagSnap0.Clone(); }
        private ContainerSnapshot GearClone() { return gearSnap0.Clone(); }

        private static bool Has(string reason, string part)
        {
            return reason != null && reason.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void RefuseBag(string label, ContainerSnapshot s, string part, ItemUidRegistry others = null)
        {
            if (s == null) { Check(false, label + " fixture snapshot missing"); return; }
            string before = State();
            bool ok = bag.TryRestore(s, others, out string reason);
            Check(!ok, label + " is refused by the bag");
            Check(Has(reason, part), $"{label} reason names '{part}' (got: {reason})");
            Check(State() == before, label + " left the bag and equipment untouched");
        }

        private void RefuseGear(string label, ContainerSnapshot s, string part, ItemUidRegistry others = null)
        {
            if (s == null) { Check(false, label + " fixture snapshot missing"); return; }
            string before = State();
            bool ok = gear.TryRestore(s, others, out string reason);
            Check(!ok, label + " is refused by the equipment");
            Check(Has(reason, part), $"{label} reason names '{part}' (got: {reason})");
            Check(State() == before, label + " left the bag and equipment untouched");
        }

        private void RefuseLoadout(string label, LoadoutSnapshot s, string part, ItemUidRegistry others = null)
        {
            string before = State();
            bool ok = LoadoutSnapshot.TryRestore(s, bag, gear, others, out string reason);
            Check(!ok, label + " is refused as a loadout");
            Check(Has(reason, part), $"{label} reason names '{part}' (got: {reason})");
            Check(State() == before, label + " changed neither the bag nor the equipment");
        }

        private void RefuseJson(string label, string json, string part)
        {
            bool ok = ContainerSnapshot.TryFromJson(json, out ContainerSnapshot parsed, out string reason);
            Check(!ok && parsed == null, label + " json is refused with no snapshot");
            Check(Has(reason, part), $"{label} json reason names '{part}' (got: {reason})");
        }

        private void RefuseLoadoutJson(string label, string json, string part)
        {
            string before = State();
            bool ok = LoadoutSnapshot.TryRestoreJson(json, bag, gear, null, out string reason);
            Check(!ok, label + " loadout json is refused");
            Check(Has(reason, part), $"{label} loadout json reason names '{part}' (got: {reason})");
            Check(State() == before, label + " loadout json changed neither container");
        }

        private string Rep(string json, string find, string repl, string label)
        {
            int i = json.IndexOf(find, StringComparison.Ordinal);
            Check(i >= 0, label + ": mutation target '" + find + "' exists in the json");
            return i < 0 ? json : json.Substring(0, i) + repl + json.Substring(i + find.Length);
        }

        private string ReplaceFirstValueToken(string json, string token, string label)
        {
            int i = json.IndexOf("\"value\":", StringComparison.Ordinal);
            Check(i >= 0, label + ": a value member exists");
            if (i < 0) return json;
            int start = i + "\"value\":".Length;
            int end = start;
            while (end < json.Length && json[end] != ',' && json[end] != '}') end++;
            return json.Substring(0, start) + token + json.Substring(end);
        }

        private void WithCulture(string name, Action action)
        {
            CultureInfo oldC = Thread.CurrentThread.CurrentCulture;
            CultureInfo oldU = Thread.CurrentThread.CurrentUICulture;
            try
            {
                CultureInfo ci = CultureInfo.GetCultureInfo(name);
                Thread.CurrentThread.CurrentCulture = ci;
                Thread.CurrentThread.CurrentUICulture = ci;
                action();
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = oldC;
                Thread.CurrentThread.CurrentUICulture = oldU;
            }
        }

        // ------------------------------------------------------------------ the checks

        private void Execute()
        {
            rig = FindFirstObjectByType<PlayerRig>();
            Check(rig != null && !rig.IsDead && rig.AttributeStats != null && rig.AttributeStats.IsReady, "real living rig with ready attributes");
            if (rig == null) return;

            bag = rig.Inventory;
            gear = rig.Equipment;
            cat = bag.Catalog;
            Check(cat != null && !cat.IsTestCatalog && cat.IsValid(out string catReason), "the rig's bag holds the REAL item catalog asset (" + (cat != null ? "valid" : "missing") + ")");
            if (cat == null) return;
            ProductionStateChecks();

            gear.Clear();
            bag.Clear();
            bag.ConfigureGrid(10, 6);
            pristineStats = StatsNoPools();
            pristineMain = rig.MainHandWeapon;
            pristineOff = rig.OffhandWeapon;

            if (!BuildFixture()) return;

            FloatChecks();
            CultureRoundTrips();
            DeterminismChecks();
            BagRejections();
            GearRejections();
            JsonRejections();
            VersionChecks();
            ProductionMigrationChecks();
            RegistryChecks();
            LoadoutAtomicity();
            OverflowChecks();
            ChecksumSpellingAndMigration();
            ObserverAndLedgerChecks();
            RestoreThenEquipRoundTrips();
            NoCatalogAndGridChecks();
        }

        // ------------------------------------------------------------------ fixture

        private bool BuildFixture()
        {
            ItemData boots = Def("footpad_boots"), cloak = Def("ashen_cloak"), mantle = Def("emberthread_mantle"), cuirass = Def("boiled_cuirass");
            ItemData blade = Def("ember_fang_blade"), shield = Def("emberbulwark_shield");
            ItemData coin = Def("ember_coin"), draught = Def("blood_draught"), arrows = Def("arrow_bundle"), relic = Def("ashen_relic");
            if (boots == null || cloak == null || mantle == null || cuirass == null || blade == null || shield == null || coin == null) return false;

            ItemInstance bootsA = Gear(boots, AffixRarity.Rare, R("affix_move_speed", AffixStatKey.MoveSpeed, 0.09f), R("affix_mana_regen", AffixStatKey.ManaRegen, 0.3f));
            ItemInstance cloakU = Gear(cloak, AffixRarity.Uncommon, R("affix_move_speed", AffixStatKey.MoveSpeed, 0.07f));
            ItemInstance cuirassR = Gear(cuirass, AffixRarity.Rare, R("affix_constitution", AffixStatKey.Constitution, 2f), R("affix_armor", AffixStatKey.Armor, 3f));
            ItemInstance bladeR = Gear(blade, AffixRarity.Rare, R("affix_melee_damage", AffixStatKey.MeleeDamage, 3f), R("affix_mana_regen", AffixStatKey.ManaRegen, 0.1f));
            ItemInstance shieldC = Gear(shield, AffixRarity.Common);
            ItemInstance mantleR = Gear(mantle, AffixRarity.Rare, R("affix_armor", AffixStatKey.Armor, 4f), R("affix_max_mana", AffixStatKey.MaxMana, 10f));
            ItemInstance bootsB = Gear(boots, AffixRarity.Uncommon, R("affix_move_speed", AffixStatKey.MoveSpeed, 0.12f));
            ItemInstance bladeB = Gear(blade, AffixRarity.Rare, R("affix_melee_damage", AffixStatKey.MeleeDamage, 4f), R("affix_projectile_damage", AffixStatKey.ProjectileDamage, 1f));
            ItemInstance[] all = { bootsA, cloakU, cuirassR, bladeR, shieldC, mantleR, bootsB, bladeB };
            for (int i = 0; i < all.Length; i++) if (all[i] == null) return false;

            for (int i = 0; i < all.Length; i++)
            {
                bool added = bag.TryAddInstance(all[i], out string reason);
                Check(added, $"fixture instance {i} enters the bag: {reason}");
            }

            ItemInstance[] worn = { bootsA, cloakU, cuirassR, bladeR, shieldC };
            for (int i = 0; i < worn.Length; i++)
            {
                bool eq = gear.TryEquipFromInventory(bag.IndexOfInstance(worn[i].Uid), out string reason);
                Check(eq, $"fixture instance {i} equipped: {reason}");
            }

            AddStack(coin, 8);
            AddStack(coin, 3);
            AddStack(draught, 3);
            AddStack(arrows, 2);
            AddStack(relic, 1);

            Check(gear.FilledCount == 5 && bag.UsedSlots == 8, $"fixture shape: 5 worn, 8 bag stacks (worn {gear.FilledCount}, stacks {bag.UsedSlots})");
            Check(rig.MainHandWeapon == blade.grantedWeapon, "the worn blade is in the main hand");

            describe0 = Describe();
            statsNoPools0 = StatsNoPools();
            totalUnits0 = bag.TotalUnits;
            carriedValue0 = bag.CarriedValue;
            Check(statsNoPools0 != pristineStats, "wearing the fixture changes the stats");

            loadout0 = LoadoutSnapshot.Capture(bag, gear);
            // D3c: the version 4 members. The fixture states them explicitly (weapon set 1 in hand, an empty belt) so every older
            // document, which migrates to exactly these defaults, can be compared byte for byte with the current one.
            loadout0.weaponSet = 0;
            loadout0.belt = new[] { "", "", "", "" };
            loadout0.Seal();
            bagSnap0 = loadout0.bag.Clone();
            gearSnap0 = loadout0.equipment.Clone();
            bagJson0 = bagSnap0.ToJson();
            gearJson0 = gearSnap0.ToJson();
            loadoutJson0 = loadout0.ToJson();

            Check(bagSnap0.schemaVersion == ContainerSnapshot.CurrentSchemaVersion && bagSnap0.kind == "bag" && bagSnap0.containerId == "bag" && bagSnap0.width == 10 && bagSnap0.height == 6 && bagSnap0.entries.Length == 8,
                "bag snapshot header: current version (2 since D3a), bag, 10x6, 8 entries");
            Check(gearSnap0.kind == "equipment" && gearSnap0.width == 0 && gearSnap0.height == 0 && gearSnap0.entries.Length == 5, "equipment snapshot header: equipment, no grid, 5 entries");
            Check(bagSnap0.checksum.StartsWith("sha256:", StringComparison.Ordinal) && bagSnap0.checksum.Length == 71 && bagSnap0.ChecksumMatches(), "checksum shape sha256:+64 hex and it matches");
            Check(StackEntry(bagSnap0, "ember_coin") != null && string.IsNullOrEmpty(StackEntry(bagSnap0, "ember_coin").item.uid), "carried stacks are quantity entries by item id with no uid");
            Check(GearEntry(bagSnap0, "emberthread_mantle", 2) != null, "gear in the bag keeps uid, rarity and affixes");
            Check(bagJson0.Contains("\"value\":0.12") && gearJson0.Contains("\"value\":0.09") && gearJson0.Contains("\"value\":0.3") && gearJson0.Contains("\"value\":0.1}"),
                "fractional affix values are written as plain invariant decimals");
            return true;
        }

        // ------------------------------------------------------------------ floats

        private void FloatRoundTrip(string label)
        {
            List<float> values = new List<float>();
            for (int k = 0; k < 10; k++)
            {
                values.Add((float)(0.03 + k * 0.01));
                values.Add(0.03f + k * 0.01f);
            }
            for (int k = 0; k < 4; k++) { values.Add((float)(0.1 + k * 0.1)); values.Add(0.1f + k * 0.1f); }
            values.Add(0.09f); values.Add(0.1f); values.Add(0.07f); values.Add(1e-7f); values.Add(float.MaxValue); values.Add(float.Epsilon); values.Add(-0f); values.Add(123456.79f);
            uint state = 12345u;
            for (int k = 0; k < 4000; k++)
            {
                state = state * 1664525u + 1013904223u;
                float v = BitConverter.Int32BitsToSingle((int)state);
                if (float.IsNaN(v) || float.IsInfinity(v)) continue;
                values.Add(v);
            }

            int bad = 0;
            string firstBad = null;
            for (int k = 0; k < values.Count; k++)
            {
                StringBuilder sb = new StringBuilder();
                StrictJson.AppendFloat(sb, values[k]);
                string text = sb.ToString();
                bool parsed = StrictJson.TryParse("{\"v\":" + text + "}", out JsonNode root, out string reason) &&
                              StrictJson.TryFloat(root.Members[0].Value, "v", out float back, out reason) &&
                              BitConverter.SingleToInt32Bits(back) == BitConverter.SingleToInt32Bits(values[k]);
                if (!parsed || text.IndexOf(',') >= 0) { bad++; if (firstBad == null) firstBad = text; }
            }
            Check(bad == 0, $"{label}: {values.Count} floats write and parse back to the same bits with no decimal comma (bad {bad}, first '{firstBad}')");

            StringBuilder nine = new StringBuilder();
            StrictJson.AppendFloat(nine, 0.09f);
            Check(nine.ToString() == "0.09", $"{label}: 0.09f is written as '0.09' (got '{nine}')");

            StringBuilder nan = new StringBuilder();
            StrictJson.AppendFloat(nan, float.NaN);
            Check(nan.ToString() == "null", label + ": NaN has no number spelling (written as null, refused by every reader)");
        }

        private void FloatChecks()
        {
            foreach (string c in new[] { "en-US", "ru-RU", "de-DE" })
            {
                WithCulture(c, () =>
                {
                    CultureInfo ci = Thread.CurrentThread.CurrentCulture;
                    Check(ci.Name == c, "thread culture is " + c + " (got " + ci.Name + ")");
                    if (c != "en-US") Check(0.09f.ToString() == "0,09", c + ": the naive ToString really would write a decimal comma here (" + 0.09f.ToString() + ")");
                    FloatRoundTrip(c);
                });
            }
            FloatRoundTrip("invariant-default");
        }

        // ------------------------------------------------------------------ culture round trips

        private void CultureRoundTrips()
        {
            foreach (string c in new[] { "en-US", "ru-RU", "de-DE" })
            {
                WithCulture(c, () =>
                {
                    LoadoutSnapshot snap = LoadoutSnapshot.Capture(bag, gear);
                    Check(snap.ToJson() == loadoutJson0, c + ": capture writes the identical loadout bytes");
                    Check(bag.ToContainerSnapshot().ToJson() == bagJson0 && gear.ToContainerSnapshot().ToJson() == gearJson0, c + ": container captures write the identical bytes");
                    Check(!loadoutJson0.Contains("0,09") && !loadoutJson0.Contains("0,3"), c + ": no decimal comma in the document");

                    Check(LoadoutSnapshot.TryFromJson(loadoutJson0, out LoadoutSnapshot parsed, out string reason), c + ": loadout json parses: " + reason);
                    Check(parsed != null && parsed.ToJson() == loadoutJson0 && parsed.ChecksumMatches(), c + ": parse then re-serialize is string-equal and the checksum matches");

                    Check(ContainerSnapshot.TryFromJson(bagJson0, out ContainerSnapshot bagParsed, out reason) && bagParsed.ToJson() == bagJson0, c + ": bag json round trips string-equal");
                    Check(ContainerSnapshot.TryFromJson(gearJson0, out ContainerSnapshot gearParsed, out reason) && gearParsed.ToJson() == gearJson0, c + ": equipment json round trips string-equal");

                    int life = bag.LifetimeCollected, rec = bag.RecoveredValue;
                    gear.Clear();
                    bag.Clear();
                    Check(Describe() != describe0, c + ": wiped target differs from the fixture");

                    bool ok = parsed != null && LoadoutSnapshot.TryRestore(parsed, bag, gear, null, out reason);
                    Check(ok, c + ": restore of the parsed loadout: " + reason);
                    Check(Describe() == describe0, c + ": restored bag and equipment are instance-equal (uid, rarity, exact float bits, cells) to the original");
                    Check(StatsNoPools() == statsNoPools0, c + ": stats re-applied exactly");
                    Check(bag.TotalUnits == totalUnits0 && bag.CarriedValue == carriedValue0, c + ": totals recomputed from the content");
                    Check(bag.LifetimeCollected == life && bag.RecoveredValue == rec, c + ": run statistics are not touched by a restore");
                    Check(LoadoutSnapshot.Capture(bag, gear).ToJson() == loadoutJson0, c + ": capturing again gives the same bytes");
                });
            }

            // Restore over a different populated state replaces it.
            bag.TryRemoveAt(0, 1);
            Check(Describe() != describe0, "changed state differs");
            Check(LoadoutSnapshot.TryRestore(loadout0.Clone(), bag, gear, null, out string r2) && Describe() == describe0, "restore over a changed state replaces it exactly: " + r2);
        }

        // ------------------------------------------------------------------ determinism

        private void DeterminismChecks()
        {
            Check(bag.ToContainerSnapshot().ToJson() == bag.ToContainerSnapshot().ToJson(), "same bag, two captures: identical bytes");
            Check(gear.ToContainerSnapshot().ToJson() == gear.ToContainerSnapshot().ToJson(), "same equipment, two captures: identical bytes");
            Check(bagSnap0.ComputeChecksum() == bagSnap0.checksum, "checksum is a pure function of the content");

            ContainerSnapshot reversed = BagClone();
            Array.Reverse(reversed.entries);
            Check(reversed.ToJson() != bagJson0, "an unsorted copy has different bytes");
            reversed.Seal();
            Check(reversed.ToJson() == bagJson0, "sealing puts any entry order back into the one canonical byte form");

            ContainerSnapshot unsortedValid = BagClone();
            Array.Reverse(unsortedValid.entries);
            unsortedValid.checksum = unsortedValid.ComputeChecksum();
            RefuseBag("entries out of canonical order (checksum fixed up)", unsortedValid, "canonical order");

            Inventory other = NewBag(cat, 10, 6, out Equipment otherGear);
            Check(other.TryRestore(bagSnap0, out string reason), "a second bag restores the same snapshot: " + reason);
            Check(other.ToContainerSnapshot().ToJson() == bagJson0, "a different container object holding the same content serializes to the identical bytes");
            Check(DescribeBagGear(other, otherGear).StartsWith(Describe().Substring(0, Describe().IndexOf(" gear{", StringComparison.Ordinal)), StringComparison.Ordinal), "the second bag is instance-equal to the first");
            Destroy(other.gameObject);
        }

        private Inventory NewBag(ItemCatalog catalog, int w, int h, out Equipment equipment)
        {
            GameObject go = new GameObject("ContainerSnapshotGate_Bag");
            Inventory b = go.AddComponent<Inventory>();
            equipment = go.AddComponent<Equipment>();
            b.ConfigureGrid(w, h);
            b.Clear();
            if (catalog != null) b.SetCatalog(catalog);
            return b;
        }

        // ------------------------------------------------------------------ bag rejections

        private void BagRejections()
        {
            string baseline = State();
            Check(bag.TryRestore(BagClone(), out string ok0), "the unmodified clone is accepted (control): " + ok0);
            Check(State() == baseline, "restoring the same content changes nothing observable");

            ContainerSnapshot s;
            ContainerEntry e;

            s = BagClone(); StackEntry(s, "ember_coin").item.itemId = "no_such_item"; s.Seal();
            RefuseBag("unknown item id", s, "unknown item");
            s = BagClone(); StackEntry(s, "ember_coin").item.itemId = "Ember_Coin"; s.Seal();
            RefuseBag("case-folded item id", s, "item");

            s = BagClone(); GearEntry(s, "footpad_boots", 1).item.affixes[0].id = "affix_nope"; s.Seal();
            RefuseBag("unknown affix", s, "unknown affix id");
            s = BagClone(); e = GearEntry(s, "footpad_boots", 1);
            e.item.affixes[0] = new AffixSnapshot("affix_melee_damage", "melee_damage", 2f); s.Seal();
            RefuseBag("foreign affix (weapon-only affix on boots)", s, "not allowed on");
            s = BagClone(); e = GearEntry(s, "ember_fang_blade", 2);
            e.item.affixes[0] = new AffixSnapshot("affix_armor", "armor", 3f); s.Seal();
            RefuseBag("foreign affix (armor on a weapon)", s, "not allowed on");
            s = BagClone(); GearEntry(s, "footpad_boots", 1).item.affixes[0].key = "armor"; s.Seal();
            RefuseBag("wrong id/key pair", s, "stat key");
            s = BagClone(); GearEntry(s, "footpad_boots", 1).item.affixes[0].key = "Move_Speed"; s.Seal();
            RefuseBag("non-canonical key text", s, "canonical");

            s = BagClone(); GearEntry(s, "footpad_boots", 1).item.affixes[0].value = float.NaN; s.Seal();
            RefuseBag("NaN affix value", s, "finite");
            s = BagClone(); GearEntry(s, "footpad_boots", 1).item.affixes[0].value = float.PositiveInfinity; s.Seal();
            RefuseBag("infinite affix value", s, "finite");
            s = BagClone(); GearEntry(s, "footpad_boots", 1).item.affixes[0].value = 0.5f; s.Seal();
            RefuseBag("move_speed above its band", s, "outside the band");
            s = BagClone(); GearEntry(s, "footpad_boots", 1).item.affixes[0].value = 0.01f; s.Seal();
            RefuseBag("move_speed below its band", s, "outside the band");
            s = BagClone(); GearEntry(s, "footpad_boots", 1).item.affixes[0].value = 0.095f; s.Seal();
            RefuseBag("off-step fractional value", s, "off-step");
            s = BagClone(); GearEntry(s, "emberthread_mantle", 2).item.affixes[0].value = 4.5f; s.Seal();
            RefuseBag("fractional attribute-style value (armor 4.5)", s, "off-step");
            s = BagClone(); GearEntry(s, "emberthread_mantle", 2).item.affixes[1].value = 99f; s.Seal();
            RefuseBag("max_mana far above its band", s, "outside the band");

            s = BagClone(); e = GearEntry(s, "emberthread_mantle", 2); e.item.affixes[1] = e.item.affixes[0].Clone(); s.Seal();
            RefuseBag("duplicate affix on one item", s, "duplicate affix");
            s = BagClone(); e = GearEntry(s, "emberthread_mantle", 2); e.item.rarity = 0; s.Seal();
            RefuseBag("Common rarity carrying 2 affixes", s, "expects 0");
            s = BagClone(); e = GearEntry(s, "emberthread_mantle", 2); e.item.affixes = new[] { e.item.affixes[0] }; s.Seal();
            RefuseBag("Rare rarity carrying 1 affix", s, "expects 2");
            s = BagClone(); e = GearEntry(s, "footpad_boots", 1); e.item.affixes = new[] { e.item.affixes[0], new AffixSnapshot("affix_max_health", "max_health", 7f) }; s.Seal();
            RefuseBag("Uncommon rarity carrying 2 affixes", s, "expects 1");
            s = BagClone(); e = GearEntry(s, "emberthread_mantle", 2);
            e.item.affixes = new[] { e.item.affixes[0], e.item.affixes[1], new AffixSnapshot("affix_max_health", "max_health", 7f) }; s.Seal();
            RefuseBag("Rare rarity carrying 3 affixes", s, "expects 2");
            s = BagClone(); GearEntry(s, "emberthread_mantle", 2).item.rarity = 7; s.Seal();
            RefuseBag("impossible rarity 7", s, "not a known rarity");
            s = BagClone(); GearEntry(s, "emberthread_mantle", 2).item.rarity = -1; s.Seal();
            RefuseBag("impossible rarity -1", s, "not a known rarity");

            s = BagClone(); GearEntry(s, "ember_fang_blade", 2).item.uid = GearEntry(s, "footpad_boots", 1).item.uid; s.Seal();
            RefuseBag("duplicate uid inside the snapshot", s, "duplicate uid");
            s = BagClone(); GearEntry(s, "ember_fang_blade", 2).item.uid = ""; s.Seal();
            RefuseBag("gear without a uid", s, "uid is missing");
            s = BagClone(); GearEntry(s, "ember_fang_blade", 2).item.uid = GearEntry(s, "ember_fang_blade", 2).item.uid.ToUpperInvariant(); s.Seal();
            RefuseBag("uppercase uid", s, "uid");
            s = BagClone(); GearEntry(s, "ember_fang_blade", 2).item.uid = new string('0', 32); s.Seal();
            RefuseBag("all-zero uid", s, "all-zero");
            s = BagClone(); GearEntry(s, "ember_fang_blade", 2).item.uid = "abc"; s.Seal();
            RefuseBag("truncated uid", s, "uid");

            s = BagClone(); e = StackEntry(s, "arrow_bundle"); e.x = StackEntry(s, "ember_coin").x; e.y = StackEntry(s, "ember_coin").y; s.Seal();
            RefuseBag("overlapping placements", s, "overlaps");
            s = BagClone(); StackEntry(s, "ashen_relic").x = 10; s.Seal();
            RefuseBag("placement at x == width", s, "outside");
            s = BagClone(); StackEntry(s, "ashen_relic").x = -1; s.Seal();
            RefuseBag("negative cell", s, "outside");
            s = BagClone(); StackEntry(s, "ashen_relic").y = 6; s.Seal();
            RefuseBag("placement at y == height", s, "outside");
            s = BagClone(); e = GearEntry(s, "ember_fang_blade", 2); e.x = 9; e.y = 5; s.Seal();
            RefuseBag("a multi-cell item hanging off the corner", s, "outside");
            s = BagClone(); e = StackEntry(s, "ember_coin"); ContainerEntry dup = e.Clone(); Append(s, dup); s.Seal();
            RefuseBag("duplicated stack entry", s, "overlaps");
            s = BagClone(); ContainerEntry dupGear = GearEntry(s, "emberthread_mantle", 2).Clone(); Append(s, dupGear); s.Seal();
            RefuseBag("duplicated gear entry", s, "duplicate uid");

            s = BagClone(); StackEntry(s, "ember_coin").item.quantity = 0; s.Seal();
            RefuseBag("stack quantity 0", s, "at least one");
            s = BagClone(); StackEntry(s, "ember_coin").item.quantity = -4; s.Seal();
            RefuseBag("negative stack quantity", s, "at least one");
            s = BagClone(); StackEntry(s, "ember_coin").item.quantity = 9; s.Seal();
            RefuseBag("stack quantity above the cap (coin cap 8)", s, "stack limit");
            s = BagClone(); StackEntry(s, "ashen_relic").item.quantity = 3; s.Seal();
            RefuseBag("relic quantity above its cap", s, "stack limit");
            s = BagClone(); GearEntry(s, "ember_fang_blade", 2).item.quantity = 2; s.Seal();
            RefuseBag("gear quantity 2", s, "exactly 1");
            s = BagClone(); GearEntry(s, "ember_fang_blade", 2).item.quantity = 0; s.Seal();
            RefuseBag("gear quantity 0", s, "exactly 1");

            s = BagClone(); StackEntry(s, "ember_coin").item.uid = "0123456789abcdef0123456789abcdef"; s.Seal();
            RefuseBag("carried stack with a uid", s, "no uid");
            s = BagClone(); StackEntry(s, "ember_coin").item.rarity = 1; s.Seal();
            RefuseBag("carried stack with a rarity", s, "no rarity");
            s = BagClone(); StackEntry(s, "ember_coin").item.affixes = new[] { new AffixSnapshot("affix_armor", "armor", 1f) }; s.Seal();
            RefuseBag("carried stack with affixes", s, "no affixes");
            s = BagClone(); StackEntry(s, "ember_coin").slot = "Weapon"; s.Seal();
            RefuseBag("bag entry naming an equipment slot", s, "sits in a cell");

            s = BagClone(); s.entries[0] = null; s.Seal();
            RefuseBag("null entry", s, "null");
            s = BagClone(); s.entries[0].item = null; s.Seal();
            RefuseBag("entry without an item", s, "no item");
            s = BagClone(); s.entries = null;
            RefuseBag("null entries array", s, "entries");

            s = BagClone(); s.checksum = ""; RefuseBag("missing checksum", s, "no checksum");
            s = BagClone(); s.checksum = null; RefuseBag("null checksum", s, "no checksum");
            s = BagClone(); s.checksum = "sha256:xyz"; RefuseBag("malformed checksum", s, "malformed");
            s = BagClone(); s.checksum = s.checksum.ToUpperInvariant(); RefuseBag("uppercase checksum", s, "malformed");
            s = BagClone(); s.checksum = "sha256:" + new string('0', 64); RefuseBag("well-formed but wrong checksum", s, "does not match");
            s = BagClone(); StackEntry(s, "ember_coin").item.quantity = 7; RefuseBag("edited value, stale checksum", s, "does not match");
            s = BagClone(); GearEntry(s, "footpad_boots", 1).item.affixes[0].value = 0.11f; RefuseBag("edited affix value, stale checksum", s, "does not match");
            s = BagClone();
            string uidA = GearEntry(s, "footpad_boots", 1).item.uid, uidB = GearEntry(s, "emberthread_mantle", 2).item.uid;
            GearEntry(s, "footpad_boots", 1).item.uid = uidB; GearEntry(s, "emberthread_mantle", 2).item.uid = uidA;
            RefuseBag("swapped uids, stale checksum", s, "does not match");
            s = BagClone(); Append(s, StackEntry(s, "ember_coin").Clone()); RefuseBag("duplicated entry, stale checksum", s, "does not match");

            s = BagClone(); s.height = 5; s.Seal(); RefuseBag("grid height differs from the bag's", s, "grid");
            s = BagClone(); s.width = 0; s.Seal(); RefuseBag("grid width 0", s, "grid");
            s = BagClone(); s.width = 65; s.Seal(); RefuseBag("grid wider than the cap", s, "grid");
            s = BagClone(); s.kind = "weird"; s.Seal(); RefuseBag("unknown kind", s, "kind");
            s = BagClone(); s.kind = "stash"; s.Seal(); RefuseBag("a stash container is not a bag", s, "expected a");
            s = BagClone(); s.containerId = "other_bag"; s.Seal(); RefuseBag("a different container id", s, "restores container");
            s = BagClone(); s.containerId = "Bag Id"; s.Seal(); RefuseBag("malformed container id", s, "container id");
            Check(gearSnap0 != null, "equipment fixture present");
            RefuseBag("equipment container handed to the bag", GearClone(), "expected a");

            ContainerSnapshot big = BagClone();
            ContainerEntry template = StackEntry(big, "ember_coin");
            big.entries = new ContainerEntry[ContainerSnapshot.MaxEntries + 1];
            for (int i = 0; i < big.entries.Length; i++) big.entries[i] = template.Clone();
            big.Seal();
            RefuseBag("oversized snapshot (entry cap " + ContainerSnapshot.MaxEntries + ")", big, "above the limit");

            Check(bag.TryRestore(null, out string nullReason) == false && Has(nullReason, "no container snapshot"), "null snapshot refused: " + nullReason);
            Check(State() == baseline, "after every bag refusal the bag and equipment equal the baseline");
        }

        private static void Append(ContainerSnapshot s, ContainerEntry e)
        {
            ContainerEntry[] grown = new ContainerEntry[s.entries.Length + 1];
            Array.Copy(s.entries, grown, s.entries.Length);
            grown[grown.Length - 1] = e;
            s.entries = grown;
        }

        // ------------------------------------------------------------------ equipment rejections

        private void GearRejections()
        {
            string baseline = State();
            ContainerSnapshot s;

            s = GearClone(); SlotEntry(s, "Feet").slot = "Head"; s.Seal();
            RefuseGear("boots in the Head slot", s, "belongs in slot");
            s = GearClone(); SlotEntry(s, "Chest").slot = "Weapon"; s.Seal();
            RefuseGear("a cuirass in the Weapon slot", s, "belongs in slot");
            s = GearClone(); { ContainerEntry second = SlotEntry(s, "Back"); second.slot = "Feet"; second.item = GearEntry(bagSnap0, "footpad_boots", 1).item.Clone(); } s.Seal();
            RefuseGear("two items in one slot", s, "holds two items");
            s = GearClone(); { ContainerEntry shield = SlotEntry(s, "OffHand"); shield.item = new ItemSnapshot { uid = "", itemId = "ember_coin", quantity = 1, rarity = 0, affixes = new AffixSnapshot[0] }; } s.Seal();
            RefuseGear("a stack in an equipment slot", s, "not wearable");
            foreach (string bad in new[] { "Gloves", "weapon", "None", "", "7", "Feet " })
            {
                s = GearClone(); SlotEntry(s, "Feet").slot = bad; s.Seal();
                RefuseGear("slot name '" + bad + "'", s, "not an equipment slot name");
            }
            s = GearClone(); SlotEntry(s, "Feet").x = 1; s.Seal();
            RefuseGear("an equipment entry with a cell", s, "no cell");
            s = GearClone(); s.width = 1; s.Seal();
            RefuseGear("an equipment container with a grid", s, "no grid");
            s = GearClone(); s.kind = "bag"; s.Seal();
            RefuseGear("kind bag handed to equipment", s, "expected a");
            RefuseGear("the bag container handed to equipment", BagClone(), "expected a");
            s = GearClone(); s.containerId = "equipment_two"; s.Seal();
            RefuseGear("a different equipment container id", s, "restores container");
            s = GearClone(); SlotEntry(s, "Weapon").item.affixes[0].value = float.NaN; s.Seal();
            RefuseGear("NaN rolled value on worn gear", s, "finite");
            s = GearClone(); SlotEntry(s, "Feet").item.affixes[0].value = 0.095f; s.Seal();
            RefuseGear("off-step rolled value on worn gear", s, "off-step");
            s = GearClone(); SlotEntry(s, "Feet").item.itemId = "no_such_item"; s.Seal();
            RefuseGear("unknown item on a worn slot", s, "unknown item");
            s = GearClone(); SlotEntry(s, "Feet").item.uid = SlotEntry(s, "Back").item.uid; s.Seal();
            RefuseGear("the same uid worn in two slots", s, "duplicate uid");
            s = GearClone(); SlotEntry(s, "Back").item.quantity = 0; RefuseGear("edited quantity, stale checksum", s, "does not match");
            s = GearClone(); Array.Reverse(s.entries); s.checksum = s.ComputeChecksum(); RefuseGear("equipment entries out of canonical order", s, "canonical order");
            s = GearClone(); s.schemaVersion = 5; s.Seal(); RefuseGear("equipment from a newer schema", s, "newer");

            Check(State() == baseline, "after every equipment refusal the bag and equipment equal the baseline");

            // empty equipment is a legal container
            ContainerSnapshot empty = new ContainerSnapshot { kind = ContainerKinds.Equipment, containerId = Equipment.SnapshotContainerId, width = 0, height = 0, entries = new ContainerEntry[0] };
            empty.Seal();
            Check(ContainerSnapshotValidator.TryValidate(empty, cat, null, ContainerKinds.Equipment, out ValidatedContainer vc, out string reason) && vc.Entries.Count == 0, "an empty equipment container is legal: " + reason);
        }

        // ------------------------------------------------------------------ json rejections

        private void JsonRejections()
        {
            string baseline = State();
            string j = bagJson0;

            RefuseJson("empty text", "", "empty");
            RefuseJson("whitespace text", "   \n ", "empty");
            RefuseJson("null text", "null", "json object");
            RefuseJson("array root", "[]", "json object");
            RefuseJson("truncated document", j.Substring(0, j.Length / 2), "malformed");
            RefuseJson("trailing garbage", j + "x", "unexpected content");
            RefuseJson("trailing second document", j + j, "unexpected content");
            RefuseJson("empty object", "{}", "schemaVersion");
            RefuseJson("not json", "hello", "malformed");
            RefuseJson("single quotes", j.Replace('"', '\''), "malformed");
            RefuseJson("trailing comma", Rep(j, "]", ",]", "trailing comma"), "malformed");
            RefuseJson("NaN literal as a value", ReplaceFirstValueToken(j, "NaN", "NaN literal"), "malformed");
            RefuseJson("Infinity literal as a value", ReplaceFirstValueToken(j, "Infinity", "Infinity literal"), "malformed");
            RefuseJson("overflowing float value", ReplaceFirstValueToken(j, "1e999", "overflow"), "finite");
            RefuseJson("null where a float belongs", ReplaceFirstValueToken(j, "null", "null value"), "must be a number");
            RefuseJson("float value as a string", ReplaceFirstValueToken(j, "\"0.09\"", "string value"), "must be a number");
            RefuseJson("leading-zero number", Rep(j, "\"width\":10", "\"width\":010", "leading zero"), "malformed");
            RefuseJson("unknown field at the root", Rep(j, "\"kind\":\"bag\",", "\"kind\":\"bag\",\"extra\":1,", "root extra"), "unknown field 'extra'");
            RefuseJson("unknown field in an entry", Rep(j, "{\"slot\":\"\",", "{\"extra\":0,\"slot\":\"\",", "entry extra"), "unknown field 'extra'");
            RefuseJson("unknown field in an item", Rep(j, "\"item\":{", "\"item\":{\"extra\":0,", "item extra"), "unknown field 'extra'");
            RefuseJson("unknown field in an affix", Rep(j, "{\"id\":\"affix_", "{\"extra\":0,\"id\":\"affix_", "affix extra"), "unknown field 'extra'");
            RefuseJson("repeated field", Rep(j, "\"width\":10,", "\"width\":10,\"width\":10,", "dup width"), "repeats the field");
            RefuseJson("missing field", Rep(j, "\"containerId\":\"bag\",", "", "no id"), "missing the field 'containerId'");
            RefuseJson("missing rarity is not silently Common", Rep(j, ",\"rarity\":0", "", "no rarity"), "missing the field 'rarity'");
            RefuseJson("missing affixes", Rep(j, ",\"affixes\":[]", "", "no affixes"), "missing the field 'affixes'");
            RefuseJson("quantity as a string", Rep(j, "\"quantity\":1,", "\"quantity\":\"1\",", "qty string"), "must be a number");
            RefuseJson("fractional quantity", Rep(j, "\"quantity\":1,", "\"quantity\":1.5,", "qty fraction"), "whole number");
            RefuseJson("exponent quantity", Rep(j, "\"quantity\":1,", "\"quantity\":1e0,", "qty exponent"), "whole number");
            RefuseJson("integer beyond int range", Rep(j, "\"quantity\":1,", "\"quantity\":99999999999,", "qty huge"), "whole number");
            RefuseJson("entries is not an array", Rep(j, "\"entries\":[", "\"entries\":{", "entries obj").Replace("}]", "}}"), "malformed");
            RefuseJson("deeply nested document", "{\"schemaVersion\":" + ContainerSnapshot.CurrentSchemaVersion + ",\"a\":" + new string('[', 40) + new string(']', 40) + "}", "nesting");
            RefuseJson("document above the size cap", "{\"a\":\"" + new string('x', StrictJson.MaxDocumentLength + 10) + "\"}", "limit");
            RefuseJson("raw control character in a string", Rep(j, "\"kind\":\"bag\"", "\"kind\":\"ba\ng\"", "control char"), "control character");
            RefuseJson("unknown escape", Rep(j, "\"kind\":\"bag\"", "\"kind\":\"b\\qg\"", "bad escape"), "escape");

            StringBuilder many = new StringBuilder("{\"schemaVersion\":" + ContainerSnapshot.CurrentSchemaVersion + ",\"kind\":\"bag\",\"containerId\":\"bag\",\"width\":10,\"height\":4,\"entries\":[");
            for (int i = 0; i <= ContainerSnapshot.MaxEntries; i++) { if (i > 0) many.Append(','); many.Append("null"); }
            many.Append("],\"checksum\":\"\"}");
            RefuseJson("more entries than the cap in the document", many.ToString(), "above the limit");

            // Canonical-only input: documents that parse to the SAME model (so the same checksum) but are not the exact
            // bytes ToJson writes are refused, so an accepted document has exactly one byte form.
            RefuseJson("a legal \\u escape", Rep(j, "\"kind\":\"bag\"", "\"kind\":\"\\u0062ag\"", "unicode escape"), "canonical");
            RefuseJson("insignificant whitespace", "  \n" + j.Replace(",\"", ",\n \"").Replace("{", "{ ") + " \t", "canonical");
            RefuseJson("leading space only", " " + j, "canonical");
            RefuseJson("trailing newline only", j + "\n", "canonical");
            RefuseJson("members in another order", Rep(j, "\"width\":10,\"height\":6", "\"height\":6,\"width\":10", "member order"), "canonical");
            RefuseJson("exponent spelling of a float", Rep(j, "\"value\":0.12", "\"value\":1.2e-1", "exponent spelling"), "canonical");
            RefuseJson("trailing zero spelling of a float", Rep(j, "\"value\":0.12", "\"value\":0.120", "trailing zero"), "canonical");
            RefuseJson("a longer spelling of the same float bits", Rep(j, "\"value\":0.12", "\"value\":0.119999997", "G9 spelling"), "canonical");
            RefuseJson("negative zero integer", Rep(j, "\"x\":0,", "\"x\":-0,", "-0 int"), "canonical");
            RefuseJson("a plus sign on a number", Rep(j, "\"width\":10", "\"width\":+10", "plus sign"), "malformed");
            RefuseJson("byte order mark", "\uFEFF" + j, "malformed");
            RefuseJson("integer above int.MaxValue", Rep(j, "\"quantity\":1,", "\"quantity\":2147483648,", "int max + 1"), "whole number");
            RefuseJson("integer below int.MinValue", Rep(j, "\"quantity\":1,", "\"quantity\":-2147483649,", "int min - 1"), "whole number");
            RefuseLoadoutJson("loadout json with whitespace", loadoutJson0.Replace("\"bag\":", "\"bag\": "), "canonical");
            RefuseLoadoutJson("loadout json repeating a member", Rep(loadoutJson0, "\"bag\":{", "\"equipment\":{\"schemaVersion\":" + ContainerSnapshot.CurrentSchemaVersion + ",\"kind\":\"equipment\",\"containerId\":\"equipment\",\"width\":0,\"height\":0,\"entries\":[],\"checksum\":\"x\"},\"bag\":{", "dup equipment member"), "repeats");
            Check(ContainerSnapshot.TryFromJson(j, out ContainerSnapshot canonicalOk, out string reason) && canonicalOk.ToJson() == j, "the exact canonical text is accepted: " + reason);

            Check(State() == baseline, "no json refusal changed the target");
        }

        // ------------------------------------------------------------------ schema versions

        private void VersionChecks()
        {
            string baseline = State();
            int cur = ContainerSnapshot.CurrentSchemaVersion;
            // D3a bumped the schema to 2 (rarity ladder), D3b to 3 (slots), D3c to 4 (weapon sets, belt); 1, 2 and 3 stay readable through the production migrations.
            Check(cur == 4 && ContainerSnapshot.MinSupportedSchemaVersion == 1, "schema version is 4 since D3c, 1, 2 and 3 are still migrated");
            string j = bagJson0;
            string sv = "\"schemaVersion\":" + cur;
            foreach (string v in new[] { "0", "5", "99", "-1", "2147483647" })
            {
                string doc = Rep(j, sv, "\"schemaVersion\":" + v, "version " + v);
                int num = int.Parse(v, CultureInfo.InvariantCulture);
                RefuseJson("schemaVersion " + v, doc, num > cur ? "newer" : "older");
                string ldoc = Rep(loadoutJson0, "{" + sv + ",\"bag\"", "{\"schemaVersion\":" + v + ",\"bag\"", "loadout version " + v);
                RefuseLoadoutJson("loadout schemaVersion " + v, ldoc, num > cur ? "newer" : "older");
            }
            RefuseJson("schemaVersion 2.0", Rep(j, sv, "\"schemaVersion\":4.0", "version float"), "whole number");
            RefuseJson("schemaVersion as a string", Rep(j, sv, "\"schemaVersion\":\"4\"", "version string"), "must be a number");
            RefuseJson("missing schemaVersion", Rep(j, sv + ",", "", "no version"), "no schemaVersion");

            ContainerSnapshot s;
            foreach (int v in new[] { 0, 1, 2, 3, 5, 7, -3 })
            {
                s = BagClone(); s.schemaVersion = v; s.Seal();
                RefuseBag("in-memory schemaVersion " + v, s, v > cur ? "newer" : "older");
            }
            LoadoutSnapshot l = loadout0.Clone(); l.schemaVersion = 5; l.Seal();
            RefuseLoadout("loadout schemaVersion 5 in memory", l, "newer");
            l = loadout0.Clone(); l.schemaVersion = 2; l.Seal();
            RefuseLoadout("loadout schemaVersion 2 in memory (an old version must be migrated while parsing)", l, "older");
            l = loadout0.Clone(); l.schemaVersion = 1; l.Seal();
            RefuseLoadout("loadout schemaVersion 1 in memory (an old version must be migrated while parsing)", l, "older");
            l = loadout0.Clone(); l.schemaVersion = 0; l.Seal();
            RefuseLoadout("loadout schemaVersion 0 in memory", l, "older");

            Check(ContainerSnapshot.TryFromJson(j, out ContainerSnapshot ok, out string reason) && ok.schemaVersion == cur, "the supported version parses: " + reason);
            Check(State() == baseline, "no version refusal changed the target");
        }

        // ------------------------------------------------------------------ D3a: the production 1 -> 2 migration

        /// <summary>A genuine version 1 document is the current model written at schemaVersion 1 and sealed: versions 1, 2 and 3 share one layout and one checksum rule, only the version differs.</summary>
        private ContainerSnapshot AsVersion1(ContainerSnapshot snapshot)
        {
            ContainerSnapshot old = snapshot.Clone();
            old.schemaVersion = 1;
            return old.Seal();
        }

        private void ProductionMigrationChecks()
        {
            string baseline = State();
            ContainerSchema.ClearMigrationsForTest(); // exactly the production chain
            string v1Bag = AsVersion1(bagSnap0).ToJson();
            Check(v1Bag.StartsWith("{\"schemaVersion\":1,", StringComparison.Ordinal) && v1Bag != bagJson0, "a version 1 bag document is built");

            Check(ContainerSnapshot.TryFromJson(v1Bag, out ContainerSnapshot migrated, out string reason), "an intact version 1 bag migrates: " + reason);
            Check(migrated != null && migrated.schemaVersion == 4 && migrated.ChecksumMatches() && migrated.ToJson() == bagJson0,
                "the migrated bag (1 to 2 to 3 to 4) is a normal version 4 snapshot with the same content (same bytes as writing it at version 2)");
            Inventory twin = NewBag(cat, 10, 6, out Equipment twinGear);
            Check(migrated != null && twin.TryRestore(migrated, out reason) && twin.ToContainerSnapshot().ToJson() == bagJson0, "and it restores with every uid, rarity and roll as saved: " + reason);
            Destroy(twin.gameObject);

            RefuseJson("a version 1 bag edited after it was written", v1Bag.Replace("\"quantity\":8", "\"quantity\":7"), "integrity");
            RefuseJson("a version 1 bag with a wrong checksum", Rep(v1Bag, "\"checksum\":\"sha256:", "\"checksum\":\"sha256:0", "bad v1 checksum"), "integrity");

            // Version 1 could only write 0, 1 or 2. A version 1 document naming a later tier was not written by version 1.
            ContainerSnapshot lateTier = BagClone();
            GearEntry(lateTier, "emberthread_mantle", 2).item.rarity = 3;
            RefuseJson("a version 1 bag that names Epic", AsVersion1(lateTier).ToJson(), "did not exist in schema version 1");
            ContainerSnapshot poorTier = BagClone();
            GearEntry(poorTier, "emberthread_mantle", 2).item.rarity = 6;
            RefuseJson("a version 1 bag that names Poor", AsVersion1(poorTier).ToJson(), "did not exist in schema version 1");

            // The same document written at the current version is structurally readable; the validator then judges the tier.
            ContainerSnapshot epicWrongCount = BagClone();
            GearEntry(epicWrongCount, "emberthread_mantle", 2).item.rarity = 3;
            epicWrongCount.Seal();
            RefuseBag("Epic carrying 2 affixes (needs 3)", epicWrongCount, "expects 3");
            ContainerSnapshot unknownTier = BagClone();
            GearEntry(unknownTier, "emberthread_mantle", 2).item.rarity = 7;
            unknownTier.Seal();
            RefuseBag("an unknown tier 7", unknownTier, "not a known rarity");

            // loadout wrapper at version 1 (both containers at version 1)
            LoadoutSnapshot oldLoadout = loadout0.Clone();
            oldLoadout.schemaVersion = 1;
            oldLoadout.bag.schemaVersion = 1;
            oldLoadout.equipment.schemaVersion = 1;
            SlotEntry(oldLoadout.equipment, "Feet").slot = "Legs"; // what version 1 really wrote: the boots were worn in Legs
            oldLoadout.Seal();
            string v1Loadout = oldLoadout.ToJson();
            Check(LoadoutSnapshot.TryFromJson(v1Loadout, out LoadoutSnapshot lo, out reason) && lo.schemaVersion == 4 && lo.ToJson() == loadoutJson0,
                "an intact version 1 loadout (boots in Legs) migrates to the version 4 bytes (boots in Feet, weaponSet 0, empty belt): " + reason);
            RefuseLoadoutJson("a version 1 loadout edited after it was written", v1Loadout.Replace("\"quantity\":8", "\"quantity\":7"), "integrity");

            // a version 2 wrapper around a version 1 container is refused
            JsonNode mixed = ParseTree(loadoutJson0);
            JsonNode inner = ContainerSchema.Member(mixed, "bag");
            JsonNode oldBag = ParseTree(v1Bag);
            ContainerSchema.Member(inner, "schemaVersion").Text = "1";
            ContainerSchema.Member(inner, "checksum").Text = ContainerSchema.Member(oldBag, "checksum").Text;
            RefuseLoadoutJson("a current loadout wrapper around a version 1 bag", StrictJson.Serialize(mixed, null), "share one version");

            // ---- D3b: the production 2 -> 3 migration (boots Legs -> Feet)
            ContainerSnapshot v2Gear = gearSnap0.Clone();
            v2Gear.schemaVersion = 2;
            SlotEntry(v2Gear, "Feet").slot = "Legs";
            v2Gear.Seal();
            string v2GearJson = v2Gear.ToJson();
            Check(v2GearJson.StartsWith("{\"schemaVersion\":2,", StringComparison.Ordinal) && v2GearJson.Contains("\"slot\":\"Legs\"") && !v2GearJson.Contains("\"Feet\""), "a version 2 equipment document with the boots in Legs is built");
            Check(ContainerSnapshot.TryFromJson(v2GearJson, out ContainerSnapshot gear3, out reason) && gear3.schemaVersion == 4 && gear3.ChecksumMatches() && gear3.ToJson() == gearJson0,
                "the version 2 equipment migrates: Legs/footpad_boots becomes Feet, entries re-ordered, uid and rolls untouched, bytes equal a version 4 write: " + reason);
            Check(gear3 != null && SlotEntry(gear3, "Feet").item.itemId == "footpad_boots" && SlotEntry(gear3, "Feet").item.uid == SlotEntry(gearSnap0, "Feet").item.uid, "the migrated Feet entry is the same boots with the same uid");
            RefuseJson("a version 2 equipment edited after it was written", v2GearJson.Replace("\"quantity\":1", "\"quantity\":2"), "integrity");

            ContainerSnapshot v2Bag = bagSnap0.Clone();
            v2Bag.schemaVersion = 2;
            v2Bag.Seal();
            Check(ContainerSnapshot.TryFromJson(v2Bag.ToJson(), out ContainerSnapshot bag3, out reason) && bag3.ToJson() == bagJson0, "a version 2 bag is only re-versioned: " + reason);

            // Version 2 could not have written the new slots: such a document was not written by version 2.
            foreach (string newSlot in new[] { "Hands", "Feet", "Necklace", "RingLeft", "RingRight" })
            {
                ContainerSnapshot forged = v2Gear.Clone();
                SlotEntry(forged, "Back").slot = newSlot;
                forged.Seal();
                RefuseJson("a version 2 equipment naming the slot " + newSlot, forged.ToJson(), "did not exist in schema version 2");
            }

            LoadoutSnapshot v2Loadout = loadout0.Clone();
            v2Loadout.schemaVersion = 2;
            v2Loadout.bag.schemaVersion = 2;
            v2Loadout.equipment.schemaVersion = 2;
            SlotEntry(v2Loadout.equipment, "Feet").slot = "Legs";
            v2Loadout.Seal();
            Check(LoadoutSnapshot.TryFromJson(v2Loadout.ToJson(), out LoadoutSnapshot lo3, out reason) && lo3.schemaVersion == 4 && lo3.ToJson() == loadoutJson0,
                "an intact version 2 loadout migrates to the version 4 bytes: " + reason);
            RefuseLoadoutJson("a version 2 loadout edited after it was written", v2Loadout.ToJson().Replace("\"quantity\":8", "\"quantity\":7"), "integrity");

            Check(State() == baseline, "production migration checks left the fixture state intact");
        }

        // ------------------------------------------------------------------ cross-container uid uniqueness

        private void RegistryChecks()
        {
            string baseline = State();
            ItemUidRegistry reg = new ItemUidRegistry();
            Check(reg.TryRegisterSnapshot(bagSnap0, cat, out string reason), "the bag snapshot registers: " + reason);
            Check(reg.Count == 3, "bag registered its 3 gear uids, not its stacks (count " + reg.Count + ")");
            Check(reg.TryRegisterSnapshot(gearSnap0, cat, out reason), "the disjoint equipment snapshot registers too: " + reason);
            Check(reg.Count == 8, "8 uids across both containers (count " + reg.Count + ")");
            Check(reg.TryRegisterSnapshot(bagSnap0, cat, out reason) && reg.Count == 8, "registering the same container again is idempotent: " + reason);

            Guid bootsBUid = Guid.Parse(GearEntry(bagSnap0, "footpad_boots", 1).item.uid);
            Check(reg.TryGetOwner(bootsBUid, out string owner) && owner == "bag", "the boots uid is owned by 'bag'");

            ContainerSnapshot gearDup = GearClone();
            SlotEntry(gearDup, "Feet").item = GearEntry(bagSnap0, "footpad_boots", 1).item.Clone();
            gearDup.Seal();
            int before = reg.Count;
            Check(!reg.TryRegisterSnapshot(gearDup, cat, out reason) && Has(reason, "already held by container 'bag'"), "an equipment snapshot reusing a bag uid is refused by the registry: " + reason);
            Check(reg.Count == before && reg.TryGetOwner(bootsBUid, out owner) && owner == "bag", "the refusal left the ledger untouched");
            Check(!ContainerSnapshotValidator.TryValidate(gearDup, cat, reg, null, out ValidatedContainer vc, out reason) && Has(reason, "already held"), "TryValidate with the ledger refuses the same cross-container duplicate: " + reason);
            Check(ContainerSnapshotValidator.TryValidate(gearDup, cat, null, null, out vc, out reason), "the same snapshot is internally consistent without a ledger: " + reason);

            Check(reg.CanHold("bag", new[] { bootsBUid }, out reason), "the owner may keep its own uid");
            Check(!reg.CanHold("stash_a", new[] { bootsBUid }, out reason) && Has(reason, "already held"), "another container may not claim it: " + reason);
            Check(!reg.CanHold("stash_a", new[] { Guid.NewGuid(), Guid.Empty }, out reason), "the all-zero uid cannot be registered");
            Guid fresh = Guid.NewGuid();
            Check(!reg.CanHold("stash_a", new[] { fresh, fresh }, out reason) && Has(reason, "twice"), "a repeated uid in one list is refused: " + reason);
            int count = reg.Count;
            Check(!reg.TryReplaceOwner("stash_a", new[] { fresh, bootsBUid }, out reason) && reg.Count == count && !reg.Contains(fresh), "TryReplaceOwner is atomic: nothing registered on refusal");
            Check(reg.TryReplaceOwner("stash_a", new[] { fresh }, out reason) && reg.Count == count + 1 && reg.Contains(fresh), "TryReplaceOwner registers a clean set");
            reg.ReleaseOwner("stash_a");
            Check(!reg.Contains(fresh) && reg.Count == count, "ReleaseOwner frees exactly that owner's uids");
            reg.ReleaseOwner("bag");
            Check(reg.TryRegisterSnapshot(gearDup, cat, out reason), "once the bag released its uids the same equipment snapshot registers: " + reason);

            // ledger used as the 'held elsewhere' set of a real restore
            ItemUidRegistry stash = new ItemUidRegistry();
            Check(stash.TryReplaceOwner("stash_main", new[] { bootsBUid }, out reason), "a stash holding the boots uid");
            RefuseBag("a bag restore reusing a uid the stash holds", bagSnap0, "already held by container 'stash_main'", stash);
            RefuseLoadout("a loadout restore reusing a uid the stash holds", loadout0.Clone(), "already held by container 'stash_main'", stash);
            Check(bag.TryRestore(bagSnap0, new ItemUidRegistry(), out reason), "an empty ledger is no obstacle: " + reason);

            // uid worn by the player cannot also be restored into the bag, and the other way round
            ContainerSnapshot bagDup = BagClone();
            GearEntry(bagDup, "footpad_boots", 1).item = SlotEntry(gearSnap0, "Feet").item.Clone();
            bagDup.Seal();
            RefuseBag("a bag snapshot holding a uid the player is wearing", bagDup, "already worn");
            RefuseGear("an equipment snapshot holding a uid the bag carries", gearDup, "already carried");

            // as a loadout, the same duplicate is refused across sections
            LoadoutSnapshot dupLoadout = new LoadoutSnapshot { bag = bagDup.Clone(), equipment = gearSnap0.Clone() };
            dupLoadout.Seal();
            RefuseLoadout("one uid in both loadout sections", dupLoadout, "both the bag and the equipment");

            Check(State() == baseline, "ledger checks changed no live state");
        }

        // ------------------------------------------------------------------ combined loadout

        private void LoadoutAtomicity()
        {
            // make the live target differ from the snapshot first, so "unchanged" means something
            int draught = bag.IndexOfItem(Def("blood_draught"));
            Check(draught >= 0 && bag.TryRemoveAt(draught, 3) == 3, "a stack is removed so the target differs from the saved loadout");
            string changed = State();
            Check(Describe() != describe0, "target differs from the snapshot");

            LoadoutSnapshot l;

            l = loadout0.Clone(); SlotEntry(l.equipment, "Chest").slot = "Weapon"; l.Seal();
            RefuseLoadout("valid bag + invalid equipment (cuirass in the Weapon slot)", l, "equipment section refused");
            l = loadout0.Clone(); StackEntry(l.bag, "ember_coin").item.itemId = "no_such_item"; l.Seal();
            RefuseLoadout("invalid bag (unknown item) + valid equipment", l, "bag section refused");
            l = loadout0.Clone(); SlotEntry(l.equipment, "Feet").item.affixes[0].value = float.NaN; l.Seal();
            RefuseLoadout("valid bag + equipment with a NaN roll", l, "equipment section refused");
            l = loadout0.Clone(); GearEntry(l.bag, "footpad_boots", 1).item = SlotEntry(l.equipment, "Feet").item.Clone(); l.Seal();
            RefuseLoadout("both sections valid on their own but sharing a uid", l, "both the bag and the equipment");
            l = loadout0.Clone(); l.bag.entries[0].item.quantity += 1; l.bag.checksum = l.bag.ComputeChecksum();
            RefuseLoadout("bag content edited and its own checksum fixed up, loadout checksum stale", l, "loadout checksum");
            l = loadout0.Clone(); l.equipment.entries[0].item.itemId = "boiled_cuirass";
            RefuseLoadout("equipment edited without any reseal", l, "loadout checksum");
            l = loadout0.Clone(); l.checksum = ""; RefuseLoadout("loadout without a checksum", l, "no checksum");
            l = loadout0.Clone(); l.checksum = "sha256:" + new string('f', 64); RefuseLoadout("loadout with a wrong checksum", l, "loadout checksum");
            l = loadout0.Clone(); l.checksum = "nope"; RefuseLoadout("loadout with a malformed checksum", l, "malformed");
            l = loadout0.Clone(); l.bag = null; RefuseLoadout("loadout without a bag section", l, "missing");
            l = loadout0.Clone(); l.equipment = null; RefuseLoadout("loadout without an equipment section", l, "missing");
            l = loadout0.Clone(); l.bag.height = 7; l.Seal(); RefuseLoadout("loadout whose bag grid differs", l, "grid");
            l = loadout0.Clone(); l.bag = loadout0.equipment.Clone(); l.equipment = loadout0.bag.Clone(); l.Seal();
            RefuseLoadout("sections swapped", l, "section refused");
            Check(!LoadoutSnapshot.TryRestore(null, bag, gear, null, out string reason) && Has(reason, "no loadout"), "null loadout refused: " + reason);

            RefuseLoadoutJson("loadout json with an unknown field", Rep(loadoutJson0, "\"bag\":{", "\"extra\":1,\"bag\":{", "loadout extra"), "unknown field");
            RefuseLoadoutJson("loadout json with an unknown field inside the bag", Rep(loadoutJson0, "\"kind\":\"bag\",", "\"kind\":\"bag\",\"extra\":1,", "bag extra"), "unknown field");
            RefuseLoadoutJson("loadout json with a tampered value", Rep(loadoutJson0, "\"value\":0.09", "\"value\":0.08", "tampered"), "checksum");
            RefuseLoadoutJson("loadout json truncated", loadoutJson0.Substring(0, loadoutJson0.Length - 40), "malformed");

            Check(State() == changed, "after every loadout refusal neither container changed");

            // a valid loadout restores both
            Check(LoadoutSnapshot.TryRestore(loadout0.Clone(), bag, gear, null, out reason), "the valid loadout restores both containers: " + reason);
            Check(Describe() == describe0 && StatsNoPools() == statsNoPools0, "bag and equipment are exactly the saved ones");
            // a valid loadout through json
            bag.TryRemoveAt(0, 1);
            Check(LoadoutSnapshot.TryRestoreJson(loadoutJson0, bag, gear, null, out reason) && Describe() == describe0, "restore from the json text: " + reason);
            Check(LoadoutSnapshot.TryRestore(loadout0.Clone(), bag, gear, new ItemUidRegistry(), out reason), "restoring over its own uids with an empty ledger works: " + reason);
        }

        // ------------------------------------------------------------------ restore, then equip/unequip

        private void RestoreThenEquipRoundTrips()
        {
            string reason;

            // no free healing, no accumulation
            gear.Clear();
            bag.Clear();
            string pristineNow = StatsNoPools();
            Check(pristineNow == pristineStats, "wiped gear returns the pristine stats and hands");
            rig.ApplyDamage(new DamageInfo(40f, rig.transform.position, Vector3.forward, rig.transform.position + Vector3.forward, 0, EntityKind.Enemy, DamageType.Physical, false, false, 0f));
            float hp = rig.CurrentHealth;
            float mana = rig.Mana.CurrentMana;
            Check(hp < rig.MaxHealth, "the rig is wounded before the restore (" + hp + "/" + rig.MaxHealth + ")");

            for (int round = 0; round < 6; round++)
            {
                Check(LoadoutSnapshot.TryRestore(loadout0.Clone(), bag, gear, null, out reason), $"restore round {round}: {reason}");
                Check(StatsNoPools() == statsNoPools0, $"restore round {round}: stats equal the single-equip stats (no accumulation)");
                Check(rig.CurrentHealth == hp && Mathf.Approximately(rig.Mana.CurrentMana, mana), $"restore round {round}: no free healing or mana (hp {rig.CurrentHealth}, was {hp})");
            }
            Check(rig.MainHandWeapon == Def("ember_fang_blade").grantedWeapon && rig.OffhandWeapon == Def("emberbulwark_shield").grantedWeapon, "restored weapon and shield are in the hands");
            Check(rig.GetArmorRating() > 0f, "restored gear counts for armor (" + rig.GetArmorRating() + ")");

            // restoring an empty equipment hands the class weapons back and removes the stats
            LoadoutSnapshot bare = new LoadoutSnapshot { bag = bag.ToContainerSnapshot(), equipment = new ContainerSnapshot { kind = ContainerKinds.Equipment, containerId = Equipment.SnapshotContainerId, entries = new ContainerEntry[0] } };
            bare.Seal();
            Check(LoadoutSnapshot.TryRestore(bare, bag, gear, null, out reason), "an empty-equipment loadout restores: " + reason);
            Check(gear.FilledCount == 0 && StatsNoPools() == pristineStats, "everything worn is removed, the class hands and pristine stats are back");
            Check(rig.MainHandWeapon == pristineMain && rig.OffhandWeapon == pristineOff, "class weapons returned to the hands");
            Check(rig.CurrentHealth <= hp, "unequipping through a restore refills nothing");
            Check(LoadoutSnapshot.TryRestore(loadout0.Clone(), bag, gear, null, out reason) && Describe() == describe0, "and the saved loadout restores again exactly: " + reason);

            // equip / unequip keep uid and rolls. The fixture bag is too fragmented to take five pieces of gear back,
            // so restore the saved equipment over an empty bag (also a legal loadout) and unequip from there.
            ContainerSnapshot emptyBag = new ContainerSnapshot { kind = ContainerKinds.Bag, containerId = Inventory.SnapshotContainerId, width = bag.GridWidth, height = bag.GridHeight, entries = new ContainerEntry[0] };
            LoadoutSnapshot roomy = new LoadoutSnapshot { bag = emptyBag, equipment = gearSnap0.Clone() };
            roomy.Seal();
            Check(LoadoutSnapshot.TryRestore(roomy, bag, gear, null, out reason) && bag.UsedSlots == 0 && gear.FilledCount == 5, "the saved equipment restores over an empty bag: " + reason);
            Dictionary<EquipmentSlot, string> wornBefore = new Dictionary<EquipmentSlot, string>();
            for (int s = 1; s < Equipment.SlotCount; s++)
            {
                ItemInstance i = gear.EquippedInstance((EquipmentSlot)s);
                if (i != null) wornBefore[(EquipmentSlot)s] = DescribeInstance(i);
            }
            Check(wornBefore.Count == 5, "five worn instances after the restore");

            foreach (KeyValuePair<EquipmentSlot, string> pair in new List<KeyValuePair<EquipmentSlot, string>>(wornBefore))
            {
                Guid uid = gear.EquippedInstance(pair.Key).Uid;
                Check(gear.TryUnequip(pair.Key, out reason), $"unequip {pair.Key}: {reason}");
                int idx = bag.IndexOfInstance(uid);
                Check(idx >= 0 && DescribeInstance(bag.InstanceAt(idx)) == pair.Value, $"{pair.Key}: the bag now holds the same uid with the same rarity and exact rolls");
                Check(!gear.HoldsUid(uid), $"{pair.Key}: the uid is not worn any more");
            }
            Check(gear.FilledCount == 0 && StatsNoPools() == pristineStats, "all unequipped: pristine stats and hands, nothing accumulated");

            ContainerSnapshot bagNow = bag.ToContainerSnapshot();
            Check(ContainerSnapshotValidator.TryValidate(bagNow, cat, null, ContainerKinds.Bag, out ValidatedContainer vcNow, out reason), "the bag after unequipping is itself a legal snapshot: " + reason);

            foreach (KeyValuePair<EquipmentSlot, string> pair in wornBefore)
            {
                string uidText = pair.Value.Substring(0, pair.Value.IndexOf(':'));
                int idx = bag.IndexOfInstance(Guid.ParseExact(uidText, "N"));
                Check(idx >= 0 && gear.TryEquipFromInventory(idx, out reason), $"re-equip {pair.Key}: {reason}");
                Check(DescribeInstance(gear.EquippedInstance(pair.Key)) == pair.Value, $"{pair.Key}: re-equipped item is the same uid with the same exact rolls");
            }
            Check(StatsNoPools() == statsNoPools0, "after re-equipping everything the stats equal the restored ones exactly");
            Check(gear.ToContainerSnapshot().ToJson() == gearJson0, "the equipment container serializes to the original bytes again");

            // a snapshot taken now round-trips through json and restores into the very same state
            string midState = Describe();
            LoadoutSnapshot now = LoadoutSnapshot.Capture(bag, gear);
            Check(LoadoutSnapshot.TryFromJson(now.ToJson(), out LoadoutSnapshot parsedNow, out reason) && parsedNow.ToJson() == now.ToJson(), "capture, json, parse is string-equal: " + reason);
            gear.Clear();
            bag.Clear();
            Check(LoadoutSnapshot.TryRestore(parsedNow, bag, gear, null, out reason) && Describe() == midState, "and restores to the same state: " + reason);
        }

        // ------------------------------------------------------------------ the state the game really produces

        private void ProductionStateChecks()
        {
            // Before the fixture wipes anything: the bag and equipment exactly as ClassLoadout.Apply left them (the
            // configured grid, the issuer-minted starting kit).
            // The scene rig may already have spent its kit; give it again through the very call ClassLoadout.Apply makes.
            if (bag.UsedSlots + gear.FilledCount == 0 && rig.Class != null) bag.GiveStartingItems(rig.Class.startingItems);
            string label = $"production {bag.GridWidth}x{bag.GridHeight} bag with {bag.UsedSlots} stack(s) and {gear.FilledCount} worn";
            string described = Describe();
            LoadoutSnapshot prod = LoadoutSnapshot.Capture(bag, gear);
            string json = prod.ToJson();
            Check(bag.UsedSlots + gear.FilledCount > 0, label + ": the starting kit is present");

            Check(LoadoutSnapshot.TryFromJson(json, out LoadoutSnapshot parsed, out string reason) && parsed.ToJson() == json, label + ": json round trip is string-equal: " + reason);
            gear.Clear();
            bag.Clear();
            Check(LoadoutSnapshot.TryRestore(parsed, bag, gear, null, out reason), label + ": restore: " + reason);
            Check(Describe() == described, label + ": restored state is instance-equal to the original");
            Check(LoadoutSnapshot.Capture(bag, gear).ToJson() == json, label + ": captures to the same bytes after the restore");
        }

        // ------------------------------------------------------------------ integer overflow in placement (M1)

        private void OverflowChecks()
        {
            string baseline = State();
            Vector2Int mantleSize = Def("emberthread_mantle").EffectiveFootprint;
            int w = bagSnap0.width, h = bagSnap0.height;
            ContainerSnapshot s;

            foreach (int bad in new[] { int.MaxValue, int.MaxValue - 1, int.MinValue, -1, w, w + 1 })
            {
                s = BagClone(); StackEntry(s, "ashen_relic").x = bad; s.Seal(); RefuseBag("1x1 stack at x=" + bad, s, "outside");
                s = BagClone(); StackEntry(s, "ashen_relic").y = bad; s.Seal(); RefuseBag("1x1 stack at y=" + bad, s, "outside");
            }
            foreach (int bad in new[] { int.MaxValue, int.MaxValue - 1, int.MinValue, -1, w, w - mantleSize.x + 1 })
            {
                s = BagClone(); GearEntry(s, "emberthread_mantle", 2).x = bad; s.Seal(); RefuseBag($"{mantleSize.x}x{mantleSize.y} gear at x={bad}", s, "outside");
            }
            foreach (int bad in new[] { int.MaxValue, int.MaxValue - 1, int.MinValue, -1, h, h - mantleSize.y + 1 })
            {
                s = BagClone(); GearEntry(s, "emberthread_mantle", 2).y = bad; s.Seal(); RefuseBag($"{mantleSize.x}x{mantleSize.y} gear at y={bad}", s, "outside");
            }

            // many gear entries parked on the one off-grid position (the exploit shape) are refused as a whole
            s = BagClone();
            ContainerEntry template = GearEntry(s, "emberthread_mantle", 2);
            for (int i = 0; i < 6; i++)
            {
                ContainerEntry extra = template.Clone();
                extra.x = int.MaxValue;
                extra.item.uid = Guid.NewGuid().ToString("N");
                Append(s, extra);
            }
            s.Seal();
            RefuseBag("six gear entries parked at x=int.MaxValue", s, "outside");

            // equipment entries carry no cell at all
            foreach (int bad in new[] { int.MaxValue, int.MinValue, -1 })
            {
                s = GearClone(); SlotEntry(s, "Feet").x = bad; s.Seal(); RefuseGear("equipment entry with x=" + bad, s, "no cell");
                s = GearClone(); SlotEntry(s, "Feet").y = bad; s.Seal(); RefuseGear("equipment entry with y=" + bad, s, "no cell");
            }

            // the live placement API is overflow-safe too
            ItemInstance probe = Gear(Def("footpad_boots"), AffixRarity.Common);
            Vector2Int[] origins = { new Vector2Int(int.MaxValue, 0), new Vector2Int(0, int.MaxValue), new Vector2Int(int.MaxValue, int.MaxValue), new Vector2Int(int.MinValue, 0), new Vector2Int(w, 0), new Vector2Int(0, h) };
            for (int i = 0; i < origins.Length; i++)
            {
                Check(!bag.CanPlace(origins[i], Vector2Int.one), "CanPlace refuses " + origins[i]);
                Check(!bag.CanPlace(origins[i], new Vector2Int(2, 3)), "CanPlace refuses a 2x3 at " + origins[i]);
                Check(!bag.TryAddInstanceAt(probe, origins[i], out string reason), "TryAddInstanceAt refuses " + origins[i] + ": " + reason);
                Check(!bag.TryAddAt(Def("ashen_relic"), 1, origins[i], out reason), "TryAddAt refuses " + origins[i] + ": " + reason);
                Check(!bag.TryMove(0, origins[i], out reason), "TryMove refuses " + origins[i] + ": " + reason);
            }

            Check(State() == baseline, "no overflow case changed the live state");
        }

        // ------------------------------------------------------------------ checksum independent of float spelling, migration order

        private void ChecksumSpellingAndMigration()
        {
            string checksum = bagSnap0.ComputeChecksum();
            string alt;
            StrictJson.SpellFloatForTest = v => v.ToString("G9", CultureInfo.InvariantCulture);
            try
            {
                alt = bagSnap0.ToJson();
                Check(alt != bagJson0, "a different float spelling changes the document text");
                Check(bagSnap0.ComputeChecksum() == checksum && bagSnap0.ChecksumMatches(), "but the checksum (hashed over exact float bits) does not change");
            }
            finally { StrictJson.SpellFloatForTest = null; }
            RefuseJson("the same document spelled by another formatter", alt, "canonical");
            Check(bagSnap0.ToJson() == bagJson0, "the default spelling is restored");

            // ---- migration: verify the ORIGINAL under the old rule, migrate, re-seal
            ContainerSchema.ClearMigrationsForTest();
            try
            {
                string v0Bag = MakeV0(bagJson0, false);
                RefuseJson("a schemaVersion 0 document with no migration registered", v0Bag, "no migration is registered");

                ContainerSchema.RegisterMigrationForTest(0, new MigrationStep
                {
                    VerifyIntegrity = root =>
                    {
                        JsonNode cs = ContainerSchema.Member(root, "checksum");
                        return cs != null && cs.Text == ContainerSchema.TreeChecksum(root) ? null : "the v0 checksum does not match the document";
                    },
                    Upgrade = root => { ContainerSchema.Member(root, "schemaVersion").Text = "1"; return null; } // then the production 1 -> 2 step runs
                });

                Check(ContainerSnapshot.TryFromJson(v0Bag, out ContainerSnapshot migrated, out string reason), "an intact v0 document migrates: " + reason);
                Check(migrated != null && migrated.schemaVersion == ContainerSnapshot.CurrentSchemaVersion && migrated.ChecksumMatches() && migrated.ToJson() == bagJson0, "the migrated model (v0 -> test step -> production 1 -> 2) is a normal current snapshot with a fresh checksum (same bytes as the original)");
                Inventory twin = NewBag(cat, 10, 6, out Equipment twinGear);
                Check(migrated != null && twin.TryRestore(migrated, out reason) && twin.ToContainerSnapshot().ToJson() == bagJson0, "and it restores: " + reason);
                Destroy(twin.gameObject);

                RefuseJson("a v0 document edited after it was written (original checksum fails)", v0Bag.Replace("\"quantity\":8", "\"quantity\":7"), "integrity");
                RefuseJson("a v0 document with a wrong checksum", Rep(v0Bag, "\"checksum\":\"sha256:", "\"checksum\":\"sha256:0", "bad v0 checksum"), "integrity");
                RefuseJson("a version 5 document is still newer even with migrations registered", Rep(bagJson0, "\"schemaVersion\":4", "\"schemaVersion\":5", "v5"), "newer");

                // loadout: wrapper and both containers at v0
                string v0Loadout = MakeV0(loadoutJson0, true);
                Check(LoadoutSnapshot.TryFromJson(v0Loadout, out LoadoutSnapshot lo, out reason) && lo.ToJson() == loadoutJson0, "an intact v0 loadout migrates to the original bytes: " + reason);
                bag.TryRemoveAt(0, 1);
                Check(Describe() != describe0, "target differs before the migrated restore");
                Check(LoadoutSnapshot.TryRestoreJson(v0Loadout, bag, gear, null, out reason) && Describe() == describe0, "a migrated loadout restores both containers: " + reason);
                RefuseLoadoutJson("a v0 loadout edited after it was written", v0Loadout.Replace("\"quantity\":8", "\"quantity\":7"), "integrity");

                // a current wrapper holding an old container is refused
                JsonNode mixed = ParseTree(loadoutJson0);
                JsonNode inner = ContainerSchema.Member(mixed, "bag");
                ContainerSchema.Member(inner, "schemaVersion").Text = "0";
                ContainerSchema.Member(inner, "checksum").Text = ContainerSchema.TreeChecksum(inner);
                RefuseLoadoutJson("a current loadout wrapper around a v0 container", StrictJson.Serialize(mixed, null), "share one version");
            }
            finally { ContainerSchema.ClearMigrationsForTest(); }

            RefuseJson("after clearing the registry the v0 document is refused again", MakeV0(bagJson0, false), "no migration is registered");
            Check(Describe() == describe0, "migration checks left the fixture state intact");
        }

        private JsonNode ParseTree(string json)
        {
            bool ok = StrictJson.TryParse(json, out JsonNode root, out string reason);
            Check(ok, "fixture json parses: " + reason);
            return root;
        }

        private void LegacySlots(JsonNode equipment)
        {
            JsonNode entries = ContainerSchema.Member(equipment, "entries");
            if (entries == null) return;
            for (int i = 0; i < entries.Items.Count; i++)
            {
                JsonNode slot = ContainerSchema.Member(entries.Items[i], "slot");
                if (slot == null) continue;
                if (slot.Text == "Feet") slot.Text = "Legs";
                Check(slot.Text != "Hands" && slot.Text != "Necklace" && slot.Text != "RingLeft" && slot.Text != "RingRight" && slot.Text != "AltWeapon" && slot.Text != "AltOffHand",
                    "the legacy fixture names no slot that did not exist in its era: " + slot.Text);
            }
        }

        /// <summary>Rewrites a current document as a schemaVersion-0 document whose checksum follows the old (tree) rule.</summary>
        private string MakeV0(string json, bool loadout)
        {
            JsonNode root = ParseTree(json);
            if (loadout)
            {
                foreach (string part in new[] { "bag", "equipment" })
                {
                    JsonNode inner = ContainerSchema.Member(root, part);
                    // A version 0 document predates the slots added in 3 (Feet) and 4 (AltWeapon, AltOffHand): the boots were worn in Legs,
                    // and the production 2 -> 3 and 3 -> 4 steps refuse a document that names a slot its era never had.
                    if (part == "equipment") LegacySlots(inner);
                    ContainerSchema.Member(inner, "schemaVersion").Text = "0";
                    ContainerSchema.Member(inner, "checksum").Text = ContainerSchema.TreeChecksum(inner);
                }
            }
            // D3c: version 0 predates the loadout's weaponSet and belt too, so they are not part of the old document (the 3 to 4 step adds them).
            if (loadout) root.Members.RemoveAll(m => m.Key == "weaponSet" || m.Key == "belt");
            ContainerSchema.Member(root, "schemaVersion").Text = "0";
            ContainerSchema.Member(root, "checksum").Text = ContainerSchema.TreeChecksum(root);
            return StrictJson.Serialize(root, null);
        }

        // ------------------------------------------------------------------ one step for observers, ledger moves

        private void ObserverAndLedgerChecks()
        {
            // a loadout in which two uids SWAP containers: boots A (worn) goes to the bag, boots B (carried) is worn
            ItemSnapshot wornA = SlotEntry(loadout0.equipment, "Feet").item.Clone();
            ItemSnapshot carriedB = GearEntry(loadout0.bag, "footpad_boots", 1).item.Clone();
            LoadoutSnapshot moved = loadout0.Clone();
            SlotEntry(moved.equipment, "Feet").item = carriedB.Clone();
            GearEntry(moved.bag, "footpad_boots", 1).item = wornA.Clone();
            moved.Seal();
            Guid uidA = Guid.ParseExact(wornA.uid, "N"), uidB = Guid.ParseExact(carriedB.uid, "N");

            Check(Describe() == describe0, "target is the saved loadout before the move");
            string descB = DescribeInstance(bag.InstanceAt(bag.IndexOfInstance(uidB)));

            // ---- ledger holding the OLD state
            ItemUidRegistry reg = new ItemUidRegistry();
            Check(reg.TryRegisterSnapshot(loadout0.bag, cat, out string reason) && reg.TryRegisterSnapshot(loadout0.equipment, cat, out reason), "ledger filled with the saved loadout: " + reason);
            int count = reg.Count;
            Check(reg.TryGetOwner(uidA, out string owner) && owner == "equipment" && reg.TryGetOwner(uidB, out owner) && owner == "bag", "ledger: A is worn, B is carried");

            RefuseBag("the moved bag alone against a ledger that still says A is worn", moved.bag, "already held by container 'equipment'", reg);
            Check(reg.Count == count && reg.TryGetOwner(uidA, out owner) && owner == "equipment", "that refusal left the ledger untouched");

            LoadoutSnapshot bad = moved.Clone();
            SlotEntry(bad.equipment, "Feet").item.affixes[0].value = float.NaN;
            bad.Seal();
            RefuseLoadout("a moved loadout with a bad roll, ledger given", bad, "equipment section refused", reg);
            Check(reg.Count == count && reg.TryGetOwner(uidA, out owner) && owner == "equipment" && reg.TryGetOwner(uidB, out owner) && owner == "bag", "a refused loadout left the ledger untouched");

            // ---- observers: consistent at every notification, and a throwing subscriber cannot undo the restore
            int calls = 0, violations = 0;
            Action<int> consistency = playerId =>
            {
                calls++;
                for (int s = 1; s < Equipment.SlotCount; s++)
                {
                    ItemInstance worn = gear.EquippedInstance((EquipmentSlot)s);
                    if (worn != null && bag.HoldsInstanceUid(worn.Uid)) violations++;
                }
                if (!gear.HoldsUid(uidB) || bag.HoldsInstanceUid(uidB) || !bag.HoldsInstanceUid(uidA) || gear.HoldsUid(uidA)) violations++;
            };
            Action<int, ItemData, bool> consistencyEquip = (playerId, item, equipped) => consistency(playerId);
            Action<int> thrower = playerId => { throw new InvalidOperationException("expected test exception from a throwing subscriber"); };
            Action<int, ItemData, bool> throwerEquip = (playerId, item, equipped) => { throw new InvalidOperationException("expected test exception from a throwing subscriber"); };

            GameEvents.InventoryChanged += consistency;
            GameEvents.EquipmentChanged += consistencyEquip;
            GameEvents.InventoryChanged += thrower;
            GameEvents.EquipmentChanged += throwerEquip;
            bool ok;
            try
            {
                ok = LoadoutSnapshot.TryRestore(moved, bag, gear, reg, out reason);
            }
            finally
            {
                GameEvents.InventoryChanged -= consistency;
                GameEvents.EquipmentChanged -= consistencyEquip;
                GameEvents.InventoryChanged -= thrower;
                GameEvents.EquipmentChanged -= throwerEquip;
            }

            Check(ok, "the moved loadout restores even though a subscriber throws: " + reason);
            Check(calls > 0, "subscribers were notified (" + calls + " calls)");
            Check(violations == 0, "no observer ever saw a uid both worn and carried, or an old half-state (" + violations + " violations)");
            Check(gear.EquippedInstance(EquipmentSlot.Feet).Uid == uidB && bag.HoldsInstanceUid(uidA) && !bag.HoldsInstanceUid(uidB), "A moved to the bag and B is worn");
            Check(DescribeInstance(gear.EquippedInstance(EquipmentSlot.Feet)) == descB, "B keeps its exact uid and rolls on the worn side");
            Check(reg.Count == count && reg.TryGetOwner(uidA, out owner) && owner == "bag" && reg.TryGetOwner(uidB, out owner) && owner == "equipment", "the ledger moved both uids atomically with the restore");
            Check(LoadoutSnapshot.Capture(bag, gear).ToJson() == moved.ToJson(), "capturing now gives the moved loadout's bytes");

            // ---- the same ledger accepts the move back
            Check(LoadoutSnapshot.TryRestore(loadout0.Clone(), bag, gear, reg, out reason) && Describe() == describe0, "moving the uids back against the same ledger works: " + reason);
            Check(reg.Count == count && reg.TryGetOwner(uidA, out owner) && owner == "equipment" && reg.TryGetOwner(uidB, out owner) && owner == "bag", "ledger follows");

            // ---- atomic multi-owner replace
            Check(!reg.TryReplaceOwners(new Dictionary<string, IReadOnlyCollection<Guid>> { { "bag", new[] { uidA } }, { "equipment", new[] { uidA } } }, out reason) && Has(reason, "two containers"), "TryReplaceOwners refuses one uid in two owners: " + reason);
            Check(!reg.TryReplaceOwners(new Dictionary<string, IReadOnlyCollection<Guid>> { { "bag", new[] { uidA } } }, out reason) && Has(reason, "already held by container 'equipment'"), "TryReplaceOwners refuses a uid held by an owner that is not being replaced: " + reason);
            Check(reg.Count == count && reg.TryGetOwner(uidA, out owner) && owner == "equipment", "refused replaces left the ledger untouched");
            ItemUidRegistry view = reg.WithoutOwners("bag", "equipment");
            Check(view.Count == 0 && reg.Count == count, "WithoutOwners is a copy: the original is untouched");
        }

        // ------------------------------------------------------------------ catalog and grid

        private void NoCatalogAndGridChecks()
        {
            Inventory noCat = NewBag(null, 10, 6, out Equipment noCatGear);
            string beforeJson = noCat.ToContainerSnapshot().ToJson();
            Check(noCat.Catalog == null, "fixture bag without a catalog");
            Check(!noCat.TryRestore(bagSnap0, out string reason) && Has(reason, "no item catalog"), "a bag without a catalog refuses every restore: " + reason);
            Check(!noCatGear.TryRestore(gearSnap0, out reason) && Has(reason, "no item catalog"), "equipment whose bag has no catalog refuses every restore: " + reason);
            Check(!LoadoutSnapshot.TryRestore(loadout0.Clone(), noCat, noCatGear, null, out reason) && Has(reason, "no item catalog"), "a loadout restore without a catalog is refused: " + reason);
            Check(noCat.ToContainerSnapshot().ToJson() == beforeJson && noCat.UsedSlots == 0, "the catalog-less bag stayed empty");
            Check(!LoadoutSnapshot.TryRestore(loadout0.Clone(), noCat, gear, null, out reason) && Has(reason, "does not belong"), "an equipment that belongs to another bag is refused: " + reason);
            Destroy(noCat.gameObject);

            Inventory small = NewBag(cat, 8, 6, out Equipment smallGear);
            Check(!small.TryRestore(bagSnap0, out reason) && Has(reason, "grid"), "a bag with a different grid refuses a 10x6 snapshot: " + reason);
            Check(small.UsedSlots == 0, "and stays empty");
            Destroy(small.gameObject);

            Inventory twin = NewBag(cat, 10, 6, out Equipment twinGear);
            Check(twin.TryRestore(bagSnap0, out reason), "a fresh bag with the real catalog restores the bag snapshot: " + reason);
            Check(twinGear.TryRestore(gearSnap0, out reason), "and its equipment restores the equipment snapshot (no rig, no hands): " + reason);
            Check(twin.UsedSlots == 8 && twinGear.FilledCount == 5, "restored content is present");
            Check(twin.ToContainerSnapshot().ToJson() == bagJson0 && twinGear.ToContainerSnapshot().ToJson() == gearJson0, "and serializes back to the original bytes");
            Destroy(twin.gameObject);
        }
    }
}
