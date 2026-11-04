using Emberdelve.BladeDefense;
using NUnit.Framework;

namespace Emberdelve.Rules.Tests
{
    /// <summary>
    /// U3C3 pure rules: the server's parry memory and the riposte verdicts. Every window, multiplier and gap is a local the test authored and
    /// the expectations are derived from those locals (window +/- a step, block time + window), never typed in as a result.
    /// </summary>
    public sealed class RiposteRulesTests
    {
        // powers of two, so t0 + window is exact in float and the inclusive boundary can be tested on the boundary itself
        private const float T0 = 64f;
        private const int Defender = 7;
        private const int OtherDefender = 8;
        private const int Attacker = 21;
        private const int Epoch = 3;

        private static RiposteRequest Ok(float window = 1f) => new RiposteRequest
        {
            KitCanRiposte = true,
            Alive = true,
            Staggered = false,
            Busy = false,
            Window = window,
            WeaponEpoch = Epoch
        };

        private static RiposteTracker WithParry(float t = T0)
        {
            RiposteTracker tracker = new RiposteTracker();
            tracker.NoteBlock(Defender, Attacker, t, Epoch);
            return tracker;
        }

        // ---- window ----------------------------------------------------------------------------------

        [TestCase(0.5f)]
        [TestCase(1f)]
        [TestCase(2f)]
        public void Window_IsOpenUpToAndIncludingTheBoundary_AndClosedAfterIt(float window)
        {
            RiposteRequest req = Ok(window);
            Assert.That(WithParry().Check(Defender, T0, in req), Is.EqualTo(RiposteVerdict.Allowed), "same instant");
            Assert.That(WithParry().Check(Defender, T0 + window * 0.5f, in req), Is.EqualTo(RiposteVerdict.Allowed), "half way");
            Assert.That(WithParry().Check(Defender, T0 + window, in req), Is.EqualTo(RiposteVerdict.Allowed), "exactly on the boundary");
            Assert.That(WithParry().Check(Defender, T0 + window + 0.01f, in req), Is.EqualTo(RiposteVerdict.WindowClosed), "just after");
            Assert.That(WithParry().Check(Defender, T0 + window * 3f, in req), Is.EqualTo(RiposteVerdict.WindowClosed), "long after");
        }

        [Test]
        public void Window_AnAuthoredShorterWindowClosesSooner()
        {
            RiposteTracker tracker = WithParry();
            float now = T0 + 0.75f;
            RiposteRequest longWindow = Ok(1f);
            RiposteRequest shortWindow = Ok(0.5f);
            Assert.That(tracker.Check(Defender, now, in longWindow), Is.EqualTo(RiposteVerdict.Allowed));
            Assert.That(tracker.Check(Defender, now, in shortWindow), Is.EqualTo(RiposteVerdict.WindowClosed));
        }

        [Test]
        public void Window_NonsenseWindowOrClockIsRefused()
        {
            RiposteTracker tracker = WithParry();
            RiposteRequest nan = Ok(float.NaN);
            RiposteRequest zero = Ok(0f);
            RiposteRequest fine = Ok();
            Assert.That(tracker.Check(Defender, T0, in nan), Is.EqualTo(RiposteVerdict.WindowClosed), "NaN window");
            Assert.That(tracker.Check(Defender, T0 + 0.1f, in zero), Is.EqualTo(RiposteVerdict.WindowClosed), "zero window");
            Assert.That(tracker.Check(Defender, T0 - 0.1f, in fine), Is.EqualTo(RiposteVerdict.WindowClosed), "a clock that ran backwards (a press before the block)");
            Assert.That(tracker.Check(Defender, float.NaN, in fine), Is.EqualTo(RiposteVerdict.WindowClosed), "NaN clock");
        }

        // ---- kit ---------------------------------------------------------------------------------------

        [Test]
        public void Kit_ShieldNonSwordRangedAndUnauthoredNeverRiposte()
        {
            Assert.That(RiposteRules.KitCanRiposte(true, false, false, false, 2), Is.True, "a flagged sword");
            Assert.That(RiposteRules.KitCanRiposte(false, false, false, false, 2), Is.False, "not flagged in data");
            Assert.That(RiposteRules.KitCanRiposte(true, true, false, false, 2), Is.False, "the weapon is a shield");
            Assert.That(RiposteRules.KitCanRiposte(true, false, true, false, 2), Is.False, "a shield contributes to the guard");
            Assert.That(RiposteRules.KitCanRiposte(true, false, true, false, 2, offhandShieldAllowed: true), Is.True, "a shield in the off hand, when the sword's data allows it");
            Assert.That(RiposteRules.KitCanRiposte(true, true, true, false, 2, offhandShieldAllowed: true), Is.False, "a shield itself never parries, whatever the flag");
            Assert.That(RiposteRules.KitCanRiposte(false, false, true, false, 2, offhandShieldAllowed: true), Is.False, "an unflagged weapon with a shield stays out");
            Assert.That(RiposteRules.KitCanRiposte(true, false, false, true, 2), Is.False, "ranged");
            Assert.That(RiposteRules.KitCanRiposte(true, false, false, false, 0), Is.False, "no strikes authored");
        }

