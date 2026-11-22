using System;
using System.Collections.Generic;

namespace Emberdelve.Rules
{
    public readonly struct PortalLocation
    {
        public readonly double X, Z;
        public PortalLocation(double x, double z) { X = x; Z = z; }
    }

    /// <summary>Authority-seeded selection from already navigation/geometry-validated group anchors.
    /// Filters against the whole remaining swarm timeline, with no unsafe fallback.</summary>
    public static class DescentPortalPlacement
    {
        public static bool TryChoose(SwarmSchedule swarm, IReadOnlyList<PortalLocation> candidates,
            int groups, double clearance, double separation, uint seed, out PortalLocation[] selected)
        {
            selected = Array.Empty<PortalLocation>();
            if (swarm == null || candidates == null || groups <= 0 || groups > candidates.Count
                || !SwarmSchedule.Finite(clearance) || clearance < 0
                || !SwarmSchedule.Finite(separation) || separation <= 0 || separation < 2 * clearance) return false;
            var available = new List<PortalLocation>();
            foreach (var candidate in candidates)
                if (swarm.ContainsThroughEnd(candidate.X, candidate.Z, clearance)) available.Add(candidate);
            uint random = seed == 0 ? 0x9E3779B9u : seed;
            for (int i = available.Count - 1; i > 0; i--)
            {
                random ^= random << 13; random ^= random >> 17; random ^= random << 5;
                int j = (int)(random % (uint)(i + 1));
                var swap = available[i]; available[i] = available[j]; available[j] = swap;
            }
            var result = new PortalLocation[groups];
            if (!TryFill(available, separation * separation, result, 0, 0)) return false;
            selected = result;
            return true;
        }

        private static bool TryFill(IReadOnlyList<PortalLocation> available, double minDistanceSquared,
            PortalLocation[] result, int start, int count)
        {
            if (count == result.Length) return true;
            int last = available.Count - (result.Length - count);
            for (int index = start; index <= last; index++)
            {
                PortalLocation candidate = available[index];
                bool clear = true;
                for (int i = 0; i < count; i++)
                {
                    double dx = candidate.X - result[i].X, dz = candidate.Z - result[i].Z;
                    if (dx * dx + dz * dz >= minDistanceSquared) continue;
                    clear = false;
                    break;
                }
                if (!clear) continue;
                result[count] = candidate;
                if (TryFill(available, minDistanceSquared, result, index + 1, count + 1)) return true;
            }
            return false;
        }
    }
}
