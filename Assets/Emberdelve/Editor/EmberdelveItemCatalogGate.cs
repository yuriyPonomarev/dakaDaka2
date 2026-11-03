using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using UnityEditor;
using UnityEngine;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Items;

namespace Emberdelve.EditorTools
{
    /// <summary>
    /// R3 gate: the one runtime item catalog and the end of "any ItemData becomes gear".
    ///
    /// Edit mode only, batch friendly: it builds in-memory ScriptableObjects and bags, loads the real catalog
    /// asset, and runs BuildDataOnly twice. Every negative check asserts the refusal AND that no state moved
    /// (no UID minted, no stack placed, no counter changed).
    /// </summary>
    public static class EmberdelveItemCatalogGate
    {
        private const string DataFolder = "Assets/Emberdelve/Data";

        private static int _n;
        private static int _fail;
        private static readonly List<UnityEngine.Object> Cleanup = new List<UnityEngine.Object>();

        private static void Check(bool ok, string text)
        {
            _n++;
            if (!ok)
            {
                _fail++;
                Debug.LogError("[ItemCatalog] mismatch " + text);
            }
        }

        public static void Run()
        {
            _n = 0;
            _fail = 0;
            try { Execute(); }
            catch (Exception e) { _fail++; Debug.LogException(e); }
            finally
            {
                for (int i = 0; i < Cleanup.Count; i++)
                {
                    if (Cleanup[i] != null) UnityEngine.Object.DestroyImmediate(Cleanup[i]);
                }
                Cleanup.Clear();
            }

            Debug.Log("[Emberdelve][ItemCatalog] " + (_fail == 0 ? "PASS" : "FAIL") + " checks=" + _n + " failures=" + _fail);
            EmberdelveGateExit.Exit(_fail == 0 ? 0 : 1);
        }

        private static T Make<T>() where T : ScriptableObject
        {
            T x = ScriptableObject.CreateInstance<T>();
            Cleanup.Add(x);
            return x;
        }

        private static ItemData MakeItem(string id, EquipmentSlot slot, ItemKind kind = ItemKind.Armor)
        {
            ItemData item = Make<ItemData>();
            item.itemId = id;
            item.displayName = id;
            item.equipSlot = slot;
            item.kind = kind;
            item.maxStack = slot == EquipmentSlot.None ? 5 : 1;
            return item;
        }

        private static Inventory MakeBag(int w, int h)
        {
            GameObject go = new GameObject("ItemCatalogGate_Bag");
            Cleanup.Add(go);
            Inventory bag = go.AddComponent<Inventory>();
            bag.ConfigureGrid(w, h);
            bag.Clear();
            return bag;
        }

        private static string BagState(Inventory bag)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append(bag.UsedSlots).Append('|').Append(bag.UsedCells).Append('|').Append(bag.TotalUnits).Append('|')
              .Append(bag.CarriedValue).Append('|').Append(bag.RecoveredValue).Append('|').Append(bag.LifetimeCollected)
              .Append('|').Append(bag.MintedInstanceCount);
            for (int i = 0; i < bag.Slots.Count; i++)
            {
                InventorySlot s = bag.Slots[i];
                sb.Append(';').Append(s.Item != null ? s.Item.itemId : "null").Append(',').Append(s.Count).Append(',')
                  .Append(s.Origin).Append(',').Append(s.UidText);
            }
            return sb.ToString();
        }

        private static T Load<T>(string path) where T : UnityEngine.Object
        {
            return AssetDatabase.LoadAssetAtPath<T>(path);
        }

