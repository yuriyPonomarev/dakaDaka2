using Emberdelve.Core;
using Emberdelve.Interaction;
using Emberdelve.Level;
using NUnit.Framework;
using UnityEngine;

namespace Emberdelve.Tests.Combat
{
    public sealed class BreakableBarrelTests
    {
        GameObject _root, _intact, _broken;
        BreakableBarrel _barrel;
        [SetUp] public void SetUp()
        {
            NetRole.ResetToOffline();
            _root=new GameObject("Barrel_0");
            _intact=GameObject.CreatePrimitive(PrimitiveType.Cylinder); _intact.transform.SetParent(_root.transform);
            _broken=new GameObject("Debris"); _broken.transform.SetParent(_root.transform);
            _barrel=_root.AddComponent<BreakableBarrel>(); _barrel.Configure(_intact,_broken,24);
        }
        [TearDown] public void TearDown() { Object.DestroyImmediate(_root); NetRole.ResetToOffline(); }

        [Test] public void ClientCannotDamageButServerSnapshotRemovesCollision()
        {
            NetRole.Set(NetRoleKind.Client);
            Assert.IsFalse(_barrel.ApplyWeaponDamage(100));
            Assert.IsFalse(_barrel.IsBroken); Assert.IsTrue(_intact.activeSelf);
            _barrel.ApplyReplicatedBroken();
            Assert.IsTrue(_barrel.IsBroken); Assert.IsFalse(_intact.activeSelf); Assert.IsTrue(_broken.activeSelf);
            Assert.IsFalse(_intact.GetComponent<Collider>().enabled);
            _barrel.ApplyReplicatedBroken(); // duplicate reliable snapshots must remain harmless
            Assert.IsTrue(_barrel.IsBroken);
        }

        [Test] public void AuthoritativeHitsAccumulateAndInvalidDamageCannotBreak()
        {
            Assert.IsFalse(_barrel.ApplyWeaponDamage(float.NaN));
            Assert.IsFalse(_barrel.ApplyWeaponDamage(float.PositiveInfinity));
            Assert.IsFalse(_barrel.ApplyWeaponDamage(-1));
            Assert.IsTrue(_barrel.ApplyWeaponDamage(12)); Assert.IsFalse(_barrel.IsBroken);
            Assert.IsTrue(_barrel.ApplyWeaponDamage(12)); Assert.IsTrue(_barrel.IsBroken);
            Assert.IsFalse(_barrel.ApplyWeaponDamage(12)); Assert.AreEqual(0,_barrel.RemainingHealth);
        }

        [Test] public void IdentityUsesFinalModuleCoordinatesButIgnoresSceneContainer()
        {
            var outer=new GameObject("HostRoot"); var module=new GameObject("crypt_hall_01_0_0");
            try
            {
                module.AddComponent<DungeonModuleDefinition>(); module.transform.SetParent(outer.transform);
                _root.transform.SetParent(module.transform);
                ulong initial=_barrel.StableId;
                outer.name="ClientRoot"; Assert.AreEqual(initial,_barrel.StableId);
                module.name="crypt_hall_01_1_0"; Assert.AreNotEqual(initial,_barrel.StableId);
                ulong unscoped = _barrel.StableId;
                Assert.False(_barrel.BindStagedFloor(1)); Assert.AreEqual(unscoped, _barrel.StableId);
                Assert.True(_barrel.BindStagedFloor(2)); ulong scoped = _barrel.StableId;
                Assert.AreNotEqual(unscoped, scoped, "Binding invalidates a previously cached ID");
                Assert.True(_barrel.BindStagedFloor(2)); Assert.AreEqual(scoped, _barrel.StableId);
                Assert.False(_barrel.BindStagedFloor(3)); Assert.AreEqual(scoped, _barrel.StableId);
                outer.name = "AnotherClientRoot"; Assert.AreEqual(scoped, _barrel.StableId);
            }
            finally { _root.transform.SetParent(null); Object.DestroyImmediate(outer); }
        }
    }
}
