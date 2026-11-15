using Emberdelve.Rules;
using NUnit.Framework;

namespace Emberdelve.Rules.Tests
{
    public sealed class RoyaleRosterTests
    {
        [Test]
        public void AloneOrSamePartyCannotStartUntilBotOppositionAdmitted()
        {
            var r = new RoyaleRoster(RoyalePartyMode.Duo);
            Assert.True(r.TryAdmit(0, 0, false)); Assert.False(r.TryStart());
            Assert.True(r.TryAdmit(1, 0, false)); Assert.False(r.TryStart());
            Assert.True(r.TryAdmit(2, 1, true)); Assert.True(r.TryStart());
            Assert.False(r.TryStart()); Assert.False(r.TryAdmit(3, 2, false));
            Assert.That(r.AliveParties, Is.EqualTo(2));
        }

        [Test]
        public void ReconnectCannotReviveOrReplaceEliminatedIdentity()
        {
            var r = Match();
            Assert.True(r.SetConnected(0, false)); Assert.That(r.AliveParties, Is.EqualTo(2));
            Assert.True(r.TryEliminate(0)); Assert.True(r.SetConnected(0, true));
            Assert.False(r.TryAdmit(0, 7, false)); Assert.False(r.TryEscape(0));
            Assert.True(r.TryGet(0, out var party, out var bot, out var connected, out var state));
            Assert.That(party, Is.EqualTo(0)); Assert.False(bot); Assert.True(connected);
            Assert.That(state, Is.EqualTo(RoyaleParticipantState.Eliminated));
        }

        [Test]
        public void LastPartyIsNotAutomaticallyEscapedAndTerminalTransitionsAreOnceOnly()
        {
            var r = Match(); Assert.False(r.TryGetLastAliveParty(out _));
            Assert.True(r.TryEliminate(1)); Assert.False(r.TryEliminate(1));
            Assert.True(r.TryGetLastAliveParty(out var party)); Assert.That(party, Is.Zero);
            r.TryGet(0, out _, out _, out _, out var state);
            Assert.That(state, Is.EqualTo(RoyaleParticipantState.Alive));
            Assert.True(r.TryEscape(0)); Assert.False(r.TryEscape(0)); Assert.False(r.TryEliminate(0));
            Assert.False(r.TryGetLastAliveParty(out _)); Assert.That(r.AliveParties, Is.Zero);
        }

        [Test]
        public void InvalidAdmissionsAndPreStartResultsDoNotMutateRoster()
        {
            var r = new RoyaleRoster();
            Assert.False(r.TryAdmit(-1, 0, false)); Assert.False(r.TryAdmit(0, -1, true));
            Assert.True(r.TryAdmit(0, 0, true)); Assert.False(r.TryAdmit(0, 1, false));
            Assert.False(r.TryEliminate(0)); Assert.False(r.TryEscape(0));
            Assert.False(r.SetConnected(0, false)); Assert.False(r.SetConnected(99, true));
            Assert.That(r.Count, Is.EqualTo(1)); Assert.False(r.TryGetLastAliveParty(out _));
        }

        [TestCase(RoyalePartyMode.Solo, 16, 16)]
        [TestCase(RoyalePartyMode.Duo, 16, 8)]
        [TestCase(RoyalePartyMode.Trio, 15, 5)]
        public void HumansAndBotsShareMatchAndPartyLimits(RoyalePartyMode mode, int capacity, int teams)
        {
            var r = new RoyaleRoster(mode);
            Assert.That(r.Capacity, Is.EqualTo(capacity));
            for (int i = 0; i < capacity; i++)
                Assert.True(r.TryAdmit(i, i / r.PartySize, i % 2 == 0));
            Assert.That(r.AliveParties, Is.EqualTo(teams));
            Assert.False(r.TryAdmit(capacity, teams, false));
            Assert.True(r.TryStart());
        }

        [TestCase(RoyalePartyMode.Solo)]
        [TestCase(RoyalePartyMode.Duo)]
        [TestCase(RoyalePartyMode.Trio)]
        public void PartyCannotExceedModeLimitOrCreateExtraPartialTeam(RoyalePartyMode mode)
        {
            var r = new RoyaleRoster(mode);
            for (int i = 0; i < r.PartySize; i++) Assert.True(r.TryAdmit(i, 100, false));
            Assert.False(r.TryAdmit(99, 100, true));
            for (int i = 1; i < r.PartyCapacity; i++) Assert.True(r.TryAdmit(100 + i, 100 + i, true));
            Assert.False(r.TryAdmit(999, 999, false));
        }

        [Test]
        public void UnknownModeIsRejected()
        {
            Assert.Throws<System.ArgumentOutOfRangeException>(() => new RoyaleRoster((RoyalePartyMode)0));
            Assert.Throws<System.ArgumentOutOfRangeException>(() => new RoyaleRoster((RoyalePartyMode)4));
        }

        private static RoyaleRoster Match()
        {
            var r = new RoyaleRoster(); r.TryAdmit(0, 0, false); r.TryAdmit(1, 1, true);
            Assert.True(r.TryStart()); return r;
        }
    }
}
