using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Items;
using Emberdelve.UI;

namespace Emberdelve.EditorTools
{
    /// <summary>
    /// D3a gate: the rarity ladder (Poor, Common, Uncommon, Rare, Epic, Legendary, Unique), its affix counts, stable
    /// stored ints and tier ordinals, the version 1 to 2 snapshot migration, the loot-table rarity ladder, the colours
    /// and the gear score. Edit-mode only, no scene. Every expected value is a literal written down independently of
    /// the code under test (the snapshot documents and their SHA-256 checksums were produced outside Unity).
    /// Run with -executeMethod Emberdelve.EditorTools.EmberdelveRarityLadderGate.Run.
    /// </summary>
    public static class EmberdelveRarityLadderGate
    {
        private const int SeedsPerCase = 24;

        private static int _checks;
        private static int _failures;
        private static readonly List<UnityEngine.Object> Cleanup = new List<UnityEngine.Object>();

        // ---- version 1 and version 2 documents with literal SHA-256 checksums (computed independently of the C#).
        private const string BagV1 = @"{""schemaVersion"":1,""kind"":""bag"",""containerId"":""bag"",""width"":10,""height"":6,""entries"":[{""slot"":"""",""x"":0,""y"":0,""item"":{""uid"":"""",""itemId"":""ember_coin"",""quantity"":8,""rarity"":0,""affixes"":[]}},{""slot"":"""",""x"":2,""y"":0,""item"":{""uid"":""a1b2c3d4e5f60718293a4b5c6d7e8f90"",""itemId"":""footpad_boots"",""quantity"":1,""rarity"":1,""affixes"":[{""id"":""affix_constitution"",""key"":""constitution"",""value"":2}]}},{""slot"":"""",""x"":4,""y"":0,""item"":{""uid"":""0f1e2d3c4b5a69788796a5b4c3d2e1f0"",""itemId"":""emberthread_mantle"",""quantity"":1,""rarity"":2,""affixes"":[{""id"":""affix_strength"",""key"":""strength"",""value"":2},{""id"":""affix_dexterity"",""key"":""dexterity"",""value"":3}]}}],""checksum"":""sha256:5176dcab484065b6c337d4865f3c9b08dbb7c7627396903d339b992d6c10e5d3""}";
        private const string BagV2 = @"{""schemaVersion"":2,""kind"":""bag"",""containerId"":""bag"",""width"":10,""height"":6,""entries"":[{""slot"":"""",""x"":0,""y"":0,""item"":{""uid"":"""",""itemId"":""ember_coin"",""quantity"":8,""rarity"":0,""affixes"":[]}},{""slot"":"""",""x"":2,""y"":0,""item"":{""uid"":""a1b2c3d4e5f60718293a4b5c6d7e8f90"",""itemId"":""footpad_boots"",""quantity"":1,""rarity"":1,""affixes"":[{""id"":""affix_constitution"",""key"":""constitution"",""value"":2}]}},{""slot"":"""",""x"":4,""y"":0,""item"":{""uid"":""0f1e2d3c4b5a69788796a5b4c3d2e1f0"",""itemId"":""emberthread_mantle"",""quantity"":1,""rarity"":2,""affixes"":[{""id"":""affix_strength"",""key"":""strength"",""value"":2},{""id"":""affix_dexterity"",""key"":""dexterity"",""value"":3}]}}],""checksum"":""sha256:8842d299f14637967d776d37f727f4de2ecf6ae7ba72b32533fa466959acd197""}";
        private const string EquipmentV1 = @"{""schemaVersion"":1,""kind"":""equipment"",""containerId"":""equipment"",""width"":0,""height"":0,""entries"":[{""slot"":""Legs"",""x"":0,""y"":0,""item"":{""uid"":""3c4d5e6f708192a3b4c5d6e7f8091a2b"",""itemId"":""footpad_boots"",""quantity"":1,""rarity"":0,""affixes"":[]}},{""slot"":""Back"",""x"":0,""y"":0,""item"":{""uid"":""5d6e7f8091a2b3c4d5e6f708192a3b4c"",""itemId"":""ashen_cloak"",""quantity"":1,""rarity"":0,""affixes"":[]}}],""checksum"":""sha256:02dd427b8904494bca581a1c95afd2a0f82ba01d42ae77e9b06fff658d29b9d8""}";
        private const string EquipmentV2 = @"{""schemaVersion"":2,""kind"":""equipment"",""containerId"":""equipment"",""width"":0,""height"":0,""entries"":[{""slot"":""Legs"",""x"":0,""y"":0,""item"":{""uid"":""3c4d5e6f708192a3b4c5d6e7f8091a2b"",""itemId"":""footpad_boots"",""quantity"":1,""rarity"":0,""affixes"":[]}},{""slot"":""Back"",""x"":0,""y"":0,""item"":{""uid"":""5d6e7f8091a2b3c4d5e6f708192a3b4c"",""itemId"":""ashen_cloak"",""quantity"":1,""rarity"":0,""affixes"":[]}}],""checksum"":""sha256:9540a6efad0fd5ef3c4965e83fa13e794ce508028b46510509abcfb3ff75900b""}";
        private const string LoadoutV1 = @"{""schemaVersion"":1,""bag"":{""schemaVersion"":1,""kind"":""bag"",""containerId"":""bag"",""width"":10,""height"":6,""entries"":[{""slot"":"""",""x"":0,""y"":0,""item"":{""uid"":"""",""itemId"":""ember_coin"",""quantity"":8,""rarity"":0,""affixes"":[]}},{""slot"":"""",""x"":2,""y"":0,""item"":{""uid"":""a1b2c3d4e5f60718293a4b5c6d7e8f90"",""itemId"":""footpad_boots"",""quantity"":1,""rarity"":1,""affixes"":[{""id"":""affix_constitution"",""key"":""constitution"",""value"":2}]}},{""slot"":"""",""x"":4,""y"":0,""item"":{""uid"":""0f1e2d3c4b5a69788796a5b4c3d2e1f0"",""itemId"":""emberthread_mantle"",""quantity"":1,""rarity"":2,""affixes"":[{""id"":""affix_strength"",""key"":""strength"",""value"":2},{""id"":""affix_dexterity"",""key"":""dexterity"",""value"":3}]}}],""checksum"":""sha256:5176dcab484065b6c337d4865f3c9b08dbb7c7627396903d339b992d6c10e5d3""},""equipment"":{""schemaVersion"":1,""kind"":""equipment"",""containerId"":""equipment"",""width"":0,""height"":0,""entries"":[{""slot"":""Legs"",""x"":0,""y"":0,""item"":{""uid"":""3c4d5e6f708192a3b4c5d6e7f8091a2b"",""itemId"":""footpad_boots"",""quantity"":1,""rarity"":0,""affixes"":[]}},{""slot"":""Back"",""x"":0,""y"":0,""item"":{""uid"":""5d6e7f8091a2b3c4d5e6f708192a3b4c"",""itemId"":""ashen_cloak"",""quantity"":1,""rarity"":0,""affixes"":[]}}],""checksum"":""sha256:02dd427b8904494bca581a1c95afd2a0f82ba01d42ae77e9b06fff658d29b9d8""},""checksum"":""sha256:b361d63c46e606f9ffe59de7181f07887175c47748e796fe068fa8229168056c""}";
        private const string LoadoutV2 = @"{""schemaVersion"":2,""bag"":{""schemaVersion"":2,""kind"":""bag"",""containerId"":""bag"",""width"":10,""height"":6,""entries"":[{""slot"":"""",""x"":0,""y"":0,""item"":{""uid"":"""",""itemId"":""ember_coin"",""quantity"":8,""rarity"":0,""affixes"":[]}},{""slot"":"""",""x"":2,""y"":0,""item"":{""uid"":""a1b2c3d4e5f60718293a4b5c6d7e8f90"",""itemId"":""footpad_boots"",""quantity"":1,""rarity"":1,""affixes"":[{""id"":""affix_constitution"",""key"":""constitution"",""value"":2}]}},{""slot"":"""",""x"":4,""y"":0,""item"":{""uid"":""0f1e2d3c4b5a69788796a5b4c3d2e1f0"",""itemId"":""emberthread_mantle"",""quantity"":1,""rarity"":2,""affixes"":[{""id"":""affix_strength"",""key"":""strength"",""value"":2},{""id"":""affix_dexterity"",""key"":""dexterity"",""value"":3}]}}],""checksum"":""sha256:8842d299f14637967d776d37f727f4de2ecf6ae7ba72b32533fa466959acd197""},""equipment"":{""schemaVersion"":2,""kind"":""equipment"",""containerId"":""equipment"",""width"":0,""height"":0,""entries"":[{""slot"":""Legs"",""x"":0,""y"":0,""item"":{""uid"":""3c4d5e6f708192a3b4c5d6e7f8091a2b"",""itemId"":""footpad_boots"",""quantity"":1,""rarity"":0,""affixes"":[]}},{""slot"":""Back"",""x"":0,""y"":0,""item"":{""uid"":""5d6e7f8091a2b3c4d5e6f708192a3b4c"",""itemId"":""ashen_cloak"",""quantity"":1,""rarity"":0,""affixes"":[]}}],""checksum"":""sha256:9540a6efad0fd5ef3c4965e83fa13e794ce508028b46510509abcfb3ff75900b""},""checksum"":""sha256:9aa07f251febeb2990aee33aae85c4ca387ae69a19cf52080f4fbd3555675ea6""}";
        private const string BagV2Checksum = "sha256:8842d299f14637967d776d37f727f4de2ecf6ae7ba72b32533fa466959acd197";
        // D3c: the same saves at schema version 5 (checksums again computed outside Unity, by a replica of the canonical body). The boots
        // moved from Legs to Feet in version 3, so the equipment entries are in slot-number order (Back 6, Feet 8); version 5 adds the
        // loadout's weaponSet 0 and an empty belt. A version 1, 2 or 3 save migrates to exactly these bytes (the version 3 ones are checked in the D3c gate).
        private const string BagV5 = @"{""schemaVersion"":5,""kind"":""bag"",""containerId"":""bag"",""width"":10,""height"":6,""entries"":[{""slot"":"""",""x"":0,""y"":0,""item"":{""uid"":"""",""itemId"":""ember_coin"",""quantity"":8,""rarity"":0,""affixes"":[]}},{""slot"":"""",""x"":2,""y"":0,""item"":{""uid"":""a1b2c3d4e5f60718293a4b5c6d7e8f90"",""itemId"":""footpad_boots"",""quantity"":1,""rarity"":1,""affixes"":[{""id"":""affix_constitution"",""key"":""constitution"",""value"":2}]}},{""slot"":"""",""x"":4,""y"":0,""item"":{""uid"":""0f1e2d3c4b5a69788796a5b4c3d2e1f0"",""itemId"":""emberthread_mantle"",""quantity"":1,""rarity"":2,""affixes"":[{""id"":""affix_strength"",""key"":""strength"",""value"":2},{""id"":""affix_dexterity"",""key"":""dexterity"",""value"":3}]}}],""checksum"":""sha256:ddd3334bd3c57aad8fa0f84eba1815615b3b4b368bc7114602a794b307fd5f8f""}";
        private const string BagV5Checksum = "sha256:ddd3334bd3c57aad8fa0f84eba1815615b3b4b368bc7114602a794b307fd5f8f";
        private const string EquipmentV5 = @"{""schemaVersion"":5,""kind"":""equipment"",""containerId"":""equipment"",""width"":0,""height"":0,""entries"":[{""slot"":""Back"",""x"":0,""y"":0,""item"":{""uid"":""5d6e7f8091a2b3c4d5e6f708192a3b4c"",""itemId"":""ashen_cloak"",""quantity"":1,""rarity"":0,""affixes"":[]}},{""slot"":""Feet"",""x"":0,""y"":0,""item"":{""uid"":""3c4d5e6f708192a3b4c5d6e7f8091a2b"",""itemId"":""footpad_boots"",""quantity"":1,""rarity"":0,""affixes"":[]}}],""checksum"":""sha256:41a6a15f5d595942d02538392e744d4c45c9ae123b3bc66d048fe0e9b9648d4f""}";
        private const string LoadoutV5 = @"{""schemaVersion"":5,""bag"":{""schemaVersion"":5,""kind"":""bag"",""containerId"":""bag"",""width"":10,""height"":6,""entries"":[{""slot"":"""",""x"":0,""y"":0,""item"":{""uid"":"""",""itemId"":""ember_coin"",""quantity"":8,""rarity"":0,""affixes"":[]}},{""slot"":"""",""x"":2,""y"":0,""item"":{""uid"":""a1b2c3d4e5f60718293a4b5c6d7e8f90"",""itemId"":""footpad_boots"",""quantity"":1,""rarity"":1,""affixes"":[{""id"":""affix_constitution"",""key"":""constitution"",""value"":2}]}},{""slot"":"""",""x"":4,""y"":0,""item"":{""uid"":""0f1e2d3c4b5a69788796a5b4c3d2e1f0"",""itemId"":""emberthread_mantle"",""quantity"":1,""rarity"":2,""affixes"":[{""id"":""affix_strength"",""key"":""strength"",""value"":2},{""id"":""affix_dexterity"",""key"":""dexterity"",""value"":3}]}}],""checksum"":""sha256:ddd3334bd3c57aad8fa0f84eba1815615b3b4b368bc7114602a794b307fd5f8f""},""equipment"":{""schemaVersion"":5,""kind"":""equipment"",""containerId"":""equipment"",""width"":0,""height"":0,""entries"":[{""slot"":""Back"",""x"":0,""y"":0,""item"":{""uid"":""5d6e7f8091a2b3c4d5e6f708192a3b4c"",""itemId"":""ashen_cloak"",""quantity"":1,""rarity"":0,""affixes"":[]}},{""slot"":""Feet"",""x"":0,""y"":0,""item"":{""uid"":""3c4d5e6f708192a3b4c5d6e7f8091a2b"",""itemId"":""footpad_boots"",""quantity"":1,""rarity"":0,""affixes"":[]}}],""checksum"":""sha256:41a6a15f5d595942d02538392e744d4c45c9ae123b3bc66d048fe0e9b9648d4f""},""weaponSet"":0,""belt"":["""","""","""","""","""",""""],""checksum"":""sha256:e748e3f166d8e601f4cab7fefe09f1050a6c69a4b88dffbb3086b625f4316a43""}";