        [Test]
        public void Kit_AShieldSwappedInAfterTheBlockRefusesTheRiposte()
        {
            RiposteTracker tracker = WithParry();
            RiposteRequest shield = Ok();
            shield.KitCanRiposte = RiposteRules.KitCanRiposte(true, false, true, false, 2);
            Assert.That(tracker.Check(Defender, T0 + 0.1f, in shield), Is.EqualTo(RiposteVerdict.KitCannotRiposte));
            Assert.That(tracker.HasLiveParry(Defender), Is.True, "a refusal changes nothing");
        }

        [Test]
        public void Kit_AChangedWeaponRefusesTheRiposte()
        {
            RiposteTracker tracker = WithParry();
            RiposteRequest swapped = Ok();
            swapped.WeaponEpoch = Epoch + 1;
            Assert.That(tracker.Check(Defender, T0 + 0.1f, in swapped), Is.EqualTo(RiposteVerdict.WeaponChanged));
        }

        // ---- parry recording ---------------------------------------------------------------------------

        [Test]
        public void IsParry_OnlyAHeldMeleeBlockOfASwordKitCounts()
        {
            Assert.That(RiposteRules.IsParry(true, false, false, true), Is.True);
            Assert.That(RiposteRules.IsParry(false, false, false, true), Is.False, "not blocked");
            Assert.That(RiposteRules.IsParry(true, true, false, true), Is.False, "a broken guard did not hold");
            Assert.That(RiposteRules.IsParry(true, false, true, true), Is.False, "a projectile");
            Assert.That(RiposteRules.IsParry(true, false, false, false), Is.False, "a shield or non-sword kit");
        }

        [Test]
        public void NoParryRecorded_ForgedOrEarlyRequestIsRefused()
        {
            RiposteTracker tracker = new RiposteTracker();
            RiposteRequest req = Ok();
            Assert.That(tracker.Check(Defender, T0, in req), Is.EqualTo(RiposteVerdict.NoParry), "nobody ever blocked");
            Assert.That(tracker.Refused, Is.EqualTo(1));
            Assert.That(tracker.RefusalsFor(RiposteVerdict.NoParry), Is.EqualTo(1));
        }

        [Test]
        public void AParryBelongsToItsDefenderOnly()
        {
            RiposteTracker tracker = WithParry();
            RiposteRequest req = Ok();
            Assert.That(tracker.Check(OtherDefender, T0 + 0.1f, in req), Is.EqualTo(RiposteVerdict.NoParry), "another player cannot use it");
            Assert.That(tracker.Check(Defender, T0 + 0.1f, in req), Is.EqualTo(RiposteVerdict.Allowed), "the owner can");
            Assert.That(tracker.ParriedAttacker(Defender), Is.EqualTo(Attacker), "the block is recorded against the attacker");
            Assert.That(tracker.BlockTimeOf(Defender), Is.EqualTo(T0));
        }

        [Test]
        public void DamageAfterTheBlock_CancelsTheParry()
        {
            RiposteTracker tracker = WithParry();
            tracker.NoteDamage(Defender);
            RiposteRequest req = Ok();
            Assert.That(tracker.Check(Defender, T0 + 0.1f, in req), Is.EqualTo(RiposteVerdict.HitSinceBlock));
            Assert.That(tracker.HasLiveParry(Defender), Is.False);

            tracker.NoteBlock(Defender, Attacker, T0 + 0.5f, Epoch);
            Assert.That(tracker.Check(Defender, T0 + 0.6f, in req), Is.EqualTo(RiposteVerdict.Allowed), "a NEW successful block is a new parry");
        }

        [Test]
        public void DamageToSomeoneElse_DoesNotCancel()
        {
            RiposteTracker tracker = WithParry();
            tracker.NoteDamage(OtherDefender);
            RiposteRequest req = Ok();
            Assert.That(tracker.Check(Defender, T0 + 0.1f, in req), Is.EqualTo(RiposteVerdict.Allowed));
        }

        [Test]
        public void ANewBlockRestartsTheWindow()
        {
            RiposteTracker tracker = WithParry();
            RiposteRequest req = Ok();
            float later = T0 + req.Window * 0.8f;
            tracker.NoteBlock(Defender, Attacker + 1, later, Epoch);
            Assert.That(tracker.Check(Defender, later + req.Window, in req), Is.EqualTo(RiposteVerdict.Allowed), "measured from the newest block");
            Assert.That(tracker.ParriedAttacker(Defender), Is.EqualTo(Attacker + 1));
        }

