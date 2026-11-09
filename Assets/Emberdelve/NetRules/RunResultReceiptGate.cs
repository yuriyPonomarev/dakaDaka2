namespace Emberdelve.Net.Rules
{
    /// <summary>Per-player-object one-shot gate for an owner result received from the server.</summary>
    public sealed class RunResultReceiptGate
    {
        public bool HasReceived { get; private set; }

        public bool TryAccept(int expectedPlayerId, int resultPlayerId)
        {
            if (HasReceived || expectedPlayerId <= 0 || resultPlayerId != expectedPlayerId) return false;
            HasReceived = true;
            return true;
        }
    }
}
