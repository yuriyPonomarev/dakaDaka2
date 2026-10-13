using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Level;

namespace Emberdelve.EditorTools
{
    /// <summary>
    /// Creates (and reloads) every ScriptableObject the vertical slice needs. All gameplay numbers
    /// live here, never inside components, so tuning happens in the Inspector afterwards.
    /// </summary>
    internal static class EmberdelveAssetLibrary
    {
        public static GameConfig Config;
        public static CharacterClassData Warrior;
        public static CharacterClassData Archer;
        public static WeaponData Sword;
        public static WeaponData Bow;
        public static WeaponData Claw;
        public static WeaponData ClawElite;
        public static WeaponData Shield;
        public static WeaponData Crossbow;
        public static WeaponData Rapier;
        public static ItemData Coin;
        public static ItemData Draught;
        public static ItemData Relic;
        public static ItemData ArrowBundle;
        public static EnemyData Servitor;
        public static EnemyData Warden;
        public static DungeonLayoutData Layout;
        public static NavGridData NavGrid;

        public static CharacterClassData[] Classes => new[] { Warrior, Archer };

        public static void BuildAll()
        {
            EmberdelveMaterials.BuildAll();
            BuildWeapons();
            BuildItems();
            BuildClasses();
            ApplyDefenseKit();
            BuildEnemies();
            BuildLayout();
            BuildNavGrid();
            BuildConfig();
        }

        private static T LoadOrCreate<T>(string path, System.Action<T> configure) where T : ScriptableObject
        {
            T asset = AssetDatabase.LoadAssetAtPath<T>(path);
            if (asset != null) return asset;

            asset = ScriptableObject.CreateInstance<T>();
            configure(asset);
            AssetDatabase.CreateAsset(asset, path);
            return asset;
        }

