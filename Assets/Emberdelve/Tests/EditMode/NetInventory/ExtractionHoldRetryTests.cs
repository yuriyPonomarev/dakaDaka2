using System.Reflection;
using Emberdelve.Net;
using Emberdelve.Player;
using NUnit.Framework;
using UnityEngine;

namespace Emberdelve.NetInventory.Tests
{
    public sealed class ExtractionHoldRetryTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

        [TestCase("_activeSequence")]
        [TestCase("_pendingSequence")]
        public void RefusalWaitsForReleaseAndIgnoresStaleResults(string sequenceField)
        {
            var go = new GameObject("extraction-retry-test");
            try
            {
                var rig = go.AddComponent<PlayerRig>();
                rig.WireRig();
                var input = rig.Input;
                input.GetType().GetMethod("Awake", Private).Invoke(input, null);
                input.SetGameplayInputEnabled(true);
                var held = input.GetType().GetProperty("InteractHeldOverride", Private);
                held.SetValue(input, (bool?)true);

                var client = go.AddComponent<NetExtractionClient>();
                typeof(NetExtractionClient).GetField("_rig", Private).SetValue(client, rig);
                var sequence = typeof(NetExtractionClient).GetField(sequenceField, Private);
                var latch = typeof(NetExtractionClient).GetField("_requiresRelease", Private);
                var update = typeof(NetExtractionClient).GetMethod("Update", Private);
                sequence.SetValue(client, 7u);

                client.OnResult(6u, false, "stale");
                Assert.IsFalse((bool)latch.GetValue(client));
                bool latchedBeforeNotification = false;
                client.HoldResultReceived += (_, accepted, reason) => latchedBeforeNotification = (bool)latch.GetValue(client);
                client.OnResult(7u, false, "Recover at least one relic first");
                Assert.IsTrue(latchedBeforeNotification);
                for (int frame = 0; frame < 120; frame++) update.Invoke(client, null);
                Assert.IsTrue((bool)latch.GetValue(client), "holding the same key cannot restart a rejected attempt");
                Assert.AreEqual(0u, sequence.GetValue(client));

                held.SetValue(input, (bool?)false);
                update.Invoke(client, null);
                Assert.IsFalse((bool)latch.GetValue(client), "release permits the next deliberate attempt");
                client.OnResult(7u, false, "late duplicate");
                Assert.IsFalse((bool)latch.GetValue(client), "late replies cannot latch a new attempt");
            }
            finally { Object.DestroyImmediate(go); }
        }
    }
}
