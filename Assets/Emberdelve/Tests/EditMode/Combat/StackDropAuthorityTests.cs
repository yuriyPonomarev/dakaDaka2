using System;
using System.Linq;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Flow;
using Emberdelve.Items;
using Emberdelve.Player;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Emberdelve.Combat.Tests
{
    public sealed class StackDropAuthorityTests
    {
        sealed class Ground : IGroundItemSink, IGroundStackSink
        {
            internal sealed class Prepared : IPreparedGroundDrop
            {
                internal bool Published, Canceled, Throw;
                public void Publish() { Published = true; if (Throw) throw new InvalidOperationException("publication failed"); }
                public void Cancel() { Canceled = true; Published = false; }
                public void Dispose() { if (!Published) Cancel(); }
            }
            internal int Amount, Calls;
            internal ItemData Item;
            internal bool Refuse, Throw;
            internal Prepared Drop;
            internal Action Preparing;
            public bool TryPrepareDropped(ItemInstance item, Vector3 at, out IPreparedGroundDrop drop, out string reason)
            { drop = null; reason = "gear not expected"; return false; }
            public bool TryPrepareDroppedStack(ItemData item, int amount, Vector3 at, out IPreparedGroundDrop drop, out string reason)
            {
                Calls++; Item = item; Amount = amount; Preparing?.Invoke();
                Drop = new Prepared { Throw = Throw }; drop = Drop;
                reason = Refuse ? "preparation failed" : null;
                return !Refuse;
            }
        }
        GameObject _root;
        PlayerRig _rig;
        Inventory _bag;
        InventorySlot _source;
        ItemData _item;
        Ground _ground;
        IGroundItemSink _previousSink;
        NetRoleKind _previousRole;
        RunDirector _previousDirector;
        [SetUp] public void SetUp()
        {
            // These transaction fixtures deliberately use the no-director ground-sink seam.
            // An ambient scene/session service must not admit or reject this isolated test rig.
            GameServices.TryGet<RunDirector>(out _previousDirector);
            GameServices.Unregister<RunDirector>(_previousDirector);
            _previousRole = NetRole.Current; NetRole.ResetToOffline();
            _previousSink = LocalInventoryAuthority.GroundSinkOverride;
            _ground = new Ground(); LocalInventoryAuthority.GroundSinkOverride = _ground;
            _root = new GameObject("StackDropAuthority"); _root.SetActive(false);
            _bag = _root.AddComponent<Inventory>(); var equipment = _root.AddComponent<Equipment>();
            _rig = _root.AddComponent<PlayerRig>(); _root.SetActive(true);
            _rig.WireRig(); _bag.Initialize(_rig); equipment.Initialize(_rig);
            var catalog = AssetDatabase.LoadAssetAtPath<ItemCatalog>("Assets/Emberdelve/Data/EmberdelveItemCatalog.asset");
            _bag.SetCatalog(catalog);
            _item = catalog.Items.First(x => x != null && !x.IsEquippable && x.EffectiveMaxStack >= 3 && x.EffectiveFootprint == Vector2Int.one);
            Assert.That(_bag.TryAddAt(_item, 3, new Vector2Int(2,1), out var reason), Is.True, reason);
            _source = _bag.Slots.Single();
        }
        [TearDown] public void TearDown()
        {
            LocalInventoryAuthority.GroundSinkOverride = _previousSink;
            if (_root != null) UnityEngine.Object.DestroyImmediate(_root);
            NetRole.Set(_previousRole);
            if (_previousDirector != null) GameServices.Register<RunDirector>(_previousDirector);
            _previousDirector = null;
        }
        InventoryCommand Command(InventoryCommandKind kind)
        {
            Assert.That(_bag.CommandAuthority.TryCreateLocalCommand(kind, _source.StackId, 0, Vector2Int.zero,
                EquipmentSlot.None, Guid.Empty, Guid.Empty, out var command, out var reason), Is.True, reason);
            return command;
        }
        [TestCase(InventoryCommandKind.DropOne, 1)]
        [TestCase(InventoryCommandKind.DropStack, 3)]
        public void DropPublishesAuthoritativeQuantityOnceAndRejectsReplay(InventoryCommandKind kind, int amount)
        {
            var command = Command(kind); ulong revision = _bag.Revision;
            _ground.Preparing = () => Assert.That(_bag.TryAdd(_item, 1), Is.False);
            var result = _bag.CommandAuthority.Submit(_rig, command);
            Assert.That(result.Accepted, Is.True, result.Reason);
            Assert.That(_ground.Item, Is.SameAs(_item)); Assert.That(_ground.Amount, Is.EqualTo(amount));
            Assert.That(_ground.Drop.Published, Is.True); Assert.That(_ground.Drop.Canceled, Is.False);
            Assert.That(_bag.TotalUnits, Is.EqualTo(3-amount)); Assert.That(_bag.Revision, Is.EqualTo(revision+1));
            Assert.That(_bag.CommandAuthority.Submit(_rig, command).Accepted, Is.False);
            Assert.That(_ground.Calls, Is.EqualTo(1));
        }
        [TestCase(false, InventoryCommandKind.DropOne)]
        [TestCase(true, InventoryCommandKind.DropOne)]
        [TestCase(false, InventoryCommandKind.DropStack)]
        [TestCase(true, InventoryCommandKind.DropStack)]
        public void GroundFailureRestoresExactSourceAndCancelsPublishedRepresentation(bool publishFailure, InventoryCommandKind kind)
        {
            string before = JsonUtility.ToJson(_bag.ToContainerSnapshot());
            _ground.Throw = publishFailure; _ground.Refuse = !publishFailure;
            var result = _bag.CommandAuthority.Submit(_rig, Command(kind));
            Assert.That(result.Accepted, Is.False); Assert.That(result.Changed, Is.False);
            Assert.That(JsonUtility.ToJson(_bag.ToContainerSnapshot()), Is.EqualTo(before));
            Assert.That(_ground.Drop.Canceled, Is.True); Assert.That(_ground.Drop.Published, Is.False);
        }
        [Test] public void StaleRevisionAndMissingRequesterNeverReachGround()
        {
            var command = Command(InventoryCommandKind.DropStack);
            Assert.That(_bag.CommandAuthority.Submit(null, command).Accepted, Is.False);
            Assert.That(_bag.TryAdd(_item, 1), Is.True);
            Assert.That(_bag.CommandAuthority.Submit(_rig, command).Accepted, Is.False);
            Assert.That(_ground.Calls, Is.Zero); Assert.That(_bag.TotalUnits, Is.EqualTo(4));
        }
        [TestCase(1)] [TestCase(2)]
        public void SplitIntentHonorsAmountDestinationAndRejectsReplay(int amount)
        {
            Assert.That(_bag.CommandAuthority.TryCreateLocalCommand(InventoryCommandKind.Split, _source.StackId, 0,
                new Vector2Int(7,3), EquipmentSlot.None, Guid.Empty, Guid.Empty, out var command, out var reason, amount), Is.True, reason);
            var result = _bag.CommandAuthority.Submit(_rig, command);
            Assert.That(result.Accepted, Is.True, result.Reason);
            Assert.That(_bag.Slots.Single(x => x.Origin == new Vector2Int(7,3)).Count, Is.EqualTo(amount));
            Assert.That(_bag.TryGetStack(_source.StackId, out var rest), Is.True);
            Assert.That(rest.Count, Is.EqualTo(3-amount)); Assert.That(_bag.TotalUnits, Is.EqualTo(3));
            string after = JsonUtility.ToJson(_bag.ToContainerSnapshot());
            Assert.That(_bag.CommandAuthority.Submit(_rig, command).Accepted, Is.False);
            Assert.That(JsonUtility.ToJson(_bag.ToContainerSnapshot()), Is.EqualTo(after));
            Assert.That(_ground.Calls, Is.Zero, "bag splitting never creates a ground pickup");
        }
        [TestCase(3)] [TestCase(4)] [TestCase(int.MaxValue)]
        public void SplitIntentCannotExceedAuthoritativeSource(int amount)
        {
            string before = JsonUtility.ToJson(_bag.ToContainerSnapshot());
            Assert.That(_bag.CommandAuthority.TryCreateLocalCommand(InventoryCommandKind.Split, _source.StackId, 0,
                new Vector2Int(7,3), EquipmentSlot.None, Guid.Empty, Guid.Empty, out var command, out var reason, amount), Is.True, reason);
            Assert.That(_bag.CommandAuthority.Submit(_rig, command).Accepted, Is.False);
            Assert.That(JsonUtility.ToJson(_bag.ToContainerSnapshot()), Is.EqualTo(before));
        }
        [Test] public void SplitIntentRejectsForeignOwnerAndStaleRevision()
        {
            Assert.That(_bag.CommandAuthority.TryCreateLocalCommand(InventoryCommandKind.Split, _source.StackId, 0,
                new Vector2Int(7,3), EquipmentSlot.None, Guid.Empty, Guid.Empty, out var command, out var reason, 1), Is.True, reason);
            var foreign = new InventoryCommand(command.Kind, command.OwnerPlayerId+1, command.Sequence,
                command.ExpectedRevision, command.SourceStackId, 0, command.DestinationCell, EquipmentSlot.None, Guid.Empty, Guid.Empty, 1);
            Assert.That(_bag.CommandAuthority.Submit(_rig, foreign).Accepted, Is.False);
            Assert.That(_bag.TryAdd(_item, 1), Is.True);
            string before = JsonUtility.ToJson(_bag.ToContainerSnapshot());
            Assert.That(_bag.CommandAuthority.Submit(_rig, command).Accepted, Is.False);
            Assert.That(JsonUtility.ToJson(_bag.ToContainerSnapshot()), Is.EqualTo(before));
        }
    }
}
