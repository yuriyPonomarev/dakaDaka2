using System;
using System.Text;

namespace Emberdelve.Core
{
    /// <summary>
    /// The generator contract every loot / drop / identity roll in the project consumes. It exists so the roller
    /// and the tables depend on an interface (a gate can inject a scripted generator) instead of on
    /// <c>System.Random</c> or <c>UnityEngine.Random</c>, neither of which is allowed in a gameplay-outcome path.
    /// </summary>
    public interface ILootRng
    {
        /// <summary>Next 64 uniformly distributed bits.</summary>
        ulong NextUInt64();

        /// <summary>Next 32 uniformly distributed bits (the high half of a 64-bit draw).</summary>
        uint NextUInt32();

        /// <summary>Uniform integer in [0, maxExclusive). <paramref name="maxExclusive"/> must be positive.</summary>
        int Next(int maxExclusive);

        /// <summary>Uniform integer in [minInclusive, maxExclusive).</summary>
        int Next(int minInclusive, int maxExclusive);

        /// <summary>Uniform double in [0, 1) with 53 random bits.</summary>
        double NextDouble();
    }

    /// <summary>
    /// The project-owned deterministic generator: SplitMix64 with an explicit 64-bit state.
    ///
    /// Why this one: it is a dozen integer operations, every step is a 64-bit unsigned multiply / xor / shift
    /// (identical on every platform and runtime, no float, no platform library, no hidden global state), it has
    /// published reference vectors (seed 0 gives 0xE220A8397B1DCDAF first) and it accepts any 64-bit seed
    /// including 0. It is NOT cryptographic, which is fine: the secret of a raid is its seed, not the stream.
    ///
    /// Integer paths (<see cref="Next(int)"/>, <see cref="Next(int,int)"/>, <see cref="NextUInt64"/>) use only
    /// integer arithmetic and unbiased rejection sampling, never a floating-point branch. <see cref="NextDouble"/>
    /// is (bits >> 11) * 2^-53, an exact IEEE-754 operation, so it is also reproducible; it is used only where a
    /// weighted float table needs a ticket.
    ///
    /// Streams: <see cref="Fork"/> derives an independent generator from the ORIGINAL seed and a stream id, not
    /// from the current state, so a stream's content never depends on how much another stream consumed. A table
    /// that gains an entry cannot shift the numbers of a sibling stream.
    /// </summary>
    public sealed class DeterministicRng : ILootRng
    {
        private const ulong Golden = 0x9E3779B97F4A7C15UL;

        private readonly ulong _seed;
        private ulong _state;

        public DeterministicRng(ulong seed)
        {
            _seed = seed;
            _state = seed;
        }

        /// <summary>A generator seeded from a 32-bit seed (zero-extended, so int seeds map to distinct 64-bit seeds).</summary>
        public static DeterministicRng FromInt(int seed) => new DeterministicRng(unchecked((uint)seed));

        /// <summary>The seed this generator started from (the state advances, the seed does not).</summary>
        public ulong Seed => _seed;

        public ulong NextUInt64()
        {
            unchecked
            {
                _state += Golden;
                return RngMix.Finalize(_state);
            }
        }

        public uint NextUInt32() => (uint)(NextUInt64() >> 32);

        public int Next(int maxExclusive)
        {
            if (maxExclusive <= 0) throw new ArgumentOutOfRangeException(nameof(maxExclusive), "must be positive");
            if (maxExclusive == 1) return 0;

            // Unbiased: discard the first (2^64 mod range) values so every residue has the same count.
            ulong range = (uint)maxExclusive;
            ulong threshold = unchecked((0UL - range) % range);
            ulong x;
            do { x = NextUInt64(); } while (x < threshold);
            return (int)(x % range);
        }

        public int Next(int minInclusive, int maxExclusive)
        {
            if (maxExclusive <= minInclusive)
                throw new ArgumentOutOfRangeException(nameof(maxExclusive), "must be greater than minInclusive");

            ulong span = unchecked((ulong)((long)maxExclusive - minInclusive));
            if (span == 1UL) return minInclusive;

            ulong threshold = unchecked((0UL - span) % span);
            ulong x;
            do { x = NextUInt64(); } while (x < threshold);
            return unchecked((int)(minInclusive + (long)(x % span)));
        }

        public double NextDouble() => (NextUInt64() >> 11) * (1.0 / 9007199254740992.0);

        /// <summary>
        /// An independent generator for <paramref name="streamId"/>, derived from this generator's original seed
        /// (see <see cref="RngMix.Combine"/>). Calling it never advances this generator, and the same id always
        /// gives the same stream.
        /// </summary>
        public DeterministicRng Fork(ulong streamId) => new DeterministicRng(RngMix.Combine(_seed, streamId));

        /// <summary>A fresh generator with the same seed, restarted from its first value.</summary>
        public DeterministicRng Restart() => new DeterministicRng(_seed);
    }

    /// <summary>
    /// The integer mixing functions all seed derivation uses. They are pure functions of their arguments, so a
    /// derived seed is a documented formula rather than a side effect of a generator.
    /// </summary>
    public static class RngMix
    {
        private const ulong Golden = 0x9E3779B97F4A7C15UL;

        /// <summary>SplitMix64 finalizer: a bijective avalanche over 64 bits.</summary>
        public static ulong Finalize(ulong z)
        {
            unchecked
            {
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                return z ^ (z >> 31);
            }
        }

        /// <summary>
        /// Combines two 64-bit values into one: <c>Finalize(Finalize(a ^ G) + b + G)</c> with G the golden-ratio
        /// constant. Not commutative (Combine(a,b) != Combine(b,a)), so a (raid, chest) pair and a (chest, raid)
        /// pair do not collide, and each argument avalanches into every output bit.
        /// </summary>
        public static ulong Combine(ulong a, ulong b)
        {
            unchecked { return Finalize(Finalize(a ^ Golden) + b + Golden); }
        }

        /// <summary>FNV-1a 64-bit over the UTF-8 bytes of <paramref name="text"/> (null and empty hash alike).</summary>
        public static ulong HashString(string text)
        {
            unchecked
            {
                ulong hash = 14695981039346656037UL;
                if (string.IsNullOrEmpty(text)) return hash;

                byte[] bytes = Encoding.UTF8.GetBytes(text);
                for (int i = 0; i < bytes.Length; i++)
                {
                    hash ^= bytes[i];
                    hash *= 1099511628211UL;
                }
                return hash;
            }
        }
    }
}
