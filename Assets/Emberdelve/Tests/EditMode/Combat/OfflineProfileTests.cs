using System;
using System.IO;
using System.Linq;
using Emberdelve.Data;
using Emberdelve.Core;
using Emberdelve.Items;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Emberdelve.Combat.Tests
{
    public sealed class OfflineProfileTests
    {
        private string _directory, _path;
        private ItemCatalog _catalog;
        private CharacterClassData _character;
        private OfflineProfile _profile;
        [SetUp] public void Setup()
        {
            _catalog=AssetDatabase.LoadAssetAtPath<ItemCatalog>("Assets/Emberdelve/Data/EmberdelveItemCatalog.asset");
            _character=AssetDatabase.LoadAssetAtPath<CharacterClassData>("Assets/Emberdelve/Data/Class_Warrior.asset");
            _directory=Path.Combine(Path.GetTempPath(),"EmberdelveProfileTests",Guid.NewGuid().ToString("N"));
            _path=Path.Combine(_directory,"warrior.json");
            Assert.That(OfflineProfile.TryOpen(_path,_catalog,_character,out _profile,out string why),Is.True,why);
        }
        [TearDown] public void Cleanup()
        {
            _profile?.Dispose();
            // Only this fixture's generated directory, never Application.persistentDataPath.
            if(Directory.Exists(_directory))Directory.Delete(_directory,true);
        }
        [Test] public void PreparationPhysicalBeltMovesSwapAndPersistWithoutDuplicatingItems()
        {
            int CountUnits() => _profile.Snapshot.Ready.bag.entries.Sum(e => e.item.quantity);
            int before = CountUnits();
            int potion = Array.FindIndex(_profile.Snapshot.Ready.bag.entries, e => e.item.itemId == "blood_draught");
            Assert.That(potion, Is.GreaterThanOrEqualTo(0));
            var cell = QuickBeltRules.Cell(5, 10);
            Assert.True(_profile.Move(PreparationArea.Bag, potion, PreparationArea.Bag, EquipmentSlot.None, cell, _profile.Revision, out var reason), reason);
            int bandage = Array.FindIndex(_profile.Snapshot.Ready.bag.entries, e => e.item.itemId == "linen_bandage");
            Assert.True(_profile.Move(PreparationArea.Bag, bandage, PreparationArea.Bag, EquipmentSlot.None, cell, _profile.Revision, out reason), reason);
            Assert.That(_profile.Snapshot.Ready.bag.entries.Single(e => e.x == cell.x && e.y == cell.y).item.itemId, Is.EqualTo("linen_bandage"));
            Assert.That(CountUnits(), Is.EqualTo(before));
            string saved = _profile.Snapshot.Ready.ToJson();
            Assert.True(OfflineProfile.TryOpen(_path, _catalog, _character, out var reopened, out reason), reason);
            using (reopened) Assert.That(reopened.Snapshot.Ready.ToJson(), Is.EqualTo(saved));
            int belt = Array.FindIndex(_profile.Snapshot.Ready.bag.entries, e => e.x == cell.x && e.y == cell.y);
            Assert.True(_profile.Move(PreparationArea.Bag, belt, PreparationArea.Bag, EquipmentSlot.None, null, _profile.Revision, out reason), reason);
            Assert.False(_profile.Snapshot.Ready.bag.entries.Any(e => e.x >= 10));
            Assert.That(CountUnits(), Is.EqualTo(before));
        }
        [TestCase("Warrior")] [TestCase("Archer")]
        public void RoyaleBaseKitParksOwnedGearAndCancellationCannotFarmSupplies(string name)
        {
            _profile.Dispose();
            _character = AssetDatabase.LoadAssetAtPath<CharacterClassData>("Assets/Emberdelve/Data/Class_" + name + ".asset");
            string path = Path.Combine(_directory, "royale-" + name + ".json");
            Assert.True(OfflineProfile.TryOpen(path,_catalog,_character,out _profile,out var why),why);
            var original = _profile.Snapshot;
            var oldUids = original.Ready.equipment.entries.Select(e => e.item.uid).ToArray();
            Assert.True(_profile.BeginRoyaleRaid(out var raid,out var kit,out var ammo,out why),why);
            Assert.That(kit.belt, Does.Contain("i:blood_draught"), "issued healing must be accessible from a wheel");
            Assert.That(kit.belt, Does.Contain("i:linen_bandage"));
            var torch = kit.bag.entries.Single(e => e.item.itemId == "pitch_torch");
            Assert.That(kit.belt, Does.Contain("u:" + torch.item.uid), "torch binding must follow the newly issued UID");
            Assert.That(kit.belt.Where(b => !string.IsNullOrEmpty(b)).Distinct().Count(),
                Is.EqualTo(kit.belt.Count(b => !string.IsNullOrEmpty(b))));
            Assert.True(kit.ChecksumMatches());
            var parked = _profile.Snapshot;
            Assert.That(parked.Ready.bag.entries,Is.Empty); Assert.That(parked.Ready.equipment.entries,Is.Empty);
            foreach (string uid in oldUids) Assert.True(parked.Stash.entries.Any(e => e.item.uid == uid));
            Assert.False(kit.equipment.entries.Any(e => oldUids.Contains(e.item.uid)));
            Assert.That(parked.Gold,Is.EqualTo(original.Gold));
            if (_character.primaryWeapon.IsRanged)
            {
                Assert.That(ammo.loaded.Sum(e => e.magazine),Is.GreaterThan(0));
                Assert.That(ammo.CarriedCount(_character.primaryWeapon.weaponId),Is.EqualTo(_character.startingReserveAmmo));
            }
            Assert.True(_profile.FinishRaid(raid,kit,ammo,out why,true),why);
            Assert.That(_profile.Snapshot.Ready.bag.entries,Is.Empty); Assert.That(_profile.Snapshot.Ready.equipment.entries,Is.Empty);
            Assert.That(_profile.Snapshot.Ammunition.ToJson(),Is.EqualTo(parked.Ammunition.ToJson()));
            Assert.That(_profile.Snapshot.Stash.ToJson(),Is.EqualTo(parked.Stash.ToJson()));
            Assert.True(_profile.BeginRoyaleRaid(out raid,out kit,out ammo,out why),why);
            Assert.True(_profile.FinishRaid(raid,kit,ammo,out why),why);
            Assert.That(_profile.Snapshot.Ready.ToJson(),Is.EqualTo(kit.ToJson()));
            Assert.That(_profile.Snapshot.Stash.ToJson(),Is.EqualTo(parked.Stash.ToJson()));
            Assert.True(OfflineProfile.TryOpen(path,_catalog,_character,out var reopened,out why),why);
            using(reopened) Assert.That(reopened.Snapshot.Ready.ToJson(),Is.EqualTo(kit.ToJson()));
        }
        [Test] public void RoyaleDepartureWriteRefusalLeavesPreparationAndDiskUntouched()
        {
            var before = _profile.Snapshot; string disk = File.ReadAllText(_path);
            using(var lease = new FileStream(_path + ".lock",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None))
                Assert.False(_profile.BeginRoyaleRaid(out _,out _,out _,out _));
            Assert.That(_profile.Snapshot.Ready.ToJson(),Is.EqualTo(before.Ready.ToJson()));
            Assert.That(_profile.Snapshot.Stash.ToJson(),Is.EqualTo(before.Stash.ToJson()));
            Assert.That(_profile.Revision,Is.EqualTo(before.Revision)); Assert.That(File.ReadAllText(_path),Is.EqualTo(disk));
            Assert.True(_profile.BeginRoyaleRaid(out var raid,out _,out _,out var why),why);
            var stash = _profile.Snapshot.Stash.ToJson();
            _profile.Dispose();
            Assert.True(OfflineProfile.TryOpen(_path,_catalog,_character,out _profile,out why),why);
            Assert.That(_profile.Snapshot.ActiveRaid,Is.Empty); Assert.That(_profile.Snapshot.Stash.ToJson(),Is.EqualTo(stash));
            Assert.That(_profile.Snapshot.Ready.equipment.entries,Is.Empty);
        }
        [Test] public void ExtractedGearKeepsUidAndRollsAcrossDiskAndNextRaid()
        {
            var before=_profile.Snapshot;
            Assert.That(_profile.BeginRaid(out string raid,out var kit,out string why),Is.True,why);
            Assert.That(_profile.Snapshot.Ready.equipment.entries,Is.Empty);
            Assert.That(_profile.FinishRaid(raid,kit,out why),Is.True,why);
            Assert.That(_profile.FinishRaid(raid,kit,out _),Is.False,"extraction is exactly once");
            Assert.That(OfflineProfile.TryOpen(_path,_catalog,_character,out var reopened,out why),Is.True,why);
            using(reopened)
            {
                Assert.That(reopened.Snapshot.Ready.ToJson(),Is.EqualTo(before.Ready.ToJson()));
                Assert.That(reopened.BeginRaid(out string second,out var again,out why),Is.True,why);
                Assert.That(again.equipment.ToJson(),Is.EqualTo(kit.equipment.ToJson()));
                Assert.That(reopened.FinishRaid(second,null,out why),Is.True,why);
                Assert.That(reopened.Snapshot.Ready.equipment.entries,Is.Empty);
            }
        }
        [Test] public void StashAndGoldSurviveDeathAndAbandonedProcess()
        {
            Assert.That(_profile.StoreBag(_profile.Revision,out string why),Is.True,why);
            var saved=_profile.Snapshot;
            Assert.That(_profile.BeginRaid(out _,out _,out why),Is.True,why);
            _profile.Dispose(); // simulate process termination; durable in-flight marker remains
            Assert.That(OfflineProfile.TryOpen(_path,_catalog,_character,out var reopened,out why),Is.True,why);
            using(reopened)
            {
                Assert.That(reopened.Snapshot.Stash.ToJson(),Is.EqualTo(saved.Stash.ToJson()));
                Assert.That(reopened.Snapshot.Gold,Is.EqualTo(saved.Gold));
                Assert.That(reopened.ActiveRaid,Is.Empty);
                Assert.That(reopened.Snapshot.Ready.equipment.entries,Is.Empty);
            }
        }
        [Test] public void ConcurrentGameCannotAbandonLiveRaid()
        {
            Assert.That(_profile.BeginRaid(out string raid,out var kit,out string why),Is.True,why);
            Assert.That(OfflineProfile.TryOpen(_path,_catalog,_character,out _,out _),Is.False);
            Assert.That(_profile.FinishRaid(raid,kit,out why),Is.True,why);
        }
        [Test] public void TradingAndMovesAreAtomicAndRejectStaleSelection()
        {
            var before=_profile.Snapshot;
            var item=_profile.Stock.First(i=>OfflineProfile.BuyPrice(i)<=before.Gold);
            Assert.That(_profile.Buy(item.itemId,_profile.Revision,out string why),Is.True,why);
            Assert.That(_profile.Snapshot.Gold,Is.EqualTo(before.Gold-OfflineProfile.BuyPrice(item)));
            long stale=before.Revision;var snapshot=_profile.Snapshot;
            Assert.That(_profile.Sell(PreparationArea.Stash,0,stale,out _),Is.False);
            Assert.That(_profile.Move(PreparationArea.Stash,0,PreparationArea.Bag,EquipmentSlot.None,new Vector2Int(99,99),_profile.Revision,out _),Is.False);
            Assert.That(_profile.Snapshot.Stash.ToJson(),Is.EqualTo(snapshot.Stash.ToJson()));
            Assert.That(_profile.Snapshot.Gold,Is.EqualTo(snapshot.Gold));
            var sold=_profile.Snapshot.Stash.entries[0].item;
            Assert.That(_profile.Sell(PreparationArea.Stash,0,_profile.Revision,out why),Is.True,why);
            Assert.That(_profile.Snapshot.Gold,Is.LessThanOrEqualTo(before.Gold),"buy/sell cannot create gold");
        }
        [Test] public void DiskFailureAndConflictingWriterDoNotPublishMutations()
        {
            var before=_profile.Snapshot;
            using(var lease=new FileStream(_path+".lock",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None))
                Assert.That(_profile.StoreBag(_profile.Revision,out _),Is.False);
            Assert.That(_profile.Snapshot.Revision,Is.EqualTo(before.Revision));
            Assert.That(_profile.Snapshot.Ready.ToJson(),Is.EqualTo(before.Ready.ToJson()));
            Assert.That(OfflineProfile.TryOpen(_path,_catalog,_character,out var other,out string why),Is.True,why);
            using(other) Assert.That(other.StoreBag(other.Revision,out why),Is.True,why);
            Assert.That(_profile.StoreBag(_profile.Revision,out _),Is.False,"stale process cannot overwrite the new profile");
        }
        [Test] public void FreeBaseWeaponsCannotCreateGoldOrFillStashBeforeExtraction()
        {
            Assert.That(_profile.BeginRaid(out var raid,out _,out var why),Is.True,why);
            Assert.That(_profile.FinishRaid(raid,null,out why),Is.True,why);
            var gold=_profile.Snapshot.Gold;
            Assert.That(_profile.EquipBaseWeapons(_profile.Revision,out why),Is.True,why);
            var kit=_profile.Snapshot.Ready;
            Assert.That(kit.equipment.entries.Length,Is.GreaterThan(0));
            Assert.That(_profile.Move(PreparationArea.Equipment,0,PreparationArea.Stash,EquipmentSlot.None,null,_profile.Revision,out _),Is.False);
            Assert.That(OfflineProfile.TryOpen(_path,_catalog,_character,out var reopened,out why),Is.True,why);
            using(reopened) Assert.That(reopened.IsSupplied(kit.equipment.entries[0].item),Is.True);
            Assert.That(_profile.Sell(PreparationArea.Equipment,0,_profile.Revision,out why),Is.True,why);
            Assert.That(_profile.Snapshot.Gold,Is.EqualTo(gold));
            Assert.That(_profile.EquipBaseWeapons(_profile.Revision,out why),Is.True,why);
            Assert.That(_profile.BeginRaid(out raid,out kit,out why),Is.True,why);
            Assert.That(_profile.FinishRaid(raid,kit,out why),Is.True,why);
            Assert.That(_profile.Move(PreparationArea.Equipment,0,PreparationArea.Stash,EquipmentSlot.None,null,_profile.Revision,out why),Is.True,why);
        }
        [Test] public void OfflineClassApplicationUsesPreparedUidsAndOnlyTheSelectedBag()
        {
            var flags=System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic;
            var session=typeof(OfflineProfileSession);
            var reset=session.GetMethod("Reset",flags);
            var priorRole=Emberdelve.Core.NetRole.Current;
            var actor=new GameObject("PreparedOfflinePlayer");
            try
            {
                Emberdelve.Core.NetRole.ResetToOffline();
                reset.Invoke(null,null);
                session.GetField("<Current>k__BackingField",flags).SetValue(null,_profile);
                Assert.That(_profile.StoreBag(_profile.Revision,out var why),Is.True,why);
                var expected=_profile.Snapshot.Ready;
                Assert.That(OfflineProfileSession.Depart(out why),Is.True,why);
                actor.AddComponent<Inventory>(); actor.AddComponent<Equipment>();
                actor.AddComponent<Emberdelve.Player.ClassLoadout>();
                var rig=actor.AddComponent<Emberdelve.Player.PlayerRig>();
                var config=AssetDatabase.LoadAssetAtPath<GameConfig>("Assets/Emberdelve/Data/EmberdelveGameConfig.asset");
                typeof(Emberdelve.Movement.PlayerMovement).GetMethod("Awake",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).Invoke(actor.GetComponent<Emberdelve.Movement.PlayerMovement>(),null);
                rig.AssignParty(1,false);
                rig.ApplyClass(_character,config);
                Assert.That((bool)session.GetField("_applied",flags).GetValue(null),Is.False,"a bot must not consume the pending player kit");
                Assert.That(LoadoutSnapshot.Capture(rig.Inventory,rig.Equipment).equipment.ToJson(),Is.Not.EqualTo(expected.equipment.ToJson()));
                rig.AssignParty(0,true);
                rig.ApplyClass(_character,config);
                Assert.That(rig.Inventory.TotalUnits,Is.Zero,"class defaults must not replace the prepared empty bag");
                var actual=LoadoutSnapshot.Capture(rig.Inventory,rig.Equipment);
                Assert.That(actual.equipment.ToJson(),Is.EqualTo(expected.equipment.ToJson()),"prepared identities must reach the real offline rig");
                Assert.That(OfflineProfileSession.Complete(true,rig,out why),Is.True,why);
                Assert.That(_profile.Snapshot.Ready.equipment.ToJson(),Is.EqualTo(expected.equipment.ToJson()));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(actor);
                reset.Invoke(null,null);
                Emberdelve.Core.NetRole.Set(priorRole);
            }
        }
        [TestCase(0)] [TestCase(1)] [TestCase(2)] [TestCase(3)]
        public void ExtractionWriteFailureFreezesOutcomeAndRetriesExactLootOnce(int partySize)
        {
            const System.Reflection.BindingFlags statics = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
            const System.Reflection.BindingFlags fields = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var sessionType = typeof(OfflineProfileSession);
            var reset = sessionType.GetMethod("Reset", statics);
            var priorRole = NetRole.Current;
            var priorMode = RunContext.SelectedRoyaleMode;
            var actor = new GameObject("PendingExtractionPlayer");
            var host = new GameObject("PendingExtractionDirector");
            try
            {
                NetRole.ResetToOffline(); reset.Invoke(null, null);
                RunContext.SelectRoyaleMode(partySize == 0 ? (Emberdelve.Rules.RoyalePartyMode?)null : (Emberdelve.Rules.RoyalePartyMode)partySize);
                sessionType.GetField("<Current>k__BackingField", statics).SetValue(null, _profile);
                Assert.True(OfflineProfileSession.Depart(out var why), why);
                actor.AddComponent<Inventory>(); actor.AddComponent<Equipment>();
                actor.AddComponent<Emberdelve.Player.ClassLoadout>();
                var rig = actor.AddComponent<Emberdelve.Player.PlayerRig>();
                typeof(Emberdelve.Movement.PlayerMovement).GetMethod("Awake", fields)
                    .Invoke(actor.GetComponent<Emberdelve.Movement.PlayerMovement>(), null);
                rig.ApplyClass(_character, AssetDatabase.LoadAssetAtPath<GameConfig>("Assets/Emberdelve/Data/EmberdelveGameConfig.asset"));
                var parked = _profile.Snapshot.Stash.ToJson();
                var gold = _profile.Snapshot.Gold;
                var director = host.AddComponent<Emberdelve.Flow.RunDirector>();
                var player = new Emberdelve.Flow.PlayerSession { PlayerId = rig.PlayerId, Rig = rig,
                    Inventory = rig.Inventory, Class = _character, IsLocal = true };
                var type = director.GetType();
                ((System.Collections.Generic.List<Emberdelve.Flow.PlayerSession>)type.GetField("_sessions", fields).GetValue(director)).Add(player);
                ((System.Collections.Generic.Dictionary<int, Emberdelve.Flow.PlayerSession>)type.GetField("_byPlayerId", fields).GetValue(director)).Add(rig.PlayerId, player);
                type.GetField("_local", fields).SetValue(director, player);
                type.GetField("_state", fields).SetValue(director, RunState.Running);
                type.GetField("_startedAt", fields).SetValue(director, Time.unscaledTime);
                if (partySize > 0)
                {
                    Assert.True(director.ConfigureRoyale((Emberdelve.Rules.RoyalePartyMode)partySize));
                    Assert.True(director.TryStartRoyaleFloor(1, 120, out why), why);
                    for (int floor = 1; floor <= 2; floor++)
                    {
                        ((float[])type.GetField("_floorStartedAt", fields).GetValue(director))[floor] = director.Elapsed - 90f;
                        Assert.False(director.BeginExtraction(rig, out _), "Intermediate floors cannot bank loot");
                        var loot = _catalog.Items.First(i => i != null && i.IsEquippable && i.equipSlot == EquipmentSlot.Necklace);
                        Assert.True(rig.Inventory.TryAdd(loot, 1, out why), why);
                        var carried = LoadoutSnapshot.Capture(rig.Inventory, rig.Equipment);
                        string ammunition = JsonUtility.ToJson(rig.Loadout.CapturePreparedAmmo(carried));
                        var portalObject = new GameObject("ProfileDescent_" + floor);
                        portalObject.transform.SetParent(host.transform, false);
                        portalObject.transform.position = rig.InteractionOrigin;
                        var portal = portalObject.AddComponent<Emberdelve.Interaction.DescentPortalGroup>();
                        Assert.True(portal.Configure(director, new Emberdelve.Rules.DescentPortalSchedule(120,
                            (Emberdelve.Rules.RoyalePartyMode)partySize, new Emberdelve.Rules.DescentPortalWave(60, 1)), floor, 0));
                        Assert.True(portal.TryEnter(rig, out why), why);
                        Assert.False(portal.TryEnter(rig, out _), "A used portal cannot duplicate a traveller");
                        Assert.True(director.TryStartRoyaleFloor(floor + 1, 120, out why), why);
                        Assert.True(director.TryArriveAtNextFloor(rig, floor + 1, new Vector3(floor * 100, 0, 0), Quaternion.identity, out why), why);
                        var arrived = LoadoutSnapshot.Capture(rig.Inventory, rig.Equipment);
                        Assert.That(arrived.ToJson(), Is.EqualTo(carried.ToJson()));
                        Assert.That(JsonUtility.ToJson(rig.Loadout.CapturePreparedAmmo(arrived)), Is.EqualTo(ammunition));
                        Assert.That(_profile.Snapshot.Stash.ToJson(), Is.EqualTo(parked));
                        Assert.That(_profile.Snapshot.Gold, Is.EqualTo(gold));
                        Assert.True(OfflineProfileSession.HasRaid); Assert.False(player.HasResult);
                    }
                }
                var expected = LoadoutSnapshot.Capture(rig.Inventory, rig.Equipment).ToJson();
                var expectedAmmo = JsonUtility.ToJson(rig.Loadout.CapturePreparedAmmo(LoadoutSnapshot.Capture(rig.Inventory, rig.Equipment)));
                using (var lease = new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                {
                    UnityEngine.TestTools.LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("\\[Emberdelve\\]\\[Profile\\]"));
                    Assert.False(director.BeginExtraction(rig, out why));
                    Assert.True(director.ExtractionSettlementPending); Assert.False(player.HasResult);
                    Assert.False(player.IsRunning); Assert.False(director.SceneFlowAllowed);
                    Assert.That(Time.timeScale, Is.Zero);
                    Assert.False(director.IsInteractionActorAdmitted(rig, out _));
                    Assert.False(director.RetryExtractionSettlement(out _));
                    Assert.True(OfflineProfileSession.HasRaid);
                }
                Assert.True(director.RetryExtractionSettlement(out why), why);
                Assert.False(director.ExtractionSettlementPending); Assert.True(player.HasResult);
                Assert.That(player.State, Is.EqualTo(RunState.Extracted));
                Assert.False(OfflineProfileSession.HasRaid);
                Assert.That(_profile.Snapshot.Ready.ToJson(), Is.EqualTo(expected));
                long revision = _profile.Revision;
                Assert.True(director.RetryExtractionSettlement(out why), why);
                Assert.That(_profile.Revision, Is.EqualTo(revision));
                Assert.That(Time.timeScale, Is.EqualTo(1f));
                Assert.That(_profile.Snapshot.Stash.ToJson(), Is.EqualTo(parked));
                Assert.That(_profile.Snapshot.Gold, Is.EqualTo(gold));
                reset.Invoke(null, null);
                Assert.True(OfflineProfile.TryOpen(_path, _catalog, _character, out _profile, out why), why);
                Assert.That(_profile.Snapshot.Ready.ToJson(), Is.EqualTo(expected), "Reopening must retain exact final-floor loot");
                var reopenedAmmo = _profile.Snapshot.Ammunition;
                reopenedAmmo.stored = Array.Empty<OfflineAmmunition.ReserveEntry>();
                Assert.That(JsonUtility.ToJson(reopenedAmmo), Is.EqualTo(expectedAmmo));
                // Reopening the disk profile must not resurrect the reserved raid or bank it twice.
                Assert.That(_profile.Revision, Is.EqualTo(revision));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(host); UnityEngine.Object.DestroyImmediate(actor);
                reset.Invoke(null, null); NetRole.Set(priorRole); RunContext.SelectRoyaleMode(priorMode); Time.timeScale = 1f;
            }
        }

        [Test] public void CorruptSaveIsNotOverwrittenOrSilentlyReset()
        {
            File.WriteAllText(_path,"{broken");
            Assert.That(OfflineProfile.TryOpen(_path,_catalog,_character,out _,out _),Is.False);
            Assert.That(File.ReadAllText(_path),Is.EqualTo("{broken"));
        }
        [Test] public void EquipmentCanMoveToStashAndBackWithoutChangingIdentity()
        {
            var original=_profile.Snapshot.Ready.equipment.entries.First();
            var slot=(EquipmentSlot)Enum.Parse(typeof(EquipmentSlot),original.slot);
            Assert.That(_profile.Move(PreparationArea.Equipment,0,PreparationArea.Stash,EquipmentSlot.None,null,_profile.Revision,out string why),Is.True,why);
            Assert.That(_profile.Snapshot.Stash.entries.Single().item.ToJson(),Is.EqualTo(original.item.ToJson()));
            Assert.That(_profile.Move(PreparationArea.Stash,0,PreparationArea.Equipment,slot,null,_profile.Revision,out why),Is.True,why);
            Assert.That(_profile.Snapshot.Ready.equipment.entries.Single(e=>e.slot==slot.ToString()).item.ToJson(),Is.EqualTo(original.item.ToJson()));
        }
    }
}
