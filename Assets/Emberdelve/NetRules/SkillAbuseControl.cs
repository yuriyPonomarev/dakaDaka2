using System;
using System.Collections.Generic;

namespace Emberdelve.Net.Rules
{
    /// <summary>What a rejected skill request was.</summary>
    public enum SkillRejectKind
    {
        /// <summary>The sender is not the owner of the object the call was made on (or has no admitted player).</summary>
        Identity = 0,
        /// <summary>Malformed (slot, sequence zero, jump past the window, non-finite or out-of-range clock).</summary>
        Invalid = 1,
        /// <summary>A sequence that is not newer than the last one the connection used.</summary>
        Replay = 2,
        /// <summary>Well formed but over the connection's request budget.</summary>
        Flood = 3
    }

    /// <summary>What the caller must do after recording one reject.</summary>
    public enum SkillRejectAction
    {
        /// <summary>Inside the reject allowance (the bucket still had a token).</summary>
        Counted = 0,
        /// <summary>Over the allowance: counted as excess, not yet at the disconnect threshold.</summary>
        Excess = 1,
        /// <summary>The excess reached the threshold: disconnect this client.</summary>
        Disconnect = 2
    }

    /// <summary>
    /// U3B2 abuse numbers (server side, configurable from the command line, see NetLaunchArgs). A client that is merely
    /// unlucky or buggy may be rejected a few times for free (<see cref="RejectBurst"/>, replenished at
    /// <see cref="RejectRefillPerSecond"/>); only rejects that arrive while that allowance is empty are "excess", and
    /// <see cref="DisconnectExcess"/> of them disconnect the client. A slot that was disconnected is remembered for
    /// <see cref="MemorySeconds"/> and returns on probation (see <see cref="ThresholdFor"/>).
    /// </summary>
    public sealed class SkillAbusePolicy
    {
        public const double DefaultRejectBurst = 8.0;
        public const double DefaultRejectRefillPerSecond = 1.0;
        public const int DefaultDisconnectExcess = 8;
        public const double DefaultMemorySeconds = 120.0;
        /// <summary>Strikes beyond this are not counted (the threshold is already 1 long before).</summary>
        public const int MaxStrikes = 8;
        /// <summary>Remembered slots at most; the oldest expiry is dropped first.</summary>
        public const int MaxMemoryEntries = 256;

        public double RejectBurst { get; }
        public double RejectRefillPerSecond { get; }
        public int DisconnectExcess { get; }
        public double MemorySeconds { get; }

        public SkillAbusePolicy(double rejectBurst, double rejectRefillPerSecond, int disconnectExcess, double memorySeconds)
        {
            RejectBurst = Clamp(rejectBurst, 1.0, 1000.0, DefaultRejectBurst);
            RejectRefillPerSecond = Clamp(rejectRefillPerSecond, 0.0, 1000.0, DefaultRejectRefillPerSecond);
            DisconnectExcess = Math.Max(1, Math.Min(100000, disconnectExcess));
            MemorySeconds = Clamp(memorySeconds, 0.0, 86400.0, DefaultMemorySeconds);
        }

        public static SkillAbusePolicy Default =>
            new SkillAbusePolicy(DefaultRejectBurst, DefaultRejectRefillPerSecond, DefaultDisconnectExcess, DefaultMemorySeconds);

        /// <summary>
        /// Excess rejects that disconnect a connection which has <paramref name="strikes"/> earlier abuse disconnects on record:
        /// the base threshold halved once per strike, never below 1.
        /// </summary>
        public int ThresholdFor(int strikes)
        {
            int shift = Math.Max(0, Math.Min(strikes, 30));
            return Math.Max(1, DisconnectExcess >> shift);
        }

