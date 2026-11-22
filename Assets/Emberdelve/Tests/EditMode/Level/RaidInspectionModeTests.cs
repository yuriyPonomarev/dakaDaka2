using System;
using System.Reflection;
using Emberdelve.AI;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Flow;
using Emberdelve.Items;
using Emberdelve.Movement;
using Emberdelve.Player;
using Emberdelve.Rules;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Emberdelve.Level.Tests
{
    public sealed class RaidInspectionModeTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private string _priorEnvironment;
        private NetRoleKind _priorRole;
        private SceneSetup[] _priorScenes;
        private RunDirector _director;
        private DungeonModuleSceneAssembler _map;
        private PlayerRig _player;

        [SetUp]
        public void SetUp()
        {
            _priorEnvironment = Environment.GetEnvironmentVariable(GameConfig.RaidAiEnvironmentVariable);
            _priorRole = NetRole.Current;
            _priorScenes = EditorSceneManager.GetSceneManagerSetup();
            Environment.SetEnvironmentVariable(GameConfig.RaidAiEnvironmentVariable, "off");
        }

        [TearDown]
        public void TearDown()
        {
            try
            {
                CloseRaid();
                if (Array.Exists(_priorScenes, scene => scene.isActive && scene.isLoaded))
                    EditorSceneManager.RestoreSceneManagerSetup(_priorScenes);
            }
            finally
            {
                Environment.SetEnvironmentVariable(GameConfig.RaidAiEnvironmentVariable, _priorEnvironment);
                NetRole.Set(_priorRole);
            }
        }

        [Test]
        public void ExplicitProcessOverrideDoesNotMutateOrCacheTheAuthoredSetting()
        {
            var config = ScriptableObject.CreateInstance<GameConfig>();
            try
            {
                Environment.SetEnvironmentVariable(GameConfig.RaidAiEnvironmentVariable, null);
                Assert.True(config.populateRaidWithAi, "New/legacy tuning assets preserve normal combat by default");
                Assert.True(GameConfig.ShouldPopulateRaidWithAi(config));
                config.populateRaidWithAi = false;
                Assert.False(GameConfig.ShouldPopulateRaidWithAi(config));
                Environment.SetEnvironmentVariable(GameConfig.RaidAiEnvironmentVariable, " ON ");
                Assert.True(GameConfig.ShouldPopulateRaidWithAi(config));
                Assert.False(config.populateRaidWithAi, "A proof process cannot rewrite the saved inspection setting");
                Environment.SetEnvironmentVariable(GameConfig.RaidAiEnvironmentVariable, "off");
                Assert.False(GameConfig.ShouldPopulateRaidWithAi(config));
                Environment.SetEnvironmentVariable(GameConfig.RaidAiEnvironmentVariable, "typo");
                Assert.False(GameConfig.ShouldPopulateRaidWithAi(config), "Unknown values fall back to the asset");
                config.populateRaidWithAi = true;
                Assert.True(GameConfig.ShouldPopulateRaidWithAi(config));
                Environment.SetEnvironmentVariable(GameConfig.RaidAiEnvironmentVariable, null);
                Assert.True(GameConfig.ShouldPopulateRaidWithAi(null));
            }
            finally { Object.DestroyImmediate(config); }
        }

        [TestCase(NetRoleKind.Offline)]
        [TestCase(NetRoleKind.Host)]
        public void InspectionPreservesAdmissionLandingAndContentBarriersOnAllFloors(NetRoleKind role)
        {
            OpenRaid(false, role);
            var floors = _director.FloorCoordinator;
            int spawnCalls = 0;
            bool Spawn(int floor, int party, out PlayerRig rig, out string reason)
            {
                spawnCalls++; rig = null; reason = "Inspection must not request a filler"; return false;
            }
            var population = new RoyalePopulationDriver(_director, 1, 120, 15, Spawn);
            Assert.False(population.Tick(0, true, out _));
            Assert.False(floors.TryCloseAdmission(1));
            Assert.False(floors.TryStartPopulation(1, 120, out _), "No clock without an admitted human");

            var enemies = Object.FindObjectsByType<EnemyAgent>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            Assert.That(enemies.Length, Is.GreaterThan(0), "Authored monster definitions remain present");
            foreach (var enemy in enemies)
            {
                Assert.False(enemy.gameObject.activeSelf);
                Assert.False(enemy.IsRuntimeInitialized);
            }
            Assert.True(_map.EnsureInitialContentReady(out var why), why);
            enemies[0].gameObject.SetActive(true);
            Assert.False(_map.EnsureInitialContentReady(out _), "An unexpectedly active monster is not ready inspection content");
            enemies[0].gameObject.SetActive(false);

            JoinHuman();
            string kit = LoadoutSnapshot.Capture(_player.Inventory, _player.Equipment).ToJson();
            Assert.False(population.Tick(0, false, out _));
            Assert.False(population.Tick(14, true, out _), "Admission delay is unchanged");
            Assert.False(population.Tick(15, false, out _), "A pending map ACK still blocks admission");
            Assert.False(floors.IsAdmissionClosed(1));
            var blocker = GameObject.CreatePrimitive(PrimitiveType.Cube);
            try
            {
                blocker.transform.position = _map.RuntimeLayout.PlayerSpawnWorld(0) + Vector3.up;
                blocker.transform.localScale = Vector3.one * .5f;
                Physics.SyncTransforms();
                Assert.False(population.Tick(15, true, out _), "Inspection cannot bypass an obstructed landing");
                Assert.True(_player.IsInFloorTransit);
                Assert.False(_director.TryGetRoyaleFloorClock(1, out _, out _));
            }
            finally { Object.DestroyImmediate(blocker); Physics.SyncTransforms(); }
            NetRole.Set(NetRoleKind.Client);
            Assert.False(population.Tick(16, true, out _), "Clients cannot start inspection floors");
            NetRole.Set(role);
            Assert.True(population.Tick(16, true, out why), why);
            Assert.False(_player.IsInFloorTransit);
            Assert.False(floors.TrySpawnBot(1, 1, PlayerPrefab(), Character(), out _, out _));
            Assert.That(_director.Players.Count, Is.EqualTo(1));

            for (int floor = 2; floor <= 3; floor++)
            {
                Assert.True(floors.TryPrepareDestination(floor, out why), why);
                Assert.False(floors.TryCloseAdmission(floor));
                Assert.True(floors.TryActivate(floor, out why), why);
                var layout = floors.ActiveLayoutFor(floor);
                Assert.NotNull(layout);
                FloorContentSpawner content = null;
                foreach (var candidate in Object.FindObjectsByType<FloorContentSpawner>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                    if (candidate.name == "Floor" + floor + "_Content") content = candidate;
                Assert.NotNull(content);
                Assert.That(content.Enemies.Count, Is.Zero);
                Assert.That(content.Loot.Count, Is.EqualTo(layout.lootSpawns.Count), "Inspection retains floor loot");
                Assert.True(_director.TryQueueDescent(_player, floor - 1, out why), why);
                Assert.True(floors.TryReserveIncoming(_player, floor, out why), why);
                var destination = new RoyalePopulationDriver(_director, floor, 120, 0, Spawn);
                Assert.False(destination.Tick(0, false, out _), "Destination readiness is still required");
                Assert.True(destination.Tick(1, true, out why), why);
                Assert.That(_director.SessionOf(_player).Floor, Is.EqualTo(floor));
                Assert.False(_player.IsInFloorTransit);
                Assert.That(floors.ReservedCount(floor), Is.EqualTo(1));
                Assert.That(LoadoutSnapshot.Capture(_player.Inventory, _player.Equipment).ToJson(), Is.EqualTo(kit));
            }
            Assert.That(spawnCalls, Is.Zero);
            Assert.False(_director.SessionOf(_player).HasResult);
        }

        [Test]
        public void LobbySceneRoundTripResolvesNewRaidPolicyWithoutLeakingDisabledActors()
        {
            // Exercise fresh scene ownership without calling the live profile-saving ReturnToLobby API
            // from an EditMode test. No player save/loadout is written by this fixture.
            foreach (bool populateAi in new[] { false, true, false })
            {
                OpenRaid(populateAi, NetRoleKind.Offline);
                JoinHuman();
                var floors = _director.FloorCoordinator;
                Assert.True(floors.TryCloseAdmission(1));
                var enemies = Object.FindObjectsByType<EnemyAgent>(FindObjectsInactive.Include, FindObjectsSortMode.None);
                foreach (var enemy in enemies) Assert.That(enemy.gameObject.activeSelf, Is.EqualTo(populateAi));
                if (populateAi)
                    Assert.False(floors.TryStartPopulation(1, 120, out _), "Normal mode still requires full population");
                int spawnCalls = 0;
                bool Spawn(int floor, int party, out PlayerRig rig, out string why)
                {
                    spawnCalls++;
                    if (!floors.TrySpawnBot(floor, party, PlayerPrefab(), Character(), out rig, out why)) return false;
                    typeof(PlayerMovement).GetMethod("Awake", Private).Invoke(rig.Movement, null);
                    return true;
                }
                var population = new RoyalePopulationDriver(_director, 1, 120, 0, Spawn);
                Assert.True(population.Tick(0, true, out var reason), reason);
                Assert.That(_director.Players.Count, Is.EqualTo(populateAi ? 16 : 1));
                Assert.That(spawnCalls, Is.EqualTo(populateAi ? 15 : 0));
                foreach (var enemy in enemies) Assert.That(enemy.IsRuntimeInitialized, Is.EqualTo(populateAi));
                CloseRaid();
                EditorSceneManager.OpenScene("Assets/Emberdelve/Scenes/Lobby.unity", OpenSceneMode.Single);
            }
        }

        private void OpenRaid(bool populateAi, NetRoleKind role)
        {
            Environment.SetEnvironmentVariable(GameConfig.RaidAiEnvironmentVariable, populateAi ? "on" : "off");
            NetRole.Set(role); RunContext.ResetHandoff(); DungeonModulePlanSession.Reset();
            RunContext.SelectRoyaleMode(RoyalePartyMode.Solo); RunContext.SetNextRaidSeed(11);
            if (NetRole.IsNetworked)
            {
                // NetServer installs the authoritative plan before loading the dungeon. Networked
                // assemblers must consume that plan instead of using the offline seed fallback.
                var catalog = Resources.Load<DungeonModuleCatalog>("Emberdelve/DungeonModuleCatalog");
                Assert.NotNull(catalog);
                ulong raidSeed = RunContext.ResolveRaidSeed(out _);
                RunContext.SetNextRaidSeed(raidSeed);
                int mapSeed = unchecked((int)(raidSeed ^ (raidSeed >> 32)));
                Assert.True(DungeonModulePlanner.TryCreate(mapSeed, catalog.Modules, out var plan, out var planReason), planReason);
                DungeonModulePlanSession.Install(plan.WithRoyaleMode(RunContext.SelectedRoyaleMode));
            }
            EditorSceneManager.OpenScene("Assets/Emberdelve/Scenes/Emberdelve_ModularCrypts.unity", OpenSceneMode.Single);
            _director = Object.FindFirstObjectByType<RunDirector>(FindObjectsInactive.Include);
            _map = Object.FindFirstObjectByType<DungeonModuleSceneAssembler>(FindObjectsInactive.Include);
            Assert.NotNull(_director); Assert.NotNull(_map);
            _director.SetServerMode(true);
            typeof(DungeonModuleSceneAssembler).GetMethod("Awake", Private).Invoke(_map, null);
            typeof(RunDirector).GetMethod("Awake", Private).Invoke(_director, null);
            Assert.True(_map.RuntimeMapValidationComplete);
            typeof(RunDirector).GetMethod("Start", Private).Invoke(_director, null);
            Assert.True(_director.IsStarted);
            Assert.True(_director.FloorCoordinator.TryAdoptInitialMap(_map, out var reason), reason);
        }

        private void JoinHuman()
        {
            _player = Object.FindFirstObjectByType<PlayerRig>(FindObjectsInactive.Include);
            Assert.NotNull(_player);
            _player.WireRig();
            typeof(PlayerMovement).GetMethod("Awake", Private).Invoke(_player.Movement, null);
            Assert.True(_director.TryJoin(_player, 0, true, Character(), out var reason), reason);
            Assert.True(_director.FloorCoordinator.TryHoldInitialPlayer(_player, out reason), reason);
        }

        private void CloseRaid()
        {
            if (_director != null)
                typeof(RoyaleFloorCoordinator).GetMethod("ReleaseOwnedMaps", Private).Invoke(_director.FloorCoordinator, null);
            if (_map != null) typeof(DungeonModuleSceneAssembler).GetMethod("ClearGeneratedModules", Private).Invoke(_map, null);
            _director = null; _map = null; _player = null;
            DungeonModulePlanSession.Reset(); RunContext.ResetHandoff();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        private static CharacterClassData Character() => AssetDatabase.LoadAssetAtPath<CharacterClassData>("Assets/Emberdelve/Data/Class_Archer.asset");
        private static GameObject PlayerPrefab() => AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Emberdelve/Prefabs/Net/Emberdelve_NetPlayer.prefab");
    }
}