        private static void BuildWeapons()
        {
            Sword = LoadOrCreate<WeaponData>(EmberdelvePaths.Data + "/Weapon_AshenShortSword.asset", w =>
            {
                w.weaponId = "ashen_short_sword";
                w.displayName = "Ashen Short Sword";
                w.kind = WeaponKind.Melee;
                w.baseDamage = 26f;
                w.damageType = DamageType.Physical;
                w.windupTime = 0.24f;
                w.activeWindow = 0.14f;
                w.recoveryTime = 0.3f;
                w.cooldown = 0.92f;
                w.reach = 2.3f;
                w.arcAngle = 120f;
                w.hitSphereRadius = 0.6f;
                w.shaftMaterial = EmberdelveMaterials.Blade;
                w.edgeMaterial = EmberdelveMaterials.Blade;
                w.gripMaterial = EmberdelveMaterials.Grip;
            });

            Bow = LoadOrCreate<WeaponData>(EmberdelvePaths.Data + "/Weapon_HollowBow.asset", w =>
            {
                w.weaponId = "hollow_bow";
                w.displayName = "Hollow Bow";
                w.kind = WeaponKind.Ranged;
                w.baseDamage = 34f;
                w.damageType = DamageType.Pierce;
                w.windupTime = 0.05f;
                w.activeWindow = 0.08f;
                w.recoveryTime = 0.18f;
                w.cooldown = 0.55f;
                w.reach = 1.2f;
                w.arcAngle = 60f;
                w.hitSphereRadius = 0.2f;
                w.rangedRange = 46f;
                w.projectileSpeed = 42f;
                w.projectileRadius = 0.05f;
                w.projectileLength = 0.85f;
                w.drawTime = 0.85f;
                w.minChargeToFire = 0.2f;
                w.minChargeDamageScale = 0.4f;
                w.magazineSize = 5;
                w.reloadTime = 1.9f;
                w.falloffStartRange = 18f;
                w.falloffEndRange = 46f;
                w.falloffMinScale = 0.45f;
                w.shaftMaterial = EmberdelveMaterials.BowWood;
                w.edgeMaterial = EmberdelveMaterials.Blade;
                w.gripMaterial = EmberdelveMaterials.Grip;
            });

            Claw = LoadOrCreate<WeaponData>(EmberdelvePaths.Data + "/Weapon_HollowClaw.asset", w =>
            {
                w.weaponId = "hollow_claw";
                w.displayName = "Hollow Claw";
                w.kind = WeaponKind.Melee;
                w.baseDamage = 15f;
                w.damageType = DamageType.Physical;
                w.windupTime = 0.6f;
                w.activeWindow = 0.16f;
                w.recoveryTime = 0.4f;
                w.cooldown = 2f;
                w.reach = 2.1f;
                w.arcAngle = 150f;
                w.hitSphereRadius = 0.55f;
                w.shaftMaterial = EmberdelveMaterials.EnemyBody;
                w.edgeMaterial = EmberdelveMaterials.EnemyHead;
                w.gripMaterial = EmberdelveMaterials.EnemyBody;
            });

            ClawElite = LoadOrCreate<WeaponData>(EmberdelvePaths.Data + "/Weapon_WardenMaul.asset", w =>
            {
                w.weaponId = "warden_maul";
                w.displayName = "Warden Maul";
                w.kind = WeaponKind.Melee;
                w.baseDamage = 26f;
                w.damageType = DamageType.Physical;
                w.windupTime = 0.85f;
                w.activeWindow = 0.18f;
                w.recoveryTime = 0.5f;
                w.cooldown = 2.5f;
                w.reach = 2.5f;
                w.arcAngle = 160f;
                w.hitSphereRadius = 0.7f;
                w.shaftMaterial = EmberdelveMaterials.EliteBody;
                w.edgeMaterial = EmberdelveMaterials.EnemyHead;
                w.gripMaterial = EmberdelveMaterials.EliteBody;
            });

            Shield = LoadOrCreate<WeaponData>(EmberdelvePaths.Data + "/Weapon_EmberbulwarkShield.asset", w =>
            {
                w.weaponId = "emberbulwark_shield";
                w.displayName = "Emberbulwark Shield";
                w.kind = WeaponKind.Melee;
                w.baseDamage = 9f;
                w.damageType = DamageType.Physical;
                w.windupTime = 0.3f;
                w.activeWindow = 0.12f;
                w.recoveryTime = 0.35f;
                w.cooldown = 1.4f;
                w.reach = 1.4f;
                w.arcAngle = 90f;
                w.hitSphereRadius = 0.45f;
                w.shaftMaterial = EmberdelveMaterials.Blade;
                w.edgeMaterial = EmberdelveMaterials.Blade;
                w.gripMaterial = EmberdelveMaterials.Grip;
            });

            Crossbow = LoadOrCreate<WeaponData>(EmberdelvePaths.Data + "/Weapon_EmberCrossbow.asset", w =>
            {
                w.weaponId = "ember_crossbow";
                w.displayName = "Ember Crossbow";
                w.kind = WeaponKind.Ranged;
                w.baseDamage = 24f;
                w.damageType = DamageType.Pierce;
                w.ammoDisplayName = "Bolts";
                w.windupTime = 0.05f;
                w.activeWindow = 0.08f;
                w.recoveryTime = 0.2f;
                w.cooldown = 1.1f;
                w.reach = 1.2f;
                w.arcAngle = 60f;
                w.hitSphereRadius = 0.2f;
                w.rangedRange = 34f;
                w.projectileSpeed = 54f;
                w.projectileRadius = 0.05f;
                w.projectileLength = 0.5f;
                w.drawTime = 0.3f;
                w.minChargeToFire = 0.12f;
                w.minChargeDamageScale = 0.55f;
                w.magazineSize = 4;
                w.reloadTime = 2.2f;
                w.falloffStartRange = 12f;
                w.falloffEndRange = 34f;
                w.falloffMinScale = 0.5f;
                w.shaftMaterial = EmberdelveMaterials.BowWood;
                w.edgeMaterial = EmberdelveMaterials.Blade;
                w.gripMaterial = EmberdelveMaterials.Grip;
            });

            Rapier = LoadOrCreate<WeaponData>(EmberdelvePaths.Data + "/Weapon_EmberRapier.asset", w =>
            {
                w.weaponId = "ember_rapier";
                w.displayName = "Ember Rapier";
                w.kind = WeaponKind.Melee;
                w.baseDamage = 19f;
                w.damageType = DamageType.Pierce;
                w.windupTime = 0.16f;
                w.activeWindow = 0.12f;
                w.recoveryTime = 0.22f;
                w.cooldown = 0.68f;
                w.reach = 2.7f;
                w.arcAngle = 70f;
                w.hitSphereRadius = 0.45f;
                w.shaftMaterial = EmberdelveMaterials.Blade;
                w.edgeMaterial = EmberdelveMaterials.Blade;
                w.gripMaterial = EmberdelveMaterials.Grip;
            });
        }

