using System.Linq;
using Emberdelve.Data;
using Emberdelve.Items;
using Emberdelve.Net;
using Emberdelve.Net.Rules;
using NUnit.Framework;
using UnityEditor;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Emberdelve.Net.Tests
{
    public sealed class CorpseContainerWireTests
    {
        private GameObject _owner;
        private Inventory _bag;
        private Equipment _equipment;
        private CorpseContainerModel _model;
        private ItemInstance _gear;
        private ItemCatalog _catalog;

        [SetUp]
        public void SetUp()
        {
            _catalog = AssetDatabase.LoadAssetAtPath<ItemCatalog>("Assets/Emberdelve/Data/EmberdelveItemCatalog.asset");
            Assert.That(_catalog, Is.Not.Null);
            ItemData gearDefinition = _catalog.Items.FirstOrDefault(item => item != null && item.IsEquippable);
            Assert.That(gearDefinition, Is.Not.Null);
            _owner = new GameObject("CorpseContainerWireTest");
            _bag = _owner.AddComponent<Inventory>();
            _equipment = _owner.AddComponent<Equipment>();
            _bag.SetCatalog(_catalog);
            _bag.ConfigureGrid(10, 6);
            Assert.That(_bag.TryMintKitInstance(gearDefinition, out _gear, out string reason), Is.True, reason);
            Assert.That(_bag.TryAddInstance(_gear, out reason), Is.True, reason);
            Assert.That(CorpseLootManifest.TryCapture(25, new Vector3(1f, 0f, 2f), _bag, _equipment,
                out CorpseLootManifest manifest, out reason), Is.True, reason);
            Assert.That(CorpseContainerModel.TryCreate(manifest, out _model, out reason), Is.True, reason);
        }

        [TearDown]
        public void TearDown()
        {
            if (_owner != null) Object.DestroyImmediate(_owner);
        }

        [Test]
        public void StateWireRoundTripsIdentityRevisionAndExactGearSnapshot()
        {
            Assert.That(CorpseContainerWire.TryCreate(9, _model, out CorpseContainerWire wire, out string reason), Is.True, reason);
            Assert.That(wire.TryRead(out CorpseContainerDocument read, out reason), Is.True, reason);
            Assert.That(read.corpseId, Is.EqualTo(9));
            Assert.That(read.revision, Is.EqualTo(_model.Revision));
            Assert.That(read.worldPosition, Is.EqualTo(_model.WorldPosition));
            Assert.That(read.entries.Length, Is.EqualTo(1));
            Assert.That(read.entries[0].item.uid, Is.EqualTo(_gear.UidText));
            Assert.That(read.entries[0].item.ToJson(), Is.EqualTo(_gear.ToSnapshot().ToJson()));
        }

        [Test]
        public void BeltLoadoutKeepsPhysicalSlotOverNgoAndBecomesVisibleCorpseLoot()
        {
            var potion = _catalog.Items.First(i => i != null && i.healAmount > 0);
            Assert.IsTrue(_bag.TryAdd(potion, 5, out string reason), reason);
            var stack = _bag.Slots.Single(s => s.Item == potion);
            Assert.IsTrue(_bag.TryMove(stack.Index, _bag.BeltCell(5), out reason), reason);
            var state = NetworkLoadoutState.Capture(_bag, _equipment);
            state.HeldStackId = stack.StackId;
            using (var writer = new FastBufferWriter(32768, Allocator.Temp))
            {
                writer.WriteValueSafe(NetworkLoadoutWire.FromState(state));
                using (var reader = new FastBufferReader(writer, Allocator.Temp))
                {
                    reader.ReadValueSafe(out NetworkLoadoutWire wire);
                    Assert.IsTrue(wire.TryToState(out var decoded, out reason), reason);
                    Assert.AreEqual(state.Revision, decoded.Revision);
                    Assert.AreEqual(stack.StackId, decoded.HeldStackId);
                    var identity = decoded.Stacks.Single(s => s.Id == stack.StackId);
                    Assert.AreEqual(_bag.BeltCell(5), new Vector2Int(identity.X, identity.Y));
                    var row = decoded.Loadout.bag.entries.Single(e => e.item.itemId == potion.itemId);
                    Assert.AreEqual(5, row.item.quantity);
                    Assert.AreEqual(_bag.BeltCell(5), new Vector2Int(row.x, row.y));
                }
            }
            Assert.IsTrue(CorpseLootManifest.TryCapture(25, Vector3.zero, _bag, _equipment, out var manifest, out reason), reason);
            Assert.IsTrue(CorpseContainerModel.TryCreate(manifest, out var corpse, out reason), reason);
            Assert.IsTrue(CorpseContainerWire.TryCreate(9, corpse, out var corpseWire, out reason), reason);
            Assert.IsTrue(corpseWire.TryRead(out var document, out reason), reason);
            var utility = document.entries.Single(e => e.item.itemId == potion.itemId);
            Assert.AreEqual(5, utility.item.quantity);
            Assert.That(utility.x, Is.InRange(0, corpse.Width - potion.EffectiveFootprint.x));
            Assert.That(utility.y, Is.GreaterThanOrEqualTo(_bag.GridHeight));
            Assert.AreEqual(_gear.UidText, document.entries.Single(e => !string.IsNullOrEmpty(e.item.uid)).item.uid);
        }

        [Test]
        public void StateWireRefusesDuplicateEntryIdsAndOversizedPayloads()
        {
            var duplicate = new CorpseContainerDocument
            {
                corpseId = 3,
                sourcePlayerId = 25,
                entries = new[]
                {
                    new CorpseContainerEntryDocument { entryId = 1, item = _gear.ToSnapshot() },
                    new CorpseContainerEntryDocument { entryId = 1, item = _gear.ToSnapshot() }
                }
            };
            var duplicateWire = new CorpseContainerWire { Json = JsonUtility.ToJson(duplicate) };
            Assert.That(duplicateWire.TryRead(out _, out string reason), Is.False);
            Assert.That(reason, Does.Contain("repeats"));

            var oversized = new CorpseContainerWire { Json = new string('x', CorpseContainerWire.MaxUtf8Bytes + 1) };
            Assert.That(oversized.TryRead(out _, out reason), Is.False);
            Assert.That(reason, Does.Contain("oversized"));
        }

        [Test]
        public void PerConnectionSequenceGateAcceptsOnlyNextNonzeroRequest()
        {
            var gate = new CorpseRequestSequenceGate();
            Assert.That(gate.TryAccept(1, out string reason), Is.True, reason);
            Assert.That(gate.TryAccept(1, out reason), Is.False, "a replay is rejected");
            Assert.That(gate.TryAccept(3, out reason), Is.False, "a skipped sequence is rejected");
            Assert.That(gate.LastAccepted, Is.EqualTo(1));
            Assert.That(gate.TryAccept(2, out reason), Is.True, reason);
            Assert.That(gate.TryAccept(0, out _), Is.False);
            Assert.That(gate.TryAccept(CorpseRequestSequenceGate.MaximumSequence + 1, out _), Is.False);
        }

        [Test]
        public void CorpseRequestRateGateRejectsBurstsAndClockRegressionWithoutMovingWindow()
        {
            var gate = new CorpseRequestRateGate(0.25);
            Assert.That(gate.TryAccept(10.0, out string reason), Is.True, reason);
            Assert.That(gate.TryAccept(10.1, out reason), Is.False);
            Assert.That(reason, Does.Contain("rate limit"));
            Assert.That(gate.TryAccept(9.9, out reason), Is.False, "backwards server time cannot bypass the interval");
            Assert.That(gate.TryAccept(10.249, out _), Is.False, "rejected attempts do not advance the window");
            Assert.That(gate.TryAccept(10.25, out reason), Is.True, reason);
            Assert.That(gate.TryAccept(double.NaN, out _), Is.False);
            Assert.That(gate.TryAccept(double.PositiveInfinity, out _), Is.False);
        }

        [Test]
        public void PresenceLedgerKeepsNewestPositionAndRejectsStaleOrResurrectedCorpse()
        {
            var ledger = new CorpsePresenceStateTable();
            Assert.That(ledger.TryApply(31, new Vector3(1f, 0f, 2f), 1, false, out _), Is.True);
            Assert.That(ledger.TryApply(31, new Vector3(4f, 0f, 5f), 2, false,
                out CorpsePresenceStateTable.State moved), Is.True);
            Assert.That(moved.Position, Is.EqualTo(new Vector3(4f, 0f, 5f)));

            Assert.That(ledger.TryApply(31, new Vector3(1f, 0f, 2f), 1, false, out _), Is.False,
                "late presence cannot move the marker back to stale coordinates");
            Assert.That(ledger.TryApply(31, new Vector3(4f, 0f, 5f), 3, true,
                out CorpsePresenceStateTable.State removed), Is.True);
            Assert.That(removed.Removed, Is.True);
            Assert.That(ledger.TryApply(31, new Vector3(4f, 0f, 5f), 4, false, out _), Is.False,
                "a removed server session ID cannot be resurrected");
            Assert.That(ledger.TryGet(31, out CorpsePresenceStateTable.State current), Is.True);
            Assert.That(current.Removed, Is.True);
        }

        [Test]
        public void PresenceLedgerRejectsInvalidIdentityAndPosition()
        {
            var ledger = new CorpsePresenceStateTable();
            Assert.That(ledger.TryApply(0, Vector3.zero, 0, false, out _), Is.False);
            Assert.That(ledger.TryApply(1, new Vector3(float.NaN, 0f, 0f), 1, false, out _), Is.False);
            Assert.That(ledger.TryApply(1, Vector3.zero, 1, false, out _), Is.True);
        }

        [Test]
        public void CorpseInteractionRequiresLiveLocalNetworkOwner()
        {
            Assert.That(CorpseInteractionGate.CanRequest(true, false, true, true, out _), Is.True,
                "a ready client owner can request through its RPC bridge");
            Assert.That(CorpseInteractionGate.CanRequest(false, true, true, true, out _), Is.True,
                "a ready listen-host owner can request through its local server bridge");
            Assert.That(CorpseInteractionGate.CanRequest(false, false, true, true, out _), Is.False,
                "server remotes and offline actors do not create owner requests");
            Assert.That(CorpseInteractionGate.CanRequest(false, false, false, true, out _), Is.False,
                "remote actor cannot use the local owner's marker");
            Assert.That(CorpseInteractionGate.CanRequest(true, false, true, false, out _), Is.False,
                "dead actor cannot request contents");
            Assert.That(NetPlayerRig.ShouldEnableHostCorpsePresenter(NetPlayerRole.HostPlayer), Is.True);
            Assert.That(NetPlayerRig.ShouldEnableHostCorpsePresenter(NetPlayerRole.Owner), Is.False);
            Assert.That(NetPlayerRig.ShouldEnableHostCorpsePresenter(NetPlayerRole.Server), Is.False);
        }

        [Test]
        public void CorpseUiSessionBindsStateAndTransferToCurrentCorpseAndSequence()
        {
            var session = new CorpseContainerUiSession();
            Assert.That(session.Open(8), Is.True);
            Assert.That(session.BeginStateRequest(1), Is.True);
            Assert.That(session.BeginStateRequest(2), Is.False, "one state request can be pending at a time");
            Assert.That(session.ApplyState(CreateDocument(9, 4, 3)), Is.False, "another corpse cannot replace the open container");
            Assert.That(session.ApplyState(CreateDocument(8, 4, 3)), Is.True);
            Assert.That(session.ApplyState(CreateDocument(8, 3, 2)), Is.False, "older state cannot replace the current corpse revision");
            Assert.That(session.ApplyResult(8, 1, true, 4, string.Empty), Is.False,
                "a late request result cannot be mistaken for an active transfer after state arrived");

            Assert.That(session.TryBeginTransfer(12, 4, 7, out _, out _), Is.False,
                "the UI cannot request more units than the displayed row contains");
            Assert.That(session.TryBeginTransfer(12, 1, 7, out CorpseTransferUiRequest request, out string reason), Is.True, reason);
            Assert.That(request.CorpseRevision, Is.EqualTo(4));
            Assert.That(request.BagRevision, Is.EqualTo(7));
            Assert.That(request.Amount, Is.EqualTo(1));
            Assert.That(session.TryBeginTransfer(12, 1, 7, out _, out _), Is.False,
                "re-entrant UI action cannot start a duplicate while awaiting the server");
            Assert.That(session.AttachTransferSequence(3), Is.True);
            Assert.That(session.ApplyResult(8, 2, false, 4, "stale"), Is.False,
                "an unrelated sequence is ignored");
            Assert.That(session.ApplyResult(8, 3, false, 5, "stale"), Is.True);
            Assert.That(session.NeedsRefresh, Is.True);
            Assert.That(session.ApplyState(CreateDocument(8, 5, 2)), Is.True);
            Assert.That(session.NeedsRefresh, Is.False);
            session.Close();
            Assert.That(session.IsOpen, Is.False);
            Assert.That(session.TryBeginTransfer(12, 1, 7, out _, out _), Is.False);
        }

        private static CorpseContainerDocument CreateDocument(ulong corpseId, ulong revision, int quantity)
        {
            return new CorpseContainerDocument
            {
                corpseId = corpseId,
                revision = revision,
                entries = new[]
                {
                    new CorpseContainerEntryDocument
                    {
                        entryId = 12,
                        item = new ItemSnapshot { itemId = "Potion", quantity = quantity }
                    }
                }
            };
        }
    }
}