        private static void Check(bool ok, string label)
        {
            _checks++;
            if (ok) return;
            _failures++;
            Debug.LogError("[RarityLadder] mismatch " + label);
        }

        private static T Make<T>() where T : ScriptableObject
        {
            T made = ScriptableObject.CreateInstance<T>();
            Cleanup.Add(made);
            return made;
        }

        /// <summary>Counts every draw, so "Poor and Common consume no randomness" is observable.</summary>
        private sealed class CountingRng : ILootRng
        {
            private readonly DeterministicRng _inner;
            public int Calls;

            public CountingRng(DeterministicRng inner) { _inner = inner; }
            public ulong NextUInt64() { Calls++; return _inner.NextUInt64(); }
            public uint NextUInt32() { Calls++; return _inner.NextUInt32(); }
            public int Next(int maxExclusive) { Calls++; return _inner.Next(maxExclusive); }
            public int Next(int minInclusive, int maxExclusive) { Calls++; return _inner.Next(minInclusive, maxExclusive); }
            public double NextDouble() { Calls++; return _inner.NextDouble(); }
        }

        public static void Run()
        {
            _checks = 0;
            _failures = 0;

            try
            {
                Execute();
            }
            catch (Exception exception)
            {
                _failures++;
                Debug.LogException(exception);
            }
            finally
            {
                foreach (UnityEngine.Object made in Cleanup) if (made != null) UnityEngine.Object.DestroyImmediate(made);
                Cleanup.Clear();
            }

            Debug.Log("[Emberdelve][RarityLadder] " + (_failures == 0 ? "PASS" : "FAIL") + " checks=" + _checks + " failures=" + _failures);
            EmberdelveGateExit.Exit(_failures == 0 ? 0 : 1);
        }