        /// <summary>
        /// Idempotent pass that writes the defense capability model onto assets that already existed
        /// before the fields were added. LoadOrCreate only configures a brand new asset, so without
        /// this pass the pre-existing sword, bow and claws would all still have no capabilities at all.
        ///
        /// Rules encoded here: a shield blocks and can never parry; a main-hand weapon is the only
        /// place Parry may be declared, and it is declared nowhere yet because the parry timing system
        /// is a later milestone.
        /// </summary>
        private static void ApplyDefenseKit()
        {
            SetDefense(Sword, DefenseRole.Weapon, DefenseCapability.Block, -1f, -1f, false);
            SetDefense(Bow, DefenseRole.Weapon, DefenseCapability.None, -1f, -1f, true);
            SetDefense(Claw, DefenseRole.Weapon, DefenseCapability.None, -1f, -1f, false);
            SetDefense(ClawElite, DefenseRole.Weapon, DefenseCapability.None, -1f, -1f, false);
            SetDefense(Crossbow, DefenseRole.Weapon, DefenseCapability.None, -1f, -1f, true);

            // The rapier is the first asset to actually claim Parry. It is still only a capability flag:
            // there is no parry timing window, animation or parry damage anywhere in the runtime yet.
            SetDefense(Rapier, DefenseRole.Weapon, DefenseCapability.Block | DefenseCapability.Parry, -1f, -1f, false);

            // Deliberately declares Parry to prove the shield rule masks it back off.
            SetDefense(Shield, DefenseRole.Shield, DefenseCapability.Block | DefenseCapability.Parry, -1f, -1f, false);

            if (Warrior != null && Warrior.offhandShield != Shield)
            {
                Warrior.offhandShield = Shield;
                EditorUtility.SetDirty(Warrior);
            }

            if (Warrior != null && (Warrior.secondaryWeapon != Crossbow
                                    || Warrior.secondaryStartingReserveAmmo != 6
                                    || Warrior.secondaryMaxReserveAmmo != 16))
            {
                Warrior.secondaryWeapon = Crossbow;
                Warrior.secondaryStartingReserveAmmo = 6;
                Warrior.secondaryMaxReserveAmmo = 16;
                EditorUtility.SetDirty(Warrior);
            }

            if (Archer != null && (Archer.secondaryWeapon != Rapier
                                   || Archer.secondaryStartingReserveAmmo != 0
                                   || Archer.secondaryMaxReserveAmmo != 0))
            {
                Archer.secondaryWeapon = Rapier;
                Archer.secondaryStartingReserveAmmo = 0;
                Archer.secondaryMaxReserveAmmo = 0;
                EditorUtility.SetDirty(Archer);
            }

            if (Archer != null && Archer.offhandShield != null)
            {
                Archer.offhandShield = null;
                EditorUtility.SetDirty(Archer);
            }

            AssetDatabase.SaveAssets();
        }

        private static void SetDefense(WeaponData weapon, DefenseRole role, DefenseCapability capabilities,
            float reductionOverride, float arcOverride, bool twoHanded)
        {
            if (weapon == null) return;

            if (weapon.defenseRole != role
                || weapon.capabilities != capabilities
                || weapon.twoHanded != twoHanded
                || !Mathf.Approximately(weapon.blockReductionOverride, reductionOverride)
                || !Mathf.Approximately(weapon.blockArcOverride, arcOverride))
            {
                weapon.defenseRole = role;
                weapon.capabilities = capabilities;
                weapon.twoHanded = twoHanded;
                weapon.blockReductionOverride = reductionOverride;
                weapon.blockArcOverride = arcOverride;
                EditorUtility.SetDirty(weapon);
            }
        }

