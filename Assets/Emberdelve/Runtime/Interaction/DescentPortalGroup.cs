using System.Collections.Generic;
using Emberdelve.Combat;
using Emberdelve.Core;
using Emberdelve.Flow;
using Emberdelve.Net.Rules;
using Emberdelve.Player;
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
        private readonly Dictionary<int, PortalHold> _holds = new Dictionary<int, PortalHold>();
        private readonly List<int> _expiredHolds = new List<int>();
        private int? _party;
        public const float TeamHoldSeconds = 2f;

        private sealed class PortalHold
        {
            public PlayerRig Rig;
            public int Slot;
            public ExtractionHoldWindow Window;
            public System.Action<DamageInfo> OnDamage;
        }
        public int RemainingSlots => _schedule == null ? 0 : _schedule.PortalsPerGroup - _usedSlots.Count;
        public string PromptText => "Descend to floor " + (_floor + 1) + " (" + RemainingSlots + " places)";
        public bool RequiresHold => _schedule?.PortalsPerGroup > 1;
        public float HoldDuration => RequiresHold ? TeamHoldSeconds : 0f;

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
        public bool IsAvailableToParty(int partyId) => !_party.HasValue || _party.Value == partyId;
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
            if (!_director.CanQueueDescent(actor, _floor, out reason)) return false;
            if (IsAvailableToParty(_director.SessionOf(actor).PartyId)) return true;
            reason = "These portal slots are reserved for another team";
            return false;
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
            CancelHold(actor.PlayerId);
            if (_schedule.PortalsPerGroup > 1) _party ??= _director.SessionOf(actor).PartyId;
            _usedBy.Add(actor.PlayerId); _usedSlots.Add(slot);
            if (view != null) view.gameObject.SetActive(false);
            return true;
        }

        public bool BeginHold(IInteractionActor actor, int slot, out string reason)
        {
            reason = "Portal cannot be activated";
            if (!RequiresHold || actor is not PlayerRig rig || _holds.ContainsKey(rig.PlayerId)
                || !_slots.TryGetValue(slot, out var view) || !view.CanInteract(rig, out reason)) return false;
            var pose = rig.transform.position;
            // The interaction bar includes a frame of input before the start request is processed.
            if (!ExtractionHoldWindow.TryStart(1, Time.timeAsDouble, TeamHoldSeconds - .08f,
                pose.x, pose.y, pose.z, out var window)) return false;
            var hold = new PortalHold { Rig = rig, Slot = slot, Window = window, OnDamage = _ => CancelHold(rig.PlayerId) };
            _holds.Add(rig.PlayerId, hold);
            if (rig.Health != null) rig.Health.Damaged += hold.OnDamage;
            return true;
        }

        public bool CompleteHold(IInteractionActor actor, int slot, out string reason)
        {
            if (!RequiresHold) return TryEnter(actor, slot, out reason);
            reason = "Hold to activate the portal";
            if (actor == null || !_holds.TryGetValue(actor.PlayerId, out var hold) || hold.Rig != (actor as PlayerRig)
                || slot != hold.Slot) return false;
            CancelHold(actor.PlayerId);
            return Evaluate(hold) == ExtractionHoldVerdict.Valid && TryEnter(actor, slot, out reason);
        }

        public void CancelHold(IInteractionActor actor, int slot)
        {
            if (actor != null && _holds.TryGetValue(actor.PlayerId, out var hold) && hold.Rig == (actor as PlayerRig)
                && slot == hold.Slot) CancelHold(actor.PlayerId);
        }

        private void CancelHold(int playerId)
        {
            if (_holds.Remove(playerId, out var hold) && hold.Rig != null && hold.Rig.Health != null)
                hold.Rig.Health.Damaged -= hold.OnDamage;
        }

        private static ExtractionHoldVerdict Evaluate(PortalHold hold)
        {
            var pose = hold.Rig.transform.position;
            return hold.Window.Evaluate(1, Time.timeAsDouble, pose.x, pose.y, pose.z);
        }

        private void Update()
        {
            if (_holds.Count == 0) return;
            foreach (var entry in _holds)
            {
                var hold = entry.Value;
                if (hold.Rig == null || !_slots.TryGetValue(hold.Slot, out var slot) || !slot.CanInteract(hold.Rig, out _)
                    || Evaluate(hold) is ExtractionHoldVerdict.Expired or ExtractionHoldVerdict.Moved)
                    _expiredHolds.Add(entry.Key);
            }
            foreach (int id in _expiredHolds) CancelHold(id);
            _expiredHolds.Clear();
        }

        private void OnDisable()
        {
            foreach (int id in _holds.Keys) _expiredHolds.Add(id);
            foreach (int id in _expiredHolds) CancelHold(id);
            _expiredHolds.Clear();
        }

        public void OnInteractInstant(IInteractionActor actor)
        {
            if (!RequiresHold && !TryEnter(actor, out string reason)) GameEvents.RaiseToast(actor != null ? actor.PlayerId : -1, reason);
        }
        public void OnHoldComplete(IInteractionActor actor) => OnInteractInstant(actor);
    }
}
