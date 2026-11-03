using Emberdelve.Net.Rules;
using NUnit.Framework;

namespace Emberdelve.NetRules.Tests
{
    /// <summary>U3B2: the reject token bucket, the abuse memory, the log throttle and the policy numbers.</summary>
    public sealed class SkillAbuseControlTests
    {
        private static SkillAbusePolicy Policy(double burst = 3.0, double rate = 0.0, int excess = 2, double memory = 60.0)
        {
            return new SkillAbusePolicy(burst, rate, excess, memory);
        }

        [Test]
        public void Policy_DefaultsAndClamping()
        {
            SkillAbusePolicy d = SkillAbusePolicy.Default;
            Assert.That(d.RejectBurst, Is.EqualTo(8.0));
            Assert.That(d.RejectRefillPerSecond, Is.EqualTo(1.0));
            Assert.That(d.DisconnectExcess, Is.EqualTo(8));
            Assert.That(d.MemorySeconds, Is.EqualTo(120.0));

            SkillAbusePolicy bad = new SkillAbusePolicy(double.NaN, -1.0, 0, 1.0e12);
            Assert.That(bad.RejectBurst, Is.EqualTo(SkillAbusePolicy.DefaultRejectBurst));
            Assert.That(bad.RejectRefillPerSecond, Is.EqualTo(0.0));
            Assert.That(bad.DisconnectExcess, Is.EqualTo(1));
            Assert.That(bad.MemorySeconds, Is.EqualTo(86400.0));
        }

        [Test]
        public void Policy_ThresholdHalvesPerStrikeAndNeverDropsBelowOne()
        {
            SkillAbusePolicy p = Policy(excess: 8);
            Assert.That(p.ThresholdFor(0), Is.EqualTo(8));
            Assert.That(p.ThresholdFor(1), Is.EqualTo(4));
            Assert.That(p.ThresholdFor(2), Is.EqualTo(2));
            Assert.That(p.ThresholdFor(3), Is.EqualTo(1));
            Assert.That(p.ThresholdFor(40), Is.EqualTo(1));
            Assert.That(p.ThresholdFor(-3), Is.EqualTo(8));
        }

        [Test]
        public void Ledger_RejectsInsideTheBurstAreOnlyCounted_ExcessDisconnects()
        {
            var ledger = new SkillRejectLedger(Policy(burst: 3.0, rate: 0.0, excess: 2), 0, 0.0);
            Assert.That(ledger.Threshold, Is.EqualTo(2));
            Assert.That(ledger.Record(SkillRejectKind.Identity, 1.0), Is.EqualTo(SkillRejectAction.Counted));
            Assert.That(ledger.Record(SkillRejectKind.Invalid, 1.0), Is.EqualTo(SkillRejectAction.Counted));
            Assert.That(ledger.Record(SkillRejectKind.Replay, 1.0), Is.EqualTo(SkillRejectAction.Counted));
            Assert.That(ledger.Excess, Is.EqualTo(0));
            Assert.That(ledger.Record(SkillRejectKind.Flood, 1.0), Is.EqualTo(SkillRejectAction.Excess));
            Assert.That(ledger.Tripped, Is.False);
            Assert.That(ledger.Record(SkillRejectKind.Flood, 1.0), Is.EqualTo(SkillRejectAction.Disconnect));
            Assert.That(ledger.Tripped, Is.True);
            Assert.That(ledger.Total, Is.EqualTo(5), "with no refill the total at the trip is burst + threshold");
            Assert.That(ledger.Excess, Is.EqualTo(2));
            Assert.That(ledger.Record(SkillRejectKind.Identity, 1.0), Is.EqualTo(SkillRejectAction.Disconnect), "stays tripped");
        }

        [Test]
        public void Ledger_EveryKindIsCounted()
        {
            var ledger = new SkillRejectLedger(Policy(burst: 100.0, excess: 50), 0, 0.0);
            ledger.Record(SkillRejectKind.Identity, 0.0);
            ledger.Record(SkillRejectKind.Identity, 0.0);
            ledger.Record(SkillRejectKind.Invalid, 0.0);
            ledger.Record(SkillRejectKind.Replay, 0.0);
            ledger.Record(SkillRejectKind.Flood, 0.0);
            ledger.Record(SkillRejectKind.Flood, 0.0);
            ledger.Record(SkillRejectKind.Flood, 0.0);
            Assert.That(ledger.Count(SkillRejectKind.Identity), Is.EqualTo(2));
            Assert.That(ledger.Count(SkillRejectKind.Invalid), Is.EqualTo(1));
            Assert.That(ledger.Count(SkillRejectKind.Replay), Is.EqualTo(1));
            Assert.That(ledger.Count(SkillRejectKind.Flood), Is.EqualTo(3));
            Assert.That(ledger.Total, Is.EqualTo(7));
            Assert.That(ledger.Excess, Is.EqualTo(0));
        }

