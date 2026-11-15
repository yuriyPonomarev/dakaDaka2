using System.Collections.Generic;
using System.Reflection;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Flow;
using Emberdelve.Movement;
using Emberdelve.Net;
using Emberdelve.Player;
using Emberdelve.Rules;
using NUnit.Framework;
using Unity.Collections;
using Unity.Netcode;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Emberdelve.Tests.NetInventory
{
    public sealed class RoyaleClockTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private GameObject _actor, _host;
        private NetRoleKind _previous;
        [SetUp] public void Setup() { _previous = NetRole.Current; NetRole.ResetToOffline(); }
        [TearDown] public void Cleanup()
        {
            NetRole.ResetToOffline(); if (_host != null) Object.DestroyImmediate(_host);
            if (_actor != null) Object.DestroyImmediate(_actor); NetRole.Set(_previous);
        }
        private PlayerRig Actor()
        {
            _actor = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Emberdelve/Prefabs/Net/Emberdelve_NetPlayer.prefab"));
            var rig = _actor.GetComponent<PlayerRig>(); rig.WireRig();
            typeof(PlayerMovement).GetMethod("Awake", Private).Invoke(rig.Movement, null);
            rig.ApplyClass(AssetDatabase.LoadAssetAtPath<CharacterClassData>("Assets/Emberdelve/Data/Class_Archer.asset"),
                AssetDatabase.LoadAssetAtPath<GameConfig>("Assets/Emberdelve/Data/EmberdelveGameConfig.asset"));
            typeof(NetPlayerRig).GetField("_rig", Private).SetValue(_actor.GetComponent<NetPlayerRig>(), rig); return rig;
        }
        [Test] public void OwnerClockUsesItsFloorAndWaitsWithoutInheritingHostDeadline()
        {
            var rig = Actor(); _host = new GameObject("ClockDirector"); var director = _host.AddComponent<RunDirector>();
            if (director.Session == null) typeof(RunDirector).GetMethod("Awake", Private).Invoke(director, null);
            Assert.True(director.ConfigureRoyale(RoyalePartyMode.Trio));
            var session = new PlayerSession { Rig = rig, PlayerId = rig.PlayerId, Inventory = rig.Inventory };
            ((List<PlayerSession>)typeof(RunDirector).GetField("_sessions", Private).GetValue(director)).Add(session);
            ((Dictionary<int, PlayerSession>)typeof(RunDirector).GetField("_byPlayerId", Private).GetValue(director)).Add(rig.PlayerId, session);
            typeof(RunDirector).GetField("_local", Private).SetValue(director, new PlayerSession());
            typeof(RunDirector).GetField("_state", Private).SetValue(director, RunState.Running);
            typeof(RunDirector).GetField("_startedAt", Private).SetValue(director, Time.unscaledTime);
            Assert.True(director.ClockFor(rig).Waiting);
            Assert.True(director.TryStartRoyaleFloor(1, 120, out _));
            ((float[])typeof(RunDirector).GetField("_floorStartedAt", Private).GetValue(director))[1] = director.Elapsed - 90;
            Assert.That(director.ClockFor(rig).RemainingSeconds, Is.EqualTo(30).Within(.1));
            Assert.True(rig.TryBeginFloorTransit(out _));
            var wait = director.ClockFor(rig); Assert.True(wait.Waiting); Assert.False(wait.Enabled); Assert.False(wait.Expired);
            Assert.That(wait.Floor, Is.EqualTo(2)); Assert.That(wait.RemainingSeconds, Is.Zero);
            Assert.True(director.TryStartRoyaleFloor(2, 180, out _));
            Assert.True(director.TryArriveAtNextFloor(rig, 2, Vector3.right * 1000, Quaternion.identity, out _));
            var clock = director.ClockFor(rig); Assert.That(clock.Floor, Is.EqualTo(2)); Assert.True(clock.Enabled);
            Assert.That(clock.RemainingSeconds, Is.EqualTo(180).Within(.1)); Assert.False(clock.Waiting); Assert.False(clock.Expired);
            Assert.That(clock.MovementGeneration, Is.EqualTo(rig.FloorMovementGeneration));
            ((float[])typeof(RunDirector).GetField("_floorStartedAt", Private).GetValue(director))[1] = director.Elapsed - 125;
            Assert.True(director.RaidExpired, "Host remains on expired source floor");
            Assert.False(director.ClockFor(rig).Expired, "Remote destination clock is independent");
        }
        [TestCase(false)] [TestCase(true)] public void FloorClockRoundTripsItsGenerationAndWaitingState(bool waiting)
        {
            var wire = RaidClockWire.From(new PlayerRaidClock(!waiting, waiting ? 0 : 130, false, 2, waiting, 4));
            using (var writer = new FastBufferWriter(128, Allocator.Temp))
            {
                writer.WriteValueSafe(wire);
                using (var reader = new FastBufferReader(writer, Allocator.Temp))
                { reader.ReadValueSafe(out RaidClockWire decoded); Assert.True(decoded.IsValid); Assert.True(decoded.Equals(wire)); }
            }
            wire.Floor = 4; Assert.False(wire.IsValid); wire.Floor = 2;
            wire.Waiting = true; wire.Enabled = true; Assert.False(wire.IsValid);
        }
        [Test] public void OwnerCannotDisplayClockFromAnotherMovementGeneration()
        {
            var rig = Actor(); var net = _actor.GetComponent<NetPlayerRig>();
            var view = _actor.GetComponent<NetOwnerSkillView>() ?? _actor.AddComponent<NetOwnerSkillView>();
            var receive = typeof(NetPlayerRig).GetMethod("OnRaidClockChanged", Private);
            bool Visible() => (bool)typeof(NetOwnerSkillView).GetField("_hasRaidClock", Private).GetValue(view);
            Assert.True(rig.TryBeginFloorTransit(out _));
            var wire = RaidClockWire.From(new PlayerRaidClock(false, 0, false, 2, true, rig.FloorMovementGeneration));
            receive.Invoke(net, new object[] { default(RaidClockWire), wire }); Assert.True(Visible());
            wire.MovementGeneration--; receive.Invoke(net, new object[] { default(RaidClockWire), wire }); Assert.False(Visible());
            wire.MovementGeneration = rig.FloorMovementGeneration; wire.Waiting = false; wire.Enabled = true; wire.RemainingSeconds = 120;
            receive.Invoke(net, new object[] { default(RaidClockWire), wire }); Assert.False(Visible(), "Held actor cannot display an active floor timer");
            Assert.True(rig.TryEndFloorTransit(Vector3.right * 100, Quaternion.identity, out _));
            wire.MovementGeneration = rig.FloorMovementGeneration;
            receive.Invoke(net, new object[] { default(RaidClockWire), wire }); Assert.True(Visible());
        }
    }
}
