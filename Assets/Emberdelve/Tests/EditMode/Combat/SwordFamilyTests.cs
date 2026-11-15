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
    /// U5B proof against the shipped assets: the arming sword and the two-handed sword exist as weapon data, catalog items and loot, their hands,
    /// riposte flags and patterns follow the design, the two-handed one fills both hands through the real Equipment rules, and its guard resists
    /// more than the one-handed sword's while its strikes do not break a one-handed sword guard. Numbers are compared with the sibling weapons'
    /// DATA (never typed in).
    /// </summary>
    public sealed class SwordFamilyTests
    {
        private WeaponData _short;
        private WeaponData _maul;
        private WeaponData _arming;
        private WeaponData _great;
        private ItemCatalog _catalog;
        private GameObject _host;
        private Equipment _gear;

        private static WeaponData Weapon(string id)
        {
            foreach (string guid in AssetDatabase.FindAssets("t:WeaponData"))
            {
                WeaponData w = AssetDatabase.LoadAssetAtPath<WeaponData>(AssetDatabase.GUIDToAssetPath(guid));
                if (w != null && w.weaponId == id) return w;
            }

            Assert.Fail("weapon asset missing: " + id);
            return null;
        }

        [OneTimeSetUp]
        public void Load()
        {
            _short = Weapon("ashen_short_sword");
            _maul = Weapon("warden_maul");
            _arming = Weapon("arming_sword");
            _great = Weapon("two_handed_sword");
            _catalog = AssetDatabase.LoadAssetAtPath<ItemCatalog>("Assets/Emberdelve/Data/EmberdelveItemCatalog.asset");
            Assert.That(_catalog, Is.Not.Null);
        }

        [SetUp]
        public void MakeGear()
        {
            _host = new GameObject("SwordFamilyTests");
            _gear = _host.AddComponent<Equipment>();
        }

        [TearDown]
        public void Cleanup()
        {
            if (_host != null) UnityEngine.Object.DestroyImmediate(_host);
        }

        private ItemData ItemFor(WeaponData weapon)
        {
            foreach (ItemData item in _catalog.Items) if (item != null && item.grantedWeapon == weapon) return item;
            Assert.Fail("no catalog item grants " + weapon.weaponId);
            return null;
        }

        [Test]
        public void Hands_AreOneAndTwo()
        {
            Assert.That(_arming.Hands, Is.EqualTo(HandUse.OneHanded));
            Assert.That(_great.Hands, Is.EqualTo(HandUse.TwoHanded));
            Assert.That(_great.twoHanded, Is.True);
            Assert.That(_arming.twoHanded, Is.False);
        }

        [Test]
        public void RiposteFlags_FollowTheDesign()
        {
            Assert.That(_arming.canRiposte && !_arming.riposteWithOffhandShield, Is.True, "the arming sword ripostes only from its own guard");
            Assert.That(_great.canRiposte, Is.True);
            Assert.That(_great.riposteWithOffhandShield, Is.False, "a two-handed sword never has a shield");
            foreach (WeaponData w in new[] { _arming, _great })
            {
                Assert.That(w.RiposteStrikeCount, Is.GreaterThanOrEqualTo(1), w.weaponId);
                for (int i = 0; i < w.RiposteStrikeCount; i++)
                    Assert.That(w.TryResolveRiposteStrike(i, out _, out SwingPatternData p) && p != null, Is.True, w.weaponId + " riposte " + i);
                Assert.That(w.CanBlock, Is.True, w.weaponId + " guards");
            }
        }

        [Test]
        public void Patterns_AreTheAuthoredCombos_AndAllValid()
        {
            Assert.That(_arming.swingPatterns.Length, Is.EqualTo(4), "left-right, right-left, overhead, thrust");
            Assert.That(_great.swingPatterns.Length, Is.EqualTo(4), "wide left-right, wide right-left, overhead, thrust");
            foreach (WeaponData w in new[] { _arming, _great })
            {
                Assert.That(w.UsesSweptMelee, Is.True, w.weaponId);
                Assert.That(w.comboSteps.Length, Is.GreaterThanOrEqualTo(3), w.weaponId + " has a combo");
                foreach (SwingPatternData p in w.swingPatterns)
                {
                    Assert.That(p, Is.Not.Null, w.weaponId);
                    Assert.That(p.Validate(out string problem), Is.True, w.weaponId + ": " + problem);
                }

                foreach (ComboStep step in w.comboSteps)
                    Assert.That(step.patternIndex, Is.InRange(0, w.swingPatterns.Length - 1), w.weaponId + " combo step points at a pattern it owns");
            }

            Assert.That(_arming.swingPatterns[0], Is.SameAs(_short.swingPatterns[0]), "the arming sword reuses the short sword's slashes");
            Assert.That(_arming.swingPatterns[1], Is.SameAs(_short.swingPatterns[1]));
        }

        [Test]
        public void Balance_SitsBetweenTheShortSwordAndTheMaul()
        {
            Assert.That(_arming.reach, Is.GreaterThan(_short.reach), "slightly longer reach");
            Assert.That(_great.reach, Is.GreaterThan(_arming.reach), "longer still");
            Assert.That(_arming.baseDamage, Is.GreaterThan(_short.baseDamage));
            Assert.That(_great.baseDamage, Is.GreaterThan(_arming.baseDamage));
            Assert.That(_great.windupTime, Is.GreaterThan(_arming.windupTime), "slower");
            Assert.That(_great.recoveryTime, Is.GreaterThan(_arming.recoveryTime));
            Assert.That(_great.cooldown, Is.GreaterThan(_arming.cooldown));
            Assert.That(_arming.windupTime, Is.EqualTo(_short.windupTime).Within(0.08f), "standard speed");
        }

        [Test]
        public void Impact_TheTwoHandedSwordDoesNotBreakAOneHandedSwordGuard_ButItsOwnGuardResistsMore()
        {
            Assert.That(_great.impactResist, Is.GreaterThan(_arming.impactResist), "its guard resists more than the one-handed sword's");
            Assert.That(_great.impactResist, Is.GreaterThan(_short.impactResist));
            foreach (SwingPatternData p in _great.swingPatterns)
            {
                float power = _great.impactPower * p.impactPowerScale;
                Assert.That(power, Is.LessThan(_arming.impactResist), p.name + " does not break an arming sword guard");
                Assert.That(power, Is.LessThan(_short.impactResist), p.name + " does not break a short sword guard");
            }

            Assert.That(_great.impactPower, Is.GreaterThan(_arming.impactPower), "but it chips more than the one-handed sword");
            Assert.That(_great.impactPower, Is.LessThan(_maul.impactPower));
        }

        [Test]
        public void Catalog_HasBothItems_ThatGrantTheWeapon_InTheWeaponSlot()
        {
            foreach (WeaponData w in new[] { _arming, _great })
            {
                ItemData item = ItemFor(w);
                Assert.That(item.itemId, Is.EqualTo(w.weaponId), "the catalog id is the weapon id");
                Assert.That(item.equipSlot, Is.EqualTo(EquipmentSlot.Weapon));
                Assert.That(item.value, Is.GreaterThan(0));
                Assert.That(item.maxStack, Is.EqualTo(1));
                Assert.That(_catalog.TryResolveItem(item.itemId, out ItemData resolved, out string reason), Is.True, reason);
                Assert.That(resolved, Is.SameAs(item));
            }

            Assert.That(ItemFor(_arming).value, Is.GreaterThan(ItemFor(_short).value), "value follows the numbers");
            Assert.That(ItemFor(_great).value, Is.GreaterThan(ItemFor(_arming).value));
        }

        [Test]
        public void Loot_CanDropBothSwords()
        {
            var found = new HashSet<WeaponData>();
            foreach (string guid in AssetDatabase.FindAssets("t:LootTable"))
            {
                LootTable table = AssetDatabase.LoadAssetAtPath<LootTable>(AssetDatabase.GUIDToAssetPath(guid));
                if (table == null || table.entries == null) continue;
                foreach (LootEntry e in table.entries)
                    if (e != null && e.item != null && e.item.grantedWeapon != null && e.weight > 0) found.Add(e.item.grantedWeapon);
            }

            Assert.That(found, Does.Contain(_arming), "a chest table can drop the arming sword");
            Assert.That(found, Does.Contain(_great), "a chest table can drop the two-handed sword");
        }

        [Test]
        public void TwoHanded_FillsBothHands_ThroughTheEquipmentRules()
        {
            ItemData great = ItemFor(_great);
            ItemData arming = ItemFor(_arming);
            ItemData shield = ItemFor(Weapon("emberbulwark_shield"));

            Assert.That(_gear.TryCheckHandRules(great, EquipmentSlot.Weapon, out int displace, out string reason), Is.True, reason);
            Assert.That(displace, Is.EqualTo(-1), "an empty off hand displaces nothing");
            Assert.That(_gear.TryCheckHandRules(great, EquipmentSlot.OffHand, out _, out _), Is.False, "never in the off hand");
            Assert.That(_gear.TryCheckHandRules(arming, EquipmentSlot.OffHand, out _, out _), Is.False, "no dual wielding");
            Assert.That(_gear.TryCheckHandRules(shield, EquipmentSlot.OffHand, out _, out reason), Is.True, reason);

            // the same hand rule as the Equipment slot check, seen from the guard: a shield never contributes next to the two-handed sword,
            // and next to the arming sword it does
            CharacterClassData warrior = null;
            foreach (string guid in AssetDatabase.FindAssets("t:CharacterClassData"))
            {
                CharacterClassData c = AssetDatabase.LoadAssetAtPath<CharacterClassData>(AssetDatabase.GUIDToAssetPath(guid));
                if (c != null && c.offhandShield != null && c.primaryWeapon != null) { warrior = c; break; }
            }

            Assert.That(warrior, Is.Not.Null);
            DefenseProfile twoHandedWithShield = DefenseProfile.Resolve(warrior, _great, warrior.offhandShield, 0f);
            DefenseProfile oneHandedWithShield = DefenseProfile.Resolve(warrior, _arming, warrior.offhandShield, 0f);
            Assert.That(twoHandedWithShield.CanBlock, Is.EqualTo(_great.CanBlock), "the two-handed sword fills both hands: the shield adds no block");
            Assert.That(twoHandedWithShield.ImpactResist, Is.EqualTo(_great.impactResist), "its own guard resist");
            Assert.That(oneHandedWithShield.HasShield, Is.True, "the arming sword leaves the off hand to the shield");
        }
    }
}
