using System;
using System.Collections.Generic;
using Emberdelve.BladeDefense;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Items;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Emberdelve.Combat.Tests
{
    /// <summary>
    /// U5A rules proof against the shipped assets: the hand rule is derived from weapon data, the slot validation (server side, before anything
    /// moves) follows it, the guard and the riposte eligibility follow the CURRENT hands, the bare fists are not an item, and one uid is one item.
    /// Expectations are derived from the data, never typed in per weapon.
    /// </summary>
    public sealed class HandsRulesTests
    {
        private static readonly List<WeaponData> Weapons = new List<WeaponData>();
        private ItemCatalog _catalog;
        private CharacterClassData _warrior;
        private GameObject _host;
        private Equipment _gear;

        [OneTimeSetUp]
        public void Load()
        {
            Weapons.Clear();
            foreach (string guid in AssetDatabase.FindAssets("t:WeaponData"))
            {
                WeaponData w = AssetDatabase.LoadAssetAtPath<WeaponData>(AssetDatabase.GUIDToAssetPath(guid));
                if (w != null && w.weaponId != null && w.weaponId.IndexOf("test", StringComparison.OrdinalIgnoreCase) < 0) Weapons.Add(w);
            }

            Assert.That(Weapons.Count, Is.GreaterThan(5), "the shipped weapons are found");
            _catalog = AssetDatabase.LoadAssetAtPath<ItemCatalog>("Assets/Emberdelve/Data/EmberdelveItemCatalog.asset");
            Assert.That(_catalog, Is.Not.Null);
            foreach (string guid in AssetDatabase.FindAssets("t:CharacterClassData"))
            {
                CharacterClassData c = AssetDatabase.LoadAssetAtPath<CharacterClassData>(AssetDatabase.GUIDToAssetPath(guid));
                if (c != null && c.offhandShield != null && c.primaryWeapon != null) { _warrior = c; break; }
            }

            Assert.That(_warrior, Is.Not.Null, "a shipped class with a sword and a shield exists");
        }

        [SetUp]
        public void MakeGear()
        {
            _host = new GameObject("HandsRulesTests");
            _gear = _host.AddComponent<Equipment>();
        }

        [TearDown]
        public void Cleanup()
        {
            if (_host != null) UnityEngine.Object.DestroyImmediate(_host);
        }

        private ItemData Item(string id)
        {
            Assert.That(_catalog.TryResolveItem(id, out ItemData item, out string reason), Is.True, reason);
            return item;
        }

        [Test]
        public void HandUseIsDerivedFromWeaponData()
        {
            foreach (WeaponData w in Weapons)
            {
                HandUse expected = w.defenseRole == DefenseRole.Shield ? HandUse.OffHandOnly : (w.twoHanded ? HandUse.TwoHanded : HandUse.OneHanded);
                Assert.That(w.Hands, Is.EqualTo(expected), w.weaponId);
            }

            Assert.That(_warrior.offhandShield.Hands, Is.EqualTo(HandUse.OffHandOnly), "the shipped shield is off-hand only");
            foreach (WeaponData w in Weapons)
                if (w.IsRanged) Assert.That(w.Hands, Is.EqualTo(HandUse.TwoHanded), w.weaponId + ": ranged weapons fill both hands");
        }

        [Test]
        public void BareFistsAreAWeaponThatIsNeverAnItem()
        {
            WeaponData fists = null;
            foreach (WeaponData w in Weapons) if (w.isUnarmed) { Assert.That(fists, Is.Null, "exactly one unarmed weapon"); fists = w; }
            Assert.That(fists, Is.Not.Null);
            Assert.That(fists.CanBlock, Is.False);
            Assert.That(fists.CanParry, Is.False);
            Assert.That(fists.UsesSweptMelee, Is.True);
            Assert.That(fists.canRiposte, Is.False);

            foreach (ItemData item in _catalog.Items)
                Assert.That(item.grantedWeapon, Is.Not.SameAs(fists), item.itemId + " must not grant the fists");

            ItemData sneaky = ScriptableObject.CreateInstance<ItemData>();
            try
            {
                sneaky.itemId = "test_fist_item";
                sneaky.equipSlot = EquipmentSlot.Weapon;
                sneaky.grantedWeapon = fists;
                Assert.That(_gear.TryCheckHandRules(sneaky, EquipmentSlot.Weapon, out _, out string reason), Is.False, "a fist item is refused");
                Assert.That(reason, Does.Contain("fists"));
            }
            finally { UnityEngine.Object.DestroyImmediate(sneaky); }
        }

        [Test]
        public void SlotValidationFollowsTheHandRule()
        {
            foreach (ItemData item in _catalog.Items)
            {
                WeaponData w = item.grantedWeapon;
                if (w == null) continue;
                foreach (EquipmentSlot slot in new[] { EquipmentSlot.Weapon, EquipmentSlot.OffHand, EquipmentSlot.AltWeapon, EquipmentSlot.AltOffHand })
                {
                    bool offSlot = EquipmentSlots.IsOffHand(slot);
                    bool expected = w.Hands == HandUse.OffHandOnly ? offSlot : !offSlot;
                    bool ok = _gear.TryCheckHandRules(item, slot, out int displace, out string reason);
                    Assert.That(ok, Is.EqualTo(expected), item.itemId + " into " + slot + ": " + reason);
                    Assert.That(displace, Is.EqualTo(-1), "an empty hand displaces nothing");
                    if (!ok) Assert.That(reason, Is.Not.Null.And.Not.Empty);
                }
            }
        }

        [Test]
        public void NonHandSlotsAreNotSubjectToHandRules()
        {
            foreach (ItemData item in _catalog.Items)
            {
                if (item.grantedWeapon != null) continue;
                Assert.That(_gear.TryCheckHandRules(item, EquipmentSlot.Chest, out _, out _), Is.True, item.itemId);
            }
        }

        [Test]
        public void GuardAndRiposteFollowTheCurrentHands()
        {
            WeaponData sword = _warrior.primaryWeapon;
            WeaponData shield = _warrior.offhandShield;
            WeaponData fists = null;
            WeaponData twoHander = null;
            foreach (WeaponData w in Weapons)
            {
                if (w.isUnarmed) fists = w;
                if (w.Hands == HandUse.TwoHanded && w.IsRanged && twoHander == null) twoHander = w;
            }

            DefenseProfile both = DefenseProfile.Resolve(_warrior, sword, shield, 0f);
            DefenseProfile swordOnly = DefenseProfile.Resolve(_warrior, sword, null, 0f);
            DefenseProfile bareWithShield = DefenseProfile.Resolve(_warrior, fists, shield, 0f);
            DefenseProfile bare = DefenseProfile.Resolve(_warrior, fists, null, 0f);
            DefenseProfile twoHandedWithShield = DefenseProfile.Resolve(_warrior, twoHander, shield, 0f);

            Assert.That(both.HasShield, Is.True, "sword and shield: a shield guard");
            Assert.That(swordOnly.HasShield, Is.False, "sword alone: no shield");
            Assert.That(swordOnly.CanBlock, Is.EqualTo(sword.CanBlock), "sword alone blocks exactly as the sword's data says");
            Assert.That(swordOnly.CanParry, Is.EqualTo(sword.CanParry));
            Assert.That(both.CanParry, Is.EqualTo(sword.CanParry), "a shield never adds a parry");
            Assert.That(bare.CanBlock, Is.False, "bare fists cannot block");
            Assert.That(bare.CanParry, Is.False, "bare fists cannot parry");
            Assert.That(bare.HasShield, Is.False);
            Assert.That(bareWithShield.HasShield, Is.True, "fists and a shield: the shield still guards");
            Assert.That(bareWithShield.CanParry, Is.False);
            Assert.That(twoHandedWithShield.CanBlock, Is.EqualTo(twoHander.CanBlock), "a two-handed weapon never lets a shield contribute its block");
            Assert.That(twoHandedWithShield.ImpactResist, Is.EqualTo(twoHander.CanBlock ? twoHander.impactResist : 0f), "nor its impact resist");

            // riposte eligibility derives from the same hands: shield in hand needs the sword's explicit allowance
            bool plain = RiposteRules.KitCanRiposte(sword.canRiposte, sword.IsShield, swordOnly.HasShield, sword.IsRanged, sword.RiposteStrikeCount, sword.riposteWithOffhandShield);
            bool paired = RiposteRules.KitCanRiposte(sword.canRiposte, sword.IsShield, both.HasShield, sword.IsRanged, sword.RiposteStrikeCount, sword.riposteWithOffhandShield);
            Assert.That(plain, Is.EqualTo(sword.CanRiposteWeapon));
            Assert.That(paired, Is.EqualTo(sword.CanRiposteWeapon && sword.riposteWithOffhandShield));
            Assert.That(RiposteRules.KitCanRiposte(fists.canRiposte, fists.IsShield, false, fists.IsRanged, fists.RiposteStrikeCount, fists.riposteWithOffhandShield), Is.False, "fists never riposte");
        }

        [Test]
        public void OneUidIsOneItemInABag()
        {
            string reason = null;
            GameObject bagHost = new GameObject("HandsRulesBag");
            try
            {
                Inventory bag = bagHost.AddComponent<Inventory>();
                bag.SetCatalog(_catalog);
                bag.ConfigureGrid(10, 6);
                ItemData shieldItem = Item("emberbulwark_shield");
                Assert.That(bag.TryMintKitInstance(shieldItem, out ItemInstance shield, out reason), Is.True, reason);
                Assert.That(bag.TryAddInstance(shield, out reason), Is.True, reason);
                int unitsBefore = bag.TotalUnits;
                Assert.That(bag.TryAddInstance(shield, out reason), Is.False, "the same instance cannot enter the bag twice");
                Assert.That(bag.TotalUnits, Is.EqualTo(unitsBefore));

                // lay it down and take it up: the same uid, exactly once
                int index = bag.IndexOfInstance(shield.Uid);
                Assert.That(bag.TryRemoveAt(index, 1), Is.EqualTo(1));
                Assert.That(bag.HoldsInstanceUid(shield.Uid), Is.False, "dropped: not in the bag");
                Assert.That(bag.TryAddInstance(shield, out reason), Is.True, reason);
                Assert.That(bag.HoldsInstanceUid(shield.Uid), Is.True);
                Assert.That(bag.TotalUnits, Is.EqualTo(unitsBefore), "after the round trip the bag holds the same units");
            }
            finally { UnityEngine.Object.DestroyImmediate(bagHost); }
        }
    }
}