        // Literal tables, written down here and not read from the code under test.
        private static int StoredOf(AffixRarity rarity)
        {
            switch (rarity)
            {
                case AffixRarity.Common: return 0;
                case AffixRarity.Uncommon: return 1;
                case AffixRarity.Rare: return 2;
                case AffixRarity.Epic: return 3;
                case AffixRarity.Legendary: return 4;
                case AffixRarity.Unique: return 5;
                case AffixRarity.Poor: return 6;
                default: return -999;
            }
        }

        private static int OrdinalOf(AffixRarity rarity)
        {
            switch (rarity)
            {
                case AffixRarity.Poor: return 0;
                case AffixRarity.Common: return 1;
                case AffixRarity.Uncommon: return 2;
                case AffixRarity.Rare: return 3;
                case AffixRarity.Epic: return 4;
                case AffixRarity.Legendary: return 5;
                case AffixRarity.Unique: return 6;
                default: return -999;
            }
        }

        private static int AffixCountOf(AffixRarity rarity)
        {
            switch (rarity)
            {
                case AffixRarity.Poor: return 0;
                case AffixRarity.Common: return 0;
                case AffixRarity.Uncommon: return 1;
                case AffixRarity.Rare: return 2;
                case AffixRarity.Epic: return 3;
                case AffixRarity.Legendary: return 4;
                case AffixRarity.Unique: return 5;
                default: return -999;
            }
        }

        private static void Execute()
        {
            LadderTable();

            ItemCatalog catalog = AssetDatabase.LoadAssetAtPath<ItemCatalog>(EmberdelveAssetLibrary.ItemCatalogPath);
            Check(catalog != null, "the real item catalog loads");
            if (catalog == null) return;
            Check(catalog.TryGetAuthority(out ItemInstanceAuthority authority, out string reason), "the real catalog builds an authority: " + reason);
            if (authority == null) return;

            string definitionsBefore = SerializeDefinitions(catalog);

            List<ItemData> gear = new List<ItemData>();
            foreach (ItemData item in catalog.Items) if (item != null && item.IsEquippable) gear.Add(item);
            Check(gear.Count >= 5, "the catalog has gear in every family (" + gear.Count + " pieces)");

            PoolSizes(catalog, authority, gear);
            List<ItemInstance> oneOfEachTier = RollCounts(authority, gear);
            ExhaustionAndInstanceRules();
            Snapshots(catalog, authority, gear);
            LootLadder(catalog, gear);
            Colours(oneOfEachTier);
            GearScoreChecks();

            Check(SerializeDefinitions(catalog) == definitionsBefore, "no roll, restore or table touched a base item or affix definition (fixed and primary stats never change)");
        }

        // ------------------------------------------------------------------ 1. the ladder itself

        private static void LadderTable()
        {
            Check(Enum.GetValues(typeof(AffixRarity)).Length == 7, "exactly seven tiers");
            Check(Array.IndexOf(Enum.GetNames(typeof(AffixRarity)), "Artifact") < 0, "there is no Artifact tier (C7)");
            Check((int)AffixRarity.Common == 0 && (int)AffixRarity.Uncommon == 1 && (int)AffixRarity.Rare == 2 &&
                  (int)AffixRarity.Epic == 3 && (int)AffixRarity.Legendary == 4 && (int)AffixRarity.Unique == 5 &&
                  (int)AffixRarity.Poor == 6, "stored ints are stable: Common 0, Uncommon 1, Rare 2, Epic 3, Legendary 4, Unique 5, Poor 6");
            Check(AffixRarities.TierCount == 7 && AffixRarities.MaxAffixCount == 5 && AffixRarities.ByRank.Length == 7, "tier count 7, most affixes 5");

            for (int i = 0; i < AffixRarities.ByRank.Length; i++)
            {
                AffixRarity tier = AffixRarities.ByRank[i];
                Check(AffixRarities.IsKnown(tier) && AffixRarities.IsKnownValue(StoredOf(tier)), tier + " is known");
                Check(AffixRarities.Ordinal(tier) == i && OrdinalOf(tier) == i, tier + " has ordinal " + i);
                Check(AffixRarities.TryFromOrdinal(i, out AffixRarity back) && back == tier, tier + " maps back from its ordinal");
                Check(AffixRarities.TryExpectedAffixCount(tier, out int count) && count == AffixCountOf(tier), tier + " rolls " + AffixCountOf(tier) + " affixes");
                Check(AffixRarities.TryExpectedAffixCount(StoredOf(tier), out int rawCount) && rawCount == AffixCountOf(tier), tier + " raw stored int gives the same count");
                Check(AffixRarities.Name(tier) == tier.ToString(), tier + " display name");
                Check(AffixRarities.RollsAffixes(tier) == (AffixCountOf(tier) > 0), tier + " RollsAffixes");
            }

            Check(!AffixRarities.TryFromOrdinal(-1, out _) && !AffixRarities.TryFromOrdinal(7, out _), "ordinals outside 0..6 map to nothing");

            // Ordering is by ordinal; the raw int order is different (Poor is stored last), which is the whole point.
            AffixRarity[] byRawInt = (AffixRarity[])AffixRarities.ByRank.Clone();
            Array.Sort(byRawInt, (a, b) => ((int)a).CompareTo((int)b));
            Check(byRawInt[byRawInt.Length - 1] == AffixRarity.Poor && byRawInt[0] == AffixRarity.Common, "a sort by raw int puts Poor last (so raw ints must never be compared)");
            AffixRarity[] shuffled = { AffixRarity.Epic, AffixRarity.Poor, AffixRarity.Unique, AffixRarity.Common, AffixRarity.Rare, AffixRarity.Legendary, AffixRarity.Uncommon };
            Array.Sort(shuffled, AffixRarities.Compare);
            bool sorted = true;
            for (int i = 0; i < shuffled.Length; i++) if (shuffled[i] != AffixRarities.ByRank[i]) sorted = false;
            Check(sorted, "sorting with AffixRarities.Compare gives Poor, Common, Uncommon, Rare, Epic, Legendary, Unique");

            bool pairsOk = true;
            for (int i = 0; i < 7; i++)
            {
                for (int k = 0; k < 7; k++)
                {
                    int expected = i == k ? 0 : (i < k ? -1 : 1);
                    int actual = AffixRarities.Compare(AffixRarities.ByRank[i], AffixRarities.ByRank[k]);
                    int sign = actual == 0 ? 0 : (actual < 0 ? -1 : 1);
                    if (sign != expected) pairsOk = false;
                    if (AffixRarities.IsAtLeast(AffixRarities.ByRank[i], AffixRarities.ByRank[k]) != (i >= k)) pairsOk = false;
                }
            }
            Check(pairsOk, "Compare and IsAtLeast agree with the ordinal for all 49 pairs");
            Check(AffixRarities.IsAtLeast(AffixRarity.Poor, AffixRarity.Common) == false && AffixRarities.IsAtLeast(AffixRarity.Common, AffixRarity.Poor), "Poor is below Common although 6 > 0");

            AffixRarity unknown = (AffixRarity)7;
            Check(!AffixRarities.IsKnown(unknown) && !AffixRarities.IsKnownValue(7) && !AffixRarities.IsKnownValue(-1) && !AffixRarities.IsKnownValue(99), "7, -1 and 99 are not rarities");
            Check(AffixRarities.Ordinal(unknown) == -1 && !AffixRarities.TryExpectedAffixCount(unknown, out int unknownCount) && unknownCount == -1, "an unknown tier has no rank and no count (never 0)");
            Check(!AffixRarities.IsAtLeast(unknown, AffixRarity.Poor) && AffixRarities.Compare(unknown, AffixRarity.Poor) < 0 && AffixRarities.Name(unknown) == "Unknown", "an unknown tier never counts as at least Poor and sorts below it");
            Check(!AffixRarities.RollsAffixes(unknown), "an unknown tier rolls nothing");
        }

        // ------------------------------------------------------------------ 2. the affix pool is large enough