        private static Dictionary<string, string> HashData()
        {
            Dictionary<string, string> map = new Dictionary<string, string>();
            using (SHA1 sha = SHA1.Create())
            {
                foreach (string file in Directory.GetFiles(DataFolder, "*", SearchOption.AllDirectories))
                {
                    if (!file.EndsWith(".asset") && !file.EndsWith(".meta") && !file.EndsWith(".inputactions")) continue;
                    map[file.Replace('\\', '/')] = BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(file)));
                }
            }
            return map;
        }

        private static void Execute()
        {
            // ---------------------------------------------------------------- idempotent build first
            // The catalog asset must exist and the builder must be a no-op the second time.
            var hash0 = HashData();
            EmberdelveBootstrapMenu.BuildDataOnly();
            AssetDatabase.SaveAssets();
            var hash1 = HashData();
            string catalogPath = EmberdelveAssetLibrary.ItemCatalogPath;
            string configPath = DataFolder + "/EmberdelveGameConfig.asset";
            string guidCatalog1 = AssetDatabase.AssetPathToGUID(catalogPath);
            string guidConfig1 = AssetDatabase.AssetPathToGUID(configPath);

            EmberdelveBootstrapMenu.BuildDataOnly();
            AssetDatabase.SaveAssets();
            var hash2 = HashData();

            Check(!string.IsNullOrEmpty(guidCatalog1), "the item catalog asset exists after BuildDataOnly");
            Check(AssetDatabase.AssetPathToGUID(catalogPath) == guidCatalog1 && AssetDatabase.AssetPathToGUID(configPath) == guidConfig1,
                "catalog and config GUIDs are stable across the second BuildDataOnly");
            Check(hash1.Count == hash2.Count && hash0.Count == hash2.Count, $"no data file created or removed by repeated builds ({hash0.Count}/{hash1.Count}/{hash2.Count})");
            int differing = 0;
            foreach (var kv in hash1)
            {
                if (!hash2.TryGetValue(kv.Key, out string h) || h != kv.Value)
                {
                    differing++;
                    Debug.LogError("[ItemCatalog] data file changed on the second build: " + kv.Key);
                }
            }
            Check(differing == 0, "second BuildDataOnly left every data asset and meta byte-identical (no dirty diff)");
            int driftFromStart = 0;
            foreach (var kv in hash0)
            {
                if (!hash2.TryGetValue(kv.Key, out string h) || h != kv.Value)
                {
                    driftFromStart++;
                    Debug.LogError("[ItemCatalog] data file differs from the tree the gate started on: " + kv.Key);
                }
            }
            Check(driftFromStart == 0, "builder output equals the tree the gate started on (the built tree is the committed truth)");

            // ---------------------------------------------------------------- real catalog coverage
            ItemCatalog catalog = Load<ItemCatalog>(catalogPath);
            Check(catalog != null, "item catalog asset loads");
            if (catalog == null) return;
            Check(!catalog.IsTestCatalog, "the asset is not a test catalog");
            Check(catalog.IsValid(out string validReason), "the asset catalog validates: " + validReason);
            ItemInstanceAuthority authority = catalog.Authority;
            Check(authority != null && ReferenceEquals(authority, catalog.Authority), "one authority, built once and cached");

            string[] guids = AssetDatabase.FindAssets("t:ItemData", new[] { DataFolder });
            HashSet<ItemData> onDisk = new HashSet<ItemData>();
            foreach (string g in guids)
            {
                ItemData asset = Load<ItemData>(AssetDatabase.GUIDToAssetPath(g));
                if (asset != null) onDisk.Add(asset);
            }

            Check(onDisk.Count > 0 && onDisk.Count == catalog.ItemCount,
                $"catalog lists exactly the ItemData assets on disk ({onDisk.Count} assets, {catalog.ItemCount} entries)");

            int orphan = 0, stale = 0;
            foreach (ItemData asset in onDisk)
            {
                if (!catalog.Contains(asset)) { orphan++; Debug.LogError("[ItemCatalog] orphan asset not in catalog: " + asset.name); }
            }
            for (int i = 0; i < catalog.Items.Count; i++)
            {
                ItemData entry = catalog.Items[i];
                string path = entry != null ? AssetDatabase.GetAssetPath(entry) : null;
                if (entry == null || string.IsNullOrEmpty(path) || !path.StartsWith(DataFolder, StringComparison.Ordinal) || !onDisk.Contains(entry))
                {
                    stale++;
                    Debug.LogError("[ItemCatalog] stale or foreign catalog entry " + i);
                }
            }
            Check(orphan == 0, "no ItemData asset is missing from the catalog (no orphans)");
            Check(stale == 0, "no catalog entry is null, foreign or stale");

            HashSet<ItemKind> kinds = new HashSet<ItemKind>();
            HashSet<EquipmentSlot> slots = new HashSet<EquipmentSlot>();
            for (int i = 0; i < catalog.Items.Count; i++)
            {
                ItemData item = catalog.Items[i];
                kinds.Add(item.kind);
                slots.Add(item.equipSlot);
                Check(ItemIdentityRules.IsStableItemId(item.itemId, out _), item.name + " has a stable item id");
                Check(catalog.TryResolveItem(item.itemId, out ItemData found, out _) && ReferenceEquals(found, item), item.itemId + " resolves to its own asset by id");
            }
            Check(kinds.Contains(ItemKind.Weapon) && kinds.Contains(ItemKind.Armor) && kinds.Contains(ItemKind.Currency) &&
                  kinds.Contains(ItemKind.Consumable) && kinds.Contains(ItemKind.Relic) && kinds.Contains(ItemKind.AmmoPack),
                "catalog covers weapons, armour, currency, consumables, relics and ammo");
            Check(slots.Contains(EquipmentSlot.Weapon) && slots.Contains(EquipmentSlot.OffHand) && slots.Contains(EquipmentSlot.Chest) &&
                  slots.Contains(EquipmentSlot.Feet) && slots.Contains(EquipmentSlot.Back) && slots.Contains(EquipmentSlot.None),
                "catalog covers weapon, shield, chest, boots (Feet since D3b), cloak and carried items");
            Check(slots.Contains(EquipmentSlot.Hands) && slots.Contains(EquipmentSlot.Necklace) && slots.Contains(EquipmentSlot.RingLeft),
                "catalog covers the D3b slot items: gloves, necklace and ring");

            int affixAssets = AssetDatabase.FindAssets("t:AffixDefinition", new[] { EmberdelveAssetLibrary.AffixFolder }).Length;
            Check(catalog.AffixCount == affixAssets && affixAssets > 0, $"catalog affix list covers every affix asset ({catalog.AffixCount}/{affixAssets})");
            bool affixesValid = AffixCatalog.TryValidate(catalog.Affixes, out string affixReason);
            Check(catalog.Affixes.Count > 0 && affixesValid, "the catalog's affix catalog validates: " + affixReason);

            GameConfig config = Load<GameConfig>(configPath);
            Check(config != null && config.itemCatalog == catalog, "GameConfig references the one item catalog");

            // every reference the shipped data makes to an item is a catalog entry
            int unknownRefs = 0;
            foreach (string g in AssetDatabase.FindAssets("t:LootTable", new[] { DataFolder }))
            {
                LootTable table = Load<LootTable>(AssetDatabase.GUIDToAssetPath(g));
                for (int i = 0; i < table.entries.Length; i++)
                {
                    if (table.entries[i].item != null && !catalog.Contains(table.entries[i].item)) { unknownRefs++; Debug.LogError("[ItemCatalog] " + table.name + " entry " + i + " is not a catalog item"); }
                }
                if (table.affixCatalog != null)
                {
                    foreach (AffixDefinition a in table.affixCatalog)
                    {
                        if (!catalog.TryResolveAffix(a.id, out AffixDefinition owned, out _) || !ReferenceEquals(owned, a)) { unknownRefs++; Debug.LogError("[ItemCatalog] " + table.name + " affix " + a.id + " is not a catalog affix"); }
                    }
                }
            }
            foreach (string g in AssetDatabase.FindAssets("t:CharacterClassData", new[] { DataFolder }))
            {
                CharacterClassData data = Load<CharacterClassData>(AssetDatabase.GUIDToAssetPath(g));
                if (data.startingItems == null) continue;
                foreach (ItemData item in data.startingItems)
                {
                    if (item != null && !catalog.Contains(item)) { unknownRefs++; Debug.LogError("[ItemCatalog] " + data.name + " starting item " + item.itemId + " is not a catalog item"); }
                }
            }
            foreach (string g in AssetDatabase.FindAssets("t:EnemyData", new[] { DataFolder }))
            {
                EnemyData data = Load<EnemyData>(AssetDatabase.GUIDToAssetPath(g));
                if (data.lootTable != null)
                {
                    foreach (ItemData item in data.lootTable)
                    {
                        if (item != null && !catalog.Contains(item)) { unknownRefs++; Debug.LogError("[ItemCatalog] " + data.name + " drop " + item.itemId + " is not a catalog item"); }
                    }
                }
                // R4: the table the runtime actually rolls for this enemy.
                if (data.dropTable != null && data.dropTable.entries != null)
                {
                    foreach (LootEntry entry in data.dropTable.entries)
                    {
                        if (entry != null && entry.item != null && !catalog.Contains(entry.item)) { unknownRefs++; Debug.LogError("[ItemCatalog] " + data.name + " drop-table item " + entry.item.itemId + " is not a catalog item"); }
                    }
                    if (data.dropTable.affixCatalog != null)
                    {
                        foreach (AffixDefinition affix in data.dropTable.affixCatalog)
                        {
                            if (affix != null && (!catalog.TryResolveAffix(affix.id, out AffixDefinition owned, out _) || owned != affix)) { unknownRefs++; Debug.LogError("[ItemCatalog] " + data.name + " drop-table affix " + affix.id + " is not a catalog affix"); }
                        }
                    }
                }
            }
            Check(unknownRefs == 0, "every loot-table entry, affix, starting item and enemy drop in the data is a catalog entry");

            // ---------------------------------------------------------------- build-time validation (negative)
            ItemData boots = Load<ItemData>(DataFolder + "/Item_FootpadBoots.asset");
            ItemData coin = Load<ItemData>(DataFolder + "/Item_EmberCoin.asset");
            ItemData shield = Load<ItemData>(DataFolder + "/Item_EmberbulwarkShieldItem.asset");
            Check(boots != null && coin != null && shield != null, "real fixtures load");
            AffixDefinition[] realAffixes = new List<AffixDefinition>(catalog.Affixes).ToArray();
            ItemInstanceAuthority built;
            string reason;

            Check(ItemCatalog.Validate(new[] { boots, coin }, realAffixes, true, out built, out reason) && built != null, "valid lists build");
            Check(!ItemCatalog.Validate(new ItemData[] { boots, null }, realAffixes, true, out built, out reason) && built == null && reason.Contains("null"), "null item reference is a build error: " + reason);
            Check(!ItemCatalog.Validate(null, realAffixes, true, out built, out reason) && built == null, "null item list is a build error");
            Check(!ItemCatalog.Validate(new[] { boots }, null, true, out built, out reason) && built == null, "null affix list is a build error");
            Check(!ItemCatalog.Validate(new ItemData[0], realAffixes, true, out built, out reason) && built == null, "empty item list is a build error");

            ItemData badUpper = MakeItem("Bad_Id", EquipmentSlot.None, ItemKind.Relic);
            ItemData badEmpty = MakeItem("", EquipmentSlot.None, ItemKind.Relic);
            ItemData badSpace = MakeItem("bad id", EquipmentSlot.None, ItemKind.Relic);
            ItemData badPad = MakeItem(" padded", EquipmentSlot.None, ItemKind.Relic);
            ItemData badLong = MakeItem("a" + new string('b', 80), EquipmentSlot.None, ItemKind.Relic);
            foreach (ItemData bad in new[] { badUpper, badEmpty, badSpace, badPad, badLong })
            {
                Check(!ItemCatalog.Validate(new[] { coin, bad }, realAffixes, true, out built, out reason) && built == null,
                    $"invalid id '{bad.itemId}' is a build error ({reason})");
            }

            ItemData twin = MakeItem(coin.itemId, EquipmentSlot.None, ItemKind.Currency);
            Check(!ItemCatalog.Validate(new[] { coin, twin }, realAffixes, true, out built, out reason) && built == null && reason.Contains("duplicate"),
                "two different definitions sharing one id are a build error: " + reason);
            Check(!ItemCatalog.Validate(new[] { coin, coin }, realAffixes, true, out built, out reason) && built == null && reason.Contains("duplicate"),
                "the same definition listed twice is a build error: " + reason);

            Check(!ItemCatalog.Validate(new[] { boots }, new AffixDefinition[0], true, out built, out reason) && built == null && reason.Contains("affix catalog"),
                "rarity-capable gear without an affix catalog is a build error: " + reason);
            Check(ItemCatalog.Validate(new[] { coin }, new AffixDefinition[0], true, out built, out reason) && built != null,
                "carried-only items need no affix catalog");

            AffixDefinition dupKey1 = Make<AffixDefinition>();
            dupKey1.id = "gate_dup_a"; dupKey1.statKey = AffixStatKey.Dexterity; dupKey1.allowedCategories = AffixCategory.Boots;
            AffixDefinition dupKey2 = Make<AffixDefinition>();
            dupKey2.id = "gate_dup_b"; dupKey2.statKey = AffixStatKey.Dexterity; dupKey2.allowedCategories = AffixCategory.Boots;
            Check(!ItemCatalog.Validate(new[] { boots }, new[] { dupKey1, dupKey2 }, true, out built, out reason) && built == null,
                "an invalid affix catalog (duplicate stat key) is a build error: " + reason);

            // an invalid asset-style catalog refuses to serve anything
            ItemCatalog broken = ItemCatalog.CreateForTest(new[] { coin, twin }, realAffixes);
            Cleanup.Add(broken);
            Check(!broken.IsValid(out reason) && broken.Authority == null && !broken.Contains(coin) && !broken.TryResolveItem(coin.itemId, out _, out _),
                "a catalog whose lists are invalid serves no lookup");

            // ---------------------------------------------------------------- minting
            ItemIdentityIssuer issuer = ItemIdentityIssuer.CreateLocalTrusted();
            ItemData clonedBoots = UnityEngine.Object.Instantiate(boots);
            Cleanup.Add(clonedBoots);
            ItemData adHocGear = MakeItem("adhoc_blade", EquipmentSlot.Weapon, ItemKind.Weapon);

            Check(!catalog.TryMintCommon(issuer, clonedBoots, out ItemInstance refused, out reason) && refused == null && issuer.MintedCount == 0,
                "a cloned definition (real id, not the catalog's object) mints nothing: " + reason);
            Check(!catalog.TryMintCommon(issuer, adHocGear, out refused, out reason) && refused == null && issuer.MintedCount == 0,
                "a definition with an id the catalog does not own mints nothing: " + reason);
            Check(!catalog.TryMintCommon(issuer, coin, out refused, out reason) && refused == null && issuer.MintedCount == 0,
                "a carried definition has no gear identity to mint: " + reason);
            Check(!catalog.TryMintCommon(issuer, null, out refused, out reason) && issuer.MintedCount == 0, "a null definition mints nothing");
            Check(!catalog.TryMintCommon(null, boots, out refused, out reason), "a null issuer mints nothing");
            Check(catalog.TryMintCommon(issuer, boots, out ItemInstance minted, out reason) && minted != null && ReferenceEquals(minted.Definition, boots) &&
                  minted.Rarity == AffixRarity.Common && minted.Quantity == 1 && issuer.MintedCount == 1,
                "a catalog entry mints exactly one Common instance: " + reason);

            // ---------------------------------------------------------------- the bag
            Inventory noCatalog = MakeBag(10, 4);
            string stateBefore = BagState(noCatalog);
            Check(!noCatalog.TryAdd(boots, 1, out reason, out int accepted) && accepted == 0 && BagState(noCatalog) == stateBefore,
                "a bag with no catalog refuses gear and moves nothing: " + reason);
            Check(!noCatalog.TryAddAt(boots, 1, Vector2Int.zero, out reason) && BagState(noCatalog) == stateBefore,
                "a bag with no catalog refuses an exact-cell gear add too");
            Check(noCatalog.TryAdd(coin, 3, out reason, out accepted) && accepted == 3, "carried goods never needed a catalog: " + reason);

            Inventory bag = MakeBag(10, 4);
            bag.SetCatalog(catalog);
            Check(ReferenceEquals(bag.Catalog, catalog), "the bag reports the catalog it was given");
            Check(bag.TryAdd(boots, 1, out reason, out accepted) && accepted == 1 && bag.MintedInstanceCount == 1 && bag.UsedSlots == 1 &&
                  bag.Slots[0].Instance != null && ReferenceEquals(bag.Slots[0].Instance.Definition, boots),
                "a catalog gear definition still lands as one minted instance: " + reason);

            stateBefore = BagState(bag);
            Check(!bag.TryAdd(clonedBoots, 1, out reason, out accepted) && accepted == 0 && BagState(bag) == stateBefore,
                "a cloned definition handed to the bag is refused with no partial state: " + reason);
            Check(!bag.TryAdd(clonedBoots, 3, out reason, out accepted) && accepted == 0 && BagState(bag) == stateBefore,
                "an amount of 3 cloned definitions is refused whole, not placed one by one: " + reason);
            Check(!bag.TryAdd(adHocGear, 1, out reason, out accepted) && accepted == 0 && BagState(bag) == stateBefore,
                "an ad-hoc definition with a fresh id is refused with no partial state: " + reason);
            Check(!bag.TryAddAt(clonedBoots, 1, new Vector2Int(6, 0), out reason) && BagState(bag) == stateBefore,
                "an exact-cell add of a cloned definition is refused with no partial state: " + reason);
            Check(!bag.TryAddAt(adHocGear, 1, new Vector2Int(6, 0), out reason) && BagState(bag) == stateBefore,
                "an exact-cell add of an ad-hoc definition is refused with no partial state: " + reason);

            bag.GiveStartingItems(new[] { coin, clonedBoots, adHocGear, shield });
            Check(bag.TotalUnits == 3 && bag.Slots.Count == 3 && bag.MintedInstanceCount == 2,
                "a starting kit with non-catalog gear gives the catalog entries and refuses the rest " +
                $"(units {bag.TotalUnits}, stacks {bag.Slots.Count}, minted {bag.MintedInstanceCount})");
            bool shieldCarried = false, clonedCarried = false, adHocCarried = false;
            for (int i = 0; i < bag.Slots.Count; i++)
            {
                if (bag.Slots[i].Item == shield) shieldCarried = true;
                if (bag.Slots[i].Item == clonedBoots) clonedCarried = true;
                if (bag.Slots[i].Item == adHocGear) adHocCarried = true;
            }
            Check(shieldCarried && !clonedCarried && !adHocCarried, "only the catalog shield joined the kit");

            // an instance built from a different authority (a definition the catalog does not own) is not admitted
            ItemCatalog sideCatalog = ItemCatalog.CreateForTest(new[] { adHocGear }, new AffixDefinition[0]);
            Cleanup.Add(sideCatalog);
            Check(sideCatalog.TryMintCommon(issuer, adHocGear, out ItemInstance sideInstance, out reason), "side catalog mints for its own entry: " + reason);
            stateBefore = BagState(bag);
            Check(!bag.TryAddInstance(sideInstance, out reason) && BagState(bag) == stateBefore,
                "an instance of a non-catalog definition is not admitted to a catalogued bag: " + reason);

            // a hand-built instance of a catalog definition carrying a forged roll is refused, bag untouched
            // (a) an affix id the catalog does not own, (b) a real catalog affix id re-used with a different stat key.
            AffixDefinition forged = Make<AffixDefinition>();
            forged.id = "gate_forged_dex"; forged.statKey = AffixStatKey.Dexterity; forged.allowedCategories = AffixCategory.Boots;
            string realArmorId = null;
            foreach (AffixDefinition real in catalog.Affixes) if (real.statKey == AffixStatKey.Armor) realArmorId = real.id;
            AffixDefinition impostor = Make<AffixDefinition>();
            impostor.id = realArmorId; impostor.statKey = AffixStatKey.Strength; impostor.allowedCategories = AffixCategory.Boots;
            Check(realArmorId != null, "the catalog has an armor affix id to impersonate");
            Check(ItemInstanceAuthority.TryCreate(new[] { boots }, new[] { forged }, out ItemInstanceAuthority forgedAuth, out reason), "forger authority (a) builds: " + reason);
            Check(ItemInstanceAuthority.TryCreate(new[] { boots }, new[] { impostor }, out ItemInstanceAuthority impostorAuth, out reason), "forger authority (b) builds: " + reason);
            Check(ItemInstance.TryCreateNew(boots, 1, AffixRarity.Uncommon, new[] { new AffixRecord("gate_forged_dex", AffixStatKey.Dexterity, 3) }, forgedAuth, out ItemInstance forgedInstance, out reason) && forgedInstance != null,
                "forged instance (a): real boots, affix id the catalog does not own: " + reason);
            Check(ItemInstance.TryCreateNew(boots, 1, AffixRarity.Uncommon, new[] { new AffixRecord(realArmorId, AffixStatKey.Strength, 3) }, impostorAuth, out ItemInstance impostorInstance, out reason) && impostorInstance != null,
                "forged instance (b): real boots, real affix id with the wrong stat key: " + reason);
            stateBefore = BagState(bag);
            Check(!bag.TryAddInstance(forgedInstance, out reason) && BagState(bag) == stateBefore, "a forged-roll instance of a real definition is refused with no state change: " + reason);
            Check(!bag.TryAddInstance(impostorInstance, out reason) && BagState(bag) == stateBefore, "a real affix id with a forged stat key is refused with no state change: " + reason);
            Check(!bag.TryAddInstanceAt(forgedInstance, new Vector2Int(7, 0), out reason) && BagState(bag) == stateBefore, "the exact-cell form refuses it too: " + reason);
            Check(!bag.TryAddInstanceAt(sideInstance, new Vector2Int(7, 0), out reason) && BagState(bag) == stateBefore, "the exact-cell form refuses a non-catalog definition: " + reason);
            Inventory bareBag = MakeBag(10, 4);
            Check(catalog.TryMintCommon(issuer, boots, out ItemInstance legit, out reason), "a legitimate catalog instance exists");
            string bareBefore = BagState(bareBag);
            Check(!bareBag.TryAddInstance(legit, out reason) && BagState(bareBag) == bareBefore, "a bag with no catalog refuses even a legitimate ready-made instance: " + reason);
            Check(bag.TryAddInstance(legit, out reason), "the catalogued bag admits the same legitimate instance: " + reason);

            // ---------------------------------------------------------------- the test seam
            Inventory testBag = MakeBag(10, 4);
            ItemCatalog testCatalog = ItemCatalog.CreateForTest(new[] { boots }, new AffixDefinition[0]);
            Cleanup.Add(testCatalog);
            Check(testCatalog.IsTestCatalog, "CreateForTest makes a test catalog");
            testBag.SetCatalog(testCatalog);
            Check(!testBag.TryAdd(adHocGear, 1, out reason, out accepted) && accepted == 0, "a test catalog still refuses what it does not list: " + reason);
            Check(testCatalog.RegisterForTest(adHocGear, out reason), "RegisterForTest accepts an ad-hoc definition on a test catalog: " + reason);
            Check(testCatalog.RegisterForTest(adHocGear, out reason) && testCatalog.ItemCount == 2, "registering the same object twice is a no-op");
            Check(testBag.TryAdd(adHocGear, 1, out reason, out accepted) && accepted == 1 && testBag.MintedInstanceCount == 1,
                "after registration the bag mints for the ad-hoc definition: " + reason);

            ItemData sameIdOtherObject = MakeItem("adhoc_blade", EquipmentSlot.Weapon, ItemKind.Weapon);
            int countBefore = testCatalog.ItemCount;
            Check(!testCatalog.RegisterForTest(sameIdOtherObject, out reason) && testCatalog.ItemCount == countBefore && reason.Contains("duplicate"),
                "two items sharing an id are rejected at registration: " + reason);
            Check(!testCatalog.TryMintCommon(issuer, sameIdOtherObject, out refused, out _) && refused == null, "the rejected twin still mints nothing");
            Check(!testCatalog.RegisterForTest(badUpper, out reason) && testCatalog.ItemCount == countBefore, "an invalid id is rejected at registration: " + reason);
            Check(!testCatalog.RegisterForTest(null, out reason), "a null definition is rejected at registration");

            int realCount = catalog.ItemCount;
            Check(!catalog.RegisterForTest(adHocGear, out reason) && catalog.ItemCount == realCount && !catalog.Contains(adHocGear),
                "the production asset catalog refuses RegisterForTest: " + reason);

            // ---------------------------------------------------------------- loot tables
            LootTable gearTable = Make<LootTable>();
            gearTable.maxRolls = 1;
            gearTable.guaranteeFirstEntry = true;
            gearTable.entries = new[] { new LootEntry { item = adHocGear, minAmount = 1, maxAmount = 1, weight = 1, maxCopies = 1 } };
            List<LootDrop> drops = new List<LootDrop>();
            ItemIdentityIssuer rollIssuer = ItemIdentityIssuer.CreateLocalTrusted();
            Check(!gearTable.TryRoll(11, drops, catalog, rollIssuer, out reason) && drops.Count == 0 && rollIssuer.MintedCount == 0,
                "a table dropping non-catalog gear refuses the whole roll and mints nothing: " + reason);
            Check(!gearTable.TryRoll(11, drops, null, rollIssuer, out reason) && drops.Count == 0 && rollIssuer.MintedCount == 0,
                "a gear table with no catalog supplied refuses and mints nothing: " + reason);

            gearTable.entries = new[] { new LootEntry { item = clonedBoots, minAmount = 1, maxAmount = 1, weight = 1, maxCopies = 1 } };
            Check(!gearTable.TryRoll(11, drops, catalog, rollIssuer, out reason) && drops.Count == 0 && rollIssuer.MintedCount == 0,
                "a table dropping a clone of a real item refuses and mints nothing: " + reason);

            AffixDefinition foreignAffix = Make<AffixDefinition>();
            foreignAffix.id = "gate_foreign_dex"; foreignAffix.statKey = AffixStatKey.Dexterity; foreignAffix.allowedCategories = AffixCategory.Boots;
            gearTable.entries = new[] { new LootEntry { item = boots, minAmount = 1, maxAmount = 1, weight = 1, maxCopies = 1, affixRarity = AffixRarity.Uncommon } };
            gearTable.affixCatalog = new[] { foreignAffix };
            Check(!gearTable.TryRoll(11, drops, catalog, rollIssuer, out reason) && drops.Count == 0 && rollIssuer.MintedCount == 0,
                "a table rolling an affix the catalog does not own refuses and mints nothing: " + reason);

            LootTable carriedTable = Make<LootTable>();
            carriedTable.maxRolls = 1;
            carriedTable.guaranteeFirstEntry = true;
            carriedTable.entries = new[] { new LootEntry { item = coin, minAmount = 2, maxAmount = 2, weight = 1, maxCopies = 1 } };
            Check(carriedTable.TryRoll(11, drops, null, rollIssuer, out reason) && drops.Count == 1 && drops[0].Instance == null && drops[0].Amount == 2,
                "a carried-goods table still needs no catalog: " + reason);

            LootTable ember = Load<LootTable>(DataFolder + "/Loot_EmberChest.asset");
            int emberGear = 0;
            for (int seed = 1; seed <= 60; seed++)
            {
                List<LootDrop> a = new List<LootDrop>(), b = new List<LootDrop>();
                bool ok = ember.TryRoll(seed, a, catalog, out reason) && ember.TryRoll(seed, b, catalog, out string reasonB);
                if (!ok) { Check(false, "real ember table refused at seed " + seed + ": " + reason); continue; }
                for (int i = 0; i < a.Count; i++)
                {
                    if (a[i].Instance == null) continue;
                    emberGear++;
                    bool sameRolls = b[i].Instance != null && b[i].Instance.AffixCount == a[i].Instance.AffixCount;
                    for (int k = 0; sameRolls && k < a[i].Instance.AffixCount; k++) sameRolls = a[i].Instance.Affixes[k] == b[i].Instance.Affixes[k];
                    if (!sameRolls) Check(false, "same seed, same rolls (ember seed " + seed + ")");
                    if (!catalog.Contains(a[i].Instance.Definition)) Check(false, "rolled instance is built from a catalog definition");
                }
            }
            Check(emberGear > 0, "the real ember table rolls gear against the real catalog");

            // ---------------------------------------------------------------- snapshots
            Emberdelve.Core.DeterministicRng rng = Emberdelve.Core.DeterministicRng.FromInt(5); // R4: project RNG (was System.Random)
            Check(LootRoller.TryRoll(shield, 1, AffixRarity.Rare, authority, rng, issuer, out ItemInstance rare, out reason) && rare != null && rare.AffixCount == 2,
                "a Rare shield rolls against the catalog authority: " + reason);

            Inventory restoreBag = MakeBag(10, 4);
            restoreBag.SetCatalog(catalog);
            ItemSnapshot rareSnap = rare.ToSnapshot();
            string json = rareSnap.ToJson();
            Check(ItemSnapshot.TryFromJson(json, out ItemSnapshot parsed, out reason), "snapshot json parses");
            Check(catalog.TryRestore(parsed, out ItemInstance restored, out reason) && restored.Uid == rare.Uid && ReferenceEquals(restored.Definition, shield) &&
                  restored.Rarity == AffixRarity.Rare && restored.AffixCount == 2 && restored.Affixes[0] == rare.Affixes[0] && restored.Affixes[1] == rare.Affixes[1],
                "a snapshot restores through the catalog with its exact uid, definition and rolls: " + reason);
            Check(restoreBag.TryAddSnapshot(parsed, out reason) && restoreBag.UsedSlots == 1 && restoreBag.MintedInstanceCount == 0 &&
                  restoreBag.Slots[0].Instance.Uid == rare.Uid,
                "the bag restores a snapshot as itself: same uid, no mint: " + reason);

            stateBefore = BagState(restoreBag);
            Check(!restoreBag.TryAddSnapshot(parsed, out reason) && BagState(restoreBag) == stateBefore, "restoring the same uid twice is refused with no state change: " + reason);

            ItemSnapshot unknownId = rareSnap.Clone();
            unknownId.uid = Guid.NewGuid().ToString("N");
            unknownId.itemId = "ghost_blade_of_nowhere";
            Check(!catalog.TryRestore(unknownId, out ItemInstance ghost, out reason) && ghost == null && reason.Contains("unknown item id"),
                "a snapshot naming an unknown item id is refused: " + reason);
            Check(!restoreBag.TryAddSnapshot(unknownId, out reason) && BagState(restoreBag) == stateBefore,
                "unknown id: the bag's state is unchanged (atomic refusal): " + reason);

            ItemSnapshot goodSecond = rareSnap.Clone();
            goodSecond.uid = Guid.NewGuid().ToString("N");
            Check(!catalog.TryRestoreAll(new[] { goodSecond, unknownId }, out List<ItemInstance> batch, out reason) && batch == null,
                "a batch holding one unknown id restores nothing at all: " + reason);
            ItemSnapshot dupUid = rareSnap.Clone();
            Check(!catalog.TryRestoreAll(new[] { rareSnap, dupUid }, out batch, out reason) && batch == null && reason.Contains("repeats"),
                "a batch repeating a uid restores nothing: " + reason);
            Check(catalog.TryRestoreAll(new[] { rareSnap, goodSecond }, out batch, out reason) && batch != null && batch.Count == 2,
                "a fully valid batch restores whole: " + reason);

            ItemSnapshot cloneId = rareSnap.Clone();
            cloneId.uid = Guid.NewGuid().ToString("N");
            cloneId.itemId = "Emberbulwark_Shield_Item";
            Check(!catalog.TryRestore(cloneId, out ghost, out reason) && ghost == null, "a malformed (case-folded) item id is refused, never matched loosely: " + reason);
            ItemSnapshot badRarity = rareSnap.Clone();
            badRarity.uid = Guid.NewGuid().ToString("N");
            badRarity.rarity = 7;
            Check(!catalog.TryRestore(badRarity, out ghost, out reason) && ghost == null, "a tampered rarity is refused: " + reason);
            ItemSnapshot badUid = rareSnap.Clone();
            badUid.uid = "not-a-uid";
            Check(!catalog.TryRestore(badUid, out ghost, out reason) && ghost == null, "a malformed uid is refused: " + reason);
            Check(!catalog.TryRestore(null, out ghost, out reason) && ghost == null, "a null snapshot is refused");

            Inventory noCatalogRestore = MakeBag(10, 4);
            Check(!noCatalogRestore.TryAddSnapshot(parsed, out reason) && noCatalogRestore.UsedSlots == 0, "a bag with no catalog cannot restore anything: " + reason);
        }
    }
}
