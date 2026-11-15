using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Emberdelve.Core;
using Emberdelve.Flow;
using Emberdelve.Level;
using Emberdelve.Rules;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Emberdelve.Combat.Tests
{
    public sealed class RoyaleSourceRetirementTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private GameObject _host, _geometry;
        private RunDirector _director;
        private DungeonModuleSceneAssembler _map;
        private DungeonLayoutData _layout;
        private PlayerSession _source;
        private NetRoleKind _role;
        private RunDirector _previousDirector;
        private ILocalPlayerSource _previousLocalSource;
        private static void Set(object target, string field, object value) => target.GetType().GetField(field, Private).SetValue(target, value);

        [SetUp] public void Setup()
        {
            GameServices.TryGet<RunDirector>(out _previousDirector);
            GameServices.TryGet<ILocalPlayerSource>(out _previousLocalSource);
            _role = NetRole.Current; NetRole.ResetToOffline();
            _host = new GameObject("RetirementHost");
            _director = _host.AddComponent<RunDirector>();
            if (_director.Session == null) typeof(RunDirector).GetMethod("Awake", Private).Invoke(_director, null);
            Assert.True(_director.ConfigureRoyale(RoyalePartyMode.Solo));
            Set(_director, "_state", RunState.Running);
            Set(_director, "_startedAt", Time.unscaledTime);
            _map = _host.AddComponent<DungeonModuleSceneAssembler>();
            _geometry = new GameObject("OwnedGeometry"); _geometry.transform.SetParent(_host.transform);
            _layout = ScriptableObject.CreateInstance<DungeonLayoutData>();
            Set(_map, "_runtimeLayout", _layout); Set(_map, "generatedRoot", _geometry.transform);
            var floorType = typeof(RoyaleFloorCoordinator).GetNestedType("Floor", BindingFlags.NonPublic);
            var floor = Activator.CreateInstance(floorType, true);
            floorType.GetField("Map").SetValue(floor, _map);
            floorType.GetField("OwnsMap").SetValue(floor, false);
            ((IDictionary)typeof(RoyaleFloorCoordinator).GetField("_floors", Private).GetValue(_director.FloorCoordinator)).Add(1, floor);
            _source = new PlayerSession { PlayerId = 7, State = RunState.Dead, HasResult = true };
            ((List<PlayerSession>)typeof(RunDirector).GetField("_sessions", Private).GetValue(_director)).Add(_source);
            Assert.True(_director.TryStartRoyaleFloor(1, 120, out _));
        }
        [TearDown] public void Cleanup()
        {
            // Awake is invoked manually in EditMode; do not rely on Unity calling OnDestroy.
            GameServices.Unregister<RunDirector>(_director);
            GameServices.Unregister<ILocalPlayerSource>(_director);
            Object.DestroyImmediate(_host); Object.DestroyImmediate(_layout); NetRole.Set(_role);
            if (_previousDirector != null) GameServices.Register<RunDirector>(_previousDirector);
            if (_previousLocalSource != null && !(_previousLocalSource is Object obj && obj == null))
                GameServices.Register<ILocalPlayerSource>(_previousLocalSource);
            _previousDirector = null; _previousLocalSource = null;
        }
        [Test] public void DestinationMustStartBeforeRetirement()
        {
            Assert.False(_director.FloorCoordinator.TryRetireSource(1, out _));
            Assert.True(_geometry.activeSelf);
        }
        [TestCase("DeathCustodyPending")]
        [TestCase("ExtractionSettlementPending")]
        public void PendingCustodyKeepsSourceUntilCommit(string pending)
        {
            Assert.True(_director.TryStartRoyaleFloor(2, 120, out _));
            Set(_source, pending, true);
            Assert.False(_director.FloorCoordinator.TryRetireSource(1, out _));
            Set(_source, pending, false);
            Assert.True(_director.FloorCoordinator.TryRetireSource(1, out _));
        }
        [Test] public void SurvivorMustLeaveSourceIdentityEvenWhenDestinationClockStarted()
        {
            Assert.True(_director.TryStartRoyaleFloor(2, 120, out _));
            _source.State = RunState.Running; _source.HasResult = false;
            Assert.False(_director.FloorCoordinator.TryRetireSource(1, out _));
            typeof(PlayerSession).GetProperty("Floor").SetValue(_source, 2);
            Assert.True(_director.FloorCoordinator.TryRetireSource(1, out _));
        }
        [Test] public void RetirementPreservesSharedHostAndMetadataAndCannotReactivate()
        {
            Assert.True(_director.TryStartRoyaleFloor(2, 120, out _));
            Assert.True(_director.FloorCoordinator.TryRetireSource(1, out _));
            Assert.True(_director.FloorCoordinator.TryRetireSource(1, out _));
            Assert.True(_director.FloorCoordinator.IsRetired(1));
            Assert.True(_host.activeInHierarchy); Assert.False(_geometry.activeSelf);
            Assert.That(_map.RuntimeLayout, Is.SameAs(_layout));
            Assert.True(_map.RuntimeContentRetired);
            Assert.False(_director.FloorCoordinator.TryActivate(1, out _));
        }
        [Test] public void ClientCannotRetireAuthorityContent()
        {
            Assert.True(_director.TryStartRoyaleFloor(2, 120, out _));
            NetRole.Set(NetRoleKind.Client);
            Assert.False(_director.FloorCoordinator.TryRetireSource(1, out _));
            Assert.True(_geometry.activeSelf);
        }
    }
}
