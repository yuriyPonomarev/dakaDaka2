using System;

namespace Emberdelve.Ballistics
{
    /// <summary>
    /// One key of a projectile's damage falloff table (D2): the damage scale an arrow or bolt has once it has been
    /// in the AIR for <see cref="time"/> seconds. Keys are authored on the weapon, ascending by time, and read
    /// by <see cref="ProjectileBallistics.FalloffScale"/> (piecewise linear, held flat before the first and after the last key).
    /// A plain serializable struct so the pure rules assembly owns the type and Unity can still store it on a WeaponData.
    /// </summary>
    [Serializable]
    public struct ProjectileFalloffKey
    {
        public float time;
        public float scale;

        public ProjectileFalloffKey(float time, float scale)
        {
            this.time = time;
            this.scale = scale;
        }
    }

    /// <summary>Position (metres) and velocity (m/s) of one projectile. Plain floats: no engine types, so a server can rerun it anywhere.</summary>
    public readonly struct BallisticState
    {
        public readonly float PX;
        public readonly float PY;
        public readonly float PZ;
        public readonly float VX;
        public readonly float VY;
        public readonly float VZ;

        public BallisticState(float px, float py, float pz, float vx, float vy, float vz)
        {
            PX = px;
            PY = py;
            PZ = pz;
            VX = vx;
            VY = vy;
            VZ = vz;
        }
    }

    /// <summary>
    /// D2 pure projectile math: the fixed-step integrator, the air-time falloff table, drop prediction and the
    /// low-arc aim solver. Nothing here knows about Unity; the combat authority steps its flights with
    /// <see cref="Advance"/> and the same functions answer "where should the arrow be" in tests and on a server.
    ///
    /// Integrator. The only acceleration is gravity (constant, straight down, no drag by design), so one fixed step
    /// uses the EXACT constant-acceleration update: p' = p + v*dt + a*dt^2/2, v' = v + a*dt. It is neither explicit nor
    /// semi-implicit Euler (those drift from the true parabola by g*dt*t/2 and so would change the drop with the step
    /// size). Because it is exact for a parabola, the sampled path is the same curve for any step size and the
    /// closed form <see cref="PositionAt"/> predicts it to float rounding. Determinism comes from the step being a
    /// fixed <see cref="StepSeconds"/> and the update being a fixed sequence of float operations: the same launch
    /// state and the same number of steps give bit-identical states in the same process.
    /// </summary>
    public static class ProjectileBallistics
    {
        /// <summary>The fixed substep of the authority's projectile simulation (1/60 s), independent of the frame rate.</summary>
        public const float StepSeconds = 1f / 60f;

        /// <summary>Slack (seconds) when deciding that an accumulated delta holds a whole step: absorbs float noise from summing frame times.</summary>
        public const float StepEpsilon = 1e-5f;

        /// <summary>Most steps one advance call may take (10 s); the backlog beyond it is dropped, never replayed later.</summary>
        public const int MaxStepsPerAdvance = 600;

        /// <summary>
        /// One fixed step of length <paramref name="dt"/> under a downward acceleration of <paramref name="gravity"/> m/s^2
        /// (a non-positive or NaN gravity means a straight, drop-free flight).
        /// </summary>
        public static BallisticState Advance(in BallisticState s, float gravity, float dt)
        {
            float g = gravity > 0f ? gravity : 0f;
            float halfGravityStepSquared = 0.5f * g * dt * dt;
            return new BallisticState(
                s.PX + s.VX * dt,
                s.PY + s.VY * dt - halfGravityStepSquared,
                s.PZ + s.VZ * dt,
                s.VX,
                s.VY - g * dt,
                s.VZ);
        }

        /// <summary>Closed-form position after <paramref name="seconds"/> of flight from <paramref name="s"/>.</summary>
        public static BallisticState PositionAt(in BallisticState s, float gravity, float seconds)
        {
            float g = gravity > 0f ? gravity : 0f;
            return new BallisticState(
                s.PX + s.VX * seconds,
                s.PY + s.VY * seconds - 0.5f * g * seconds * seconds,
                s.PZ + s.VZ * seconds,
                s.VX,
                s.VY - g * seconds,
                s.VZ);
        }

        /// <summary>Flight time of <paramref name="steps"/> fixed steps.</summary>
        public static float AirTime(int steps) => steps * StepSeconds;

        /// <summary>
        /// How far an arrow that left along the aim line with <paramref name="speed"/> is BELOW that line (metres, vertical)
        /// when it has flown <paramref name="aimDistance"/> metres of aim line: g*t^2/2 with t = aimDistance/speed. Exact for
        /// any pitch because gravity is the only acceleration (the arrow and the point it would have reached without
        /// gravity share the same time).
        /// </summary>
        public static float DropBelowAimLine(float speed, float gravity, float aimDistance)
        {
            if (!(speed > 0f)) return 0f;
            float g = gravity > 0f ? gravity : 0f;
            float t = aimDistance / speed;
            return 0.5f * g * t * t;
        }

