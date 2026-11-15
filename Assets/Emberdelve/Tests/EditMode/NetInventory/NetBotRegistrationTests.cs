using System;
using System.Collections.Generic;
using System.Reflection;
using Emberdelve.Combat;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Flow;
using Emberdelve.Items;
using Emberdelve.Movement;
using Emberdelve.Net;
using Emberdelve.Player;
using NUnit.Framework;
using Unity.Netcode;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Emberdelve.Tests.NetInventory
{
    public sealed class NetBotRegistrationTests
    {
        private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
        private readonly List<GameObject> _objects = new List<GameObject>();
        private RunDirector _director;
        private NetRoleKind _priorRole;
        private object _combat;
        private Type _combatType;

        [TestCase(true, true, true, true, NetPlayerRole.HostPlayer)]
        [TestCase(true, true, true, false, NetPlayerRole.Server)]
        [TestCase(true, false, true, false, NetPlayerRole.Server)]
        [TestCase(true, true, false, true, NetPlayerRole.Server)]
        [TestCase(false, false, true, true, NetPlayerRole.Owner)]
        [TestCase(false, false, false, true, NetPlayerRole.Observer)]
        [TestCase(false, false, true, false, NetPlayerRole.Observer)]
        [TestCase(false, false, false, false, NetPlayerRole.Observer)]
        public void OnlyHumanPlayerObjectReceivesLocalRole(bool server, bool host, bool owner, bool playerObject, NetPlayerRole expected)
        {
            Assert.That(NetPlayerRig.ResolveRole(server, host, owner, playerObject), Is.EqualTo(expected));
        }

        [Test] public void HostAndTwoBotsKeepSeparateActorsWithoutInventingConnections()
        {
            _priorRole = NetRole.Current; NetRole.ResetToOffline();
            var root = new GameObject("BotRegistrationAuthority"); _objects.Add(root);
            _director = root.AddComponent<RunDirector>();
            typeof(RunDirector).GetField("config", Private).SetValue(_director,
                AssetDatabase.LoadAssetAtPath<GameConfig>("Assets/Emberdelve/Data/EmberdelveGameConfig.asset"));
            if (_director.Session == null) typeof(RunDirector).GetMethod("Awake", Private).Invoke(_director, null);
            var host = CreateActor(0, true);
            var first = CreateActor(1, false);
            var second = CreateActor(2, false);
            var table = new NetIdentityTable();
            var row = new NetIdentity { ClientId = 0, Slot = "host", PlayerId = host.PlayerId, PartyId = 0,
                Rig = host, Object = host.GetComponent<NetworkObject>() };
            Assert.True(table.Add(row));
            _combatType = typeof(NetPlayerRig).Assembly.GetType("Emberdelve.Net.NetCombatServer", true);
            _combat = Activator.CreateInstance(_combatType, new object[] { null, table, null,
                (Action<string>)(_ => { }), (Action<string>)(message => Assert.Fail(message)) });
            Invoke("OnAdmitted", row);
            Assert.False((bool)Invoke("OnBotAdmitted", host, host.GetComponent<NetPlayerRig>()));
            Assert.True((bool)Invoke("OnBotAdmitted", first, first.GetComponent<NetPlayerRig>()));
            Assert.True((bool)Invoke("OnBotAdmitted", second, second.GetComponent<NetPlayerRig>()));
            Assert.False((bool)Invoke("OnBotAdmitted", first, first.GetComponent<NetPlayerRig>()));
            Assert.That(_combatType.GetProperty("ConnectionCount").GetValue(_combat), Is.EqualTo(1));
            Assert.That(_combatType.GetProperty("ActorCount").GetValue(_combat), Is.EqualTo(3));
            Assert.That(table.Count, Is.EqualTo(1));
            Assert.True(table.TryGetByClient(0, out var retained)); Assert.That(retained, Is.SameAs(row));
            Assert.False(table.TryGetByPlayer(first.PlayerId, out _));
            Assert.False(CombatSenders.Authorize(host.CombatSender, first.CombatId, out _));
            Assert.True(CombatSenders.Authorize(first.CombatSender, first.CombatId, out _));
            Assert.True(first.Movement.ExternalDriven);
            Assert.True(first.GetComponent<MeleeCombat>().RemoteDriven);
            Assert.True(first.GetComponent<RangedCombat>().RemoteDriven);
            Invoke("Tick", 1f);
            Assert.False((bool)Invoke("OnBotRemoved", host.CombatId));
            var serverType = typeof(NetPlayerRig).Assembly.GetType("Emberdelve.Net.NetServer", true);
            var serverErrors = new List<string>();
            var server = Activator.CreateInstance(serverType, new object[] { null, null, null, (Action<string>)(_ => { }), (Action<string>)serverErrors.Add });
            serverType.GetField("_director", Private).SetValue(server, _director);
            serverType.GetField("_combat", Private).SetValue(server, _combat);
            var bots = (Dictionary<int, PlayerRig>)serverType.GetField("_bots", Private).GetValue(server);
            bots.Add(second.PlayerId, second);
            serverType.GetMethod("OnRunEnded", Private).Invoke(server, new object[] { second.PlayerId,
                new RunResult(RunState.Dead, "Archer", 0, 0, 0, 10, second.PlayerId) });
            Assert.That(serverErrors, Is.Empty, "Bots have no connection waiting for an owner-only run result");
            Assert.That(bots.Count, Is.EqualTo(1), "A result must not bypass corpse custody by despawning the bot");
            int retiredCombat = second.CombatId;
            var retiredSender = second.CombatSender;
            serverType.GetMethod("OnShutdownBegin").Invoke(server, null);
            Assert.That(serverType.GetProperty("BotCount").GetValue(server), Is.EqualTo(0));
            Assert.False(CombatSenders.Authorize(retiredSender, retiredCombat, out _));
            Assert.That(_director.Players.Count, Is.EqualTo(2));
            Assert.That(_combatType.GetProperty("ActorCount").GetValue(_combat), Is.EqualTo(2));
            Assert.That(table.Count, Is.EqualTo(1), "Bot shutdown must not invent or close human connections");
            Assert.True((bool)Invoke("OnBotRemoved", first.CombatId));
            Assert.False((bool)Invoke("OnBotRemoved", first.CombatId));
            Assert.That(_combatType.GetProperty("ActorCount").GetValue(_combat), Is.EqualTo(1));
            Assert.That(_director.LocalSession.Rig, Is.SameAs(host));
        }

        private object Invoke(string method, params object[] args) => _combatType.GetMethod(method).Invoke(_combat, args);
        private PlayerRig CreateActor(int party, bool local)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Emberdelve/Prefabs/Net/Emberdelve_NetPlayer.prefab");
            Assert.NotNull(prefab);
            var root = Object.Instantiate(prefab); root.name = "Actor_" + party; _objects.Add(root);
            var rig = root.GetComponent<PlayerRig>(); rig.WireRig();
            typeof(PlayerMovement).GetMethod("Awake", Private).Invoke(rig.Movement, null);
            var character = AssetDatabase.LoadAssetAtPath<CharacterClassData>("Assets/Emberdelve/Data/Class_Archer.asset");
            Assert.True(_director.TryJoin(rig, party, local, character, out var reason), reason);
            return rig;
        }

        [TearDown] public void Cleanup()
        {
            if (_combat != null) Invoke("Dispose");
            _combat = null;
            for (int i = _objects.Count - 1; i >= 0; i--) Object.DestroyImmediate(_objects[i]);
            if (_objects.Count > 0) NetRole.Set(_priorRole);
            _objects.Clear();
        }
    }
}
