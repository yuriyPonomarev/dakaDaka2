using System;
using Emberdelve.Data;
using Emberdelve.Items;
using Emberdelve.Net;
using NUnit.Framework;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace Emberdelve.Tests.NetInventory
{
    public sealed class InventoryIntentWireTests
    {
        [Test]
        public void CommandRoundTrip_UsesServerOwnerAndPreservesBothUids()
        {
            Guid source = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef");
            Guid target = Guid.Parse("fedcba98-7654-3210-fedc-ba9876543210");
            var input = new InventoryCommand(InventoryCommandKind.Swap, 99, 7, 11, 5, 6,
                Vector2Int.zero, EquipmentSlot.None, source, target);
            var wire = InventoryIntentWire.FromCommand(input);
            Assert.IsTrue(wire.TryCreateCommand(42, out var output, out _));
            Assert.AreEqual(42, output.OwnerPlayerId);
            Assert.AreEqual(source, output.ExpectedSourceGearUid);
            Assert.AreEqual(target, output.ExpectedTargetGearUid);
            Assert.AreEqual(input.Sequence, output.Sequence);
            Assert.AreEqual(input.ExpectedRevision, output.ExpectedRevision);
            Assert.AreEqual(input.SourceStackId, output.SourceStackId);
            Assert.AreEqual(input.TargetStackId, output.TargetStackId);
        }

        [Test]
        public void RefusesMissingOwnerUnknownKindAndInvalidFields()
        {
            var wire = new InventoryIntentWire { Kind = (int)InventoryCommandKind.Move, Sequence = 1, SourceStackId = 5 };
            Assert.IsFalse(wire.TryCreateCommand(0, out _, out _));
            wire.Kind = 900;
            Assert.IsFalse(wire.TryCreateCommand(1, out _, out _));
            wire.Kind = (int)InventoryCommandKind.Move;
            wire.CellX = -1;
            Assert.IsFalse(wire.TryCreateCommand(1, out _, out _));
            wire.CellX = 0;
            Assert.IsTrue(wire.TryCreateCommand(1, out _, out _));
            wire.Sequence = 0;
            Assert.IsFalse(wire.TryCreateCommand(1, out _, out _));
        }

        [Test]
        public void ServerRejectsWhenNotRunningAndDoesNotRevealState()
        {
            var server = new NetInventoryServer(null, new NetIdentityTable());
            Assert.IsFalse(server.Receive(3, null, default, out var result));
            Assert.IsFalse(result.Accepted);
            Assert.IsFalse(result.Changed);
            Assert.AreEqual(0, result.Revision);
            server.Dispose();
        }

        [Test]
        public void IntentSerializesThroughNgoWithoutLosingIdentities()
        {
            var source = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef");
            var target = Guid.Parse("fedcba98-7654-3210-fedc-ba9876543210");
            var command = new InventoryCommand(InventoryCommandKind.Swap, 99, 12, 24, 32, 44,
                Vector2Int.zero, EquipmentSlot.None, source, target);
            var wire = InventoryIntentWire.FromCommand(command);
            using (var writer = new FastBufferWriter(512, Allocator.Temp))
            {
                writer.WriteValueSafe(wire);
                using (var reader = new FastBufferReader(writer, Allocator.Temp))
                {
                    reader.ReadValueSafe(out InventoryIntentWire decoded);
                    Assert.IsTrue(decoded.TryCreateCommand(5, out var rebuilt, out _));
                    Assert.AreEqual(source, rebuilt.ExpectedSourceGearUid);
                    Assert.AreEqual(target, rebuilt.ExpectedTargetGearUid);
                    Assert.AreEqual(command.ExpectedRevision, rebuilt.ExpectedRevision);
                    Assert.AreEqual(command.SourceStackId, rebuilt.SourceStackId);
                    Assert.AreEqual(command.TargetStackId, rebuilt.TargetStackId);
                    Assert.AreEqual(5, rebuilt.OwnerPlayerId);
                }
            }
        }
    }
}
