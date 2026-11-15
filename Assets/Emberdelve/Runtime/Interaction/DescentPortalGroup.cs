using System.Collections.Generic;
using Emberdelve.Core;
using Emberdelve.Flow;
using Emberdelve.Rules;
using UnityEngine;

namespace Emberdelve.Interaction
{
    /// <summary>A mode-sized group of one-use descent slots. This component never settles profile loot.
    /// Client RPC routing and destination matchmaking are owned by the network/floor coordinator.</summary>
    public sealed class DescentPortalGroup : MonoBehaviour, IInteractable
    {
        private RunDirector _director;
        private DescentPortalSchedule _schedule;
        private int _floor, _group;
        private readonly HashSet<int> _usedSlots = new HashSet<int>();
        private readonly Dictionary<int, DescentPortalSlot> _slots = new Dictionary<int, DescentPortalSlot>();
        private readonly HashSet<int> _usedBy = new HashSet<int>();
        public int RemainingSlots => _schedule == null ? 0 : _schedule.PortalsPerGroup - _usedSlots.Count;
        public string PromptText => "Descend to floor " + (_floor + 1) + " (" + RemainingSlots + " places)";
        public bool RequiresHold => false;
        public float HoldDuration => 0f;

        public bool Configure(RunDirector director, DescentPortalSchedule schedule, int floor, int group)
        {
            if (_director != null || director == null || schedule == null || floor < 1 || floor >= 3
                || group < 0 || group >= schedule.TotalGroups
                || !director.RoyaleMode.HasValue || (int)director.RoyaleMode.Value != schedule.PortalsPerGroup) return false;
            _director = director; _schedule = schedule; _floor = floor; _group = group;
            return true;
        }

        public int Floor => _floor;
        public int Group => _group;
        public bool SlotUsed(int slot) => _usedSlots.Contains(slot);
        public void RegisterSlot(int slot, DescentPortalSlot view)
        { if (_schedule != null && slot >= 0 && slot < _schedule.PortalsPerGroup && view != null) _slots[slot] = view; }
        public bool CanInteract(IInteractionActor actor, out string reason) => CanInteractAt(actor, this, out reason);
        internal bool CanInteractAt(IInteractionActor actor, Component target, out string reason)
        {
            reason = "Portal is unavailable";
            if (_director == null || _schedule == null || NetRole.IsClientOnly) return false;
            if (!_director.IsRoyaleFloorOpen(_floor)
                || !_director.TryGetRoyaleFloorClock(_floor, out float elapsed, out float remaining)
                || System.Math.Abs(elapsed + remaining - _schedule.Duration) > 0.01
                || !_schedule.IsGroupOpen(_group, elapsed))
            { reason = "Portal is not open"; return false; }
            if (RemainingSlots <= 0) { reason = "Portal group is exhausted"; return false; }
            if (actor == null || _usedBy.Contains(actor.PlayerId)) return false;
            if (!InteractionRules.Validate(actor, target, _director.InteractionReach, _director, out reason)) return false;
            var session = _director.SessionOf(actor);
            if (session == null || session.Floor != _floor) { reason = "Portal belongs to another floor"; return false; }
            return true;
        }

        public bool TryEnter(IInteractionActor actor, out string reason)
        {
            int slot = 0; while (_usedSlots.Contains(slot)) slot++;
            return TryEnter(actor, slot, out reason);
        }
        public bool TryEnter(IInteractionActor actor, int slot, out string reason)
        {
            reason = "Portal slot is unavailable";
            if (_schedule == null || slot < 0 || slot >= _schedule.PortalsPerGroup || _usedSlots.Contains(slot)) return false;
            Component target = _slots.TryGetValue(slot, out var view) ? (Component)view : this;
            if (!CanInteractAt(actor, target, out reason)) return false;
            if (!_director.TryQueueDescent(actor, _floor, out reason)) return false;
            _usedBy.Add(actor.PlayerId); _usedSlots.Add(slot);
            if (view != null) view.gameObject.SetActive(false);
            return true;
        }

        public void OnInteractInstant(IInteractionActor actor)
        {
            if (!TryEnter(actor, out string reason)) GameEvents.RaiseToast(actor != null ? actor.PlayerId : -1, reason);
        }
        public void OnHoldComplete(IInteractionActor actor) => OnInteractInstant(actor);
    }
}
