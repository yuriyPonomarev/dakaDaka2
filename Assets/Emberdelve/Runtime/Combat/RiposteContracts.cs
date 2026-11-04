namespace Emberdelve.Combat
{
    /// <summary>U3C3: the answer to a riposte request. NotYet means "ask again" (the strike gap has not passed, nothing was consumed).</summary>
    public enum RiposteResult { Refused = 0, NotYet = 1, Started = 2 }

    /// <summary>
    /// U3C3: what the authority needs to know about an entity's guard to judge a riposte. Implemented by the player rig. An entity that
    /// does not implement it (an enemy) never records a parry and can never riposte.
    /// </summary>
    public interface IRiposteKit
    {
        /// <summary>True when a shield contributes to the guard. A shield never parries.</summary>
        bool GuardHasShield { get; }
    }

    /// <summary>
    /// U3C3: the sender-bound riposte commands. The client only ever sends the block press it always sent; the server turns it into one
    /// of these calls on the sender's own rig. The authority decides everything from the parry it recorded itself.
    /// </summary>
    public interface IRiposteAuthority
    {
        /// <summary>
        /// Starts the first riposte strike: refused unless the server recorded a successful sword block by this entity, inside the
        /// window (weapon data), with no damage taken since, a sword kit now, alive, not staggered, not swinging. One parry opens one riposte.
        /// The intent is the ordinary swing request (aim snapshot, sequence); its pattern and step are ignored: the weapon data picks both.
        /// </summary>
        bool TryBeginRiposte(CombatSenderId sender, in MeleeSwingIntent intent, out int swingId, out int strikesTotal, out string reason);

        /// <summary>Starts the next strike of the riposte this entity already opened. Refused without a live riposte; NotYet before the gap has passed.</summary>
        RiposteResult TryContinueRiposte(CombatSenderId sender, in MeleeSwingIntent intent, out int swingId, out string reason);
    }
}
