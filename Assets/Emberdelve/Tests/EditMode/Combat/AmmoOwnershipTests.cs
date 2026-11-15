using System.Collections.Generic;
using Emberdelve.Combat;
using NUnit.Framework;

namespace Emberdelve.Combat.Tests
{
    public sealed class AmmoOwnershipTests
    {
        private const string Uid = "e07dc3bd144d40ce8e70ae49b6f993bc";
        private const string Weapon = "test_bow";
        private Dictionary<string, CombatKitState.AmmoPool.LoadedRounds> _magazines;
        [SetUp] public void Setup()
        {
            Assert.That(System.Guid.TryParseExact(Uid, "N", out _), Is.True, "fixture uses a physical item UID");
            _magazines = new Dictionary<string, CombatKitState.AmmoPool.LoadedRounds>();
        }
        private static KitSeed Seed(string uid, int loaded, int reserve, KitSeedReason reason = KitSeedReason.Weapon)
            => new KitSeed { Reason = reason, ActiveWeaponId = Weapon, ActiveAmmoPoolId = uid,
                Pools = new[] { new KitAmmoSeed { WeaponId = Weapon, PoolId = uid, ReserveId = Weapon,
                    MagazineSize = 5, Magazine = loaded, Reserve = reserve, MaxReserve = 20, ReloadTime = 1f } } };
        private CombatKitState Holder(int loaded, int reserve, string uid = Uid)
        {
            var state = new CombatKitState();
            state.ApplySeed(Seed(uid, loaded, reserve, KitSeedReason.ClassApply), true);
            state.BindLoadedRounds(_magazines);
            return state;
        }

        [Test] public void NewHolderAdoptsActualMagazineButKeepsOwnLooseReserve()
        {
            var first = Holder(4, 7);
            Assert.That(first.TryConsumeAmmo(1), Is.True);
            var second = Holder(0, 2);
            Assert.That(second.Active.Magazine, Is.EqualTo(3));
            Assert.That(second.Active.Reserve, Is.EqualTo(2));
            Assert.That(first.Active.Reserve, Is.EqualTo(7));
        }

        [Test] public void ReturnedWeaponUsesLatestShotsAndReloadNotPreviousHoldersCachedSeed()
        {
            var first = Holder(4, 7);
            Assert.That(first.TryConsumeAmmo(1), Is.True);
            var second = Holder(0, 2);
            Assert.That(second.TryConsumeAmmo(2), Is.True);
            Assert.That(second.TryStartReload(1f), Is.True);
            second.Tick(1.01f);
            Assert.That(second.Active.Magazine, Is.EqualTo(3));
            Assert.That(second.Active.Reserve, Is.Zero);
            first.ApplySeed(Seed(Uid, 4, 7), false);
            first.BindLoadedRounds(_magazines);
            Assert.That(first.Active.Magazine, Is.EqualTo(3));
            Assert.That(first.Active.Reserve, Is.EqualTo(7));
            Assert.That(first.TryConsumeAmmo(1), Is.True);
            Assert.That(_magazines[Uid].Count, Is.EqualTo(2));
        }

        [Test] public void MagazineSurvivesHolderRecordReplacementAndNeverRefillsFromStaleClassSeed()
        {
            var first = Holder(5, 0);
            Assert.That(first.TryConsumeAmmo(4), Is.True);
            first = null; // actor record can be removed while the item remains in a corpse/ground
            var next = Holder(5, 0);
            Assert.That(next.Active.Magazine, Is.EqualTo(1));
            next.ApplySeed(Seed(Uid, 5, 0, KitSeedReason.ClassApply), false);
            next.BindLoadedRounds(_magazines);
            Assert.That(next.Active.Magazine, Is.EqualTo(1));
        }

        [Test] public void IdenticalDefinitionWithDifferentUidHasIndependentMagazine()
        {
            var first = Holder(4, 2);
            var second = Holder(0, 3, "fc54f4a4e9e744dd9e091bcd5ca2b559");
            Assert.That(second.Active.Magazine, Is.Zero);
            Assert.That(first.Active.Magazine, Is.EqualTo(4));
        }

        [Test] public void LegacyDefinitionAndSyntheticReservePoolsNeverShareAcrossActors()
        {
            var first = Holder(4, 2, Weapon);
            var second = Holder(0, 3, Weapon);
            Holder(0, 8, "reserve:" + Weapon);
            Assert.That(second.Active.Magazine, Is.Zero);
            Assert.That(first.Active.Magazine, Is.EqualTo(4));
            Assert.That(_magazines, Is.Empty);
        }
    }
}
