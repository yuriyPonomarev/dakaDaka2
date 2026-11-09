namespace Emberdelve.Net.Rules
{
    /// <summary>Client-side prompt guard; the server remains authoritative and repeats admission and geometry checks.</summary>
    public static class CorpseInteractionGate
    {
        public static bool CanRequest(bool clientOwnerReady, bool hostOwnerReady, bool actorIsLocal, bool actorAlive,
            out string reason)
        {
            if ((!clientOwnerReady && !hostOwnerReady) || !actorIsLocal || !actorAlive)
            {
                reason = "Corpse search is unavailable";
                return false;
            }
            reason = null;
            return true;
        }
    }
}
