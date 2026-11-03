namespace Emberdelve.Net.Rules
{
    /// <summary>
    /// U3B2: numbers every side of the skill channel must agree on. Pure constants (no engine or package dependency) in the
    /// assembly both the runtime kit (Emberdelve.Runtime references this assembly, never the reverse) and the wire layer see.
    /// </summary>
    public static class SkillNetLimits
    {
        /// <summary>
        /// Active skill slots on the wire. <c>SkillCaster.ActiveSlotCapacity</c> is declared FROM this constant, and
        /// <see cref="SkillIntentGate"/> bounds the slot of a request with it, so the gate, the caster and the replicated
        /// state cannot drift apart. The key bindings stay slot 0 = Skill1, slot 1 = Skill2.
        /// </summary>
        public const int ActiveSlots = 2;

        /// <summary>Longest skill id the observer visual and the owner result carry (longer ids are cut by the server).</summary>
        public const int MaxSkillIdChars = 32;

        /// <summary>
        /// Client side: smallest gap between two intents a client sends. Above the gate's refill period (1 / 4 s), so an honest
        /// key-masher never reaches the server's Flood verdict and never feeds its own reject bucket.
        /// </summary>
        public const float ClientMinIntervalSeconds = 0.3f;
    }

    /// <summary>Why the server's skill kit refused a request that passed the wire gate. Nothing was spent for any value but None.</summary>
    public enum SkillRefusalCode : byte
    {
        None = 0,
        NoSuchSlot = 1,
        NoSkill = 2,
        NoCaster = 3,
        Dead = 4,
        Cooldown = 5,
        Mana = 6,
        NoMana = 7,
        NoBehavior = 8,
        NoAuthority = 9,
        Requirement = 10,
        /// <summary>The skill's own CanCast refused (for example a dash that is already running).</summary>
        Behavior = 11,
        /// <summary>The combat authority refused the opening of the cast.</summary>
        Authority = 12,
        /// <summary>The cast opened but its effect did not run (a bug; the cast stays spent).</summary>
        Failed = 13,
        /// <summary>The process is a client: it never casts.</summary>
        ClientRole = 14
    }

    /// <summary>What the server tells the owner about one request that passed the wire gate.</summary>
    public enum SkillCastOutcome : byte
    {
        /// <summary>The cast happened: mana and cooldown were spent by the server.</summary>
        Cast = 0,
        /// <summary>The server refused it; <see cref="SkillRefusalCode"/> says why and nothing was spent.</summary>
        Refused = 1
    }
}
