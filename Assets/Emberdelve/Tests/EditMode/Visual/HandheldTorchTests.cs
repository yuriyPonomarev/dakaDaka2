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
        public void LegacyCameraFillMigrationLeavesItemAndWorldLightsAlone()
        {
            var migrate = typeof(PlayerRig).GetMethod("DisableLegacyCameraFill",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(migrate);
            System.Action<Transform> disable = camera => migrate.Invoke(null, new object[] { camera });
            var root = new GameObject("TemporaryCamera");
            try
            {
                var legacy = new GameObject("Player_Torch");
                legacy.transform.SetParent(root.transform, false);
                var fill = legacy.AddComponent<Light>();
                var other = new GameObject("OtherLight");
                other.transform.SetParent(root.transform, false);
                var otherLight = other.AddComponent<Light>();
                var item = new GameObject("EquippedTorch");
                item.transform.SetParent(root.transform, false);
                var nested = new GameObject("Player_Torch");
                nested.transform.SetParent(item.transform, false);
                var heldLight = nested.AddComponent<Light>();
                var visual = item.AddComponent<HandheldTorchVisual>();
                visual.source = heldLight;
                disable(null);
                disable(root.transform);
                Assert.IsTrue(fill.enabled, "A non-camera pivot is not migrated");
                root.AddComponent<Camera>();
                disable(root.transform);
                disable(root.transform);
                Assert.IsFalse(fill.enabled, "Legacy direct camera fill stays off");
                Assert.IsTrue(otherLight.enabled, "Other camera lights are untouched");
                Assert.IsTrue(heldLight.enabled, "Nested item light is untouched even with the legacy name");
                Object.DestroyImmediate(legacy);
                disable(root.transform);
                Assert.IsTrue(heldLight.enabled, "Missing legacy light is harmless");
            }
            finally { Object.DestroyImmediate(root); }
        }

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