        [Test]
        public void Ledger_AllowanceRefillsOverTimeSoRareRejectsNeverAccumulate()
        {
            var ledger = new SkillRejectLedger(Policy(burst: 2.0, rate: 1.0, excess: 3), 0, 0.0);
            Assert.That(ledger.Record(SkillRejectKind.Replay, 0.0), Is.EqualTo(SkillRejectAction.Counted));
            Assert.That(ledger.Record(SkillRejectKind.Replay, 0.0), Is.EqualTo(SkillRejectAction.Counted));
            Assert.That(ledger.Record(SkillRejectKind.Replay, 0.0), Is.EqualTo(SkillRejectAction.Excess));
            Assert.That(ledger.Record(SkillRejectKind.Replay, 10.0), Is.EqualTo(SkillRejectAction.Counted), "ten idle seconds refill the bucket (capped at the burst)");
            Assert.That(ledger.Tokens, Is.EqualTo(1.0).Within(1e-9));
            Assert.That(ledger.Excess, Is.EqualTo(1));
        }

        [Test]
        public void Ledger_BackwardOrNonFiniteClockNeverRefillsOrThrows()
        {
            var ledger = new SkillRejectLedger(Policy(burst: 1.0, rate: 1000.0, excess: 5), 0, 100.0);
            Assert.That(ledger.Record(SkillRejectKind.Invalid, 100.0), Is.EqualTo(SkillRejectAction.Counted));
            Assert.That(ledger.Record(SkillRejectKind.Invalid, 50.0), Is.EqualTo(SkillRejectAction.Excess));
            Assert.That(ledger.Record(SkillRejectKind.Invalid, double.NaN), Is.EqualTo(SkillRejectAction.Excess));
            Assert.That(ledger.Record(SkillRejectKind.Invalid, double.PositiveInfinity), Is.EqualTo(SkillRejectAction.Excess));
        }

        [Test]
        public void Ledger_ProbationStartsEmptyWithAHalvedThreshold()
        {
            SkillAbusePolicy p = Policy(burst: 8.0, rate: 0.0, excess: 8);
            var probation = new SkillRejectLedger(p, 1, 0.0);
            Assert.That(probation.Strikes, Is.EqualTo(1));
            Assert.That(probation.Threshold, Is.EqualTo(4));
            Assert.That(probation.Tokens, Is.EqualTo(0.0));
            for (int i = 0; i < 3; i++) Assert.That(probation.Record(SkillRejectKind.Identity, 0.0), Is.EqualTo(SkillRejectAction.Excess));
            Assert.That(probation.Record(SkillRejectKind.Replay, 0.0), Is.EqualTo(SkillRejectAction.Disconnect));
            Assert.That(probation.Total, Is.EqualTo(4), "probation trips at the threshold alone: no free allowance");

            var first = new SkillRejectLedger(p, 0, 0.0);
            int rejects = 0;
            while (first.Record(SkillRejectKind.Flood, 0.0) != SkillRejectAction.Disconnect) rejects++;
            Assert.That(rejects + 1, Is.EqualTo(16), "first offence: burst 8 + threshold 8");
        }

        [Test]
        public void Ledger_LogsAreRateLimitedPerKind()
        {
            var ledger = new SkillRejectLedger(Policy(burst: 1000.0, excess: 1000), 0, 0.0);
            int logged = 0;
            for (int i = 0; i < 200; i++)
            {
                ledger.Record(SkillRejectKind.Flood, 0.0);
                if (ledger.ShouldLog(SkillRejectKind.Flood)) logged++;
            }

            // 1,2,3, then 4,8,16,32,64,128 (powers of two) and 100, 200 (hundreds).
            Assert.That(logged, Is.EqualTo(3 + 6 + 2));
        }

        [Test]
        public void LogThrottle_Rule()
        {
            Assert.That(NetLogThrottle.ShouldLog(0), Is.False);
            Assert.That(NetLogThrottle.ShouldLog(-1), Is.False);
            foreach (int c in new[] { 1, 2, 3, 4, 8, 16, 100, 128, 200, 300 }) Assert.That(NetLogThrottle.ShouldLog(c), Is.True, "count " + c);
            foreach (int c in new[] { 5, 6, 7, 9, 99, 101, 150 }) Assert.That(NetLogThrottle.ShouldLog(c), Is.False, "count " + c);
        }