        private static void PoolSizes(ItemCatalog catalog, ItemInstanceAuthority authority, List<ItemData> gear)
        {
            Check(AffixCatalog.TryValidate(catalog.Affixes, out string catalogReason), "the real affix catalog is valid: " + catalogReason);
            AffixCategory covered = AffixCategory.None;
            foreach (ItemData item in gear)
            {
                Check(authority.TryCategory(item, out AffixCategory category, out string reason) && AffixCategories.IsSingle(category), item.itemId + " resolves to one gear family: " + reason);
                covered |= category;

                HashSet<AffixStatKey> keys = new HashSet<AffixStatKey>();
                foreach (AffixDefinition definition in catalog.Affixes) if (definition != null && definition.AllowsCategory(category)) keys.Add(definition.statKey);
                Check(keys.Count >= ItemRarityRules.MaxAffixCount,
                    item.itemId + " (" + AffixCategories.Describe(category) + ") has " + keys.Count + " distinct affix keys, enough for the 5 a Unique needs");
            }
            Check(covered == AffixCategories.All, "the catalog has gear in all five families (boots, cloak, armour, weapon, jewelry): " + AffixCategories.Describe(covered));
        }

        // ------------------------------------------------------------------ 3. counts, uniqueness, determinism

        private static string CheckRecords(AffixRecord[] records, int expected, ItemInstanceAuthority authority, AffixCategory category)
        {
            if (records == null) return "no records";
            if (records.Length != expected) return "got " + records.Length + " records, expected " + expected;

            HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
            HashSet<AffixStatKey> keys = new HashSet<AffixStatKey>();
            for (int i = 0; i < records.Length; i++)
            {
                AffixRecord record = records[i];
                if (!AffixStatKeys.IsKnown(record.StatKey)) return "record " + i + " has a key outside the allowlist";
                if (!ids.Add(record.Id)) return "duplicate affix id " + record.Id;
                if (!keys.Add(record.StatKey)) return "duplicate stat key " + record.CanonicalKey;
                if (!authority.TryResolveAffix(record.Id, out AffixDefinition definition, out string reason)) return "unknown affix " + record.Id + ": " + reason;
                if (definition.statKey != record.StatKey) return "id/key pair mismatch on " + record.Id;
                if (!definition.TryValidateRolledValue(record.Value, category, out reason)) return "value " + record.Value + " not accepted: " + reason;
            }
            return null;
        }

        private static bool SameRecords(AffixRecord[] a, IReadOnlyList<AffixRecord> b)
        {
            if (a.Length != b.Count) return false;
            for (int i = 0; i < a.Length; i++) if (!a[i].Equals(b[i])) return false;
            return true;
        }

        private static string Describe(AffixRecord[] records)
        {
            StringBuilder builder = new StringBuilder();
            for (int i = 0; i < records.Length; i++) builder.Append(records[i].Id).Append(':').Append(records[i].Value).Append(';');
            return builder.ToString();
        }

        private static List<ItemInstance> RollCounts(ItemInstanceAuthority authority, List<ItemData> gear)
        {
            ItemIdentityIssuer issuer = ItemIdentityIssuer.CreateLocalTrusted();
            List<ItemInstance> oneOfEachTier = new List<ItemInstance>();
            Dictionary<string, string> fixedByItem = new Dictionary<string, string>();
            int totalRolls = 0;

            foreach (ItemData item in gear)
            {
                authority.TryCategory(item, out AffixCategory category, out _);

                foreach (AffixRarity tier in AffixRarities.ByRank)
                {
                    int expected = AffixCountOf(tier);
                    string problem = null;
                    HashSet<string> variants = new HashSet<string>(StringComparer.Ordinal);

                    for (int seed = 0; seed < SeedsPerCase && problem == null; seed++)
                    {
                        int mixed = seed * 31 + 7;
                        CountingRng rng = new CountingRng(DeterministicRng.FromInt(mixed));
                        if (!LootRoller.TryRollAffixes(item, tier, authority, rng, out AffixRecord[] records, out string reason))
                        {
                            problem = "seed " + seed + " refused: " + reason;
                            break;
                        }

                        totalRolls++;
                        problem = CheckRecords(records, expected, authority, category);
                        if (problem != null) { problem = "seed " + seed + ": " + problem; break; }

                        if (expected == 0 && rng.Calls != 0) problem = "seed " + seed + ": a tier with no affixes consumed " + rng.Calls + " draws";
                        if (expected > 0 && rng.Calls == 0) problem = "seed " + seed + ": a rolled tier consumed no draws";

                        if (!LootRoller.TryRollAffixes(item, tier, authority, DeterministicRng.FromInt(mixed), out AffixRecord[] again, out reason) || !SameRecords(records, again))
                            problem = "seed " + seed + ": the same seed gave a different roll";

                        variants.Add(Describe(records));
                    }

                    Check(problem == null, item.itemId + " " + tier + " (" + expected + " affixes, " + SeedsPerCase + " seeds): " + problem);
                    if (expected > 0) Check(variants.Count > 1, item.itemId + " " + tier + " varies across seeds (" + variants.Count + " distinct rolls)");
                }

                // the minted path: the instance carries the tier, its stored int and exactly the rolled records
                string fixedText = null;
                foreach (AffixRarity tier in AffixRarities.ByRank)
                {
                    int mintedBefore = issuer.MintedCount;
                    bool ok = LootRoller.TryRoll(item, 1, tier, authority, DeterministicRng.FromInt(5), issuer, out ItemInstance instance, out string reason);
                    Check(ok && instance != null && issuer.MintedCount == mintedBefore + 1, item.itemId + " " + tier + " mints one instance: " + reason);
                    if (!ok || instance == null) continue;

                    Check(instance.Rarity == tier && instance.RarityValue == StoredOf(tier) && instance.AffixCount == AffixCountOf(tier) && ReferenceEquals(instance.Definition, item),
                        item.itemId + " " + tier + " instance carries its tier, stored int " + StoredOf(tier) + " and " + AffixCountOf(tier) + " records on the shared definition");

                    ItemSnapshot snapshot = instance.ToSnapshot();
                    Check(snapshot.rarity == StoredOf(tier) && snapshot.affixes.Length == AffixCountOf(tier), item.itemId + " " + tier + " snapshot stores int " + StoredOf(tier));
                    Check(ItemInstance.TryRestore(snapshot, authority, out ItemInstance restored, out reason) && restored.Uid == instance.Uid &&
                          restored.Rarity == tier && SameRecords(ToArray(instance.Affixes), restored.Affixes), item.itemId + " " + tier + " restores exactly, no reroll: " + reason);

                    ItemTooltipModel tooltip = ItemTooltip.ForInstance(instance);
                    string fixedLines = string.Join("|", tooltip.FixedLines) + "/" + tooltip.SlotName;
                    if (fixedText == null) fixedText = fixedLines;
                    else if (fixedText != fixedLines) Check(false, item.itemId + " " + tier + " fixed properties differ from another tier of the same item");
                    Check(tooltip.RolledLines.Count == AffixCountOf(tier) && tooltip.HasRolledSection == (AffixCountOf(tier) > 0), item.itemId + " " + tier + " tooltip lists " + AffixCountOf(tier) + " rolled lines");

                    if (item.equipSlot == EquipmentSlot.Feet) oneOfEachTier.Add(instance);
                }
                fixedByItem[item.itemId] = fixedText;
            }

            Check(totalRolls == gear.Count * 7 * SeedsPerCase, "every roll in the sweep ran (" + totalRolls + ")");
            Check(oneOfEachTier.Count == 7, "one boots instance per tier was kept for the colour checks");
            return oneOfEachTier;
        }

        private static AffixRecord[] ToArray(IReadOnlyList<AffixRecord> records)
        {
            AffixRecord[] copy = new AffixRecord[records.Count];
            for (int i = 0; i < copy.Length; i++) copy[i] = records[i];
            return copy;
        }

        // ------------------------------------------------------------------ 4. a pool that is too small is rejected

