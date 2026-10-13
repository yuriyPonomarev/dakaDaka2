using UnityEngine;

namespace Emberdelve.Data
{
    /// <summary>
    /// Temporary runtime state a skill can put on an entity. Deliberately separate from the permanent
    /// passive modifiers in CharacterStatStack: a status expires, a passive does not.
    ///
    /// The meaning of StatusSpec.Magnitude depends on the kind, and every kind means "multiplier",
    /// which keeps the consuming code free of per-kind special cases:
    ///   Slow       -> movement speed multiplier   (0.55 = walk at 55%)
    ///   Empowered  -> outgoing damage multiplier   (1.25 = deal 125%)
    /// </summary>
    public enum StatusKind
    {
        None = 0,
        Slow = 1,
        Empowered = 2,
    }

    /// <summary>
    /// A status as it travels from the caster to the authority to the target. It is a value type so a
    /// future server can carry the exact same request over the network without a reference to any
    /// scene object, and so an accepted hit and the state it applies are one atomic thing.
    /// </summary>
    public readonly struct StatusSpec
    {
        public readonly StatusKind Kind;
        public readonly float Duration;
        public readonly float Magnitude;

        public StatusSpec(StatusKind kind, float duration, float magnitude)
        {
            Kind = kind;
            Duration = Mathf.Max(0f, duration);
            Magnitude = Mathf.Max(0f, magnitude);
        }

        public bool IsNone => Kind == StatusKind.None || Duration <= 0f;

        public static StatusSpec None => new StatusSpec(StatusKind.None, 0f, 1f);

        public override string ToString()
        {
            return $"{Kind} {Magnitude:F2} for {Duration:F1}s";
        }
    }
}
