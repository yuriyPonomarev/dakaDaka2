using NUnit.Framework;
using UnityEngine;
using Emberdelve.Data;
using Emberdelve.Skills;

namespace Emberdelve.Combat.Tests
{
    public sealed class SkillLoadoutStoreTests
    {
        private CharacterClassData _class;
        private SkillData _a1, _a2, _a3, _p1, _p2, _p3;

        private static SkillData Skill(string id, SkillSlot slot)
        {
            var s = ScriptableObject.CreateInstance<SkillData>();
            s.skillId = id; s.displayName = id; s.slot = slot;
            return s;
        }

        [SetUp]
        public void SetUp()
        {
            _a1 = Skill("t_a1", SkillSlot.Active); _a2 = Skill("t_a2", SkillSlot.Active); _a3 = Skill("t_a3", SkillSlot.Active);
            _p1 = Skill("t_p1", SkillSlot.Passive); _p2 = Skill("t_p2", SkillSlot.Passive); _p3 = Skill("t_p3", SkillSlot.Passive);
            _class = ScriptableObject.CreateInstance<CharacterClassData>();
            _class.classId = "test_kit_class_" + GetHashCode();
            _class.displayName = "Test";
            _class.activeSkills = new[] { _a1, _a2 };
            _class.passiveSkills = new[] { _p1, _p2 };
            SkillLoadoutStore.Reset(_class);
        }

        [TearDown]
        public void TearDown()
        {
            SkillLoadoutStore.Reset(_class);
            foreach (var o in new Object[] { _a1, _a2, _a3, _p1, _p2, _p3, _class }) Object.DestroyImmediate(o);
        }

        [Test]
        public void NoSavedChoice_IsTheClassKit_AndApplyReturnsTheSameAsset()
        {
            SkillLoadoutStore.Get(_class, out var actives, out var passives);
            Assert.That(actives, Is.EqualTo(new[] { _a1, _a2 }));
            Assert.That(passives, Is.EqualTo(new[] { _p1, _p2 }));
            Assert.That(SkillLoadoutStore.Apply(_class), Is.SameAs(_class));
        }

        [Test]
        public void Pool_HoldsOnlyTheSlotType_WithoutRepeats()
        {
            var actives = SkillLoadoutStore.Pool(_class, SkillSlot.Active);
            var passives = SkillLoadoutStore.Pool(_class, SkillSlot.Passive);
            Assert.That(actives, Has.Member(_a1).And.Member(_a2));
            Assert.That(passives, Has.Member(_p1).And.Member(_p2));
            foreach (var s in actives) Assert.That(s.slot, Is.EqualTo(SkillSlot.Active));
            foreach (var s in passives) Assert.That(s.slot, Is.EqualTo(SkillSlot.Passive));
            Assert.That(actives, Is.Unique);
            Assert.That(passives, Is.Unique);
        }

        [Test]
        public void ReplacingASlot_AppliesToACopy_AndLeavesTheClassAssetAlone()
        {
            // a3 is not in the class kit, so it has to be in the pool to be accepted; the universal pool is the only source of such skills.
            var pool = SkillPool.Load();
            Assume.That(pool != null && pool.universalActives.Length > 0, "the universal pool asset is part of the project");
            SkillData extra = pool.universalActives[0];
            SkillLoadoutStore.Save(_class, new[] { extra, _a2 }, new[] { _p1, _p2 });

            CharacterClassData applied = SkillLoadoutStore.Apply(_class);
            Assert.That(applied, Is.Not.SameAs(_class));
            Assert.That(applied.activeSkills, Is.EqualTo(new[] { extra, _a2 }));
            Assert.That(_class.activeSkills, Is.EqualTo(new[] { _a1, _a2 }), "the asset on disk must not change");
            Assert.That(applied.classId, Is.EqualTo(_class.classId));
            Object.DestroyImmediate(applied);
        }

        [Test]
        public void ASavedChoiceThatNoLongerResolves_FallsBackPerSlot()
        {
            SkillLoadoutStore.Save(_class, new[] { _a3, _a2 }, new[] { _p1, _p2 }); // a3 is in no pool
            SkillLoadoutStore.Get(_class, out var actives, out _);
            Assert.That(actives, Is.EqualTo(new[] { _a1, _a2 }));
        }

        [Test]
        public void TheSameSkillTwice_NeverEntersAKit()
        {
            SkillLoadoutStore.Save(_class, new[] { _a2, _a2 }, new[] { _p1, _p2 });
            SkillLoadoutStore.Get(_class, out var actives, out _);
            Assert.That(actives[0], Is.Not.SameAs(actives[1]));
        }
    }
}
