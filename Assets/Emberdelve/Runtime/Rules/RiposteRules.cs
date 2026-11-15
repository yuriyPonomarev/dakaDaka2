using System;
using System.Collections.Generic;

namespace Emberdelve.BladeDefense
{
    /// <summary>Why a riposte request was or was not allowed. Allowed is 0; every other value is a refusal (nothing changes on a refusal).</summary>
    public enum RiposteVerdict
    {
        Allowed = 0,
        /// <summary>The server never recorded a successful sword block for this player (forged, early, or the parry was never made).</summary>
        NoParry = 1,
        /// <summary>The recorded parry is older than the window (data).</summary>
        WindowClosed = 2,
        /// <summary>The defender took damage after the block: the parry was cancelled.</summary>
        HitSinceBlock = 3,
        /// <summary>The parry already paid for its one riposte (a replay or a duplicate press).</summary>
        AlreadyUsed = 4,
        /// <summary>The kit in hand cannot riposte now (a shield, a non-sword, or no riposte data).</summary>
        KitCannotRiposte = 5,
        /// <summary>The weapon changed since the block.</summary>
        WeaponChanged = 6,
        Dead = 7,
        /// <summary>A broken guard staggers the player (action lock).</summary>
        Staggered = 8,
        /// <summary>A swing is running (action lock).</summary>
        Busy = 9,
        /// <summary>A follow-up strike was asked for without a live riposte (never started, finished, or cancelled).</summary>
        NoGrant = 10,
        /// <summary>The follow-up strike's gap has not passed yet. The caller may ask again; nothing was consumed.</summary>
        TooEarly = 11,
        /// <summary>The follow-up strike came too late: the riposte is over.</summary>
        GrantExpired = 12
    }

    /// <summary>What the caller (the authority) knows about the defender at the instant of a riposte request. Plain values; nothing here reads a scene.</summary>
    public struct RiposteRequest
    {
        /// <summary>The defender's CURRENT kit: sword-class weapon (data flag), not a shield, melee, with riposte strikes authored.</summary>
        public bool KitCanRiposte;
        public bool Alive;
        /// <summary>The guard is staggered (broken guard).</summary>
        public bool Staggered;
        /// <summary>The defender's own swing is running.</summary>
        public bool Busy;
        /// <summary>The window in seconds from the weapon data.</summary>
        public float Window;
        /// <summary>The weapon-change counter now (compared with the one recorded at the block).</summary>
        public int WeaponEpoch;
    }

    /// <summary>
    /// U3C3: the server-side memory of "successful sword block at time T by player P (against attacker A)". One parry buys at most ONE
    /// riposte. Pure and engine-free: the authority's clock and the facts about the defender come in as arguments, so every rule is a
    /// unit test. Per defender it holds a single record:
    ///   NoteBlock  -> Live (replaces whatever was there: a new successful block is a new parry)
    ///   NoteDamage -> a Live parry becomes Hit (cancelled); a running riposte grant is cancelled too
    ///   Commit     -> Live becomes Used and a grant for the follow-up strikes opens
    /// <see cref="Check"/> never mutates (a refused or failed begin keeps the parry); <see cref="Commit"/> is called only after the swing
    /// really began. The window is inclusive: a press exactly <c>Window</c> seconds after the block is still in time.
    /// </summary>
    public sealed class RiposteTracker
    {
        /// <summary>How long after its earliest moment a follow-up strike may still start (seconds). A rule constant, not data: it only bounds how long the grant lives.</summary>
        public const float ContinueGraceSeconds = 0.5f;

        private enum State { None, Live, Used, Hit }

        private sealed class Entry
        {
            public State State;
            public float BlockTime;
            public int Attacker;
            public int WeaponEpoch;
            public bool ParryBonus;

            public bool GrantActive;
            public int StrikesTotal;
            public int StrikesDone;
            public float Earliest;
            public float Deadline;
        }

        private readonly Dictionary<int, Entry> _entries = new Dictionary<int, Entry>();
        private readonly List<int> _scratch = new List<int>();
        private readonly int[] _refusals = new int[Enum.GetValues(typeof(RiposteVerdict)).Length];

        /// <summary>Successful blocks recorded.</summary>
        public int BlocksRecorded { get; private set; }
        /// <summary>Riposte starts accepted (first strike began).</summary>
        public int RipostesStarted { get; private set; }
        /// <summary>Follow-up strikes accepted.</summary>
        public int StrikesContinued { get; private set; }
        /// <summary>Requests refused, any reason (a TooEarly retry counts too).</summary>
        public int Refused { get; private set; }

        public int RefusalsFor(RiposteVerdict verdict) => _refusals[(int)verdict];