        private static void ExhaustionAndInstanceRules()
        {
            ItemData boots = Make<ItemData>();
            boots.itemId = "ladder_gate_boots";
            boots.kind = ItemKind.Armor;
            boots.equipSlot = EquipmentSlot.Legs;

            string[] ids = { "ladder_dex", "ladder_con", "ladder_str", "ladder_int", "ladder_wis" };
            AffixStatKey[] keys = { AffixStatKey.Dexterity, AffixStatKey.Constitution, AffixStatKey.Strength, AffixStatKey.Intelligence, AffixStatKey.Wisdom };
            AffixDefinition[] definitions = new AffixDefinition[5];
            for (int i = 0; i < 5; i++)
            {
                definitions[i] = Make<AffixDefinition>();
                definitions[i].id = ids[i];
                definitions[i].statKey = keys[i];
                definitions[i].allowedCategories = AffixCategory.Boots;
                definitions[i].weight = 1f;
            }

            AffixDefinition[] four = { definitions[0], definitions[1], definitions[2], definitions[3] };
            Check(ItemInstanceAuthority.TryCreate(new[] { boots }, four, out ItemInstanceAuthority small, out string reason), "a four-affix test authority builds: " + reason);
            Check(ItemInstanceAuthority.TryCreate(new[] { boots }, definitions, out ItemInstanceAuthority full, out reason), "a five-affix test authority builds: " + reason);

            ItemIdentityIssuer issuer = ItemIdentityIssuer.CreateLocalTrusted();
            Check(LootRoller.TryRollAffixes(boots, AffixRarity.Epic, small, DeterministicRng.FromInt(1), out AffixRecord[] epic, out reason) && epic.Length == 3, "Epic fits a pool of four");
            Check(LootRoller.TryRollAffixes(boots, AffixRarity.Legendary, small, DeterministicRng.FromInt(1), out AffixRecord[] legendary, out reason) && legendary.Length == 4, "Legendary uses all four");
            int minted = issuer.MintedCount;
            Check(!LootRoller.TryRollAffixes(boots, AffixRarity.Unique, small, DeterministicRng.FromInt(1), out AffixRecord[] none, out reason) && none == null && reason.Contains("never lowered"),
                "Unique on a pool of four is rejected and never capped to four: " + reason);
            Check(!LootRoller.TryRoll(boots, 1, AffixRarity.Unique, small, DeterministicRng.FromInt(1), issuer, out ItemInstance instance, out reason) && instance == null && issuer.MintedCount == minted,
                "and nothing is minted for it");
            Check(LootRoller.TryRoll(boots, 1, AffixRarity.Unique, full, DeterministicRng.FromInt(1), issuer, out instance, out reason) && instance.AffixCount == 5 && issuer.MintedCount == minted + 1,
                "with five definitions Unique rolls five: " + reason);

            // instance-level rules (the shared creation boundary)
            AffixRecord[] fiveRecords = ToArray(instance.Affixes);
            AffixRecord[] fourRecords = new AffixRecord[4];
            Array.Copy(fiveRecords, fourRecords, 4);
            AffixRecord[] oneRecord = { fiveRecords[0] };
            minted = issuer.MintedCount;
            Check(!issuer.TryCreateInstance(boots, 1, AffixRarity.Unique, fourRecords, full, out ItemInstance bad, out reason) && bad == null && Has(reason, "expects 5"), "Unique with four records is refused: " + reason);
            Check(!issuer.TryCreateInstance(boots, 1, AffixRarity.Legendary, fiveRecords, full, out bad, out reason) && Has(reason, "expects 4"), "Legendary with five records is refused: " + reason);
            Check(!issuer.TryCreateInstance(boots, 1, AffixRarity.Poor, oneRecord, full, out bad, out reason) && Has(reason, "expects 0"), "Poor with a record is refused: " + reason);
            Check(!issuer.TryCreateInstance(boots, 1, AffixRarity.Common, oneRecord, full, out bad, out reason) && Has(reason, "expects 0"), "Common with a record is refused: " + reason);
            Check(issuer.TryCreateInstance(boots, 1, AffixRarity.Poor, null, full, out ItemInstance poor, out reason) && poor.Rarity == AffixRarity.Poor && poor.AffixCount == 0, "Poor with none is accepted: " + reason);
            AffixRecord duplicate = fiveRecords[0];
            AffixRecord[] repeated = { duplicate, duplicate, fiveRecords[1] };
            Check(!issuer.TryCreateInstance(boots, 1, AffixRarity.Epic, repeated, full, out bad, out reason) && Has(reason, "duplicate"), "Epic with a repeated affix is refused: " + reason);
            AffixRecord[] sameKey = { fiveRecords[0], new AffixRecord(fiveRecords[1].Id, fiveRecords[0].StatKey, fiveRecords[1].Value), fiveRecords[2] };
            Check(!issuer.TryCreateInstance(boots, 1, AffixRarity.Epic, sameKey, full, out bad, out reason), "Epic with a repeated stat key (wrong id/key pair) is refused: " + reason);
            Check(!issuer.TryCreateInstance(boots, 1, (AffixRarity)7, null, full, out bad, out reason) && Has(reason, "unknown rarity"), "an unknown tier is refused at creation: " + reason);
            Check(issuer.MintedCount == minted + 1, "no refusal minted an identity (only the accepted Poor item did)");
        }

