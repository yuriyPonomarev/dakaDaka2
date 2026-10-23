using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
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
    /// U2B11 focused gate: the Back/cloak equipment slot, the two cloak definitions, their chest loot, and the
    /// rarity / fixed-vs-rolled tooltip model.
    ///
    /// Run is one batch entry in two phases that share one log line. Phase 1 (edit mode): the real builder
    /// (BuildDataOnly) runs twice and the data folder must be byte-identical afterwards, with stable GUIDs and
    /// no duplicated cloak assets or loot entries. Phase 2 (play mode on the real level scene, with the real
    /// rig): equip / unequip round trips, fixed + rolled stats, the real damage path, atomic swap refusal, loot
    /// rolls on the real tables and the tooltip text. The phase-1 counters travel through SessionState across
    /// the domain reload.
    ///
    /// Batch entry: Emberdelve.EditorTools.EmberdelveCloakSlotGate.Run
    /// </summary>
    [InitializeOnLoad]
    public static class EmberdelveCloakSlotGate
    {
        private const string Pending = "Emberdelve.CloakSlot.Pending";
        internal const string ChecksKey = "Emberdelve.CloakSlot.Checks";
        internal const string FailuresKey = "Emberdelve.CloakSlot.Failures";

        private static int _n, _fail;

        static EmberdelveCloakSlotGate()
        {
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.EnteredPlayMode && SessionState.GetBool(Pending, false))
                {
                    SessionState.SetBool(Pending, false);
                    new GameObject("CloakSlot_Gate").AddComponent<CloakSlotHarness>();
                }
            };
        }

        private static void Check(bool ok, string text)
        {
            _n++;
            if (!ok) { _fail++; Debug.LogError("[CloakSlot] mismatch " + text); }
        }

        public static void Run()
        {
            _n = 0; _fail = 0;
            try { BuilderPhase(); }
            catch (Exception e) { _fail++; Debug.LogException(e); }

            SessionState.SetInt(ChecksKey, _n);
            SessionState.SetInt(FailuresKey, _fail);
            SessionState.SetBool(Pending, true);
            EditorSceneManager.OpenScene("Assets/Emberdelve/Scenes/Emberdelve_Level01.unity");
            EditorApplication.EnterPlaymode();
        }

        // ------------------------------------------------------------------ phase 1: builder

        private static Dictionary<string, string> HashData()
        {
            var map = new Dictionary<string, string>();
            using (SHA1 sha = SHA1.Create())
            {
                foreach (string file in Directory.GetFiles(EmberdelvePaths.Data, "*", SearchOption.AllDirectories))
                {
                    if (!file.EndsWith(".asset") && !file.EndsWith(".meta") && !file.EndsWith(".inputactions")) continue;
                    map[file.Replace('\\', '/')] = BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(file)));
                }
            }
            return map;
        }

        private static void BuilderPhase()
        {
            EmberdelveBootstrapMenu.BuildDataOnly();
            AssetDatabase.SaveAssets();
            var first = HashData();
            string cloakAPath = EmberdelvePaths.Data + "/Item_AshenCloak.asset";
            string cloakBPath = EmberdelvePaths.Data + "/Item_EmberthreadMantle.asset";
            string guidA1 = AssetDatabase.AssetPathToGUID(cloakAPath), guidB1 = AssetDatabase.AssetPathToGUID(cloakBPath);

            EmberdelveBootstrapMenu.BuildDataOnly();
            AssetDatabase.SaveAssets();
            var second = HashData();

            Check(!string.IsNullOrEmpty(guidA1) && !string.IsNullOrEmpty(guidB1), "both cloak assets exist after the first build");
            Check(AssetDatabase.AssetPathToGUID(cloakAPath) == guidA1 && AssetDatabase.AssetPathToGUID(cloakBPath) == guidB1,
                "cloak GUIDs are stable across the second build");
            Check(first.Count == second.Count, $"second build created or removed no data file ({first.Count} -> {second.Count})");
            int differing = 0;
            foreach (var kv in first)
            {
                if (!second.TryGetValue(kv.Key, out string h) || h != kv.Value)
                {
                    differing++;
                    Debug.LogError("[CloakSlot] data file changed on the second build: " + kv.Key);
                }
            }
            Check(differing == 0, "second BuildDataOnly left every data asset and meta byte-identical (no dirty diff)");

            ItemData ashen = AssetDatabase.LoadAssetAtPath<ItemData>(cloakAPath);
            ItemData mantle = AssetDatabase.LoadAssetAtPath<ItemData>(cloakBPath);
            Check(ashen != null && mantle != null, "cloak definitions load");
            if (ashen == null || mantle == null) return;

            foreach (ItemData cloak in new[] { ashen, mantle })
            {
                Check(cloak.equipSlot == EquipmentSlot.Back && cloak.kind == ItemKind.Armor && cloak.maxStack == 1 && cloak.IsEquippable,
                    cloak.itemId + " is single, equippable Back armour");
                Check(ItemIdentityRules.IsStableItemId(cloak.itemId, out _), cloak.itemId + " has a stable id");
                Check(cloak.StatTotal(SkillStat.Armor) > 0f, cloak.itemId + " carries a fixed armour rating");
                Check(cloak.HasAttributeBonuses, cloak.itemId + " carries a fixed attribute bonus");
                Check(cloak.EffectiveFootprint.x <= 10 && cloak.EffectiveFootprint.y <= 4, cloak.itemId + " fits the bag grid");
            }
            Check(ashen.itemId != mantle.itemId, "the two cloaks have distinct ids");
            Check(Mathf.Approximately(ashen.StatTotal(SkillStat.Armor), 2f) && Mathf.Approximately(mantle.StatTotal(SkillStat.Armor), 1f),
                "authored fixed armour 2 and 1");
            Check(Mathf.Approximately(mantle.StatTotal(SkillStat.MaxMana), 15f), "mantle authors +15 max mana as a fixed modifier");

            int idsAshen = 0, idsMantle = 0;
            foreach (string guid in AssetDatabase.FindAssets("t:ItemData", new[] { EmberdelvePaths.Data }))
            {
                ItemData item = AssetDatabase.LoadAssetAtPath<ItemData>(AssetDatabase.GUIDToAssetPath(guid));
                if (item == null) continue;
                if (item.itemId == "ashen_cloak") idsAshen++;
                if (item.itemId == "emberthread_mantle") idsMantle++;
            }
            Check(idsAshen == 1 && idsMantle == 1, $"each cloak id exists exactly once ({idsAshen}/{idsMantle})");

            LootTable supply = AssetDatabase.LoadAssetAtPath<LootTable>(EmberdelvePaths.Data + "/Loot_SupplyChest.asset");
            LootTable ember = AssetDatabase.LoadAssetAtPath<LootTable>(EmberdelvePaths.Data + "/Loot_EmberChest.asset");
            LootTable sealedTable = AssetDatabase.LoadAssetAtPath<LootTable>(EmberdelvePaths.Data + "/Loot_SealedChest.asset");
            Check(CountEntries(supply, ashen) == 1 && CountEntries(ember, mantle) == 1 && CountEntries(sealedTable, mantle) == 1,
                "each chest table holds its cloak entry exactly once after two builds");
            Check(supply.entries.Length == 5 && ember.entries.Length == 5 && sealedTable.entries.Length == 4, "entry counts 5/5/4");
            Check(supply.Validate().Count == 0 && ember.Validate().Count == 0 && sealedTable.Validate().Count == 0, "tables still validate clean");
        }

        private static int CountEntries(LootTable table, ItemData item)
        {
            int count = 0;
            if (table == null || table.entries == null) return count;
            foreach (LootEntry entry in table.entries) if (entry != null && entry.item == item) count++;
            return count;
        }
    }

    public sealed class CloakSlotHarness : MonoBehaviour
    {
        private int n, fail;
        private readonly List<UnityEngine.Object> cleanup = new List<UnityEngine.Object>();

        private void Check(bool ok, string text)
        {
            n++;
            if (!ok) { fail++; Debug.LogError("[CloakSlot] mismatch " + text); }
        }

        private T Make<T>() where T : ScriptableObject
        {
            var x = ScriptableObject.CreateInstance<T>();
            cleanup.Add(x);
            return x;
        }

        private IEnumerator Start()
        {
            n = SessionState.GetInt(EmberdelveCloakSlotGate.ChecksKey, 0);
            fail = SessionState.GetInt(EmberdelveCloakSlotGate.FailuresKey, 0);

            for (int i = 0; i < 10; i++) yield return null;

            try { Execute(); }
            catch (Exception e) { fail++; Debug.LogException(e); }
            finally { foreach (var x in cleanup) if (x != null) Destroy(x); }

            Debug.Log("[Emberdelve][CloakSlot] " + (fail == 0 ? "PASS" : "FAIL") + " checks=" + n + " failures=" + fail);
            EditorApplication.Exit(fail == 0 ? 0 : 1);
        }

        // ------------------------------------------------------------------ fixtures

        private sealed class Actor : ICombatEntity, IArmorRatingProvider, IActiveWeaponProvider
        {
            public WeaponData Weapon;
            public WeaponData ActiveWeapon => Weapon;
            public int CombatId { get; set; }
            public Vector3 Point;
            public EntityKind Kind => EntityKind.Enemy;
            public int TeamId => 1;
            public Vector3 CombatCenter => Point;
            public Collider HeadCollider => null;
            public bool IsDead => false;
            public float CurrentHealth => 1000;
            public float MaxHealth => 1000;
            public void ApplyDamage(DamageInfo d) { }
            public float GetArmorRating() => 10000;
        }

        private Actor actor;
        private LocalCombatAuthority authority;
        private float clock = 100;
        private DamageInfo captured;

        private void Hit(PlayerRig rig, WeaponData weapon, float expected, string label)
        {
            rig.Health.Configure(120, 0, 0);
            rig.transform.rotation = Quaternion.identity;
            rig.SetGuardHeld(false);
            actor.Point = rig.CombatCenter + Vector3.forward * 1.5f;
            clock += 5;

            var intent = new AttackIntent(actor.CombatId, actor.Kind, weapon, 1, actor.Point, rig.CombatCenter - actor.Point, 3,
                CombatRegistry.NextSequence(actor.CombatId), clock);
            Action<DamageInfo> receive = d => captured = d;
            rig.Health.Damaged += receive;
            bool ok;
            try { ok = authority.SubmitAttack(intent, new HitCandidate(rig.CombatId, rig.CombatCenter, false)); }
            finally { rig.Health.Damaged -= receive; }

            Check(ok, label + " accepted");
            Check(Mathf.Abs(120 - rig.CurrentHealth - expected) < 0.001f, label + " real health delta " + (120 - rig.CurrentHealth) + " vs " + expected);
            Check(Mathf.Abs(captured.Amount - expected) < 0.001f, label + " DamageInfo amount " + captured.Amount + " vs " + expected);
            Check(!captured.Blocked, label + " armour does not flag a block");
        }

        private static List<AffixDefinition> LoadAffixes()
        {
            var list = new List<AffixDefinition>();
            foreach (string guid in AssetDatabase.FindAssets("t:AffixDefinition", new[] { EmberdelveAssetLibrary.AffixFolder }))
                list.Add(AssetDatabase.LoadAssetAtPath<AffixDefinition>(AssetDatabase.GUIDToAssetPath(guid)));
            list.Sort((a, b) => string.CompareOrdinal(a.id, b.id));
            return list;
        }

        private ItemInstance Mint(ItemInstanceAuthority auth, ItemData def, AffixRarity rarity, params AffixRecord[] records)
        {
            bool ok = ItemInstance.TryCreateNew(def, 1, rarity, records.Length == 0 ? null : records, auth, out ItemInstance inst, out string reason);
            Check(ok && inst != null, $"mint {def.itemId} {rarity} ({reason})");
            return inst;
        }

        private static float[] Scores(PlayerRig rig)
        {
            var scores = new float[AttributeRules.AttributeCount];
            for (int i = 0; i < scores.Length; i++) scores[i] = rig.AttributeStats.GetAttribute((SixAttribute)i);
            return scores;
        }

        private static bool SameScores(float[] a, float[] b)
        {
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        // ------------------------------------------------------------------ the checks

        private void Execute()
        {
            PlayerRig rig = FindFirstObjectByType<PlayerRig>();
            Check(rig != null && !rig.IsDead && rig.AttributeStats != null && rig.AttributeStats.IsReady, "real living rig with ready attributes");
            if (rig == null) return;

            Inventory bag = rig.Inventory;
            Equipment gear = rig.Equipment;
            gear.Clear();
            bag.Clear();
            bag.ConfigureGrid(10, 4);

            ItemData ashen = AssetDatabase.LoadAssetAtPath<ItemData>(EmberdelvePaths.Data + "/Item_AshenCloak.asset");
            ItemData mantle = AssetDatabase.LoadAssetAtPath<ItemData>(EmberdelvePaths.Data + "/Item_EmberthreadMantle.asset");
            ItemData boots = AssetDatabase.LoadAssetAtPath<ItemData>(EmberdelvePaths.Data + "/Item_FootpadBoots.asset");
            ItemData blade = AssetDatabase.LoadAssetAtPath<ItemData>(EmberdelvePaths.Data + "/Item_EmberFangBlade.asset");
            Check(ashen != null && mantle != null && boots != null && blade != null, "assets load");
            if (ashen == null || mantle == null) return;

            // ---- slot enum and iteration derived from it
            Check((int)EquipmentSlot.Back == 6, "Back is appended as 6, existing values unchanged");
            Check((int)EquipmentSlot.Legs == 5 && (int)EquipmentSlot.Chest == 4 && (int)EquipmentSlot.Head == 3 &&
                  (int)EquipmentSlot.OffHand == 2 && (int)EquipmentSlot.Weapon == 1, "earlier slot values untouched");
            Check(Equipment.SlotCount == 7, "slot count derived from the enum is 7 (None + six slots)");
            Check(gear.WornInstances.Count == Equipment.SlotCount, "the worn-instance window is as long as the derived slot count");
            Check(gear.IndexOfSlot(EquipmentSlot.Back) == 6 && gear.IndexOfSlot(EquipmentSlot.None) == -1, "slot index mapping");
            Check(ItemCategoryRules.TryFromEquipmentSlot(ashen, out AffixCategory cat, out string catReason) && cat == AffixCategory.Cloak,
                "Back maps to the Cloak affix category (" + catReason + ")");

            // ---- trusted catalog over the real affix assets
            List<AffixDefinition> affixes = LoadAffixes();
            Check(affixes.Count == 13, "13 builder-owned affix assets load (" + affixes.Count + ")");

            ItemData bigCloak = Make<ItemData>();
            bigCloak.itemId = "gate_big_cloak";
            bigCloak.displayName = "Gate Big Cloak";
            bigCloak.kind = ItemKind.Armor;
            bigCloak.equipSlot = EquipmentSlot.Back;
            bigCloak.gridFootprint = new Vector2Int(4, 3);
            bigCloak.value = 41;
            bigCloak.statModifiers = new[] { new StatModifier(SkillStat.Armor, SkillModifierMode.Flat, 5f) };
            ItemData filler = Make<ItemData>();
            filler.itemId = "gate_cloak_filler";
            filler.kind = ItemKind.Relic;
            filler.gridFootprint = Vector2Int.one;
            filler.maxStack = 1;

            Check(ItemInstanceAuthority.TryCreate(new[] { ashen, mantle, bigCloak, boots, blade }, affixes, null, out ItemInstanceAuthority auth, out string reason),
                "authority over the real items and affixes: " + reason);
            if (auth == null) return;

            // R3 fixture: bigCloak is an ad-hoc definition the production catalog does not own, so the bag carries an
            // explicit TEST catalog (the real items + the 13 real affixes + bigCloak) for the rest of this pass.
            ItemCatalog gateCatalog = ItemCatalog.CreateForTest(new[] { ashen, mantle, bigCloak, boots, blade }, affixes);
            cleanup.Add(gateCatalog);
            bag.SetCatalog(gateCatalog);

            // Rolled values stay inside each affix definition's own band (1..3 attributes, 1..6 armor, 5..15 pools).
            ItemInstance rareAshen = Mint(auth, ashen, AffixRarity.Rare,
                new AffixRecord("affix_armor", AffixStatKey.Armor, 4), new AffixRecord("affix_wisdom", AffixStatKey.Wisdom, 2));
            ItemInstance uncommonAshen = Mint(auth, ashen, AffixRarity.Uncommon, new AffixRecord("affix_max_health", AffixStatKey.MaxHealth, 9));
            ItemInstance commonAshen = Mint(auth, ashen, AffixRarity.Common);
            ItemInstance commonMantle = Mint(auth, mantle, AffixRarity.Common);
            ItemInstance rolledMantle = Mint(auth, mantle, AffixRarity.Uncommon, new AffixRecord("affix_max_mana", AffixStatKey.MaxMana, 10));
            ItemInstance wornBig = Mint(auth, bigCloak, AffixRarity.Common);
            if (rareAshen == null || uncommonAshen == null || commonAshen == null || commonMantle == null || rolledMantle == null || wornBig == null) return;

            // a weapon-only affix cannot be minted onto a cloak (rejection, nothing created)
            Check(!ItemInstance.TryCreateNew(ashen, 1, AffixRarity.Uncommon, new[] { new AffixRecord("affix_melee_damage", AffixStatKey.MeleeDamage, 2) },
                    auth, out ItemInstance weaponOnly, out string weaponOnlyReason) && weaponOnly == null,
                "a cloak cannot be minted with the weapon-only melee_damage affix (" + weaponOnlyReason + ")");
            Check(!ItemInstance.TryCreateNew(ashen, 1, AffixRarity.Uncommon, new[] { new AffixRecord("affix_projectile_damage", AffixStatKey.ProjectileDamage, 2) },
                    auth, out weaponOnly, out weaponOnlyReason) && weaponOnly == null,
                "a cloak cannot be minted with the weapon-only projectile_damage affix (" + weaponOnlyReason + ")");

            Check(rareAshen.Category == AffixCategory.Cloak, "a minted cloak instance carries the Cloak category");

            // ---- baseline and a no-accumulation equip / unequip round trip
            float baseArmor = rig.GetArmorRating();
            float[] baseScores = Scores(rig);
            float baseMaxHealth = rig.MaxHealth;
            int baseMaxMana = rig.Mana.MaxMana;
            float hpBefore = rig.CurrentHealth;
            float manaBefore = rig.Mana.CurrentMana;
            Check(baseArmor == 0f, "baseline armour rating is 0");

            string snapshotRare = rareAshen.ToSnapshot().ToJson();
            int collected = 0;
            Action<int, ItemData, int> onCollected = (p, d, t) => collected++;
            GameEvents.ItemCollected += onCollected;
            int minted, recovered, lifetime;
            try
            {
                Check(bag.TryAddInstance(rareAshen, out reason), "rare cloak enters the bag: " + reason);
                minted = bag.MintedInstanceCount;
                recovered = bag.RecoveredValue;
                lifetime = bag.LifetimeCollected;
                collected = 0; // adding a drop to the bag is the one legitimate collection; transfers below must add none
                Check(gear.TryEquipFromInventory(bag.IndexOfInstance(rareAshen.Uid), out reason), "rare cloak equips into the Back slot: " + reason);
                Check(ReferenceEquals(gear.EquippedInstance(EquipmentSlot.Back), rareAshen) && gear.Equipped(EquipmentSlot.Back) == ashen && gear.FilledCount == 1,
                    "the Back slot holds the exact instance");
                Check(!bag.HoldsInstanceUid(rareAshen.Uid), "one owner: the bag released the uid");
                Check(!bag.TryAddInstance(rareAshen, out _), "the worn uid cannot be added to the bag a second time");

                // fixed 2 + rolled 4 armour, fixed 1 + rolled 2 wisdom, both through the same paths as chest/legs
                Check(rig.GetArmorRating() == 6f, "armour = fixed 2 + rolled 4 = 6 (got " + rig.GetArmorRating() + ")");
                float[] worn = Scores(rig);
                int wis = (int)SixAttribute.Wisdom;
                Check(worn[wis] == baseScores[wis] + 3, $"wisdom = base + fixed 1 + rolled 2 ({baseScores[wis]} -> {worn[wis]})");
                for (int i = 0; i < worn.Length; i++) if (i != wis) Check(worn[i] == baseScores[i], "no other score moved: " + (SixAttribute)i);
                Check(rig.CurrentHealth == hpBefore, "equipping a cloak healed nothing");
                Check(Mathf.Approximately(rig.Mana.CurrentMana, manaBefore), "equipping a cloak refilled no mana");

                gear.RefreshStats();
                gear.RefreshStats();
                Check(rig.GetArmorRating() == 6f && Scores(rig)[wis] == baseScores[wis] + 3, "repeated refresh adds nothing");

                Check(gear.TryUnequip(EquipmentSlot.Back, out reason), "unequip: " + reason);
                Check(ReferenceEquals(bag.InstanceAt(bag.IndexOfInstance(rareAshen.Uid)), rareAshen) && gear.FilledCount == 0, "the same instance is back in the bag");
                Check(rig.GetArmorRating() == baseArmor && SameScores(Scores(rig), baseScores), "unequip restored the pristine armour and all six scores");
                Check(rig.MaxHealth == baseMaxHealth && rig.Mana.MaxMana == baseMaxMana, "unequip restored the pristine pools");
                Check(rig.CurrentHealth == hpBefore && Mathf.Approximately(rig.Mana.CurrentMana, manaBefore), "no free healing across the round trip");

                for (int round = 0; round < 5; round++)
                {
                    bool ok = gear.TryEquipFromInventory(bag.IndexOfInstance(rareAshen.Uid), out reason) && gear.TryUnequip(EquipmentSlot.Back, out reason);
                    if (!ok) Check(false, "round trip " + round + ": " + reason);
                }
                Check(rig.GetArmorRating() == baseArmor && SameScores(Scores(rig), baseScores) && rig.MaxHealth == baseMaxHealth &&
                      rig.Mana.MaxMana == baseMaxMana, "five more round trips accumulate nothing");
                Check(snapshotRare == rareAshen.ToSnapshot().ToJson(), "uid, rarity and rolls unchanged by equip/unequip");
                Check(bag.MintedInstanceCount == minted + 0 && bag.RecoveredValue == recovered && bag.LifetimeCollected == lifetime && collected == 0,
                    "transfers minted and credited nothing (minted " + bag.MintedInstanceCount + "/" + minted + ", recovered " + bag.RecoveredValue + "/" + recovered + ", lifetime " + bag.LifetimeCollected + "/" + lifetime + ", collected events " + collected + ")");
            }
            finally { GameEvents.ItemCollected -= onCollected; }
            bag.Clear();
            gear.Clear();

            // ---- fixed pools plus a rolled pool: the rolled half adds exactly its own amount
            Check(bag.TryAddInstance(commonMantle, out reason) && gear.TryEquipFromInventory(bag.IndexOfInstance(commonMantle.Uid), out reason), "common mantle equips: " + reason);
            int mantleFixedMana = rig.Mana.MaxMana;
            Check(mantleFixedMana == rig.AttributeStats.MaxMana + 15, $"mantle's fixed +15 max mana applies exactly on top of the INT-derived ceiling ({rig.AttributeStats.MaxMana} -> {mantleFixedMana})");
            Check(rig.GetArmorRating() == 1f, "mantle fixed armour 1");
            Check(Mathf.Approximately(rig.Mana.CurrentMana, manaBefore), "a bigger mana ceiling paid out no current mana");
            Check(gear.TryUnequip(EquipmentSlot.Back, out reason), "common mantle unequips: " + reason);
            Check(bag.TryAddInstance(rolledMantle, out reason) && gear.TryEquipFromInventory(bag.IndexOfInstance(rolledMantle.Uid), out reason), "rolled mantle equips: " + reason);
            Check(rig.Mana.MaxMana == mantleFixedMana + 10, $"rolled +10 max mana on top of the fixed amount ({mantleFixedMana} -> {rig.Mana.MaxMana})");
            Check(gear.TryUnequip(EquipmentSlot.Back, out reason) && rig.Mana.MaxMana == baseMaxMana, "unequip returns to the pristine ceiling");
            bag.Clear();
            gear.Clear();

            // ---- chest + legs + back together: each slot contributes once
            ItemInstance legs = Mint(auth, boots, AffixRarity.Common);
            Check(bag.TryAddInstance(legs, out reason) && bag.TryAddInstance(commonAshen, out reason), "boots and cloak in the bag");
            Check(gear.TryEquipFromInventory(bag.IndexOfInstance(legs.Uid), out reason) && gear.TryEquipFromInventory(bag.IndexOfInstance(commonAshen.Uid), out reason), "boots and cloak worn together");
            Check(gear.FilledCount == 2 && gear.EquippedInstance(EquipmentSlot.Legs) == legs && gear.EquippedInstance(EquipmentSlot.Back) == commonAshen, "two different slots, two instances");
            Check(gear.Describe().Contains("Back=Ashen Cloak") && gear.Describe().Contains("Legs=Footpad Boots"), "Describe names both slots: " + gear.Describe());
            Check(gear.TryUnequip(EquipmentSlot.Legs, out reason) && gear.EquippedInstance(EquipmentSlot.Back) == commonAshen, "removing boots leaves the cloak");
            gear.Clear();
            bag.Clear();
            Check(gear.FilledCount == 0 && rig.GetArmorRating() == baseArmor && SameScores(Scores(rig), baseScores), "Clear removes the cloak contribution too");

            // ---- the cloak's armour through the real authority damage path
            var cfg = Make<GameConfig>();
            cfg.levelMask = 0;
            var go = new GameObject("Cloak_Authority");
            cleanup.Add(go);
            authority = go.AddComponent<LocalCombatAuthority>();
            authority.SetClock(() => clock);
            typeof(LocalCombatAuthority).GetField("config", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).SetValue(authority, cfg);
            actor = new Actor { CombatId = CombatRegistry.AllocateId() };
            CombatRegistry.Register(actor.CombatId, actor);
            WeaponData weapon = Make<WeaponData>();
            weapon.weaponId = "cloak_gate_claw";
            weapon.baseDamage = 15;
            weapon.damageType = DamageType.Physical;
            actor.Weapon = weapon;

            Hit(rig, weapon, 15f, "bare back");
            Check(bag.TryAddInstance(rareAshen, out reason) && gear.TryEquipFromInventory(bag.IndexOfInstance(rareAshen.Uid), out reason), "wear the rare cloak for the damage path");
            Hit(rig, weapon, 15f * 100f / 106f, "rare cloak (fixed 2 + rolled 4 armour)");
            weapon.damageType = DamageType.Holy;
            Hit(rig, weapon, 15f, "Holy is not mitigated by a cloak");
            weapon.damageType = DamageType.Physical;
            Check(gear.TryUnequip(EquipmentSlot.Back, out reason), "take the rare cloak off");
            Hit(rig, weapon, 15f, "back to bare after unequip");
            CombatRegistry.Unregister(actor.CombatId, actor);
            bag.Clear();
            gear.Clear();

            // ---- atomic swap: a full bag rejects a worn-cloak replacement that cannot fit, and loses or duplicates nothing
            bag.ConfigureGrid(10, 4);
            Check(bag.TryAddInstance(wornBig, out reason) && gear.TryEquipFromInventory(bag.IndexOfInstance(wornBig.Uid), out reason), "wear the 4x3 test cloak");
            Check(rig.GetArmorRating() == 5f, "the big cloak's fixed armour 5 applies");
            Check(bag.TryAddInstanceAt(commonAshen, Vector2Int.zero, out reason), "incoming 2x3 cloak placed");
            for (int y = 0; y < 4; y++)
                for (int x = 0; x < 10; x++)
                    if (!bag.IsCellOccupied(new Vector2Int(x, y))) Check(bag.TryAddAt(filler, 1, new Vector2Int(x, y), out reason), "fill a free cell");
            Check(bag.FreeCells == 0, "the bag is completely full");
            int units = bag.TotalUnits, value = bag.CarriedValue, usedCells = bag.UsedCells;
            recovered = bag.RecoveredValue;
            minted = bag.MintedInstanceCount;
            int notifications = 0;
            Action<int> onChanged = p => notifications++;
            GameEvents.InventoryChanged += onChanged;
            try
            {
                Check(!gear.TryEquipFromInventory(bag.IndexOfInstance(commonAshen.Uid), out reason), "swap that cannot fit the outgoing cloak is refused (" + reason + ")");
                Check(ReferenceEquals(gear.EquippedInstance(EquipmentSlot.Back), wornBig) && bag.HoldsInstanceUid(commonAshen.Uid) && !bag.HoldsInstanceUid(wornBig.Uid),
                    "after the refusal both cloaks are exactly where they were");
                Check(bag.TotalUnits == units && bag.CarriedValue == value && bag.UsedCells == usedCells && bag.RecoveredValue == recovered && bag.MintedInstanceCount == minted,
                    "the refusal kept every counter");
                Check(rig.GetArmorRating() == 5f, "the refusal kept the worn armour exactly (no half-applied swap)");
                Check(!gear.TryUnequip(EquipmentSlot.Back, out reason) && ReferenceEquals(gear.EquippedInstance(EquipmentSlot.Back), wornBig) && bag.TotalUnits == units,
                    "unequip into a full bag is refused without loss");
                Check(notifications == 0, "refusals raised no inventory notification (" + notifications + ")");
            }
            finally { GameEvents.InventoryChanged -= onChanged; }
            bag.Clear();
            gear.Clear();

            // ---- a successful same-footprint swap: replacement, not accumulation
            bag.ConfigureGrid(10, 4);
            Check(bag.TryAddInstance(rareAshen, out reason) && bag.TryAddInstance(uncommonAshen, out reason), "two ashen cloaks in the bag");
            Check(gear.TryEquipFromInventory(bag.IndexOfInstance(rareAshen.Uid), out reason) && rig.GetArmorRating() == 6f, "first cloak worn, armour 6");
            Check(gear.TryEquipFromInventory(bag.IndexOfInstance(uncommonAshen.Uid), out reason), "swap to the second cloak: " + reason);
            Check(ReferenceEquals(gear.EquippedInstance(EquipmentSlot.Back), uncommonAshen) && bag.HoldsInstanceUid(rareAshen.Uid) && !bag.HoldsInstanceUid(uncommonAshen.Uid),
                "the first returned to the bag, the second is worn, both uids conserved");
            Check(rig.GetArmorRating() == 2f, "armour is the second cloak's fixed 2 only - the first one's 6 left with it (got " + rig.GetArmorRating() + ")");
            Check(rig.MaxHealth == baseMaxHealth + 9, "the second cloak's rolled max_health +9 applies exactly (" + baseMaxHealth + " -> " + rig.MaxHealth + ")");
            Check(gear.TryUnequip(EquipmentSlot.Back, out reason) && rig.GetArmorRating() == baseArmor && rig.MaxHealth == baseMaxHealth, "unequip restores the pristine numbers");
            bag.Clear();
            gear.Clear();

            // ---- loot tables drop cloaks with correct rarity and affix counts
            LootChecks(ashen, mantle, affixes);

            // ---- tooltip model: instance-keyed, fixed separated from rolled
            TooltipChecks(rig, auth, ashen, blade, rareAshen, uncommonAshen, commonAshen);

            // ---- character panel read model with the cloak worn
            CharacterPanelChecks(rig, rareAshen, bag, gear, baseScores);

            bag.Clear();
            gear.Clear();
        }

        private void LootChecks(ItemData ashen, ItemData mantle, List<AffixDefinition> affixes)
        {
            LootTable supply = AssetDatabase.LoadAssetAtPath<LootTable>(EmberdelvePaths.Data + "/Loot_SupplyChest.asset");
            LootTable ember = AssetDatabase.LoadAssetAtPath<LootTable>(EmberdelvePaths.Data + "/Loot_EmberChest.asset");
            LootTable seal = AssetDatabase.LoadAssetAtPath<LootTable>(EmberdelvePaths.Data + "/Loot_SealedChest.asset");
            var byId = new Dictionary<string, AffixDefinition>();
            foreach (var a in affixes) byId[a.id] = a;
            // R3: real tables roll against the one real item catalog asset.
            ItemCatalog realCatalog = AssetDatabase.LoadAssetAtPath<ItemCatalog>(EmberdelveAssetLibrary.ItemCatalogPath);
            Check(realCatalog != null, "the item catalog asset exists");

            int commonSeen = 0, uncommonSeen = 0, rareSeen = 0;
            var rolledKeys = new HashSet<AffixStatKey>();
            foreach (LootTable table in new[] { supply, ember, seal })
            {
                for (int seed = 1; seed <= 400; seed++)
                {
                    var drops = new List<LootDrop>();
                    var again = new List<LootDrop>();
                    bool ok = table.TryRoll(seed, drops, realCatalog, out string reason) & table.TryRoll(seed, again, realCatalog, out string reason2);
                    if (!ok) { Check(false, table.tableId + " seed " + seed + " refused: " + reason + " " + reason2); continue; }
                    Check(drops.Count == again.Count, table.tableId + "/" + seed + " same seed gives the same drop count (full determinism is asserted in the AffixProfile gate)");

                    for (int i = 0; i < drops.Count; i++)
                    {
                        LootDrop d = drops[i];
                        if (d.Item != ashen && d.Item != mantle) continue;

                        ItemInstance inst = d.Instance;
                        Check(inst != null && inst.Quantity == 1 && d.Amount == 1, table.tableId + "/" + seed + " cloak is a quantity-1 instance with its own uid");
                        if (inst == null) continue;

                        LootEntry entry = Array.Find(table.entries, e => e.item == d.Item);
                        Check(entry != null && inst.Rarity == entry.affixRarity, table.tableId + "/" + seed + " cloak rarity follows its entry");
                        Check(inst.Category == AffixCategory.Cloak, table.tableId + "/" + seed + " cloak instance category is Cloak");
                        AffixRarities.TryExpectedAffixCount(inst.Rarity, out int expected);
                        Check(inst.AffixCount == expected, table.tableId + "/" + seed + " " + inst.Rarity + " cloak has " + inst.AffixCount + " affixes, expected " + expected);

                        var keys = new HashSet<AffixStatKey>();
                        foreach (AffixRecord rec in inst.Affixes)
                        {
                            Check(keys.Add(rec.StatKey), table.tableId + "/" + seed + " distinct stat keys on one cloak");
                            Check(byId.TryGetValue(rec.Id, out AffixDefinition def) && def.statKey == rec.StatKey, "record id matches its definition's key");
                            if (def == null) continue;
                            Check(def.AllowsCategory(AffixCategory.Cloak), table.tableId + "/" + seed + " " + rec.Id + " is allowlisted for Cloak");
                            Check(def.TryValidateRolledValue(rec.Value, AffixCategory.Cloak, out string vr), table.tableId + "/" + seed + " " + rec + " valid for Cloak: " + vr);
                            Check(rec.StatKey != AffixStatKey.MeleeDamage && rec.StatKey != AffixStatKey.ProjectileDamage, "no weapon-only affix rolled on a cloak");
                            rolledKeys.Add(rec.StatKey);
                        }

                        if (inst.Rarity == AffixRarity.Common) commonSeen++;
                        else if (inst.Rarity == AffixRarity.Uncommon) uncommonSeen++;
                        else rareSeen++;
                    }
                }
            }

            Debug.Log($"[Emberdelve][CloakSlot] cloak drops over 3 tables x 400 seeds: common={commonSeen} uncommon={uncommonSeen} rare={rareSeen} distinctRolledKeys={rolledKeys.Count}");
            Check(commonSeen > 0, "the Supply table drops Common cloaks");
            Check(uncommonSeen > 0, "the Ember table drops Uncommon cloaks");
            Check(rareSeen > 0, "the Sealed table drops Rare cloaks");
            Check(rolledKeys.Count >= 4, "cloaks roll a variety of Cloak-allowlisted keys (" + rolledKeys.Count + ")");
        }

        private void TooltipChecks(PlayerRig rig, ItemInstanceAuthority auth, ItemData ashen, ItemData blade,
            ItemInstance rare, ItemInstance uncommon, ItemInstance common)
        {
            ItemTooltipModel mRare = ItemTooltip.ForInstance(rare);
            ItemTooltipModel mUncommon = ItemTooltip.ForInstance(uncommon);
            ItemTooltipModel mCommon = ItemTooltip.ForInstance(common);
            Check(mRare != null && mUncommon != null && mCommon != null, "tooltip models build from instances");
            if (mRare == null || mUncommon == null || mCommon == null) return;

            string tRare = mRare.ToPlainText(), tUncommon = mUncommon.ToPlainText(), tCommon = mCommon.ToPlainText();
            Debug.Log("[Emberdelve][CloakSlot] tooltip rare:\n" + tRare + "\n--- uncommon:\n" + tUncommon + "\n--- common:\n" + tCommon);

            Check(tRare != tUncommon && tRare != tCommon && tUncommon != tCommon, "three instances of one base item give three different tooltips");
            Check(mRare.RarityName == "Rare" && mUncommon.RarityName == "Uncommon" && mCommon.RarityName == "Common", "rarity names");
            Check(mRare.RarityColor == ItemTooltip.RareColor && mUncommon.RarityColor == ItemTooltip.UncommonColor && mCommon.RarityColor == ItemTooltip.CommonColor, "rarity colours");
            Check(mRare.RarityColor != mUncommon.RarityColor && mUncommon.RarityColor != mCommon.RarityColor && mRare.RarityColor != mCommon.RarityColor, "the three rarity colours are distinct");
            Check(tRare.Contains("Rarity: Rare") && tUncommon.Contains("Rarity: Uncommon") && tCommon.Contains("Rarity: Common"), "the text states the rarity");
            Check(mRare.UidShort.Length == ItemTooltip.ShortUidLength && mRare.UidShort != mUncommon.UidShort && mRare.UidShort != mCommon.UidShort && tRare.Contains("uid " + mRare.UidShort),
                "each instance shows its own short uid");

            // fixed section: identical across drops of the same base, authored values
            Check(mRare.FixedLines.Count == mUncommon.FixedLines.Count && mRare.FixedLines.Count == mCommon.FixedLines.Count, "fixed line count is the base item's, whatever the roll");
            bool sameFixed = true;
            for (int i = 0; i < mRare.FixedLines.Count; i++) sameFixed &= mRare.FixedLines[i] == mUncommon.FixedLines[i] && mRare.FixedLines[i] == mCommon.FixedLines[i];
            Check(sameFixed, "the fixed properties are identical for every drop of the same base item");
            Check(mRare.FixedLines.Contains("Armour +2") && mRare.FixedLines.Contains("+1 wisdom"), "fixed armour and fixed attribute bonus are listed: " + string.Join(" | ", mRare.FixedLines));

            // rolled section: per instance, separate from fixed
            Check(mRare.HasRolledSection && mRare.RolledLines.Count == 2 && mRare.RolledLines.Contains("+4 armor") && mRare.RolledLines.Contains("+2 wisdom"),
                "rare lists both rolled affixes with values: " + string.Join(" | ", mRare.RolledLines));
            Check(mUncommon.HasRolledSection && mUncommon.RolledLines.Count == 1 && mUncommon.RolledLines[0] == "+9 max_health", "uncommon lists its single rolled affix");
            Check(!mCommon.HasRolledSection && mCommon.RolledLines.Count == 0 && !tCommon.Contains(ItemTooltipModel.RolledHeader), "common shows no affix section at all");
            Check(tRare.Contains(ItemTooltipModel.FixedHeader) && tRare.Contains(ItemTooltipModel.RolledHeader) && tCommon.Contains(ItemTooltipModel.FixedHeader), "section headers present");

            int fixedAt = tRare.IndexOf(ItemTooltipModel.FixedHeader, StringComparison.Ordinal);
            int rolledAt = tRare.IndexOf(ItemTooltipModel.RolledHeader, StringComparison.Ordinal);
            Check(fixedAt >= 0 && rolledAt > fixedAt, "FIXED comes before ROLLED in the text");
            string fixedPart = tRare.Substring(fixedAt, rolledAt - fixedAt), rolledPart = tRare.Substring(rolledAt);
            Check(fixedPart.Contains("Armour +2") && fixedPart.Contains("+1 wisdom") && !fixedPart.Contains("+4 armor") && !fixedPart.Contains("+2 wisdom"),
                "fixed lines live only in the fixed section");
            Check(rolledPart.Contains("+4 armor") && rolledPart.Contains("+2 wisdom") && !rolledPart.Contains("Armour +2") && !rolledPart.Contains("+1 wisdom"),
                "rolled lines live only in the rolled section");
            Check(mRare.Headline.Contains("Rare") && mRare.Headline.Contains("Ashen Cloak") && mRare.Headline.Contains(mRare.UidShort), "headline carries rarity, name and uid");

            // a weapon shows its base damage as a fixed property
            ItemInstance bladeInstance = Mint(auth, blade, AffixRarity.Uncommon, new AffixRecord("affix_melee_damage", AffixStatKey.MeleeDamage, 2));
            if (bladeInstance != null)
            {
                ItemTooltipModel mBlade = ItemTooltip.ForInstance(bladeInstance);
                Check(mBlade.FixedLines.Exists(l => l.StartsWith("Damage ")) && mBlade.RolledLines.Contains("+2 melee_damage"), "weapon: base damage fixed, rolled melee_damage separate");
            }

            // a stack with no identity gets fixed lines and no rarity
            ItemTooltipModel carried = ItemTooltip.ForDefinition(AssetDatabase.LoadAssetAtPath<ItemData>(EmberdelvePaths.Data + "/Item_EmberCoin.asset"));
            Check(carried != null && !carried.HasRarity && !carried.HasRolledSection && carried.UidShort.Length == 0, "carried coin: no rarity, no rolled section, no uid");

            // the tooltip never writes the shared definition
            Check(ashen.StatTotal(SkillStat.Armor) == 2f && ashen.attributeBonuses.Length == 1, "building tooltips left the definition untouched");
        }

        private void CharacterPanelChecks(PlayerRig rig, ItemInstance rare, Inventory bag, Equipment gear, float[] baseScores)
        {
            GameObject host = new GameObject("CloakStatsHost", typeof(RectTransform));
            cleanup.Add(host);
            var view = new CharacterStatsView(host.transform, 346f);
            view.Configure(rig);
            view.Build();

            Check(CharacterStatsView.AttributeRowCount == 6, "the pane declares six attribute rows");
            Dictionary<string, string> before = ReadPane(host.transform);
            string[] names = { "STRENGTH", "DEXTERITY", "CONSTITUTION", "INTELLIGENCE", "WISDOM", "CHARISMA" };
            foreach (string name in names) Check(before.ContainsKey(name), "pane lists " + name);
            Check(before.ContainsKey("Armour") && before["Armour"].StartsWith("0"), "pane shows armour 0 with nothing worn");

            Check(bag.TryAddInstance(rare, out string reason) && gear.TryEquipFromInventory(bag.IndexOfInstance(rare.Uid), out reason), "wear the rare cloak for the pane: " + reason);
            view.Refresh();
            Dictionary<string, string> after = ReadPane(host.transform);
            int wis = (int)SixAttribute.Wisdom;
            int expectedWis = (int)baseScores[wis] + 3;
            Check(after.TryGetValue("WISDOM", out string wisRow) && wisRow.StartsWith(expectedWis + "  ") && wisRow.Contains("worn +3"),
                "WISDOM row shows the effective score " + expectedWis + " with worn +3: " + (wisRow ?? "missing"));
            Check(after.TryGetValue("Armour", out string armorRow) && armorRow.StartsWith("6"), "Armour row shows 6 with the cloak: " + (armorRow ?? "missing"));
            Check(after["STRENGTH"] == before["STRENGTH"] && after["CHARISMA"] == before["CHARISMA"], "scores the cloak does not touch are unchanged");
            Check(view.RowCount == 6 + 2 + 2 + 3, "the pane has one row per number (D1b: the deprecated Stamina row is gone, 3 -> 2 vitals) and did not grow on refresh (" + view.RowCount + ")");

            gear.TryUnequip(EquipmentSlot.Back, out _);
            view.Refresh();
            Dictionary<string, string> restored = ReadPane(host.transform);
            Check(restored["WISDOM"] == before["WISDOM"] && restored["Armour"] == before["Armour"], "unequip restores the pane exactly");
        }

        private static Dictionary<string, string> ReadPane(Transform host)
        {
            var rows = new Dictionary<string, string>();
            Transform root = host.Find("CharacterRows");
            if (root == null) return rows;
            for (int i = 0; i < 64; i++)
            {
                Transform label = root.Find("StatLabel" + i);
                Transform value = root.Find("StatValue" + i);
                if (label == null || value == null) break;
                rows[label.GetComponent<UnityEngine.UI.Text>().text] = value.GetComponent<UnityEngine.UI.Text>().text;
            }
            return rows;
        }
    }
}