        // ---- action locks -----------------------------------------------------------------------------

        [Test]
        public void DeadStaggeredAndBusyDefendersCannotRiposte()
        {
            RiposteTracker tracker = WithParry();
            RiposteRequest dead = Ok(); dead.Alive = false;
            RiposteRequest stagger = Ok(); stagger.Staggered = true;
            RiposteRequest busy = Ok(); busy.Busy = true;
            Assert.That(tracker.Check(Defender, T0 + 0.1f, in dead), Is.EqualTo(RiposteVerdict.Dead));
            Assert.That(tracker.Check(Defender, T0 + 0.1f, in stagger), Is.EqualTo(RiposteVerdict.Staggered));
            Assert.That(tracker.Check(Defender, T0 + 0.1f, in busy), Is.EqualTo(RiposteVerdict.Busy));
            Assert.That(tracker.HasLiveParry(Defender), Is.True, "none of those spent the parry");
        }

        // ---- one parry, one riposte ---------------------------------------------------------------------

        [Test]
        public void OneParryBuysAtMostOneRiposte()
        {
            RiposteTracker tracker = WithParry();
            RiposteRequest req = Ok();
            Assert.That(tracker.Check(Defender, T0 + 0.1f, in req), Is.EqualTo(RiposteVerdict.Allowed));
            tracker.Commit(Defender, 2, T0 + 1f, 0.1f);

            Assert.That(tracker.Check(Defender, T0 + 0.2f, in req), Is.EqualTo(RiposteVerdict.AlreadyUsed), "a duplicate press");
            Assert.That(tracker.Check(Defender, T0 + req.Window, in req), Is.EqualTo(RiposteVerdict.AlreadyUsed), "a replay inside the window");
            Assert.That(tracker.RipostesStarted, Is.EqualTo(1));

            tracker.NoteBlock(Defender, Attacker, T0 + 3f, Epoch);
            Assert.That(tracker.Check(Defender, T0 + 3.1f, in req), Is.EqualTo(RiposteVerdict.Allowed), "the next parry buys the next riposte");
        }

        [Test]
        public void ACheckThatIsRefusedOrNotCommittedKeepsTheParry()
        {
            RiposteTracker tracker = WithParry();
            RiposteRequest req = Ok();
            for (int i = 0; i < 5; i++) Assert.That(tracker.Check(Defender, T0 + 0.1f, in req), Is.EqualTo(RiposteVerdict.Allowed));
            Assert.That(tracker.RipostesStarted, Is.EqualTo(0), "Check alone never spends it (a swing the authority refuses leaves the parry)");
        }

        // ---- the strike chain --------------------------------------------------------------------------

        [Test]
        public void FollowUpStrike_WaitsForTheGap_ThenIsAllowed_ThenTheRiposteEnds()
        {
            const float gap = 0.125f;
            float swingEnd = T0 + 1f;
            RiposteTracker tracker = WithParry();
            RiposteRequest req = Ok();
            tracker.Commit(Defender, 2, swingEnd, gap);
            Assert.That(tracker.HasGrant(Defender), Is.True);
            Assert.That(tracker.StrikesDone(Defender), Is.EqualTo(1));

            Assert.That(tracker.CheckContinue(Defender, swingEnd, in req), Is.EqualTo(RiposteVerdict.TooEarly), "the gap has not passed");
            Assert.That(tracker.CheckContinue(Defender, swingEnd + gap - 0.01f, in req), Is.EqualTo(RiposteVerdict.TooEarly));
            Assert.That(tracker.Refused, Is.EqualTo(0), "an early retry is not a refusal");
            Assert.That(tracker.CheckContinue(Defender, swingEnd + gap, in req), Is.EqualTo(RiposteVerdict.Allowed), "on the gap");

            tracker.CommitContinue(Defender, swingEnd + gap + 1f, gap);
            Assert.That(tracker.StrikesContinued, Is.EqualTo(1));
            Assert.That(tracker.StrikesDone(Defender), Is.EqualTo(2), "two strikes in all");
            Assert.That(tracker.HasGrant(Defender), Is.False, "the riposte is over after its last strike");
            Assert.That(tracker.CheckContinue(Defender, swingEnd + gap + 2f, in req), Is.EqualTo(RiposteVerdict.NoGrant), "no third strike");
        }

        [Test]
        public void FollowUpStrike_ComesTooLate_TheRiposteIsOver()
        {
            const float gap = 0.125f;
            float swingEnd = T0 + 1f;
            RiposteTracker tracker = WithParry();
            RiposteRequest req = Ok();
            tracker.Commit(Defender, 2, swingEnd, gap);
            float deadline = swingEnd + gap + RiposteTracker.ContinueGraceSeconds;
            Assert.That(tracker.CheckContinue(Defender, deadline, in req), Is.EqualTo(RiposteVerdict.Allowed), "exactly at the end of the grace");
            Assert.That(tracker.CheckContinue(Defender, deadline + 0.01f, in req), Is.EqualTo(RiposteVerdict.GrantExpired));
        }

