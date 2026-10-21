using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Emberdelve.Data;
using Emberdelve.Items;

namespace Emberdelve.EditorTools
{
    /// <summary>
    /// U2B10 manager gate: builds the data twice through the real builder and checks the authored affix
    /// catalog and the chest rarity profiles end to end (stable GUIDs, no duplicates, valid catalog, real
    /// seeded table rolls with fixed + rolled properties, determinism, untouched definitions).
    /// Batch entry: Emberdelve.EditorTools.EmberdelveAffixProfileGate.Run
    /// </summary>
    public static class EmberdelveAffixProfileGate
    {
        static int _n, _fail;

        static void Check(bool ok, string text)
        {
            _n++;
            if (!ok) { _fail++; Debug.LogError("[AffixProfile] mismatch " + text); }
        }

        public static void Run()
        {
            _n = 0; _fail = 0;
            try { Execute(); }
            catch (Exception e) { _fail++; Debug.LogException(e); }
            Debug.Log("[Emberdelve][AffixProfile] " + (_fail == 0 ? "PASS" : "FAIL") + " checks=" + _n + " failures=" + _fail);
            EditorApplication.Exit(_fail == 0 ? 0 : 1);
        }

        static Dictionary<string, string> Guids()
        {
            var map = new Dictionary<string, string>();
            foreach (string guid in AssetDatabase.FindAssets("t:AffixDefinition", new[] { EmberdelveAssetLibrary.AffixFolder }))
                map[AssetDatabase.GUIDToAssetPath(guid)] = guid;
            foreach (string t in new[] { "Loot_SupplyChest", "Loot_EmberChest", "Loot_SealedChest" })
            {
                string path = EmberdelvePaths.Data + "/" + t + ".asset";
                map[path] = AssetDatabase.AssetPathToGUID(path);
            }
            return map;
        }

        static string Json(UnityEngine.Object o) => o == null ? "null" : EditorJsonUtility.ToJson(o);

