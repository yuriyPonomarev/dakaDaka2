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
                Assert.That(bag.HeldStackId,Is.Zero);
                var potion=AssetDatabase.LoadAssetAtPath<ItemData>("Assets/Emberdelve/Data/Item_BloodDraught.asset");
                Assert.That(bag.TryAdd(potion,1),Is.True);
                var potionStack=bag.Slots[bag.IndexOfItem(potion)];
                Assert.That(authority.TryCreateLocalCommand(InventoryCommandKind.Use,potionStack.StackId,0,Vector2Int.zero,EquipmentSlot.None,Guid.Empty,Guid.Empty,out var potionUse,out _),Is.True);
                var potionResult=authority.Submit(rig,potionUse);
                Assert.That(potionResult.Accepted,Is.False);
                Assert.That(potionResult.Reason,Does.Contain("take that item in hand"));
                Assert.That(bag.RequestHoldStack(potionStack.StackId,out _),Is.True);
                ranged.Initialize(Weapon(matching),3,20);
                Assert.That(bag.RequestQuickStack(stack.StackId,out _),Is.False,"ammo cannot replace a held potion implicitly");
                Assert.That(bag.RequestWeaponSet(0,out _),Is.True);
                Assert.That(bag.HeldStackId,Is.Zero);
                ranged.Initialize(Weapon(wrong),3,20);
                Assert.That(authority.TryCreateLocalCommand(InventoryCommandKind.Use,stack.StackId,0,Vector2Int.zero,EquipmentSlot.None,Guid.Empty,Guid.Empty,out var denied,out var why),Is.True,why);
                Assert.That(authority.Submit(rig,denied).Accepted,Is.False);
                Assert.That(bag.TotalUnits,Is.EqualTo(2));Assert.That(ranged.Reserve,Is.EqualTo(3));
                ranged.Initialize(Weapon(matching),3,20);
                Assert.That(authority.TryCreateLocalCommand(InventoryCommandKind.Use,stack.StackId,0,Vector2Int.zero,EquipmentSlot.None,Guid.Empty,Guid.Empty,out var accepted,out why),Is.True,why);
                ranged.Initialize(Weapon(matching),20,20);
                Assert.That(bag.RequestQuickStack(stack.StackId,out _),Is.False,"full reserve preserves the round");
                Assert.That(bag.TotalUnits,Is.EqualTo(2));
                ranged.Initialize(Weapon(matching),3,20);
                Assert.That(bag.RequestQuickStack(stack.StackId,out var useReason),Is.True,useReason);
                Assert.That(ranged.Weapon,Is.SameAs(Weapon(matching)));
                Assert.That(ranged.enabled,Is.True);
                Assert.That(bag.HeldStackId,Is.Zero,"quick ammo never stows the weapon");
                Assert.That(ranged.Reserve,Is.EqualTo(4));Assert.That(bag.TotalUnits,Is.EqualTo(1));
                Assert.That(authority.Submit(rig,accepted).Accepted,Is.False,"replaying consumption cannot mint ammo");
                Assert.That(ranged.Reserve,Is.EqualTo(4));
                Assert.That(bag.HeldStackId,Is.Zero,"consumed stack cannot remain held");
                Assert.That(bag.HeldItem,Is.Null);
                Assert.That(bag.RequestHoldStack(stack.StackId,out _),Is.False,"retired stack cannot be selected");
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