        [Test]
        public void FollowUpStrike_AHitOnTheDefenderBetweenStrikesInterruptsTheRiposte()
        {
            RiposteTracker tracker = WithParry();
            RiposteRequest req = Ok();
            tracker.Commit(Defender, 2, T0 + 1f, 0.1f);
            tracker.NoteDamage(Defender);
            Assert.That(tracker.CheckContinue(Defender, T0 + 1.1f, in req), Is.EqualTo(RiposteVerdict.NoGrant));
        }

        [Test]
        public void FollowUpStrike_NeedsALiveRiposteAndTheSameKit()
        {
            RiposteTracker tracker = WithParry();
            RiposteRequest req = Ok();
            Assert.That(tracker.CheckContinue(Defender, T0 + 0.5f, in req), Is.EqualTo(RiposteVerdict.NoGrant), "a forged follow-up without a first strike");
            tracker.Commit(Defender, 3, T0 + 1f, 0f);
            RiposteRequest swapped = Ok(); swapped.WeaponEpoch = Epoch + 1;
            RiposteRequest dead = Ok(); dead.Alive = false;
            RiposteRequest stagger = Ok(); stagger.Staggered = true;
            Assert.That(tracker.CheckContinue(Defender, T0 + 1f, in swapped), Is.EqualTo(RiposteVerdict.WeaponChanged));
            Assert.That(tracker.CheckContinue(Defender, T0 + 1f, in dead), Is.EqualTo(RiposteVerdict.Dead));
            Assert.That(tracker.CheckContinue(Defender, T0 + 1f, in stagger), Is.EqualTo(RiposteVerdict.Staggered));
            Assert.That(tracker.CheckContinue(Defender, T0 + 1f, in req), Is.EqualTo(RiposteVerdict.Allowed));
        }

        [Test]
        public void StrikeCount_FollowsTheAuthoredTotal()
        {
            foreach (int total in new[] { 1, 2, 3 })
            {
                RiposteTracker tracker = WithParry();
                RiposteRequest req = Ok();
                tracker.Commit(Defender, total, T0 + 1f, 0f);
                int strikes = 1;
                while (tracker.CheckContinue(Defender, T0 + 1f, in req) == RiposteVerdict.Allowed)
                {
                    tracker.CommitContinue(Defender, T0 + 1f, 0f);
                    strikes++;
                    Assert.That(strikes, Is.LessThanOrEqualTo(total), "never more than authored");
                }

                Assert.That(strikes, Is.EqualTo(total));
            }
        }

        // ---- misc ------------------------------------------------------------------------------------

        [Test]
        public void StrikeMultiplier_IsTheDataValue_Sanitized()
        {
            foreach (float authored in new[] { 1.25f, 1.5f, 2f }) Assert.That(RiposteRules.StrikeMultiplier(authored), Is.EqualTo(authored));
            Assert.That(RiposteRules.StrikeMultiplier(float.NaN), Is.EqualTo(1f));
            Assert.That(RiposteRules.StrikeMultiplier(float.PositiveInfinity), Is.EqualTo(1f));
            Assert.That(RiposteRules.StrikeMultiplier(0f), Is.EqualTo(1f));
            Assert.That(RiposteRules.StrikeMultiplier(-2f), Is.EqualTo(1f));
        }

        [Test]
        public void WindowRemaining_CountsDownAndStopsAtZero()
        {
            RiposteTracker tracker = WithParry();
            const float window = 1f;
            Assert.That(tracker.WindowRemaining(Defender, T0, window), Is.EqualTo(window));
            Assert.That(tracker.WindowRemaining(Defender, T0 + 0.25f, window), Is.EqualTo(window - 0.25f));
            Assert.That(tracker.WindowRemaining(Defender, T0 + window + 1f, window), Is.EqualTo(0f));
            Assert.That(tracker.WindowRemaining(OtherDefender, T0, window), Is.EqualTo(0f));
        }

        [Test]
        public void Prune_DropsTheRecordsOfEntitiesThatAreGone()
        {
            RiposteTracker tracker = WithParry();
            tracker.NoteBlock(OtherDefender, Attacker, T0, Epoch);
            tracker.Prune(id => id == Defender);
            Assert.That(tracker.EntryCount, Is.EqualTo(1));
            Assert.That(tracker.HasLiveParry(Defender), Is.True);
            Assert.That(tracker.HasLiveParry(OtherDefender), Is.False);
            tracker.Forget(Defender);
            Assert.That(tracker.EntryCount, Is.EqualTo(0));
        }
    }
}
