namespace Emberdelve.Net.Rules
{
    /// <summary>
    /// U3B3: what a client may ask for on the weapon-combat channel. A request is a KIND and nothing else: there is no aim, no
    /// charge, no target, no damage, no weapon id and no time in it (the server uses its own pose, its own draw clock and its own
    /// kit). The numeric values are the wire values (one byte); 0 and anything above <see cref="CombatNetLimits.KindMax"/> are invalid.
    /// </summary>
    public enum CombatIntentKind : byte
    {
        None = 0,
        /// <summary>Primary button went down: begin the next melee swing, or (nothing else) a no-op for a ranged weapon.</summary>
        AttackPress = 1,
        /// <summary>Guard button went down (level, not edge): the server raises the guard if the kit and the flow allow it.</summary>
        GuardDown = 2,
        /// <summary>Guard button released.</summary>
        GuardUp = 3,
        /// <summary>Primary button went down with a ranged weapon: the server starts ITS OWN draw clock.</summary>
        DrawStart = 4,
        /// <summary>Primary button released: the server measures the charge from its own clock and fires or refuses.</summary>
        DrawRelease = 5,
        /// <summary>Weapon set swap key (the authority's SwapWeaponSet command decides).</summary>
        SwapWeapon = 6,
        /// <summary>Refresh a short-lived held melee intent. Server timing still decides each swing.</summary>
        AttackHold = 7,
        /// <summary>Release held melee intent without cancelling an already committed swing.</summary>
        AttackRelease = 8,
        /// <summary>Request server-owned reload of the equipped ranged weapon.</summary>
        Reload = 9,
        /// <summary>Cancel an uncommitted ranged draw without firing or changing ammo, guard, or reload.</summary>
        DrawCancel = 10
    }

    /// <summary>Why the server did not do what a combat request (that passed the wire gate) asked. Nothing was spent for any value but None.</summary>
    public enum CombatRefusalCode : byte
    {
        None = 0,
        /// <summary>The sender's rig has no combat kit (no melee/ranged component).</summary>
        NoKit = 1,
        Dead = 2,
        /// <summary>The wielder is guarding: lower the guard before swinging or drawing.</summary>
        Guarding = 3,
        /// <summary>The weapon in hand is the other kind (a melee press with a bow, a draw with a sword).</summary>
        WrongWeapon = 4,
        /// <summary>The combat authority refused the swing or the launch (cooldown, sequence, aim, ammo, muzzle...). The reason text is logged on the server.</summary>
        Authority = 5,
        /// <summary>A draw is already running.</summary>
        AlreadyDrawing = 6,
        /// <summary>Release without a draw.</summary>
        NotDrawing = 7,
        /// <summary>Released before the weapon's minimum charge: nothing is fired and no ammunition is spent.</summary>
        TooEarly = 8,
        /// <summary>No ammunition in the magazine, or a reload is running.</summary>
        NoAmmo = 9,
        /// <summary>The guard cannot be raised (no block capability, staggered, or the weapon is ranged).</summary>
        CannotGuard = 10,
        /// <summary>The weapon swap was refused (action lock, no second weapon).</summary>
        SwapRefused = 11,
        /// <summary>The process is a client: it never resolves combat.</summary>
        ClientRole = 12,
        /// <summary>The kind is not handled by this build.</summary>
        Unsupported = 13
    }

    /// <summary>What the server tells the owner about one combat request that passed the wire gate.</summary>
    public enum CombatOutcome : byte
    {
        /// <summary>The request changed server state (a swing began, a guard went up/down, a draw began, an arrow flew, a swap happened).</summary>
        Accepted = 0,
        /// <summary>The server refused it; <see cref="CombatRefusalCode"/> says why.</summary>
        Refused = 1
    }

    /// <summary>
    /// U3B3: numbers both sides of the combat channel agree on. Pure constants in the assembly the runtime (Emberdelve.Runtime references
    /// this assembly, never the reverse) and the wire layer both see.
    /// </summary>
    public static class CombatNetLimits
    {
        /// <summary>Largest valid <see cref="CombatIntentKind"/> value.</summary>
        public const byte KindMax = (byte)CombatIntentKind.DrawCancel;

        /// <summary>
        /// Client side: smallest gap between two sends. Above the server gate's refill period (1 / <see cref="CombatIntentGate.RequestsPerSecond"/>),
        /// so an honest client never reaches the Flood verdict. A request that cannot go out yet is QUEUED, never dropped (a lost
        /// GuardUp would leave the guard up), up to <see cref="ClientQueueLimit"/> entries.
        /// </summary>
        public const float ClientMinIntervalSeconds = 0.12f;

        public const int ClientQueueLimit = 8;

        /// <summary>
        /// A draw that is still held this long after it reached full charge is cancelled by the server (a client that vanished or
        /// never releases). The server keeps its own clock; this only bounds how long the state can be held.
        /// </summary>
        public const float MaxDrawOverholdSeconds = 20f;

        /// <summary>
        /// The development combat-abuse probe sends exactly this many VALID AttackPress intents on its own object (one, sequence 1). The gate derives
        /// the swings it expects from the abuser from this number (a valid press is a real swing; it just has no target).
        /// </summary>
        public const int AbuseProbeValidAttackPresses = 1;

        public static bool IsValidKind(int kind) { return kind >= 1 && kind <= KindMax; }
    }
}
