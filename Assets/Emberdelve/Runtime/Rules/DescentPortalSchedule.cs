using System;

namespace Emberdelve.Rules
{
    public readonly struct DescentPortalWave
    {
        public readonly double AppearsAt;
        public readonly int Groups;
        public DescentPortalWave(double appearsAt, int groups) { AppearsAt = appearsAt; Groups = groups; }
    }

    /// <summary>Immutable authority-time schedule. Querying it never consumes or respawns a portal;
    /// runtime tracks usage separately. The final wave must arrive during the last minute.</summary>
    public sealed class DescentPortalSchedule
    {
        private readonly DescentPortalWave[] _waves;
        public double Duration { get; }
        public int PortalsPerGroup { get; }
        public int TotalGroups { get; }

        public DescentPortalSchedule(double duration, RoyalePartyMode mode, params DescentPortalWave[] waves)
        {
            if (!SwarmSchedule.Finite(duration) || duration <= 0) throw new ArgumentOutOfRangeException(nameof(duration));
            if (mode < RoyalePartyMode.Solo || mode > RoyalePartyMode.Trio) throw new ArgumentOutOfRangeException(nameof(mode));
            if (waves == null || waves.Length == 0) throw new ArgumentException("At least one portal wave is required", nameof(waves));
            Duration = duration; PortalsPerGroup = (int)mode;
            _waves = (DescentPortalWave[])waves.Clone();
            double previous = -1;
            int total = 0;
            foreach (var wave in _waves)
            {
                if (!SwarmSchedule.Finite(wave.AppearsAt) || wave.AppearsAt < 0 || wave.AppearsAt <= previous
                    || wave.AppearsAt >= duration || wave.Groups <= 0 || wave.Groups > 64 - total)
                    throw new ArgumentException("Invalid or unordered portal wave", nameof(waves));
                previous = wave.AppearsAt;
                total += wave.Groups;
            }
            if (previous < Math.Max(0, duration - 60))
                throw new ArgumentException("The last wave must appear in the final minute", nameof(waves));
            TotalGroups = total;
        }

        public bool TryGetAvailableGroups(double elapsed, out int groups)
        {
            groups = 0;
            if (!SwarmSchedule.Finite(elapsed) || elapsed < 0) return false;
            if (elapsed >= Duration) return true;
            foreach (var wave in _waves)
            {
                if (elapsed < wave.AppearsAt) break;
                groups += wave.Groups;
            }
            return true;
        }

        public bool IsGroupOpen(int groupIndex, double elapsed) => groupIndex >= 0
            && TryGetAvailableGroups(elapsed, out int available) && groupIndex < available;

        public bool TryGetSecondsToNextWave(double elapsed, out double seconds)
        {
            seconds = 0;
            if (!SwarmSchedule.Finite(elapsed) || elapsed < 0) return false;
            foreach (var wave in _waves)
            {
                if (wave.AppearsAt <= elapsed) continue;
                seconds = wave.AppearsAt - elapsed;
                return true;
            }
            return false;
        }
    }
}
