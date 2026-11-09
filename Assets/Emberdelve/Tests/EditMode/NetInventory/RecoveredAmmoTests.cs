using System;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Items;
using Emberdelve.Player;
using Emberdelve.Combat;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
namespace Emberdelve.Tests.NetInventory
{
    public sealed class RecoveredAmmoTests
    {
        [TestCase("Arrow","HollowBow","EmberCrossbow")]
        [TestCase("Bolt","EmberCrossbow","HollowBow")]
        public void WrongWeaponPreservesRoundAndMatchingUseGrantsExactlyOne(string itemName,string matching,string wrong)
        {
            var previous=NetRole.Current;GameObject owner=null;
            try
            {
                NetRole.ResetToOffline();owner=new GameObject("RecoveredAmmoTest");owner.SetActive(false);
                var bag=owner.AddComponent<Inventory>();var equipment=owner.AddComponent<Equipment>();
                var rig=owner.AddComponent<PlayerRig>();var ranged=owner.AddComponent<RangedCombat>();
                owner.SetActive(true);rig.WireRig();bag.Initialize(rig);equipment.Initialize(rig);
                bag.SetCatalog(AssetDatabase.LoadAssetAtPath<ItemCatalog>("Assets/Emberdelve/Data/EmberdelveItemCatalog.asset"));
                bag.SetIssuer(ItemIdentityIssuer.CreateLocalTrusted());
                var item=AssetDatabase.LoadAssetAtPath<ItemData>("Assets/Emberdelve/Data/Item_Recovered"+itemName+".asset");
                Assert.That(bag.TryAdd(item,1),Is.True);
                var stack=bag.Slots[0];var authority=bag.CommandAuthority;
                ranged.Initialize(Weapon(wrong),3,20);
                Assert.That(authority.TryCreateLocalCommand(InventoryCommandKind.Use,stack.StackId,0,Vector2Int.zero,EquipmentSlot.None,Guid.Empty,Guid.Empty,out var denied,out var why),Is.True,why);
                Assert.That(authority.Submit(rig,denied).Accepted,Is.False);
                Assert.That(bag.TotalUnits,Is.EqualTo(1));Assert.That(ranged.Reserve,Is.EqualTo(3));
                ranged.Initialize(Weapon(matching),3,20);
                Assert.That(authority.TryCreateLocalCommand(InventoryCommandKind.Use,stack.StackId,0,Vector2Int.zero,EquipmentSlot.None,Guid.Empty,Guid.Empty,out var accepted,out why),Is.True,why);
                Assert.That(authority.Submit(rig,accepted).Accepted,Is.True);
                Assert.That(ranged.Reserve,Is.EqualTo(4));Assert.That(bag.TotalUnits,Is.Zero);
                Assert.That(authority.Submit(rig,accepted).Accepted,Is.False,"replaying consumption cannot mint ammo");
                Assert.That(ranged.Reserve,Is.EqualTo(4));
            }
            finally {if(owner!=null)UnityEngine.Object.DestroyImmediate(owner);NetRole.Set(previous);}
        }
        static WeaponData Weapon(string name)=>AssetDatabase.LoadAssetAtPath<WeaponData>("Assets/Emberdelve/Data/Weapon_"+name+".asset");
        [Test] public void RecoveryRestsAboveRealFloorAndRefusesVoid()
        {
            var floor=GameObject.CreatePrimitive(PrimitiveType.Cube);floor.transform.position=new Vector3(2000,-.5f,2000);floor.transform.localScale=new Vector3(10,1,10);
            try
            {
                Physics.SyncTransforms();
                Assert.That(ProjectileRecovery.TryGround(new Vector3(2000,3,2000),Physics.DefaultRaycastLayers,out var p),Is.True);
                Assert.That(p.y,Is.EqualTo(.055f).Within(.005f));
                Assert.That(ProjectileRecovery.TryGround(new Vector3(3000,3,3000),Physics.DefaultRaycastLayers,out _),Is.False);
            }
            finally {UnityEngine.Object.DestroyImmediate(floor);}
        }
    }
}