        static void Execute()
        {
            EmberdelvePaths.EnsureFolders();
            EmberdelveAssetLibrary.BuildAll();
            AssetDatabase.SaveAssets();
            var first = Guids();
            string emberJson1 = Json(EmberdelveAssetLibrary.EmberLoot), sealedJson1 = Json(EmberdelveAssetLibrary.SealedLoot);

            EmberdelveAssetLibrary.BuildAll();
            AssetDatabase.SaveAssets();
            var second = Guids();

            // ---- idempotence
            Check(first.Count == 16, $"13 affixes + 3 tables found after first build (got {first.Count})");
            Check(second.Count == first.Count, $"second build created no extra assets ({first.Count} -> {second.Count})");
            bool sameGuids = true;
            foreach (var kv in first) if (!second.TryGetValue(kv.Key, out string g) || g != kv.Value) sameGuids = false;
            Check(sameGuids, "every asset kept its GUID across rebuild");
            Check(Json(EmberdelveAssetLibrary.EmberLoot) == emberJson1 && Json(EmberdelveAssetLibrary.SealedLoot) == sealedJson1,
                "rebuild left table data byte-identical (no appending)");

            // ---- catalog
            AffixDefinition[] cat = EmberdelveAssetLibrary.Affixes;
            Check(cat != null && cat.Length == AffixStatKeys.KeyCount, "builder exposes 13 affix definitions");
            if (cat == null) return;
            Check(AffixCatalog.TryValidate(cat, out string catReason), "catalog validates: " + catReason);
            for (int i = 0; i < cat.Length; i++)
            {
                var d = cat[i];
                if (d == null) { Check(false, "catalog entry " + i + " null"); continue; }
                AffixStatKeys.TryPrototypeBand(d.statKey, out float mn, out float mx, out float st);
                Check(d.statKey == (AffixStatKey)i && Mathf.Approximately(d.minValue, mn) && Mathf.Approximately(d.maxValue, mx)
                      && Mathf.Approximately(d.step, st) && Mathf.Approximately(d.weight, 1f), $"{d.id} prototype band/weight");
            }
            AffixCategory all = AffixCategories.All;
            Check(cat[(int)AffixStatKey.Armor].allowedCategories == (AffixCategory.Boots | AffixCategory.Cloak | AffixCategory.Armor), "armor categories");
            Check(cat[(int)AffixStatKey.MoveSpeed].allowedCategories == (AffixCategory.Boots | AffixCategory.Cloak), "move speed categories");
            Check(cat[(int)AffixStatKey.MeleeDamage].allowedCategories == AffixCategory.Weapon, "melee damage categories");
            Check(cat[(int)AffixStatKey.ProjectileDamage].allowedCategories == AffixCategory.Weapon, "projectile damage categories");
            Check(cat[(int)AffixStatKey.Strength].allowedCategories == all && cat[(int)AffixStatKey.ManaRegen].allowedCategories == all, "general categories");

            // ---- profiles
            LootTable supply = EmberdelveAssetLibrary.SupplyLoot, ember = EmberdelveAssetLibrary.EmberLoot, seal = EmberdelveAssetLibrary.SealedLoot;
            Check(supply.tableId == "supply_chest" && ember.tableId == "ember_chest" && seal.tableId == "sealed_chest", "table ids preserved");
            // U2B11 rewrite: the original 4/4/3 entries are preserved IN ORDER (asserted entry by entry below, by index) and
            // each table gained exactly one appended cloak entry, so the counts are now 5/5/4. The previous expectation
            // 4/4/3 pre-dated the cloak slot and only counted the pre-cloak tables; coverage is kept and extended.
            Check(supply.entries.Length == 5 && ember.entries.Length == 5 && seal.entries.Length == 4, "entry counts: original 4/4/3 plus one appended cloak each");
            Check(supply.entries[4].item == EmberdelveAssetLibrary.AshenCloak && supply.entries[4].affixRarity == AffixRarity.Common, "supply cloak entry appended Common");
            Check(ember.entries[4].item == EmberdelveAssetLibrary.EmberMantle && ember.entries[4].affixRarity == AffixRarity.Uncommon, "ember cloak entry appended Uncommon");
            Check(seal.entries[3].item == EmberdelveAssetLibrary.EmberMantle && seal.entries[3].affixRarity == AffixRarity.Rare, "sealed cloak entry appended Rare");
            foreach (var e in supply.entries) Check(e.affixRarity == AffixRarity.Common, "supply entry Common " + e.item.itemId);
            Check(ember.entries[0].item == EmberdelveAssetLibrary.Cuirass && ember.entries[0].affixRarity == AffixRarity.Uncommon, "ember cuirass Uncommon");
            Check(ember.entries[3].item == EmberdelveAssetLibrary.FangBlade && ember.entries[3].affixRarity == AffixRarity.Uncommon, "ember blade Uncommon");
            Check(ember.entries[1].affixRarity == AffixRarity.Common && ember.entries[2].affixRarity == AffixRarity.Common, "ember carried Common");
            Check(seal.entries[1].item == EmberdelveAssetLibrary.ShieldItem && seal.entries[1].affixRarity == AffixRarity.Rare, "sealed shield Rare");
            Check(seal.entries[0].affixRarity == AffixRarity.Common && seal.entries[2].affixRarity == AffixRarity.Common, "sealed carried Common");
            Check(ember.affixCatalog != null && ember.affixCatalog.Length == 13 && seal.affixCatalog != null && seal.affixCatalog.Length == 13, "rolled tables carry catalog");
            Check(ember.Validate().Count == 0 && seal.Validate().Count == 0 && supply.Validate().Count == 0, "tables validate clean");

            // ---- real rolls
            string defsBefore = Json(EmberdelveAssetLibrary.Cuirass) + Json(EmberdelveAssetLibrary.FangBlade) + Json(EmberdelveAssetLibrary.ShieldItem) + Json(EmberdelveAssetLibrary.Boots);
            int uncommonSeen = 0, rareSeen = 0, commonGearSeen = 0, carried = 0;
            var distinctValues = new HashSet<string>();
            foreach (var table in new[] { supply, ember, seal })
            {
                for (int seed = 1; seed <= 300; seed++)
                {
                    var a = new List<LootDrop>(); var b = new List<LootDrop>();
                    bool okA = table.TryRoll(seed, a, EmberdelveAssetLibrary.ItemCatalogAsset, out string ra), okB = table.TryRoll(seed, b, EmberdelveAssetLibrary.ItemCatalogAsset, out string rb);
                    if (!okA || !okB) { Check(false, $"{table.tableId} seed {seed} refused: {ra} {rb}"); continue; }
                    bool same = a.Count == b.Count;
                    for (int i = 0; i < a.Count; i++)
                    {
                        LootDrop d = a[i];
                        if (same && (b[i].Item != d.Item || b[i].Amount != d.Amount)) same = false;
                        if (d.Instance == null) { carried++; Check(!d.Item.IsEquippable, $"{table.tableId}/{seed} carried drop is not gear"); continue; }
                        var inst = d.Instance;
                        Check(inst.Quantity == 1 && d.Amount == 1, "gear unit is quantity 1");
                        LootEntry entry = Array.Find(table.entries, x => x.item == d.Item);
                        Check(entry != null && inst.Rarity == entry.affixRarity, $"{table.tableId}/{seed} rarity matches entry");
                        AffixRarities.TryExpectedAffixCount(inst.Rarity, out int expected);
                        Check(inst.AffixCount == expected, $"{table.tableId}/{seed} {d.Item.itemId} affix count {inst.AffixCount}=={expected}");
                        var keys = new HashSet<AffixStatKey>();
                        foreach (var rec in inst.Affixes)
                        {
                            Check(keys.Add(rec.StatKey), "distinct key per item");
                            var def = cat[(int)rec.StatKey];
                            bool valueOk = def.TryValidateRolledValue(rec.Value, inst.Category, out string vr);
                            Check(def.id == rec.Id && valueOk, $"record {rec} valid: {vr}");
                            distinctValues.Add(rec.Id + ":" + rec.Value);
                        }
                        // determinism of the rolled half
                        if (i < b.Count && b[i].Instance != null)
                        {
                            bool sameRolls = b[i].Instance.AffixCount == inst.AffixCount && b[i].Instance.UidText != inst.UidText;
                            for (int k = 0; sameRolls && k < inst.AffixCount; k++) sameRolls = b[i].Instance.Affixes[k] == inst.Affixes[k];
                            Check(sameRolls, $"{table.tableId}/{seed} same seed same rolls, fresh UID");
                        }
                        if (inst.Rarity == AffixRarity.Uncommon) uncommonSeen++;
                        else if (inst.Rarity == AffixRarity.Rare) rareSeen++;
                        else commonGearSeen++;
                    }
                    Check(same, $"{table.tableId}/{seed} same seed same shape");
                }
            }
            Check(uncommonSeen > 0 && rareSeen > 0 && commonGearSeen > 0 && carried > 0,
                $"profiles produced all kinds (uncommon {uncommonSeen}, rare {rareSeen}, common gear {commonGearSeen}, carried {carried})");
            Check(distinctValues.Count > 5, $"seeds give varied affixes ({distinctValues.Count} distinct id:value)");
            // ---- explicit seeds 303/404/505 on the real tables (added in P1 verification)
            int seenCuirass = 0, seenBlade = 0, seenShield = 0, seenBoots = 0;
            foreach (int seed in new[] { 303, 404, 505 })
            {
                foreach (var table in new[] { supply, ember, seal })
                {
                    var drops = new List<LootDrop>();
                    Check(table.TryRoll(seed, drops, EmberdelveAssetLibrary.ItemCatalogAsset, out string rr), $"{table.tableId} seed {seed} rolls: {rr}");
                    foreach (var d in drops)
                    {
                        if (d.Instance == null) continue;
                        var keys = new HashSet<AffixStatKey>();
                        foreach (var rec in d.Instance.Affixes) keys.Add(rec.StatKey);
                        if (d.Item == EmberdelveAssetLibrary.Cuirass) { seenCuirass++; Check(d.Instance.AffixCount == 1 && keys.Count == 1, $"{table.tableId}/{seed} cuirass has exactly 1 affix (got {d.Instance.AffixCount})"); }
                        else if (d.Item == EmberdelveAssetLibrary.FangBlade) { seenBlade++; Check(d.Instance.AffixCount == 1 && keys.Count == 1, $"{table.tableId}/{seed} blade has exactly 1 affix (got {d.Instance.AffixCount})"); }
                        else if (d.Item == EmberdelveAssetLibrary.ShieldItem) { seenShield++; Check(d.Instance.AffixCount == 2 && keys.Count == 2, $"{table.tableId}/{seed} shield has 2 distinct affix keys (got {d.Instance.AffixCount}/{keys.Count})"); }
                        else if (d.Item == EmberdelveAssetLibrary.Boots) { seenBoots++; Check(d.Instance.AffixCount == 0, $"{table.tableId}/{seed} Common boots have 0 affixes (got {d.Instance.AffixCount})"); }
                    }
                }
            }
            Debug.Log($"[Emberdelve][AffixProfile] seeds303/404/505 seen cuirass={seenCuirass} blade={seenBlade} shield={seenShield} boots={seenBoots}");
            Check(seenCuirass + seenBlade + seenShield + seenBoots > 0, "explicit seeds produced at least one checked gear drop");
            string defsAfter =Json(EmberdelveAssetLibrary.Cuirass) + Json(EmberdelveAssetLibrary.FangBlade) + Json(EmberdelveAssetLibrary.ShieldItem) + Json(EmberdelveAssetLibrary.Boots);
            Check(defsBefore == defsAfter, "rolling never wrote to item definitions");
            Debug.Log($"[Emberdelve][AffixProfile] rolls uncommon={uncommonSeen} rare={rareSeen} commonGear={commonGearSeen} carried={carried} distinct={distinctValues.Count}");
        }
    }
}