        private static bool Has(string reason, string part)
        {
            return reason != null && reason.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // ------------------------------------------------------------------ 5. snapshots

        private static void Snapshots(ItemCatalog catalog, ItemInstanceAuthority authority, List<ItemData> gear)
        {
            Check(ContainerSnapshot.CurrentSchemaVersion == 5 && ContainerSnapshot.MinSupportedSchemaVersion == 1, "schema version 5 (was 3 before D3c), version 1 still readable");

            // ---- a literal version 1 save (only Common/Uncommon/Rare existed) loads unchanged
            Check(ContainerSnapshot.TryFromJson(BagV1, out ContainerSnapshot migrated, out string reason), "the literal version 1 bag migrates: " + reason);
            Check(migrated != null && migrated.schemaVersion == 5 && migrated.ToJson() == BagV5 && migrated.checksum == BagV5Checksum,
                "migrated bag equals the literal version 5 document and its independently computed checksum (1 to 2 to 3 to 4)");
            Check(migrated != null && migrated.entries.Length == 3 && migrated.entries[0].item.itemId == "ember_coin" && migrated.entries[0].item.quantity == 8 && migrated.entries[0].item.rarity == 0 &&
                  migrated.entries[1].item.itemId == "footpad_boots" && migrated.entries[1].item.rarity == 1 && migrated.entries[1].item.affixes.Length == 1 && migrated.entries[1].item.affixes[0].value == 2f &&
                  migrated.entries[2].item.itemId == "emberthread_mantle" && migrated.entries[2].item.rarity == 2 && migrated.entries[2].item.affixes.Length == 2 &&
                  migrated.entries[2].item.affixes[0].id == "affix_strength" && migrated.entries[2].item.affixes[0].value == 2f && migrated.entries[2].item.affixes[1].id == "affix_dexterity" && migrated.entries[2].item.affixes[1].value == 3f &&
                  migrated.entries[1].item.uid == "a1b2c3d4e5f60718293a4b5c6d7e8f90" && migrated.entries[2].item.uid == "0f1e2d3c4b5a69788796a5b4c3d2e1f0",
                "every uid, tier and rolled value of the old save is exactly as written");
            Check(migrated != null && ContainerSnapshotValidator.TryValidate(migrated, catalog, null, ContainerKinds.Bag, out ValidatedContainer validated, out reason) && validated != null, "the migrated bag passes the full validator: " + reason);
            Check(ContainerSnapshot.TryFromJson(BagV2, out ContainerSnapshot bagFromV2, out reason) && bagFromV2.ToJson() == BagV5, "the literal version 2 bag migrates to the version 5 bytes: " + reason);
            Check(ContainerSnapshot.TryFromJson(BagV5, out ContainerSnapshot current, out reason) && current.ToJson() == BagV5, "the version 5 document parses as current: " + reason);
            Check(ContainerSnapshot.TryFromJson(EquipmentV1, out ContainerSnapshot gearOld, out reason) && gearOld.ToJson() == EquipmentV5 &&
                  ContainerSnapshotValidator.TryValidate(gearOld, catalog, null, ContainerKinds.Equipment, out validated, out reason), "the literal version 1 equipment migrates and validates: " + reason);
            Check(LoadoutSnapshot.TryFromJson(LoadoutV1, out LoadoutSnapshot loadout, out reason) && loadout.schemaVersion == 5 && loadout.ToJson() == LoadoutV5 && loadout.ChecksumMatches(), "the literal version 1 loadout migrates to the version 5 bytes: " + reason);

            Check(ContainerSnapshot.TryFromJson(EquipmentV2, out ContainerSnapshot gearFromV2, out reason) && gearFromV2.ToJson() == EquipmentV5 &&
                  LoadoutSnapshot.TryFromJson(LoadoutV2, out LoadoutSnapshot loadoutFromV2, out reason) && loadoutFromV2.ToJson() == LoadoutV5,
                "the literal version 2 equipment and loadout (boots in Legs) migrate to the version 5 bytes (boots in Feet, weaponSet 0, empty belt): " + reason);

            // ---- tampering with a version 1 save
            Check(!ContainerSnapshot.TryFromJson(BagV1.Replace("\"quantity\":8", "\"quantity\":7"), out _, out reason) && Has(reason, "integrity"), "an edited version 1 save fails its own checksum: " + reason);
            Check(!ContainerSnapshot.TryFromJson(BagV1.Replace("\"rarity\":2", "\"rarity\":3"), out _, out reason) && Has(reason, "integrity"), "an old save with a tier edited in is caught by the checksum first: " + reason);

            // ---- unknown tiers are rejected by the validator; known new tiers are not
            foreach (int tier in new[] { 7, 8, 99, -1, 2147483647 })
            {
                ContainerSnapshot tampered = current.Clone();
                tampered.entries[2].item.rarity = tier;
                tampered.Seal();
                Check(!ContainerSnapshotValidator.TryValidate(tampered, catalog, null, ContainerKinds.Bag, out _, out reason) && Has(reason, "not a known rarity"), "unknown tier " + tier + " is rejected: " + reason);
            }
            Check(!new ItemSnapshot { rarity = 7 }.TryGetRarity(out _, out reason) && new ItemSnapshot { rarity = 6 }.TryGetRarity(out AffixRarity six, out _) && six == AffixRarity.Poor, "ItemSnapshot.TryGetRarity: 7 is unknown, 6 is Poor");

            // ---- every new tier round-trips through a container, JSON and the validator
            ItemIdentityIssuer issuer = ItemIdentityIssuer.CreateLocalTrusted();
            ContainerSnapshot all = new ContainerSnapshot { kind = ContainerKinds.Bag, containerId = "bag", width = 30, height = 6, entries = new ContainerEntry[7] };
            List<ItemInstance> made = new List<ItemInstance>();
            for (int i = 0; i < 7; i++)
            {
                AffixRarity tier = AffixRarities.ByRank[i];
                ItemData item = gear[i % gear.Count];
                Check(LootRoller.TryRoll(item, 1, tier, authority, DeterministicRng.FromInt(100 + i), issuer, out ItemInstance instance, out reason), tier + " rolls for the container test: " + reason);
                made.Add(instance);
                all.entries[i] = new ContainerEntry { slot = "", x = i * 3, y = 0, item = instance != null ? instance.ToSnapshot() : null };
            }
            all.Seal();
            Check(ContainerSnapshotValidator.TryValidate(all, catalog, null, ContainerKinds.Bag, out validated, out reason), "a bag holding one item of each of the seven tiers validates: " + reason);
            string json = all.ToJson();
            Check(ContainerSnapshot.TryFromJson(json, out ContainerSnapshot parsed, out reason) && parsed.ToJson() == json, "and round-trips through JSON byte for byte: " + reason);
            bool allRestored = parsed != null;
            for (int i = 0; allRestored && i < 7; i++)
            {
                allRestored = ItemInstance.TryRestore(parsed.entries[i].item, authority, out ItemInstance restored, out reason) && made[i] != null &&
                              restored.Uid == made[i].Uid && restored.Rarity == AffixRarities.ByRank[i] && SameRecords(ToArray(made[i].Affixes), restored.Affixes);
            }
            Check(allRestored, "each restored item keeps its uid, tier and exact rolls: " + reason);

            // ---- the count rule holds in a snapshot too (rank order: entry 0 is Poor, entry 6 is Unique)
            if (parsed == null) return;
            ContainerSnapshot short5 = parsed.Clone();
            Check(short5.entries[0].item.rarity == 6 && short5.entries[6].item.rarity == 5 && short5.entries[6].item.affixes.Length == 5, "the container holds Poor (stored 6) first and Unique (stored 5, five affixes) last");
            short5.entries[6].item.affixes = new[] { short5.entries[6].item.affixes[0], short5.entries[6].item.affixes[1], short5.entries[6].item.affixes[2], short5.entries[6].item.affixes[3] };
            short5.Seal();
            Check(!ContainerSnapshotValidator.TryValidate(short5, catalog, null, ContainerKinds.Bag, out _, out reason) && Has(reason, "expects 5"), "a Unique entry with four affixes is rejected: " + reason);
            ContainerSnapshot poorWithAffix = parsed.Clone();
            poorWithAffix.entries[0].item.affixes = new[] { poorWithAffix.entries[6].item.affixes[0] };
            poorWithAffix.Seal();
            Check(!ContainerSnapshotValidator.TryValidate(poorWithAffix, catalog, null, ContainerKinds.Bag, out _, out reason) && Has(reason, "expects 0"), "a Poor entry carrying an affix is rejected: " + reason);
        }

        // ------------------------------------------------------------------ 6. the loot table ladder

        private static LootEntry Entry(ItemData item, int weight, int copies, int min, int max, AffixRarity rarity)
        {
            return new LootEntry { item = item, minAmount = min, maxAmount = max, weight = weight, maxCopies = copies, affixRarity = rarity };
        }

        private static ItemData Find(ItemCatalog catalog, string itemId)
        {
            foreach (ItemData item in catalog.Items) if (item != null && item.itemId == itemId) return item;
            return null;
        }

        private static string DropShape(List<LootDrop> drops)
        {
            StringBuilder builder = new StringBuilder();
            foreach (LootDrop drop in drops) builder.Append(drop.Item.itemId).Append('x').Append(drop.Amount).Append(',');
            return builder.ToString();
        }

        private static string DropRarities(List<LootDrop> drops)
        {
            StringBuilder builder = new StringBuilder();
            foreach (LootDrop drop in drops)
            {
                builder.Append(drop.Item.itemId).Append(':').Append(drop.IsRolled ? drop.Rarity.ToString() : "-");
                if (drop.IsRolled) builder.Append('[').Append(Describe(ToArray(drop.Instance.Affixes))).Append(']');
                builder.Append(',');
            }
            return builder.ToString();
        }

        private static LootTable MakeTable(ItemCatalog catalog, AffixDefinition[] affixes, RarityWeight[] ladder)
        {
            LootTable table = Make<LootTable>();
            table.tableId = "ladder_gate_table";
            table.maxRolls = 4;
            table.entries = new[]
            {
                Entry(Find(catalog, "footpad_boots"), 5, 2, 1, 1, AffixRarity.Common),
                Entry(Find(catalog, "boiled_cuirass"), 5, 2, 1, 1, AffixRarity.Common),
                Entry(Find(catalog, "ember_fang_blade"), 5, 2, 1, 1, AffixRarity.Common),
                Entry(Find(catalog, "ember_coin"), 5, 2, 1, 3, AffixRarity.Common),
            };
            table.affixCatalog = affixes;
            table.rarityLadder = ladder;
            return table;
        }

        private static RarityWeight[] Ladder(params int[] weightsByRank)
        {
            RarityWeight[] ladder = new RarityWeight[weightsByRank.Length];
            for (int i = 0; i < ladder.Length; i++) ladder[i] = new RarityWeight(AffixRarities.ByRank[i], weightsByRank[i]);
            return ladder;
        }

        private static void LootLadder(ItemCatalog catalog, List<ItemData> gear)
        {
            AffixDefinition[] affixes = new AffixDefinition[catalog.AffixCount];
            for (int i = 0; i < affixes.Length; i++) affixes[i] = catalog.Affixes[i];

            // entry-level tiers
            LootEntry poorEntry = Entry(gear[0], 1, 1, 1, 1, AffixRarity.Poor);
            Check(!poorEntry.RequestsRolledGear && Entry(gear[0], 1, 1, 1, 1, AffixRarity.Common).RequestsRolledGear == false &&
                  Entry(gear[0], 1, 1, 1, 1, AffixRarity.Uncommon).RequestsRolledGear && Entry(gear[0], 1, 1, 1, 1, AffixRarity.Epic).RequestsRolledGear &&
                  Entry(gear[0], 1, 1, 1, 1, AffixRarity.Unique).RequestsRolledGear && !Entry(gear[0], 1, 1, 1, 1, (AffixRarity)99).RequestsRolledGear,
                "Poor and Common request no affixes, Uncommon..Unique do, an unknown tier is not rolled gear");

            foreach (AffixRarity tier in AffixRarities.ByRank)
            {
                LootTable single = Make<LootTable>();
                single.tableId = "ladder_gate_single";
                single.maxRolls = 1;
                single.guaranteeFirstEntry = true;
                single.entries = new[] { Entry(Find(catalog, "ember_fang_blade"), 1, 1, 1, 1, tier) };
                single.affixCatalog = affixes;
                List<LootDrop> drops = new List<LootDrop>();
                bool ok = single.TryRollSeeded(11UL, drops, catalog, ItemIdentityIssuer.CreateLocalTrusted(), out string reason);
                Check(ok && drops.Count >= 1 && drops[0].IsRolled && drops[0].Rarity == tier && drops[0].Instance.AffixCount == AffixCountOf(tier),
                    "an entry authored at " + tier + " drops a " + tier + " blade with " + AffixCountOf(tier) + " affixes: " + reason);
            }

            // ladder: distribution, determinism, isolation
            RarityWeight[] weights = Ladder(10, 25, 25, 15, 10, 10, 5);
            LootTable table = MakeTable(catalog, affixes, weights);
            Check(table.Validate().Count == 0, "the ladder table validates: " + string.Join("; ", table.Validate()));
            Check(table.UsesRarityLadder && table.RarityLadderTotalWeight == 100 && table.RequestsRolledGear, "ladder properties: used, total 100, requests rolled gear");

            LootTable reversed = MakeTable(catalog, affixes, new[] { weights[6], weights[5], weights[4], weights[3], weights[2], weights[1], weights[0] });
            LootTable plain = MakeTable(catalog, affixes, null);

            Dictionary<AffixRarity, int> seen = new Dictionary<AffixRarity, int>();
            bool countsOk = true, deterministic = true, orderFree = true, shapeSame = true, refused = false, affixOk = true;
            int units = 0;
            for (int seed = 0; seed < 300; seed++)
            {
                ulong tableSeed = (ulong)seed * 2654435761UL + 17UL;
                List<LootDrop> a = new List<LootDrop>(), b = new List<LootDrop>(), c = new List<LootDrop>(), d = new List<LootDrop>();
                if (!table.TryRollSeeded(tableSeed, a, catalog, ItemIdentityIssuer.CreateLocalTrusted(), out string reason)) { refused = true; Debug.LogError("[RarityLadder] mismatch ladder roll refused: " + reason); break; }
                table.TryRollSeeded(tableSeed, b, catalog, ItemIdentityIssuer.CreateLocalTrusted(), out reason);
                reversed.TryRollSeeded(tableSeed, c, catalog, ItemIdentityIssuer.CreateLocalTrusted(), out reason);
                plain.TryRollSeeded(tableSeed, d, catalog, ItemIdentityIssuer.CreateLocalTrusted(), out reason);

                if (DropRarities(a) != DropRarities(b)) deterministic = false;
                if (DropRarities(a) != DropRarities(c)) orderFree = false;
                if (DropShape(a) != DropShape(d)) shapeSame = false;

                foreach (LootDrop drop in a)
                {
                    if (!drop.IsRolled) continue;
                    units++;
                    seen.TryGetValue(drop.Rarity, out int n);
                    seen[drop.Rarity] = n + 1;
                    if (drop.Instance.AffixCount != AffixCountOf(drop.Rarity)) countsOk = false;
                    HashSet<AffixStatKey> keys = new HashSet<AffixStatKey>();
                    foreach (AffixRecord record in drop.Instance.Affixes) if (!keys.Add(record.StatKey)) affixOk = false;
                }
            }
            Check(!refused, "300 seeded ladder rolls were all accepted");
            Check(units > 300, "the ladder produced gear units (" + units + ")");
            bool everyTier = true;
            foreach (AffixRarity tier in AffixRarities.ByRank) if (!seen.ContainsKey(tier) || seen[tier] < 3) everyTier = false;
            Check(everyTier, "every one of the seven tiers is produced by the ladder over 300 seeds");
            Check(countsOk && affixOk, "every ladder drop carries exactly its tier's affix count with distinct keys");
            Check(deterministic, "the same seed gives the same tiers and rolls");
            Check(orderFree, "the order the rungs were authored in does not change a seed's outcome");
            Check(shapeSame, "which items and amounts drop is identical with and without a ladder (the shape stream is untouched)");
            Check(seen.ContainsKey(AffixRarity.Common) && seen[AffixRarity.Common] > seen[AffixRarity.Unique], "weights matter: Common (25) is more frequent than Unique (5)");

            // ---- bad ladders are refused, whole, with a reason, and publish nothing
            ExpectRefusal(catalog, affixes, new[] { new RarityWeight((AffixRarity)99, 5), new RarityWeight(AffixRarity.Common, 5) }, "known rarity", "an unknown tier on a rung");
            ExpectRefusal(catalog, affixes, new[] { new RarityWeight(AffixRarity.Rare, 5), new RarityWeight(AffixRarity.Rare, 5) }, "twice", "a tier listed twice");
            ExpectRefusal(catalog, affixes, Ladder(0, 0, 0, 0, 0, 0, 0), "no positive weight", "a ladder with no weight");
            ExpectRefusal(catalog, affixes, new[] { new RarityWeight(AffixRarity.Common, LootTable.MaxLadderWeight + 1) }, "outside", "an oversized weight");
            ExpectRefusal(catalog, affixes, new[] { new RarityWeight(AffixRarity.Common, -1), new RarityWeight(AffixRarity.Rare, 4) }, "outside", "a negative weight");
            AffixDefinition[] four = { affixes[0], affixes[1], affixes[2], affixes[3] };
            ExpectRefusal(catalog, four, new[] { new RarityWeight(AffixRarity.Unique, 5) }, "never lowered", "Unique on a catalog of four");
            ExpectRefusal(catalog, new AffixDefinition[0], new[] { new RarityWeight(AffixRarity.Rare, 5) }, "affix catalog", "Rare with no affix catalog");

            LootTable carriedOnly = Make<LootTable>();
            carriedOnly.tableId = "ladder_gate_carried";
            carriedOnly.entries = new[] { Entry(Find(catalog, "ember_coin"), 1, 1, 1, 1, AffixRarity.Common) };
            carriedOnly.rarityLadder = Ladder(1, 1, 1, 1, 1, 1, 1);
            carriedOnly.affixCatalog = affixes;
            bool flagged = false;
            foreach (string problem in carriedOnly.Validate()) if (Has(problem, "drops no worn gear")) flagged = true;
            Check(flagged, "a ladder on a table that drops no gear is a validation problem");

            // a ladder that is only Poor/Common needs no affix catalog at all
            LootTable lowOnly = MakeTable(catalog, new AffixDefinition[0], Ladder(1, 1, 0, 0, 0, 0, 0));
            List<LootDrop> lowDrops = new List<LootDrop>();
            bool lowValid = lowOnly.Validate().Count == 0;
            bool lowRolled = lowOnly.TryRollSeeded(3UL, lowDrops, catalog, ItemIdentityIssuer.CreateLocalTrusted(), out string lowReason);
            Check(lowValid && lowRolled, "a Poor/Common-only ladder rolls with no affix catalog: " + lowReason);
        }

        private static void ExpectRefusal(ItemCatalog catalog, AffixDefinition[] affixes, RarityWeight[] ladder, string part, string label)
        {
            LootTable table = MakeTable(catalog, affixes, ladder);
            List<LootDrop> drops = new List<LootDrop>();
            ItemIdentityIssuer issuer = ItemIdentityIssuer.CreateLocalTrusted();
            bool ok = table.TryRollSeeded(7UL, drops, catalog, issuer, out string reason);
            Check(!ok && drops.Count == 0 && issuer.MintedCount == 0 && Has(reason, part), label + " is refused and mints nothing: " + reason);
        }

        // ------------------------------------------------------------------ 7. colours

        private static float Distance(Color a, Color b)
        {
            return Mathf.Sqrt((a.r - b.r) * (a.r - b.r) + (a.g - b.g) * (a.g - b.g) + (a.b - b.b) * (a.b - b.b));
        }

        private static void Colours(List<ItemInstance> oneOfEachTier)
        {
            Color poor = ItemTooltip.RarityColor(AffixRarity.Poor), common = ItemTooltip.RarityColor(AffixRarity.Common), uncommon = ItemTooltip.RarityColor(AffixRarity.Uncommon);
            Color rare = ItemTooltip.RarityColor(AffixRarity.Rare), epic = ItemTooltip.RarityColor(AffixRarity.Epic), legendary = ItemTooltip.RarityColor(AffixRarity.Legendary);
            Color unique = ItemTooltip.RarityColor(AffixRarity.Unique);

            Check(Mathf.Abs(poor.r - poor.g) < 0.02f && Mathf.Abs(poor.g - poor.b) < 0.02f && poor.r > 0.4f && poor.r < 0.7f, "Poor is grey");
            Check(common.r >= 0.95f && common.g >= 0.95f && common.b >= 0.95f, "Common is white");
            Check(uncommon.g > uncommon.r + 0.3f && uncommon.g > uncommon.b + 0.3f, "Uncommon is green");
            Check(rare.b > rare.r + 0.3f && rare.b > rare.g + 0.2f, "Rare is blue");
            Check(epic.r > epic.g + 0.2f && epic.b > epic.g + 0.3f && epic.b > epic.r, "Epic is purple");
            Check(legendary.r > 0.9f && legendary.g > 0.4f && legendary.g < 0.7f && legendary.b < 0.3f, "Legendary is orange");
            Check(unique.r > 0.9f && unique.g > 0.75f && unique.b < 0.4f && unique.g > legendary.g + 0.1f, "Unique is gold");

            Color[] all = { poor, common, uncommon, rare, epic, legendary, unique };
            bool distinct = true;
            for (int i = 0; i < all.Length; i++) for (int k = i + 1; k < all.Length; k++) if (Distance(all[i], all[k]) < 0.1f) distinct = false;
            Check(distinct, "all seven colours are pairwise distinct");
            Check(ItemTooltip.RarityColor((AffixRarity)7) == ItemTooltip.NeutralColor && ItemTooltip.RarityName((AffixRarity)7) == "Unknown", "an unknown tier is painted neutral, never as a real tier");

            Color panel = new Color(0.055f, 0.05f, 0.045f, 1f);
            bool readable = true;
            foreach (Color c in all) if (ItemTooltip.ContrastRatio(c, panel) < 4.5f) readable = false;
            Check(readable, "every tier colour is readable on the panel (contrast >= 4.5)");

            Check(oneOfEachTier.Count == 7, "seven tooltips to check");
            foreach (ItemInstance instance in oneOfEachTier)
            {
                ItemTooltipModel model = ItemTooltip.ForInstance(instance);
                Check(model.RarityName == instance.Rarity.ToString() && model.RarityColor == ItemTooltip.RarityColor(instance.Rarity) && model.ToPlainText().Contains("Rarity: " + instance.Rarity),
                    instance.Rarity + " tooltip names its tier and carries its colour");
            }
        }

        // ------------------------------------------------------------------ 8. gear score

        private static int Score(int[] rarities, int[] weights)
        {
            return GearScoreRules.TryTotal(rarities, weights, out int total, out _) ? total : -1;
        }

        private static void GearScoreChecks()
        {
            int[] poorToUnique = { 1, 2, 3, 5, 8, 13, 21 };
            for (int i = 0; i < 7; i++)
            {
                Check(GearScoreRules.TryPoints(StoredOf(AffixRarities.ByRank[i]), out int points) && points == poorToUnique[i], AffixRarities.ByRank[i] + " is worth " + poorToUnique[i] + " points");
            }
            Check(!GearScoreRules.TryPoints(7, out int none) && none == 0 && !GearScoreRules.TryPoints(-1, out none), "an unknown tier has no points");

            Check(GearScore.SlotWeight(EquipmentSlot.Weapon) == 2 && GearScore.SlotWeight(EquipmentSlot.OffHand) == 1 && GearScore.SlotWeight(EquipmentSlot.Head) == 1 &&
                  GearScore.SlotWeight(EquipmentSlot.Chest) == 1 && GearScore.SlotWeight(EquipmentSlot.Legs) == 1 && GearScore.SlotWeight(EquipmentSlot.Back) == 1 &&
                  GearScore.SlotWeight(EquipmentSlot.None) == 0, "slot weights: weapon 2, every other worn slot 1, None 0");

            // slot order here is Weapon, OffHand, Head, Chest, Legs, Back
            int[] weights = { 2, 1, 1, 1, 1, 1 };
            int e = GearScoreRules.EmptySlot;
            Check(Score(new[] { e, e, e, e, e, e }, weights) == 0, "nothing worn scores 0");
            Check(Score(new[] { 0, 0, 0, 0, 0, 0 }, weights) == 14, "six Common pieces score 14 (2*2 + 5*2)");
            Check(Score(new[] { 6, 6, 6, 6, 6, 6 }, weights) == 7, "six Poor pieces score 7 - more than nothing, less than Common");
            Check(Score(new[] { 5, 5, 5, 5, 5, 5 }, weights) == 147, "six Unique pieces score 147 (2*21 + 5*21)");
            Check(Score(new[] { 2, e, 6, 3, 1, 4 }, weights) == 35, "Rare weapon 10 + Poor head 1 + Epic chest 8 + Uncommon legs 3 + Legendary back 13 = 35");
            Check(Score(new[] { 2, e, e, e, e, e }, weights) == 10 && Score(new[] { e, 2, e, e, e, e }, weights) == 5, "the same Rare piece is worth 10 as the weapon and 5 off hand");
            Check(Score(new[] { 7, 0, 0, 0, 0, 0 }, weights) == -1 && Score(new[] { 0, 0 }, new[] { 1 }) == -1 && Score(new[] { 0 }, new[] { 0 }) == -1 && Score(new[] { 0 }, new[] { 11 }) == -1,
                "an unknown tier, a length mismatch or a bad weight refuses the whole score (no partial total)");

            // lobby display
            GameObject canvasObject = new GameObject("RarityLadderGate_Canvas");
            try
            {
                Canvas canvas = canvasObject.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                RunResult result = new RunResult(RunState.Extracted, "Warrior", 12f, 3, 2, 60f, 0, 21);
                Check(result.GearScore == 21 && new RunResult(RunState.Dead, "Archer", 0f, 0, 0, 1f).GearScore == 0, "RunResult carries the gear score and defaults to 0");
                new LobbyScreenView().Build(canvas, new CharacterClassData[0], null, null, result, true);
                Text label = null;
                foreach (Text text in canvasObject.GetComponentsInChildren<Text>(true)) if (text.name == "GearScore") label = text;
                Check(label != null && label.text == "Gear score  21", "the lobby shows the last run's gear score: " + (label != null ? label.text : "no label"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(canvasObject);
            }

            GameObject emptyCanvas = new GameObject("RarityLadderGate_Canvas2");
            try
            {
                Canvas canvas = emptyCanvas.AddComponent<Canvas>();
                new LobbyScreenView().Build(canvas, new CharacterClassData[0], null, null, default, false);
                Text label = null;
                foreach (Text text in emptyCanvas.GetComponentsInChildren<Text>(true)) if (text.name == "GearScore") label = text;
                Check(label != null && label.text == "Gear score  0", "before any run the lobby shows 0");
                Check(GearScore.ForEquipment(null) == 0 && GearScore.Label(35) == "Gear score  35", "GearScore.ForEquipment(null) is 0 and the label is 'Gear score  N'");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(emptyCanvas);
            }
        }

        // ------------------------------------------------------------------ helpers

        private static string SerializeDefinitions(ItemCatalog catalog)
        {
            StringBuilder builder = new StringBuilder();
            foreach (ItemData item in catalog.Items) if (item != null) builder.Append(EditorJsonUtility.ToJson(item)).Append('\n');
            foreach (AffixDefinition affix in catalog.Affixes) if (affix != null) builder.Append(EditorJsonUtility.ToJson(affix)).Append('\n');
            return builder.ToString();
        }
    }
}
