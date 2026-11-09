using Emberdelve.Items;
using Emberdelve.Net;
using NUnit.Framework;
using UnityEngine;

namespace Emberdelve.Tests.NetInventory
{
    public sealed class FloorLootWireTests
    {
        [Test]
        public void AcceptsCompleteStateAndRejectsDuplicateIdsOrEmptyRows()
        {
            var document = new FloorLootDocument
            {
                revision = 12,
                entries = new[]
                {
                    new FloorLootEntryDocument { id = 7, position = new Vector3(1, .3f, 2),
                        item = new ItemSnapshot { itemId = "coin", quantity = 3 } }
                }
            };
            var wire = new FloorLootWire { Json = JsonUtility.ToJson(document) };
            Assert.IsTrue(wire.TryRead(out FloorLootDocument parsed, out _));
            Assert.AreEqual(12ul, parsed.revision);
            Assert.AreEqual(3, parsed.entries[0].item.quantity);

            document.entries = new[] { document.entries[0], document.entries[0] };
            wire.Json = JsonUtility.ToJson(document);
            Assert.IsFalse(wire.TryRead(out _, out _));

            document.entries = new[]
            {
                new FloorLootEntryDocument { id = 8, position = Vector3.zero,
                    item = new ItemSnapshot { itemId = "coin", quantity = 0 } }
            };
            wire.Json = JsonUtility.ToJson(document);
            Assert.IsFalse(wire.TryRead(out _, out _));
        }

        [Test]
        public void RejectsOversizedSnapshot()
        {
            var wire = new FloorLootWire { Json = new string('x', FloorLootWire.MaxUtf8Bytes + 1) };
            Assert.IsFalse(wire.TryRead(out _, out _));
        }
    }
}
