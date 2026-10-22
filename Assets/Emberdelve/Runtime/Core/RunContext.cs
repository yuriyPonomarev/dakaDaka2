using System;
using UnityEngine;
using Emberdelve.Data;

namespace Emberdelve.Core
{
    public readonly struct RunResult
    {
        public readonly RunState Outcome;
        public readonly string ClassName;
        public readonly float LootValue;
        public readonly int ItemsCollected;
        public readonly int EnemiesSlain;
        public readonly float DurationSeconds;

        public RunResult(RunState outcome, string className, float lootValue, int itemsCollected, int enemiesSlain, float duration)
        {
            Outcome = outcome; ClassName = className; LootValue = lootValue;
            ItemsCollected = itemsCollected; EnemiesSlain = enemiesSlain; DurationSeconds = duration;
        }
    }

    /// <summary>
    /// Carries the player's chosen class and run bookkeeping between scenes.
    /// Today it is filled locally in the lobby. In a networked build this same struct would be
    /// produced by the session/lobby service and handed to the client, so nothing downstream changes.
    /// </summary>
    public static class RunContext
    {
        public static CharacterClassData SelectedClass { get; private set; }
        public static RunResult LastResult { get; private set; }
        public static bool HasResult { get; private set; }

        // ------------------------------------------------------------------ raid seed (R5)

        /// <summary>Command-line switch (development builds and the editor only): <c>-emberdelveRaidSeed 12345</c>.</summary>
        public const string RaidSeedArgument = "-emberdelveRaidSeed";

        private static bool _hasPendingRaidSeed;
        private static ulong _pendingRaidSeed;
        private static long _entropyCounter;

        /// <summary>
        /// Chooses the seed of the NEXT raid explicitly (a lobby / server / test). It is consumed by exactly one
        /// <see cref="ResolveRaidSeed"/>; the raid after it falls back to the default, so a result-screen retry never
        /// silently replays the previous seed.
        /// </summary>
        public static void SetNextRaidSeed(ulong seed)
        {
            _pendingRaidSeed = seed;
            _hasPendingRaidSeed = true;
        }

        public static bool HasPendingRaidSeed => _hasPendingRaidSeed;

        /// <summary>Drops a seed set by <see cref="SetNextRaidSeed"/> that no raid consumed (test hygiene).</summary>
        public static void ClearNextRaidSeed() => _hasPendingRaidSeed = false;

        /// <summary>
        /// The seed for the raid that is starting now, and where it came from. Order: 1. the seed set with
        /// <see cref="SetNextRaidSeed"/> (consumed); 2. in the editor and development builds only, the
        /// <c>-emberdelveRaidSeed N</c> argument or the <c>EMBERDELVE_RAID_SEED</c> environment variable (a fixed seed
        /// for reproducing a bug or a gate); 3. a high-entropy default from <see cref="DefaultRaidSeed"/>.
        /// </summary>
        public static ulong ResolveRaidSeed(out string source)
        {
            if (_hasPendingRaidSeed)
            {
                _hasPendingRaidSeed = false;
                source = "explicit";
                return _pendingRaidSeed;
            }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++)
            {
                if (args[i] == RaidSeedArgument && ulong.TryParse(args[i + 1], out ulong fromArgument))
                {
                    source = "command line";
                    return fromArgument;
                }
            }

            string fromEnvironment = Environment.GetEnvironmentVariable("EMBERDELVE_RAID_SEED");
            if (!string.IsNullOrEmpty(fromEnvironment) && ulong.TryParse(fromEnvironment, out ulong envSeed))
            {
                source = "environment";
                return envSeed;
            }
#endif

            source = "entropy";
            return DefaultRaidSeed();
        }

        /// <summary>
        /// 64 bits mixed from the operating system's GUID source (a cryptographic random on every supported
        /// platform), the clock and a per-process counter. The counter guarantees two raids started in one
        /// process in the same tick still differ even if the GUID source were poor.
        /// </summary>
        public static ulong DefaultRaidSeed()
        {
            byte[] bytes = Guid.NewGuid().ToByteArray();
            ulong a = BitConverter.ToUInt64(bytes, 0);
            ulong b = BitConverter.ToUInt64(bytes, 8);
            long counter = System.Threading.Interlocked.Increment(ref _entropyCounter);
            ulong h = Emberdelve.Core.RngMix.Combine(a, b);
            h = Emberdelve.Core.RngMix.Combine(h, unchecked((ulong)DateTime.UtcNow.Ticks));
            return Emberdelve.Core.RngMix.Combine(h, unchecked((ulong)counter));
        }

        public static void SelectClass(CharacterClassData data)
        {
            SelectedClass = data;
            HasResult = false;
        }

        public static void CompleteRun(RunResult result)
        {
            LastResult = result;
            HasResult = true;
        }

        public static CharacterClassData GetSelectedClassOrDefault(CharacterClassData fallback)
        {
            if (SelectedClass != null) return SelectedClass;
            SelectedClass = fallback;
            return SelectedClass;
        }
    }
}
