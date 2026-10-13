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

        public float RemainingOf(StatusKind kind)
        {
            int index = Find(kind);
            return index >= 0 ? _entries[index].Remaining : 0f;
        }

        public void ApplyStatus(StatusSpec spec, int sourceCombatId)
        {
            if (spec.IsNone) return;

            int index = Find(spec.Kind);
            if (index >= 0)
            {
                // Refreshing the same status extends it and takes the stronger magnitude, so two
                // casts of the same skill cannot stack into an unbounded multiplier.
                Entry entry = _entries[index];
                entry.Remaining = Mathf.Max(entry.Remaining, spec.Duration);
                entry.Magnitude = spec.Kind == StatusKind.Slow
                    ? Mathf.Min(entry.Magnitude, spec.Magnitude)
                    : Mathf.Max(entry.Magnitude, spec.Magnitude);
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

            GameEvents.RaiseStatusChanged(SelfCombatId(), spec.Kind, spec.Duration);
        }

        public void ClearStatuses()
        {
            if (_entries.Count == 0) return;
            _entries.Clear();
            GameEvents.RaiseStatusChanged(SelfCombatId(), StatusKind.None, 0f);
        }

        private int SelfCombatId()
        {
            ICombatEntity self = GetComponent<ICombatEntity>();
            return self != null ? self.CombatId : -1;
        }

        private void Update()
        {
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                Entry entry = _entries[i];
                entry.Remaining -= Time.deltaTime;

                if (entry.Remaining <= 0f)
                {
                    _entries.RemoveAt(i);
                    GameEvents.RaiseStatusChanged(SelfCombatId(), entry.Kind, 0f);
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