        public int EntryCount => _entries.Count;

        /// <summary>Legacy window query: true for a live eligible block, including an ordinary block. Use HasParryBonus to distinguish parries.</summary>
        public bool HasLiveParry(int defender) => _entries.TryGetValue(defender, out Entry e) && e.State == State.Live;

        /// <summary>The attacker whose hit the defender last parried (-1 when none was recorded).</summary>
        public int ParriedAttacker(int defender) => _entries.TryGetValue(defender, out Entry e) && e.State != State.None ? e.Attacker : -1;

        /// <summary>Authority time of the last recorded block (NaN when none).</summary>
        public float BlockTimeOf(int defender) => _entries.TryGetValue(defender, out Entry e) && e.State != State.None ? e.BlockTime : float.NaN;

        /// <summary>True while a riposte has strikes left to start.</summary>
        public bool HasGrant(int defender) => _entries.TryGetValue(defender, out Entry e) && e.GrantActive;

        public int StrikesDone(int defender) => _entries.TryGetValue(defender, out Entry e) ? e.StrikesDone : 0;

        /// <summary>Seconds left in the riposte window of the defender's Live parry (0 when none or closed).</summary>
        public float WindowRemaining(int defender, float now, float window)
        {
            if (!_entries.TryGetValue(defender, out Entry e) || e.State != State.Live) return 0f;
            float left = window - (now - e.BlockTime);
            return left > 0f ? left : 0f;
        }

        public bool HasParryBonus(int defender) => _entries.TryGetValue(defender, out Entry e)
            && (e.State == State.Live || e.GrantActive) && e.ParryBonus;

        public void NoteBlock(int defender, int attacker, float now, int weaponEpoch, bool parryBonus = false)
        {
            if (float.IsNaN(now) || float.IsInfinity(now)) return;
            Entry e = EntryFor(defender);
            e.State = State.Live;
            e.BlockTime = now;
            e.Attacker = attacker;
            e.WeaponEpoch = weaponEpoch;
            e.ParryBonus = parryBonus;
            e.GrantActive = false;
            BlocksRecorded++;
        }

        /// <summary>The defender took damage: a Live parry is cancelled and a running riposte stops. No-op for a defender with no record.</summary>
        public void NoteDamage(int defender)
        {
            if (!_entries.TryGetValue(defender, out Entry e)) return;
            if (e.State == State.Live) e.State = State.Hit;
            e.GrantActive = false;
        }

        /// <summary>Drops one defender's record (death, respawn, leaving the run).</summary>
        public void Forget(int defender) => _entries.Remove(defender);

        public void Clear() => _entries.Clear();

        /// <summary>Drops the records of defenders the predicate no longer knows (unregistered entities).</summary>
        public void Prune(Func<int, bool> isKnown)
        {
            if (_entries.Count == 0 || isKnown == null) return;
            _scratch.Clear();
            foreach (KeyValuePair<int, Entry> pair in _entries)
            {
                if (!isKnown(pair.Key)) _scratch.Add(pair.Key);
            }

            for (int i = 0; i < _scratch.Count; i++) _entries.Remove(_scratch[i]);
        }

        /// <summary>Would a riposte START now? Read only (refusals are counted).</summary>
        public RiposteVerdict Check(int defender, float now, in RiposteRequest req)
        {
            RiposteVerdict v = Evaluate(defender, now, in req);
            if (v != RiposteVerdict.Allowed) Count(v);
            return v;
        }

        private RiposteVerdict Evaluate(int defender, float now, in RiposteRequest req)
        {
            if (!_entries.TryGetValue(defender, out Entry e) || e.State == State.None) return RiposteVerdict.NoParry;
            if (e.State == State.Used) return RiposteVerdict.AlreadyUsed;
            if (e.State == State.Hit) return RiposteVerdict.HitSinceBlock;
            if (!req.Alive) return RiposteVerdict.Dead;
            if (!req.KitCanRiposte) return RiposteVerdict.KitCannotRiposte;
            if (req.WeaponEpoch != e.WeaponEpoch) return RiposteVerdict.WeaponChanged;
            if (req.Staggered) return RiposteVerdict.Staggered;
            if (req.Busy) return RiposteVerdict.Busy;

            float elapsed = now - e.BlockTime;
            if (float.IsNaN(elapsed) || float.IsNaN(req.Window) || elapsed < 0f || !(elapsed <= req.Window)) return RiposteVerdict.WindowClosed;
            return RiposteVerdict.Allowed;
        }

