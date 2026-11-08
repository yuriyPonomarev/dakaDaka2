using System.Linq;
using System.Reflection;
using Emberdelve.Data;
using Emberdelve.Items;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Emberdelve.Tests.Combat
{
    public class EquipAtomicityTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public void OffhandTransferRefusalPreservesExactBag(bool invalidSource)
        {
            var go = new GameObject("atomic equip test");
            try
            {
                var bag = go.AddComponent<Inventory>();
                var catalog = AssetDatabase.LoadAssetAtPath<ItemCatalog>("Assets/Emberdelve/Data/EmberdelveItemCatalog.asset");
                bag.SetCatalog(catalog);
                var item = AssetDatabase.FindAssets("t:ItemData").Select(AssetDatabase.GUIDToAssetPath)
                    .Select(AssetDatabase.LoadAssetAtPath<ItemData>).First(x => x.IsEquippable && catalog.Contains(x));
                Assert.That(bag.TryAdd(item, 2), Is.True);
                var offhand = bag.InstanceAt(1);
                Assert.That(bag.TryRemoveAt(1, 1), Is.EqualTo(1));
                var incoming = bag.InstanceAt(0);
                string before = bag.ToContainerSnapshot().ToJson();
                var next = typeof(Inventory).GetField("_nextStackId", BindingFlags.NonPublic | BindingFlags.Instance);
                object beforeNext = next.GetValue(bag);
                int units = bag.TotalUnits, value = bag.CarriedValue;
                // Stow succeeds, then transfer rejects either a stale source index or
                // an outgoing UID already present in the bag. Neither may leak the stow.
                object[] args = { invalidSource ? -1 : 0, incoming, incoming, offhand, null };
                var method = typeof(Inventory).GetMethod("TryEquipTransferWithOffhand", BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.That((bool)method.Invoke(bag, args), Is.False);
                Assert.That(bag.ToContainerSnapshot().ToJson(), Is.EqualTo(before));
                Assert.That(next.GetValue(bag), Is.EqualTo(beforeNext));
                Assert.That(bag.TotalUnits, Is.EqualTo(units));
                Assert.That(bag.CarriedValue, Is.EqualTo(value));
                Assert.That(bag.IndexOfInstance(offhand.Uid), Is.EqualTo(-1));
            }
            finally { Object.DestroyImmediate(go); }
        }
    }
}
