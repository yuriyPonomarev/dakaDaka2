using System.Collections.Generic;
using System.Reflection;
using Emberdelve.AI;
using Emberdelve.Combat;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Flow;
using Emberdelve.Items;
using Emberdelve.Interaction;
using Emberdelve.Level;
using Emberdelve.Movement;
using Emberdelve.Player;
using Emberdelve.Rules;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Emberdelve.Combat.Tests
{
    public sealed class RoyaleInitialPopulationTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        [TestCase(RoyalePartyMode.Solo, 16, false)]
        [TestCase(RoyalePartyMode.Duo, 16, false)]
        [TestCase(RoyalePartyMode.Trio, 15, false)]
        [TestCase(RoyalePartyMode.Solo, 16, true)]
        [TestCase(RoyalePartyMode.Duo, 16, true)]
        [TestCase(RoyalePartyMode.Trio, 15, true)]
        public void AuthoredInitialWorldAdmitsFullRosterWithoutDuplicatingContent(RoyalePartyMode mode, int capacity, bool automatic)
            => VerifyInitialWorld(mode, capacity, automatic, 11);

        [TestCase(RoyalePartyMode.Solo, 16)] [TestCase(RoyalePartyMode.Duo, 16)] [TestCase(RoyalePartyMode.Trio, 15)]
        public void AlternateLayoutHasSafePortalWaves(RoyalePartyMode mode, int capacity)
            => VerifyInitialWorld(mode, capacity, true, 2043687868);

        [TestCase(RoyalePartyMode.Solo, 16, 11)]
        [TestCase(RoyalePartyMode.Duo, 16, 11)]
        [TestCase(RoyalePartyMode.Trio, 15, 11)]
        [TestCase(RoyalePartyMode.Trio, 15, 2043687868)]
        public void DescentCohortReservesLateSurvivorsBeforeRefillingBothDestinations(RoyalePartyMode mode, int capacity, int seed)
            => VerifyInitialWorld(mode, capacity, true, (ulong)seed, true);

        [TestCase(11)] [TestCase(2043687868)]
        public void BotsPrioritizeReachableSafeZoneOverCombat(int seed)
            => VerifyInitialWorld(RoyalePartyMode.Solo, 16, true, (ulong)seed, zoneRecovery: true);

        [TestCase(false)] [TestCase(true)]
        public void BotPicksExactGroundInstanceOnceThroughAuthority(bool networkPickup)
            => VerifyInitialWorld(RoyalePartyMode.Solo, 16, true, 11, botLoot: true, networkPickup: networkPickup);

        [TestCase(RoyalePartyMode.Solo, 16)] [TestCase(RoyalePartyMode.Duo, 16)] [TestCase(RoyalePartyMode.Trio, 15)]
        public void BotEntersOpenPortalAndArrivesWithExactKit(RoyalePartyMode mode, int capacity)
            => VerifyInitialWorld(mode, capacity, true, 11, botDescent: true);

        [TestCase(RoyalePartyMode.Solo, 16, false)] [TestCase(RoyalePartyMode.Duo, 16, false)]
        [TestCase(RoyalePartyMode.Trio, 15, false)] [TestCase(RoyalePartyMode.Trio, 15, true)]
        public void BotFinalExitRequiresUninterruptedHoldAndPreservesLoot(RoyalePartyMode mode, int capacity, bool host)
            => VerifyInitialWorld(mode, capacity, true, 11, descend: true, botExtraction: true, networkPickup: host);

        private void VerifyInitialWorld(RoyalePartyMode mode, int capacity, bool automatic, ulong seed, bool descend = false,
            bool zoneRecovery = false, bool botLoot = false, bool networkPickup = false, bool botDescent = false, bool botExtraction = false)
        {
            var setup = EditorSceneManager.GetSceneManagerSetup();
            var previousRole = NetRole.Current;
            var actors = new List<PlayerRig>();
            DungeonModuleSceneAssembler map = null;
            try
            {
                NetRole.ResetToOffline(); RunContext.ResetHandoff(); DungeonModulePlanSession.Reset();
                RunContext.SelectRoyaleMode(mode); RunContext.SetNextRaidSeed(seed);
                EditorSceneManager.OpenScene("Assets/Emberdelve/Scenes/Emberdelve_ModularCrypts.unity", OpenSceneMode.Single);
                var director = Object.FindFirstObjectByType<RunDirector>(FindObjectsInactive.Include);
                map = Object.FindFirstObjectByType<DungeonModuleSceneAssembler>(FindObjectsInactive.Include);
                Assert.NotNull(director); Assert.NotNull(map);
                director.SetServerMode(true);
                typeof(DungeonModuleSceneAssembler).GetMethod("Awake", Private).Invoke(map, null);
                typeof(RunDirector).GetMethod("Awake", Private).Invoke(director, null);
                Assert.True(map.RuntimeMapValidationComplete);
                Assert.That(map.AppliedRoyaleMode, Is.EqualTo(mode));
                typeof(RunDirector).GetMethod("Start", Private).Invoke(director, null);
                Assert.True(director.IsStarted);
                int enemies = Object.FindObjectsByType<EnemyAgent>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length;
                int loot = Object.FindObjectsByType<LootPickup>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length;
                int chests = director.Chests.Count;
                var coordinator = director.FloorCoordinator;
                Assert.True(coordinator.TryAdoptInitialMap(map, out var reason), reason);
                Assert.True(coordinator.TryAdoptInitialMap(map, out reason), reason);
                Assert.False(coordinator.TryDiscardUnused(1), "Scene-owned initial map must never be discarded as a staged floor");
                Assert.That(coordinator.CopyPreparedPlan(1).RoyaleMode, Is.EqualTo(mode));
                var player = Object.FindFirstObjectByType<PlayerRig>(FindObjectsInactive.Include);
                player.WireRig();
                typeof(PlayerMovement).GetMethod("Awake", Private).Invoke(player.Movement, null);
                var character = AssetDatabase.LoadAssetAtPath<CharacterClassData>("Assets/Emberdelve/Data/Class_Archer.asset");
                Assert.True(director.TryJoin(player, 0, true, character, out reason), reason);
                actors.Add(player);
                var kits = new Dictionary<int, string>();
                var ammunition = new Dictionary<int, string>();
                ammunition.Add(player.PlayerId, CaptureAmmo(player));
                kits.Add(player.PlayerId, LoadoutSnapshot.Capture(player.Inventory, player.Equipment).ToJson());
                Assert.True(coordinator.TryHoldInitialPlayer(player, out reason), reason);
                Assert.True(coordinator.TryHoldInitialPlayer(player, out reason), "Initial hold is idempotent");
                Assert.True(player.IsInFloorTransit);
                Assert.False(coordinator.TryArrive(player, 1, out _));
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Emberdelve/Prefabs/Net/Emberdelve_NetPlayer.prefab");
                // A cancelled held human must release only its own seat, including within a partial party.
                var cancelledObject = Object.Instantiate(prefab);
                var cancelled = cancelledObject.GetComponent<PlayerRig>(); cancelled.WireRig();
                typeof(PlayerMovement).GetMethod("Awake", Private).Invoke(cancelled.Movement, null);
                Assert.True(director.TryJoin(cancelled, -1, false, character, out reason), reason);
                Assert.True(cancelled.IsInFloorTransit);
                Assert.That(cancelled.PartyId, Is.EqualTo(1 / (int)mode), "Automatic admission respects the selected party size");
                Assert.True(director.RemovePlayer(cancelled.PlayerId, "Cancelled initial admission"));
                Object.DestroyImmediate(cancelledObject);
                var identities = new HashSet<int> { player.CombatId };
                void RecordBot(PlayerRig bot)
                {
                    typeof(PlayerMovement).GetMethod("Awake", Private).Invoke(bot.Movement, null);
                    actors.Add(bot);
                    Assert.True(identities.Add(bot.CombatId));
                    Assert.True(CombatSenders.Authorize(bot.CombatSender, bot.CombatId, out _));
                    Assert.True(bot.IsInFloorTransit);
                    kits.Add(bot.PlayerId, LoadoutSnapshot.Capture(bot.Inventory, bot.Equipment).ToJson());
                    ammunition.Add(bot.PlayerId, CaptureAmmo(bot));
                }
                if (automatic)
                {
                    bool failOnce = true;
                    bool Spawn(int floor, int party, out PlayerRig rig, out string why)
                    {
                        rig = null; why = "Simulated transient spawn failure";
                        if (actors.Count == 3 && failOnce) { failOnce = false; return false; }
                        if (!coordinator.TrySpawnBot(floor, party, prefab, character, out rig, out why)) return false;
                        RecordBot(rig); return true;
                    }
                    var population = new RoyalePopulationDriver(director, 1, 120, 15, Spawn);
                    Assert.False(population.Tick(0, false, out _));
                    Assert.False(population.Tick(14, true, out _));
                    Assert.False(population.Tick(15, false, out _), "Pending map ACK must delay cutoff/fill");
                    Assert.That(actors.Count, Is.EqualTo(1));
                    Assert.False(population.Tick(15, true, out _), "Spawn failure must not start the clock");
                    Assert.That(actors.Count, Is.EqualTo(3));
                    Assert.True(coordinator.IsAdmissionClosed(1));
                    Assert.False(director.TryGetRoyaleFloorClock(1, out _, out _));
                    // Other seats can fill, but the full cohort cannot start with an obstructed human landing.
                    var blocker = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    try
                    {
                        blocker.transform.position = map.RuntimeLayout.PlayerSpawnWorld(0) + Vector3.up;
                        blocker.transform.localScale = Vector3.one * .5f; Physics.SyncTransforms();
                        Assert.False(population.Tick(16, true, out _));
                        Assert.That(actors.Count, Is.EqualTo(capacity));
                        Assert.False(director.TryGetRoyaleFloorClock(1, out _, out _));
                        foreach (var actor in actors) Assert.True(actor.IsInFloorTransit);
                    }
                    finally { Object.DestroyImmediate(blocker); Physics.SyncTransforms(); }
                    Assert.False(population.Tick(17, false, out _));
                    Assert.True(population.Tick(18, true, out reason), reason);
                    Assert.True(population.Tick(19, true, out reason), "Successful driver is idempotent");
                    Assert.That(actors.Count, Is.EqualTo(capacity));
                }
                else for (int i = 1; i < capacity; i++)
                {
                    Assert.True(coordinator.TrySpawnBot(1, i / (int)mode, prefab, character, out var bot, out reason), reason);
                    RecordBot(bot);
                }
                Assert.False(coordinator.TrySpawnBot(1, 100, prefab, character, out _, out _), "No seventeenth/sixteenth participant");
                var excessObject = Object.Instantiate(prefab);
                var excess = excessObject.GetComponent<PlayerRig>(); excess.WireRig();
                Assert.False(director.TryJoin(excess, 100, false, character, out _), "Excess human must be rejected before class/kit admission");
                Assert.False(CombatSenders.TryGetOwner(excess.CombatId, out _), "Rejected human has no admitted sender binding");
                Object.DestroyImmediate(excessObject);
                Assert.That(director.Players.Count, Is.EqualTo(capacity));
                if (!automatic)
                {
                    Assert.False(director.TryGetRoyaleFloorClock(1, out _, out _));
                    Assert.True(director.TryStartRoyaleFloor(1, 120, out reason), reason);
                }
                for (int i = 0; i < actors.Count; i++)
                {
                    var actor = actors[i];
                    if (!automatic) Assert.True(coordinator.TryArrive(actor, 1, out reason), reason);
                    Assert.That(actor.PartyId, Is.EqualTo(i / (int)mode));
                    Assert.That(actor.transform.position, Is.EqualTo(map.RuntimeLayout.PlayerSpawnWorld(i) + Vector3.up * .15f));
                    Assert.That(LoadoutSnapshot.Capture(actor.Inventory, actor.Equipment).ToJson(), Is.EqualTo(kits[actor.PlayerId]));
                    Assert.That(CaptureAmmo(actor), Is.EqualTo(ammunition[actor.PlayerId]));
                    Assert.False(actor.IsInFloorTransit);
                    Assert.False(coordinator.TryArrive(actor, 1, out _));
                }
                Assert.That(Object.FindObjectsByType<EnemyAgent>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length, Is.EqualTo(enemies));
                Assert.That(Object.FindObjectsByType<LootPickup>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length, Is.EqualTo(loot));
                Assert.That(director.Chests.Count, Is.EqualTo(chests));
                if (automatic)
                {
                    var encounter = coordinator.EncounterFor(1); Assert.NotNull(encounter);
                    if (botLoot)
                    {
                        var bot = actors[1];
                        var definition = bot.Equipment.MainHandInstance(0).Definition;
                        Assert.True(bot.Inventory.Catalog.TryMintCommon(ItemIdentityIssuer.CreateLocalTrusted(), definition,
                            out var instance, out reason), reason);
                        var pickup = director.SpawnLootInstance(instance, bot.transform.position + Vector3.up * .45f);
                        Assert.NotNull(pickup);
                        // Awake is not invoked for a runtime-created component by EditMode.
                        pickup.PlaceAt(pickup.transform.position); Physics.SyncTransforms();
                        try
                        {
                            if (networkPickup) { pickup.SetNetworkLootId(137); NetRole.Set(NetRoleKind.Host); }
                            Assert.True(pickup.CanInteract(bot, out reason), reason);
                            int bagUnitsBefore = bot.Inventory.TotalUnits;
                            // EditMode executes the real transaction, but Unity refuses deferred destruction.
                            // The source's spent flag still prevents replay; cleanup below owns the test object.
                            UnityEngine.TestTools.LogAssert.Expect(LogType.Error,
                                new System.Text.RegularExpressions.Regex("Destroy may not be called from edit mode"));
                            bot.GetComponent<RoyaleBotController>().Step(.02f);
                            Assert.True(pickup.IsSpent);
                            Assert.That(bot.Inventory.TotalUnits, Is.EqualTo(bagUnitsBefore + 1));
                            int matching = 0;
                            foreach (var slot in bot.Inventory.Slots)
                                if (slot.UidText == instance.UidText) { matching++; Assert.That(slot.Instance, Is.SameAs(instance)); }
                            Assert.That(matching, Is.EqualTo(1));
                            for (int tick = 0; tick < 10; tick++) bot.GetComponent<RoyaleBotController>().Step(.02f);
                            Assert.That(bot.Inventory.TotalUnits, Is.EqualTo(bagUnitsBefore + 1));
                            Assert.False(pickup.TryCollectFromNetwork(bot), "Spent source cannot be claimed twice");
                        }
                        finally { if (pickup != null) Object.DestroyImmediate(pickup.gameObject); NetRole.ResetToOffline(); }
                        return;
                    }
                    Assert.That(encounter.Groups.Count, Is.EqualTo(3));
                    foreach (var group in encounter.Groups)
                    {
                        var slots = group.GetComponentsInChildren<DescentPortalSlot>(true);
                        Assert.That(slots.Length, Is.EqualTo((int)mode));
                        foreach (var slot in slots) Assert.True(encounter.Swarm.ContainsThroughEnd(
                            slot.transform.position.x, slot.transform.position.z, .75f), "Whole portal slot stays inside shrinking zone");
                        Assert.False(group.gameObject.activeSelf);
                    }
                    void At(float elapsed)
                    {
                        ((float[])typeof(RunDirector).GetField("_floorStartedAt", Private).GetValue(director))[1] = director.Elapsed - elapsed;
                        encounter.Tick();
                    }
                    if (zoneRecovery)
                    {
                        At(100);
                        // Exercise safety movement with no visible escape route; portal priority has its own cases.
                        foreach (var group in encounter.Groups) group.gameObject.SetActive(false);
                        Assert.True(encounter.TrySample(out var zone));
                        PlayerRig escaping = null;
                        foreach (var candidate in actors)
                            if (candidate != player && !zone.Contains(candidate.transform.position.x, candidate.transform.position.z))
                            { escaping = candidate; break; }
                        Assert.NotNull(escaping, "Layout must exercise an outside bot");
                        var brain = escaping.GetComponent<RoyaleBotController>();
                        Vector3 start = escaping.transform.position;
                        // Offer a visible hostile nearby; zone movement must win over stopping to fire.
                        player.Movement.TeleportTo(start + Vector3.forward * 3f); Physics.SyncTransforms();
                        for (int step = 0; step < 60; step++) { brain.Step(.02f); Physics.SyncTransforms(); }
                        Assert.True(brain.SeekingSafeZone);
                        Assert.That(brain.TargetPlayerId, Is.EqualTo(-1));
                        Assert.That(Vector3.Distance(start, escaping.transform.position), Is.GreaterThan(.1f));
                        var route = new List<Vector3>();
                        var finder = new NavGridPathfinder(map.RuntimeNavGrid);
                        Assert.True(finder.TryFindPathInsideCircle(escaping.transform.position,
                            new Vector2((float)zone.X, (float)zone.Z), (float)zone.Radius - 4f, route));
                        // Follow the actual route with the character controller and ordinary door intent.
                        for (int step = 0; step < 3000 && brain.SeekingSafeZone; step++)
                        { brain.Step(.02f); Physics.SyncTransforms(); }
                        Assert.False(brain.SeekingSafeZone, "Bot must reach safety without teleporting");
                        Assert.True(zone.Contains(escaping.transform.position.x, escaping.transform.position.z));
                        Assert.That(LoadoutSnapshot.Capture(escaping.Inventory, escaping.Equipment).ToJson(), Is.EqualTo(kits[escaping.PlayerId]));
                        return;
                    }
                    if (botDescent)
                    {
                        var bot = actors[1]; var brain = bot.GetComponent<RoyaleBotController>();
                        var slot = encounter.Groups[0].GetComponentsInChildren<DescentPortalSlot>(true)[0];
                        bot.Movement.TeleportTo(slot.transform.position - Vector3.up * 1.1f); Physics.SyncTransforms();
                        At(29); brain.Step(.02f);
                        Assert.False(bot.IsInFloorTransit, "Unopened portal must not be discovered/consumed");
                        Assert.That(slot.Group.RemainingSlots, Is.EqualTo((int)mode));
                        string carriedKit = LoadoutSnapshot.Capture(bot.Inventory, bot.Equipment).ToJson();
                        string carriedAmmo = CaptureAmmo(bot);
                        At(60.1f);
                        for (int step = 0; step < 40 && !bot.IsInFloorTransit; step++)
                        { brain.Step(.02f); Physics.SyncTransforms(); }
                        Assert.True(bot.IsInFloorTransit, "Normal brain must request the portal transition");
                        Assert.True(slot.Group.SlotUsed(slot.Slot));
                        Assert.That(slot.Group.RemainingSlots, Is.EqualTo((int)mode - 1));
                        brain.Step(.02f);
                        Assert.That(slot.Group.RemainingSlots, Is.EqualTo((int)mode - 1), "Held actor cannot consume again");
                        Assert.False(slot.Group.TryEnter(bot, slot.Slot, out _));
                        Assert.That(LoadoutSnapshot.Capture(bot.Inventory, bot.Equipment).ToJson(), Is.EqualTo(carriedKit));
                        bool Prepare(int floor, out string why)
                        {
                            var catalog = Resources.Load<DungeonModuleCatalog>("Emberdelve/DungeonModuleCatalog");
                            if (!DungeonModulePlanner.TryCreate(7930, catalog.Modules, out var plan, out why)) return false;
                            return coordinator.TryPrepare(floor, map.RuntimeLayout, catalog, plan, new Vector3(1000, 0, 1000), out why);
                        }
                        bool Spawn(int floor, int party, out PlayerRig rig, out string why)
                        {
                            if (!coordinator.TrySpawnBot(floor, party, prefab, character, out rig, out why)) return false;
                            actors.Add(rig); return true;
                        }
                        var descentDriver = new RoyaleDescentDriver(director, 120, Prepare, Spawn, _ => true);
                        try
                        {
                            Assert.False(descentDriver.Tick(0, out _), "Living source runners must keep destination admission open");
                            Assert.True(bot.IsInFloorTransit);
                            At(120);
                            Assert.True(descentDriver.Tick(1, out reason), reason);
                            Assert.That(director.SessionOf(bot).Floor, Is.EqualTo(2));
                            Assert.False(bot.IsInFloorTransit);
                            Assert.That(coordinator.ReservedCount(2), Is.EqualTo(capacity));
                            Assert.That(LoadoutSnapshot.Capture(bot.Inventory, bot.Equipment).ToJson(), Is.EqualTo(carriedKit));
                            Assert.That(CaptureAmmo(bot), Is.EqualTo(carriedAmmo));
                            Assert.That(brain.Floor, Is.EqualTo(2));
                        }
                        finally { typeof(RoyaleFloorCoordinator).GetMethod("ReleaseOwnedMaps", Private).Invoke(coordinator, null); }
                        return;
                    }
                    At(29); Assert.False(encounter.Groups[0].gameObject.activeSelf);
                    At(31); Assert.True(encounter.Groups[0].gameObject.activeSelf); Assert.False(encounter.Groups[1].gameObject.activeSelf);
                    At(49); Assert.True(encounter.Groups[1].gameObject.activeSelf); Assert.False(encounter.Groups[2].gameObject.activeSelf);
                    At(59.9f); Assert.False(encounter.Groups[2].gameObject.activeSelf);
                    At(60.1f); Assert.True(encounter.Groups[2].gameObject.activeSelf, "Final group opens in last60seconds");
                    var chosen = encounter.Groups[0].GetComponentsInChildren<DescentPortalSlot>(true)[0];
                    player.Movement.TeleportTo(chosen.transform.position - Vector3.up * 1.1f); Physics.SyncTransforms();
                    Assert.True(chosen.Group.TryEnter(player, chosen.Slot, out reason), reason);
                    Assert.False(chosen.gameObject.activeSelf); Assert.True(player.IsInFloorTransit);
                    Assert.That(chosen.Group.RemainingSlots, Is.EqualTo((int)mode - 1));
                    Assert.False(chosen.Group.TryEnter(actors[1], chosen.Slot, out _));
                    Assert.That(LoadoutSnapshot.Capture(player.Inventory, player.Equipment).ToJson(), Is.EqualTo(kits[player.PlayerId]));
                    Assert.That(CaptureAmmo(player), Is.EqualTo(ammunition[player.PlayerId]));
                    actors[1].Movement.TeleportTo(map.RuntimeLayout.origin - Vector3.one * 20);
                    float before = actors[1].CurrentHealth, heldHealth = player.CurrentHealth;
                    At(65); Assert.Less(actors[1].CurrentHealth, before, "Authority zone damages only running outside actors");
                    Assert.That(player.CurrentHealth, Is.EqualTo(heldHealth), "Transit actor is protected from source zone");
                    before = actors[1].CurrentHealth; NetRole.Set(NetRoleKind.Client); At(67);
                    Assert.That(actors[1].CurrentHealth, Is.EqualTo(before), "Client cannot apply zone damage"); NetRole.ResetToOffline();
                    At(120); foreach (var group in encounter.Groups) Assert.False(group.gameObject.activeSelf);
                    if (descend) VerifyDescentCohorts(director, map, actors, prefab, character, capacity, (int)seed);
                    if (botExtraction) VerifyBotExtraction(director, actors, networkPickup);
                }
                var layout = map.RuntimeLayout;
                typeof(RoyaleFloorCoordinator).GetMethod("ReleaseOwnedMaps", Private).Invoke(coordinator, null);
                Assert.True(map != null && map.gameObject.activeInHierarchy);
                Assert.That(map.RuntimeLayout, Is.SameAs(layout));
            }
            finally
            {
                foreach (var actor in actors) if (actor != null) Object.DestroyImmediate(actor.gameObject);
                if (map != null) typeof(DungeonModuleSceneAssembler).GetMethod("ClearGeneratedModules", Private).Invoke(map, null);
                DungeonModulePlanSession.Reset(); RunContext.ResetHandoff(); NetRole.Set(previousRole);
                if (System.Array.Exists(setup, scene => scene.isActive && scene.isLoaded)) EditorSceneManager.RestoreSceneManagerSetup(setup);
                else EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
        }
        private static void VerifyDescentCohorts(RunDirector director, DungeonModuleSceneAssembler initial,
            List<PlayerRig> actors, GameObject prefab, CharacterClassData character, int capacity, int seed)
        {
            var coordinator = director.FloorCoordinator;
            var catalog = AssetDatabase.LoadAssetAtPath<DungeonModuleCatalog>("Assets/Emberdelve/Resources/Emberdelve/DungeonModuleCatalog.asset");
            bool ready = false, failPrepare = true, failSpawn = true;
            int spawnCalls = 0;
            bool Prepare(int floor, out string reason)
            {
                if (failPrepare) { failPrepare = false; reason = "Transient map failure"; return false; }
                if (!DungeonModulePlanner.TryCreate(unchecked(seed + floor * 7919), catalog.Modules, out var plan, out reason)) return false;
                return coordinator.TryPrepare(floor, initial.RuntimeLayout, catalog, plan, new Vector3(floor * 1000, 0, 1000), out reason);
            }
            bool Spawn(int floor, int party, out PlayerRig bot, out string reason)
            {
                spawnCalls++; bot = null;
                if (failSpawn && spawnCalls == 3) { failSpawn = false; reason = "Transient bot failure"; return false; }
                if (!coordinator.TrySpawnBot(floor, party, prefab, character, out bot, out reason)) return false;
                typeof(PlayerMovement).GetMethod("Awake", Private).Invoke(bot.Movement, null);
                actors.Add(bot); return true;
            }
            var driver = new RoyaleDescentDriver(director, 120, Prepare, Spawn, _ => ready);
            for (int source = 1; source <= 2; source++)
            {
                int destination = source + 1;
                var survivors = new List<PlayerRig>();
                foreach (var actor in actors)
                    if (director.SessionOf(actor).Floor == source && survivors.Count < (int)director.RoyaleMode.Value + 1)
                        survivors.Add(actor); // One full party plus one member of another party.
                Assert.That(survivors.Count, Is.EqualTo((int)director.RoyaleMode.Value + 1));
                var items = new Dictionary<int, string>(); var ammo = new Dictionary<int, string>();
                foreach (var actor in survivors)
                { items[actor.PlayerId] = LoadoutSnapshot.Capture(actor.Inventory, actor.Equipment).ToJson(); ammo[actor.PlayerId] = CaptureAmmo(actor); }
                ((float[])typeof(RunDirector).GetField("_floorStartedAt", Private).GetValue(director))[source] = director.Elapsed - 119;
                if (!survivors[0].IsInFloorTransit) Assert.True(director.TryQueueDescent(survivors[0], source, out _));
                int callsBefore = spawnCalls;
                if (source == 1) Assert.False(driver.Tick(100, out _), "Map failure must leave survivor held");
                Assert.False(driver.Tick(101, out _));
                Assert.That(coordinator.ReservedCount(destination), Is.EqualTo(1));
                Assert.That(spawnCalls, Is.EqualTo(callsBefore));
                for (int i = 1; i < survivors.Count; i++) Assert.True(director.TryQueueDescent(survivors[i], source, out _));
                Assert.False(driver.Tick(102, out _));
                Assert.That(coordinator.ReservedCount(destination), Is.EqualTo(survivors.Count), "Late teammates reserve before filler bots");
                Assert.False(coordinator.IsAdmissionClosed(destination));
                PlayerRig cancelled = null;
                foreach (var actor in actors)
                    if (director.SessionOf(actor).Floor == source && !survivors.Contains(actor)) { cancelled = actor; break; }
                Assert.NotNull(cancelled);
                Assert.True(director.TryQueueDescent(cancelled, source, out _));
                Assert.False(driver.Tick(102.5f, out _));
                Assert.That(coordinator.ReservedCount(destination), Is.EqualTo(survivors.Count + 1));
                Assert.True(director.RemovePlayer(cancelled.PlayerId, "Disconnected during descent"));
                actors.Remove(cancelled); Object.DestroyImmediate(cancelled.gameObject);
                Assert.That(coordinator.ReservedCount(destination), Is.EqualTo(survivors.Count), "Disconnect releases only the pending destination seat");
                Assert.That(coordinator.ReservedCount(source), Is.EqualTo(capacity), "Started source roster stays closed");
                ((float[])typeof(RunDirector).GetField("_floorStartedAt", Private).GetValue(director))[source] = director.Elapsed - 120;
                ready = false; Assert.False(driver.Tick(103, out _));
                Assert.That(spawnCalls, Is.EqualTo(callsBefore), "No bots or arrival before destination readiness");
                Assert.False(director.TryGetRoyaleFloorClock(destination, out _, out _));
                ready = true;
                if (source == 1)
                {
                    Assert.False(driver.Tick(104, out _), "Transient bot failure retains reservations and clock");
                    Assert.That(coordinator.ReservedCount(destination), Is.EqualTo(survivors.Count + 2));
                    Assert.False(director.TryGetRoyaleFloorClock(destination, out _, out _));
                    foreach (var survivor in survivors) Assert.True(survivor.IsInFloorTransit);
                }
                Assert.True(driver.Tick(105, out var reason), reason);
                Assert.That(coordinator.ReservedCount(destination), Is.EqualTo(capacity));
                Assert.True(director.TryGetRoyaleFloorClock(destination, out float elapsed, out float remaining));
                Assert.Less(elapsed, 1); Assert.Greater(remaining, 119);
                var parties = new Dictionary<int, int>(); int count = 0;
                foreach (var session in director.Players)
                {
                    if (session.Floor != destination) continue;
                    count++; Assert.False(session.Rig.IsInFloorTransit);
                    parties[session.PartyId] = parties.TryGetValue(session.PartyId, out int n) ? n + 1 : 1;
                }
                Assert.That(count, Is.EqualTo(capacity));
                foreach (int members in parties.Values) Assert.That(members, Is.EqualTo((int)director.RoyaleMode.Value));
                foreach (var survivor in survivors)
                {
                    Assert.That(director.SessionOf(survivor).Floor, Is.EqualTo(destination));
                    Assert.That(LoadoutSnapshot.Capture(survivor.Inventory, survivor.Equipment).ToJson(), Is.EqualTo(items[survivor.PlayerId]));
                    Assert.That(CaptureAmmo(survivor), Is.EqualTo(ammo[survivor.PlayerId]));
                }
                int stableCount = director.Players.Count;
                Assert.False(driver.Tick(106, out _)); Assert.That(director.Players.Count, Is.EqualTo(stableCount));
                Assert.That(coordinator.EncounterFor(destination).Groups.Count, Is.EqualTo(destination == 3 ? 0 : 3));
                var encounter = coordinator.EncounterFor(destination);
                if (destination == 3)
                {
                    var exit = encounter.FinalExit; Assert.NotNull(exit); Assert.That(exit.Floor, Is.EqualTo(3));
                    Assert.True(encounter.Swarm.ContainsThroughEnd(exit.transform.position.x, exit.transform.position.z, .75));
                    encounter.Tick(); Assert.True(exit.gameObject.activeInHierarchy);
                    Assert.That(Emberdelve.Interaction.ExtractionPoint.FindForFloor(3), Is.SameAs(exit));
                    Assert.That(exit.HoldDuration, Is.EqualTo(director.Config.extractionHoldDuration));
                    Assert.True(exit.GetComponent<Collider>().isTrigger);
                }
                else Assert.Null(encounter.FinalExit);
                ready = false;
            }
        }

        private static void VerifyBotExtraction(RunDirector director, List<PlayerRig> actors, bool host)
        {
            PlayerRig bot = null;
            foreach (var actor in actors)
                if (director.SessionOf(actor).IsBot && director.SessionOf(actor).Floor == 3) { bot = actor; break; }
            Assert.NotNull(bot);
            var exit = director.FloorCoordinator.EncounterFor(3).FinalExit;
            foreach (var actor in actors)
                if (actor != bot) actor.Movement.TeleportTo(exit.transform.position + Vector3.right * 100);
            var definition = bot.Equipment.MainHandInstance(0).Definition;
            Assert.True(bot.Inventory.Catalog.TryMintCommon(ItemIdentityIssuer.CreateLocalTrusted(), definition,
                out var instance, out var reason), reason);
            Assert.True(bot.Inventory.TryAddInstance(instance, out reason), reason);
            string kit = LoadoutSnapshot.Capture(bot.Inventory, bot.Equipment).ToJson(), ammo = CaptureAmmo(bot);
            bot.Movement.TeleportTo(exit.transform.position - Vector3.up * 1.1f); Physics.SyncTransforms();
            Assert.True(exit.CanInteract(bot, out reason), reason);
            var brain = bot.GetComponent<RoyaleBotController>();
            NetRole.Set(NetRoleKind.Client); brain.Step(.02f);
            Assert.False(brain.HoldingExtraction, "Client cannot advance bot outcome");
            NetRole.Set(host ? NetRoleKind.Host : NetRoleKind.Offline);
            brain.Step(.02f); Assert.True(brain.HoldingExtraction);
            for (int i = 0; i < 50; i++) brain.Step(.02f);
            Assert.False(bot.IsExtracted, "Cannot skip the configured hold duration");
            bot.Health.ApplyDamage(new DamageInfo(1, bot.transform.position, Vector3.forward,
                bot.transform.position, -1, EntityKind.Enemy, DamageType.Physical, false, false, 0));
            Assert.False(brain.HoldingExtraction, "Damage cancels even if health is restored between ticks");
            for (int i = 0; i < 110; i++) brain.Step(.02f);
            Assert.False(bot.IsExtracted, "Damaged attempt cannot retain its earlier progress");
            bot.Movement.TeleportTo(bot.transform.position + Vector3.right * .2f); Physics.SyncTransforms();
            brain.Step(.02f); Assert.False(brain.HoldingExtraction, "Movement cancels using normal hold tolerance");
            for (int i = 0; i < 110; i++) brain.Step(.02f);
            Assert.False(bot.IsExtracted, "Moved attempt starts from zero");
            for (int i = 0; i < 60; i++) brain.Step(.02f);
            Assert.True(bot.IsExtracted);
            Assert.That(director.SessionOf(bot).State, Is.EqualTo(RunState.Extracted));
            Assert.True(director.SessionOf(bot).HasResult);
            Assert.That(LoadoutSnapshot.Capture(bot.Inventory, bot.Equipment).ToJson(), Is.EqualTo(kit));
            Assert.That(CaptureAmmo(bot), Is.EqualTo(ammo));
            Assert.True(bot.Inventory.HoldsInstanceUid(instance.Uid));
            var result = director.SessionOf(bot).Result;
            for (int i = 0; i < 200; i++) brain.Step(.02f);
            Assert.That(director.SessionOf(bot).Result, Is.EqualTo(result));
            Assert.False(brain.HoldingExtraction);
            NetRole.ResetToOffline();
        }

        private static string CaptureAmmo(PlayerRig rig) => JsonUtility.ToJson(typeof(ClassLoadout)
            .GetMethod("CapturePreparedAmmo", Private).Invoke(rig.Loadout,
                new object[] { LoadoutSnapshot.Capture(rig.Inventory, rig.Equipment) }));
    }
}