        [Test]
        public void Memory_StrikesSurviveWithinTheWindowAndExpireAfterIt()
        {
            var memory = new SkillAbuseMemory(Policy(memory: 60.0));
            Assert.That(memory.StrikesFor("b", 0.0), Is.EqualTo(0));
            Assert.That(memory.RecordDisconnect("b", 10.0), Is.EqualTo(1));
            Assert.That(memory.StrikesFor("b", 30.0), Is.EqualTo(1));
            Assert.That(memory.SecondsLeft("b", 30.0), Is.EqualTo(40.0).Within(1e-9));
            Assert.That(memory.StrikesFor("b", 69.9), Is.EqualTo(1));
            Assert.That(memory.StrikesFor("b", 70.0), Is.EqualTo(0), "the window is over");
            Assert.That(memory.Count, Is.EqualTo(0), "an expired slot is forgotten");
        }

        [Test]
        public void Memory_AnotherStrikeInsideTheWindowAccumulatesAndRenewsIt()
        {
            var memory = new SkillAbuseMemory(Policy(memory: 60.0));
            Assert.That(memory.RecordDisconnect("b", 0.0), Is.EqualTo(1));
            Assert.That(memory.RecordDisconnect("b", 50.0), Is.EqualTo(2));
            Assert.That(memory.StrikesFor("b", 100.0), Is.EqualTo(2), "renewed to 110");
            Assert.That(memory.StrikesFor("b", 110.0), Is.EqualTo(0));
            Assert.That(memory.RecordDisconnect("b", 200.0), Is.EqualTo(1), "a strike after expiry starts over");
        }

        [Test]
        public void Memory_IsKeyedBySlotCaseInsensitively_AndOthersAreUnaffected()
        {
            var memory = new SkillAbuseMemory(Policy());
            memory.RecordDisconnect("Skl-B", 0.0);
            Assert.That(memory.StrikesFor("skl-b", 1.0), Is.EqualTo(1));
            Assert.That(memory.StrikesFor("skl-a", 1.0), Is.EqualTo(0));
            Assert.That(memory.RecordDisconnect("", 0.0), Is.EqualTo(0));
            Assert.That(memory.RecordDisconnect(null, 0.0), Is.EqualTo(0));
            Assert.That(memory.RecordDisconnect("x", double.NaN), Is.EqualTo(0));
            Assert.That(memory.Count, Is.EqualTo(1));
        }

        [Test]
        public void Memory_StrikesAreCappedAndTheTableIsBounded()
        {
            var memory = new SkillAbuseMemory(Policy(memory: 1000.0));
            int last = 0;
            for (int i = 0; i < SkillAbusePolicy.MaxStrikes + 5; i++) last = memory.RecordDisconnect("b", i);
            Assert.That(last, Is.EqualTo(SkillAbusePolicy.MaxStrikes));

            var many = new SkillAbuseMemory(Policy(memory: 1000.0));
            for (int i = 0; i < SkillAbusePolicy.MaxMemoryEntries + 50; i++) many.RecordDisconnect("slot" + i, i);
            Assert.That(many.Count, Is.LessThanOrEqualTo(SkillAbusePolicy.MaxMemoryEntries));
            Assert.That(many.StrikesFor("slot" + (SkillAbusePolicy.MaxMemoryEntries + 49), 1.0e3 - 1.0), Is.EqualTo(1), "the newest is kept");
        }

        [Test]
        public void Memory_ZeroSecondsMeansNothingIsRemembered()
        {
            var memory = new SkillAbuseMemory(Policy(memory: 0.0));
            Assert.That(memory.RecordDisconnect("b", 5.0), Is.EqualTo(1));
            Assert.That(memory.StrikesFor("b", 5.0), Is.EqualTo(0));
        }

        [Test]
        public void RejoinPolicy_EndToEndInPureTerms()
        {
            // First offence: full allowance. The slot is remembered; its next connection starts on probation and trips sooner.
            SkillAbusePolicy p = Policy(burst: 8.0, rate: 0.0, excess: 8, memory: 300.0);
            var memory = new SkillAbuseMemory(p);

            var first = new SkillRejectLedger(p, memory.StrikesFor("b", 0.0), 0.0);
            while (first.Record(SkillRejectKind.Flood, 0.0) != SkillRejectAction.Disconnect) { }
            int strikes = memory.RecordDisconnect("b", 1.0);
            Assert.That(strikes, Is.EqualTo(1));

            var second = new SkillRejectLedger(p, memory.StrikesFor("b", 5.0), 5.0);
            while (second.Record(SkillRejectKind.Flood, 5.0) != SkillRejectAction.Disconnect) { }
            Assert.That(second.Total, Is.LessThan(first.Total));
            Assert.That(second.Total, Is.EqualTo(p.ThresholdFor(1)));
            Assert.That(memory.RecordDisconnect("b", 6.0), Is.EqualTo(2));
            Assert.That(memory.StrikesFor("b", 400.0), Is.EqualTo(0), "after the window nothing is carried");
        }
    }
}
