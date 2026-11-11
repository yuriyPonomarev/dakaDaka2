using System.Linq;
using Emberdelve.Data;
using Emberdelve.EditorTools;
using Emberdelve.Items;
using Emberdelve.Player;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Emberdelve.Tests.Visual
{
    public sealed class HandheldTorchTests
    {
        [Test]
        public void TorchIsCatalogGearWithUniqueIdentityAndNoDefense()
        {
            var item=AssetDatabase.LoadAssetAtPath<ItemData>(EmberdelveTorchContent.ItemPath);
            Assert.IsNotNull(item);
            Assert.AreEqual(EquipmentSlot.Weapon,item.equipSlot);
            Assert.AreEqual(1,item.maxStack);
            Assert.AreEqual(HandUse.OneHanded,item.grantedWeapon.Hands);
            Assert.IsFalse(item.grantedWeapon.CanBlock);
            Assert.IsFalse(item.grantedWeapon.CanParry);
            Assert.IsFalse(item.grantedWeapon.IsShield);
            var catalog=AssetDatabase.LoadAssetAtPath<ItemCatalog>(EmberdelveTorchContent.DataRoot+"EmberdelveItemCatalog.asset");
            var issuer=ItemIdentityIssuer.CreateLocalTrusted();
            Assert.IsTrue(catalog.TryMintCommon(issuer,item,out var first,out var reason),reason);
            Assert.IsTrue(catalog.TryMintCommon(issuer,item,out var second,out reason),reason);
            Assert.AreNotEqual(first.Uid,second.Uid);
            foreach(string name in new[]{"Warrior","Archer"})
            {
                var data=AssetDatabase.LoadAssetAtPath<CharacterClassData>(EmberdelveTorchContent.DataRoot+"Class_"+name+".asset");
                Assert.AreEqual(1,data.startingItems.Count(x=>x==item),name+" starts with one torch");
            }
            var prefab=AssetDatabase.LoadAssetAtPath<GameObject>(EmberdelveTorchContent.PrefabPath);
            var visual=prefab.GetComponent<HandheldTorchVisual>();
            Assert.IsNotNull(visual.source);
            Assert.IsNotNull(visual.flame);
            Assert.AreEqual(LightShadows.Soft,visual.source.shadows);
            Assert.AreEqual(0,prefab.GetComponentsInChildren<Collider>(true).Length);
        }
    }
}
