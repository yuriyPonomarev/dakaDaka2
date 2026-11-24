using System;

namespace Emberdelve.AI
{
    /// <summary>
    /// Server-side choice of which of a monster weapon's swing patterns the next swing uses (pattern index; 0 = the original
    /// attack). Pure and seedable so tests can pin it; the choice is made by the brain's owner (the authority side), never sent by a client.
    /// </summary>
    public static class EnemyAttackChoice
    {
        /// <summary>Test/gate seam: a value >= 0 pins the choice (clamped into range); -1 = random per swing.</summary>
        public static int Override = -1;

        public static int Pick(int patternCount, Random random)
        {
            if (patternCount <= 1) return 0;
            if (Override >= 0) return Math.Min(Override, patternCount - 1);
            return (random ?? new Random()).Next(patternCount);
        }

        /// <summary>
        /// Picks among the weapon's USABLE pattern indices (non-null slot, resolvable by the authority's own rule, valid keys); 0 when there is none.
        /// </summary>
        public static int PickFor(Emberdelve.Data.WeaponData weapon, Random random)
        {
            System.Collections.Generic.List<int> usable = UsableIndices(weapon);
            if (usable.Count == 0) return 0;
            if (usable.Count == 1) return usable[0];
            if (Override >= 0) return usable.Contains(Override) ? Override : usable[0];
            return usable[(random ?? new Random()).Next(usable.Count)];
        }

        public static System.Collections.Generic.List<int> UsableIndices(Emberdelve.Data.WeaponData weapon)
        {
            var usable = new System.Collections.Generic.List<int>(2);
            if (weapon == null || weapon.swingPatterns == null) return usable;
            for (int i = 0; i < weapon.swingPatterns.Length; i++)
            {
                if (weapon.TryResolveComboStep(0, i, out _, out Emberdelve.Data.SwingPatternData pattern, out _) && pattern != null && pattern.Validate(out _))
                    usable.Add(i);
            }
            return usable;
        }

        /// <summary>True when <paramref name="index"/> names one of <paramref name="patternCount"/> patterns.</summary>
        public static bool IsValid(int index, int patternCount) => index >= 0 && index < patternCount;
    }
}
