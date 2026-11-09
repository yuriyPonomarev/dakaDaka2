namespace Emberdelve.Net.Rules
{
    /// <summary>One strictly increasing request sequence for state and transfer intents on a connection.</summary>
    public sealed class CorpseRequestSequenceGate
    {
        public const uint MaximumSequence = 0x7fffffff;
        public uint LastAccepted { get; private set; }

        public bool TryAccept(uint sequence, out string reason)
        {
            if (sequence == 0 || sequence > MaximumSequence || LastAccepted == MaximumSequence || sequence != LastAccepted + 1)
            {
                reason = "corpse request sequence is stale, skipped, or exhausted";
                return false;
            }
            LastAccepted = sequence;
            reason = null;
            return true;
        }
    }
}