        private static void BuildItems()
        {
            Coin = LoadOrCreate<ItemData>(EmberdelvePaths.Data + "/Item_EmberCoin.asset", i =>
            {
                i.itemId = "ember_coin";
                i.displayName = "Ember Coin";
                i.description = "Minted in the old halls. Worth something to whoever waits above.";
                i.kind = ItemKind.Currency;
                i.value = 12;
                i.maxStack = 8;
                i.iconColor = new Color(0.85f, 0.68f, 0.25f);
                i.worldMaterial = EmberdelveMaterials.CoinMat;
                i.worldSize = new Vector3(0.22f, 0.22f, 0.22f);
            });

            Draught = LoadOrCreate<ItemData>(EmberdelvePaths.Data + "/Item_BloodDraught.asset", i =>
            {
                i.itemId = "blood_draught";
                i.displayName = "Blood Draught";
                i.description = "Thick, warm, faintly metallic. Press 1-4 to drink.";
                i.kind = ItemKind.Consumable;
                i.value = 18;
                i.maxStack = 3;
                i.healAmount = 45;
                i.iconColor = new Color(0.75f, 0.18f, 0.18f);
                i.worldMaterial = EmberdelveMaterials.DraughtMat;
                i.worldSize = new Vector3(0.24f, 0.3f, 0.24f);
            });

            Relic = LoadOrCreate<ItemData>(EmberdelvePaths.Data + "/Item_AshenRelic.asset", i =>
            {
                i.itemId = "ashen_relic";
                i.displayName = "Ashen Relic";
                i.description = "The gate only answers to someone carrying something of the deep.";
                i.kind = ItemKind.Relic;
                i.value = 90;
                i.maxStack = 2;
                i.iconColor = new Color(0.95f, 0.78f, 0.35f);
                i.worldMaterial = EmberdelveMaterials.RelicMat;
                i.worldSize = new Vector3(0.3f, 0.36f, 0.3f);
            });

            ArrowBundle = LoadOrCreate<ItemData>(EmberdelvePaths.Data + "/Item_ArrowBundle.asset", i =>
            {
                i.itemId = "arrow_bundle";
                i.displayName = "Arrow Bundle";
                i.description = "Eight fletched shafts. Press 1-4 to restock the quiver.";
                i.kind = ItemKind.AmmoPack;
                i.value = 10;
                i.maxStack = 4;
                i.ammoGrant = 8;
                i.iconColor = new Color(0.6f, 0.62f, 0.55f);
                i.worldMaterial = EmberdelveMaterials.ArrowMat;
                i.worldSize = new Vector3(0.26f, 0.26f, 0.26f);
            });
        }

        private static void BuildClasses()
        {
            Warrior = LoadOrCreate<CharacterClassData>(EmberdelvePaths.Data + "/Class_Warrior.asset", c =>
            {
                c.classId = "warrior";
                c.displayName = "Warrior";
                c.description = "Slow, heavy, hard to kill. Holds a shield-line of steel and walks out with what he carried in.";
                c.accentColor = new Color(0.85f, 0.42f, 0.22f);
                c.maxHealth = 150;
                c.healthRegenPerSecond = 1.5f;
                c.regenDelay = 8f;
                c.moveSpeed = 3.5f;
                c.sprintSpeed = 5.2f;
                c.crouchSpeed = 1.7f;
                c.jumpHeight = 1.1f;
                c.standHeight = 1.8f;
                c.crouchHeight = 1.15f;
                c.standEyeHeight = 1.66f;
                c.crouchEyeHeight = 1.05f;
                c.primaryWeapon = Sword;
                c.startingItems = new[] { Draught };
                c.startingReserveAmmo = 0;
                c.maxReserveAmmo = 0;
                c.blockReduction = 0.7f;
                c.blockArc = 140f;
                c.guardSpeedMultiplier = 0.45f;
            });

            Archer = LoadOrCreate<CharacterClassData>(EmberdelvePaths.Data + "/Class_Archer.asset", c =>
            {
                c.classId = "archer";
                c.displayName = "Archer";
                c.description = "Quick and fragile. Wins by distance, and by leaving before the thing reaches you.";
                c.accentColor = new Color(0.35f, 0.72f, 0.62f);
                c.maxHealth = 100;
                c.healthRegenPerSecond = 2f;
                c.regenDelay = 7f;
                c.moveSpeed = 4.4f;
                c.sprintSpeed = 6.4f;
                c.crouchSpeed = 2.1f;
                c.jumpHeight = 1.25f;
                c.standHeight = 1.75f;
                c.crouchHeight = 1.1f;
                c.standEyeHeight = 1.64f;
                c.crouchEyeHeight = 1.0f;
                c.primaryWeapon = Bow;
                c.startingItems = new[] { ArrowBundle, Draught };
                c.startingReserveAmmo = 20;
                c.maxReserveAmmo = 34;
                c.blockReduction = 0f;
                c.blockArc = 90f;
                c.guardSpeedMultiplier = 1f;
            });
        }

