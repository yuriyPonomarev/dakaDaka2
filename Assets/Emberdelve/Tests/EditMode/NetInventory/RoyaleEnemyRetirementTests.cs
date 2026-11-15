using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Emberdelve.Combat;
using Emberdelve.Core;
using Emberdelve.Level;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Emberdelve.Net.Tests
{
    public sealed class RoyaleEnemyRetirementTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private NetRoleKind _role;
        private static readonly Rect Source = new Rect(0, 0, 20, 20);
        private static EnemyPoseWire Pose(int id, float x) => new EnemyPoseWire { Enemy = id, X = x, Y = 1, Z = 5 };
        private static EnemyPresentationWire Presentation(int id) => new EnemyPresentationWire { Version = EnemyPresentationWire.CurrentVersion, Enemy = id };
        private static EnemyVitalsWire Vitals(int id) => new EnemyVitalsWire { Enemy = id, Health = 10, MaxHealth = 10 };
        [SetUp] public void Setup()
        { _role = NetRole.Current; NetRole.ResetToOffline(); NetEnemyViews.Clear(); CombatRegistry.ResetForRun(); }
        [TearDown] public void Cleanup()
        { NetEnemyViews.Clear(); CombatRegistry.ResetForRun(); NetRole.Set(_role); }
        private static NetEnemyProxy SeedProxy(int id, float x)
        {
            NetEnemyViews.OnVitals(Vitals(id));
            var entries = (IDictionary)typeof(NetEnemyViews).GetField("Entries", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            var entry = entries[id];
            var proxy = new GameObject("SeededEnemyProxy" + id).AddComponent<NetEnemyProxy>();
            proxy.transform.position = new Vector3(x, 1, 5);
            entry.GetType().GetField("Proxy").SetValue(entry, proxy);
            entry.GetType().GetField("HasPosition").SetValue(entry, true);
            entry.GetType().GetField("LastPosition").SetValue(entry, proxy.transform.position);
            return proxy;
        }
        [Test] public void RetirementRemovesSourceProxyAndRejectsAllDelayedStreams()
        {
            var source = SeedProxy(1, 5); var destination = SeedProxy(2, 105);
            NetEnemyViews.RetireFloor(Source);
            Assert.True(source == null); Assert.True(destination != null);
            NetEnemyViews.OnVitals(Vitals(1)); NetEnemyViews.OnPresentation(Presentation(1));
            NetEnemyViews.OnPose(Pose(1, 5)); NetEnemyViews.OnPose(Pose(1, 105));
            Assert.That(NetEnemyViews.Total, Is.EqualTo(1));
            Assert.That(NetEnemyViews.PoseCount, Is.Zero);
            Assert.True(NetEnemyViews.TryGetNearestAliveProxy(Vector3.zero, out int id, out _)); Assert.That(id, Is.EqualTo(2));
        }
        [Test] public void FirstPoseArrivingAfterRetirementCannotCreateOldFloorProxy()
        {
            NetEnemyViews.RetireFloor(Source);
            NetEnemyViews.OnVitals(Vitals(3)); NetEnemyViews.OnPresentation(Presentation(3));
            NetEnemyViews.OnPose(Pose(3, 5));
            NetEnemyViews.OnVitals(Vitals(3)); NetEnemyViews.OnPresentation(Presentation(3));
            Assert.That(NetEnemyViews.Total, Is.Zero); Assert.That(NetEnemyViews.PoseCount, Is.Zero);
            Assert.False(NetEnemyViews.TryGetNearestAliveProxy(Vector3.zero, out _, out _));
        }
        [Test] public void NewRunClearsRetirementIdentityAndBounds()
        {
            NetEnemyViews.RetireFloor(Source); NetEnemyViews.OnPose(Pose(4, 5));
            NetEnemyViews.Clear();
            NetEnemyViews.OnVitals(new EnemyVitalsWire { Enemy = 4, Dead = true });
            NetEnemyViews.OnPose(Pose(4, 5)); // Dead actor records its position without loading a visual.
            Assert.That(NetEnemyViews.Total, Is.EqualTo(1));
            NetEnemyViews.RetireFloor(Source); Assert.That(NetEnemyViews.Total, Is.Zero);
        }
        private sealed class Enemy : ICombatEntity
        {
            public int CombatId { get; set; }
            public Vector3 CombatCenter { get; set; }
            public EntityKind Kind => EntityKind.Enemy;
            public int TeamId => 0;
            public Collider HeadCollider => null;
            public bool IsDead => false;
            public float CurrentHealth => 10;
            public float MaxHealth => 10;
            public void ApplyDamage(DamageInfo damage) { Assert.Fail("Retirement must not apply damage"); }
        }
        [Test] public void ServerStopsOnlyExplicitlyRetiredFloorPublication()
        {
            var mapRoot = new GameObject("RetirementMap");
            var layout = ScriptableObject.CreateInstance<DungeonLayoutData>();
            object server = null;
            var type = typeof(NetInventoryServer).Assembly.GetType("Emberdelve.Net.NetCombatServer");
            try
            {
                layout.gridWidth = layout.gridHeight = 20; layout.cellSize = 1;
                var map = mapRoot.AddComponent<DungeonModuleSceneAssembler>();
                typeof(DungeonModuleSceneAssembler).GetField("_runtimeLayout", Private).SetValue(map, layout);
                var source = new Enemy { CombatId = 1, CombatCenter = new Vector3(5, 1, 5) };
                var destination = new Enemy { CombatId = 2, CombatCenter = new Vector3(105, 1, 5) };
                CombatRegistry.Register(1, source); CombatRegistry.Register(2, destination);
                Action<string> ignore = _ => { };
                server = Activator.CreateInstance(type, new object[] { null, null, null, ignore, ignore });
                var publish = type.GetMethod("PublishEnemies", Private);
                mapRoot.SetActive(false); // Temporary inactive map is not retirement.
                publish.Invoke(server, null);
                var ids = (List<int>)type.GetField("_scratchIds", Private).GetValue(server);
                CollectionAssert.AreEquivalent(new[] { 1, 2 }, ids);
                map.RetireRuntimeContent(); publish.Invoke(server, null);
                CollectionAssert.AreEquivalent(new[] { 2 }, ids);
                Assert.True(CombatRegistry.TryGet(1, out _)); Assert.False(source.IsDead);
            }
            finally
            {
                if (server != null) type.GetMethod("Dispose").Invoke(server, null);
                Object.DestroyImmediate(mapRoot); Object.DestroyImmediate(layout);
            }
        }
    }
}
