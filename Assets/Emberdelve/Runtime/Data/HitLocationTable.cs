using UnityEngine;
using Emberdelve.Core;

namespace Emberdelve.Data
{
    /// <summary>
    /// D1a hit-location damage multipliers. These are OUR OWN tunables (Docs/Design/DND_PARITY.md C9: same
    /// table shape as the reference genre, our own numbers, retuned in our own playtests): head 1.5, body 1.0,
    /// arms 0.8, hands 0.7, legs 0.6, feet 0.5. The table is data: a <see cref="Emberdelve.Core.GameConfig"/>
    /// may reference an asset; with none the code defaults below apply, so a fixture or an old scene still
    /// resolves a location.
    ///
    /// The head multiplier is additive-capable: a future gear bonus is ADDED to the table value and the sum is
    /// capped at <see cref="HeadCap"/> (the existing x1.8 headshot ceiling), never multiplied on top of it.
    /// </summary>
    [CreateAssetMenu(menuName = "Emberdelve/04 Hit Location Table", fileName = "HitLocationTable")]
    public class HitLocationTable : ScriptableObject
    {
        public const float DefaultHead = 1.5f;
        public const float DefaultBody = 1.0f;
        public const float DefaultArms = 0.8f;
        public const float DefaultHands = 0.7f;
        public const float DefaultLegs = 0.6f;
        public const float DefaultFeet = 0.5f;

        /// <summary>The existing headshot ceiling (DamageResolver.HeadshotMultiplier): head table value + bonus never exceeds it.</summary>
        public const float HeadCap = 1.8f;

        [Range(0.1f, 3f)] public float head = DefaultHead;
        [Range(0.1f, 3f)] public float body = DefaultBody;
        [Range(0.1f, 3f)] public float arms = DefaultArms;
        [Range(0.1f, 3f)] public float hands = DefaultHands;
        [Range(0.1f, 3f)] public float legs = DefaultLegs;
        [Range(0.1f, 3f)] public float feet = DefaultFeet;

        private static HitLocationTable _default;

        /// <summary>Code-default table (never saved). Used when no asset is configured.</summary>
        public static HitLocationTable Default
        {
            get
            {
                if (_default == null)
                {
                    _default = CreateInstance<HitLocationTable>();
                    _default.name = "HitLocationTable_Default";
                    _default.hideFlags = HideFlags.HideAndDontSave;
                }
                return _default;
            }
        }

        /// <summary>
        /// Damage multiplier for a location. <paramref name="headBonus"/> (default 0; the hook for a gear
        /// headshot bonus) is added to the head value and the sum is capped at <see cref="HeadCap"/>. A
        /// non-finite or non-positive authored value falls back to the default for that slot.
        /// </summary>
        public float Multiplier(HitLocation location, float headBonus = 0f)
        {
            switch (location)
            {
                case HitLocation.Head:
                {
                    float bonus = float.IsNaN(headBonus) || float.IsInfinity(headBonus) || headBonus < 0f ? 0f : headBonus;
                    return Mathf.Min(HeadCap, Sane(head, DefaultHead) + bonus);
                }
                case HitLocation.Body: return Sane(body, DefaultBody);
                case HitLocation.Arms: return Sane(arms, DefaultArms);
                case HitLocation.Hands: return Sane(hands, DefaultHands);
                case HitLocation.Legs: return Sane(legs, DefaultLegs);
                case HitLocation.Feet: return Sane(feet, DefaultFeet);
                default: return 1f;
            }
        }

        private static float Sane(float value, float fallback) =>
            float.IsNaN(value) || float.IsInfinity(value) || value <= 0f ? fallback : value;
    }
}