        private static void BuildEnemies()
        {
            Servitor = LoadOrCreate<EnemyData>(EmberdelvePaths.Data + "/Enemy_HollowServitor.asset", e =>
            {
                e.enemyId = "hollow_servitor";
                e.displayName = "Hollow Servitor";
                e.isElite = false;
                e.maxHealth = 70;
                e.attackWeapon = Claw;
                e.attackRange = 2.2f;
                e.attackWindup = 0.6f;
                e.attackCooldown = 2f;
                e.attackTelegraphHeight = 0.5f;
                e.patrolSpeed = 1.05f;
                e.chaseSpeed = 2.7f;
                e.patrolRadius = 6f;
                e.turnRate = 380f;
                e.sightRange = 14f;
                e.sightCone = 130f;
                e.hearingRange = 8f;
                e.memoryDuration = 6f;
                e.investigateTime = 3.5f;
                e.lootTable = new[] { Coin, Draught, ArrowBundle };
                e.lootRolls = 2;
                e.lootDropChance = 0.8f;
                e.bodyMaterial = EmberdelveMaterials.EnemyBody;
                e.eliteTint = new Color(1f, 0.45f, 0.35f);
            });

            Warden = LoadOrCreate<EnemyData>(EmberdelvePaths.Data + "/Enemy_AshenWarden.asset", e =>
            {
                e.enemyId = "ashen_warden";
                e.displayName = "Ashen Warden";
                e.isElite = true;
                e.maxHealth = 160;
                e.attackWeapon = ClawElite;
                e.attackRange = 2.5f;
                e.attackWindup = 0.85f;
                e.attackCooldown = 2.5f;
                e.attackTelegraphHeight = 0.7f;
                e.patrolSpeed = 1.15f;
                e.chaseSpeed = 3.1f;
                e.patrolRadius = 7f;
                e.turnRate = 300f;
                e.sightRange = 18f;
                e.sightCone = 140f;
                e.hearingRange = 11f;
                e.memoryDuration = 8f;
                e.investigateTime = 4f;
                e.lootTable = new[] { Relic, Coin, Draught };
                e.lootRolls = 3;
                e.lootDropChance = 0.9f;
                e.bodyMaterial = EmberdelveMaterials.EliteBody;
                e.eliteTint = new Color(1f, 0.4f, 0.3f);
            });
        }

