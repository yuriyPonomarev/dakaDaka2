using System.Reflection;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Interaction;
using Emberdelve.Movement;
using Emberdelve.Net;
using Emberdelve.Player;
using NUnit.Framework;
using Unity.Collections;
using Unity.Netcode;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Emberdelve.Tests.NetInventory
{
    public sealed class DescentPortalNetworkTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private GameObject _actor, _view, _materialSource;
        private NetRoleKind _previous;
        [SetUp] public void Setup() { _previous = NetRole.Current; NetRole.ResetToOffline(); }
        [TearDown] public void Cleanup()
        {
            if (_actor != null) Object.DestroyImmediate(_actor); if (_view != null) Object.DestroyImmediate(_view);
            if (_materialSource != null) Object.DestroyImmediate(_materialSource); NetRole.Set(_previous);
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
        private static DescentPortalWire State(int count, uint generation = 0)
        {
            var state = new DescentPortalWire { Floor = 1, Count = (byte)count, Generation = generation, Available = (ushort)((1 << count) - 1) };
            for (int i = 0; i < count; i++) state.SetPosition(i, new Vector3(i * 4, 1.1f, 10));
            return state;
        }
        [TestCase(3)] [TestCase(6)] [TestCase(9)] public void SlotsPositionsMaskAndGenerationRoundTrip(int count)
        {
            var wire = State(count, 4); wire.Available &= unchecked((ushort)~2);
            using (var writer = new FastBufferWriter(512, Allocator.Temp))
            {
                writer.WriteValueSafe(wire);
                using (var reader = new FastBufferReader(writer, Allocator.Temp))
                {
                    reader.ReadValueSafe(out DescentPortalWire decoded); Assert.True(decoded.IsValid); Assert.True(decoded.Equals(wire));
                    Assert.False(decoded.IsAvailable(1)); Assert.False(decoded.IsAvailable(-1)); Assert.False(decoded.IsAvailable(count));
                    for (int i = 0; i < count; i++) Assert.That(decoded.Position(i), Is.EqualTo(wire.Position(i)));
                }
            }
        }
        [Test] public void InvalidFloorSizeMaskAndPositionsAreRejected()
        {
            var state = State(3); state.Floor = 4; Assert.False(state.IsValid);
            state = State(3); state.Count = 4; Assert.False(state.IsValid);
            state = State(3); state.Available |= 1 << 8; Assert.False(state.IsValid);
            state = State(3); state.SetPosition(0, new Vector3(float.NaN, 0, 0)); Assert.False(state.IsValid);
            state = State(3); state.SetPosition(0, Vector3.right * 100001); Assert.False(state.IsValid);
            Assert.True(default(DescentPortalWire).IsValid);
            Assert.True(new DescentPortalWire { Floor = 3 }.IsValid);
        }
        private sealed class Authority : IDescentPortalAuthority
        {
            public bool Ready = true; public int Calls, Floor, Group, Slot;
            public bool DescentReady(int floor) => Ready;
            public bool RequestDescent(int floor, int group, int slot)
            { Calls++; Floor = floor; Group = group; Slot = slot; return true; }
        }
        [Test] public void ClientViewOnlySendsOwnerIntentAndNeverConsumesOrTeleportsLocally()
        {
            var rig = Actor(); var authority = new Authority();
            _view = GameObject.CreatePrimitive(PrimitiveType.Cube); _view.GetComponent<BoxCollider>().isTrigger = true;
            _view.layer = LayerMask.NameToLayer(GameConfig.InteractLayerName);
            _view.transform.position = rig.InteractionOrigin + Vector3.forward;
            var slot = _view.AddComponent<ReplicatedDescentPortalSlot>(); slot.Configure(authority, rig, 1, 2, 2);
            Physics.SyncTransforms(); Assert.True(slot.CanInteract(rig, out var reason), reason);
            Assert.False(slot.CanInteract(null, out _));
            var before = rig.transform.position; slot.OnInteractInstant(rig);
            Assert.That(authority.Calls, Is.EqualTo(1)); Assert.That(authority.Group, Is.EqualTo(2)); Assert.That(authority.Slot, Is.EqualTo(2));
            Assert.That(rig.transform.position, Is.EqualTo(before)); Assert.False(rig.IsInFloorTransit); Assert.True(_view.activeSelf);
            authority.Ready = false; slot.OnInteractInstant(rig); Assert.That(authority.Calls, Is.EqualTo(1));
            authority.Ready = true; _view.SetActive(false); slot.OnInteractInstant(rig); Assert.That(authority.Calls, Is.EqualTo(1));
        }
        [Test] public void PresenterHidesUsedSlotsAndNeverShowsAnotherMovementGeneration()
        {
            var rig = Actor(); var owner = _actor.GetComponent<NetPlayerRig>();
            typeof(NetPlayerRig).GetProperty("Role").SetValue(owner, NetPlayerRole.Owner);
            _materialSource = GameObject.CreatePrimitive(PrimitiveType.Cube); _materialSource.name = "Gate_Portal";
            _materialSource.transform.position = Vector3.one * 100;
            var views = _actor.AddComponent<NetDescentPortalViews>(); views.Configure(owner);
            var state = State(3, rig.FloorMovementGeneration); views.Apply(state);
            Assert.That(views.Slots.Count, Is.EqualTo(9));
            for (int i = 0; i < 9; i++) Assert.That(views.Slots[i].gameObject.activeInHierarchy, Is.EqualTo(i < 3));
            state.Available = 2; views.Apply(state);
            Assert.False(views.Slots[0].gameObject.activeInHierarchy); Assert.True(views.Slots[1].gameObject.activeInHierarchy);
            Assert.True(rig.TryBeginFloorTransit(out _)); views.Apply(state); Assert.False(views.Slots[1].gameObject.activeInHierarchy);
            Assert.True(rig.TryEndFloorTransit(Vector3.zero, Quaternion.identity, out _)); views.Apply(state);
            Assert.False(views.Slots[1].gameObject.activeInHierarchy, "Old floor generation cannot reappear after arrival");
            state.Generation = rig.FloorMovementGeneration; views.Apply(state); Assert.True(views.Slots[1].gameObject.activeInHierarchy);
            state.Floor = 4; views.Apply(state); Assert.False(views.Slots[1].gameObject.activeInHierarchy);
        }
    }
}
