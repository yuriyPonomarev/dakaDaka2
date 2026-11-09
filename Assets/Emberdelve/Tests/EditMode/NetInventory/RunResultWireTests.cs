using Emberdelve.Core;
using Emberdelve.Net;
using NUnit.Framework;

namespace Emberdelve.NetInventory.Tests
{
    public sealed class RunResultWireTests
    {
        [Test]
        public void ServerResultRoundTripsEveryPresentationFieldExactly()
        {
            var expected = new RunResult(RunState.Extracted, "Ashen Warrior", 123.5f, 7, 4, 87.25f, 19, 345);
            Assert.That(RunResultWire.TryCreate(in expected, out RunResultWire wire), Is.True);
            Assert.That(wire.TryToRunResult(19, out RunResult actual), Is.True);
            Assert.That(actual.Outcome, Is.EqualTo(expected.Outcome));
            Assert.That(actual.ClassName, Is.EqualTo(expected.ClassName));
            Assert.That(actual.LootValue, Is.EqualTo(expected.LootValue));
            Assert.That(actual.ItemsCollected, Is.EqualTo(expected.ItemsCollected));
            Assert.That(actual.EnemiesSlain, Is.EqualTo(expected.EnemiesSlain));
            Assert.That(actual.DurationSeconds, Is.EqualTo(expected.DurationSeconds));
            Assert.That(actual.PlayerId, Is.EqualTo(expected.PlayerId));
            Assert.That(actual.GearScore, Is.EqualTo(expected.GearScore));
        }

        [Test]
        public void WireRejectsWrongOwnerAndMalformedOrNonterminalResults()
        {
            var expected = new RunResult(RunState.Dead, "Archer", 12f, 1, 2, 31f, 8, 40);
            Assert.That(RunResultWire.TryCreate(in expected, out RunResultWire wire), Is.True);
            Assert.That(wire.TryToRunResult(9, out _), Is.False);

            wire.Outcome = (byte)RunState.Running;
            Assert.That(wire.TryToRunResult(8, out _), Is.False);

            wire.Outcome = (byte)RunState.Dead;
            wire.LootValue = float.NaN;
            Assert.That(wire.TryToRunResult(8, out _), Is.False);

            var invalid = new RunResult(RunState.Running, "Archer", 12f, 1, 2, 31f, 8, 40);
            Assert.That(RunResultWire.TryCreate(in invalid, out _), Is.False);

            var tooLong = new RunResult(RunState.Dead, new string('x', 121), 12f, 1, 2, 31f, 8, 40);
            Assert.That(RunResultWire.TryCreate(in tooLong, out _), Is.False);
        }
    }
}