        private static void BuildLayout()
        {
            Layout = LoadOrCreate<DungeonLayoutData>(EmberdelvePaths.Data + "/DungeonLayout_AshenHalls.asset", l =>
            {
                l.levelName = "The Ashen Halls";
                l.gridWidth = 26;
                l.gridHeight = 22;
                l.cellSize = 2;
                l.wallHeight = 3.6f;
                l.wallThickness = 0.45f;
                l.origin = Vector3.zero;

                l.rooms.Add(new RoomSpec { roomName = "Entry Hall", minCell = new Vector2Int(2, 2), maxCell = new Vector2Int(6, 5) });
                l.rooms.Add(new RoomSpec { roomName = "Library", minCell = new Vector2Int(11, 2), maxCell = new Vector2Int(15, 5) });
                l.rooms.Add(new RoomSpec { roomName = "Central Hall", minCell = new Vector2Int(10, 8), maxCell = new Vector2Int(16, 14) });
                l.rooms.Add(new RoomSpec { roomName = "Armory", minCell = new Vector2Int(2, 9), maxCell = new Vector2Int(6, 13) });
                l.rooms.Add(new RoomSpec { roomName = "Chapel", minCell = new Vector2Int(19, 8), maxCell = new Vector2Int(23, 13) });
                l.rooms.Add(new RoomSpec { roomName = "Crypt", minCell = new Vector2Int(3, 16), maxCell = new Vector2Int(9, 20) });
                l.rooms.Add(new RoomSpec { roomName = "Ember Gate", minCell = new Vector2Int(18, 16), maxCell = new Vector2Int(23, 20) });

                l.corridors.Add(new CorridorSpec { corridorName = "Entry to Library", fromCell = new Vector2Int(6, 3), toCell = new Vector2Int(11, 3), width = 2 });
                l.corridors.Add(new CorridorSpec { corridorName = "Entry to Central", fromCell = new Vector2Int(4, 5), toCell = new Vector2Int(4, 8), width = 2 });
                l.corridors.Add(new CorridorSpec { corridorName = "Library to Central", fromCell = new Vector2Int(13, 5), toCell = new Vector2Int(13, 8), width = 2 });
                l.corridors.Add(new CorridorSpec { corridorName = "Armory to Central", fromCell = new Vector2Int(6, 11), toCell = new Vector2Int(10, 11), width = 2 });
                l.corridors.Add(new CorridorSpec { corridorName = "Central to Chapel", fromCell = new Vector2Int(16, 11), toCell = new Vector2Int(19, 11), width = 2 });
                l.corridors.Add(new CorridorSpec { corridorName = "Central to Crypt", fromCell = new Vector2Int(12, 14), toCell = new Vector2Int(12, 16), width = 2 });
                l.corridors.Add(new CorridorSpec { corridorName = "Crypt to Gate", fromCell = new Vector2Int(9, 18), toCell = new Vector2Int(18, 18), width = 2 });
                l.corridors.Add(new CorridorSpec { corridorName = "Chapel to Gate", fromCell = new Vector2Int(21, 13), toCell = new Vector2Int(21, 16), width = 2 });

                l.playerStartCell = new Vector2Int(4, 3);
                l.extractionCell = new Vector2Int(21, 19);

                l.lootSpawns.Add(new LootSpawnSpec { cell = new Vector2Int(3, 4), item = Coin, amount = 3 });
                l.lootSpawns.Add(new LootSpawnSpec { cell = new Vector2Int(13, 3), item = ArrowBundle, amount = 1 });
                l.lootSpawns.Add(new LootSpawnSpec { cell = new Vector2Int(5, 11), item = Draught, amount = 1 });
                l.lootSpawns.Add(new LootSpawnSpec { cell = new Vector2Int(12, 10), item = Coin, amount = 2 });
                l.lootSpawns.Add(new LootSpawnSpec { cell = new Vector2Int(21, 10), item = Relic, amount = 1 });
                l.lootSpawns.Add(new LootSpawnSpec { cell = new Vector2Int(5, 18), item = Relic, amount = 1 });
                l.lootSpawns.Add(new LootSpawnSpec { cell = new Vector2Int(14, 13), item = ArrowBundle, amount = 1 });
                l.lootSpawns.Add(new LootSpawnSpec { cell = new Vector2Int(11, 9), item = Draught, amount = 1 });

                l.enemySpawns.Add(new EnemySpawnSpec { cell = new Vector2Int(13, 11), enemy = Servitor });
                l.enemySpawns.Add(new EnemySpawnSpec { cell = new Vector2Int(13, 4), enemy = Servitor });
                l.enemySpawns.Add(new EnemySpawnSpec { cell = new Vector2Int(4, 11), enemy = Servitor });
                l.enemySpawns.Add(new EnemySpawnSpec { cell = new Vector2Int(6, 18), enemy = Servitor });
                l.enemySpawns.Add(new EnemySpawnSpec { cell = new Vector2Int(21, 12), enemy = Warden });
                l.enemySpawns.Add(new EnemySpawnSpec { cell = new Vector2Int(19, 17), enemy = Servitor });

                l.torches.Add(new TorchSpec { cell = new Vector2Int(3, 2), height = 2.5f, range = 9f, intensity = 13f });
                l.torches.Add(new TorchSpec { cell = new Vector2Int(14, 2), height = 2.5f, range = 9f, intensity = 13f });
                l.torches.Add(new TorchSpec { cell = new Vector2Int(10, 8), height = 2.6f, range = 11f, intensity = 16f });
                l.torches.Add(new TorchSpec { cell = new Vector2Int(16, 14), height = 2.6f, range = 11f, intensity = 16f });
                l.torches.Add(new TorchSpec { cell = new Vector2Int(3, 13), height = 2.5f, range = 9f, intensity = 13f });
                l.torches.Add(new TorchSpec { cell = new Vector2Int(22, 9), height = 2.5f, range = 10f, intensity = 15f });
                l.torches.Add(new TorchSpec { cell = new Vector2Int(4, 19), height = 2.5f, range = 9f, intensity = 13f });
                l.torches.Add(new TorchSpec { cell = new Vector2Int(9, 17), height = 2.4f, range = 8f, intensity = 11f });
                l.torches.Add(new TorchSpec { cell = new Vector2Int(18, 20), height = 2.6f, range = 10f, intensity = 15f });
                l.torches.Add(new TorchSpec { cell = new Vector2Int(4, 7), height = 2.3f, range = 7f, intensity = 10f });
            });

            List<string> problems = Layout.Validate();
            for (int i = 0; i < problems.Count; i++) Debug.LogWarning("[Emberdelve][Layout] " + problems[i]);
        }

