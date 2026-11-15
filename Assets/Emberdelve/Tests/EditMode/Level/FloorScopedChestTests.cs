using Emberdelve.Core;
using Emberdelve.Flow;
using Emberdelve.Level;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Emberdelve.Level.Tests
{
    public sealed class FloorScopedChestTests
    {
        [Test]
        public void AuthorityFloorContentHasSeparateIdsAndCannotBeReboundToAnotherHost()
        {
            var prior = NetRole.Current; NetRole.ResetToOffline();
            var first = new GameObject("Floor1"); var second = new GameObject("Floor2");
            var host = new GameObject("Authority"); var other = new GameObject("OtherAuthority");
            try
            {
                var director = host.AddComponent<RunDirector>(); var otherDirector = other.AddComponent<RunDirector>();
                var layout = AssetDatabase.LoadAssetAtPath<DungeonLayoutData>("Assets/Emberdelve/Data/DungeonLayout_AshenHalls.asset");
                var a = ChestSpawner.CreateForFloor(layout, null, director, first.transform, 1);
                var b = ChestSpawner.CreateForFloor(layout, null, director, second.transform, 2);
                Assert.NotNull(a); Assert.NotNull(b); Assert.That(a.Count, Is.GreaterThan(0)); Assert.That(b.Count, Is.EqualTo(a.Count));
                Assert.That(ChestSpawner.CreateForFloor(layout, null, director, first.transform, 1), Is.SameAs(a));
                Assert.IsNull(ChestSpawner.CreateForFloor(layout, null, otherDirector, first.transform, 1));
                Assert.IsNull(ChestSpawner.CreateForFloor(layout, null, director, first.transform, 2));
                for (int i = 0; i < a.Count; i++)
                {
                    Assert.True(a.Chests[i].transform.IsChildOf(first.transform));
                    Assert.True(b.Chests[i].transform.IsChildOf(second.transform));
                    Assert.That(a.Chests[i].ChestId, Does.StartWith("Floor1_"));
                    Assert.That(b.Chests[i].ChestId, Does.StartWith("Floor2_"));
                    Assert.That(a.Chests[i].ChestId, Is.Not.EqualTo(b.Chests[i].ChestId));
                }
            }
            finally
            {
                Object.DestroyImmediate(first); Object.DestroyImmediate(second);
                Object.DestroyImmediate(host); Object.DestroyImmediate(other); NetRole.Set(prior);
            }
        }

        [Test]
        public void ClientLegacyPresentationDoesNotDeleteStagedFloorViews()
        {
            var prior = NetRole.Current; NetRole.Set(NetRoleKind.Client);
            var first = new GameObject("StagedFloor1"); var second = new GameObject("StagedFloor2");
            ChestSpawner legacy = null;
            try
            {
                first.SetActive(false); second.SetActive(false);
                var layout = AssetDatabase.LoadAssetAtPath<DungeonLayoutData>("Assets/Emberdelve/Data/DungeonLayout_AshenHalls.asset");
                var a = ChestSpawner.CreateClientPresentationForFloor(layout, first.transform, 1);
                var b = ChestSpawner.CreateClientPresentationForFloor(layout, second.transform, 2);
                Assert.NotNull(a); Assert.NotNull(b);
                legacy = ChestSpawner.EnsureClientPresentation(layout);
                Assert.NotNull(legacy); Assert.True(a != null); Assert.True(b != null);
                Assert.False(first.activeSelf); Assert.False(second.activeSelf);
                Assert.That(ChestSpawner.CreateClientPresentationForFloor(layout, second.transform, 2), Is.SameAs(b));
            }
            finally
            {
                if (legacy != null) Object.DestroyImmediate(legacy.gameObject);
                Object.DestroyImmediate(first); Object.DestroyImmediate(second); NetRole.Set(prior);
            }
        }
    }
}
