using System;

namespace Emberdelve.Rules
{
    /// <summary>Project-authored phase data. No reference-game timing is implied.</summary>
    public readonly struct SwarmPhase
    {
        public readonly double Wait, Shrink, X, Z, Radius, DamagePerSecond;
        public SwarmPhase(double wait, double shrink, double x, double z, double radius, double damagePerSecond)
        { Wait = wait; Shrink = shrink; X = x; Z = z; Radius = radius; DamagePerSecond = damagePerSecond; }
    }

    public readonly struct SwarmSample
    {
        public readonly int Phase;
        public readonly double X, Z, Radius, DamagePerSecond, SecondsToBoundary;
        public readonly bool Shrinking, Completed;
        public SwarmSample(int phase, double x, double z, double radius, double damage, double remaining,
            bool shrinking, bool completed)
        { Phase = phase; X = x; Z = z; Radius = radius; DamagePerSecond = damage;
          SecondsToBoundary = remaining; Shrinking = shrinking; Completed = completed; }

        public bool Contains(double x, double z)
        {
            if (!SwarmSchedule.Finite(x) || !SwarmSchedule.Finite(z)) return false;
            double dx = x - X, dz = z - Z;
            return dx * dx + dz * dz <= Radius * Radius;
        }
    }

    /// <summary>
    /// Immutable safe-zone timeline sampled by authority elapsed time, not accumulated render frames.
    /// Nested target circles cannot expand the current safe area. Runtime owns damage, pause and floor transitions.
    /// </summary>
    public sealed class SwarmSchedule
    {
        private readonly double _x, _z, _radius;
        private readonly SwarmPhase[] _phases;
        public double Duration { get; }

        public SwarmSchedule(double x, double z, double radius, params SwarmPhase[] phases)
        {
            if (!Finite(x) || !Finite(z) || !Finite(radius) || radius <= 0 || radius > 1000000)
                throw new ArgumentOutOfRangeException(nameof(radius));
            if (phases == null || phases.Length == 0) throw new ArgumentException("At least one phase is required", nameof(phases));
            _x = x; _z = z; _radius = radius;
            _phases = (SwarmPhase[])phases.Clone();
            double duration = 0, previousX = x, previousZ = z, previousRadius = radius;
            foreach (SwarmPhase phase in _phases)
            {
                if (!Finite(phase.Wait) || phase.Wait < 0 || !Finite(phase.Shrink) || phase.Shrink <= 0
                    || !Finite(phase.X) || !Finite(phase.Z) || !Finite(phase.Radius) || phase.Radius < 0
                    || !Finite(phase.DamagePerSecond) || phase.DamagePerSecond < 0)
                    throw new ArgumentException("Invalid swarm phase", nameof(phases));
                double dx = phase.X - previousX, dz = phase.Z - previousZ;
                if (Math.Sqrt(dx * dx + dz * dz) + phase.Radius > previousRadius + 1e-8)
                    throw new ArgumentException("Target safe circle must be contained in the preceding circle", nameof(phases));
                duration += phase.Wait + phase.Shrink;
                if (!Finite(duration)) throw new ArgumentException("Timeline duration overflow", nameof(phases));
                previousX = phase.X; previousZ = phase.Z; previousRadius = phase.Radius;
            }
            Duration = duration;
        }

        public bool TrySample(double elapsed, out SwarmSample sample)
        {
            sample = default;
            if (!Finite(elapsed) || elapsed < 0) return false;
            double x = _x, z = _z, radius = _radius, start = 0;
            for (int i = 0; i < _phases.Length; i++)
            {
                SwarmPhase p = _phases[i];
                double movingAt = start + p.Wait, end = movingAt + p.Shrink;
                if (elapsed < movingAt)
                {
                    sample = new SwarmSample(i, x, z, radius, p.DamagePerSecond, movingAt - elapsed, false, false);
                    return true;
                }
                if (elapsed < end)
                {
                    double t = (elapsed - movingAt) / p.Shrink;
                    sample = new SwarmSample(i, x + (p.X - x) * t, z + (p.Z - z) * t,
                        radius + (p.Radius - radius) * t, p.DamagePerSecond, end - elapsed, true, false);
                    return true;
                }
                start = end; x = p.X; z = p.Z; radius = p.Radius;
            }
            SwarmPhase last = _phases[_phases.Length - 1];
            sample = new SwarmSample(_phases.Length - 1, x, z, radius, last.DamagePerSecond, 0, false, true);
            return true;
        }

        internal static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

        /// <summary>Nested circles guarantee a stationary portal inside the final circle stays safe
        /// throughout every wait and shrink. Clearance includes the whole portal group's footprint.
        /// A schedule ending too small must be redesigned, not silently place an unsafe portal.</summary>
        public bool ContainsThroughEnd(double x, double z, double clearance)
        {
            if (!Finite(x) || !Finite(z) || !Finite(clearance) || clearance < 0) return false;
            SwarmPhase last = _phases[_phases.Length - 1];
            if (clearance > last.Radius) return false;
            double dx = x - last.X, dz = z - last.Z, radius = last.Radius - clearance;
            return dx * dx + dz * dz <= radius * radius;
        }
    }
}
