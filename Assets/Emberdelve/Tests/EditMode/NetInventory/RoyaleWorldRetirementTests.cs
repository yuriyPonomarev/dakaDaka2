using System;
using System.Collections.Generic;
using System.Reflection;
using Emberdelve.Core;
using Emberdelve.Items;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Emberdelve.Net.Tests
{
    public sealed class RoyaleWorldRetirementTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private NetInventoryServer _server;
        private NetRoleKind _role;
        private readonly List<GameObject> _objects = new List<GameObject>();
        private T Field<T>(string name) => (T)typeof(NetInventoryServer).GetField(name, Private).GetValue(_server);
        private static CorpseContainerModel Model(int source, Vector3 position) =>
            (CorpseContainerModel)Activator.CreateInstance(typeof(CorpseContainerModel), Private, null, new object[] { source, position }, null);
        [SetUp] public void Setup()
        { _role = NetRole.Current; NetRole.ResetToOffline(); _server = new NetInventoryServer(null, null); }
        [TearDown] public void Cleanup()
        {
            _server.Dispose(); foreach (var go in _objects) Object.DestroyImmediate(go); _objects.Clear(); NetRole.Set(_role);
        }
        [Test] public void OnlySourceContainersRetireAndCommittedDeathIdentitySurvives()
        {
            var corpses = Field<Dictionary<ulong, CorpseContainerModel>>("_corpseContainers");
            var source = Model(1, new Vector3(5, 0, 5)); var destination = Model(2, new Vector3(105, 0, 5));
            corpses.Add(1, source); corpses.Add(2, destination);
            var deaths = Field<HashSet<int>>("_committedDeathPlayers"); deaths.Add(1);
            _server.RetireFloorLoot(new Rect(0, 0, 20, 20));
            _server.RetireFloorLoot(new Rect(0, 0, 20, 20));
            Assert.False(corpses.ContainsKey(1)); Assert.That(corpses[2], Is.SameAs(destination));
            Assert.True(deaths.Contains(1));
            Assert.That(Field<List<Rect>>("_retiredFloorBounds").Count, Is.EqualTo(1));
        }
        [Test] public void OldPickupIdsAreRemovedAndDestinationPickupIsUntouched()
        {
            var pickups = Field<Dictionary<ulong, LootPickup>>("_floorLoot");
            var ids = Field<Dictionary<int, ulong>>("_floorLootObjectIds");
            for (int i = 0; i < 2; i++)
            {
                var go = new GameObject("RetirementPickup" + i); _objects.Add(go);
                var pickup = go.AddComponent<LootPickup>(); pickup.PlaceAt(new Vector3(5 + i * 100, 0, 5));
                pickups.Add((ulong)i + 1, pickup); ids.Add(pickup.GetInstanceID(), (ulong)i + 1);
            }
            _server.RetireFloorLoot(new Rect(0, 0, 20, 20));
            Assert.False(pickups.ContainsKey(1)); Assert.True(pickups.ContainsKey(2));
            Assert.That(ids.Count, Is.EqualTo(1));
            Assert.False(_objects[0].activeSelf); Assert.True(_objects[1].activeSelf);
        }
        [Test] public void ClientCannotRemoveSourceCustody()
        {
            var corpses = Field<Dictionary<ulong, CorpseContainerModel>>("_corpseContainers");
            corpses.Add(1, Model(1, new Vector3(5, 0, 5)));
            NetRole.Set(NetRoleKind.Client);
            _server.RetireFloorLoot(new Rect(0, 0, 20, 20));
            Assert.True(corpses.ContainsKey(1));
        }
    }
}
