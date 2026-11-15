using System;
using System.Collections;
using System.Reflection;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Movement;
using Emberdelve.Net;
using Emberdelve.Net.Rules;
using Emberdelve.Player;
using NUnit.Framework;
using Unity.Collections;
using Unity.Netcode;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Emberdelve.Tests.NetInventory
{
    public sealed class FloorMovementBarrierTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private GameObject _actor;
        private NetRoleKind _previousRole;
        [SetUp] public void Setup() { _previousRole = NetRole.Current; NetRole.ResetToOffline(); }
        [TearDown] public void Cleanup()
        { NetRole.ResetToOffline(); if (_actor != null) Object.DestroyImmediate(_actor); NetRole.Set(_previousRole); }

        private PlayerRig Actor()
        {
            _actor = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Emberdelve/Prefabs/Net/Emberdelve_NetPlayer.prefab"));
            var rig = _actor.GetComponent<PlayerRig>(); rig.WireRig();
            typeof(PlayerMovement).GetMethod("Awake", Private).Invoke(rig.Movement, null);
            rig.ApplyClass(AssetDatabase.LoadAssetAtPath<CharacterClassData>("Assets/Emberdelve/Data/Class_Archer.asset"),
                AssetDatabase.LoadAssetAtPath<GameConfig>("Assets/Emberdelve/Data/EmberdelveGameConfig.asset"));
            typeof(NetPlayerRig).GetField("_rig", Private).SetValue(_actor.GetComponent<NetPlayerRig>(), rig);
            return rig;
        }

        [TestCase(false)] [TestCase(true)]
        public void TransitionAndInputAckGenerationSurviveWire(bool waiting)
        {
            var transition = new FloorMovementWire { Generation = 4, InTransit = waiting,
                Position = new Vector3(120, 1.8f, 33), Rotation = Quaternion.Euler(0, 90, 0) };
            var frame = MoveInputFrame.Create(1, 0, 1, 90, 0, false, false, false);
            var batch = MoveBatchWire.From(new[] { frame }, 1, 4);
            var state = new PredictedState { X = 120, Y = 1.8f, Z = 33, CapsuleHeight = 1.8f };
            var ack = MoveAckWire.From(1, 400, in state, 4);
            using (var writer = new FastBufferWriter(512, Allocator.Temp))
            {
                writer.WriteValueSafe(transition); writer.WriteValueSafe(batch); writer.WriteValueSafe(ack);
                using (var reader = new FastBufferReader(writer, Allocator.Temp))
                {
                    reader.ReadValueSafe(out FloorMovementWire decoded); Assert.True(decoded.IsValid); Assert.True(decoded.Equals(transition));
                    reader.ReadValueSafe(out MoveBatchWire inputs); Assert.That(inputs.Generation, Is.EqualTo(4)); Assert.That(inputs.F0.Seq, Is.EqualTo(1));
                    reader.ReadValueSafe(out MoveAckWire result); Assert.That(result.Generation, Is.EqualTo(4)); Assert.That(result.X, Is.EqualTo(120));
                }
            }
            transition.Position.x = float.NaN; Assert.False(transition.IsValid);
        }

        [Test] public void ServerDiscardsQueuedAndDelayedPreviousGenerationInput()
        {
            var rig = Actor(); var network = _actor.GetComponent<NetPlayerRig>();
            var table = new NetIdentityTable();
            Assert.True(table.Add(new NetIdentity { ClientId = 7, PlayerId = rig.PlayerId, Rig = rig,
                Object = _actor.GetComponent<NetworkObject>(), Slot = "transition" }));
            var type = typeof(NetPlayerRig).Assembly.GetType("Emberdelve.Net.NetMovementServer", true);
            var server = Activator.CreateInstance(type, new object[] { null, table, (Action<string>)(_ => {}), (Action<string>)(s => Assert.Fail(s)) });
            try
            {
                var stateType = type.GetNestedType("MoveState", BindingFlags.NonPublic);
                var state = Activator.CreateInstance(stateType, true);
                stateType.GetField("PlayerId").SetValue(state, rig.PlayerId); stateType.GetField("ClientId").SetValue(state, 7UL);
                stateType.GetField("Rig").SetValue(state, rig); stateType.GetField("NetRig").SetValue(state, network);
                stateType.GetField("Object").SetValue(state, _actor.GetComponent<NetworkObject>());
                ((IDictionary)type.GetField("_states", Private).GetValue(server)).Add(rig.PlayerId, state);
                var buffer = (ServerMoveInputBuffer)stateType.GetField("Buffer").GetValue(state);
                void Receive(uint generation, uint seq)
                {
                    var frame = MoveInputFrame.Create(seq, 0, 1, 90, 0, false, true, false);
                    type.GetMethod("Receive").Invoke(server, new object[] { network, 7UL, MoveBatchWire.From(new[] { frame }, 1, generation) });
                }
                Receive(0, 1); Assert.That(buffer.Depth, Is.EqualTo(1));
                Assert.True(rig.TryBeginFloorTransit(out _)); type.GetMethod("OnFloorMovementChanged").Invoke(server, new object[] { rig });
                Assert.That(buffer.Depth, Is.Zero);
                Receive(0, 2); Receive(1, 1); Assert.That(buffer.Depth, Is.Zero);
                var held = rig.transform.position;
                type.GetMethod("StepPlayer", Private).Invoke(server, new object[] { state, rig, NetTuning.TickSeconds });
                Assert.That(rig.transform.position, Is.EqualTo(held));
                Assert.True(rig.TryEndFloorTransit(new Vector3(100, 0, 100), Quaternion.identity, out _));
                type.GetMethod("OnFloorMovementChanged").Invoke(server, new object[] { rig });
                Receive(0, 3); Receive(3, 1); Assert.That(buffer.Depth, Is.Zero);
                Receive(2, 1); Assert.That(buffer.Depth, Is.EqualTo(1), "Arrival starts a fresh sequence window even after a long wait");
                Assert.That(((InputSanitizer)stateType.GetField("Sanitizer").GetValue(state)).Accepted, Is.EqualTo(2),
                    "Transition must preserve lifetime sanitizer counters");
                Assert.False((bool)stateType.GetField("AckDirty").GetValue(state));
            }
            finally { type.GetMethod("Dispose").Invoke(server, null); }
        }

        [Test] public void OwnerClearsHistoryAndIgnoresOldAckOrTransition()
        {
            var rig = Actor(); var network = _actor.GetComponent<NetPlayerRig>();
            var prediction = _actor.AddComponent<NetOwnerPrediction>(); prediction.Configure(network);
            var reconciler = (PredictionReconciler)typeof(NetOwnerPrediction).GetField("_recon", Private).GetValue(prediction);
            reconciler.Record(new PredictionEntry { Seq = 12, State = new PredictedState { X = 1 } });
            reconciler.Blend.Add(2, 0, 0);
            typeof(NetOwnerPrediction).GetField("_predicting", Private).SetValue(prediction, true);
            NetRole.Set(NetRoleKind.Client);
            var arrival = new FloorMovementWire { Generation = 2, Position = new Vector3(100, 2, 30), Rotation = Quaternion.identity };
            void Apply(FloorMovementWire wire) => typeof(NetPlayerRig).GetMethod("OnFloorMovementChanged", Private)
                .Invoke(network, new object[] { default(FloorMovementWire), wire });
            Apply(arrival);
            Assert.That(rig.transform.position, Is.EqualTo(arrival.Position)); Assert.That(reconciler.Ring.Count, Is.Zero);
            Assert.That(reconciler.Blend.RemainingX, Is.Zero); Assert.False(prediction.IsPredicting);
            rig.Movement.CapturePredictionState(out var current);
            reconciler.Record(new PredictionEntry { Seq = 1, State = current });
            typeof(NetOwnerPrediction).GetField("_predicting", Private).SetValue(prediction, true);
            var old = new PredictedState { X = -50, CapsuleHeight = 1.8f };
            prediction.OnAck(MoveAckWire.From(1, 10, in old, 0));
            prediction.OnAck(MoveAckWire.From(1, 10, in old, 3));
            Assert.That(rig.transform.position, Is.EqualTo(arrival.Position)); Assert.That(reconciler.Ring.Count, Is.EqualTo(1));
            var stale = arrival; stale.Generation = 1; stale.InTransit = true; stale.Position = Vector3.zero; Apply(stale);
            Assert.False(rig.IsInFloorTransit); Assert.That(rig.transform.position, Is.EqualTo(arrival.Position));
            prediction.OnAck(MoveAckWire.From(1, 11, in current, 2)); Assert.That(reconciler.Stats.Acks, Is.EqualTo(1));
            var aggregate = new MoveInputAggregator(); aggregate.Sample(1, 1, true, true, true, 90, 0); aggregate.Flush();
            aggregate.ResetGeneration(); var neutral = aggregate.Flush();
            Assert.That(neutral.Seq, Is.EqualTo(1)); Assert.That(neutral.MoveX, Is.Zero); Assert.That(neutral.MoveY, Is.Zero); Assert.That(neutral.Flags, Is.Zero);
        }
    }
}
