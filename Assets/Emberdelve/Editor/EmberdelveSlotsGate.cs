using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using Emberdelve.Combat;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Items;
using Emberdelve.Player;
using Emberdelve.Stats;
using Emberdelve.UI;

namespace Emberdelve.EditorTools
{
    /// <summary>
    /// D3b gate: the five new equipment slots (Hands, Feet, Necklace, RingLeft, RingRight), boots moved to Feet, schema 3,
    /// the jewelry affix family, atomic ring swaps through the sequenced owner-bound authority, and gear score.
    ///
    /// One batch entry in two phases that share one final line. Phase 1 (edit mode, no scene): the real builder runs twice
    /// (idempotent, byte-identical), then every pure rule is checked against literals written here: slot integers, the
    /// literal version 2 save with the boots in Legs migrating to Feet (now through version 4), unknown slot ints and names rejected,
    /// item and affix assets, the resolver, the gear score arithmetic. Phase 2 (play mode on the real level scene) is needed
    /// only because the command authority is bound to a live rig: equip/unequip cycles per slot with no stat accumulation,
    /// wrong-slot refusals, ring placement, the atomic ring swap and its refusals, snapshots and the character pane.
    ///
    /// Batch entry: Emberdelve.EditorTools.EmberdelveSlotsGate.Run
    /// Final line: [Emberdelve][Slots] PASS|FAIL checks=N failures=M, then EmberdelveGateExit.Exit (the only exit).
    /// </summary>
    [InitializeOnLoad]
    public static class EmberdelveSlotsGate
    {
        private const string Pending = "Emberdelve.Slots.Pending";
        internal const string ChecksKey = "Emberdelve.Slots.Checks";
        internal const string FailuresKey = "Emberdelve.Slots.Failures";

        // The literal version 2 save (D3a): the boots are worn in Legs. Checksum computed outside Unity.
        internal const string EquipmentV2 = @"{""schemaVersion"":2,""kind"":""equipment"",""containerId"":""equipment"",""width"":0,""height"":0,""entries"":[{""slot"":""Legs"",""x"":0,""y"":0,""item"":{""uid"":""3c4d5e6f708192a3b4c5d6e7f8091a2b"",""itemId"":""footpad_boots"",""quantity"":1,""rarity"":0,""affixes"":[]}},{""slot"":""Back"",""x"":0,""y"":0,""item"":{""uid"":""5d6e7f8091a2b3c4d5e6f708192a3b4c"",""itemId"":""ashen_cloak"",""quantity"":1,""rarity"":0,""affixes"":[]}}],""checksum"":""sha256:9540a6efad0fd5ef3c4965e83fa13e794ce508028b46510509abcfb3ff75900b""}";
        // The same save at version 4 (D3c; was version 3 before): boots in Feet, entries in slot-number order (Back 6, Feet 8). Checksum computed outside Unity.
        internal const string EquipmentV4 = @"{""schemaVersion"":4,""kind"":""equipment"",""containerId"":""equipment"",""width"":0,""height"":0,""entries"":[{""slot"":""Back"",""x"":0,""y"":0,""item"":{""uid"":""5d6e7f8091a2b3c4d5e6f708192a3b4c"",""itemId"":""ashen_cloak"",""quantity"":1,""rarity"":0,""affixes"":[]}},{""slot"":""Feet"",""x"":0,""y"":0,""item"":{""uid"":""3c4d5e6f708192a3b4c5d6e7f8091a2b"",""itemId"":""footpad_boots"",""quantity"":1,""rarity"":0,""affixes"":[]}}],""checksum"":""sha256:cb0c2f7b21b3e701d48760d08a91580224ee3562ded63936fe3a94ebe8db5c46""}";
        internal const string EquipmentV4Checksum = "sha256:cb0c2f7b21b3e701d48760d08a91580224ee3562ded63936fe3a94ebe8db5c46";

        private static int _n, _fail;
        private static readonly List<UnityEngine.Object> Cleanup = new List<UnityEngine.Object>();