        private static void BuildNavGrid()
        {
            NavGrid = LoadOrCreate<NavGridData>(EmberdelvePaths.Data + "/EmberdelveNavGrid.asset", g =>
            {
                g.width = Layout.gridWidth;
                g.height = Layout.gridHeight;
                g.cellSize = Layout.cellSize;
                g.origin = Layout.origin;
                g.navPlaneHeight = 0f;
                g.mask = Layout.BuildNavMask();
            });

            NavGrid.width = Layout.gridWidth;
            NavGrid.height = Layout.gridHeight;
            NavGrid.cellSize = Layout.cellSize;
            NavGrid.origin = Layout.origin;
            NavGrid.mask = Layout.BuildNavMask();
            EditorUtility.SetDirty(NavGrid);
        }

        private static void BuildConfig()
        {
            Config = LoadOrCreate<GameConfig>(EmberdelvePaths.Data + "/EmberdelveGameConfig.asset", c =>
            {
                c.lobbySceneName = GameConfig.DefaultLobbySceneName;
                c.dungeonSceneName = GameConfig.DefaultDungeonSceneName;
            });

            Config.levelTitle = Layout.levelName;
            Config.objectiveText = "Recover at least one relic, then hold [E] at the Ember Gate.";
            Config.requireLootToExtract = true;
            Config.extractionHoldDuration = 3f;
            Config.interactionRange = 2.4f;
            Config.interactSphereRadius = 0.45f;
            Config.inventoryCapacity = 8;
            Config.mouseSensitivity = 1.2f;
            Config.fieldOfView = 68f;

            Config.levelMask = LayerMask.GetMask(GameConfig.LevelLayerName);
            Config.playerMask = LayerMask.GetMask(GameConfig.PlayerLayerName);
            Config.enemyMask = LayerMask.GetMask(GameConfig.EnemyLayerName);
            Config.interactMask = LayerMask.GetMask(GameConfig.LootLayerName, GameConfig.InteractLayerName);
            Config.hitMask = LayerMask.GetMask(GameConfig.PlayerLayerName, GameConfig.EnemyLayerName);
            Config.projectileMask = LayerMask.GetMask(GameConfig.ProjectileLayerName);

            EditorUtility.SetDirty(Config);
        }
    }
}
