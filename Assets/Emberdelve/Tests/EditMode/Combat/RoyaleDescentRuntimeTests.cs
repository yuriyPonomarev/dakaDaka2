using System.Collections.Generic;
using System.Reflection;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Flow;
using Emberdelve.Interaction;
using Emberdelve.Items;
using Emberdelve.Level;
using Emberdelve.Movement;
using Emberdelve.Net.Rules;
using Emberdelve.Player;
using Emberdelve.Rules;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Emberdelve.Combat.Tests
{
    public sealed class RoyaleDescentRuntimeTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private GameObject _actor, _host;
        private PlayerRig _rig;
        private RunDirector _director;
        private PlayerSession _session;
        private NetRoleKind _priorRole;
        private readonly List<GameObject> _portals = new List<GameObject>();

        [SetUp] public void Setup()
        {
            _priorRole = NetRole.Current; NetRole.ResetToOffline();
            _actor = new GameObject("DescendingPlayer"); _host = new GameObject("FloorAuthority");
            _actor.AddComponent<Inventory>(); _actor.AddComponent<Equipment>(); _actor.AddComponent<ClassLoadout>();
            _rig = _actor.AddComponent<PlayerRig>();
            typeof(PlayerMovement).GetMethod("Awake", Private).Invoke(_actor.GetComponent<PlayerMovement>(), null);
            var character = AssetDatabase.LoadAssetAtPath<CharacterClassData>("Assets/Emberdelve/Data/Class_Archer.asset");
            _rig.ApplyClass(character, AssetDatabase.LoadAssetAtPath<GameConfig>("Assets/Emberdelve/Data/EmberdelveGameConfig.asset"));
            _rig.AssignParty(0, true);
            _director = _host.AddComponent<RunDirector>();
            if (_director.Session == null) typeof(RunDirector).GetMethod("Awake", Private).Invoke(_director, null);
            Assert.True(_director.ConfigureRoyale(RoyalePartyMode.Solo));
            _session = new PlayerSession { Rig = _rig, PlayerId = _rig.PlayerId, Inventory = _rig.Inventory, Class = character, IsLocal = true };
            ((List<PlayerSession>)typeof(RunDirector).GetField("_sessions", Private).GetValue(_director)).Add(_session);
            ((Dictionary<int, PlayerSession>)typeof(RunDirector).GetField("_byPlayerId", Private).GetValue(_director)).Add(_rig.PlayerId, _session);
            typeof(RunDirector).GetField("_state", Private).SetValue(_director, RunState.Running);
            typeof(RunDirector).GetField("_startedAt", Private).SetValue(_director, Time.unscaledTime);
            Assert.True(_director.TryStartRoyaleFloor(1, 120, out _));
            typeof(RunDirector).GetField("_startedAt", Private).SetValue(_director, Time.unscaledTime - 90f);
        }

        [TearDown] public void Cleanup()
        {
            foreach (var go in _portals) Object.DestroyImmediate(go);
            _portals.Clear(); Object.DestroyImmediate(_host); Object.DestroyImmediate(_actor);
            NetRole.Set(_priorRole);
        }

        private DescentPortalGroup Portal(int floor, Vector3 position, double appearsAt = 60)
        {
            var go = new GameObject("DescentPortal"); _portals.Add(go); go.transform.position = position;
            var portal = go.AddComponent<DescentPortalGroup>();
            Assert.True(portal.Configure(_director, new DescentPortalSchedule(120, RoyalePartyMode.Solo,
                new DescentPortalWave(appearsAt, 1)), floor, 0));
            return portal;
        }

        [Test] public void TwoDescentsKeepExactRigLoadoutAmmoAndOnlyFloorThreeAllowsExtraction()
        {
            var loadout = LoadoutSnapshot.Capture(_rig.Inventory, _rig.Equipment);
            string items = loadout.ToJson(), ammo = JsonUtility.ToJson(_rig.Loadout.CapturePreparedAmmo(loadout));
            float health = _rig.CurrentHealth;
            var oldGate = new GameObject("OldFloorExit"); _portals.Add(oldGate);
            var legacy = oldGate.AddComponent<ExtractionPoint>();
            typeof(ExtractionPoint).GetField("_sink", Private).SetValue(legacy, _director);
            var finalGate = new GameObject("FinalFloorExit"); _portals.Add(finalGate);
            var final = finalGate.AddComponent<ExtractionPoint>(); final.BindFinalFloor(_director);
            Assert.That(ExtractionPoint.FindForFloor(0), Is.SameAs(legacy));
            Assert.That(ExtractionPoint.FindForFloor(3), Is.SameAs(final));
            for (int floor = 1; floor <= 2; floor++)
            {
                Assert.False(_director.ExtractionAllowed(_rig, out _));
                Assert.That(_director.ExtractionFloorFor(_rig), Is.EqualTo(-1));
                finalGate.transform.position = oldGate.transform.position = _rig.InteractionOrigin;
                Assert.False(legacy.CanInteract(_rig, out _)); Assert.False(final.CanInteract(_rig, out _));
                var portal = Portal(floor, _rig.InteractionOrigin);
                Assert.True(portal.TryEnter(_rig, out var reason), reason);
                Assert.That(portal.RemainingSlots, Is.Zero); Assert.False(portal.TryEnter(_rig, out _));
                Assert.True(_rig.IsInFloorTransit); Assert.False(_session.HasResult);
                Assert.That(_director.ExtractionFloorFor(_rig), Is.EqualTo(-1));
                Assert.True(_director.TryStartRoyaleFloor(floor + 1, 120, out _));
                Assert.True(_director.TryArriveAtNextFloor(_rig, floor + 1, new Vector3(floor * 100, 0, 0), Quaternion.identity, out reason), reason);
                typeof(RunDirector).GetField("_startedAt", Private).SetValue(_director, Time.unscaledTime - (floor + 1) * 90f);
                Assert.That(_session.Rig, Is.SameAs(_rig)); Assert.That(_session.Floor, Is.EqualTo(floor + 1));
                var carried = LoadoutSnapshot.Capture(_rig.Inventory, _rig.Equipment);
                Assert.That(carried.ToJson(), Is.EqualTo(items));
                Assert.That(JsonUtility.ToJson(_rig.Loadout.CapturePreparedAmmo(carried)), Is.EqualTo(ammo));
                Assert.That(_rig.CurrentHealth, Is.EqualTo(health)); Assert.False(_session.HasResult);
            }
            Assert.True(_director.ExtractionAllowed(_rig, out var why), why);
            Assert.False(_director.TryQueueDescent(_rig, 3, out _));
            finalGate.transform.position = oldGate.transform.position = _rig.InteractionOrigin;
            Assert.That(_director.ExtractionFloorFor(_rig), Is.EqualTo(3));
            Assert.False(legacy.CanInteract(_rig, out _)); Assert.True(final.CanInteract(_rig, out why), why);
            var duplicate = new GameObject("DuplicateFinalExit"); _portals.Add(duplicate);
            duplicate.AddComponent<ExtractionPoint>().BindFinalFloor(_director);
            Assert.Null(ExtractionPoint.FindForFloor(3));
            duplicate.SetActive(false); Assert.That(ExtractionPoint.FindForFloor(3), Is.SameAs(final));
            final.enabled = false; Assert.Null(ExtractionPoint.FindForFloor(3)); Assert.False(final.CanInteract(_rig, out _));
            final.enabled = true;
            ((float[])typeof(RunDirector).GetField("_floorStartedAt", Private).GetValue(_director))[3] = _director.Elapsed - 121;
            Assert.That(_director.ExtractionFloorFor(_rig), Is.EqualTo(-1)); Assert.False(final.CanInteract(_rig, out _));
        }

        [Test] public void PendingTransferRejectsMovementSkillsDamageAndInvalidArrival()
        {
            Assert.True(Portal(1, _rig.InteractionOrigin).TryEnter(_rig, out var why), why);
            var before = _rig.transform.position; float health = _rig.CurrentHealth;
            var frame = MoveInputFrame.Create(1, 1, 1, 0, 0, true, true, true);
            _rig.Movement.Step(frame, 0.1f);
            Assert.That(_rig.transform.position, Is.EqualTo(before)); Assert.False(_rig.IsInPlay);
            Assert.False(_actor.GetComponent<Emberdelve.Skills.SkillCaster>().CanCastSlot(0, out _));
            _rig.ApplyDamage(new DamageInfo(10, before, Vector3.forward, before, -1, EntityKind.Enemy, DamageType.Physical, false, false, 0));
            Assert.That(_rig.CurrentHealth, Is.EqualTo(health));
            Assert.False(_director.IsInteractionActorAdmitted(_rig, out _));
            Assert.False(_director.TryArriveAtNextFloor(_rig, 3, before, Quaternion.identity, out _));
            Assert.False(_director.TryArriveAtNextFloor(_rig, 2, before, Quaternion.identity, out _));
            Assert.True(_director.TryStartRoyaleFloor(2, 120, out _));
            Assert.False(_director.TryArriveAtNextFloor(_rig, 2, new Vector3(float.NaN, 0, 0), Quaternion.identity, out _));
            Assert.True(_rig.IsInFloorTransit); Assert.That(_session.Floor, Is.EqualTo(1));
        }

        [Test] public void SourceTimeoutPreservesTransitAndDestinationHasIndependentDeadline()
        {
            Assert.True(Portal(1, _rig.InteractionOrigin).TryEnter(_rig, out _));
            float health = _rig.CurrentHealth;
            typeof(RunDirector).GetField("_startedAt", Private).SetValue(_director, Time.unscaledTime - 130f);
            typeof(RunDirector).GetMethod("TickRoyaleFloors", Private).Invoke(_director, null);
            Assert.That(_rig.CurrentHealth, Is.EqualTo(health));
            Assert.True(_rig.IsInFloorTransit);
            Assert.True(_director.TryStartRoyaleFloor(2, 120, out _));
            Assert.False(_director.TryStartRoyaleFloor(2, 300, out _));
            Assert.True(_director.TryArriveAtNextFloor(_rig, 2, Vector3.zero, Quaternion.identity, out _));
            typeof(RunDirector).GetMethod("TickRoyaleFloors", Private).Invoke(_director, null);
            Assert.That(_rig.CurrentHealth, Is.EqualTo(health));
            Assert.True(_director.TryGetRoyaleFloorClock(2, out _, out float remaining));
            Assert.That(remaining, Is.GreaterThan(119f));
            typeof(RunDirector).GetField("_startedAt", Private).SetValue(_director, Time.unscaledTime - 251f);
            Assert.False(_director.IsInteractionActorAdmitted(_rig, out _));
            typeof(RunDirector).GetMethod("TickRoyaleFloors", Private).Invoke(_director, null);
            Assert.That(_rig.CurrentHealth, Is.Zero);
        }

        [Test] public void FloorClockRejectsSkippedFloorAndClientStart()
        {
            Assert.False(_director.TryStartRoyaleFloor(3, 120, out _));
            Assert.False(_director.TryStartRoyaleFloor(2, 0, out _));
            NetRole.Set(NetRoleKind.Client);
            Assert.False(_director.TryStartRoyaleFloor(2, 120, out _));
        }

        [TestCase(11)]
        [TestCase(2043687868)]
        public void PreparedMapOwnsArrivalPositionAndPreservesCarriedItems(int seed)
        {
            var catalog = AssetDatabase.LoadAssetAtPath<DungeonModuleCatalog>("Assets/Emberdelve/Resources/Emberdelve/DungeonModuleCatalog.asset");
            var layout = AssetDatabase.LoadAssetAtPath<DungeonLayoutData>("Assets/Emberdelve/Data/DungeonLayout_AshenHalls.asset");
            Assert.True(DungeonModulePlanner.TryCreate(seed, catalog.Modules, out var plan, out var reason), reason);
            var coordinator = _director.FloorCoordinator;
            DungeonModuleSceneAssembler stage = null;
            try
            {
                Assert.True(coordinator.TryPrepare(2, layout, catalog, plan, new Vector3(1000, 0, 1000), out reason), reason);
                stage = Object.FindFirstObjectByType<DungeonModuleSceneAssembler>(FindObjectsInactive.Include);
                Assert.NotNull(stage); Assert.False(stage.gameObject.activeSelf);
                Assert.That(stage.GetComponentInChildren<ChestSpawner>(true).Count, Is.GreaterThan(0));
                var content = stage.GetComponentInChildren<FloorContentSpawner>(true);
                Assert.NotNull(content);
                Assert.That(content.Enemies.Count, Is.EqualTo(stage.RuntimeLayout.enemySpawns.Count));
                Assert.That(content.Loot.Count, Is.EqualTo(stage.RuntimeLayout.lootSpawns.Count));
                Assert.That(content.Enemies.Count, Is.GreaterThan(0));
                Assert.That(content.Loot.Count, Is.GreaterThan(0));
                for (int i = 0; i < content.Enemies.Count; i++)
                {
                    var enemy = content.Enemies[i];
                    Assert.False(enemy.gameObject.activeInHierarchy);
                    Assert.That(enemy.Data, Is.SameAs(stage.RuntimeLayout.enemySpawns[i].enemy));
                    Assert.That(typeof(Emberdelve.AI.EnemyAgent).GetField("navGrid", Private).GetValue(enemy), Is.SameAs(stage.RuntimeNavGrid));
                    Assert.That(typeof(Emberdelve.AI.EnemyAgent).GetField("lootHost", Private).GetValue(enemy), Is.SameAs(_director));
                    Assert.NotNull(enemy.GetComponentInChildren<Animator>(true));
                    Assert.That(enemy.transform.position, Is.EqualTo(stage.RuntimeLayout.CellCenterWorld(stage.RuntimeLayout.enemySpawns[i].cell)));
                }
                for (int i = 0; i < content.Loot.Count; i++)
                {
                    Assert.False(content.Loot[i].gameObject.activeInHierarchy);
                    Assert.That(content.Loot[i].Item, Is.SameAs(stage.RuntimeLayout.lootSpawns[i].item));
                    Assert.That(content.Loot[i].HomePosition, Is.EqualTo(stage.RuntimeLayout.CellCenterWorld(stage.RuntimeLayout.lootSpawns[i].cell) + Vector3.up * .45f));
                }
                Assert.IsNull(FloorContentSpawner.CreateForFloor(stage.RuntimeLayout, stage.RuntimeNavGrid,
                    _director.Config, _director, stage.transform, 2, out _), "Duplicate content must not mint another floor population");
                Assert.False(coordinator.TryPrepare(3, layout, catalog, plan, new Vector3(1000, 0, 1000), out _));
                string carried = LoadoutSnapshot.Capture(_rig.Inventory, _rig.Equipment).ToJson();
                Assert.False(coordinator.TryReserveIncoming(_rig, 2, out _));
                Assert.True(Portal(1, _rig.InteractionOrigin).TryEnter(_rig, out reason), reason);
                Assert.True(coordinator.TryReserveIncoming(_rig, 2, out reason), reason);
                Assert.True(coordinator.TryReserveIncoming(_rig, 2, out reason), "Reservation retry must be idempotent");
                Assert.False(coordinator.TryDiscardUnused(2), "Reserved survivors must retain their destination");
                Assert.False(coordinator.TryArrive(_rig, 2, out _));
                Assert.True(coordinator.TryActivate(2, out reason), reason);
                var firstEnemy = content.Enemies[0];
                Assert.True(firstEnemy.IsRuntimeInitialized);
                Assert.That(firstEnemy.CurrentHealth, Is.EqualTo(firstEnemy.Data.maxHealth));
                Assert.True(firstEnemy.IsDormant);
                firstEnemy.ApplyDamage(new DamageInfo(1, firstEnemy.transform.position, Vector3.forward,
                    firstEnemy.transform.position, -1, EntityKind.Neutral, DamageType.Physical, false, false, 0));
                float damagedHealth = firstEnemy.CurrentHealth;
                Assert.True(coordinator.TryActivate(2, out _));
                Assert.That(firstEnemy.CurrentHealth, Is.EqualTo(damagedHealth), "Activation retry must not heal monsters");
                var drop = (LootPickup)typeof(RunDirector).GetMethod("BuildPickupBody", Private).Invoke(_director,
                    new object[] { content.Loot[0].Item, firstEnemy.transform.position, true });
                Assert.True(drop.transform.IsChildOf(stage.transform), "Runtime loot must retire with its floor");
                Assert.False(coordinator.TryArrive(_rig, 2, out _));
                typeof(RunDirector).GetField("_local", Private).SetValue(_director, _session);
                typeof(RunDirector).GetField("config", Private).SetValue(_director,
                    AssetDatabase.LoadAssetAtPath<GameConfig>("Assets/Emberdelve/Data/EmberdelveGameConfig.asset"));
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Emberdelve/Prefabs/Net/Emberdelve_NetPlayer.prefab");
                Assert.False(coordinator.TrySpawnBot(2, 1, _rig.gameObject, _session.Class, out _, out _), "Never clone a survivor in transit");
                Assert.False(coordinator.TrySpawnBot(2, 0, prefab, _session.Class, out _, out _), "Reserved solo seat belongs to the survivor");
                Assert.True(coordinator.TrySpawnBot(2, 1, prefab, _session.Class, out var bot, out reason), reason);
                int retiredBotId = bot.PlayerId;
                Assert.True(_director.RemovePlayer(bot.PlayerId, "Simulated network spawn failure"));
                Object.DestroyImmediate(bot.gameObject);
                Assert.True(coordinator.TrySpawnBot(2, 2, prefab, _session.Class, out bot, out reason), reason);
                Assert.That(bot.PlayerId, Is.Not.EqualTo(retiredBotId));
                _portals.Add(bot.gameObject);
                typeof(PlayerMovement).GetMethod("Awake", Private).Invoke(bot.Movement, null);
                Assert.True(_director.SessionOf(bot).IsBot);
                Assert.That(_director.SessionOf(bot).Floor, Is.EqualTo(2));
                Assert.True(bot.IsInFloorTransit);
                Assert.False(bot.IsLocallyControlled);
                Assert.True(bot.Movement.ExternalDriven);
                Assert.True(CombatSenders.Authorize(bot.CombatSender, bot.CombatId, out _));
                Assert.False(CombatSenders.Authorize(bot.CombatSender, _rig.CombatId, out _));
                Assert.False(coordinator.TrySpawnBot(2, 2, prefab, _session.Class, out _, out _));
                string botKit = LoadoutSnapshot.Capture(bot.Inventory, bot.Equipment).ToJson();
                Assert.True(_director.TryStartRoyaleFloor(2, 120, out reason), reason);
                Assert.False(coordinator.TrySpawnBot(2, 2, prefab, _session.Class, out _, out _), "No refill after clock start");
                Assert.True(coordinator.TryArrive(bot, 2, out reason), reason);
                Assert.False(bot.IsInFloorTransit);
                Assert.That(bot.transform.position, Is.EqualTo(stage.RuntimeLayout.PlayerSpawnWorld(1) + Vector3.up * .15f));
                Assert.That(LoadoutSnapshot.Capture(bot.Inventory, bot.Equipment).ToJson(), Is.EqualTo(botKit));
                Assert.False(coordinator.TryArrive(bot, 2, out _));
                var brain = bot.GetComponent<Emberdelve.AI.RoyaleBotController>();
                Assert.NotNull(brain); Assert.That(brain.Floor, Is.EqualTo(2));
                Vector3 botStart = bot.transform.position;
                for (int step = 0; step < 60; step++) { brain.Step(.02f); Physics.SyncTransforms(); }
                Assert.That(brain.TargetPlayerId, Is.EqualTo(-1), "Source-floor survivor cannot be a destination target");
                Assert.That(Vector3.Distance(botStart, bot.transform.position), Is.GreaterThan(.1f), "Bot must move through real PlayerMovement, not merely produce a route");
                Assert.True(bot.TryBeginFloorTransit(out _));
                Vector3 waitingPosition = bot.transform.position;
                brain.Step(.02f);
                Assert.That(bot.transform.position, Is.EqualTo(waitingPosition));
                Assert.That(brain.TargetPlayerId, Is.EqualTo(-1));
                Assert.False(coordinator.TryReserveIncoming(_rig, 2, out _), "Roster is closed when the clock starts");
                var obstruction = GameObject.CreatePrimitive(PrimitiveType.Cube);
                _portals.Add(obstruction); // Simulates another actor, outside the map-owned hierarchy.
                obstruction.transform.position = stage.RuntimeLayout.PlayerSpawnWorld(0) + Vector3.up;
                Physics.SyncTransforms();
                Assert.False(coordinator.TryArrive(_rig, 2, out _));
                Assert.True(_rig.IsInFloorTransit);
                Object.DestroyImmediate(obstruction);
                Physics.SyncTransforms();
                Assert.True(coordinator.TryArrive(_rig, 2, out reason), reason);
                Assert.That(_rig.transform.position, Is.EqualTo(stage.RuntimeLayout.PlayerSpawnWorld(0) + Vector3.up * .15f));
                Assert.That(LoadoutSnapshot.Capture(_rig.Inventory, _rig.Equipment).ToJson(), Is.EqualTo(carried));
                Assert.That(_session.Floor, Is.EqualTo(2)); Assert.False(_session.HasResult);
                Assert.False(coordinator.TryArrive(_rig, 2, out _));
                Assert.False(coordinator.TryDiscardUnused(2));
            }
            finally { if (stage != null) stage.DiscardStagedFloor(); }
        }

        [Test] public void ClosedDistantAndWrongFloorPortalsDoNotConsumeSlots()
        {
            foreach (var portal in new[] { Portal(1, _rig.InteractionOrigin, 110), Portal(1, new Vector3(100, 0, 0)), Portal(2, _rig.InteractionOrigin) })
            {
                Assert.False(portal.TryEnter(_rig, out _)); Assert.That(portal.RemainingSlots, Is.EqualTo(1));
                Assert.False(_rig.IsInFloorTransit);
            }
        }

        [Test] public void TransitFreezesAuthorityReloadAndRejectsSenderCommandsWithoutIdentityStrike()
        {
            var authority = _host.AddComponent<LocalCombatAuthority>();
            var seed = _rig.CaptureKitSeed(KitSeedReason.ClassApply);
            Assert.That(seed.Pools.Length, Is.GreaterThan(0));
            for (int i = 0; i < seed.Pools.Length; i++) seed.Pools[i].Reserve = 2;
            Assert.True(authority.SeedKit(_rig.CombatSender, _rig.CombatId, seed, out var why), why);
            Assert.True(authority.TryForfeitRound(_rig.CombatSender, _rig.CombatId));
            Assert.True(authority.TryStartReload(_rig.CombatSender, _rig.CombatId, out why), why);
            Assert.True(authority.TryGetKit(_rig.CombatId, out var before));
            Assert.True(Portal(1, _rig.InteractionOrigin).TryEnter(_rig, out why), why);
            Assert.False(authority.TryForfeitRound(_rig.CombatSender, _rig.CombatId));
            Assert.False(authority.TryStartReload(_rig.CombatSender, _rig.CombatId, out _));
            typeof(LocalCombatAuthority).GetMethod("AdvanceKits", Private).Invoke(authority, new object[] { 100f });
            Assert.True(authority.TryGetKit(_rig.CombatId, out var after));
            Assert.That(after.Magazine, Is.EqualTo(before.Magazine)); Assert.That(after.Reserve, Is.EqualTo(before.Reserve));
            Assert.That(after.ReloadRemaining, Is.EqualTo(before.ReloadRemaining));
            Assert.That(authority.SenderRefusalCount, Is.Zero);
        }

        [Test] public void ClientCannotConsumePortalOrStartAuthoritativeTransfer()
        {
            var portal = Portal(1, _rig.InteractionOrigin);
            NetRole.Set(NetRoleKind.Client);
            Assert.False(portal.TryEnter(_rig, out _));
            Assert.False(_director.TryQueueDescent(_rig, 1, out _));
            Assert.False(_rig.TryBeginFloorTransit(out _));
            Assert.That(portal.RemainingSlots, Is.EqualTo(1)); Assert.False(_rig.IsInFloorTransit);
        }

        [Test] public void TransitRemovesSourceCollisionAndRestoresOriginalVisibilityFlags()
        {
            var visible = GameObject.CreatePrimitive(PrimitiveType.Cube); visible.transform.SetParent(_actor.transform, false);
            var hidden = GameObject.CreatePrimitive(PrimitiveType.Cube); hidden.transform.SetParent(_actor.transform, false);
            hidden.GetComponent<Renderer>().forceRenderingOff = true;
            hidden.GetComponent<Collider>().enabled = false;
            var controller = _actor.GetComponent<CharacterController>();
            Assert.True(controller.enabled);
            Assert.True(_rig.TryBeginFloorTransit(out var why), why);
            Assert.False(controller.enabled); Assert.False(visible.GetComponent<Collider>().enabled);
            Assert.True(visible.GetComponent<Renderer>().forceRenderingOff);
            Assert.True(_rig.TryEndFloorTransit(new Vector3(200, 0, 0), Quaternion.identity, out why), why);
            Assert.True(controller.enabled); Assert.True(visible.GetComponent<Collider>().enabled);
            Assert.False(visible.GetComponent<Renderer>().forceRenderingOff);
            Assert.True(hidden.GetComponent<Renderer>().forceRenderingOff);
            Assert.False(hidden.GetComponent<Collider>().enabled);
        }
    }
}
