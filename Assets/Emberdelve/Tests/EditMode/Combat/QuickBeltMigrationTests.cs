using Emberdelve.Items;
using NUnit.Framework;

namespace Emberdelve.Combat.Tests
{
    public sealed class QuickBeltMigrationTests
    {
        private static LoadoutSnapshot Legacy()
        {
            var bag = new ContainerSnapshot { schemaVersion = 4, kind = ContainerKinds.Bag, containerId = "bag", width = 10, height = 4 };
            var gear = new ContainerSnapshot { schemaVersion = 4, kind = ContainerKinds.Equipment, containerId = "equipment" };
            bag.Seal(); gear.Seal();
            var saved = new LoadoutSnapshot { schemaVersion = 4, bag = bag, equipment = gear, weaponSet = 1,
                belt = new[] { "i:blood_draught", "", "u:0123456789abcdef0123456789abcdef", "i:arrow_bundle" } };
            saved.Seal(); return saved;
        }

        [Test] public void FourSavedIdentitiesMigrateToSixWithoutReordering()
        {
            var old = Legacy();
            Assert.IsTrue(LoadoutSnapshot.TryFromJson(old.ToJson(), out var current, out string reason), reason);
            Assert.AreEqual(ContainerSnapshot.CurrentSchemaVersion, current.schemaVersion);
            Assert.AreEqual(ContainerSnapshot.CurrentSchemaVersion, current.bag.schemaVersion);
            Assert.AreEqual(ContainerSnapshot.CurrentSchemaVersion, current.equipment.schemaVersion);
            Assert.AreEqual(1, current.weaponSet);
            CollectionAssert.AreEqual(new[] { old.belt[0], old.belt[1], old.belt[2], old.belt[3], "", "" }, current.belt);
            Assert.IsTrue(current.ChecksumMatches());
            Assert.IsTrue(LoadoutSnapshot.TryFromJson(current.ToJson(), out var again, out reason), reason);
            Assert.AreEqual(current.ToJson(), again.ToJson());
        }

        [Test] public void MigrationDoesNotLegitimizeEditedLegacyBelt()
        {
            var old = Legacy();
            old.belt[0] = "i:arrow_bundle";
            Assert.IsFalse(LoadoutSnapshot.TryFromJson(old.ToJson(), out _, out _));
        }

        [Test] public void LegacyBeltWithInventedExtraSlotIsRefusedEvenWithValidChecksum()
        {
            var old = Legacy(); old.belt = new[] { "", "", "", "", "" }; old.Seal();
            Assert.IsFalse(LoadoutSnapshot.TryFromJson(old.ToJson(), out _, out _));
        }
    }
}
