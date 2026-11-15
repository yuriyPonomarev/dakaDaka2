namespace Emberdelve.Flow
{
    public readonly struct PlayerRaidClock
    {
        public readonly bool Enabled, Expired, Waiting;
        public readonly float RemainingSeconds;
        public readonly byte Floor;
        public readonly uint MovementGeneration;
        public PlayerRaidClock(bool enabled, float remaining, bool expired, byte floor = 0, bool waiting = false, uint generation = 0)
        { Enabled = enabled; RemainingSeconds = remaining; Expired = expired; Floor = floor; Waiting = waiting; MovementGeneration = generation; }
    }
}