        private static double Clamp(double value, double min, double max, double fallback)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return fallback;
            return Math.Max(min, Math.Min(max, value));
        }
    }

    /// <summary>
    /// Cheap token bucket for REJECTED skill traffic of one connection, consulted before anything expensive happens (it takes a
    /// kind and a server timestamp, nothing else). Every reject of every kind is counted; the bucket only decides when the
    /// counting turns into a disconnect. Pure. A connection that returns while its slot is remembered starts with an EMPTY bucket
    /// and a lower threshold (<c>strikes &gt; 0</c>), so a rejoin never hands a hostile client a fresh allowance.
    /// </summary>
    public sealed class SkillRejectLedger
    {
        private readonly SkillAbusePolicy _policy;
        private readonly int[] _counts = new int[4];
        private double _tokens;
        private double _lastTime;

        public SkillRejectLedger(SkillAbusePolicy policy, int strikes, double startTime)
        {
            _policy = policy ?? SkillAbusePolicy.Default;
            Strikes = Math.Max(0, strikes);
            Threshold = _policy.ThresholdFor(Strikes);
            _tokens = Strikes > 0 ? 0.0 : _policy.RejectBurst;
            _lastTime = double.IsNaN(startTime) || double.IsInfinity(startTime) ? 0.0 : startTime;
        }

        /// <summary>Earlier abuse disconnects of this slot that were still remembered when the connection was admitted.</summary>
        public int Strikes { get; }
        /// <summary>Excess rejects that disconnect this connection.</summary>
        public int Threshold { get; }
        public int Total { get; private set; }
        public int Excess { get; private set; }
        public bool Tripped { get; private set; }
        public double Tokens => _tokens;

        public int Count(SkillRejectKind kind)
        {
            int index = (int)kind;
            return index >= 0 && index < _counts.Length ? _counts[index] : 0;
        }

        public SkillRejectAction Record(SkillRejectKind kind, double now)
        {
            int index = (int)kind;
            if (index >= 0 && index < _counts.Length) _counts[index]++;
            Total++;

            if (!double.IsNaN(now) && !double.IsInfinity(now) && now > _lastTime)
            {
                _tokens = Math.Min(_policy.RejectBurst, _tokens + (now - _lastTime) * _policy.RejectRefillPerSecond);
                _lastTime = now;
            }

            if (_tokens >= 1.0 && !Tripped)
            {
                _tokens -= 1.0;
                return SkillRejectAction.Counted;
            }

            Excess++;
            if (Excess >= Threshold) Tripped = true;
            return Tripped ? SkillRejectAction.Disconnect : SkillRejectAction.Excess;
        }

        /// <summary>True when the reject of this kind that was just recorded should be written to the log (rate limited per kind).</summary>
        public bool ShouldLog(SkillRejectKind kind)
        {
            return NetLogThrottle.ShouldLog(Count(kind));
        }
    }

    /// <summary>
    /// Server-wide memory of slots (dev identities from the connect payload, NOT authentication) that were disconnected for abuse.
    /// Survives the connection and the actor: a slot that returns within <see cref="SkillAbusePolicy.MemorySeconds"/> of its last
    /// strike is admitted on probation. Every new strike renews the window; once a window has passed without a strike the slot is
    /// forgotten. Pure: the caller supplies the server's monotonic clock.
    /// </summary>
    public sealed class SkillAbuseMemory
    {
        private sealed class Entry
        {
            public int Strikes;
            public double Until;
        }

        private readonly SkillAbusePolicy _policy;
        private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        public SkillAbuseMemory(SkillAbusePolicy policy)
        {
            _policy = policy ?? SkillAbusePolicy.Default;
        }

        public int Count => _entries.Count;

        /// <summary>Records an abuse disconnect of <paramref name="slot"/>; returns its strike count now (0 for an unusable slot).</summary>
        public int RecordDisconnect(string slot, double now)
        {
            if (string.IsNullOrEmpty(slot) || double.IsNaN(now) || double.IsInfinity(now)) return 0;

            if (!_entries.TryGetValue(slot, out Entry entry) || now >= entry.Until)
            {
                entry = new Entry();
                _entries[slot] = entry;
            }

            entry.Strikes = Math.Min(SkillAbusePolicy.MaxStrikes, entry.Strikes + 1);
            entry.Until = now + _policy.MemorySeconds;
            if (_entries.Count > SkillAbusePolicy.MaxMemoryEntries) Prune(now);
            return entry.Strikes;
        }

        /// <summary>Strikes still remembered for <paramref name="slot"/> at <paramref name="now"/>; 0 when none or expired (and then forgotten).</summary>
        public int StrikesFor(string slot, double now)
        {
            if (string.IsNullOrEmpty(slot) || !_entries.TryGetValue(slot, out Entry entry)) return 0;
            if (now >= entry.Until)
            {
                _entries.Remove(slot);
                return 0;
            }

            return entry.Strikes;
        }

        /// <summary>Seconds until the remembered strikes of <paramref name="slot"/> are forgotten (0 when nothing is remembered).</summary>
        public double SecondsLeft(string slot, double now)
        {
            if (string.IsNullOrEmpty(slot) || !_entries.TryGetValue(slot, out Entry entry)) return 0.0;
            return Math.Max(0.0, entry.Until - now);
        }

        private void Prune(double now)
        {
            List<string> expired = new List<string>();
            string oldest = null;
            double oldestUntil = double.MaxValue;
            foreach (KeyValuePair<string, Entry> pair in _entries)
            {
                if (now >= pair.Value.Until) expired.Add(pair.Key);
                else if (pair.Value.Until < oldestUntil)
                {
                    oldestUntil = pair.Value.Until;
                    oldest = pair.Key;
                }
            }

            for (int i = 0; i < expired.Count; i++) _entries.Remove(expired[i]);
            if (_entries.Count > SkillAbusePolicy.MaxMemoryEntries && oldest != null) _entries.Remove(oldest);
        }
    }

    /// <summary>Rate limit for repetitive log lines: the first three, then powers of two, then every hundredth (the movement server's rule).</summary>
    public static class NetLogThrottle
    {
        public static bool ShouldLog(int count)
        {
            return count >= 1 && (count <= 3 || (count & (count - 1)) == 0 || count % 100 == 0);
        }
    }
}