        /// <summary>Seconds to cover <paramref name="aimDistance"/> metres of the aim line at <paramref name="speed"/>.</summary>
        public static float TimeAlongAimLine(float speed, float aimDistance)
        {
            return speed > 0f ? aimDistance / speed : float.PositiveInfinity;
        }

        /// <summary>
        /// Seconds to cover <paramref name="horizontalDistance"/> metres of horizontal ground distance when launched at
        /// <paramref name="pitchRadians"/> (positive = up) with <paramref name="speed"/>. Infinity if the shot has no horizontal travel.
        /// </summary>
        public static float TimeToHorizontalDistance(float speed, float pitchRadians, float horizontalDistance)
        {
            float horizontalSpeed = speed * MathF.Cos(pitchRadians);
            return horizontalSpeed > 0f ? horizontalDistance / horizontalSpeed : float.PositiveInfinity;
        }

        /// <summary>
        /// Low-arc launch pitch (radians, positive = up) that lands on a point <paramref name="horizontalDistance"/> metres away
        /// and <paramref name="heightDelta"/> metres above (negative = below) the muzzle, for a drop-compensated aim.
        /// False when the point is out of reach at this speed (no real solution). With no gravity it is the straight aim angle.
        /// </summary>
        public static bool SolveLowPitch(float speed, float gravity, float horizontalDistance, float heightDelta, out float pitchRadians)
        {
            pitchRadians = 0f;
            if (!(speed > 0f) || !(horizontalDistance > 0f)) return false;
            float g = gravity > 0f ? gravity : 0f;
            if (g <= 0f)
            {
                pitchRadians = MathF.Atan2(heightDelta, horizontalDistance);
                return true;
            }
            float v2 = speed * speed;
            float discriminant = v2 * v2 - g * (g * horizontalDistance * horizontalDistance + 2f * heightDelta * v2);
            if (discriminant < 0f) return false;
            pitchRadians = MathF.Atan((v2 - MathF.Sqrt(discriminant)) / (g * horizontalDistance));
            return true;
        }

        /// <summary>
        /// Damage scale of a projectile that has been airborne <paramref name="airTime"/> seconds: the table linearly interpolated,
        /// held at the first key's scale before it and at the last key's scale after it. A null or empty table is 1 (no falloff).
        /// The result is never negative. Keys must ascend by time (<see cref="IsValidTable"/>); equal times act as a step.
        /// </summary>
        public static float FalloffScale(ProjectileFalloffKey[] keys, float airTime)
        {
            if (keys == null || keys.Length == 0) return 1f;
            float t = float.IsNaN(airTime) ? 0f : airTime;
            if (t <= keys[0].time) return NonNegative(keys[0].scale);
            for (int i = 1; i < keys.Length; i++)
            {
                if (t > keys[i].time) continue;
                float t0 = keys[i - 1].time;
                float span = keys[i].time - t0;
                if (!(span > 0f)) return NonNegative(keys[i].scale);
                float u = (t - t0) / span;
                return NonNegative(keys[i - 1].scale + (keys[i].scale - keys[i - 1].scale) * u);
            }
            return NonNegative(keys[keys.Length - 1].scale);
        }

        /// <summary>True when the table is empty (no falloff) or has finite keys with non-negative scales in non-descending time order.</summary>
        public static bool IsValidTable(ProjectileFalloffKey[] keys)
        {
            if (keys == null) return true;
            for (int i = 0; i < keys.Length; i++)
            {
                if (float.IsNaN(keys[i].time) || float.IsInfinity(keys[i].time) || keys[i].time < 0f) return false;
                if (float.IsNaN(keys[i].scale) || float.IsInfinity(keys[i].scale) || keys[i].scale < 0f) return false;
                if (i > 0 && keys[i].time < keys[i - 1].time) return false;
            }
            return true;
        }

        /// <summary>
        /// Splits an accumulated frame time into whole fixed steps. Returns how many steps to run and leaves the remainder
        /// (less than one step) in <paramref name="accumulator"/> for the next call. NaN, zero or negative time runs nothing.
        /// The step count depends only on the total time accumulated, not on how it was chopped into frames.
        /// </summary>
        public static int ConsumeSteps(ref float accumulator, int maxSteps)
        {
            if (!(accumulator > 0f))
            {
                accumulator = 0f;
                return 0;
            }
            int steps = 0;
            while (steps < maxSteps && accumulator + StepEpsilon >= StepSeconds)
            {
                accumulator -= StepSeconds;
                steps++;
            }
            if (accumulator < 0f) accumulator = 0f;
            if (accumulator >= StepSeconds) accumulator = 0f;
            return steps;
        }

        private static float NonNegative(float value) => value > 0f ? value : 0f;
    }
}
