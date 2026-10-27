using System.Collections.Generic;
using UnityEngine;
using Emberdelve.Core;
using Emberdelve.Data;

namespace Emberdelve.Combat
{
    /// <summary>
    /// Anything that can carry a temporary status. The combat authority is the only thing that calls
    /// <see cref="ApplyStatus"/>, so a skill never writes state onto its own target the way a hack
    /// would: it asks the authority, and the authority decides whether that target may have it.
    /// </summary>
    public interface IStatusTarget
    {
        void ApplyStatus(StatusSpec spec, int sourceCombatId);
        void ClearStatuses();
        bool HasStatus(StatusKind kind);
    }

    /// <summary>
    /// Temporary multipliers for one entity. Permanent passive modifiers live in CharacterStatStack;
    /// this is only for state that expires.
    /// </summary>
    public sealed class StatusComponent : MonoBehaviour, IStatusTarget
    {
        private struct Entry
        {
            public StatusKind Kind;
            public float Remaining;
            public float Magnitude;
        }

        private readonly List<Entry> _entries = new List<Entry>(4);

        /// <summary>Changes when an apply, clear, or expiry changes a captured multiplier.</summary>
        public int Version { get; private set; }

        public static StatusComponent Ensure(GameObject target)
        {
            if (target == null) return null;
            StatusComponent existing = target.GetComponent<StatusComponent>();
            return existing != null ? existing : target.AddComponent<StatusComponent>();
        }

        public int ActiveCount => _entries.Count;
        public bool HasStatus(StatusKind kind) => Find(kind) >= 0;

        /// <summary>Multiplier applied to whatever locomotion this entity does.</summary>
        public float SpeedMultiplier
        {
            get
            {
                float multiplier = 1f;
                for (int i = 0; i < _entries.Count; i++)
                {
                    if (_entries[i].Kind == StatusKind.Slow) multiplier = Mathf.Min(multiplier, _entries[i].Magnitude);
                }
                return Mathf.Clamp(multiplier, 0f, 4f);
            }
        }

        /// <summary>Multiplier applied to damage this entity deals. Strongest buff wins.</summary>
        public float OutgoingDamageMultiplier
        {
            get
            {
                float multiplier = 1f;
                for (int i = 0; i < _entries.Count; i++)
                {
                    if (_entries[i].Kind == StatusKind.Empowered) multiplier = Mathf.Max(multiplier, _entries[i].Magnitude);
                }
                return Mathf.Clamp(multiplier, 0f, 8f);
            }
        }

        /// <summary>
        /// Seconds the status is still carried: the longest remaining entry of that kind. For Slow this is NOT the time the
        /// current (strongest) magnitude lasts - a weaker, longer slow can outlast it (B1) - see <see cref="SpeedMultiplier"/>.
        /// </summary>
        public float RemainingOf(StatusKind kind)
        {
            float remaining = 0f;
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].Kind == kind && _entries[i].Remaining > remaining) remaining = _entries[i].Remaining;
            }
            return remaining;
        }

        /// <summary>Upper bound of simultaneous slow entries (distinct, non-dominated slows); the shortest one goes first.</summary>
        private const int MaxSlowEntries = 8;

        public void ApplyStatus(StatusSpec spec, int sourceCombatId)
        {
            if (spec.IsNone) return;

            if (spec.Kind == StatusKind.Slow)
            {
                // B1: a slow keeps its OWN magnitude and duration. The effective slow is the strongest one active at any
                // moment (SpeedMultiplier), every entry expires individually, so a weaker slow outlasting a stronger one still
                // applies afterwards and a stronger slow is never stretched to a longer slow's time. "Refresh, not stack":
                // an entry that is no stronger AND no longer than another is redundant and dropped, so the same slow applied
                // again just refreshes (max duration) and a strictly weaker, shorter one disappears behind the stronger.
                _entries.Add(new Entry { Kind = StatusKind.Slow, Remaining = spec.Duration, Magnitude = spec.Magnitude });
                PruneDominatedSlows();
            }
            else
            {
                int index = Find(spec.Kind);
                if (index >= 0)
                {
                    // Refreshing the same status extends it and takes the stronger magnitude, so two
                    // casts of the same skill cannot stack into an unbounded multiplier.
                    Entry entry = _entries[index];
                    entry.Remaining = Mathf.Max(entry.Remaining, spec.Duration);
                    entry.Magnitude = Mathf.Max(entry.Magnitude, spec.Magnitude);
                    _entries[index] = entry;
                }
                else
                {
                    _entries.Add(new Entry
                    {
                        Kind = spec.Kind,
                        Remaining = spec.Duration,
                        Magnitude = spec.Magnitude
                    });
                }
            }

            Version++;
            GameEvents.RaiseStatusChanged(SelfCombatId(), spec.Kind, spec.Duration);
        }

        /// <summary>Drops every slow entry that another slow beats or equals in both strength and remaining time (the newest of two equal ones goes).</summary>
        private void PruneDominatedSlows()
        {
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                if (_entries[i].Kind != StatusKind.Slow) continue;
                for (int j = 0; j < _entries.Count; j++)
                {
                    if (j == i || _entries[j].Kind != StatusKind.Slow) continue;
                    bool beats = _entries[j].Magnitude <= _entries[i].Magnitude && _entries[j].Remaining >= _entries[i].Remaining;
                    bool equal = _entries[j].Magnitude == _entries[i].Magnitude && _entries[j].Remaining == _entries[i].Remaining;
                    // equal entries: keep the lower index (older one), drop the higher
                    if (beats && (!equal || j < i))
                    {
                        _entries.RemoveAt(i);
                        break;
                    }
                }
            }

            int slows = 0;
            for (int i = 0; i < _entries.Count; i++) if (_entries[i].Kind == StatusKind.Slow) slows++;
            while (slows > MaxSlowEntries)
            {
                int shortest = -1;
                for (int i = 0; i < _entries.Count; i++)
                {
                    if (_entries[i].Kind != StatusKind.Slow) continue;
                    if (shortest < 0 || _entries[i].Remaining < _entries[shortest].Remaining) shortest = i;
                }
                _entries.RemoveAt(shortest);
                slows--;
            }
        }

        public void ClearStatuses()
        {
            if (_entries.Count == 0) return;
            _entries.Clear();
            Version++;
            GameEvents.RaiseStatusChanged(SelfCombatId(), StatusKind.None, 0f);
        }

        private int SelfCombatId()
        {
            ICombatEntity self = GetComponent<ICombatEntity>();
            return self != null ? self.CombatId : -1;
        }

        private void Update()
        {
            Tick(Time.deltaTime);
        }

        /// <summary>Advances every entry by <paramref name="deltaTime"/> and expires the finished ones individually (internal so a gate can step time exactly).</summary>
        internal void Tick(float deltaTime)
        {
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                Entry entry = _entries[i];
                entry.Remaining -= deltaTime;

                if (entry.Remaining <= 0f)
                {
                    _entries.RemoveAt(i);
                    Version++;
                    // "ended" is only news when no other entry of the kind (a longer, weaker slow) keeps it alive
                    GameEvents.RaiseStatusChanged(SelfCombatId(), entry.Kind, Find(entry.Kind) >= 0 ? RemainingOf(entry.Kind) : 0f);
                }
                else
                {
                    _entries[i] = entry;
                }
            }
        }

        private int Find(StatusKind kind)
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].Kind == kind) return i;
            }
            return -1;
        }
    }
}