        /// <summary>
        /// The first strike really began: the parry is spent (one parry, one riposte) and a grant opens for the other
        /// <c>strikesTotal - 1</c> strikes. <paramref name="swingEnd"/> is the authority time the strike's swing ends; the next strike may start
        /// <paramref name="gap"/> seconds after it and for <see cref="ContinueGraceSeconds"/> more.
        /// </summary>
        public void Commit(int defender, int strikesTotal, float swingEnd, float gap)
        {
            if (!_entries.TryGetValue(defender, out Entry e) || e.State != State.Live) return;
            e.State = State.Used;
            RipostesStarted++;
            OpenGrant(e, strikesTotal, swingEnd, gap);
        }

        /// <summary>Would the NEXT strike of a running riposte start now? Read only; TooEarly means "ask again".</summary>
        public RiposteVerdict CheckContinue(int defender, float now, in RiposteRequest req)
        {
            RiposteVerdict v = EvaluateContinue(defender, now, in req);
            if (v != RiposteVerdict.Allowed && v != RiposteVerdict.TooEarly) Count(v);   // an early retry is not a refusal
            return v;
        }

        private RiposteVerdict EvaluateContinue(int defender, float now, in RiposteRequest req)
        {
            if (!_entries.TryGetValue(defender, out Entry e) || !e.GrantActive) return RiposteVerdict.NoGrant;
            if (!req.Alive) return RiposteVerdict.Dead;
            if (!req.KitCanRiposte) return RiposteVerdict.KitCannotRiposte;
            if (req.WeaponEpoch != e.WeaponEpoch) return RiposteVerdict.WeaponChanged;
            if (req.Staggered) return RiposteVerdict.Staggered;
            if (float.IsNaN(now) || now > e.Deadline) return RiposteVerdict.GrantExpired;
            if (now < e.Earliest) return RiposteVerdict.TooEarly;
            if (req.Busy) return RiposteVerdict.Busy;
            return RiposteVerdict.Allowed;
        }

        /// <summary>The follow-up strike really began.</summary>
        public void CommitContinue(int defender, float swingEnd, float gap)
        {
            if (!_entries.TryGetValue(defender, out Entry e) || !e.GrantActive) return;
            StrikesContinued++;
            OpenGrant(e, e.StrikesTotal, swingEnd, gap);
        }

        /// <summary>Ends a riposte whose follow-up can no longer happen (expired, refused for good).</summary>
        public void CancelGrant(int defender)
        {
            if (_entries.TryGetValue(defender, out Entry e)) e.GrantActive = false;
        }

        private static void OpenGrant(Entry e, int strikesTotal, float swingEnd, float gap)
        {
            e.StrikesTotal = strikesTotal;
            e.StrikesDone++;
            if (e.StrikesDone >= strikesTotal)
            {
                e.GrantActive = false;
                return;
            }

            float safeGap = float.IsNaN(gap) || float.IsInfinity(gap) || gap < 0f ? 0f : gap;
            e.GrantActive = true;
            e.Earliest = swingEnd + safeGap;
            e.Deadline = e.Earliest + ContinueGraceSeconds;
        }

        private Entry EntryFor(int defender)
        {
            if (!_entries.TryGetValue(defender, out Entry e))
            {
                e = new Entry();
                _entries[defender] = e;
            }

            e.StrikesDone = 0;
            return e;
        }

        private void Count(RiposteVerdict v)
        {
            Refused++;
            _refusals[(int)v]++;
        }
    }

    /// <summary>The numbers a riposte strike runs with, derived from the weapon data (never from a request).</summary>
    public static class RiposteRules
    {
        /// <summary>The damage multiplier of a riposte strike: the data value, sanitized (NaN, infinity and non-positive fall back to 1).</summary>
        public static float StrikeMultiplier(float authored)
        {
            if (float.IsNaN(authored) || float.IsInfinity(authored) || authored <= 0f) return 1f;
            return authored;
        }

        /// <summary>A kit may riposte when its weapon is flagged (data), is not a shield or ranged, and no shield contributes to the guard. The legacy allowance argument is ignored; a shield cannot grant another weapon's response.</summary>
        public static bool KitCanRiposte(bool weaponFlagged, bool weaponIsShield, bool guardHasShield, bool weaponIsRanged, int strikeCount, bool offhandShieldAllowed = false) =>
            weaponFlagged && !weaponIsShield && !guardHasShield && !weaponIsRanged && strikeCount > 0;

        /// <summary>
        /// A block opens an ordinary riposte when the guard HELD (blocked, not broken) against a melee hit and the kit can riposte. A broken
        /// guard, a miss, a hit from behind and a projectile are not parries.
        /// </summary>
        public static bool IsRiposteBlock(bool blocked, bool guardBroken, bool projectile, bool kitCanRiposte) =>
            blocked && !guardBroken && !projectile && kitCanRiposte;
    }
}