        static EmberdelveSlotsGate()
        {
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.EnteredPlayMode && SessionState.GetBool(Pending, false))
                {
                    SessionState.SetBool(Pending, false);
                    new GameObject("Slots_Gate").AddComponent<SlotsHarness>();
                }
            };
        }

        private static void Check(bool ok, string text)
        {
            _n++;
            if (ok) return;
            _fail++;
            Debug.LogError("[Slots] mismatch " + text);
        }

        public static void Run()
        {
            _n = 0;
            _fail = 0;
            try { PureAndBuilderPhase(); }
            catch (Exception e) { _fail++; Debug.LogException(e); }
            finally
            {
                foreach (UnityEngine.Object made in Cleanup) if (made != null) UnityEngine.Object.DestroyImmediate(made);
                Cleanup.Clear();
            }

            SessionState.SetInt(ChecksKey, _n);
            SessionState.SetInt(FailuresKey, _fail);
            SessionState.SetBool(Pending, true);
            EditorSceneManager.OpenScene("Assets/Emberdelve/Scenes/Emberdelve_Level01.unity");
            EditorApplication.EnterPlaymode();
        }

        private static Dictionary<string, string> HashData()
        {
            Dictionary<string, string> map = new Dictionary<string, string>();
            using (SHA1 sha = SHA1.Create())
            {
                foreach (string file in Directory.GetFiles(EmberdelvePaths.Data, "*", SearchOption.AllDirectories))
                {
                    if (!file.EndsWith(".asset") && !file.EndsWith(".meta")) continue;
                    map[file.Replace('\\', '/')] = BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(file)));
                }
            }
            return map;
        }

        private static ItemData LoadItem(string file)
        {
            return AssetDatabase.LoadAssetAtPath<ItemData>(EmberdelvePaths.Data + "/" + file);
        }

        // ------------------------------------------------------------------ phase 1

        private static void PureAndBuilderPhase()
        {
            // ---- the builder is idempotent and creates the new assets exactly once
            EmberdelveBootstrapMenu.BuildDataOnly();
            AssetDatabase.SaveAssets();
            Dictionary<string, string> first = HashData();
            string glovesGuid = AssetDatabase.AssetPathToGUID(EmberdelvePaths.Data + "/Item_GraspwrapGloves.asset");
            EmberdelveBootstrapMenu.BuildDataOnly();
            AssetDatabase.SaveAssets();
            Dictionary<string, string> second = HashData();
            Check(!string.IsNullOrEmpty(glovesGuid) && AssetDatabase.AssetPathToGUID(EmberdelvePaths.Data + "/Item_GraspwrapGloves.asset") == glovesGuid, "gloves asset exists with a stable GUID across builds");
            Check(first.Count == second.Count, "the second build created or removed no data file (" + first.Count + " -> " + second.Count + ")");
            int differing = 0;
            foreach (KeyValuePair<string, string> pair in first)
            {
                if (!second.TryGetValue(pair.Key, out string hash) || hash != pair.Value)
                {
                    differing++;
                    Debug.LogError("[Slots] data file changed on the second build: " + pair.Key);
                }
            }
            Check(differing == 0, "the second build left every data asset and meta byte-identical");

            // ---- slot integers are append-only
            Check((int)EquipmentSlot.None == 0 && (int)EquipmentSlot.Weapon == 1 && (int)EquipmentSlot.OffHand == 2 && (int)EquipmentSlot.Head == 3 &&
                  (int)EquipmentSlot.Chest == 4 && (int)EquipmentSlot.Legs == 5 && (int)EquipmentSlot.Back == 6, "the original slot integers are unchanged");
            Check((int)EquipmentSlot.Hands == 7 && (int)EquipmentSlot.Feet == 8 && (int)EquipmentSlot.Necklace == 9 &&
                  (int)EquipmentSlot.RingLeft == 10 && (int)EquipmentSlot.RingRight == 11, "Hands 7, Feet 8, Necklace 9, RingLeft 10, RingRight 11");
            Check(Equipment.SlotCount == 14 && Enum.GetValues(typeof(EquipmentSlot)).Length == 14, "fourteen enum values, slot arrays of 14 (12 before D3c appended AltWeapon 12 and AltOffHand 13)");
            Check((int)EquipmentSlot.AltWeapon == 12 && (int)EquipmentSlot.AltOffHand == 13, "AltWeapon 12, AltOffHand 13");
            Check((int)AffixCategory.Jewelry == 16 && (int)AffixCategories.All == 31, "the jewelry category bit is 16 and the known set is 31");

            // ---- unknown slot integers
            EquipmentSlot unknown = (EquipmentSlot)14;
            Check(!EquipmentSlots.IsRealSlot(unknown) && !EquipmentSlots.IsRealSlot((EquipmentSlot)(-1)) && !EquipmentSlots.IsRealSlot(EquipmentSlot.None), "14, -1 and None are not real slots (12 and 13 became AltWeapon and AltOffHand in D3c)");
            Check(!EquipmentSlots.Accepts(unknown, unknown) && !EquipmentSlots.Accepts(EquipmentSlot.RingLeft, unknown) && !EquipmentSlots.Accepts(EquipmentSlot.None, EquipmentSlot.None), "an unknown slot accepts and fits nothing");
            ItemData probe = ScriptableObject.CreateInstance<ItemData>();
            Cleanup.Add(probe);
            probe.itemId = "slots_gate_probe";
            probe.equipSlot = unknown;
            Check(!ItemCategoryRules.TryFromEquipmentSlot(probe, out AffixCategory unknownCategory, out string unknownReason) && unknownCategory == AffixCategory.None && unknownReason.Contains("not a slot this project has"),
                "an item authored for slot 14 resolves to no category: " + unknownReason);

            // ---- the accept matrix
            Check(EquipmentSlots.Accepts(EquipmentSlot.RingLeft, EquipmentSlot.RingLeft) && EquipmentSlots.Accepts(EquipmentSlot.RingLeft, EquipmentSlot.RingRight) &&
                  EquipmentSlots.Accepts(EquipmentSlot.RingRight, EquipmentSlot.RingLeft), "a ring fits either ring slot");
            Check(!EquipmentSlots.Accepts(EquipmentSlot.Necklace, EquipmentSlot.RingLeft) && !EquipmentSlots.Accepts(EquipmentSlot.RingLeft, EquipmentSlot.Necklace) &&
                  !EquipmentSlots.Accepts(EquipmentSlot.Feet, EquipmentSlot.Hands) && !EquipmentSlots.Accepts(EquipmentSlot.Hands, EquipmentSlot.Feet) &&
                  !EquipmentSlots.Accepts(EquipmentSlot.Feet, EquipmentSlot.Legs) && !EquipmentSlots.Accepts(EquipmentSlot.Legs, EquipmentSlot.Feet), "no other pair of slots is interchangeable");
            Check(EquipmentSlots.OtherRing(EquipmentSlot.RingLeft) == EquipmentSlot.RingRight && EquipmentSlots.OtherRing(EquipmentSlot.RingRight) == EquipmentSlot.RingLeft &&
                  EquipmentSlots.OtherRing(EquipmentSlot.Necklace) == EquipmentSlot.None, "the other ring slot");

            // ---- resolver: Hands armour, Feet boots, Necklace and rings jewelry
            string[] reasons = new string[5];
            ItemData[] slotProbes = new ItemData[5];
            EquipmentSlot[] newSlots = { EquipmentSlot.Hands, EquipmentSlot.Feet, EquipmentSlot.Necklace, EquipmentSlot.RingLeft, EquipmentSlot.RingRight };
            AffixCategory[] wanted = { AffixCategory.Armor, AffixCategory.Boots, AffixCategory.Jewelry, AffixCategory.Jewelry, AffixCategory.Jewelry };
            for (int i = 0; i < newSlots.Length; i++)
            {
                slotProbes[i] = ScriptableObject.CreateInstance<ItemData>();
                Cleanup.Add(slotProbes[i]);
                slotProbes[i].itemId = "slots_gate_probe_" + i;
                slotProbes[i].equipSlot = newSlots[i];
                Check(ItemCategoryRules.TryFromEquipmentSlot(slotProbes[i], out AffixCategory category, out reasons[i]) && category == wanted[i], newSlots[i] + " resolves to " + wanted[i] + ": " + reasons[i]);
            }

            // ---- authored assets
            ItemData boots = LoadItem("Item_FootpadBoots.asset"), gloves = LoadItem("Item_GraspwrapGloves.asset"),
                     necklace = LoadItem("Item_EmberbeadNecklace.asset"), ring = LoadItem("Item_CinderBandRing.asset");
            Check(boots != null && gloves != null && necklace != null && ring != null, "boots, gloves, necklace and ring load");
            if (boots == null || gloves == null || necklace == null || ring == null) return;
            Check(boots.equipSlot == EquipmentSlot.Feet && boots.itemId == "footpad_boots" && boots.gridFootprint == new Vector2Int(2, 2), "Footpad Boots author Feet (was Legs) and are otherwise unchanged");
            Check(gloves.equipSlot == EquipmentSlot.Hands && gloves.maxStack == 1 && gloves.StatTotal(SkillStat.Armor) > 0f, "gloves are single Hands armour with a small fixed armour rating");
            Check(necklace.equipSlot == EquipmentSlot.Necklace && ring.equipSlot == EquipmentSlot.RingLeft && necklace.maxStack == 1 && ring.maxStack == 1, "necklace and ring are single items; the ring authors RingLeft");
            Check(necklace.StatTotal(SkillStat.Armor) == 0f && ring.StatTotal(SkillStat.Armor) == 0f && !necklace.HasAttributeBonuses && !ring.HasAttributeBonuses, "jewelry carries no armour and no fixed bonus");
            Check(necklace.EffectiveFootprint.x <= 10 && ring.EffectiveFootprint == Vector2Int.one && gloves.EffectiveFootprint.x <= 10, "the new items fit the bag grid");

            // ---- affix eligibility
            string[] general = { "Affix_Strength", "Affix_Dexterity", "Affix_Constitution", "Affix_Intelligence", "Affix_Wisdom", "Affix_Charisma", "Affix_MaxHealth", "Affix_MaxMana", "Affix_ManaRegen" };
            string[] notJewelry = { "Affix_Armor", "Affix_MoveSpeed", "Affix_MeleeDamage", "Affix_ProjectileDamage" };
            foreach (string name in general)
            {
                AffixDefinition a = AssetDatabase.LoadAssetAtPath<AffixDefinition>(EmberdelveAssetLibrary.AffixFolder + "/" + name + ".asset");
                Check(a != null && a.AllowsCategory(AffixCategory.Jewelry) && a.AllowsCategory(AffixCategory.Weapon) && a.AllowsCategory(AffixCategory.Cloak) && (int)a.allowedCategories == 31, name + " rolls on jewelry and still on every earlier family (31)");
            }
            foreach (string name in notJewelry)
            {
                AffixDefinition a = AssetDatabase.LoadAssetAtPath<AffixDefinition>(EmberdelveAssetLibrary.AffixFolder + "/" + name + ".asset");
                Check(a != null && !a.AllowsCategory(AffixCategory.Jewelry), name + " does not roll on jewelry");
            }

            ItemCatalog catalog = AssetDatabase.LoadAssetAtPath<ItemCatalog>(EmberdelveAssetLibrary.ItemCatalogPath);
            Check(catalog != null && catalog.TryGetAuthority(out ItemInstanceAuthority authority, out string authorityReason), "the real catalog builds an authority");
            if (catalog == null || !catalog.TryGetAuthority(out authority, out authorityReason)) return;
            Check(catalog.Contains(gloves) && catalog.Contains(necklace) && catalog.Contains(ring), "the catalog lists the three new items");
            foreach (ItemData jewelry in new[] { necklace, ring })
            {
                HashSet<AffixStatKey> keys = new HashSet<AffixStatKey>();
                foreach (AffixDefinition d in catalog.Affixes) if (d != null && d.AllowsCategory(AffixCategory.Jewelry)) keys.Add(d.statKey);
                Check(keys.Count == 9, jewelry.itemId + " has 9 distinct eligible affix keys (>= 5, enough for a Unique): " + keys.Count);
                for (int i = 0; i < 5; i++)
                {
                    AffixRarity tier = AffixRarities.ByRank[i + 1];
                    Check(LootRoller.TryRoll(jewelry, 1, tier, authority, DeterministicRng.FromInt(40 + i), ItemIdentityIssuer.CreateLocalTrusted(), out ItemInstance rolled, out string rollReason) && rolled.Category == AffixCategory.Jewelry,
                        jewelry.itemId + " rolls as " + tier + " in the Jewelry family: " + rollReason);
                }
            }

            // ---- the literal version 2 save migrates: boots Legs -> Feet
            Check(ContainerSnapshot.TryFromJson(EquipmentV2, out ContainerSnapshot migrated, out string reason), "the literal version 2 equipment parses: " + reason);
            Check(migrated != null && migrated.schemaVersion == 4 && migrated.ToJson() == EquipmentV4 && migrated.checksum == EquipmentV4Checksum,
                "it migrates (2 to 3 to 4) to the literal version 4 document with its independently computed checksum");
            Check(migrated != null && migrated.entries.Length == 2 && migrated.entries[0].slot == "Back" && migrated.entries[1].slot == "Feet" &&
                  migrated.entries[1].item.itemId == "footpad_boots" && migrated.entries[1].item.uid == "3c4d5e6f708192a3b4c5d6e7f8091a2b", "boots are in Feet with the same uid; no Legs entry remains");
            Check(migrated != null && ContainerSnapshotValidator.TryValidate(migrated, catalog, null, ContainerKinds.Equipment, out ValidatedContainer validated, out reason) && validated != null,
                "the migrated equipment passes the full validator against the real catalog: " + reason);
            Check(ContainerSnapshot.TryFromJson(EquipmentV4, out ContainerSnapshot v3, out reason) && v3.ToJson() == EquipmentV4, "the version 4 document parses as current: " + reason);

            // ---- unknown slot names and wrong slots are rejected by the validator
            foreach (string bad in new[] { "12", "Ring", "RingMiddle", "ringleft", "Hand", "" })
            {
                ContainerSnapshot forged = v3.Clone();
                forged.entries[1].slot = bad;
                forged.Seal();
                Check(!ContainerSnapshotValidator.TryValidate(forged, catalog, null, ContainerKinds.Equipment, out _, out reason) && reason.Contains("not an equipment slot name"), "slot name '" + bad + "' is rejected: " + reason);
            }
            foreach (string wrong in new[] { "Hands", "Legs", "Necklace", "RingLeft", "RingRight" })
            {
                ContainerSnapshot forged = v3.Clone();
                forged.entries[1].slot = wrong;
                forged.Seal();
                Check(!ContainerSnapshotValidator.TryValidate(forged, catalog, null, ContainerKinds.Equipment, out _, out reason) && reason.Contains("belongs in slot"), "the boots in " + wrong + " are rejected: " + reason);
            }
            ContainerSnapshot olderLegs = v3.Clone();
            olderLegs.entries[1].slot = "Legs";
            olderLegs.Seal();
            Check(!ContainerSnapshotValidator.TryValidate(olderLegs, catalog, null, ContainerKinds.Equipment, out _, out reason) && reason.Contains("belongs in slot"), "a current document with the boots in Legs is no longer valid: " + reason);

            // ---- gear score: weights and arithmetic
            foreach (EquipmentSlot slot in newSlots) Check(GearScore.SlotWeight(slot) == 1, slot + " weighs 1");
            Check(GearScore.SlotWeight(unknown) == 0 && GearScore.SlotWeight(EquipmentSlot.None) == 0 && GearScore.SlotWeight(EquipmentSlot.Weapon) == 2, "an unknown slot weighs 0, None 0, the weapon still 2");
            int total = 0;
            foreach (EquipmentSlot slot in Enum.GetValues(typeof(EquipmentSlot))) total += GearScore.SlotWeight(slot);
            Check(total == 15, "all authored slots include both weapon sets (two main hands weigh 2 each)");
            int activeWeight = 0;
            foreach (EquipmentSlot slot in Enum.GetValues(typeof(EquipmentSlot)))
                if (EquipmentSlots.SetOf(slot) != 1) activeWeight += GearScore.SlotWeight(slot);
            Check(activeWeight == 12, "one active full kit retains twelve points of weight (weapon 2 + ten slots)");
            int[] commons = new int[11];
            int[] weights = { 2, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1 };
            Check(GearScoreRules.TryTotal(commons, weights, out int score, out reason) && score == 24, "eleven Common pieces score 24 (2*2 + 10*2): " + reason);
            int[] rares = { 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2 };
            Check(GearScoreRules.TryTotal(rares, weights, out score, out reason) && score == 60, "eleven Rare pieces score 60 (2*5 + 10*5): " + reason);

            // ---- the command: an unknown slot is not even a legal intent
            Check(Enum.GetNames(typeof(InventoryCommandKind)).Length == 8 && (int)InventoryCommandKind.SwapWeaponSet == 7 && (int)InventoryCommandKind.SwapEquipment == 6 && (int)InventoryCommandKind.Use == 5, "SwapEquipment is appended as 6 after Use 5");
        }

        // The harness reads the phase-1 totals and finishes the single final line.
        internal static void Finish(int checks, int failures)
        {
            Debug.Log("[Emberdelve][Slots] " + (failures == 0 ? "PASS" : "FAIL") + " checks=" + checks + " failures=" + failures);
            EmberdelveGateExit.Exit(failures == 0 ? 0 : 1);
        }
    }

    public sealed class SlotsHarness : MonoBehaviour
    {
        private int _checks;
        private int _failures;
        private PlayerRig _rig;
        private Inventory _bag;
        private Equipment _gear;
        private IInventoryAuthority _authority;
        private ItemCatalog _catalog;
        private ItemInstanceAuthority _items;
        private int _serial;

        private void Check(bool ok, string label)
        {
            _checks++;
            if (ok) return;
            _failures++;
            Debug.LogError("[Slots] mismatch " + label);
        }

        private IEnumerator Start()
        {
            for (int i = 0; i < 10; i++) yield return null;
            try { Execute(); }
            catch (Exception e) { _failures++; Debug.LogException(e); }

            int checks = _checks + SessionState.GetInt(EmberdelveSlotsGate.ChecksKey, 0);
            int failures = _failures + SessionState.GetInt(EmberdelveSlotsGate.FailuresKey, 0);
            EmberdelveSlotsGate.Finish(checks, failures);
        }

        private ItemData Resolve(string id)
        {
            bool ok = _catalog.TryResolveItem(id, out ItemData item, out string reason);
            Check(ok && item != null, "catalog resolves " + id + ": " + reason);
            return item;
        }

        /// <summary>One worn-gear instance with exactly one rolled affix (Uncommon), added to the bag.</summary>
        private ItemInstance Mint(ItemData item, AffixStatKey key, string affixId, float value)
        {
            AffixRecord[] records = { new AffixRecord(affixId, key, value) };
            bool made = ItemInstance.TryCreateNew(item, 1, AffixRarity.Uncommon, records, _items, out ItemInstance instance, out string reason);
            Check(made && instance != null, "mint " + item.itemId + " with " + affixId + "=" + value + ": " + reason);
            if (!made || instance == null) return null;
            bool added = _bag.TryAddInstance(instance, out reason);
            Check(added, "the " + item.itemId + " enters the bag: " + reason);
            return added ? instance : null;
        }

        private string State()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append("units=").Append(_bag.TotalUnits).Append(" hp=").Append(_rig.MaxHealth).Append(" gs=").Append(GearScore.ForEquipment(_gear)).Append(" gear{");
            for (int s = 1; s < Equipment.SlotCount; s++)
            {
                ItemInstance worn = _gear.EquippedInstance((EquipmentSlot)s);
                sb.Append((EquipmentSlot)s).Append('=').Append(worn != null ? worn.UidText : "-").Append(';');
            }
            sb.Append("} bag{");
            for (int i = 0; i < _bag.Slots.Count; i++)
            {
                ItemInstance held = _bag.InstanceAt(i);
                sb.Append(held != null ? held.UidText : _bag.Slots[i].Item.itemId).Append(';');
            }
            return sb.Append('}').ToString();
        }

        private Guid WornUid(EquipmentSlot slot)
        {
            ItemInstance worn = _gear.EquippedInstance(slot);
            return worn != null ? worn.Uid : Guid.Empty;
        }

        private InventoryCommand Command(InventoryCommandKind kind, ulong source, EquipmentSlot slot, Guid sourceUid, Guid targetUid)
        {
            bool created = _authority.TryCreateLocalCommand(kind, source, 0, Vector2Int.zero, slot, sourceUid, targetUid, out InventoryCommand command, out string reason);
            Check(created, "create " + kind + " command: " + reason);
            return command;
        }

        private void Execute()
        {
            _rig = FindFirstObjectByType<PlayerRig>();
            Check(_rig != null && !_rig.IsDead && _rig.Inventory != null && _rig.Equipment != null, "live level rig has a bag/equipment pair");
            if (_rig == null || _rig.IsDead || _rig.Inventory == null || _rig.Equipment == null) return;

            _bag = _rig.Inventory;
            _gear = _rig.Equipment;
            _catalog = _bag.Catalog;
            string reason = "no catalog";
            Check(_catalog != null && !_catalog.IsTestCatalog && _catalog.IsValid(out reason), "the live bag uses the valid production catalog: " + reason);
            if (_catalog == null || !_catalog.TryGetAuthority(out _items, out reason)) return;
            _authority = _bag.CommandAuthority;
            Check(_authority != null, "the bag exposes its bound authority");
            if (_authority == null) return;

            _gear.Clear();
            _bag.Clear();
            _bag.ConfigureGrid(10, 6);

            ItemData boots = Resolve("footpad_boots"), gloves = Resolve("graspwrap_gloves"), necklace = Resolve("emberbead_necklace"), ring = Resolve("cinder_band_ring");
            if (boots == null || gloves == null || necklace == null || ring == null) return;
            Check(boots.equipSlot == EquipmentSlot.Feet, "the live catalog's boots author Feet");

            float baseHp = _rig.MaxHealth;
            float baseMana = _rig.Mana.MaxMana;
            int baseStr = _rig.AttributeStats.GetAttribute(SixAttribute.Strength);
            int baseGs = GearScore.ForEquipment(_gear);
            Check(baseGs == 0 && _gear.FilledCount == 0, "nothing worn scores 0");

            SlotCycles(boots, gloves, necklace, ring, baseHp);
            WrongSlots(boots, gloves, necklace, ring);
            RingRules(ring, baseHp);
            RingSwapThroughAuthority(ring, baseHp);
            FullKitGearScoreAndSnapshot(boots, gloves, necklace, ring, baseHp);
            CharacterPane(ring, baseStr);

            _gear.Clear();
            _bag.Clear();
            Check(_gear.FilledCount == 0 && Mathf.Approximately(_rig.MaxHealth, baseHp) && Mathf.Approximately(_rig.Mana.MaxMana, baseMana) && GearScore.ForEquipment(_gear) == 0,
                "after clearing, health, mana and gear score are exactly the baseline");
        }

        // ------------------------------------------------------------------ equip / unequip x10 per slot, no accumulation

        private void SlotCycles(ItemData boots, ItemData gloves, ItemData necklace, ItemData ring, float baseHp)
        {
            ItemData[] items = { gloves, boots, necklace, ring };
            EquipmentSlot[] slots = { EquipmentSlot.Hands, EquipmentSlot.Feet, EquipmentSlot.Necklace, EquipmentSlot.RingRight };
            for (int k = 0; k < items.Length; k++)
            {
                ItemInstance piece = Mint(items[k], AffixStatKey.MaxHealth, "affix_max_health", 10f);
                if (piece == null) continue;
                Guid uid = piece.Uid;
                string label = slots[k].ToString();
                int unitsInBag = _bag.TotalUnits;
                for (int cycle = 0; cycle < 10; cycle++)
                {
                    bool equipped = cycle % 2 == 0 && !EquipmentSlots.IsRing(slots[k])
                        ? _gear.TryEquipFromInventory(_bag.IndexOfInstance(uid), out string why)
                        : _gear.TryEquipFromInventory(_bag.IndexOfInstance(uid), slots[k], out why);
                    Check(equipped, label + " cycle " + cycle + " equips: " + why);
                    Check(ReferenceEquals(_gear.EquippedInstance(slots[k]), piece) && !_bag.HoldsInstanceUid(uid) && _gear.FilledCount == 1, label + " cycle " + cycle + " the instance is worn and left the bag");
                    Check(Mathf.Approximately(_rig.MaxHealth, baseHp + 10f), label + " cycle " + cycle + " +10 max health exactly once (" + _rig.MaxHealth + ")");
                    _gear.RefreshStats();
                    _gear.RefreshStats();
                    Check(Mathf.Approximately(_rig.MaxHealth, baseHp + 10f) && GearScore.ForEquipment(_gear) == 3, label + " cycle " + cycle + " repeated refresh adds nothing; an Uncommon piece scores 3");
                    bool removed = _gear.TryUnequip(slots[k], out why);
                    Check(removed && _gear.FilledCount == 0 && _bag.HoldsInstanceUid(uid) && WornUid(slots[k]) == Guid.Empty, label + " cycle " + cycle + " unequips back into the bag: " + why);
                    Check(Mathf.Approximately(_rig.MaxHealth, baseHp) && GearScore.ForEquipment(_gear) == 0 && _bag.TotalUnits == unitsInBag, label + " cycle " + cycle + " exact baseline again, uid conserved");
                }
                _bag.Clear();
                _gear.Clear();
            }
        }

        // ------------------------------------------------------------------ wrong slot refused, nothing changes

        private void WrongSlots(ItemData boots, ItemData gloves, ItemData necklace, ItemData ring)
        {
            ItemInstance g = Mint(gloves, AffixStatKey.MaxHealth, "affix_max_health", 10f);
            ItemInstance n = Mint(necklace, AffixStatKey.MaxHealth, "affix_max_health", 10f);
            ItemInstance r = Mint(ring, AffixStatKey.MaxHealth, "affix_max_health", 10f);
            ItemInstance b = Mint(boots, AffixStatKey.MaxHealth, "affix_max_health", 10f);
            if (g == null || n == null || r == null || b == null) return;

            (ItemInstance piece, EquipmentSlot slot)[] attempts =
            {
                (g, EquipmentSlot.Feet), (g, EquipmentSlot.Legs), (g, EquipmentSlot.RingLeft),
                (b, EquipmentSlot.Hands), (b, EquipmentSlot.Legs),
                (n, EquipmentSlot.RingLeft), (n, EquipmentSlot.RingRight), (n, EquipmentSlot.Hands),
                (r, EquipmentSlot.Necklace), (r, EquipmentSlot.Feet), (r, EquipmentSlot.Hands),
                (g, (EquipmentSlot)14), (g, EquipmentSlot.AltWeapon), (r, EquipmentSlot.AltOffHand), (r, (EquipmentSlot)(-1)),
            };
            foreach ((ItemInstance piece, EquipmentSlot slot) attempt in attempts)
            {
                string before = State();
                bool equipped = _gear.TryEquipFromInventory(_bag.IndexOfInstance(attempt.piece.Uid), attempt.slot, out string why);
                Check(!equipped && State() == before, attempt.piece.Definition.itemId + " into " + attempt.slot + " is refused and changes nothing: " + why);
            }

            // through the authority: the same refusals, and a refused command still consumes its sequence but moves nothing
            string stateBefore = State();
            ulong revision = _bag.Revision;
            InventorySlot slotOfGloves = FindStack(g.Uid);
            InventoryCommand wrong = Command(InventoryCommandKind.Equip, slotOfGloves.StackId, EquipmentSlot.Feet, g.Uid, Guid.Empty);
            InventoryCommandResult result = _authority.Submit(_rig, wrong);
            Check(!result.Accepted && !result.Changed && result.Revision == revision && State() == stateBefore, "an Equip command gloves -> Feet is refused through the authority: " + result.Reason);

            bool created = _authority.TryCreateLocalCommand(InventoryCommandKind.Equip, slotOfGloves.StackId, 0, Vector2Int.zero, (EquipmentSlot)14, g.Uid, Guid.Empty, out _, out string unknownReason);
            Check(!created && unknownReason != null, "an Equip command naming slot 14 is not a legal intent: " + unknownReason);

            _bag.Clear();
            _gear.Clear();
        }

        private InventorySlot FindStack(Guid uid)
        {
            int index = _bag.IndexOfInstance(uid);
            Check(index >= 0, "the bag holds instance " + uid.ToString("N"));
            return index >= 0 ? _bag.Slots[index] : default;
        }

        // ------------------------------------------------------------------ ring placement

        private void RingRules(ItemData ring, float baseHp)
        {
            ItemInstance r1 = Mint(ring, AffixStatKey.MaxHealth, "affix_max_health", 5f);
            ItemInstance r2 = Mint(ring, AffixStatKey.MaxHealth, "affix_max_health", 6f);
            ItemInstance r3 = Mint(ring, AffixStatKey.MaxHealth, "affix_max_health", 7f);
            if (r1 == null || r2 == null || r3 == null) return;
            Check(r1.Uid != r2.Uid && r2.Uid != r3.Uid && r1.Uid != r3.Uid, "three distinct ring identities");

            Check(_gear.TryEquipFromInventory(_bag.IndexOfInstance(r1.Uid), out string why) && WornUid(EquipmentSlot.RingLeft) == r1.Uid && WornUid(EquipmentSlot.RingRight) == Guid.Empty, "the first ring with no target goes to the first empty ring slot (RingLeft): " + why);
            Check(_gear.TryEquipFromInventory(_bag.IndexOfInstance(r2.Uid), out why) && WornUid(EquipmentSlot.RingRight) == r2.Uid && WornUid(EquipmentSlot.RingLeft) == r1.Uid, "the second goes to RingRight: " + why);
            Check(Mathf.Approximately(_rig.MaxHealth, baseHp + 11f), "two rings add 5 + 6 exactly once (" + _rig.MaxHealth + ")");

            string before = State();
            bool refused = !_gear.TryEquipFromInventory(_bag.IndexOfInstance(r3.Uid), out why);
            Check(refused && why != null && why.Contains("both ring slots are full") && State() == before, "a third ring with no explicit target is refused and changes nothing: " + why);
            Check(_gear.TryChooseSlot(ring, EquipmentSlot.None, out EquipmentSlot chosen, out why) == false && chosen == EquipmentSlot.None, "TryChooseSlot reports no default when both are full");

            Check(_gear.TryEquipFromInventory(_bag.IndexOfInstance(r3.Uid), EquipmentSlot.RingRight, out why), "an explicit RingRight replaces: " + why);
            Check(WornUid(EquipmentSlot.RingRight) == r3.Uid && WornUid(EquipmentSlot.RingLeft) == r1.Uid && _bag.HoldsInstanceUid(r2.Uid) && !_bag.HoldsInstanceUid(r3.Uid), "r3 is worn on the right, r2 went back to the bag, r1 is untouched");
            Check(Mathf.Approximately(_rig.MaxHealth, baseHp + 12f), "stats follow the replacement: 5 + 7 (" + _rig.MaxHealth + ")");

            Check(_gear.TryUnequip(EquipmentSlot.RingLeft, out why) && _gear.TryUnequip(EquipmentSlot.RingRight, out why) && _gear.FilledCount == 0 && Mathf.Approximately(_rig.MaxHealth, baseHp), "both rings unequip to the baseline");
            Check(_gear.TryEquipFromInventory(_bag.IndexOfInstance(r2.Uid), EquipmentSlot.RingRight, out why) && WornUid(EquipmentSlot.RingRight) == r2.Uid && WornUid(EquipmentSlot.RingLeft) == Guid.Empty, "a ring may be placed in RingRight while RingLeft is empty: " + why);

            // the same uid cannot be in both ring slots
            before = State();
            Check(_gear.TryEquipFromInventory(_bag.IndexOfInstance(r1.Uid), EquipmentSlot.RingRight, out why), "r1 can replace r2 on the right");
            _bag.Clear();
            _gear.Clear();
        }

        // ------------------------------------------------------------------ the atomic swap through the authority

        private void RingSwapThroughAuthority(ItemData ring, float baseHp)
        {
            _bag.Clear();
            _gear.Clear();
            ItemInstance a = Mint(ring, AffixStatKey.MaxHealth, "affix_max_health", 5f);
            ItemInstance b = Mint(ring, AffixStatKey.MaxHealth, "affix_max_health", 9f);
            if (a == null || b == null) return;
            Check(_gear.TryEquipFromInventory(_bag.IndexOfInstance(a.Uid), EquipmentSlot.RingLeft, out string why) && _gear.TryEquipFromInventory(_bag.IndexOfInstance(b.Uid), EquipmentSlot.RingRight, out why), "a on the left, b on the right: " + why);

            string before = State();
            ulong revision = _bag.Revision;
            float hp = _rig.MaxHealth;
            int score = GearScore.ForEquipment(_gear);
            int units = _bag.TotalUnits;

            // a legal swap: RingRight receives the ring from RingLeft
            InventoryCommand swap = Command(InventoryCommandKind.SwapEquipment, 0, EquipmentSlot.RingRight, a.Uid, b.Uid);
            InventoryCommandResult result = _authority.Submit(_rig, swap);
            Check(result.Accepted && result.Changed && result.Revision == revision + 1, "the swap is accepted and moves the revision by exactly one: " + result.Reason);
            Check(WornUid(EquipmentSlot.RingLeft) == b.Uid && WornUid(EquipmentSlot.RingRight) == a.Uid, "the two rings traded slots");
            Check(_bag.TotalUnits == units && !_bag.HoldsInstanceUid(a.Uid) && !_bag.HoldsInstanceUid(b.Uid) && _gear.FilledCount == 2, "both uids are conserved: still worn, none in the bag, none minted");
            Check(Mathf.Approximately(_rig.MaxHealth, hp) && GearScore.ForEquipment(_gear) == score && Mathf.Approximately(hp, baseHp + 14f), "stats and gear score are unchanged by a swap (no accumulation)");

            // replay of the same command: refused
            string afterSwap = State();
            result = _authority.Submit(_rig, swap);
            Check(!result.Accepted && !result.Changed && State() == afterSwap, "replaying the command (same sequence) is refused and changes nothing: " + result.Reason);

            // stale identity: the expectation names the OLD occupant
            InventoryCommand stale = Command(InventoryCommandKind.SwapEquipment, 0, EquipmentSlot.RingRight, a.Uid, b.Uid);
            result = _authority.Submit(_rig, stale);
            Check(!result.Accepted && !result.Changed && State() == afterSwap, "a swap expecting the old occupants is refused and changes nothing: " + result.Reason);

            // an invented identity
            InventoryCommand invented = Command(InventoryCommandKind.SwapEquipment, 0, EquipmentSlot.RingLeft, Guid.NewGuid(), WornUid(EquipmentSlot.RingLeft));
            result = _authority.Submit(_rig, invented);
            Check(!result.Accepted && State() == afterSwap, "an unknown source identity is refused and changes nothing: " + result.Reason);

            // stale revision: the model moves on between creating and submitting
            InventoryCommand late = Command(InventoryCommandKind.SwapEquipment, 0, EquipmentSlot.RingLeft, WornUid(EquipmentSlot.RingRight), WornUid(EquipmentSlot.RingLeft));
            Check(_gear.TryUnequip(EquipmentSlot.RingLeft, out why) && _gear.TryEquipFromInventory(_bag.IndexOfInstance(b.Uid), EquipmentSlot.RingLeft, out why), "the model changes after the command was created: " + why);
            string moved = State();
            result = _authority.Submit(_rig, late);
            Check(!result.Accepted && !result.Changed && State() == moved && result.Reason != null && result.Reason.Contains("stale"), "a swap built on an old revision is refused and changes nothing: " + result.Reason);

            // both ring slots empty: nothing to swap
            Check(_gear.TryUnequip(EquipmentSlot.RingLeft, out why) && _gear.TryUnequip(EquipmentSlot.RingRight, out why), "both rings out");
            string empty = State();
            InventoryCommand nothing = Command(InventoryCommandKind.SwapEquipment, 0, EquipmentSlot.RingLeft, Guid.Empty, Guid.Empty);
            result = _authority.Submit(_rig, nothing);
            Check(!result.Accepted && !result.Changed && State() == empty, "swapping two empty ring slots is refused: " + result.Reason);

            // moving a ring into an EMPTY ring slot is a swap with an empty target
            Check(_gear.TryEquipFromInventory(_bag.IndexOfInstance(a.Uid), EquipmentSlot.RingLeft, out why), "a back on the left: " + why);
            InventoryCommand moveAcross = Command(InventoryCommandKind.SwapEquipment, 0, EquipmentSlot.RingRight, a.Uid, Guid.Empty);
            revision = _bag.Revision;
            result = _authority.Submit(_rig, moveAcross);
            Check(result.Accepted && result.Revision == revision + 1 && WornUid(EquipmentSlot.RingRight) == a.Uid && WornUid(EquipmentSlot.RingLeft) == Guid.Empty, "a ring moves across into the empty slot, same uid: " + result.Reason);

            // not a ring slot: not a legal intent at all
            bool created = _authority.TryCreateLocalCommand(InventoryCommandKind.SwapEquipment, 0, 0, Vector2Int.zero, EquipmentSlot.Chest, a.Uid, Guid.Empty, out _, out string structural);
            Check(!created && structural != null, "a swap whose destination is not a ring slot is not a legal intent: " + structural);
            created = _authority.TryCreateLocalCommand(InventoryCommandKind.SwapEquipment, 5, 0, Vector2Int.zero, EquipmentSlot.RingLeft, a.Uid, Guid.Empty, out _, out structural);
            Check(!created, "a swap that names a bag stack is not a legal intent: " + structural);

            // owner-bound: another rig cannot submit this player's command, and a forged owner is refused
            string fixedState = State();
            InventoryCommand mine = Command(InventoryCommandKind.SwapEquipment, 0, EquipmentSlot.RingLeft, a.Uid, Guid.Empty);
            GameObject foreignObject = new GameObject("Slots_ForeignRequester");
            PlayerRig foreign = foreignObject.AddComponent<PlayerRig>();
            Inventory foreignBag = foreignObject.AddComponent<Inventory>();
            foreign.WireRig();
            foreignBag.Initialize(foreign);
            result = _authority.Submit(foreign, mine);
            Check(!result.Accepted && !result.Changed && State() == fixedState, "a foreign requester cannot submit the command: " + result.Reason);
            InventoryCommand forged = new InventoryCommand(mine.Kind, mine.OwnerPlayerId + 1, mine.Sequence + 1, mine.ExpectedRevision, 0, 0, Vector2Int.zero, mine.DestinationSlot, mine.ExpectedSourceGearUid, mine.ExpectedTargetGearUid);
            result = _authority.Submit(_rig, forged);
            Check(!result.Accepted && !result.Changed && State() == fixedState, "a command that names another owner is refused: " + result.Reason);
            Destroy(foreignObject);

            // a wrong-owner or foreign attempt did not burn this player's right to submit the real command
            result = _authority.Submit(_rig, mine);
            Check(result.Accepted && WornUid(EquipmentSlot.RingLeft) == a.Uid && WornUid(EquipmentSlot.RingRight) == Guid.Empty, "the real command still applies afterwards: " + result.Reason);

            // the direct API refuses the same malformed requests
            string direct = State();
            Check(!_gear.TrySwapRingSlots(EquipmentSlot.RingLeft, EquipmentSlot.RingLeft, out why) && !_gear.TrySwapRingSlots(EquipmentSlot.Necklace, EquipmentSlot.RingLeft, out why) &&
                  !_gear.TrySwapRingSlots(EquipmentSlot.RingLeft, EquipmentSlot.Hands, out why) && State() == direct, "TrySwapRingSlots refuses same-slot and non-ring requests without touching anything");

            _bag.Clear();
            _gear.Clear();
        }

        // ------------------------------------------------------------------ five new slots worn together, gear score, snapshots

        private void FullKitGearScoreAndSnapshot(ItemData boots, ItemData gloves, ItemData necklace, ItemData ring, float baseHp)
        {
            _bag.Clear();
            _gear.Clear();
            ItemInstance g = Mint(gloves, AffixStatKey.MaxHealth, "affix_max_health", 5f);
            ItemInstance f = Mint(boots, AffixStatKey.MaxHealth, "affix_max_health", 6f);
            ItemInstance n = Mint(necklace, AffixStatKey.MaxHealth, "affix_max_health", 7f);
            ItemInstance r1 = Mint(ring, AffixStatKey.MaxHealth, "affix_max_health", 8f);
            ItemInstance r2 = Mint(ring, AffixStatKey.MaxHealth, "affix_max_health", 9f);
            if (g == null || f == null || n == null || r1 == null || r2 == null) return;

            bool all = _gear.TryEquipFromInventory(_bag.IndexOfInstance(g.Uid), out string why);
            all &= _gear.TryEquipFromInventory(_bag.IndexOfInstance(f.Uid), out why);
            all &= _gear.TryEquipFromInventory(_bag.IndexOfInstance(n.Uid), out why);
            all &= _gear.TryEquipFromInventory(_bag.IndexOfInstance(r1.Uid), out why);
            all &= _gear.TryEquipFromInventory(_bag.IndexOfInstance(r2.Uid), out why);
            Check(all && _gear.FilledCount == 5, "all five new slots are worn at once: " + why);
            Check(Mathf.Approximately(_rig.MaxHealth, baseHp + 35f), "the five contributions add up exactly once: 5+6+7+8+9 (" + _rig.MaxHealth + ")");
            Check(GearScore.ForEquipment(_gear) == 15, "gear score counts all five new slots: five Uncommon pieces x 3 = 15 (" + GearScore.ForEquipment(_gear) + ")");
            Check(_gear.Describe().Contains("Hands=") && _gear.Describe().Contains("Feet=") && _gear.Describe().Contains("Necklace=") && _gear.Describe().Contains("RingLeft=") && _gear.Describe().Contains("RingRight="), "Describe names the new slots: " + _gear.Describe());

            // ---- snapshot: slots by name, exact restore
            string beforeRestore = State();
            LoadoutSnapshot saved = LoadoutSnapshot.Capture(_bag, _gear);
            string json = saved.ToJson();
            Check(saved.schemaVersion == 4 && saved.equipment.schemaVersion == 4 && json.Contains("\"slot\":\"RingLeft\"") && json.Contains("\"slot\":\"RingRight\"") && json.Contains("\"slot\":\"Feet\"") &&
                  json.Contains("\"slot\":\"Hands\"") && json.Contains("\"slot\":\"Necklace\"") && !json.Contains("\"Legs\""), "the snapshot is version 4 and names the new slots; nothing is in Legs");
            _gear.Clear();
            _bag.Clear();
            Check(LoadoutSnapshot.TryRestoreJson(json, _bag, _gear, null, out why), "the loadout restores from its JSON: " + why);
            string afterRestore = State();
            Check(afterRestore == beforeRestore, "restored state equals the saved state exactly (uids per slot, health, gear score; the bag revision is excluded: Clear and the restore each commit one)\n  before: " + beforeRestore + "\n  after:  " + afterRestore);
            Check(WornUid(EquipmentSlot.RingLeft) == r1.Uid && WornUid(EquipmentSlot.RingRight) == r2.Uid, "each ring is back in its own ring slot");

            // restoring over the same state twice accumulates nothing
            Check(LoadoutSnapshot.TryRestoreJson(json, _bag, _gear, null, out why) && Mathf.Approximately(_rig.MaxHealth, baseHp + 35f), "a second restore adds nothing: " + why);

            // a forged slot assignment is refused and changes nothing
            ContainerSnapshot gearOnly = _gear.ToContainerSnapshot();
            ContainerEntry glovesEntry = null;
            for (int i = 0; i < gearOnly.entries.Length; i++) if (gearOnly.entries[i].slot == "Hands") glovesEntry = gearOnly.entries[i];
            Check(glovesEntry != null, "the equipment snapshot has the gloves entry");
            if (glovesEntry != null)
            {
                glovesEntry.slot = "Feet";
                for (int i = 0; i < gearOnly.entries.Length; i++) if (gearOnly.entries[i].item.itemId == "footpad_boots") gearOnly.entries[i].slot = "Legs";
                gearOnly.Seal();
                string stateNow = State();
                Check(!_gear.TryRestore(gearOnly, out why) && State() == stateNow, "gloves in Feet and boots in Legs are refused and nothing changes: " + why);
            }

            _gear.Clear();
            _bag.Clear();
        }

        // ------------------------------------------------------------------ the character pane includes the new slots' stats

        private void CharacterPane(ItemData ring, int baseStr)
        {
            _gear.Clear();
            _bag.Clear();
            ItemInstance strRing = Mint(ring, AffixStatKey.Strength, "affix_strength", 2f);
            if (strRing == null) return;
            Check(_gear.TryEquipFromInventory(_bag.IndexOfInstance(strRing.Uid), EquipmentSlot.RingLeft, out string why), "a +2 strength ring is worn: " + why);
            Check(_rig.AttributeStats.GetAttribute(SixAttribute.Strength) == baseStr + 2 && _rig.AttributeStats.EquippedBonuses.Total(SixAttribute.Strength) == 2, "the effective strength includes the ring (+2)");

            GameObject host = new GameObject("Slots_PaneHost", typeof(RectTransform));
            try
            {
                CharacterStatsView view = new CharacterStatsView(host.transform, 400f);
                view.Configure(_rig);
                view.Build();
                bool shown = false;
                foreach (Text text in host.GetComponentsInChildren<Text>(true)) if (text.text != null && text.text.Contains("worn +2")) shown = true;
                Check(shown, "the character pane shows the ring's worn +2 on the strength row");
                Check(_gear.TryUnequip(EquipmentSlot.RingLeft, out why), "the ring comes off: " + why);
                view.Refresh();
                bool stillShown = false;
                foreach (Text text in host.GetComponentsInChildren<Text>(true)) if (text.text != null && text.text.Contains("worn +2")) stillShown = true;
                Check(!stillShown && view.RowCount == CharacterStatsView.AttributeRowCount + CharacterStatsView.VitalRowCount + CharacterStatsView.GuardRowCount + CharacterStatsView.SpeedRowCount,
                    "refreshing the pane after unequipping drops the line and adds none");
            }
            finally { Destroy(host); }

            _gear.Clear();
            _bag.Clear();
        }
    }
}
